using Cv.App.Logic;
using Cv.Core.Nvr;
using Cv.Core.Settings;
using Cv.Core.Video;

namespace Cv.Core.Tests.Live;

public class GridLayoutTests
{
    static List<ChannelInfo> Cams(int n) => Enumerable.Range(1, n).Select(i => new ChannelInfo(i, $"Camera {i}", true)).ToList();
    static int[] Nos(IEnumerable<ChannelInfo> l) => l.Select(c => c.No).ToArray();

    // ---- Automatic stays as it was ----

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 4)]
    [InlineData(5, 6)]
    [InlineData(7, 9)] // not 8
    [InlineData(10, 16)] // not 12
    [InlineData(17, 20)]
    [InlineData(21, 25)]
    [InlineData(40, 25)]
    public void Automatic_size_never_picks_8_or_12(int cameras, int expected) =>
        Assert.Equal(expected, GridLayout.AutoSize(cameras));

    [Theory]
    [InlineData(0, 17, 20)]
    [InlineData(99, 17, 20)] // unknown code = automatic
    [InlineData(8, 17, 8)]
    [InlineData(12, 3, 12)]
    [InlineData(105, 17, 105)]
    [InlineData(25, 3, 25)]
    public void Resolve_keeps_known_codes(int setting, int cameras, int expected) =>
        Assert.Equal(expected, GridLayout.Resolve(setting, cameras));

    // ---- Layouts ----

    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(4, 2, 2)]
    [InlineData(6, 3, 2)]
    [InlineData(8, 4, 2)]
    [InlineData(9, 3, 3)]
    [InlineData(12, 4, 3)]
    [InlineData(16, 4, 4)]
    [InlineData(20, 5, 4)]
    [InlineData(25, 5, 5)]
    public void Equal_layouts_fill_the_grid(int size, int cols, int rows)
    {
        var spec = GridLayout.Spec(size);
        Assert.Equal((cols, rows, size), (spec.Cols, spec.Rows, spec.Size));
        Assert.All(spec.Cells, c => Assert.Equal((1, 1, false), (c.ColSpan, c.RowSpan, c.Big)));
        AssertNoOverlapAndFull(spec);
    }

    [Theory]
    [InlineData(105, 3, 3, 2, 6)]
    [InlineData(107, 4, 4, 3, 8)]
    [InlineData(112, 4, 4, 2, 13)]
    public void Big_layouts_have_one_big_tile_first_and_cover_the_grid_exactly(int code, int cols, int rows, int bigSpan, int tiles)
    {
        var spec = GridLayout.Spec(code);
        Assert.Equal((cols, rows, tiles), (spec.Cols, spec.Rows, spec.Size));
        var big = spec.Cells[0];
        Assert.Equal((0, 0, bigSpan, bigSpan, true), (big.Col, big.Row, big.ColSpan, big.RowSpan, big.Big));
        Assert.Single(spec.Cells, c => c.Big);
        AssertNoOverlapAndFull(spec);
    }

    static void AssertNoOverlapAndFull(LayoutSpec spec)
    {
        var taken = new bool[spec.Cols, spec.Rows];
        foreach (var c in spec.Cells)
            for (var r = c.Row; r < c.Row + c.RowSpan; r++)
                for (var k = c.Col; k < c.Col + c.ColSpan; k++)
                {
                    Assert.True(k < spec.Cols && r < spec.Rows, $"cell {c} outside the grid");
                    Assert.False(taken[k, r], $"cell {c} overlaps another");
                    taken[k, r] = true;
                }
        foreach (var t in taken) Assert.True(t, "a grid square is not covered");
    }

    [Fact]
    public void Every_picker_code_is_a_known_code()
    {
        foreach (var code in GridLayout.PickerSizes.Concat(GridLayout.BigPickerSizes).Where(c => c != 0))
            Assert.Contains(code, GridLayout.Sizes);
    }

    [Fact]
    public void The_big_tile_and_a_one_camera_layout_use_the_main_stream()
    {
        var big = GridLayout.Spec(105);
        Assert.Equal(StreamKind.Main, GridLayout.StreamFor(big.Cells[0], big.Size));
        Assert.Equal(StreamKind.Sub, GridLayout.StreamFor(big.Cells[1], big.Size));
        var one = GridLayout.Spec(1);
        Assert.Equal(StreamKind.Main, GridLayout.StreamFor(one.Cells[0], one.Size));
        var four = GridLayout.Spec(4);
        Assert.All(four.Cells, c => Assert.Equal(StreamKind.Sub, GridLayout.StreamFor(c, four.Size)));
    }

    [Fact]
    public void Page_count_rounds_up_and_is_at_least_one()
    {
        Assert.Equal(1, GridLayout.PageCount(0, 9));
        Assert.Equal(2, GridLayout.PageCount(10, 9));
        Assert.Equal(3, GridLayout.PageCount(13, 6));
    }

    // ---- Order, hidden, drag and drop ----

    [Fact]
    public void Order_follows_the_saved_order_then_appends_new_cameras()
    {
        var saved = new[] { new CameraRef("n", 3), new CameraRef("n", 1), new CameraRef("other", 2), new CameraRef("n", 99) };
        Assert.Equal([3, 1, 2, 4], Nos(GridLayout.Order(Cams(4), saved, "n")));
    }

    [Fact]
    public void Hidden_cameras_stay_in_the_order_but_not_in_the_visible_list()
    {
        var all = GridLayout.Order(Cams(5), [], "n");
        var hidden = GridLayout.HiddenSet([new CameraRef("n", 2), new CameraRef("other", 3), new CameraRef("n", 4)], "n");
        Assert.Equal([2, 4], hidden.OrderBy(x => x));
        Assert.Equal([1, 3, 5], Nos(GridLayout.Visible(all, hidden)));
        Assert.Equal(5, all.Count);
    }

    [Fact]
    public void Swap_works_by_camera_number_across_pages()
    {
        var all = Cams(10);
        GridLayout.Swap(all, 1, 10);
        Assert.Equal([10, 2, 3, 4, 5, 6, 7, 8, 9, 1], Nos(all));
        GridLayout.Swap(all, 3, 3); // same camera: nothing
        GridLayout.Swap(all, 3, 77); // unknown camera: nothing
        Assert.Equal([10, 2, 3, 4, 5, 6, 7, 8, 9, 1], Nos(all));
    }

    [Fact]
    public void Dragging_a_shown_camera_onto_another_swaps_them_and_onto_an_empty_tile_moves_it_to_the_end()
    {
        var all = Cams(5);
        var hidden = new HashSet<int>();
        GridLayout.Place(all, hidden, draggedNo: 5, targetNo: 2);
        Assert.Equal([1, 5, 3, 4, 2], Nos(all));
        GridLayout.Place(all, hidden, draggedNo: 1, targetNo: null);
        Assert.Equal([5, 3, 4, 2, 1], Nos(all));
        GridLayout.Place(all, hidden, draggedNo: 3, targetNo: 3);
        Assert.Equal([5, 3, 4, 2, 1], Nos(all));
    }

    [Fact]
    public void A_hidden_camera_dragged_onto_a_tile_shows_and_the_camera_it_replaces_goes_to_the_end()
    {
        var all = Cams(6);
        var hidden = new HashSet<int> { 5 };
        GridLayout.Place(all, hidden, draggedNo: 5, targetNo: 2);
        Assert.Empty(hidden);
        // 5 now sits where 2 was; 2 is not lost, it is last.
        Assert.Equal([1, 5, 3, 4, 6, 2], Nos(all));
    }

    [Fact]
    public void A_hidden_camera_dragged_onto_an_empty_tile_shows_at_the_end()
    {
        var all = Cams(4);
        var hidden = new HashSet<int> { 2 };
        GridLayout.Place(all, hidden, draggedNo: 2, targetNo: null);
        Assert.Empty(hidden);
        Assert.Equal([1, 3, 4, 2], Nos(all));
    }

    [Fact]
    public void ToRefs_round_trips_through_Order()
    {
        var all = Cams(4);
        GridLayout.Swap(all, 1, 4);
        var refs = GridLayout.ToRefs(all, "n");
        Assert.Equal(Nos(all), Nos(GridLayout.Order(Cams(4), refs, "n")));
    }
}
