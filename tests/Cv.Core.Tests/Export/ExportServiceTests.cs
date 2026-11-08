using Cv.Core.Export;
using Cv.Core.Nvr;
using Cv.Core.Tests.Fakes;

namespace Cv.Core.Tests.Export;

public class ExportServiceTests : IDisposable
{
    private static readonly DateTime From = new(2026, 11, 7, 10, 0, 0);
    private static readonly DateTime To = new(2026, 11, 7, 10, 10, 0);

    private readonly TempDir _dir = new();
    private readonly FakeMediaTools _media = new();
    private readonly Dictionary<string, FakeNvrClient> _nvrs = new() { ["nvr1"] = new FakeNvrClient("nvr1"), ["nvr2"] = new FakeNvrClient("nvr2") };
    private readonly List<ExportService> _services = [];

    private string Temp => Path.Combine(_dir.Path, "temp");
    private string Out => Path.Combine(_dir.Path, "out");
    private FakeNvrClient Nvr => _nvrs["nvr1"];

    public void Dispose()
    {
        foreach (var s in _services) s.Dispose();
        _dir.Dispose();
    }

    private ExportService Service()
    {
        var s = new ExportService(id => _nvrs.GetValueOrDefault(id), _media, Temp, new FixedTime(new DateTime(2026, 11, 7, 20, 0, 0)));
        _services.Add(s);
        return s;
    }

    private ExportRequest Request(string nvr = "nvr1", string camera = "Cổng chính", DateTime? from = null, DateTime? to = null) =>
        new(nvr, 1, camera, from ?? From, to ?? To, Out);

    private static Task Finished(ExportJob job) =>
        Wait.For(() => job.Status is ExportStatus.Done or ExportStatus.Failed or ExportStatus.Canceled, what: "job end");

    private bool TempIsEmpty() => !Directory.Exists(Temp) || !Directory.EnumerateFileSystemEntries(Temp).Any();

    [Fact]
    public async Task An_honored_download_is_remuxed_untrimmed_into_a_named_mp4()
    {
        _media.Duration = _ => TimeSpan.FromMinutes(10).Add(TimeSpan.FromSeconds(3));
        var s = Service();
        var job = s.Enqueue(Request());
        Assert.Equal(new DateTime(2026, 11, 7, 20, 0, 0), job.CreatedAt);
        await Finished(job);

        Assert.Equal(ExportStatus.Done, job.Status);
        Assert.Equal(Path.Combine(Out, "Cổng chính_2026-11-07_10h00-10h10.mp4"), job.OutputPath);
        Assert.True(File.Exists(job.OutputPath));
        Assert.Equal(1, job.Progress);
        Assert.Equal("ByTime", job.Method);
        Assert.Equal(1000, job.BytesDownloaded);
        var part = Assert.Single(Assert.Single(_media.Remuxes).Parts);
        Assert.Null(part.From);
        Assert.Null(part.To);
        Assert.True(TempIsEmpty());
    }

    [Fact]
    public async Task Whole_segments_are_trimmed_to_the_covered_range()
    {
        Nvr.OnDownload = (ch, f, t, folder, _, _) => FakeNvrClient.Parts(
            FakeNvrClient.WritePart(folder, ch, f, f.AddMinutes(5), DownloadMethod.BySegmentWhole, nominal: f.AddMinutes(-30), index: 1),
            FakeNvrClient.WritePart(folder, ch, f.AddMinutes(5), t, DownloadMethod.BySegmentWhole, nominal: f.AddMinutes(5), index: 2, fileStamp: f));
        _media.Duration = p => p.EndsWith("_1.ps") ? TimeSpan.FromMinutes(35) : TimeSpan.FromMinutes(55);
        var job = Service().Enqueue(Request());
        await Finished(job);

        Assert.Equal(ExportStatus.Done, job.Status);
        var parts = Assert.Single(_media.Remuxes).Parts;
        Assert.Equal((TimeSpan.FromMinutes(30), (TimeSpan?)null), (parts[0].From, parts[0].To));
        Assert.Equal(((TimeSpan?)null, TimeSpan.FromMinutes(5)), (parts[1].From, parts[1].To));
        Assert.Equal("BySegmentWhole", job.Method);
    }

