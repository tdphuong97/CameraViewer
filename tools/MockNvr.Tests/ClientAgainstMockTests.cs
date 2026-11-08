using Cv.Core;
using Cv.Core.Hik;
using Cv.Core.Nvr;
using Cv.Core.Video;
using Cv.Diagnostics;
using Xunit.Abstractions;

namespace MockNvr.Tests;

/// <summary>The app's real ISAPI client (Cv.Core) and the probe against the mock over loopback.</summary>
public sealed class ClientAgainstMockTests(ITestOutputHelper output) : IDisposable
{
    private const string Password = "Mock@1234";
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "mocknvr-client-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { if (Directory.Exists(_temp)) Directory.Delete(_temp, true); } catch (IOException) { }
    }

    private static INvrClient Client(RunningMock m, string password = Password, TimeMode mode = TimeMode.Auto) =>
        new HikNvrClientFactory().Create(new NvrConfig { Host = "127.0.0.1", HttpPort = m.Port, User = "admin", TimeMode = mode }, password);

    private static RecordingStore Store(RunningMock m) => new(new MockClock(m.Options, TimeProvider.System));

    private static DateTime DeviceNow(RunningMock m) => new MockClock(m.Options, TimeProvider.System).LocalNow;

    /// <summary>A long finished file of channel 1 from yesterday, and a 10 s window 10 minutes into it.</summary>
    private static (Segment Seg, DateTime From, DateTime To) Window(RunningMock m)
    {
        var day = DeviceNow(m).Date.AddDays(-1);
        var seg = Store(m).Find(1, day, day.AddDays(1)).First(s => s.Duration > TimeSpan.FromMinutes(40));
        var from = seg.Start.AddMinutes(10);
        return (seg, from, from.AddSeconds(10));
    }

    [Fact]
    public async Task Device_clock_and_channels()
    {
        await using var m = await RunningMock.StartAsync();
        using var c = Client(m);

        var info = await c.GetDeviceInfoAsync(default);
        Assert.Equal("DS-7732NI-M4", info.Model);
        Assert.StartsWith("V4.71.120", info.Firmware);

        var clock = await c.GetClockAsync(default);
        Assert.Equal(TimeSpan.FromHours(7), clock.UtcOffset);
        Assert.InRange((clock.LocalNow - DeviceNow(m)).Duration(), TimeSpan.Zero, TimeSpan.FromSeconds(5));

        var channels = await c.GetChannelsAsync(default);
        Assert.Equal(17, channels.Count);
        Assert.Equal([5], channels.Where(x => !x.Online).Select(x => x.No));
        Assert.Equal("Cổng chính", channels[0].Name);
        Assert.Equal("DS-2CD1043G2-LIU", channels[0].CameraModel);
    }

    [Theory]
    [InlineData("localz")]
    [InlineData("utc")]
    [InlineData("offset")]
    public async Task Search_pages_and_every_time_mode_gives_the_same_device_times(string mode)
    {
        await using var m = await RunningMock.StartAsync(o => o.TimeMode = MockOptions.Parse(["--time-mode", mode], out _)!.TimeMode);
        using var c = Client(m);
        var now = DeviceNow(m);

        var resolved = await c.ResolveTimeModeAsync(default);
        var segs = await c.SearchAsync(1, now.Date.AddDays(-2), now, default);

        output.WriteLine($"{mode}: resolved {resolved}, {segs.Count} segments");
        if (mode == "localz") Assert.Equal(TimeMode.LocalWithZ, resolved);
        if (mode is "utc" or "offset") Assert.Equal(TimeMode.Utc, resolved);
        var expected = Store(m).Find(1, now.Date.AddDays(-2), now);
        Assert.True(expected.Count > 50, "more than one page");
        Assert.Equal(expected.Select(s => (s.Start, s.End)), segs.Select(s => (s.Start, s.End)));
        Assert.All(segs, s => Assert.Contains("name=", s.PlaybackUri));
        Assert.Contains(m.LogLines, l => l.Contains("pos 50"));
    }

    [Theory]
    [InlineData(VideoCodec.H265, (byte)0x24)]
    [InlineData(VideoCodec.H264, (byte)0x1B)]
    public async Task Honor_mode_downloads_exactly_the_range_by_time(VideoCodec codec, byte streamType)
    {
        await using var m = await RunningMock.StartAsync(o => o.DownloadCodec = codec);
        using var c = Client(m);
        var (_, from, to) = Window(m);

        var parts = await c.DownloadAsync(1, from, to, _temp, null, default);

        var part = Assert.Single(parts);
        Assert.Equal(DownloadMethod.ByTime, part.Method);
        var ps = PsInfo.Read(File.ReadAllBytes(part.FilePath));
        Assert.True(ps.Imkh);
        Assert.Equal(streamType, ps.VideoStreamType);
        Assert.Equal(to.AddSeconds(5), part.CoveredTo); // the end is asked 5 s later (HikNvrClient.TimeEndPad)
        Assert.Equal(225, ps.VideoFrames);
        Assert.True(ps.AudioPes > 0);
        Assert.Equal(DownloadMethod.ByTime, ((INvrDiagnostics)c).WorkingMethod);
    }

    [Fact]
    public async Task Whole_mode_refuses_time_uris_and_sends_whole_files_for_segment_uris()
    {
        await using var m = await RunningMock.StartAsync(o => { o.Download = DownloadMode.Whole; o.Audio = false; });
        using var c = Client(m);
        var d = (INvrDiagnostics)c;
        var (seg, from, to) = Window(m);

        await Assert.ThrowsAsync<NvrDownloadUnsupportedException>(() => d.DownloadWithAsync(DownloadMethod.ByTime, 1, from, to, _temp, null, default));

        var whole = Assert.Single(await d.DownloadWithAsync(DownloadMethod.BySegmentWhole, 1, from, to, _temp, null, default));
        Assert.Equal(seg.Start, whole.NominalStart);
        var ps = PsInfo.Read(File.ReadAllBytes(whole.FilePath));
        Assert.InRange(ps.Duration, seg.Duration - TimeSpan.FromSeconds(1), seg.Duration);

        // Narrowed segment URIs get the whole file too (the firmware ignores starttime/endtime).
        var range = Assert.Single(await d.DownloadWithAsync(DownloadMethod.BySegmentRange, 1, from, to, _temp, null, default));
        Assert.InRange(PsInfo.Read(File.ReadAllBytes(range.FilePath)).Duration, seg.Duration - TimeSpan.FromSeconds(1), seg.Duration);
        Assert.Contains(m.LogLines, l => l.Contains("refused (whole mode"));
    }

    [Fact]
    public async Task Framed_mode_by_time_keeps_the_stream_and_drops_the_cut_frame()
    {
        // DS-7732NI-M4 V5.04.081: a 24-byte block before IMKH, the body stops inside the last frame.
        await using var m = await RunningMock.StartAsync(o => { o.Download = DownloadMode.Framed; o.Audio = false; });
        using var c = Client(m);
        var (seg, from, to) = Window(m);

        var part = Assert.Single(await c.DownloadAsync(1, from, to, _temp, null, default));

        Assert.Equal(DownloadMethod.ByTime, part.Method);
        var ps = PsInfo.Read(File.ReadAllBytes(part.FilePath));
        Assert.True(ps.Imkh); // the block before IMKH is gone
        var asked = (int)Math.Round((part.CoveredTo - part.CoveredFrom).TotalSeconds * MockDevice.Fps);
        Assert.Equal(asked - 1, ps.VideoFrames); // the unfinished last frame is gone
        Assert.Contains(m.LogLines, l => l.Contains("[framed"));

        // Segment URIs get the whole file, like Whole mode.
        var range = Assert.Single(await ((INvrDiagnostics)c).DownloadWithAsync(DownloadMethod.BySegmentRange, 1, from, to, _temp, null, default));
        Assert.InRange(PsInfo.Read(File.ReadAllBytes(range.FilePath)).Duration, seg.Duration - TimeSpan.FromSeconds(1), seg.Duration);
    }

    [Fact]
    public async Task Xml_error_mode_makes_every_isapi_method_fail()
    {
        await using var m = await RunningMock.StartAsync(o => o.Download = DownloadMode.XmlError);
        using var c = Client(m);
        var (_, from, to) = Window(m);

        await Assert.ThrowsAsync<NvrDownloadUnsupportedException>(() => c.DownloadAsync(1, from, to, _temp, null, default));
        Assert.Empty(Directory.Exists(_temp) ? Directory.GetFiles(_temp) : []);
        Assert.Contains(m.LogLines, l => l.Contains("-> 200") && l.Contains("xml-error"));
    }

    [Fact]
    public async Task Five_wrong_passwords_lock_the_ip_and_the_client_says_for_how_long()
    {
        await using var m = await RunningMock.StartAsync(o => o.LockDuration = TimeSpan.FromMinutes(3));
        using (var bad = Client(m, "wrong"))
            for (var i = 0; i < 5; i++)
                await Assert.ThrowsAsync<NvrAuthException>(() => bad.GetDeviceInfoAsync(default));

        using var good = Client(m);
        var e = await Assert.ThrowsAsync<NvrAuthException>(() => good.GetDeviceInfoAsync(default));
        output.WriteLine(e.Message);
        Assert.Contains("3", e.Message);
        Assert.Contains("phút", e.Message);
        Assert.Contains(m.LogLines, l => l.Contains("LOCKED"));
        // Exactly one failed login per wrong call: the client never walks the recorder to its lock twice as fast.
        Assert.Equal(4, m.LogLines.Count(l => l.Contains("WRONG PASSWORD")));
    }

    [Fact]
    public async Task Expired_nonces_are_not_failed_logins()
    {
        await using var m = await RunningMock.StartAsync(o => o.NonceLifetime = TimeSpan.FromMilliseconds(300));
        using var c = Client(m);
        for (var i = 0; i < 6; i++)
        {
            await c.GetDeviceInfoAsync(default);
            await Task.Delay(500);
        }
        Assert.Contains(m.LogLines, l => l.Contains("[stale nonce]"));
        Assert.DoesNotContain(m.LogLines, l => l.Contains("WRONG PASSWORD") || l.Contains("LOCKED"));
    }

    [Theory]
    [InlineData(DownloadMode.Honor)]
    [InlineData(DownloadMode.Whole)]
    [InlineData(DownloadMode.XmlError)]
    [InlineData(DownloadMode.Framed)]
    public async Task Probe_runs_end_to_end_against_the_mock(DownloadMode mode)
    {
        await using var m = await RunningMock.StartAsync(o => { o.Download = mode; o.Audio = false; });
        var media = new PsMedia();
        var runner = new ProbeRunner(new HikNvrClientFactory(), new FramesFactory(), media)
        {
            Timeouts = new ProbeTimeouts { Watch = TimeSpan.FromMilliseconds(30), Poll = TimeSpan.FromMilliseconds(2), MethodByteCap = 2L * 1024 * 1024 },
        };

        var report = await runner.RunAsync(new NvrConfig { Host = "127.0.0.1", HttpPort = m.Port, User = "admin" }, Password, _temp, null, default);

        output.WriteLine(report.Text);
        Assert.DoesNotContain(Password, report.Text);
        Assert.NotNull(report.SavedPath);
        Assert.Contains("DS-7732NI-M4", report.Text);
        Assert.Contains("17 (16 đang chạy; tắt: D5)", report.Text);
        switch (mode)
        {
            case DownloadMode.Honor:
                Assert.True(report.Passed, report.Text);
                Assert.Contains("[ByTime: được, BySegmentRange: được, BySegmentWhole: được (cả đoạn)]", report.Text);
                break;
            case DownloadMode.Whole:
                Assert.Contains("ByTime: từ chối", report.Text);
                Assert.Contains("BySegmentRange: trả cả đoạn", report.Text);
                Assert.Contains("BySegmentWhole: được (cả đoạn)", report.Text);
                break;
            case DownloadMode.XmlError:
                Assert.False(report.Passed);
                Assert.Contains("chỉ bằng cách ghi lại qua RTSP", report.Text);
                break;
            case DownloadMode.Framed:
                // The real site recorder of 2026-11-08: by time works once the block before IMKH is skipped.
                Assert.True(report.Passed, report.Text);
                Assert.Contains("[ByTime: được, BySegmentRange: trả cả đoạn, BySegmentWhole: được (cả đoạn)]", report.Text);
                break;
        }
    }

    /// <summary>Video sources that play at once (RTSP is MediaMTX's job, not tested here).</summary>
    private sealed class FramesFactory : IVideoSourceFactory
    {
        public IVideoSource Create(VideoSourceOptions options) => new Frames();

        private sealed class Frames : IVideoSource
        {
            private long _n;
            public VideoState State { get; private set; }
            public string? Error => null;
            public event Action<IVideoSource>? StateChanged;
            public VideoStats Stats => new("H.265", 1280, 720, 15, 1000, false);
            public TimeSpan Position => TimeSpan.Zero;
            public void Start() { State = VideoState.Playing; StateChanged?.Invoke(this); }
            public void SetTargetSize(int width, int height) { }
            public void Pause() { }
            public void Resume() { }
            public bool TryCopyLatest(ref long lastSequence, FrameCopier copier) { lastSequence = ++_n; return true; }
            public Task<FrameSnapshot> CaptureAsync(CancellationToken ct) => throw new NotSupportedException();
            public void Dispose() { }
        }
    }
}
