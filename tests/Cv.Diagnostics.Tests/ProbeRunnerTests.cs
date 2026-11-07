using System.Text;
using Cv.Core;
using Cv.Core.Nvr;

namespace Cv.Diagnostics.Tests;

public sealed class ProbeRunnerTests : IDisposable
{
    private const string Password = "Mock@1234#!";
    private static readonly DateTimeOffset Started = new(2026, 11, 7, 19, 45, 10, TimeSpan.FromHours(7));
    private static readonly ProbeTimeouts Fast = new()
    {
        Watch = TimeSpan.FromMilliseconds(30), Poll = TimeSpan.FromMilliseconds(2), VideoOpen = TimeSpan.FromSeconds(2),
    };

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cv-probe-tests-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly FakeNvr _nvr = new();
    private readonly FakeVideoFactory _video = new();
    private readonly FakeMedia _media = new();

    public ProbeRunnerTests()
    {
        Directory.CreateDirectory(_dir);
        Log.Folder = Path.Combine(_dir, "logs");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private Task<ProbeReport> RunAsync(CancellationToken ct = default, List<string>? progress = null, bool diagnostics = true) =>
        new ProbeRunner(new FakeNvrFactory(_nvr, diagnostics), _video, _media) { Time = new FixedTime(Started), Timeouts = Fast }
            .RunAsync(new NvrConfig { Host = FakeNvr.Host, User = "admin" }, Password, _dir,
                progress is null ? null : new InlineProgress(progress), ct);

    private static string[] Lines(ProbeReport r, string tag) =>
        r.Text.Split('\n').Where(l => l.Contains($"[{tag}]")).Select(l => l.TrimEnd('\r')).ToArray();

    [Fact]
    public async Task Healthy_recorder_passes_and_report_is_saved_with_summary_first()
    {
        var steps = new List<string>();
        var r = await RunAsync(progress: steps);

        Assert.True(r.Passed, r.Text);
        Assert.Equal(Path.Combine(_dir, "probe-report-20261107-194510.txt"), r.SavedPath);
        Assert.Equal(r.Text, File.ReadAllText(r.SavedPath!, Encoding.UTF8));
        Assert.True(r.Text.IndexOf("KẾT QUẢ: ĐẠT", StringComparison.Ordinal) < r.Text.IndexOf("CHI TIẾT", StringComparison.Ordinal));
        Assert.Empty(Lines(r, "FAIL"));
        Assert.Contains("DS-7732NI-M4, firmware V4.71.120", r.Text);
        Assert.Contains("17 (16 đang chạy; tắt: D5)", r.Text);
        Assert.Contains("tải qua ISAPI (cách ByTime)", r.Text);
        Assert.Contains("H.264 640x360", r.Text);
        Assert.Contains("H.265 1280x720", r.Text);
        Assert.NotEmpty(steps);

        // Sub stream, main stream, playback; playback is not live; every source disposed.
        Assert.Equal(3, _video.Created.Count);
        Assert.EndsWith("/Streaming/Channels/102", _video.Created[0].Options.Url);
        Assert.EndsWith("/Streaming/Channels/101", _video.Created[1].Options.Url);
        Assert.Contains("/Streaming/tracks/101", _video.Created[2].Options.Url);
        Assert.False(_video.Created[2].Options.IsLive);
        Assert.All(_video.Created, s => Assert.True(s.Disposed));

        // The window is 10 s inside a finished file: exported as the app does, then by each ISAPI method, then over RTSP.
        var download = Assert.Single(_nvr.Calls, c => c.StartsWith("download ", StringComparison.Ordinal));
        var parts = download.Split(' ')[2].Split('-');
        Assert.Equal(TimeSpan.FromSeconds(10), TimeSpan.Parse(parts[1]) - TimeSpan.Parse(parts[0]));
        Assert.Equal(["downloadWith ByTime", "downloadWith BySegmentRange", "downloadWith BySegmentWhole"],
            _nvr.Calls.Where(c => c.StartsWith("downloadWith", StringComparison.Ordinal)));
        Assert.Equal(4, _media.Remuxed.Count);
        Assert.Single(_media.Recorded);
        Assert.Contains("[ByTime: được, BySegmentRange: được, BySegmentWhole: được]", r.Text);
        Assert.True(_nvr.Disposed);
    }

    [Fact]
    public async Task Password_is_never_written_even_when_an_error_contains_it()
    {
        _nvr.Download = (_, _, _, _, _, _) =>
            throw new IOException($"connection reset rtsp://admin:{Uri.EscapeDataString(Password)}@{FakeNvr.Host}/x password was {Password}");

        var r = await RunAsync();

        var file = File.ReadAllText(r.SavedPath!);
        foreach (var text in new[] { r.Text, file })
        {
            Assert.DoesNotContain(Password, text);
            Assert.DoesNotContain(Uri.EscapeDataString(Password), text);
            Assert.DoesNotContain("1234#", text);
        }
        Assert.Contains("rtsp://admin:***@", r.Text);
        Assert.Contains("mật khẩu không ghi vào báo cáo", r.Text);
    }

    [Fact]
    public async Task Wrong_password_stops_at_once_so_the_recorder_does_not_lock()
    {
        _nvr.DeviceInfo = () => throw new NvrAuthException("Sai tài khoản hoặc mật khẩu đầu ghi.");

        var r = await RunAsync();

        Assert.False(r.Passed);
        Assert.Equal(["deviceInfo"], _nvr.Calls);
        Assert.Empty(_video.Created);
        Assert.Empty(_media.Recorded);
        Assert.Contains("Sai tài khoản hoặc mật khẩu đầu ghi.", Lines(r, "FAIL").Single());
        Assert.All(Lines(r, "SKIP"), l => Assert.Contains("khóa tài khoản", l));
        Assert.Contains("CÓ LỖI", r.Text);
    }

    [Fact]
    public async Task Unreachable_recorder_skips_the_remaining_network_steps()
    {
        _nvr.Clock = () => throw new NvrUnreachableException("Không kết nối được đầu ghi.");

        var r = await RunAsync();

        Assert.Equal(["deviceInfo", "time"], _nvr.Calls);
        Assert.Contains(Lines(r, "SKIP"), l => l.Contains("không kết nối được đầu ghi"));
        Assert.Empty(_video.Created);
    }

    [Fact]
    public async Task Isapi_download_unsupported_falls_back_to_rtsp_record()
    {
        _nvr.Download = (_, _, _, _, _, _) => throw new NvrDownloadUnsupportedException("Đầu ghi không cho tải video qua mạng.");

        var r = await RunAsync();

        Assert.False(r.Passed);
        Assert.Contains("chỉ bằng cách ghi lại qua RTSP", r.Text);
        Assert.Contains(Lines(r, "FAIL"), l => l.Contains("Đầu ghi không cho tải video qua mạng."));
        Assert.Equal(3, Lines(r, "FAIL").Count(l => l.Contains("cách By")));
        Assert.Contains(Lines(r, "PASS"), l => l.Contains("Ghi lại"));
        Assert.Single(_media.Recorded);
        Assert.Empty(_media.Remuxed);
    }

    [Fact]
    public async Task Whole_segment_download_is_trimmed_like_the_export_and_still_passes()
    {
        // A recorder that only serves whole files: ByTime and BySegmentRange refused.
        _nvr.Download = (method, from, to, folder, _, _) =>
        {
            if (method is DownloadMethod.ByTime or DownloadMethod.BySegmentRange) throw new NvrDownloadUnsupportedException("refused");
            var path = Path.Combine(folder, "seg.ps");
            File.WriteAllBytes(path, [(byte)'I', (byte)'M', (byte)'K', (byte)'H']);
            return [new DownloadedPart(path, from.AddMinutes(-30), from, to, DownloadMethod.BySegmentWhole)];
        };
        _media.Duration = f => f == "seg.ps" ? TimeSpan.FromHours(1) : TimeSpan.FromSeconds(11);

        var r = await RunAsync();

        Assert.True(r.Passed, r.Text);
        Assert.Equal(2, _media.Remuxed.Count); // the app's export and the forced BySegmentWhole
        Assert.All(_media.Remuxed, parts =>
        {
            var part = Assert.Single(parts);
            Assert.Equal(TimeSpan.FromMinutes(30), part.From);
            Assert.Equal(TimeSpan.FromMinutes(30) + TimeSpan.FromSeconds(10), part.To);
        });
        var fails = Lines(r, "FAIL");
        Assert.Contains(fails, l => l.Contains("cách ByTime") && l.Contains("từ chối"));
        Assert.Contains(fails, l => l.Contains("cách BySegmentRange") && l.Contains("từ chối"));
        Assert.Contains(Lines(r, "PASS"), l => l.Contains("cách BySegmentWhole"));
        Assert.Contains("tải qua ISAPI (cách BySegmentWhole)", r.Text);
    }

    [Fact]
    public async Task Segment_range_answered_with_a_whole_file_is_stopped_early_and_flagged()
    {
        _nvr.Download = (method, from, to, folder, bytes, ct) =>
        {
            if (method == DownloadMethod.BySegmentRange)
            {
                bytes?.Report(100L * 1024 * 1024); // the cap cancels the token
                ct.ThrowIfCancellationRequested();
                throw new InvalidOperationException("the probe should have stopped this download");
            }
            var path = Path.Combine(folder, "p.ps");
            File.WriteAllBytes(path, [(byte)'I', (byte)'M', (byte)'K', (byte)'H']);
            return [new DownloadedPart(path, from, from, to, method ?? DownloadMethod.ByTime)];
        };

        var r = await RunAsync();

        Assert.True(r.Passed, r.Text); // per-method lines are information
        Assert.Contains(Lines(r, "FAIL"), l => l.Contains("cách BySegmentRange") && l.Contains("có vẻ trả cả đoạn"));
        Assert.Contains("BySegmentRange: trả cả đoạn", r.Text);
    }

    [Fact]
    public async Task Without_diagnostics_the_methods_are_inferred_from_the_one_that_worked()
    {
        _nvr.Download = (method, from, to, folder, _, _) =>
        {
            var path = Path.Combine(folder, "p.ps");
            File.WriteAllBytes(path, [(byte)'I', (byte)'M', (byte)'K', (byte)'H']);
            return [new DownloadedPart(path, from, from, to, DownloadMethod.BySegmentRange)];
        };

        var r = await RunAsync(diagnostics: false);

        Assert.True(r.Passed, r.Text);
        Assert.DoesNotContain(_nvr.Calls, c => c.StartsWith("downloadWith", StringComparison.Ordinal));
        Assert.Contains(Lines(r, "FAIL"), l => l.Contains("cách ByTime"));
        Assert.Contains(Lines(r, "PASS"), l => l.Contains("cách BySegmentRange"));
        Assert.Contains(Lines(r, "SKIP"), l => l.Contains("cách BySegmentWhole"));
    }

    [Fact]
    public async Task Export_of_the_wrong_length_fails()
    {
        _media.Duration = f => f == "probe-isapi.mp4" ? TimeSpan.FromMinutes(60) : TimeSpan.FromSeconds(10);

        var r = await RunAsync();

        Assert.False(r.Passed);
        Assert.Contains(Lines(r, "FAIL"), l => l.Contains("SAI ĐỘ DÀI"));
    }

    [Fact]
    public async Task No_recordings_skips_playback_and_export()
    {
        _nvr.Search = _ => [];

        var r = await RunAsync();

        Assert.False(r.Passed);
        Assert.Equal(["search 1", "search 2", "search 3", "search 4"], _nvr.Calls.Where(c => c.StartsWith("search", StringComparison.Ordinal)));
        Assert.DoesNotContain(_nvr.Calls, c => c.StartsWith("download", StringComparison.Ordinal));
        Assert.Contains(Lines(r, "FAIL"), l => l.Contains("D1, D2, D3, D4"));
        Assert.Equal(3, Lines(r, "SKIP").Length);
        Assert.Equal(2, _video.Created.Count); // live only
    }

    [Fact]
    public async Task Video_that_does_not_open_is_reported_with_the_source_error()
    {
        _video.Fail = url => url.EndsWith("/Channels/101", StringComparison.Ordinal) ? "Không mở được hình camera." : null;

        var r = await RunAsync();

        Assert.False(r.Passed);
        Assert.Contains(Lines(r, "FAIL"), l => l.Contains("luồng chính") && l.Contains("Không mở được hình camera."));
        Assert.Contains("luồng chính KHÔNG được", r.Text);
    }

    [Fact]
    public async Task Cancel_saves_the_partial_report_then_throws()
    {
        using var cts = new CancellationTokenSource();
        _nvr.Search = ch => { cts.Cancel(); return []; };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunAsync(cts.Token));

        var file = Assert.Single(Directory.GetFiles(_dir, "probe-report-*.txt"));
        var text = File.ReadAllText(file);
        Assert.Contains("ĐÃ HỦY", text);
        Assert.Contains("[PASS]", text);
    }

    [Fact]
    public async Task Second_report_in_the_same_second_gets_a_suffix()
    {
        var a = await RunAsync();
        var b = await RunAsync();

        Assert.NotEqual(a.SavedPath, b.SavedPath);
        Assert.EndsWith("-2.txt", b.SavedPath);
    }

    [Fact]
    public async Task Clock_far_from_the_pc_is_flagged()
    {
        _nvr.Clock = () => new DeviceClock(FakeNvr.Now.AddMinutes(20), TimeSpan.FromHours(7));

        var r = await RunAsync();

        Assert.Contains("nên bật NTP", r.Text);
    }

    private sealed class InlineProgress(List<string> sink) : IProgress<string>
    {
        public void Report(string value) { lock (sink) sink.Add(value); }
    }
}
