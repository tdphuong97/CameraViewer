using Cv.Core.Video;
using Cv.Media.Tests.Support;
using Xunit.Abstractions;

namespace Cv.Media.Tests;

/// <summary>Real RTSP through MediaMTX (skipped when the dev tools are not installed).</summary>
public class LiveRtspTests(ITestOutputHelper output) : IDisposable
{
    private readonly FFmpegVideoSourceFactory _factory = new();
    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        foreach (var d in Enumerable.Reverse(_owned)) d.Dispose();
        _factory.Dispose();
    }

    private T Own<T>(T d) where T : IDisposable { _owned.Add(d); return d; }

    [MediaMtxFact]
    public async Task Live_with_at_sign_password_decodes_and_reconnects_after_publisher_restart()
    {
        foreach (var auth in new[] { "basic", "digest" })
        {
            var mtx = Own(new MediaMtx(auth));
            mtx.Publish("cam", TestClips.H265Mp4);
            await Task.Delay(1500);
            var s = Own(_factory.Create(new VideoSourceOptions { Url = mtx.ReaderUrl("cam"), IsLive = true, TargetWidth = 160, TargetHeight = 90 }));
            s.Start();
            var f = await Wait.NextFrame(s, 0, 15);
            output.WriteLine($"{auth}: first frame {f.W}x{f.H}, state {s.State}, {s.Stats}");
            Assert.Equal((160, 90), (f.W, f.H));
            Assert.Equal("H.265", s.Stats.Codec);

            mtx.StopPublisher("cam");
            Assert.True(await Wait.ForState(s, VideoState.Reconnecting, 15), $"state {s.State} / {mtx.ServerLog}");
            output.WriteLine($"{auth}: after publisher stop: {s.State} '{s.Error}'");
            await Task.Delay(2500); // a couple of failed retries (404: no publisher) must not end in Failed
            Assert.Equal(VideoState.Reconnecting, s.State);

            mtx.Publish("cam", TestClips.H265Mp4);
            Assert.True(await Wait.ForState(s, VideoState.Playing, 25), $"state {s.State} '{s.Error}'");
            long seq = 0;
            await Wait.NextFrame(s, seq, 10);
            output.WriteLine($"{auth}: playing again");
        }
    }

    [MediaMtxFact]
    public async Task Stalled_stream_is_detected_after_about_10_seconds()
    {
        var mtx = Own(new MediaMtx());
        mtx.Publish("cam", TestClips.H264Mp4);
        await Task.Delay(1500);
        var s = Own(_factory.Create(new VideoSourceOptions { Url = mtx.ReaderUrl("cam"), IsLive = true }));
        s.Start();
        await Wait.NextFrame(s, 0, 15);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        mtx.FreezePublisher("cam", true); // session stays up, no packets
        Assert.True(await Wait.ForState(s, VideoState.Reconnecting, 20), $"state {s.State}");
        output.WriteLine($"stall detected after {sw.Elapsed.TotalSeconds:F1} s: '{s.Error}'");
        Assert.InRange(sw.Elapsed.TotalSeconds, 8, 14);
        mtx.FreezePublisher("cam", false);
        Assert.True(await Wait.ForState(s, VideoState.Playing, 25), $"state {s.State} '{s.Error}'");
    }

    [MediaMtxFact]
    public async Task Rtsp_pause_and_resume_for_recorded_playback()
    {
        var mtx = Own(new MediaMtx());
        mtx.Publish("cam", TestClips.H264Mp4);
        await Task.Delay(1500);
        var s = Own(_factory.Create(new VideoSourceOptions { Url = mtx.ReaderUrl("cam"), IsLive = false }));
        s.Start();
        var f = await Wait.NextFrame(s, 0, 15);
        await Task.Delay(1000);
        s.Pause();
        Assert.True(await Wait.ForState(s, VideoState.Paused));
        var p = s.Position;
        await Task.Delay(3000);
        Assert.Equal(p, s.Position);
        s.Resume();
        Assert.True(await Wait.ForState(s, VideoState.Playing));
        long seq = 0;
        Wait.Grab(s, ref seq);
        await Wait.NextFrame(s, seq, 5);
        await Task.Delay(1000);
        output.WriteLine($"position before pause {p.TotalSeconds:F2} s, after resume {s.Position.TotalSeconds:F2} s, state {s.State}");
        Assert.InRange((s.Position - p).TotalSeconds, 0.5, 2.0); // continues from the paused picture
        Assert.Equal(VideoState.Playing, s.State);
    }

    [MediaMtxFact]
    public async Task Wrong_password_against_real_server_fails()
    {
        var mtx = Own(new MediaMtx("digest"));
        mtx.Publish("cam", TestClips.H264Mp4);
        await Task.Delay(1000);
        var url = mtx.ReaderUrl("cam").Replace("Mock%401234", "Mock%401235");
        var s = Own(_factory.Create(new VideoSourceOptions { Url = url, IsLive = true }));
        s.Start();
        Assert.True(await Wait.ForState(s, VideoState.Failed, 15));
        Assert.Equal("Sai tài khoản hoặc mật khẩu đầu ghi", s.Error);
    }

    [MediaMtxFact]
    public async Task Record_pulls_rtsp_to_mp4()
    {
        var mtx = Own(new MediaMtx());
        mtx.Publish("cam", TestClips.H264Mp4);
        await Task.Delay(1500);
        var dir = TestEnv.NewTempDir();
        var outFile = Path.Combine(dir, "rec.mp4");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await new FFmpegMediaTools().RecordAsync(mtx.ReaderUrl("cam"), TimeSpan.FromSeconds(3), outFile, null, CancellationToken.None);
        var p = MediaProbe.Probe(outFile);
        output.WriteLine($"record took {sw.Elapsed.TotalSeconds:F1} s, {p.Video.Packets} frames, span {p.VideoSpan:F2} s");
        Assert.True(p.Video.FirstIsKey);
        Assert.InRange(p.VideoSpan, 2.7, 3.0);
        Directory.Delete(dir, true);
    }
}
