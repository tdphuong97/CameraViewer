using System.Diagnostics;
using Cv.Core.Video;
using Cv.Media.Tests.Support;

namespace Cv.Media.Tests;

/// <summary>Network failure paths against an in-process fake RTSP server (no MediaMTX needed).</summary>
public class RtspErrorTests : IDisposable
{
    private readonly FFmpegVideoSourceFactory _factory = new();
    private readonly List<IDisposable> _owned = [];

    private IVideoSource Live(string url, bool reconnect = true, double openTimeout = 10)
    {
        var s = _factory.Create(new VideoSourceOptions { Url = url, IsLive = true, Reconnect = reconnect, OpenTimeout = TimeSpan.FromSeconds(openTimeout) });
        _owned.Add(s);
        return s;
    }

    private FakeRtspServer Server(FakeRtspServer.Mode mode)
    {
        var srv = new FakeRtspServer(mode);
        _owned.Add(srv);
        return srv;
    }

    public void Dispose()
    {
        foreach (var d in _owned) d.Dispose();
        _factory.Dispose();
    }

    [NativeFact]
    public async Task Wrong_password_fails_without_retrying()
    {
        var srv = Server(FakeRtspServer.Mode.Unauthorized);
        var s = Live(srv.Url("admin:wrong@"));
        s.Start();
        Assert.True(await Wait.ForState(s, VideoState.Failed), $"state {s.State}");
        Assert.Equal("Sai tài khoản hoặc mật khẩu đầu ghi", s.Error);
        int requests = srv.Requests;
        await Task.Delay(2500);
        Assert.Equal(requests, srv.Requests); // no retry loop that could lock the recorder account
    }

    [NativeFact]
    public async Task Missing_stream_fails_with_not_found()
    {
        var srv = Server(FakeRtspServer.Mode.NotFound);
        var s = Live(srv.Url("admin:x@"));
        s.Start();
        Assert.True(await Wait.ForState(s, VideoState.Failed));
        Assert.Equal("Không tìm thấy video của camera này", s.Error);
    }

    [NativeFact]
    public async Task Percent_encoded_password_is_decoded_for_basic_auth()
    {
        var srv = Server(FakeRtspServer.Mode.BasicAuth);
        var s = Live(srv.Url("admin:Mock%401234@"));
        s.Start();
        Assert.True(await Wait.ForState(s, VideoState.Failed));
        Assert.Equal("admin:Mock@1234", srv.BasicCredentials);
    }

    [NativeFact]
    public async Task Percent_encoded_password_is_decoded_for_digest_auth()
    {
        var srv = Server(FakeRtspServer.Mode.DigestAuth);
        var s = Live(srv.Url("admin:Mock%401234@"));
        s.Start();
        Assert.True(await Wait.ForState(s, VideoState.Failed));
        Assert.True(srv.DigestValid, "digest computed with the decoded password");
        Assert.Equal("Không tìm thấy video của camera này", s.Error); // the fake answers 404 after a good login
    }

    [NativeFact]
    public async Task Hung_server_times_out_with_vietnamese_error()
    {
        var srv = Server(FakeRtspServer.Mode.Hang);
        var s = Live(srv.Url("admin:x@"), reconnect: false, openTimeout: 1.5);
        var sw = Stopwatch.StartNew();
        s.Start();
        Assert.True(await Wait.ForState(s, VideoState.Failed, 8));
        Assert.InRange(sw.Elapsed.TotalSeconds, 1.2, 5);
        Assert.Equal("Không kết nối được đầu ghi", s.Error);
    }

    [NativeFact]
    public async Task Refused_connection_keeps_retrying_with_backoff()
    {
        // Nothing listens there. FFmpeg on Windows does not see the refusal (WSAPoll), so this ends by the open deadline.
        int port = TestEnv.FreeTcpPort();
        var s = Live($"rtsp://admin:x@127.0.0.1:{port}/Streaming/Channels/102", openTimeout: 2);
        var states = new List<VideoState>();
        s.StateChanged += src => { lock (states) states.Add(src.State); };
        s.Start();
        Assert.True(await Wait.ForState(s, VideoState.Reconnecting, 8));
        Assert.Equal("Không kết nối được đầu ghi, đang thử lại…", s.Error);
        await Task.Delay(6000); // further attempts (2 s each + 1, 2 s backoff): still trying, never Failed
        Assert.Equal(VideoState.Reconnecting, s.State);
        lock (states) Assert.DoesNotContain(VideoState.Failed, states);
    }

    [NativeFact]
    public async Task Dispose_is_fast_while_the_server_hangs()
    {
        var srv = Server(FakeRtspServer.Mode.Hang);
        var s = Live(srv.Url("admin:x@"), openTimeout: 30);
        s.Start();
        Assert.True(await Wait.For(() => srv.Requests > 0, TimeSpan.FromSeconds(5)));
        await Task.Delay(300); // inside avformat_open_input, waiting for an answer
        var sw = Stopwatch.StartNew();
        s.Dispose();
        Assert.True(sw.ElapsedMilliseconds < 1000, $"{sw.ElapsedMilliseconds} ms");
    }
}