    [Fact]
    public async Task A_whole_segment_that_is_exactly_the_range_is_not_trimmed()
    {
        Nvr.OnDownload = (ch, f, t, folder, _, _) => FakeNvrClient.Parts(
            FakeNvrClient.WritePart(folder, ch, f, t, DownloadMethod.BySegmentWhole, nominal: f));
        _media.Duration = _ => TimeSpan.FromMinutes(10);
        var job = Service().Enqueue(Request());
        await Finished(job);
        var part = Assert.Single(Assert.Single(_media.Remuxes).Parts);
        Assert.Equal(((TimeSpan?)null, (TimeSpan?)null), (part.From, part.To));
    }

    [Theory]
    [InlineData(DownloadMethod.ByTime)]
    [InlineData(DownloadMethod.BySegmentRange)]
    public async Task A_mismatch_of_a_cut_download_is_kept_untrimmed(DownloadMethod method)
    {
        Nvr.OnDownload = (ch, f, t, folder, _, _) => FakeNvrClient.Parts(FakeNvrClient.WritePart(folder, ch, f, t, method));
        _media.Duration = _ => TimeSpan.FromMinutes(42);
        var job = Service().Enqueue(Request());
        await Finished(job);
        Assert.Equal(ExportStatus.Done, job.Status);
        var part = Assert.Single(Assert.Single(_media.Remuxes).Parts);
        Assert.Equal(((TimeSpan?)null, (TimeSpan?)null), (part.From, part.To));
    }

    [Fact]
    public async Task A_segment_range_answer_that_is_the_whole_segment_is_cut_like_a_whole_file()
    {
        // Firmware ignored the narrowed times and sent the whole 09:30-10:30 file for a 10:00-10:10 request.
        Nvr.OnDownload = (ch, f, t, folder, _, _) =>
        {
            var part = FakeNvrClient.WritePart(folder, ch, f, t, DownloadMethod.BySegmentRange) with
            {
                Segment = new TimeRange(f.AddMinutes(-30), f.AddMinutes(30)),
            };
            return FakeNvrClient.Parts(part);
        };
        _media.Duration = _ => TimeSpan.FromMinutes(60);
        var job = Service().Enqueue(Request());
        await Finished(job);

        Assert.Equal(ExportStatus.Done, job.Status);
        var part = Assert.Single(Assert.Single(_media.Remuxes).Parts);
        Assert.Equal((TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(40)), (part.From, part.To));
    }

    [Fact]
    public async Task Unsupported_download_falls_back_to_rtsp_recording()
    {
        Nvr.OnDownload = (_, _, _, _, _, _) => throw new NvrDownloadUnsupportedException("refused");
        var job = Service().Enqueue(Request());
        await Finished(job);

        Assert.Equal(ExportStatus.Done, job.Status);
        Assert.Equal("RtspRecord", job.Method);
        var rec = Assert.Single(_media.Records);
        // 5 s past the end (ExportService.RecordEndPad): the recorder may stop short on the file it is writing.
        Assert.Equal("rtsp://admin:pw@10.0.0.1:554/Streaming/tracks/101?starttime=20261107T100000Z&endtime=20261107T101005Z", rec.Url);
        Assert.Equal(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(5), rec.Duration);
        Assert.StartsWith(Temp, rec.Output);
        Assert.Equal(Path.Combine(Out, "Cổng chính_2026-11-07_10h00-10h10.mp4"), job.OutputPath);
        Assert.True(File.Exists(job.OutputPath));
        Assert.Equal(1, Nvr.Resolved);
        Assert.Empty(_media.Remuxes);
        Assert.True(TempIsEmpty());
    }

    [Fact]
    public async Task Cancel_while_downloading_deletes_everything()
    {
        var started = new TaskCompletionSource();
        Nvr.OnDownload = async (ch, f, t, folder, _, ct) =>
        {
            FakeNvrClient.WritePart(folder, ch, f, t);
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return [];
        };
        var s = Service();
        var job = s.Enqueue(Request());
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        s.Cancel(job.Id);
        await Finished(job);

        Assert.Equal(ExportStatus.Canceled, job.Status);
        Assert.True(TempIsEmpty());
        Assert.Empty(Directory.GetFiles(Out));
    }

