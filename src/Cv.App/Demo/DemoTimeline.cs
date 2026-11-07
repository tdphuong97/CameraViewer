using Cv.Core.Nvr;

namespace Cv.App.Demo;

/// <summary>
/// Stand-in for RecordingTimeline while Core is still a stub in this branch. Used only in demo mode, only when the
/// real one throws NotImplementedException, so the real one wins as soon as it exists.
/// </summary>
internal static class DemoTimeline
{
    public static IReadOnlyList<TimeRange> Merge(IEnumerable<RecordSegment> segments, TimeSpan? tolerance = null)
    {
        var tol = tolerance ?? TimeSpan.FromSeconds(5);
        var spans = new List<TimeRange>();
        foreach (var s in segments.Where(s => s.End > s.Start).OrderBy(s => s.Start))
        {
            if (spans.Count > 0 && s.Start <= spans[^1].End + tol)
            {
                var last = spans[^1];
                if (s.End > last.End) spans[^1] = last with { End = s.End };
            }
            else spans.Add(new TimeRange(s.Start, s.End));
        }
        return spans;
    }

    public static TimeRange? SpanAtOrAfter(IReadOnlyList<TimeRange> spans, DateTime t)
    {
        foreach (var s in spans)
            if (s.Contains(t) || s.Start > t) return s;
        return null;
    }
}
