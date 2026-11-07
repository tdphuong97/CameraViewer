using System.Net.Http;
using System.Xml.Linq;
using Cv.Core.Nvr;
using Cv.Core.Tests.Fakes;

namespace Cv.Core.Tests.Hik;

/// <summary>Device clock in the fake: 2026-11-07 19:45:12 +07:00; channels 1 and 3 online, 2 offline.</summary>
public class TimeModeTests
{
    private static string V(XElement req, string name) => req.Descendants().First(e => e.Name.LocalName == name).Value;

    [Theory]
    [InlineData(TimeMode.LocalWithZ)]
    [InlineData(TimeMode.Utc)]
    public async Task A_configured_mode_is_returned_without_searching(TimeMode mode)
    {
        var f = new FakeRecorder();
        using var c = f.Client(mode);
        Assert.Equal(mode, await c.ResolveTimeModeAsync(default));
        Assert.Empty(f.SearchRequests);
    }

    [Fact]
    public async Task Wall_clock_with_z_is_detected_and_cached()
    {
        var f = new FakeRecorder()
            .Add(101, "2026-11-07T18:00:00Z", "2026-11-07T19:00:00Z")
            .Add(101, "2026-11-07T19:00:00Z", "2026-11-07T19:44:30Z");
        using var c = f.Client(TimeMode.Auto);
        Assert.Equal(TimeMode.LocalWithZ, await c.ResolveTimeModeAsync(default));
        var req = Assert.Single(f.SearchRequests);
        Assert.Equal("2026-11-06T19:45:12Z", V(req, "startTime"));
        Assert.Equal("2026-11-08T19:45:12Z", V(req, "endTime"));

        var before = f.Handler.Requests.Count;
        Assert.Equal(TimeMode.LocalWithZ, await c.ResolveTimeModeAsync(default));
        Assert.Equal(before, f.Handler.Requests.Count);
    }

    [Fact]
    public async Task Real_utc_is_detected_and_used_for_search_and_playback()
    {
        var f = new FakeRecorder().Add(101, "2026-11-07T11:00:00Z", "2026-11-07T12:44:00Z");
        using var c = f.Client(TimeMode.Auto);
        Assert.Equal(TimeMode.Utc, await c.ResolveTimeModeAsync(default));

        var segs = await c.SearchAsync(1, new DateTime(2026, 11, 7), new DateTime(2026, 11, 8), default);
        Assert.Equal("2026-11-06T17:00:00Z", V(f.SearchRequests[^1], "startTime"));
        var s = Assert.Single(segs);
        Assert.Equal((new DateTime(2026, 11, 7, 18, 0, 0), new DateTime(2026, 11, 7, 19, 44, 0)), (s.Start, s.End));
        Assert.Contains("starttime=20261107T113000Z", c.PlaybackUrl(1, new DateTime(2026, 11, 7, 18, 30, 0)));
    }

    [Fact]
    public async Task Offline_channels_are_skipped_and_later_channels_tried()
    {
        var f = new FakeRecorder().Add(301, "2026-11-07T19:00:00Z", "2026-11-07T19:45:00Z");
        using var c = f.Client(TimeMode.Auto);
        Assert.Equal(TimeMode.LocalWithZ, await c.ResolveTimeModeAsync(default));
        Assert.Equal(["101", "301"], f.SearchRequests.Select(r => V(r, "trackID")));
    }

    [Fact]
    public async Task Unsure_is_local_with_z()
    {
        var f = new FakeRecorder().Add(101, "2026-11-07T03:00:00Z", "2026-11-07T03:05:00Z");
        using var c = f.Client(TimeMode.Auto);
        Assert.Equal(TimeMode.LocalWithZ, await c.ResolveTimeModeAsync(default));
        Assert.Equal(2, f.SearchRequests.Count); // both online channels tried
    }

    [Fact]
    public async Task Nothing_recorded_is_local_with_z()
    {
        using var c = new FakeRecorder().Client(TimeMode.Auto);
        Assert.Equal(TimeMode.LocalWithZ, await c.ResolveTimeModeAsync(default));
    }

    [Fact]
    public async Task Explicit_offsets_resolve_to_utc()
    {
        var f = new FakeRecorder().Add(101, "2026-11-07T19:00:00+07:00", "2026-11-07T19:40:00+07:00");
        using var c = f.Client(TimeMode.Auto);
        Assert.Equal(TimeMode.Utc, await c.ResolveTimeModeAsync(default));
        var s = Assert.Single(await c.SearchAsync(1, new DateTime(2026, 11, 7), new DateTime(2026, 11, 8), default));
        Assert.Equal(new DateTime(2026, 11, 7, 19, 40, 0), s.End);
    }

    [Fact]
    public async Task A_network_failure_is_thrown_and_not_cached()
    {
        var f = new FakeRecorder().Add(101, "2026-11-07T11:00:00Z", "2026-11-07T12:44:00Z");
        using var c = f.Client(TimeMode.Auto);
        f.Handler.Throw = new HttpRequestException("down");
        await Assert.ThrowsAsync<NvrUnreachableException>(() => c.ResolveTimeModeAsync(default));
        f.Handler.Throw = null;
        Assert.Equal(TimeMode.Utc, await c.ResolveTimeModeAsync(default));
    }

    [Fact]
    public async Task Search_resolves_the_mode_first_and_concurrent_callers_share_one_detection()
    {
        var f = new FakeRecorder().Add(101, "2026-11-07T11:00:00Z", "2026-11-07T12:44:00Z");
        using var c = f.Client(TimeMode.Auto);
        var day = new DateTime(2026, 11, 7);
        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => c.SearchAsync(1, day, day.AddDays(1), default)));
        Assert.Equal(1, f.Handler.Count("/ISAPI/System/time"));
        Assert.Equal(1 + 5, f.SearchRequests.Count);
        Assert.All(f.SearchRequests.Skip(1), r => Assert.Equal("2026-11-06T17:00:00Z", V(r, "startTime")));
    }
}