    [Fact]
    public async Task Cancel_while_converting_deletes_the_partial_mp4()
    {
        _media.RemuxHook = ct => Task.Delay(Timeout.Infinite, ct);
        var s = Service();
        var job = s.Enqueue(Request());
        await Wait.For(() => job.Status == ExportStatus.Converting);
        s.Cancel(job.Id);
        await Finished(job);
        Assert.Equal(ExportStatus.Canceled, job.Status);
        Assert.Empty(Directory.GetFiles(Out));
        Assert.True(TempIsEmpty());
    }

    [Fact]
    public async Task A_queued_job_can_be_canceled_and_never_runs()
    {
        var gate = new TaskCompletionSource();
        Nvr.OnDownload = async (ch, f, t, folder, _, ct) => { await gate.Task.WaitAsync(ct); return [FakeNvrClient.WritePart(folder, ch, f, t)]; };
        var s = Service();
        var first = s.Enqueue(Request());
        var second = s.Enqueue(Request());
        await Wait.For(() => first.Status == ExportStatus.Downloading);
        s.Cancel(second.Id);
        Assert.Equal(ExportStatus.Canceled, second.Status);
        gate.SetResult();
        await Finished(first);
        await Task.Delay(100);
        Assert.Equal(1, Nvr.Downloads);
        Assert.Equal(ExportStatus.Canceled, second.Status);
    }

    [Fact]
    public async Task A_failed_job_can_be_retried()
    {
        var calls = 0;
        Nvr.OnDownload = (ch, f, t, folder, _, _) => ++calls == 1
            ? throw new NvrUnreachableException("Mất kết nối đầu ghi khi đang tải video. Kiểm tra mạng rồi bấm Thử lại.")
            : FakeNvrClient.Parts(FakeNvrClient.WritePart(folder, ch, f, t));
        var s = Service();
        var job = s.Enqueue(Request());
        await Finished(job);
        Assert.Equal(ExportStatus.Failed, job.Status);
        Assert.Equal("Mất kết nối đầu ghi khi đang tải video. Kiểm tra mạng rồi bấm Thử lại.", job.Error);

        s.Retry(job.Id);
        Assert.Null(job.Error);
        await Finished(job);
        Assert.Equal(ExportStatus.Done, job.Status);
        Assert.Single(s.Jobs);
    }

    [Fact]
    public async Task Retry_ignores_running_and_done_jobs()
    {
        var s = Service();
        var job = s.Enqueue(Request());
        await Finished(job);
        s.Retry(job.Id);
        Assert.Equal(ExportStatus.Done, job.Status);
        await Task.Delay(50);
        Assert.Equal(1, Nvr.Downloads);
    }

    [Fact]
    public async Task Nothing_recorded_fails_with_the_recorder_message()
    {
        Nvr.OnDownload = (_, _, _, _, _, _) => throw new NvrException("Không có ghi hình trong khoảng thời gian này.");
        var job = Service().Enqueue(Request());
        await Finished(job);
        Assert.Equal(ExportStatus.Failed, job.Status);
        Assert.Equal("Không có ghi hình trong khoảng thời gian này.", job.Error);
        Assert.True(TempIsEmpty());
    }

    [Fact]
    public async Task An_unknown_recorder_fails_readably()
    {
        var job = Service().Enqueue(Request(nvr: "gone"));
        await Finished(job);
        Assert.Equal(ExportStatus.Failed, job.Status);
        Assert.Contains("Không tìm thấy đầu ghi", job.Error);
    }

    [Fact]
    public async Task Conversion_errors_are_vietnamese_and_leave_no_partial_file()
    {
        _media.RemuxError = new InvalidOperationException("avformat_open_input failed");
        var job = Service().Enqueue(Request());
        await Finished(job);
        Assert.Equal(ExportStatus.Failed, job.Status);
        Assert.Equal("Không chuyển được video sang MP4. Bấm Thử lại.", job.Error);
        Assert.Empty(Directory.GetFiles(Out));
    }

    [Fact]
    public async Task A_full_disk_says_so()
    {
        _media.RemuxError = new IOException("There is not enough space on the disk.", unchecked((int)0x80070070));
        var job = Service().Enqueue(Request());
        await Finished(job);
        Assert.Equal("Ổ đĩa đầy. Giải phóng bớt dung lượng rồi bấm Thử lại.", job.Error);
    }

