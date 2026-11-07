using System.Diagnostics;
using Cv.Core.Video;
using Cv.Media.Tests.Support;

namespace Cv.Media.Tests;

public class VideoSourceTests : IDisposable
{
    private readonly FFmpegVideoSourceFactory _factory = new();
    private readonly List<IVideoSource> _sources = [];

    private IVideoSource Open(string clip, bool live = false, int tw = 0, int th = 0)
    {
        var s = _factory.Create(new VideoSourceOptions { Url = TestClips.Resolve(clip), IsLive = live, TargetWidth = tw, TargetHeight = th });
        _sources.Add(s);
        return s;
    }

    public void Dispose()
    {
        foreach (var s in _sources) s.Dispose();
        _factory.Dispose();
    }

    [NativeTheory]
    [InlineData("h264.mp4", "H.264")]
    [InlineData("h265.mp4", "H.265")]
    [InlineData("h264.ps", "H.264")]
    [InlineData("h265.ps", "H.265")]
    [InlineData("h264_g711.ps", "H.264")]
    [InlineData("h264_aac.ts", "H.264")]
    public async Task Decodes_file_and_frames_arrive(string clip, string codec)
    {
        var s = Open(clip);
        var states = new List<VideoState>();
        s.StateChanged += src => { lock (states) states.Add(src.State); };
        s.Start();
        var f = await Wait.NextFrame(s);
        Assert.Equal((TestClips.Width, TestClips.Height), (f.W, f.H));
        Assert.Equal(VideoState.Playing, s.State);
        Assert.Null(s.Error);
        Assert.Equal(codec, s.Stats.Codec);
        Assert.Equal((TestClips.Width, TestClips.Height), (s.Stats.Width, s.Stats.Height));
        Assert.False(s.Stats.HardwareDecoding); // narrow streams always decode in software
        lock (states) Assert.Equal([VideoState.Connecting, VideoState.Playing], states.Take(2));
        // The picture is not black: testsrc2 is colourful.
        Assert.Contains(f.Pixels, b => b > 64);
    }

    [NativeFact]
    public async Task Scales_into_the_box_and_follows_SetTargetSize()
    {
        var s = Open("h264.mp4", tw: 160, th: 160);
        s.Start();
        var f = await Wait.NextFrame(s);
        Assert.Equal((160, 90), (f.W, f.H));

        s.SetTargetSize(1920, 1080); // never upscaled
        long seq = 0;
        Assert.True(await Wait.For(() => Wait.Grab(s, ref seq) is { W: 320, H: 180 }, TimeSpan.FromSeconds(3)));

        s.SetTargetSize(101, 101); // odd box: even size inside it
        Assert.True(await Wait.For(() => Wait.Grab(s, ref seq) is { W: 100, H: 56 }, TimeSpan.FromSeconds(3)));
    }

    [NativeFact]
    public async Task TryCopyLatest_only_reports_newer_frames()
    {
        var s = Open("h264.mp4");
        s.Start();
        await Wait.NextFrame(s);
        s.Pause();
        Assert.True(await Wait.ForState(s, VideoState.Paused));
        await Task.Delay(150);
        long seq = 0;
        Assert.NotNull(Wait.Grab(s, ref seq));
        Assert.True(seq > 0);
        int calls = 0;
        long before = seq;
        Assert.False(s.TryCopyLatest(ref seq, (_, _, _, _) => calls++));
        Assert.Equal(0, calls);
        Assert.Equal(before, seq);
        s.Resume();
        var next = await Wait.NextFrame(s, seq, 3);
        Assert.Equal(320, next.W);
    }

    [NativeFact]
    public async Task Playback_pauses_resumes_and_ends()
    {
        var s = Open("h265.ps");
        var sw = Stopwatch.StartNew();
        s.Start();
        await Wait.NextFrame(s);
        await Task.Delay(800);
        s.Pause();
        Assert.True(await Wait.ForState(s, VideoState.Paused));
        var p1 = s.Position;
        Assert.InRange(p1.TotalSeconds, 0.5, 2.0);
        await Task.Delay(1000);
        Assert.Equal(p1, s.Position); // clock stopped
        s.Resume();
        Assert.True(await Wait.ForState(s, VideoState.Playing));
        await Task.Delay(500);
        Assert.True(s.Position > p1);
        Assert.True(await Wait.ForState(s, VideoState.Ended, 10), $"state {s.State}");
        sw.Stop();
        // Paced at real speed: ~4.9 s of media + 1 s paused.
        Assert.InRange(sw.Elapsed.TotalSeconds, 5.3, 9.0);
        Assert.InRange(s.Position.TotalSeconds, TestClips.Span.TotalSeconds - 0.2, TestClips.Span.TotalSeconds + 0.1);
        Assert.Null(s.Error);
        Assert.InRange(s.Stats.Fps, 12, 18);
    }

