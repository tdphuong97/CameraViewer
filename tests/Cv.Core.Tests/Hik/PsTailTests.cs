using Cv.Core.Hik;
using Cv.Core.Tests.Fakes;

namespace Cv.Core.Tests.Hik;

public class PsTailTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private string Write(byte[] data)
    {
        var path = _dir.File(Guid.NewGuid().ToString("N") + ".ps");
        File.WriteAllBytes(path, data);
        return path;
    }

    [Fact]
    public void A_file_ending_on_a_packet_boundary_is_left_alone()
    {
        var (ps, _) = Samples.FramedPs(10);
        var path = Write(ps);
        Assert.Equal(0, PsTail.DropIncompleteFrame(path));
        Assert.Equal(ps, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData(500)]   // inside the continuation PES of the last frame
    [InlineData(1010)]  // inside the last frame's second pack header
    [InlineData(1800)]  // inside the PES that starts the last frame
    public void A_file_cut_inside_the_last_frame_loses_that_frame(int missing)
    {
        var (ps, frameStarts) = Samples.FramedPs(10);
        var path = Write(ps[..^missing]);
        var removed = PsTail.DropIncompleteFrame(path);
        Assert.Equal(ps[..frameStarts[^1]], File.ReadAllBytes(path));
        Assert.Equal(ps.Length - missing - frameStarts[^1], removed);
    }

    [Fact]
    public void Data_that_is_not_a_program_stream_is_left_alone()
    {
        var ps = Samples.Ps(20_000);
        var path = Write(ps[..^7]);
        Assert.Equal(0, PsTail.DropIncompleteFrame(path));
        Assert.Equal(ps.Length - 7, new FileInfo(path).Length);
    }

    [Fact]
    public void A_tiny_file_is_never_cut_to_nothing()
    {
        var (ps, _) = Samples.FramedPs(2);
        var path = Write(ps[..^100]);
        Assert.Equal(0, PsTail.DropIncompleteFrame(path));
    }

    [Fact]
    public void Only_the_end_of_a_big_file_is_read()
    {
        // More than the 4 MB window: the walk starts at a pack header inside the window and still finds the cut.
        var (ps, frameStarts) = Samples.FramedPs(2500);
        Assert.True(ps.Length > PsTail.Window);
        var path = Write(ps[..^300]);
        PsTail.DropIncompleteFrame(path);
        Assert.Equal(frameStarts[^1], new FileInfo(path).Length);
    }
}
