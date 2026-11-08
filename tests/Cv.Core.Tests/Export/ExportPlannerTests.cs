using Cv.Core.Export;
using Cv.Core.Nvr;

namespace Cv.Core.Tests.Export;

public class ExportPlannerTests
{
    private static readonly DateTime From = new(2026, 11, 8, 4, 46, 31);
    private static readonly DateTime To = From.AddSeconds(10);
    // The real file of the 2026-11-08 on-site check: 02:16:37-07:16:35 (17998 s).
    private static readonly TimeRange Segment = new(new DateTime(2026, 11, 8, 2, 16, 37), new DateTime(2026, 11, 8, 7, 16, 35));

    private static DownloadedPart Part(DownloadMethod m, DateTime nominal, TimeRange? seg = null) => new("x.ps", nominal, From, To, m, seg);

    [Fact]
    public void A_by_time_part_of_the_right_length_is_copied_whole()
    {
        // 12.05 s for 10 s asked: the recorder starts at the key frame before.
        var (part, fit) = ExportPlanner.Plan(Part(DownloadMethod.ByTime, From), TimeSpan.FromSeconds(12.05));
        Assert.Equal(PartFit.Exact, fit);
        Assert.Equal(new RemuxPart("x.ps"), part);
    }

    [Fact]
    public void A_segment_range_answered_with_the_whole_file_is_cut_from_the_segment_start()
    {
        var (part, fit) = ExportPlanner.Plan(Part(DownloadMethod.BySegmentRange, From, Segment), TimeSpan.FromSeconds(17998.4));
        Assert.Equal(PartFit.WholeFileCut, fit);
        Assert.Equal(new RemuxPart("x.ps", From - Segment.Start, To - Segment.Start), part);
    }

    [Fact]
    public void A_whole_segment_is_cut_to_the_range()
    {
        var (part, fit) = ExportPlanner.Plan(Part(DownloadMethod.BySegmentWhole, Segment.Start, Segment), TimeSpan.FromSeconds(17998));
        Assert.Equal(PartFit.WholeFileCut, fit);
        Assert.Equal(TimeSpan.FromSeconds(8994), part.From);
    }

    [Fact]
    public void Any_other_length_is_a_mismatch_kept_as_is()
    {
        var (part, fit) = ExportPlanner.Plan(Part(DownloadMethod.ByTime, From), TimeSpan.FromSeconds(300));
        Assert.Equal(PartFit.Mismatch, fit);
        Assert.Equal(new RemuxPart("x.ps"), part);
        Assert.Equal(PartFit.Mismatch, ExportPlanner.Plan(Part(DownloadMethod.BySegmentRange, From, Segment), TimeSpan.FromSeconds(600)).Fit);
        Assert.Equal(PartFit.Mismatch, ExportPlanner.Plan(Part(DownloadMethod.ByTime, From), null).Fit);
    }
}
