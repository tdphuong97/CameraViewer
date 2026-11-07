namespace Cv.Core.Nvr;

/// <summary>Turns the recorder's segment list into continuous spans for the playback timeline. (Track A implements.)</summary>
public static class RecordingTimeline
{
    /// <summary>
    /// Sorts the segments and merges those that overlap or are separated by at most <paramref name="tolerance"/>
    /// (default 5 s) into spans. Segments with End &lt;= Start are ignored.
    /// </summary>
    public static IReadOnlyList<TimeRange> Merge(IEnumerable<RecordSegment> segments, TimeSpan? tolerance = null) =>
        throw new NotImplementedException("Track A: RecordingTimeline.Merge");

    /// <summary>The span containing t, else the first span starting after t, else null.</summary>
    public static TimeRange? SpanAtOrAfter(IReadOnlyList<TimeRange> spans, DateTime t) =>
        throw new NotImplementedException("Track A: RecordingTimeline.SpanAtOrAfter");
}
