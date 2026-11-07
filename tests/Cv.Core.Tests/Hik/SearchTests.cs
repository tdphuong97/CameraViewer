using System.Xml.Linq;
using Cv.Core.Hik;
using Cv.Core.Nvr;
using Cv.Core.Tests.Fakes;

namespace Cv.Core.Tests.Hik;

public class SearchTests
{
    private static readonly DateTime Day = new(2026, 11, 7);

    private static string V(XElement req, string name) => req.Descendants().First(e => e.Name.LocalName == name).Value;

    private static string Iso(DateTime t) => t.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

    [Fact]
    public async Task All_pages_are_fetched_with_one_search_id()
    {
        var f = new FakeRecorder();
        for (var i = 0; i < 120; i++) f.Add(101, Iso(Day.AddMinutes(i * 10)), Iso(Day.AddMinutes(i * 10 + 9)));
        using var c = f.Client();
        var segs = await c.SearchAsync(1, Day, Day.AddDays(1), default);
        Assert.Equal(120, segs.Count);
        Assert.Equal(3, f.SearchRequests.Count);
        Assert.Equal(["0", "50", "100"], f.SearchRequests.Select(r => V(r, "searchResultPostion")));
        Assert.Single(f.SearchRequests.Select(r => V(r, "searchID")).Distinct());
        Assert.All(f.SearchRequests, r => Assert.Equal("50", V(r, "maxResults")));
        Assert.Equal(segs.OrderBy(s => s.Start), segs);
    }

    [Fact]
    public async Task No_matches_is_an_empty_list()
    {
        var f = new FakeRecorder();
        using var c = f.Client();
        Assert.Empty(await c.SearchAsync(1, Day, Day.AddDays(1), default));
        Assert.Single(f.SearchRequests);
    }

    [Fact]
    public async Task Results_are_sorted_and_keep_the_playback_uri()
    {
        var f = new FakeRecorder();
        f.Add(101, "2026-11-07T10:00:00Z", "2026-11-07T11:00:00Z").Add(101, "2026-11-07T08:00:00Z", "2026-11-07T09:00:00Z");
        using var c = f.Client();
        var segs = await c.SearchAsync(1, Day, Day.AddDays(1), default);
        Assert.Equal([Day.AddHours(8), Day.AddHours(10)], segs.Select(s => s.Start));
        Assert.Equal("rtsp://192.168.1.64/Streaming/tracks/101/?starttime=20261107T080000Z&endtime=20261107T090000Z&name=00001&size=524288000", segs[0].PlaybackUri);
    }

    [Fact]
    public async Task Sub_stream_searches_track_102()
    {
        var f = new FakeRecorder();
        using var c = f.Client();
        await c.SearchAsync(3, Day, Day.AddDays(1), default, StreamKind.Sub);
        Assert.Equal("302", V(f.SearchRequests[0], "trackID"));
    }

    [Fact]
    public async Task Local_with_z_sends_and_reads_the_wall_clock()
    {
        var f = new FakeRecorder().Add(101, "2026-11-07T14:00:00Z", "2026-11-07T15:00:00Z");
        using var c = f.Client(TimeMode.LocalWithZ);
        var segs = await c.SearchAsync(1, Day.AddHours(14), Day.AddHours(16), default);
        Assert.Equal("2026-11-07T14:00:00Z", V(f.SearchRequests[0], "startTime"));
        Assert.Equal("2026-11-07T16:00:00Z", V(f.SearchRequests[0], "endTime"));
        var s = Assert.Single(segs);
        Assert.Equal((Day.AddHours(14), Day.AddHours(15)), (s.Start, s.End));
        Assert.Equal(DateTimeKind.Unspecified, s.Start.Kind);
    }

    [Fact]
    public async Task Utc_mode_sends_utc_and_converts_results_with_the_device_offset()
    {
        var f = new FakeRecorder().Add(101, "2026-11-07T07:00:00Z", "2026-11-07T08:00:00Z");
        using var c = f.Client(TimeMode.Utc);
        var segs = await c.SearchAsync(1, Day.AddHours(14), Day.AddHours(16), default);
        Assert.Equal("2026-11-07T07:00:00Z", V(f.SearchRequests[0], "startTime"));
        Assert.Equal("2026-11-07T09:00:00Z", V(f.SearchRequests[0], "endTime"));
        var s = Assert.Single(segs);
        Assert.Equal((Day.AddHours(14), Day.AddHours(15)), (s.Start, s.End));
    }

    [Fact]
    public async Task Explicit_offsets_convert_to_device_local()
    {
        var f = new FakeRecorder()
            .Add(101, "2026-11-07T14:00:00+07:00", "2026-11-07T15:00:00+07:00")
            .Add(101, "2026-11-07T08:00:00+00:00", "2026-11-07T09:00:00+00:00");
        using var c = f.Client(TimeMode.LocalWithZ);
        var segs = await c.SearchAsync(1, Day, Day.AddDays(1), default);
        Assert.Equal([(Day.AddHours(14), Day.AddHours(15)), (Day.AddHours(15), Day.AddHours(16))], segs.Select(s => (s.Start, s.End)));
    }

    [Fact]
    public async Task Paging_stops_at_the_safety_cap()
    {
        var f = new FakeRecorder { EndlessMore = true };
        using var c = f.Client();
        var segs = await c.SearchAsync(1, Day, Day.AddDays(1), default);
        Assert.Equal(HikNvrClient.SearchMaxPages, f.SearchRequests.Count);
        Assert.Single(segs); // identical items collapse
    }

    [Fact]
    public async Task Unparsable_items_are_skipped()
    {
        var f = new FakeRecorder().Add(101, "2026-11-07T10:00:00Z", "2026-11-07T11:00:00Z");
        f.Tracks[101].Add(new FakeRecorder.Item("2026-11-07T12:00:00Z", "garbage-time-value-x", "rtsp://x"));
        using var c = f.Client();
        Assert.Single(await c.SearchAsync(1, Day, Day.AddDays(1), default));
    }
}
