using Cv.Core.Nvr;

namespace Cv.Core.Tests.Nvr;

public class PeopleTimelineTests
{
    private static readonly DateTime T0 = new(2026, 11, 7, 8, 0, 0);

    private static DetectionEvent E(int ch, double seconds, string? pic = null) => new(ch, T0.AddSeconds(seconds), pic);

    [Fact]
    public void Nothing_gives_no_visits() => Assert.Empty(PeopleTimeline.Merge([]));

    [Fact]
    public void A_person_seen_every_second_is_one_visit()
    {
        var visits = PeopleTimeline.Merge(Enumerable.Range(0, 30).Select(i => E(14, i, $"/p{i}")));

        var v = Assert.Single(visits);
        Assert.Equal(new PeopleVisit(14, T0, T0.AddSeconds(29), "/p0", 30), v);
    }

    [Fact]
    public void A_gap_of_a_minute_starts_a_new_visit()
    {
        var visits = PeopleTimeline.Merge([E(14, 0), E(14, 59), E(14, 119), E(14, 119.5)]);

        Assert.Equal([(T0, T0.AddSeconds(59), 2), (T0.AddSeconds(119), T0.AddSeconds(119.5), 2)], visits.Select(v => (v.Start, v.End, v.Count)));
    }

    [Fact]
    public void Channels_merge_apart_and_visits_come_oldest_first_whatever_the_input_order()
    {
        var visits = PeopleTimeline.Merge([E(8, 300), E(14, 10), E(8, 0), E(14, 0), E(8, 1)]);

        Assert.Equal([(8, T0, 2), (14, T0, 2), (8, T0.AddSeconds(300), 1)], visits.Select(v => (v.Channel, v.Start, v.Count)));
    }

    [Fact]
    public void Visit_picture_is_the_first_detection_that_has_one()
    {
        var visits = PeopleTimeline.Merge([E(14, 0), E(14, 1, "/p1"), E(14, 2, "/p2")]);

        Assert.Equal("/p1", Assert.Single(visits).PictureUri);
    }
}
