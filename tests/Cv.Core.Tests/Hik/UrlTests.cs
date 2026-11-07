using Cv.Core.Hik;
using Cv.Core.Nvr;
using Cv.Core.Tests.Fakes;

namespace Cv.Core.Tests.Hik;

public class UrlTests
{
    private static readonly DateTime From = new(2026, 11, 7, 14, 30, 0);
    private static readonly DateTime To = new(2026, 11, 7, 15, 0, 0);

    [Fact]
    public void Live_urls_percent_encode_user_and_password()
    {
        using var c = new FakeRecorder().Client(password: "Ab@1#c!");
        Assert.Equal("rtsp://admin:Ab%401%23c%21@192.168.1.64:554/Streaming/Channels/101", c.LiveUrl(1, StreamKind.Main));
        Assert.Equal("rtsp://admin:Ab%401%23c%21@192.168.1.64:554/Streaming/Channels/1702", c.LiveUrl(17, StreamKind.Sub));
    }

    [Fact]
    public void Odd_user_names_and_ports_are_handled()
    {
        var cfg = FakeRecorder.Config(rtsp: 8554) with { User = "op:er@x", Host = " 10.0.0.5 " };
        using var c = new HikNvrClient(cfg, "p/w?%", new FakeIsapiHandler());
        Assert.Equal("rtsp://op%3Aer%40x:p%2Fw%3F%25@10.0.0.5:8554/Streaming/Channels/302", c.LiveUrl(3, StreamKind.Sub));
    }

    [Fact]
    public void Playback_url_before_resolution_is_local_with_z()
    {
        using var c = new FakeRecorder().Client(TimeMode.Auto, "Ab@1#c!");
        Assert.Equal("rtsp://admin:Ab%401%23c%21@192.168.1.64:554/Streaming/tracks/101?starttime=20261107T143000Z&endtime=20261107T150000Z",
            c.PlaybackUrl(1, From, To));
        Assert.Equal("rtsp://admin:Ab%401%23c%21@192.168.1.64:554/Streaming/tracks/1702?starttime=20261107T143000Z",
            c.PlaybackUrl(17, From, null, StreamKind.Sub));
    }

    [Fact]
    public async Task Playback_url_in_utc_mode_subtracts_the_device_offset()
    {
        using var c = new FakeRecorder().Client(TimeMode.Utc);
        Assert.Equal(TimeMode.Utc, await c.ResolveTimeModeAsync(default));
        Assert.Equal("rtsp://admin:secret@192.168.1.64:554/Streaming/tracks/101?starttime=20261107T073000Z&endtime=20261107T080000Z",
            c.PlaybackUrl(1, From, To));
    }
}
