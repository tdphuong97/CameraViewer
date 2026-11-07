using Cv.Core.Nvr;

namespace Cv.Core.Tests.Nvr;

public class RecordingTimelineTests
{
    private static readonly DateTime D = new(2026, 11, 7);

    private static RecordSegment S(double fromMin, double toMin) => new(D.AddMinutes(fromMin), D.AddMinutes(toMin), "");

    private static TimeRange R(double fromMin, double toMin) => new(D.AddMinutes(fromMin), D.AddMinutes(toMin));

    [Fact]
    public void Overlapping_and_touching_segments_merge()
    {
        var spans = RecordingTimeline.Merge([S(0, 60), S(60, 120), S(110, 130)]);
        Assert.Equal([R(0, 130)], spans);
    }

    [Fact]
    public void Gaps_up_to_the_tolerance_merge_and_larger_ones_split()
    {
        var segs = new[]
        {
            S(0, 60),
            new RecordSegment(D.AddSeconds(3605), D.AddMinutes(90), ""),
            new RecordSegment(D.AddSeconds(5406), D.AddMinutes(100), ""),
        };
        Assert.Equal([R(0, 90), new TimeRange(D.AddSeconds(5406), D.AddMinutes(100))], RecordingTimeline.Merge(segs));
        Assert.Equal([R(0, 100)], RecordingTimeline.Merge(segs, TimeSpan.FromSeconds(10)));
        Assert.Equal(3, RecordingTimeline.Merge(segs, TimeSpan.Zero).Count);
    }

    [Fact]
    public void Unsorted_input_is_sorted_and_empty_segments_ignored()
    {
        var spans = RecordingTimeline.Merge([S(200, 260), S(0, 30), S(40, 40), S(50, 45), S(10, 20)]);
        Assert.Equal([R(0, 30), R(200, 260)], spans);
        Assert.Empty(RecordingTimeline.Merge([]));
    }

    [Fact]
    public void A_contained_segment_does_not_shorten_the_span()
    {
        Assert.Equal([R(0, 120)], RecordingTimeline.Merge([S(0, 120), S(10, 20)]));
    }

    [Fact]
    public void Span_at_or_after_finds_the_containing_or_next_span()
    {
        var spans = new[] { R(0, 60), R(90, 120) };
        Assert.Equal(R(0, 60), RecordingTimeline.SpanAtOrAfter(spans, D.AddMinutes(0)));
        Assert.Equal(R(0, 60), RecordingTimeline.SpanAtOrAfter(spans, D.AddMinutes(59)));
        Assert.Equal(R(90, 120), RecordingTimeline.SpanAtOrAfter(spans, D.AddMinutes(60))); // end is exclusive
        Assert.Equal(R(90, 120), RecordingTimeline.SpanAtOrAfter(spans, D.AddMinutes(75)));
        Assert.Equal(R(0, 60), RecordingTimeline.SpanAtOrAfter(spans, D.AddMinutes(-10)));
        Assert.Null(RecordingTimeline.SpanAtOrAfter(spans, D.AddMinutes(120)));
        Assert.Null(RecordingTimeline.SpanAtOrAfter([], D));
    }
}
