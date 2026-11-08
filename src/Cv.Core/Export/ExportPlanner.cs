using Cv.Core.Nvr;

namespace Cv.Core.Export;

/// <summary>How a downloaded part relates to the range it should cover.</summary>
public enum PartFit
{
    /// <summary>Lasts as long as the range (± max(10 s, 10 %)): copied whole.</summary>
    Exact,
    /// <summary>A whole recorder file (asked for, or sent instead of the narrowed range): cut to the range.</summary>
    WholeFileCut,
    /// <summary>Neither: copied as it is, the length will be off.</summary>
    Mismatch,
}

/// <summary>
/// Turns a downloaded part into what the remux copies. Shared by the export and the on-site check (Chạy chẩn đoán),
/// so the report tells what an export really does.
/// </summary>
public static class ExportPlanner
{
    public static TimeSpan Tolerance(TimeSpan length) => TimeSpan.FromTicks(Math.Max(TimeSpan.FromSeconds(10).Ticks, length.Ticks / 10));

    /// <summary>
    /// A part is honored when its real <paramref name="duration"/> matches the covered range. BySegmentWhole files are
    /// whole recorder files, so they are always cut to the covered range (bounds that reach the file ends stay open). A
    /// BySegmentRange part that lasts as long as its whole segment is that file (firmware that ignores the narrowed
    /// times) and is cut the same way. Anything else is kept as is.
    /// </summary>
    public static (RemuxPart Part, PartFit Fit) Plan(DownloadedPart p, TimeSpan? duration)
    {
        var expected = p.CoveredTo - p.CoveredFrom;
        var honored = duration is { } d && (d - expected).Duration() <= Tolerance(expected);

        if (!honored && p.Method == DownloadMethod.BySegmentRange && p.Segment is { } seg && duration is { } whole
            && (whole - seg.Duration).Duration() <= Tolerance(seg.Duration))
            p = p with { NominalStart = seg.Start, Method = DownloadMethod.BySegmentWhole };

        if (p.Method == DownloadMethod.BySegmentWhole)
        {
            var from = p.CoveredFrom - p.NominalStart;
            var to = p.CoveredTo - p.NominalStart;
            TimeSpan? f = from > TimeSpan.Zero ? from : null;
            TimeSpan? t = duration is { } len && to >= len ? null : to;
            if (honored && f is null && t is null) return (new RemuxPart(p.FilePath), PartFit.Exact);
            return (new RemuxPart(p.FilePath, f, t), PartFit.WholeFileCut);
        }
        return (new RemuxPart(p.FilePath), honored ? PartFit.Exact : PartFit.Mismatch);
    }
}