    [NativeFact]
    public async Task Capture_returns_full_resolution_even_when_paused_or_ended()
    {
        var s = Open("h264.ps", tw: 80, th: 80);
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.CaptureAsync(CancellationToken.None)); // nothing decoded yet
        s.Start();
        var small = await Wait.NextFrame(s);
        Assert.Equal((80, 44), (small.W, small.H));

        var snap = await s.CaptureAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal((320, 180, 320 * 4), (snap.Width, snap.Height, snap.Stride));
        Assert.Equal(320 * 180 * 4, snap.Bgra.Length);
        Assert.Contains(snap.Bgra, b => b > 64);

        s.Pause();
        Assert.True(await Wait.ForState(s, VideoState.Paused));
        snap = await s.CaptureAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(320, snap.Width);

        // Resizing while paused re-scales the frozen picture.
        s.SetTargetSize(160, 160);
        long seq = 0;
        Assert.True(await Wait.For(() => Wait.Grab(s, ref seq) is { W: 160, H: 90 }, TimeSpan.FromSeconds(3)));

        s.Resume();
        Assert.True(await Wait.ForState(s, VideoState.Ended, 10));
        snap = await s.CaptureAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(180, snap.Height);
    }

    [NativeFact]
    public async Task Dispose_returns_quickly_while_playing()
    {
        var s = Open("h264.mp4");
        s.Start();
        await Wait.NextFrame(s);
        var sw = Stopwatch.StartNew();
        s.Dispose();
        Assert.True(sw.ElapsedMilliseconds < 1000, $"{sw.ElapsedMilliseconds} ms");
        long seq = 0;
        await Task.Delay(200);
        Assert.False(s.TryCopyLatest(ref seq, (_, _, _, _) => { }));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => s.CaptureAsync(CancellationToken.None));
    }

    [NativeFact]
    public async Task Live_file_loops_at_real_speed()
    {
        var s = Open("h264.mp4", live: true);
        s.Start();
        await Wait.NextFrame(s);
        await Task.Delay(TimeSpan.FromSeconds(6)); // past the 5 s clip
        Assert.Equal(VideoState.Playing, s.State);
        long seq = 0;
        Assert.NotNull(await Task.Run(async () => { await Task.Delay(300); return Wait.Grab(s, ref seq); }));
        Assert.InRange(s.Position.TotalSeconds, 5.5, 8);
        s.Pause(); // ignored for live
        await Task.Delay(300);
        Assert.Equal(VideoState.Playing, s.State);
    }

    private static readonly Lazy<string> Wide1080 = new(() =>
    {
        var file = Path.Combine(TestEnv.NewTempDir(), "wide.mp4");
        TestEnv.RunFFmpeg("-f lavfi -i testsrc2=size=1920x1080:rate=15:duration=3 -pix_fmt yuv420p -c:v libx265 -preset ultrafast " +
            "-x265-params keyint=15:bframes=0:log-level=error -tag:v hvc1 \"" + file + "\"");
        return file;
    });

    [GplFact]
    public async Task Wide_stream_uses_the_shared_d3d11va_device_unless_off()
    {
        var hw = _factory.Create(new VideoSourceOptions { Url = Wide1080.Value, IsLive = false, TargetWidth = 640, TargetHeight = 360 });
        var sw = _factory.Create(new VideoSourceOptions { Url = Wide1080.Value, IsLive = false, HwDecode = HwDecodeMode.Off });
        _sources.Add(hw);
        _sources.Add(sw);
        hw.Start();
        sw.Start();
        var f = await Wait.NextFrame(hw);
        Assert.Equal((640, 360), (f.W, f.H));
        var g = await Wait.NextFrame(sw);
        Assert.Equal((1920, 1080), (g.W, g.H));
        Assert.False(sw.Stats.HardwareDecoding);
        // On a machine without D3D11 video decoding the device is not created and Auto falls back to software.
        Assert.Equal(_factory.HardwareDeviceCreated, hw.Stats.HardwareDecoding);
        var snap = await hw.CaptureAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal((1920, 1080), (snap.Width, snap.Height));
        Assert.True(await Wait.ForState(hw, VideoState.Ended, 10));
    }

    [NativeFact]
    public void Factory_requires_ffmpeg_and_validates_folder()
    {
        Assert.True(FFmpegRuntime.TryInitialize(out var err));
        Assert.Null(err);
        Assert.Equal("9.0", FFmpegRuntime.Version);
        Assert.False(FFmpegRuntime.ValidateFolder(Path.Combine(Path.GetTempPath(), "no-ffmpeg-here"), out var missing));
        Assert.StartsWith("Thiếu thư viện FFmpeg", missing);
    }
}
