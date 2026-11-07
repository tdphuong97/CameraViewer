namespace Cv.Core.Nvr;

/// <summary>Turns the recorder's segment list into continuous spans for the playback timeline.</summary>
public static class RecordingTimeline
{
    public static readonly TimeSpan DefaultTolerance = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Sorts the segments and merges those that overlap or are separated by at most <paramref name="tolerance"/>
    /// (default 5 s) into spans. Segments with End &lt;= Start are ignored.
    /// </summary>
    public static IReadOnlyList<TimeRange> Merge(IEnumerable<RecordSegment> segments, TimeSpan? tolerance = null)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var tol = tolerance ?? DefaultTolerance;
        if (tol < TimeSpan.Zero) tol = TimeSpan.Zero;
        var spans = new List<TimeRange>();
        DateTime start = default, end = default;
        var open = false;
        foreach (var s in segments.Where(s => s is not null && s.End > s.Start).OrderBy(s => s.Start))
        {
            if (open && s.Start <= end + tol)
            {
                if (s.End > end) end = s.End;
                continue;
            }
            if (open) spans.Add(new TimeRange(start, end));
            (start, end, open) = (s.Start, s.End, true);
        }
        if (open) spans.Add(new TimeRange(start, end));
        return spans;
    }

    /// <summary>The span containing t, else the first span starting after t, else null.</summary>
    public static TimeRange? SpanAtOrAfter(IReadOnlyList<TimeRange> spans, DateTime t)
    {
        ArgumentNullException.ThrowIfNull(spans);
        TimeRange? next = null;
        foreach (var s in spans)
        {
            if (s.Contains(t)) return s;
            if (s.Start > t && (next is null || s.Start < next.Value.Start)) next = s;
        }
        return next;
    }
}
