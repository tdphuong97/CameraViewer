using System.IO;
using Cv.Core.Export;

namespace Cv.App.Demo;

/// <summary>
/// Export queue of the demo: jobs move through Downloading and Converting with believable progress, one at a time,
/// and end Done without writing any file. A few finished jobs are seeded so the list is not empty.
/// </summary>
internal sealed class DemoExportService : IExportService, IDisposable
{
    readonly object _gate = new();
    readonly List<ExportJob> _jobs = [];
    readonly CancellationTokenSource _stop = new();
    readonly SemaphoreSlim _wake = new(0);

    public DemoExportService(string folder, DateTime today)
    {
        Seed(folder, today);
        _ = Task.Run(WorkAsync);
    }

    public IReadOnlyList<ExportJob> Jobs
    {
        get { lock (_gate) return _jobs.ToArray(); }
    }

    public event Action<ExportJob?>? JobChanged;

    public ExportJob Enqueue(ExportRequest request)
    {
        if (request.To <= request.From) throw new ArgumentException("Giờ kết thúc phải sau giờ bắt đầu.");
        if (request.To - request.From > TimeSpan.FromHours(24)) throw new ArgumentException("Mỗi lần chỉ lưu được tối đa 24 giờ.");
        if (string.IsNullOrWhiteSpace(request.OutputFolder) || !Path.IsPathRooted(request.OutputFolder))
            throw new ArgumentException("Hãy chọn thư mục lưu video.");
        var job = new ExportJob { Id = Guid.NewGuid(), Request = request, CreatedAt = DateTime.Now, Status = ExportStatus.Queued };
        lock (_gate) _jobs.Add(job);
        JobChanged?.Invoke(job);
        _wake.Release();
        return job;
    }

    public void Cancel(Guid jobId)
    {
        ExportJob? job;
        lock (_gate)
        {
            job = _jobs.Find(j => j.Id == jobId);
            if (job is null || job.Status is ExportStatus.Done or ExportStatus.Failed or ExportStatus.Canceled) return;
            job.Status = ExportStatus.Canceled;
            job.Progress = 0;
        }
        JobChanged?.Invoke(job);
    }

    public void Retry(Guid jobId)
    {
        ExportJob? job;
        lock (_gate)
        {
            job = _jobs.Find(j => j.Id == jobId);
            if (job is null || job.Status is not (ExportStatus.Failed or ExportStatus.Canceled)) return;
            job.Status = ExportStatus.Queued;
            job.Progress = 0;
            job.BytesDownloaded = 0;
            job.Error = null;
        }
        JobChanged?.Invoke(job);
        _wake.Release();
    }

    public void ClearFinished()
    {
        lock (_gate) _jobs.RemoveAll(j => j.Status is ExportStatus.Done or ExportStatus.Failed or ExportStatus.Canceled);
        JobChanged?.Invoke(null);
    }

    async Task WorkAsync()
    {
        var ct = _stop.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                ExportJob? job;
                lock (_gate) job = _jobs.Where(j => j.Status is ExportStatus.Queued or ExportStatus.Downloading).OrderBy(j => j.CreatedAt).FirstOrDefault();
                if (job is null)
                {
                    await _wake.WaitAsync(ct);
                    continue;
                }
                await RunAsync(job, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    async Task RunAsync(ExportJob job, CancellationToken ct)
    {
        var minutes = (job.Request.To - job.Request.From).TotalMinutes;
        var seconds = Math.Clamp(4 + minutes * 0.5, 5, 25);
        var bytesTotal = (long)(minutes * 60 * 4_000_000 / 8);
        lock (_gate) job.Status = ExportStatus.Downloading;
        while (job.Progress < 0.9)
        {
            await Task.Delay(250, ct);
            lock (_gate)
            {
                if (job.Status == ExportStatus.Canceled) { job.BytesDownloaded = 0; break; }
                job.Progress = Math.Min(0.9, job.Progress + 0.9 * 0.25 / seconds);
                job.BytesDownloaded = (long)(bytesTotal * job.Progress / 0.9);
            }
            JobChanged?.Invoke(job);
        }
        if (job.Status == ExportStatus.Canceled) return;
        lock (_gate) job.Status = ExportStatus.Converting;
        for (var i = 0; i < 6 && job.Status == ExportStatus.Converting; i++)
        {
            await Task.Delay(250, ct);
            lock (_gate) job.Progress = Math.Min(1, job.Progress + 0.1 / 6);
            JobChanged?.Invoke(job);
        }
        lock (_gate)
        {
            if (job.Status == ExportStatus.Canceled) return;
            job.Status = ExportStatus.Done;
            job.Progress = 1;
            job.Method = "Demo";
            job.OutputPath = OutputPath(job.Request);
        }
        JobChanged?.Invoke(job);
    }

    static string OutputPath(ExportRequest r) =>
        Path.Combine(r.OutputFolder, ExportNaming.BaseName(r.CameraName, r.Channel, r.From, r.To) + ".mp4");

    void Seed(string folder, DateTime today)
    {
        ExportJob Make(int ch, double fromH, double minutes, ExportStatus status, double progress, int ageMinutes)
        {
            var from = today.Date.AddHours(fromH);
            var r = new ExportRequest("demo", ch, DemoData.Name(ch), from, from.AddMinutes(minutes), folder);
            return new ExportJob
            {
                Id = Guid.NewGuid(), Request = r, CreatedAt = DateTime.Now.AddMinutes(-ageMinutes), Status = status, Progress = progress,
                BytesDownloaded = (long)(minutes * 60 * 4_000_000 / 8 * Math.Min(1, progress / 0.9)),
                OutputPath = status == ExportStatus.Done ? OutputPath(r) : null, Method = status == ExportStatus.Done ? "Demo" : null,
                Error = status == ExportStatus.Failed ? "Mất kết nối đầu ghi khi đang tải. Hãy thử lại." : null,
            };
        }
        _jobs.Add(Make(1, 8, 5, ExportStatus.Done, 1, 50));
        _jobs.Add(Make(3, 7 + 10 / 60.0, 30, ExportStatus.Failed, 0.4, 35));
        _jobs.Add(Make(2, 9.25, 15, ExportStatus.Downloading, 0.38, 3));
        _jobs.Add(Make(6, 10, 2, ExportStatus.Queued, 0, 1));
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }
}
