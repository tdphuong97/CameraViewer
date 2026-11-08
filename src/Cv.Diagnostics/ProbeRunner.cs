using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text;
using Cv.Core;
using Cv.Core.Export;
using Cv.Core.Hik;
using Cv.Core.Nvr;
using Cv.Core.Video;

namespace Cv.Diagnostics;

/// <summary>Result of a probe: the full text report, whether every check passed, and where it was saved.</summary>
public sealed record ProbeReport(string Text, bool Passed, string? SavedPath);

public enum ProbeOutcome { Pass, Fail, Skip }

/// <summary>One line of the report.</summary>
public sealed record ProbeStep(string Name, ProbeOutcome Outcome, TimeSpan Elapsed, string Detail);

/// <summary>Step time limits (shorter in tests).</summary>
internal sealed record ProbeTimeouts
{
    public TimeSpan Isapi { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan VideoOpen { get; init; } = TimeSpan.FromSeconds(15);
    /// <summary>How long a stream is watched after its first frame, so fps/bit rate mean something.</summary>
    public TimeSpan Watch { get; init; } = TimeSpan.FromSeconds(3);
    public int MinFrames { get; init; } = 5;
    public TimeSpan Poll { get; init; } = TimeSpan.FromMilliseconds(40);
    /// <summary>A recorder that only serves whole 1-hour files can send gigabytes for the app's own export.</summary>
    public TimeSpan Download { get; init; } = TimeSpan.FromMinutes(10);
    /// <summary>Forced single-method downloads stop here: far more than 10 s of any stream.</summary>
    public long MethodByteCap { get; init; } = 64L * 1024 * 1024;
    public TimeSpan Media { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan Record { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan ExportLength { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// On-site check of a real recorder (Track D implements): device info, clock and time mode, channels, search, people
/// detections (information only), live and playback RTSP (a few frames each), a 10-second ISAPI download by each
/// method, remux to MP4.
/// Read-only on the recorder. Saves "probe-report-yyyyMMdd-HHmmss.txt" (password never written) into reportFolder.
/// Stops at the first login error so the recorder does not lock the account. Cancelling saves the partial report and
/// then throws OperationCanceledException.
/// </summary>
public sealed class ProbeRunner(INvrClientFactory nvrs, IVideoSourceFactory video, IMediaTools media)
{
    internal TimeProvider Time { get; init; } = TimeProvider.System;
    internal ProbeTimeouts Timeouts { get; init; } = new();

    public async Task<ProbeReport> RunAsync(NvrConfig config, string password, string reportFolder, IProgress<string>? progress, CancellationToken ct)
    {
        var started = Time.GetLocalNow();
        var session = new Session(this, config, password ?? "", progress, ct);
        var canceled = false;
        try
        {
            await session.ExecuteAsync();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            canceled = true;
        }
        finally
        {
            session.Cleanup();
        }

        var text = session.Compose(started, canceled);
        string? saved = null;
        try
        {
            Directory.CreateDirectory(reportFolder);
            var path = Path.Combine(reportFolder, $"probe-report-{started:yyyyMMdd-HHmmss}.txt");
            for (var i = 2; File.Exists(path); i++) path = Path.Combine(reportFolder, $"probe-report-{started:yyyyMMdd-HHmmss}-{i}.txt");
            await File.WriteAllTextAsync(path, text, new UTF8Encoding(true), CancellationToken.None);
            saved = path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            text += Environment.NewLine + session.Mask($"Không lưu được báo cáo vào {reportFolder}: {ex.Message}") + Environment.NewLine;
        }
        Log.Info($"Probe {(canceled ? "canceled" : session.Passed ? "passed" : "found problems")}, report {saved ?? "not saved"}");
        if (canceled) throw new OperationCanceledException("Đã hủy chẩn đoán.", ct);
        return new ProbeReport(text, session.Passed, saved);
    }

    private static readonly DownloadMethod[] IsapiMethods = [DownloadMethod.ByTime, DownloadMethod.BySegmentRange, DownloadMethod.BySegmentWhole];

    private sealed class Session(ProbeRunner owner, NvrConfig config, string password, IProgress<string>? progress, CancellationToken ct)
    {
        private readonly List<ProbeStep> _steps = [];
        private readonly List<string> _notes = [];
        private readonly string[] _secrets = new[] { password, Uri.EscapeDataString(password), WebUtility.UrlEncode(password) }
            .Where(s => s.Length > 0).Distinct().OrderByDescending(s => s.Length).ToArray();
        private readonly string _temp = Path.Combine(Path.GetTempPath(), "CameraViewer-probe-" + Guid.NewGuid().ToString("N")[..8]);
        private INvrClient? _client;
        private string? _stopReason;

        // What the summary needs.
        private NvrDeviceInfo? _info;
        private DeviceClock? _clock;
        private TimeSpan? _skew;
        private TimeMode? _mode;
        private IReadOnlyList<ChannelInfo>? _channels;
        private int? _recChannel;
        private IReadOnlyList<RecordSegment> _segments = [];
        private string? _liveSub, _liveMain;
        private bool? _playback;
        private DownloadMethod? _isapiMethod;
        private readonly Dictionary<DownloadMethod, string> _methods = [];
        private bool _isapiOk;
        private TimeSpan? _isapiMp4, _rtspMp4;
        private string? _people;

        private ProbeTimeouts T => owner.Timeouts;

        /// <summary>Main steps only: the indented per-method lines are information (a recorder that only supports
        /// segment downloads works fine).</summary>
        public bool Passed => MainSteps.Any() && MainSteps.All(s => s.Outcome != ProbeOutcome.Fail);

        private IEnumerable<ProbeStep> MainSteps => _steps.Where(s => !s.Name.StartsWith(' '));

        public async Task ExecuteAsync()
        {
            try { _client = owner._nvrs.Create(config, password); }
            catch (Exception ex)
            {
                Add("Tạo kết nối tới đầu ghi", ProbeOutcome.Fail, TimeSpan.Zero, Describe(ex));
                _stopReason = "không tạo được kết nối";
                return;
            }
            var nvr = _client;

            _info = (await Run("Thông tin đầu ghi (deviceInfo)", T.Isapi, nvr.GetDeviceInfoAsync,
                i => (true, $"model {i.Model}, firmware {i.Firmware}"))).Value;

            _clock = (await Run("Đồng hồ đầu ghi", T.Isapi, nvr.GetClockAsync, ClockVerdict)).Value;

            var mode = await Run("Chế độ thời gian (giờ trong kết quả tìm kiếm)", T.Isapi, nvr.ResolveTimeModeAsync,
                m => (true, ModeText(m)));
            if (mode.Ok) _mode = mode.Value;

            _channels = (await Run("Danh sách camera", T.Isapi, nvr.GetChannelsAsync,
                l => l.Count == 0 ? (false, "đầu ghi không trả về camera nào") : (true, ChannelsText(l)))).Value;

            await SearchStep(nvr);
            await PeopleStep(nvr);
            var window = PickWindow();

            var liveCh = _channels?.FirstOrDefault(c => c.No == _recChannel && c.Online)?.No ?? _channels?.FirstOrDefault(c => c.Online)?.No;
            if (liveCh is int lc)
            {
                _liveSub = await WatchStep($"Xem trực tiếp luồng phụ (ô lưới), kênh D{lc}", () => nvr.LiveUrl(lc, StreamKind.Sub), true);
                _liveMain = await WatchStep($"Xem trực tiếp luồng chính (một camera), kênh D{lc}", () => nvr.LiveUrl(lc, StreamKind.Main), true);
            }
            else
            {
                Skip("Xem trực tiếp luồng phụ (ô lưới)", "không có camera nào đang chạy");
                Skip("Xem trực tiếp luồng chính (một camera)", "không có camera nào đang chạy");
            }

            if (window is { } w && _recChannel is int ch)
            {
                _playback = await WatchStep($"Xem lại qua RTSP, kênh D{ch} từ {w.From:HH:mm:ss}",
                    () => nvr.PlaybackUrl(ch, w.From, w.From.AddMinutes(2)), false) is not null;
                await IsapiExportSteps(nvr, ch, w.From, w.To);
                await RtspRecordStep(nvr, ch, w.From, w.To);
            }
            else
            {
                const string why = "không có đoạn ghi hình phù hợp để thử";
                Skip("Xem lại qua RTSP", why);
                Skip("Lưu video qua ISAPI", why);
                Skip("Ghi lại qua RTSP (cách dự phòng)", why);
            }
        }

        public void Cleanup()
        {
            try { _client?.Dispose(); } catch (Exception ex) { Log.Warn("Probe: dispose client", ex); }
            try { if (Directory.Exists(_temp)) Directory.Delete(_temp, true); } catch (Exception ex) { Log.Warn("Probe: delete temp", ex); }
        }

        // ---------- steps ----------

        private (bool, string) ClockVerdict(DeviceClock c)
        {
            var deviceUtc = DateTime.SpecifyKind(c.LocalNow - c.UtcOffset, DateTimeKind.Utc);
            _skew = TimeSpan.FromSeconds(Math.Round((deviceUtc - owner.Time.GetUtcNow().UtcDateTime).TotalSeconds));
            var skewText = $"lệch so với máy tính {Signed(_skew.Value)}";
            if (_skew.Value.Duration() > TimeSpan.FromMinutes(5)) skewText += " (nhiều: nên bật NTP trên đầu ghi)";
            return (true, $"{c.LocalNow:yyyy-MM-dd HH:mm:ss} UTC{Offset(c.UtcOffset)}, {skewText}");
        }

        private async Task SearchStep(INvrClient nvr)
        {
            const string name = "Tìm ghi hình 24 giờ qua";
            if (_channels is not { Count: > 0 }) { Skip(name, "chưa có danh sách camera"); return; }
            var now = _clock?.LocalNow ?? owner.Time.GetLocalNow().DateTime;
            var from = now.AddHours(-24);
            var candidates = _channels.Where(c => c.Online).Take(4).ToList();
            if (candidates.Count == 0) candidates = _channels.Take(2).ToList();
            var tried = new List<string>();

            await Run(name, T.Isapi * candidates.Count, async c =>
            {
                foreach (var ch in candidates)
                {
                    tried.Add($"D{ch.No}");
                    var segs = await nvr.SearchAsync(ch.No, from, now.AddMinutes(5), c);
                    if (segs.Count > 0) { _recChannel = ch.No; _segments = segs; break; }
                }
                return _segments;
            }, segs =>
            {
                if (segs.Count == 0) return (false, $"không thấy ghi hình trên kênh {string.Join(", ", tried)}");
                var spans = Merge(segs);
                var last = segs.Max(s => s.End);
                var behind = now - last;
                var text = $"kênh D{_recChannel}: {segs.Count} đoạn, {spans.Count} khoảng liên tục; đầu {segs.Min(s => s.Start):yyyy-MM-dd HH:mm:ss}, " +
                           $"cuối {last:yyyy-MM-dd HH:mm:ss} (trước giờ đầu ghi {Signed(behind)})";
                // A wrong time mode moves every recording by the UTC offset, into the past or the future.
                if (_clock is { } k && k.UtcOffset != TimeSpan.Zero && (behind.Duration() - k.UtcOffset.Duration()).Duration() < TimeSpan.FromMinutes(10))
                    text += "; CẢNH BÁO: lệch đúng bằng múi giờ, có thể sai chế độ thời gian";
                return (true, text);
            });
        }

        /// <summary>
        /// People detections of the last 24 hours, an information line (a recorder without them still plays and exports):
        /// one request over every channel (first page: how many, where), the busiest channel searched the way the app
        /// does (every page), one picture. Few requests: the recorder logs about one detection a second per person.
        /// </summary>
        private async Task PeopleStep(INvrClient nvr)
        {
            const string name = "  Tìm lúc có người (phát hiện người của camera)";
            if (nvr is not INvrDetections people) { Skip(name, "kết nối này không có chức năng tìm lúc có người"); return; }
            if (_channels is not { Count: > 0 } channels) { Skip(name, "chưa có danh sách camera"); return; }
            await Run(name, T.Isapi * 3, async c =>
            {
                try { return await PeopleCheckAsync(nvr, people, channels, c); }
                catch (NvrDetectionUnsupportedException ex)
                {
                    _people = "đầu ghi không hỗ trợ (xem và lưu video không bị ảnh hưởng)";
                    return (false, $"{ex.Message} Xem và lưu video không bị ảnh hưởng.");
                }
            }, r => r);
        }

        private async Task<(bool, string)> PeopleCheckAsync(INvrClient nvr, INvrDetections people, IReadOnlyList<ChannelInfo> channels, CancellationToken c)
        {
            var now = _clock?.LocalNow ?? owner.Time.GetLocalNow().DateTime;
            var (from, to) = (now.AddHours(-24), now.AddMinutes(5));
            const string quiet = "có thể camera chưa bật phát hiện người (Motion Detection 2.0, mục tiêu người)";
            var parts = new List<string>();

            int? busiest = null;
            if (nvr is INvrPeopleOverview overview)
            {
                try
                {
                    var o = await overview.PeopleOverviewAsync(channels.Select(x => x.No).ToList(), from, to, c);
                    if (o.Events.Count == 0)
                    {
                        _people = "không thấy lần nào trong 24 giờ qua";
                        return (true, $"không thấy lần nào trong 24 giờ qua trên {channels.Count} camera: {quiet}");
                    }
                    var byChannel = o.Events.GroupBy(e => e.Channel).ToList();
                    busiest = byChannel.OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First().Key;
                    var total = o.More && o.Total <= o.Events.Count ? $"{o.Total}+" : o.Total.ToString(CultureInfo.InvariantCulture);
                    var where = string.Join(" ", byChannel.Select(g => g.Key).Order().Select(k => $"D{k}"));
                    _people = $"{total} lần trong 24 giờ qua, kênh {where}{(o.More ? "…" : "")}";
                    parts.Add($"có: {total} lần trong 24 giờ qua, kênh {where}" + (o.More ? $" (theo {o.Events.Count} lần đầu)" : ""));
                }
                catch (NvrException ex) when (ex is not (NvrAuthException or NvrUnreachableException or NvrDetectionUnsupportedException))
                {
                    // The app searches one channel at a time: that is still worth checking.
                    parts.Add($"tìm mọi kênh một lần không được ({Describe(ex)})");
                }
            }

            var ch = busiest ?? _recChannel ?? channels.FirstOrDefault(x => x.Online)?.No ?? channels[0].No;
            var list = await people.SearchPeopleAsync(ch, from, to, c);
            if (list.Count == 0)
            {
                _people ??= $"kênh D{ch}: không thấy lần nào trong 24 giờ qua";
                parts.Add($"kênh D{ch}: không thấy lần nào trong 24 giờ qua, {quiet}");
                return (true, string.Join("; ", parts));
            }
            _people ??= $"kênh D{ch}: {list.Count} lần trong 24 giờ qua";
            parts.Add($"kênh D{ch}: {list.Count} lần, gần nhất {list[^1].Time:yyyy-MM-dd HH:mm:ss}");

            if (list.LastOrDefault(e => e.PictureUri is not null) is not { } shot)
            {
                parts.Add("đầu ghi không lưu ảnh kèm theo");
                return (true, string.Join("; ", parts));
            }
            try
            {
                var bytes = await people.GetPictureAsync(shot.PictureUri!, c);
                _people += $"; ảnh được ({Bytes(bytes.Length)})";
                parts.Add($"ảnh lúc {shot.Time:HH:mm:ss}: {Bytes(bytes.Length)}{(bytes is [0xFF, 0xD8, 0xFF, ..] ? " JPEG" : "")}");
                return (true, string.Join("; ", parts));
            }
            catch (NvrException ex) when (ex is not (NvrAuthException or NvrUnreachableException))
            {
                _people += "; ảnh KHÔNG tải được";
                parts.Add($"ảnh lúc {shot.Time:HH:mm:ss}: {Describe(ex)}");
                return (false, string.Join("; ", parts));
            }
        }

        private (DateTime From, DateTime To)? PickWindow()
        {
            if (_segments.Count == 0) return null;
            var len = T.ExportLength;
            var ordered = _segments.OrderBy(s => s.Start).ToList();
            // A finished file (not the one still being written), long enough, newest first; middle of it.
            var pick = ordered.Take(ordered.Count - 1).LastOrDefault(s => s.End - s.Start >= len + TimeSpan.FromMinutes(2))
                       ?? ordered.LastOrDefault(s => s.End - s.Start >= len + TimeSpan.FromSeconds(30))
                       ?? ordered.OrderByDescending(s => s.End - s.Start).First();
            if (pick.End - pick.Start < len) return null;
            var mid = pick.Start + (pick.End - pick.Start - len) / 2;
            var from = new DateTime(mid.Ticks - mid.Ticks % TimeSpan.TicksPerSecond, mid.Kind);
            return (from, from + len);
        }

        /// <summary>Opens the stream, waits for a few frames, reports stats. Returns the stats text, null when it failed.</summary>
        private async Task<string?> WatchStep(string name, Func<string> url, bool live)
        {
            string? result = null;
            await Run(name, T.VideoOpen + T.Watch + TimeSpan.FromSeconds(10), async c =>
            {
                var u = url();
                using var src = owner._video.Create(new VideoSourceOptions
                {
                    Url = u, IsLive = live, Reconnect = false, OpenTimeout = T.VideoOpen, TargetWidth = 640, TargetHeight = 360,
                });
                var sw = Stopwatch.StartNew();
                src.Start();
                long seq = 0;
                var frames = 0;
                TimeSpan? first = null;
                while (true)
                {
                    if (src.TryCopyLatest(ref seq, static (_, _, _, _) => { })) { frames++; first ??= sw.Elapsed; }
                    var state = src.State;
                    if (state == VideoState.Failed || (state == VideoState.Ended && frames == 0))
                        return (false, $"{src.Error ?? StateText(state)} | {Log.Redact(u)}");
                    if (first is { } f)
                    {
                        if (frames >= T.MinFrames && sw.Elapsed - f >= T.Watch) break;
                        if (state == VideoState.Ended || sw.Elapsed - f >= T.Watch + TimeSpan.FromSeconds(5)) break;
                    }
                    else if (sw.Elapsed > T.VideoOpen)
                        return (false, $"không nhận được hình sau {T.VideoOpen.TotalSeconds:0} giây (trạng thái: {StateText(state)}{(src.Error is null ? "" : ", " + src.Error)}) | {Log.Redact(u)}");
                    await Task.Delay(T.Poll, c);
                }
                if (frames < 2) return (false, $"chỉ nhận được {frames} hình rồi đứng | {Log.Redact(u)}");
                var s = src.Stats;
                var stats = $"{s.Codec} {s.Width}x{s.Height}";
                result = stats;
                return (true, $"{stats}, {s.Fps:0.#} hình/giây, {s.Kbps:0} kbit/s, {(s.HardwareDecoding ? "giải mã bằng card đồ họa" : "giải mã bằng CPU")}; " +
                              $"hình đầu sau {Seconds(first!.Value)}, {frames} hình nhận được | {Log.Redact(u)}");
            }, r => r);
            return result;
        }

        private async Task IsapiExportSteps(INvrClient nvr, int ch, DateTime from, DateTime to)
        {
            var len = to - from;
            var diag = nvr as INvrDiagnostics;

            // 1. The export as the app does it: DownloadAsync picks the method (ByTime, then BySegmentRange, then
            // BySegmentWhole); no byte cap, a recorder that only serves whole files really sends a whole file.
            IReadOnlyList<DownloadedPart>? parts = null;
            var unsupported = false;
            await Run($"Lưu video {Seconds(len)} qua ISAPI như phần mềm làm, kênh D{ch} {from:HH:mm:ss}–{to:HH:mm:ss}", T.Download + T.Media, async c =>
            {
                var bytes = new ByteCounter(long.MaxValue);
                var sw = Stopwatch.StartNew();
                try { parts = await nvr.DownloadAsync(ch, from, to, Folder("auto"), bytes, c); }
                catch (NvrDownloadUnsupportedException ex)
                {
                    unsupported = true;
                    return (false, Describe(ex) + " (phần mềm sẽ ghi lại qua RTSP)");
                }
                if (parts.Count == 0) return (false, "đầu ghi không trả về phần nào");
                var method = diag?.WorkingMethod ?? parts[0].Method;
                _isapiMethod = method;
                var took = sw.Elapsed;
                var conv = await ConvertAsync(parts, len, Path.Combine(_temp, "probe-isapi.mp4"), c);
                _isapiMp4 = conv.Mp4;
                _isapiOk = conv.Ok;
                return (conv.Ok, $"cách phần mềm chọn: {method}; tải {Bytes(bytes.Value)} trong {Seconds(took)}; {conv.Detail}");
            }, r => r);
            DeleteFolder("auto");

            // 2. Each ISAPI method alone: what this recorder can do (informational lines, they do not fail the probe).
            if (diag is not null)
            {
                foreach (var m in IsapiMethods) await MethodStep(diag, m, ch, from, to);
                return;
            }
            // Without INvrDiagnostics: DownloadAsync on a fresh client tried the methods in order, so the method it
            // returned tells which earlier ones the recorder refused.
            if (_stopReason is not null) return;
            var usedAt = parts is { Count: > 0 } ? Array.IndexOf(IsapiMethods, parts[0].Method) : -1;
            for (var i = 0; i < IsapiMethods.Length; i++)
            {
                var m = IsapiMethods[i];
                if (unsupported) Add($"  cách {m}", ProbeOutcome.Fail, TimeSpan.Zero, "đầu ghi không làm được");
                else if (usedAt < 0) Skip($"  cách {m}", "không xác định được");
                else if (i < usedAt) Add($"  cách {m}", ProbeOutcome.Fail, TimeSpan.Zero, $"đầu ghi không làm được (phần mềm đã chuyển sang cách {IsapiMethods[usedAt]})");
                else if (i == usedAt) Add($"  cách {m}", ProbeOutcome.Pass, TimeSpan.Zero, "đầu ghi làm được, phần mềm dùng cách này");
                else Skip($"  cách {m}", "không cần thử: phần mềm dừng ở cách đầu tiên làm được");
            }
        }

        /// <summary>
        /// One forced method. A download far bigger than the range (a whole 1-hour file can be gigabytes on a real
        /// recorder) is stopped early: enough to know what the recorder sends.
        /// </summary>
        private async Task MethodStep(INvrDiagnostics d, DownloadMethod m, int ch, DateTime from, DateTime to)
        {
            var len = to - from;
            await Run($"  cách {m}", T.Download + T.Media, async c =>
            {
                var bytes = new ByteCounter(T.MethodByteCap);
                using var cap = CancellationTokenSource.CreateLinkedTokenSource(c);
                bytes.Exceeded = cap.Cancel;
                var sw = Stopwatch.StartNew();
                IReadOnlyList<DownloadedPart> got;
                try
                {
                    got = await d.DownloadWithAsync(m, ch, from, to, Folder(m.ToString()), bytes, cap.Token);
                }
                catch (NvrDownloadUnsupportedException)
                {
                    _methods[m] = "từ chối";
                    return (false, "đầu ghi từ chối cách này");
                }
                catch (OperationCanceledException) when (bytes.Over && !c.IsCancellationRequested)
                {
                    var rate = $"{Bytes(bytes.Value)} sau {Seconds(sw.Elapsed)}, {bytes.Value / 1048576.0 / Math.Max(0.001, sw.Elapsed.TotalSeconds):0.0} MB/giây";
                    if (m == DownloadMethod.BySegmentWhole)
                    {
                        _methods[m] = "được (cả đoạn)";
                        return (true, $"đầu ghi gửi cả đoạn ghi ({rate}); dừng sớm cho đỡ tốn thời gian");
                    }
                    _methods[m] = "trả cả đoạn";
                    // ExportPlanner recognises such a file by its length and cuts it like a whole segment.
                    return (true, $"đầu ghi gửi cả đoạn ghi thay vì {Seconds(len)} ({rate}): phần mềm nhận ra và cắt lại như cách BySegmentWhole; dừng sớm cho đỡ tốn thời gian");
                }
                var took = sw.Elapsed;
                var conv = await ConvertAsync(got, len, Path.Combine(_temp, $"probe-{m}.mp4"), c);
                _methods[m] = conv.Ok ? "được" : "sai";
                return (conv.Ok, $"tải {Bytes(bytes.Value)} trong {Seconds(took)}; {conv.Detail}");
            }, r => r);
            DeleteFolder(m.ToString());
        }

        /// <summary>
        /// The export's own rule (<see cref="ExportPlanner"/>): a part as long as its range is copied whole, a whole
        /// recorder file is cut to the range, anything else is a problem. Then remux to MP4 and check its length.
        /// </summary>
        private async Task<(bool Ok, string Detail, TimeSpan? Mp4)> ConvertAsync(IReadOnlyList<DownloadedPart> parts, TimeSpan expected, string mp4, CancellationToken c)
        {
            var remux = new List<RemuxPart>();
            var notes = new List<string>();
            var ok = true;
            for (var i = 0; i < parts.Count; i++)
            {
                var p = parts[i];
                var covered = p.CoveredTo - p.CoveredFrom;
                var d = await owner._media.GetDurationAsync(p.FilePath, c);
                if (d is null) return (false, $"{PartsText(parts)}; phần {i + 1}: không đọc được video trong file", null);
                var (part, fit) = ExportPlanner.Plan(p, d);
                remux.Add(part);
                switch (fit)
                {
                    case PartFit.Exact:
                        notes.Add($"phần {i + 1} dài {Seconds(d.Value)}, đúng khoảng yêu cầu");
                        break;
                    case PartFit.WholeFileCut:
                        var whole = p.Method == DownloadMethod.BySegmentRange ? "đầu ghi gửi cả đoạn ghi " : "cả đoạn ghi ";
                        notes.Add($"phần {i + 1}: {whole}{Seconds(d.Value)}, cắt lấy {Seconds(part.From ?? TimeSpan.Zero)}–{(part.To is { } t ? Seconds(t) : "hết")}");
                        break;
                    default:
                        ok = false;
                        notes.Add($"phần {i + 1} dài {Seconds(d.Value)} thay vì {Seconds(covered)}: đầu ghi không cắt đúng khoảng yêu cầu");
                        break;
                }
            }
            await owner._media.RemuxAsync(remux, mp4, null, c);
            var md = await owner._media.GetDurationAsync(mp4, c);
            // A download by time asks a few seconds past the end (HikNvrClient.TimeEndPad): what was asked is the yardstick.
            var asked = parts.Aggregate(TimeSpan.Zero, (sum, p) => sum + (p.CoveredTo - p.CoveredFrom));
            var (lok, ltext) = LengthVerdict(md, expected, mp4, asked);
            return (ok && lok, $"{PartsText(parts)}; {string.Join("; ", notes)}; {ltext}", md);
        }

        private string Folder(string name)
        {
            var f = Path.Combine(_temp, name);
            Directory.CreateDirectory(f);
            return f;
        }

        /// <summary>Downloads can be large: free the disk after each step.</summary>
        private void DeleteFolder(string name)
        {
            try { var f = Path.Combine(_temp, name); if (Directory.Exists(f)) Directory.Delete(f, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn("Probe: delete " + name, ex); }
        }

        private async Task RtspRecordStep(INvrClient nvr, int ch, DateTime from, DateTime to)
        {
            var mp4 = Path.Combine(_temp, "probe-rtsp.mp4");
            Directory.CreateDirectory(_temp);
            var url = "";
            await Run($"Ghi lại {Seconds(to - from)} qua RTSP (cách dự phòng, chạy bằng thời gian thật)", T.Record + (to - from), async c =>
            {
                url = nvr.PlaybackUrl(ch, from, to);
                await owner._media.RecordAsync(url, to - from, mp4, null, c);
                return await owner._media.GetDurationAsync(mp4, c);
            }, d =>
            {
                _rtspMp4 = d;
                var (ok, text) = LengthVerdict(d, to - from, mp4);
                return (ok, text + " | " + Log.Redact(url));
            });
        }

        private static (bool, string) LengthVerdict(TimeSpan? d, TimeSpan expected, string file, TimeSpan? asked = null)
        {
            if (d is null) return (false, "không đọc được file MP4");
            var size = File.Exists(file) ? Bytes(new FileInfo(file).Length) : "?";
            var longest = asked is { } a && a > expected ? a : expected;
            // Cutting at the key frame before the start may add up to one GOP.
            var ok = d.Value >= expected * 0.8 && d.Value <= longest + TimeSpan.FromSeconds(5);
            var extra = longest - expected >= TimeSpan.FromSeconds(1) ? $", tải thêm {Seconds(longest - expected)} ở cuối cho chắc đủ" : "";
            return (ok, $"MP4 dài {Seconds(d.Value)} (cần {Seconds(expected)}{extra}), {size}" + (ok ? "" : " — SAI ĐỘ DÀI"));
        }

        // ---------- plumbing ----------

        private async Task<(bool Ok, TResult? Value)> Run<TResult>(string name, TimeSpan timeout, Func<CancellationToken, Task<TResult>> action,
            Func<TResult, (bool Ok, string Detail)> verdict)
        {
            ct.ThrowIfCancellationRequested();
            if (_stopReason is not null) { Skip(name, _stopReason); return (false, default); }
            progress?.Report(name);
            var sw = Stopwatch.StartNew();
            using var cts = Linked(timeout);
            try
            {
                var value = await action(cts.Token);
                var (ok, detail) = verdict(value);
                Add(name, ok ? ProbeOutcome.Pass : ProbeOutcome.Fail, sw.Elapsed, detail);
                return (ok, value);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Add(name, ProbeOutcome.Fail, sw.Elapsed, $"quá thời gian chờ ({timeout.TotalSeconds:0} giây)");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A lost or locked recorder stops the probe, so it is never just an information line: the report must
                // not say ĐẠT with the rest skipped.
                Add(ex is NvrAuthException or NvrUnreachableException ? name.TrimStart() : name, ProbeOutcome.Fail, sw.Elapsed, Describe(ex));
                StopIfFatal(ex);
            }
            return (false, default);
        }

        private void StopIfFatal(Exception ex)
        {
            if (ex is NvrAuthException) _stopReason = "đã dừng sau lỗi đăng nhập để đầu ghi không khóa tài khoản";
            else if (ex is NvrUnreachableException) _stopReason = "không kết nối được đầu ghi";
        }

        private CancellationTokenSource Linked(TimeSpan timeout)
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            return cts;
        }

        private void Add(string name, ProbeOutcome outcome, TimeSpan elapsed, string detail)
        {
            var d = Mask(detail).Replace('\r', ' ').Replace('\n', ' ');
            if (d.Length > 600) d = d[..600] + "...";
            lock (_steps) _steps.Add(new ProbeStep(Mask(name), outcome, elapsed, d));
        }

        /// <summary>After a fatal error every later step names that error as the reason.</summary>
        private void Skip(string name, string reason) => Add(name, ProbeOutcome.Skip, TimeSpan.Zero, _stopReason ?? reason);

        public string Mask(string? s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = Log.Redact(s);
            foreach (var secret in _secrets) s = s.Replace(secret, "***", StringComparison.Ordinal);
            return s;
        }

        // ---------- report ----------

        public string Compose(DateTimeOffset started, bool canceled)
        {
            var pass = MainSteps.Count(s => s.Outcome == ProbeOutcome.Pass);
            var fail = MainSteps.Count(s => s.Outcome == ProbeOutcome.Fail);
            var skip = MainSteps.Count(s => s.Outcome == ProbeOutcome.Skip);
            var sb = new StringBuilder();
            sb.AppendLine("CAMERA VIEWER - BÁO CÁO CHẨN ĐOÁN ĐẦU GHI");
            sb.AppendLine(new string('=', 60));
            Field(sb, "Thời điểm", $"{started:yyyy-MM-dd HH:mm:ss} (giờ máy tính, UTC{Offset(started.Offset)})");
            Field(sb, "Phần mềm", $"Camera Viewer {AppVersion()}");
            Field(sb, "Windows", $"{Environment.OSVersion.VersionString}, {(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")}");
            Field(sb, "Đầu ghi", $"{(config.UseHttps ? "https" : "http")}://{config.Host}:{config.HttpPort}, RTSP {config.RtspPort}, tài khoản \"{config.User}\" (mật khẩu không ghi vào báo cáo)");
            Field(sb, "Chế độ giờ (cài đặt)", config.TimeMode.ToString());
            sb.AppendLine();

            var verdict = canceled ? "ĐÃ HỦY giữa chừng" : fail == 0 ? "ĐẠT" : "CÓ LỖI";
            sb.AppendLine($"KẾT QUẢ: {verdict} — {pass} bước đạt, {fail} lỗi, {skip} bỏ qua");
            if (_info is not null) Field(sb, "Đầu ghi", $"{_info.Model}, firmware {_info.Firmware}");
            if (_channels is not null)
            {
                var off = _channels.Where(c => !c.Online).Select(c => $"D{c.No}").ToList();
                Field(sb, "Camera", $"{_channels.Count} ({_channels.Count - off.Count} đang chạy{(off.Count > 0 ? "; tắt: " + string.Join(", ", off) : "")})");
            }
            if (_mode is not null || _skew is not null)
                Field(sb, "Thời gian", $"{(_mode is { } m ? ModeText(m) : "chưa xác định")}{(_skew is { } k ? $"; đồng hồ lệch máy tính {Signed(k)}" : "")}");
            if (_recChannel is not null) Field(sb, "Ghi hình", $"kênh D{_recChannel}, {_segments.Count} đoạn trong 24 giờ qua");
            if (_people is not null) Field(sb, "Lúc có người", _people);
            Field(sb, "Xem trực tiếp", _liveSub is null && _liveMain is null ? "KHÔNG được" :
                $"{(_liveSub is null ? "luồng phụ KHÔNG được" : "luồng phụ " + _liveSub)}; {(_liveMain is null ? "luồng chính KHÔNG được" : "luồng chính " + _liveMain)}");
            Field(sb, "Xem lại", _playback switch { true => "được", false => "KHÔNG được", null => "chưa thử" });
            Field(sb, "Lưu video", ExportSummary());
            foreach (var f in MainSteps.Where(s => s.Outcome == ProbeOutcome.Fail))
                sb.AppendLine($"  ! {f.Name.Trim()}: {Short(f.Detail)}");
            foreach (var n in _notes) sb.AppendLine("  " + n);
            if (canceled) sb.AppendLine("  Người dùng đã hủy; các bước sau đó không chạy.");
            sb.AppendLine();

            sb.AppendLine("CHI TIẾT");
            sb.AppendLine(new string('-', 60));
            var width = Math.Min(70, _steps.Count == 0 ? 10 : _steps.Max(s => s.Name.Length));
            var i = 0;
            foreach (var s in _steps)
            {
                var no = s.Name.StartsWith(' ') ? "   " : $"{++i,2}.";
                var tag = s.Outcome switch { ProbeOutcome.Pass => "PASS", ProbeOutcome.Fail => "FAIL", _ => "SKIP" };
                var ms = s.Elapsed > TimeSpan.Zero ? $"{s.Elapsed.TotalMilliseconds,7:0} ms" : "       -  ";
                sb.AppendLine($"{no} [{tag}] {s.Name.PadRight(width)} | {ms} | {s.Detail}");
            }
            sb.AppendLine();
            sb.AppendLine("PASS = đạt, FAIL = lỗi, SKIP = bỏ qua. Chẩn đoán chỉ đọc: không thay đổi gì trên đầu ghi.");
            return Mask(sb.ToString());
        }

        private string ExportSummary()
        {
            var methods = _methods.Count == 0 ? "" : " [" + string.Join(", ", IsapiMethods.Where(_methods.ContainsKey).Select(k => $"{k}: {_methods[k]}")) + "]";
            if (_isapiMethod is { } m && _isapiMp4 is not null)
                return _isapiOk
                    ? $"được, tải qua ISAPI (cách {m}), MP4 dài {Seconds(_isapiMp4.Value)}{methods}"
                    : $"CÓ VẤN ĐỀ: tải qua ISAPI (cách {m}) nhưng video không đúng khoảng yêu cầu (xem bước lỗi){methods}";
            if (_rtspMp4 is not null)
                return $"chỉ bằng cách ghi lại qua RTSP (chậm: bằng thời gian thật), MP4 dài {Seconds(_rtspMp4.Value)}{methods}";
            if (_isapiMethod is { } m2) return $"tải qua ISAPI được (cách {m2}) nhưng chuyển sang MP4 có lỗi{methods}";
            return _steps.Any(s => s.Name.StartsWith("Lưu video", StringComparison.Ordinal) && s.Outcome != ProbeOutcome.Skip) ? "KHÔNG được" : "chưa thử";
        }

        private static void Field(StringBuilder sb, string label, string value) => sb.AppendLine($"  {label,-21}: {value}");

        private static string Short(string s) => s.Length > 160 ? s[..160] + "..." : s;

        private static string AppVersion()
        {
            // The app's exe is CameraViewer.dll inside the bundle; elsewhere (tests, tools) say which assembly ran it.
            var a = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(x => x.GetName().Name == "CameraViewer");
            if (a is null) return $"(chạy từ {Assembly.GetEntryAssembly()?.GetName().Name ?? "?"})";
            var v = a.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? a.GetName().Version?.ToString() ?? "?";
            var plus = v.IndexOf('+');
            return plus > 0 ? v[..plus] : v;
        }

        private static string ModeText(TimeMode m) => m switch
        {
            TimeMode.LocalWithZ => "LocalWithZ (giờ địa phương ghi kèm chữ Z)",
            TimeMode.Utc => "Utc (giờ quốc tế)",
            _ => m.ToString(),
        };

        private static string ChannelsText(IReadOnlyList<ChannelInfo> l)
        {
            var off = l.Where(c => !c.Online).Select(c => $"D{c.No}").ToList();
            var models = l.Where(c => !string.IsNullOrEmpty(c.CameraModel)).GroupBy(c => c.CameraModel!)
                .Select(g => g.Count() > 1 ? $"{g.Key} x{g.Count()}" : g.Key);
            var unnamed = l.Count(c => string.IsNullOrWhiteSpace(c.Name));
            return $"{l.Count} camera, {l.Count - off.Count} đang chạy" + (off.Count > 0 ? $", tắt: {string.Join(", ", off)}" : "") +
                   $"; kênh: {string.Join(" ", l.Select(c => $"D{c.No}"))}" +
                   (unnamed > 0 ? $"; {unnamed} camera không có tên" : "") +
                   (models.Any() ? $"; model: {string.Join(", ", models)}" : "");
        }

        private static string PartsText(IReadOnlyList<DownloadedPart> parts)
        {
            long total = 0;
            var lines = parts.Select((p, i) =>
            {
                var len = File.Exists(p.FilePath) ? new FileInfo(p.FilePath).Length : 0;
                total += len;
                return $"phần {i + 1}: {p.CoveredFrom:HH:mm:ss}–{p.CoveredTo:HH:mm:ss} (file bắt đầu {p.NominalStart:HH:mm:ss}), {Bytes(len)}, {Header(p.FilePath)}";
            }).ToList();
            return $"cách {parts[0].Method}, {parts.Count} phần, {Bytes(total)}: " + string.Join("; ", lines);
        }

        /// <summary>What the file starts with: IMKH (Hikvision PS), a PS pack, XML (an error that came back as 200)...</summary>
        private static string Header(string path)
        {
            try
            {
                using var f = File.OpenRead(path);
                var b = new byte[8];
                var n = f.Read(b, 0, b.Length);
                if (n >= 4 && b[0] == 'I' && b[1] == 'M' && b[2] == 'K' && b[3] == 'H') return "đầu file IMKH";
                if (n >= 4 && b[0] == 0 && b[1] == 0 && b[2] == 1 && b[3] == 0xBA) return "đầu file 00 00 01 BA";
                if (n >= 1 && b[0] == '<') return "đầu file là XML";
                return "đầu file " + Convert.ToHexString(b, 0, n);
            }
            catch (IOException) { return "không đọc được file"; }
        }

        private static List<(DateTime Start, DateTime End)> Merge(IReadOnlyList<RecordSegment> segs)
        {
            var spans = new List<(DateTime Start, DateTime End)>();
            foreach (var s in segs.OrderBy(s => s.Start))
            {
                if (spans.Count > 0 && s.Start <= spans[^1].End.AddSeconds(1))
                    spans[^1] = (spans[^1].Start, s.End > spans[^1].End ? s.End : spans[^1].End);
                else spans.Add((s.Start, s.End));
            }
            return spans;
        }

        private static string StateText(VideoState s) => s switch
        {
            VideoState.Idle => "chưa chạy",
            VideoState.Connecting => "đang kết nối",
            VideoState.Reconnecting => "đang kết nối lại",
            VideoState.Ended => "hết video",
            VideoState.Failed => "lỗi",
            _ => s.ToString(),
        };

        private static string Describe(Exception ex)
        {
            var text = ex is NvrException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
            if (ex.InnerException is { } inner) text += $" ({inner.GetType().Name}: {inner.Message})";
            return text;
        }

        private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
        private static string Seconds(TimeSpan t) => string.Create(CultureInfo.InvariantCulture, $"{t.TotalSeconds:0.0} giây");
        private static string Signed(TimeSpan t) => (t < TimeSpan.FromSeconds(-0.5) ? "-" : "+") + t.Duration().ToString(t.Duration() >= TimeSpan.FromDays(1) ? @"d\.hh\:mm\:ss" : @"hh\:mm\:ss", CultureInfo.InvariantCulture);
        private static string Offset(TimeSpan t) => (t < TimeSpan.Zero ? "-" : "+") + t.Duration().ToString(@"hh\:mm", CultureInfo.InvariantCulture);
        private static string Bytes(long b) => b switch
        {
            < 1024 => $"{b} B",
            < 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{b / 1024.0:0.0} KB"),
            _ => string.Create(CultureInfo.InvariantCulture, $"{b / 1048576.0:0.0} MB"),
        };
    }

    /// <summary>Running byte total reported inline (no SynchronizationContext hop like Progress&lt;T&gt;); calls Exceeded once over the cap.</summary>
    private sealed class ByteCounter(long cap) : IProgress<long>
    {
        private long _value;
        private int _over;
        public Action? Exceeded { get; set; }
        public long Value => Interlocked.Read(ref _value);
        public bool Over => Volatile.Read(ref _over) == 1;

        public void Report(long value)
        {
            Interlocked.Exchange(ref _value, value);
            if (value > cap && Interlocked.Exchange(ref _over, 1) == 0) Exceeded?.Invoke();
        }
    }

    private readonly INvrClientFactory _nvrs = nvrs;
    private readonly IVideoSourceFactory _video = video;
    private readonly IMediaTools _media = media;
}