    [Fact]
    public async Task Rtsp_recording_errors_are_vietnamese()
    {
        Nvr.OnDownload = (_, _, _, _, _, _) => throw new NvrDownloadUnsupportedException("refused");
        _media.RecordError = new InvalidOperationException("rtsp 454");
        var job = Service().Enqueue(Request());
        await Finished(job);
        Assert.Equal("Không ghi được video từ đầu ghi. Bấm Thử lại.", job.Error);
        Assert.Empty(Directory.GetFiles(Out));
    }

    [Fact]
    public async Task Existing_files_get_a_number()
    {
        Directory.CreateDirectory(Out);
        File.WriteAllBytes(Path.Combine(Out, "Cổng chính_2026-11-07_10h00-10h10.mp4"), [0]);
        var s = Service();
        var a = s.Enqueue(Request());
        var b = s.Enqueue(Request());
        await Finished(a);
        await Finished(b);
        Assert.Equal(Path.Combine(Out, "Cổng chính_2026-11-07_10h00-10h10 (2).mp4"), a.OutputPath);
        Assert.Equal(Path.Combine(Out, "Cổng chính_2026-11-07_10h00-10h10 (3).mp4"), b.OutputPath);
    }

    [Fact]
    public async Task One_job_at_a_time_per_recorder_and_recorders_in_parallel()
    {
        var meter1 = new ConcurrencyMeter();
        var all = new ConcurrencyMeter();
        var release = new TaskCompletionSource();
        DownloadFunc slow = async (ch, f, t, folder, _, ct) =>
        {
            all.Enter();
            try { await release.Task.WaitAsync(ct); }
            finally { all.Exit(); }
            return [FakeNvrClient.WritePart(folder, ch, f, t)];
        };
        _nvrs["nvr1"].OnDownload = slow;
        _nvrs["nvr1"].Meter = meter1;
        _nvrs["nvr2"].OnDownload = slow;
        var s = Service();
        var jobs = new[] { s.Enqueue(Request()), s.Enqueue(Request()), s.Enqueue(Request()), s.Enqueue(Request(nvr: "nvr2")) };
        await Wait.For(() => all.Max == 2, what: "both recorders downloading");
        await Task.Delay(100);
        Assert.Equal(1, meter1.Max);
        Assert.Equal(2, all.Max);
        Assert.Equal(ExportStatus.Queued, jobs[1].Status);
        release.SetResult();
        foreach (var j in jobs) await Finished(j);
        Assert.All(jobs, j => Assert.Equal(ExportStatus.Done, j.Status));
        Assert.Equal(1, meter1.Max);
        Assert.Equal(3, _nvrs["nvr1"].Downloads);
    }

    [Fact]
    public async Task Progress_follows_bytes_and_stays_under_the_download_share()
    {
        var seen = new List<double>();
        var go = new TaskCompletionSource();
        Nvr.OnDownload = async (ch, f, t, folder, bytes, ct) =>
        {
            bytes!.Report(150_000_000); // 10 min at 4 Mbit/s is 300 MB: half
            await go.Task.WaitAsync(ct);
            bytes.Report(900_000_000);
            return [FakeNvrClient.WritePart(folder, ch, f, t)];
        };
        var s = Service();
        s.ProgressInterval = TimeSpan.Zero;
        var job = s.Enqueue(Request());
        await Wait.For(() => job.BytesDownloaded == 150_000_000);
        Assert.Equal(0.45, job.Progress, 3);
        go.SetResult();
        await Finished(job);
        Assert.Equal(1, job.Progress);
    }

    [Fact]
    public async Task The_estimate_learns_the_measured_rate_of_earlier_downloads()
    {
        var s = Service();
        s.ProgressInterval = TimeSpan.Zero;
        var first = s.Enqueue(Request()); // writes 2044 bytes for 10 minutes
        await Finished(first);

        var go = new TaskCompletionSource();
        Nvr.OnDownload = async (ch, f, t, folder, bytes, ct) =>
        {
            bytes!.Report(1022); // half of what this camera produced last time for the same length
            await go.Task.WaitAsync(ct);
            return [FakeNvrClient.WritePart(folder, ch, f, t)];
        };
        var second = s.Enqueue(Request());
        await Wait.For(() => second.BytesDownloaded == 1022);
        Assert.Equal(0.45, second.Progress, 3);
        go.SetResult();
        await Finished(second);
    }

