using System.Threading.Channels;
using Cv.Core.Nvr;

namespace Cv.Core.Export;

/// <summary>
/// The export queue. One worker per recorder (jobs of one recorder run one at a time, recorders in parallel).
/// A job downloads over ISAPI (or records the RTSP playback when the recorder refuses), then remuxes to MP4.
/// clientForNvr returns the live client of a recorder id, or null when unknown. Jobs live in memory only.
/// </summary>
public sealed class ExportService : IExportService, IDisposable
{
    public const double DownloadShare = 0.9;
    /// <summary>Estimate before anything was measured: 4 Mbit/s of recording.</summary>
    internal const double DefaultBytesPerSecond = 4_000_000 / 8.0;
    public static readonly TimeSpan MaxLength = TimeSpan.FromHours(24);

    private readonly Func<string, INvrClient?> _clientForNvr;
    private readonly IMediaTools _media;
    private readonly string _temp;
    private readonly TimeProvider _time;

    private readonly object _lock = new();
    private readonly List<ExportJob> _jobs = [];
    private readonly Dictionary<string, Queue<ExportJob>> _queues = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task> _workers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, CancellationTokenSource> _running = [];
    private readonly HashSet<string> _reserved = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string, int), double> _rates = [];
    private readonly Channel<ExportJob?> _events = Channel.CreateUnbounded<ExportJob?>(new UnboundedChannelOptions { SingleReader = true });
    private bool _disposed;

    public ExportService(Func<string, INvrClient?> clientForNvr, IMediaTools media, string tempFolder, TimeProvider? time = null)
    {
        _clientForNvr = clientForNvr ?? throw new ArgumentNullException(nameof(clientForNvr));
        _media = media ?? throw new ArgumentNullException(nameof(media));
        _temp = string.IsNullOrWhiteSpace(tempFolder) ? AppPaths.Temp : tempFolder;
        _time = time ?? TimeProvider.System;
        CleanTemp();
        _ = Task.Run(PumpAsync);
    }

    /// <summary>Minimum time between two progress-only JobChanged events of a job.</summary>
    internal TimeSpan ProgressInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Snapshot in enqueue order (oldest first).</summary>
    public IReadOnlyList<ExportJob> Jobs { get { lock (_lock) return _jobs.ToArray(); } }

    public event Action<ExportJob?>? JobChanged;

    public ExportJob Enqueue(ExportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);
        var job = new ExportJob
        {
            Id = Guid.NewGuid(),
            Request = request,
            CreatedAt = _time.GetLocalNow().DateTime,
            Status = ExportStatus.Queued,
        };
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _jobs.Add(job);
            QueueLocked(job);
        }
        Raise(job);
        return job;
    }

    /// <summary>Throws ArgumentException with a Vietnamese message (no parameter name, so nothing English is appended).</summary>
    internal static void Validate(ExportRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.NvrId)) throw new ArgumentException("Chưa chọn đầu ghi.");
        if (r.Channel <= 0) throw new ArgumentException("Chưa chọn camera.");
        if (r.To <= r.From) throw new ArgumentException("Thời điểm kết thúc phải sau thời điểm bắt đầu.");
        if (r.To - r.From > MaxLength) throw new ArgumentException("Mỗi lần chỉ lưu được tối đa 24 giờ video. Hãy chia thành nhiều đoạn.");
        if (string.IsNullOrWhiteSpace(r.OutputFolder)) throw new ArgumentException("Chưa chọn thư mục lưu video.");
        try
        {
            Directory.CreateDirectory(r.OutputFolder);
            var probe = Path.Combine(r.OutputFolder, $".cv-{Guid.NewGuid():N}.tmp");
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new ArgumentException($"Không lưu được video vào thư mục \"{r.OutputFolder}\". Hãy chọn thư mục khác.", e);
        }
    }

    public void Cancel(Guid jobId)
    {
        CancellationTokenSource? cts = null;
        ExportJob? changed = null;
        lock (_lock)
        {
            var job = _jobs.FirstOrDefault(j => j.Id == jobId);
            if (job?.Status == ExportStatus.Queued)
            {
                // Still waiting in its queue (the worker skips it); a retried job may still have its old run cleaning up.
                job.Status = ExportStatus.Canceled;
                changed = job;
            }
            else _running.TryGetValue(jobId, out cts);
        }
        // Outside the lock: token callbacks run synchronously.
        try { cts?.Cancel(); } catch (ObjectDisposedException) { }
        if (changed is not null) Raise(changed);
    }

    public void Retry(Guid jobId)
    {
        ExportJob? job;
        lock (_lock)
        {
            job = _jobs.FirstOrDefault(j => j.Id == jobId);
            if (_disposed || job is null || job.Status is not (ExportStatus.Failed or ExportStatus.Canceled)) return;
            job.Status = ExportStatus.Queued;
            job.Progress = 0;
            job.BytesDownloaded = 0;
            job.Error = null;
            job.OutputPath = null;
            job.Method = null;
            QueueLocked(job);
        }
        Raise(job);
    }

    public void ClearFinished()
    {
        int removed;
        lock (_lock)
            removed = _jobs.RemoveAll(j => j.Status is ExportStatus.Done or ExportStatus.Failed or ExportStatus.Canceled && !_running.ContainsKey(j.Id));
        if (removed > 0) Raise(null);
    }

    public void Dispose()
    {
        CancellationTokenSource[] running;
        Task[] workers;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            running = [.. _running.Values];
            workers = [.. _workers.Values];
        }
        foreach (var c in running)
            try { c.Cancel(); } catch (ObjectDisposedException) { }
        // Give running jobs a moment to delete their temp files.
        try { Task.WaitAll(workers, TimeSpan.FromSeconds(3)); } catch (AggregateException) { }
        _events.Writer.TryComplete();
    }

    // ---- queue ----

    private void QueueLocked(ExportJob job)
    {
        var id = job.Request.NvrId;
        if (!_queues.TryGetValue(id, out var q)) _queues[id] = q = new Queue<ExportJob>();
        q.Enqueue(job);
        if (!_workers.ContainsKey(id)) _workers[id] = Task.Run(() => WorkerAsync(id));
    }

    private async Task WorkerAsync(string nvrId)
    {
        while (true)
        {
            ExportJob? job = null;
            CancellationTokenSource cts;
            lock (_lock)
            {
                if (_queues.TryGetValue(nvrId, out var q))
                    while (q.Count > 0)
                    {
                        var j = q.Dequeue();
                        if (j.Status == ExportStatus.Queued && _jobs.Contains(j)) { job = j; break; }
                    }
                if (job is null || _disposed)
                {
                    _workers.Remove(nvrId);
                    if (q is { Count: 0 }) _queues.Remove(nvrId);
                    return;
                }
                cts = new CancellationTokenSource();
                _running[job.Id] = cts;
                // Taken from the queue: from now on Cancel goes through the token, never the Queued shortcut.
                job.Status = ExportStatus.Downloading;
                job.Progress = 0;
                job.BytesDownloaded = 0;
            }
            try { await RunAsync(job, cts.Token); }
            catch (Exception e) { Log.Error($"Export {job.Id}: worker error", e); }
            finally
            {
                lock (_lock) _running.Remove(job.Id);
                cts.Dispose();
            }
        }
    }

    // ---- one job ----

    private sealed class RunState
    {
        public long LastRaise;
        public ExportStatus Phase = ExportStatus.Downloading;
        public bool Rtsp;
    }

    private async Task RunAsync(ExportJob job, CancellationToken ct)
    {
        var r = job.Request;
        var tempDir = Path.Combine(_temp, job.Id.ToString("N"));
        var run = new RunState { LastRaise = _time.GetTimestamp() };
        string? output = null;
        try
        {
            Raise(job); // now Downloading (set when the worker took it)
            var nvr = _clientForNvr(r.NvrId) ?? throw new NvrException("Không tìm thấy đầu ghi của camera này. Kiểm tra phần Cài đặt.");
            Directory.CreateDirectory(tempDir);
            Directory.CreateDirectory(r.OutputFolder);
            var estimate = EstimateBytes(r);

            IReadOnlyList<DownloadedPart>? parts = null;
            try
            {
                parts = await nvr.DownloadAsync(r.Channel, r.From, r.To, tempDir,
                    new SyncProgress<long>(b => OnBytes(job, run, b, estimate)), ct);
            }
            catch (NvrDownloadUnsupportedException e)
            {
                Log.Warn($"Export {job.Id}: ISAPI download refused, recording the RTSP playback instead ({e.Message})");
            }

            if (parts is null)
            {
                run.Rtsp = true;
                Update(job, j => { j.Method = nameof(DownloadMethod.RtspRecord); j.Progress = 0; });
                await nvr.ResolveTimeModeAsync(ct);
                var url = nvr.PlaybackUrl(r.Channel, r.From, r.To);
                var recorded = Path.Combine(tempDir, "record.mp4");
                Log.Info($"Export {job.Id}: recording {Log.Redact(url)}");
                await _media.RecordAsync(url, r.To - r.From, recorded,
                    new SyncProgress<double>(p => SetProgress(job, run, 0.99 * Clamp01(p))), ct);
                output = ReserveOutput(r);
                File.Move(recorded, output);
            }
            else
            {
                if (parts.Count == 0) throw new NvrException("Không có ghi hình trong khoảng thời gian này.");
                Learn(r, parts);
                Update(job, j => j.Method = parts[0].Method.ToString());
                var remux = new List<RemuxPart>(parts.Count);
                foreach (var p in parts) remux.Add(await PlanPartAsync(job, p, ct));

                run.Phase = ExportStatus.Converting;
                Update(job, j => { j.Status = ExportStatus.Converting; j.Progress = DownloadShare; });
                output = ReserveOutput(r);
                await _media.RemuxAsync(remux, output,
                    new SyncProgress<double>(p => SetProgress(job, run, DownloadShare + (1 - DownloadShare) * Clamp01(p))), ct);
            }

            if (!File.Exists(output)) throw new IOException("The export produced no file.");
            var done = output;
            Update(job, j => { j.Status = ExportStatus.Done; j.Progress = 1; j.OutputPath = done; });
            Log.Info($"Export {job.Id} done: ch{r.Channel} {r.From:yyyy-MM-dd HH:mm:ss}-{r.To:HH:mm:ss} via {job.Method} -> {output}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            DeleteOutput(output);
            Update(job, j => j.Status = ExportStatus.Canceled);
            Log.Info($"Export {job.Id} canceled");
        }
        catch (Exception e)
        {
            DeleteOutput(output);
            var message = ErrorText(e, run);
            Log.Error($"Export {job.Id} failed while {(run.Rtsp ? "recording" : run.Phase.ToString())}: {message}", e);
            Update(job, j => { j.Status = ExportStatus.Failed; j.Error = message; });
        }
        finally
        {
            TryDeleteDir(tempDir);
            if (output is not null) lock (_lock) _reserved.Remove(output);
        }
    }

    /// <summary>
    /// A part is honored when its real duration matches the covered range (± max(10 s, 10 %)). BySegmentWhole files
    /// are whole recorder files, so they are always cut to the covered range (bounds that reach the file ends stay
    /// open). ByTime/BySegmentRange are never cut: a mismatch is only logged.
    /// </summary>
    private async Task<RemuxPart> PlanPartAsync(ExportJob job, DownloadedPart p, CancellationToken ct)
    {
        var expected = p.CoveredTo - p.CoveredFrom;
        var tolerance = TimeSpan.FromTicks(Math.Max(TimeSpan.FromSeconds(10).Ticks, expected.Ticks / 10));
        TimeSpan? duration = null;
        try { duration = await _media.GetDurationAsync(p.FilePath, ct); }
        catch (Exception e) when (e is not OperationCanceledException) { Log.Warn($"Export {job.Id}: duration of {Path.GetFileName(p.FilePath)} unknown", e); }
        var honored = duration is { } d && (d - expected).Duration() <= tolerance;

        if (p.Method == DownloadMethod.BySegmentWhole)
        {
            var from = p.CoveredFrom - p.NominalStart;
            var to = p.CoveredTo - p.NominalStart;
            TimeSpan? f = from > TimeSpan.Zero ? from : null;
            TimeSpan? t = duration is { } len && to >= len ? null : to;
            if (honored && f is null && t is null) return new RemuxPart(p.FilePath);
            return new RemuxPart(p.FilePath, f, t);
        }
        if (!honored)
            Log.Warn($"Export {job.Id}: {p.Method} part {Path.GetFileName(p.FilePath)} lasts {duration?.ToString() ?? "?"} but covers {expected}; kept as is");
        return new RemuxPart(p.FilePath);
    }

    private double EstimateBytes(ExportRequest r)
    {
        double rate;
        lock (_lock) rate = _rates.TryGetValue((r.NvrId, r.Channel), out var x) ? x : DefaultBytesPerSecond;
        return Math.Max(1, rate * (r.To - r.From).TotalSeconds);
    }

    /// <summary>Remembers the recorder's real byte rate for this camera, for the next estimate.</summary>
    private void Learn(ExportRequest r, IReadOnlyList<DownloadedPart> parts)
    {
        try
        {
            var bytes = parts.Sum(p => new FileInfo(p.FilePath).Length);
            var seconds = parts.Sum(p => (p.CoveredTo - p.CoveredFrom).TotalSeconds);
            if (seconds >= 10 && bytes > 0) lock (_lock) _rates[(r.NvrId, r.Channel)] = bytes / seconds;
        }
        catch (IOException) { }
    }

    private void OnBytes(ExportJob job, RunState run, long bytes, double estimate)
    {
        job.BytesDownloaded = bytes;
        SetProgress(job, run, Math.Min(DownloadShare, DownloadShare * bytes / estimate));
    }

    private void SetProgress(ExportJob job, RunState run, double progress)
    {
        job.Progress = progress;
        var now = _time.GetTimestamp();
        if (_time.GetElapsedTime(run.LastRaise, now) < ProgressInterval) return;
        run.LastRaise = now;
        Raise(job);
    }

    private static double Clamp01(double p) => double.IsNaN(p) ? 0 : Math.Clamp(p, 0, 1);

    private void Update(ExportJob job, Action<ExportJob> change)
    {
        lock (_lock) change(job);
        Raise(job);
    }

    /// <summary>A free output path in the request folder; " (2)", " (3)"... when taken (on disk or by a running job).</summary>
    private string ReserveOutput(ExportRequest r)
    {
        var baseName = ExportNaming.BaseName(r.CameraName, r.Channel, r.From, r.To);
        lock (_lock)
        {
            for (var i = 1; ; i++)
            {
                var path = Path.Combine(r.OutputFolder, (i == 1 ? baseName : $"{baseName} ({i})") + ".mp4");
                if (_reserved.Contains(path) || File.Exists(path) || File.Exists(path + ".part")) continue;
                _reserved.Add(path);
                return path;
            }
        }
    }

    private static string ErrorText(Exception e, RunState run)
    {
        if (e is NvrException n) return n.Message;
        if (IsDiskFull(e)) return "Ổ đĩa đầy. Giải phóng bớt dung lượng rồi bấm Thử lại.";
        if (e is UnauthorizedAccessException) return "Không ghi được vào thư mục lưu video. Hãy chọn thư mục khác trong Cài đặt.";
        if (run.Rtsp) return "Không ghi được video từ đầu ghi. Bấm Thử lại.";
        if (run.Phase == ExportStatus.Converting) return "Không chuyển được video sang MP4. Bấm Thử lại.";
        return "Lỗi khi lưu video. Bấm Thử lại.";
    }

    private static bool IsDiskFull(Exception? e)
    {
        for (; e is not null; e = e.InnerException)
            if (e is IOException && (e.HResult & 0xFFFF) is 112 or 39) return true; // ERROR_DISK_FULL, ERROR_HANDLE_DISK_FULL
        return false;
    }

    // ---- files ----

    private static void DeleteOutput(string? output)
    {
        if (output is null) return;
        TryDelete(output + ".part");
        TryDelete(output);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Warn("Could not delete " + path, e); }
    }

    private static void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Warn("Could not delete " + dir, e); }
    }

    /// <summary>Leftovers of a previous run: job folders (32-hex names) and loose media files.</summary>
    private void CleanTemp()
    {
        try
        {
            if (!Directory.Exists(_temp)) return;
            foreach (var d in Directory.EnumerateDirectories(_temp))
                if (Guid.TryParseExact(Path.GetFileName(d), "N", out _)) TryDeleteDir(d);
            foreach (var f in Directory.EnumerateFiles(_temp))
                if (Path.GetExtension(f).ToLowerInvariant() is ".ps" or ".mp4" or ".part" or ".tmp") TryDelete(f);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Warn("Could not clean " + _temp, e); }
    }

    // ---- events ----

    private void Raise(ExportJob? job) => _events.Writer.TryWrite(job);

    /// <summary>Raises JobChanged in order on one background task, never on the caller's thread.</summary>
    private async Task PumpAsync()
    {
        await foreach (var job in _events.Reader.ReadAllAsync())
        {
            try { JobChanged?.Invoke(job); }
            catch (Exception e) { Log.Error("JobChanged handler failed", e); }
        }
    }
}