    [Fact]
    public void Validation_messages_are_vietnamese()
    {
        var s = Service();
        Assert.Equal("Thời điểm kết thúc phải sau thời điểm bắt đầu.",
            Assert.Throws<ArgumentException>(() => s.Enqueue(Request(from: To, to: From))).Message);
        Assert.Equal("Mỗi lần chỉ lưu được tối đa 24 giờ video. Hãy chia thành nhiều đoạn.",
            Assert.Throws<ArgumentException>(() => s.Enqueue(Request(to: From.AddHours(24).AddSeconds(1)))).Message);
        Assert.Equal("Chưa chọn thư mục lưu video.",
            Assert.Throws<ArgumentException>(() => s.Enqueue(Request() with { OutputFolder = " " })).Message);

        var file = _dir.File("a-file");
        File.WriteAllText(file, "x");
        var e = Assert.Throws<ArgumentException>(() => s.Enqueue(Request() with { OutputFolder = Path.Combine(file, "sub") }));
        Assert.StartsWith("Không lưu được video vào thư mục", e.Message);
        Assert.Empty(s.Jobs);

        s.Enqueue(Request(to: From.AddHours(24))); // exactly 24 h is fine
    }

    [Fact]
    public async Task Clear_finished_removes_ended_jobs_and_raises_null()
    {
        var gate = new TaskCompletionSource();
        var calls = 0;
        Nvr.OnDownload = async (ch, f, t, folder, _, ct) =>
        {
            if (Interlocked.Increment(ref calls) == 2) await gate.Task.WaitAsync(ct);
            return [FakeNvrClient.WritePart(folder, ch, f, t)];
        };
        var s = Service();
        var events = new List<ExportJob?>();
        s.JobChanged += j => { lock (events) events.Add(j); };
        var done = s.Enqueue(Request());
        await Finished(done);
        var running = s.Enqueue(Request());
        await Wait.For(() => running.Status == ExportStatus.Downloading);

        s.ClearFinished();
        Assert.Equal([running], s.Jobs);
        await Wait.For(() => { lock (events) return events.Contains(null); });
        gate.SetResult();
        await Finished(running);
    }

    [Fact]
    public async Task Job_changed_is_raised_off_the_calling_thread()
    {
        var s = Service();
        var caller = Environment.CurrentManagedThreadId;
        using var first = new ManualResetEventSlim();
        var handlerThread = 0;
        s.JobChanged += _ => { if (Interlocked.CompareExchange(ref handlerThread, Environment.CurrentManagedThreadId, 0) == 0) first.Set(); };
        var job = s.Enqueue(Request());
        Assert.True(first.Wait(5000)); // blocks the caller: the event must come from another thread
        Assert.NotEqual(caller, handlerThread);
        await Finished(job);
    }

    [Fact]
    public void Leftovers_in_the_temp_folder_are_removed_at_start()
    {
        var old = Path.Combine(Temp, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(old);
        File.WriteAllBytes(Path.Combine(old, "ch1_x_1.ps"), [1]);
        File.WriteAllBytes(Path.Combine(Temp, "stray.ps"), [1]);
        File.WriteAllBytes(Path.Combine(Temp, "keep.txt"), [1]);
        Service();
        Assert.False(Directory.Exists(old));
        Assert.False(File.Exists(Path.Combine(Temp, "stray.ps")));
        Assert.True(File.Exists(Path.Combine(Temp, "keep.txt")));
    }

    [Fact]
    public async Task Each_job_downloads_into_its_own_temp_folder()
    {
        var s = Service();
        var a = s.Enqueue(Request());
        await Finished(a);
        Assert.Equal(Path.Combine(Temp, a.Id.ToString("N")), Assert.Single(Nvr.Folders));
    }

    private sealed class FixedTime(DateTime local) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(local, TimeSpan.FromHours(7)).ToUniversalTime();
        public override TimeZoneInfo LocalTimeZone { get; } = TimeZoneInfo.CreateCustomTimeZone("UTC+7", TimeSpan.FromHours(7), "UTC+7", "UTC+7");
    }
}
