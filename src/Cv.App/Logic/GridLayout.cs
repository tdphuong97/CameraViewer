using Cv.Core.Nvr;
using Cv.Core.Settings;

namespace Cv.App.Logic;

/// <summary>One tile of a live layout: grid position and span; Big marks the large tile of a "1 big + N small" layout.</summary>
public readonly record struct Cell(int Col, int Row, int ColSpan, int RowSpan, bool Big);

/// <summary>A live layout: the grid it sits on and its tiles in reading order (the big tile first).</summary>
public sealed record LayoutSpec(int Code, int Cols, int Rows, IReadOnlyList<Cell> Cells)
{
    public int Size => Cells.Count;
}

/// <summary>Live grid sizing and camera order. Pure logic, no WPF.</summary>
public static class GridLayout
{
    /// <summary>Layout codes the settings accept (AppSettings.LiveGrid); 0 = automatic. 100 + N = one big tile and N small ones.</summary>
    public static readonly int[] Sizes = [1, 4, 6, 8, 9, 12, 16, 20, 25, 105, 107, 112];

    /// <summary>"Chia đều" choices in the layout picker (0 = automatic).</summary>
    public static readonly int[] PickerSizes = [0, 1, 4, 6, 8, 9, 12, 16, 25];

    /// <summary>"Khung lớn + khung nhỏ" choices in the layout picker.</summary>
    public static readonly int[] BigPickerSizes = [105, 107, 112];

    /// <summary>Sizes automatic picks from. Kept as before: 8 and 12 are chosen by hand only, so automatic never changes.</summary>
    static readonly int[] AutoSizes = [1, 4, 6, 9, 16, 20, 25];

    public const int Max = 25;

    public static bool IsBig(int code) => code >= 100;

    static bool IsKnown(int setting) => setting != 0 && Array.IndexOf(Sizes, setting) >= 0;

    /// <summary>Text on the layout button: "Tự động · 20", "9 khung" or "1+5" (<paramref name="resolved"/> = what Resolve returned).</summary>
    public static string Label(int setting, int resolved) =>
        IsKnown(setting) ? ShortLabel(setting) : $"Tự động · {Spec(resolved).Size}";

    /// <summary>Same without the size of automatic, for lists: "Tự động", "9 khung", "1+5".</summary>
    public static string ShortLabel(int setting) =>
        !IsKnown(setting) ? "Tự động" : IsBig(setting) ? $"1+{setting - 100}" : $"{setting} khung";

    /// <summary>Text under a thumbnail in the layout picker: "Tự động", "9", "1+5".</summary>
    public static string OptionLabel(int code) =>
        !IsKnown(code) ? "Tự động" : IsBig(code) ? $"1+{code - 100}" : code.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Smallest size that fits every camera, at most 25 (17 cameras → 20 = 5×4).</summary>
    public static int AutoSize(int cameraCount)
    {
        foreach (var s in AutoSizes)
            if (s >= cameraCount) return s;
        return Max;
    }

    /// <summary>The code to show for a setting (0 or an unknown value = automatic).</summary>
    public static int Resolve(int setting, int cameraCount) =>
        setting != 0 && Array.IndexOf(Sizes, setting) >= 0 ? setting : AutoSize(cameraCount);

    /// <summary>The layout for a resolved code: equal tiles wider than tall to suit 16:9 screens, or one big tile plus small ones.</summary>
    public static LayoutSpec Spec(int code) => code switch
    {
        105 => Big(code, 3, 3, 2, 2),
        107 => Big(code, 4, 4, 3, 3),
        112 => Big(code, 4, 4, 2, 2),
        _ => Equal(code),
    };

    static LayoutSpec Equal(int size)
    {
        var (cols, rows) = Dimensions(size);
        var cells = new List<Cell>(size);
        for (var i = 0; i < size; i++) cells.Add(new Cell(i % cols, i / cols, 1, 1, false));
        return new LayoutSpec(size, cols, rows, cells);
    }

    /// <summary>The big tile in the top-left corner, small tiles filling the rest in reading order.</summary>
    static LayoutSpec Big(int code, int cols, int rows, int bigCols, int bigRows)
    {
        var cells = new List<Cell> { new(0, 0, bigCols, bigRows, true) };
        for (var r = 0; r < rows; r++)
            for (var c = 0; c < cols; c++)
                if (c >= bigCols || r >= bigRows) cells.Add(new Cell(c, r, 1, 1, false));
        return new LayoutSpec(code, cols, rows, cells);
    }

    /// <summary>Columns × rows of an equal-tile grid.</summary>
    public static (int Cols, int Rows) Dimensions(int size) => size switch
    {
        <= 1 => (1, 1),
        4 => (2, 2),
        6 => (3, 2),
        8 => (4, 2),
        9 => (3, 3),
        12 => (4, 3),
        16 => (4, 4),
        20 => (5, 4),
        25 => (5, 5),
        _ => Fallback(size),
    };

    static (int, int) Fallback(int size)
    {
        var cols = (int)Math.Ceiling(Math.Sqrt(size));
        return (cols, (int)Math.Ceiling(size / (double)cols));
    }

    public static int PageCount(int cameraCount, int size) =>
        cameraCount <= 0 ? 1 : (cameraCount + size - 1) / size;

    /// <summary>
    /// Orders cameras by the saved order: known cameras in saved order, cameras not in the saved order appended by
    /// channel number, saved entries that no longer exist skipped. Hidden cameras stay in the list (see Visible).
    /// </summary>
    public static List<ChannelInfo> Order(IReadOnlyList<ChannelInfo> channels, IReadOnlyList<CameraRef> saved, string nvrId)
    {
        var byNo = new Dictionary<int, ChannelInfo>();
        foreach (var c in channels) byNo[c.No] = c;
        var result = new List<ChannelInfo>(channels.Count);
        var used = new HashSet<int>();
        foreach (var r in saved)
        {
            if (r.NvrId != nvrId || !byNo.TryGetValue(r.Channel, out var c) || !used.Add(c.No)) continue;
            result.Add(c);
        }
        foreach (var c in channels.OrderBy(c => c.No))
            if (used.Add(c.No)) result.Add(c);
        return result;
    }

    /// <summary>Channel numbers of this recorder's hidden cameras.</summary>
    public static HashSet<int> HiddenSet(IReadOnlyList<CameraRef> hidden, string nvrId) =>
        hidden.Where(r => r.NvrId == nvrId).Select(r => r.Channel).ToHashSet();

    /// <summary>The cameras the live grid shows: the ordered list without the hidden ones.</summary>
    public static List<ChannelInfo> Visible(IReadOnlyList<ChannelInfo> all, ISet<int> hidden) =>
        all.Where(c => !hidden.Contains(c.No)).ToList();

    /// <summary>The order as saved in the settings.</summary>
    public static List<CameraRef> ToRefs(IEnumerable<ChannelInfo> all, string nvrId) =>
        all.Select(c => new CameraRef(nvrId, c.No)).ToList();

    public static List<CameraRef> ToRefs(IEnumerable<int> channels, string nvrId) =>
        channels.Select(n => new CameraRef(nvrId, n)).ToList();

    /// <summary>Swaps two cameras in the ordered list, wherever they are (also across pages).</summary>
    public static void Swap(List<ChannelInfo> all, int noA, int noB)
    {
        var i = all.FindIndex(c => c.No == noA);
        var j = all.FindIndex(c => c.No == noB);
        if (i < 0 || j < 0 || i == j) return;
        (all[i], all[j]) = (all[j], all[i]);
    }

    public static void MoveToEnd(List<ChannelInfo> all, int no)
    {
        var i = all.FindIndex(c => c.No == no);
        if (i < 0 || i == all.Count - 1) return;
        var c = all[i];
        all.RemoveAt(i);
        all.Add(c);
    }

    /// <summary>
    /// A camera dragged from the camera list onto a tile. Onto another camera: the two swap. Onto an empty tile: the
    /// camera goes to the end. A hidden camera becomes visible; the camera it replaces moves to the end (not lost).
    /// </summary>
    public static void Place(List<ChannelInfo> all, ISet<int> hidden, int draggedNo, int? targetNo)
    {
        if (targetNo == draggedNo) return;
        var wasHidden = hidden.Remove(draggedNo);
        if (targetNo is not { } target)
        {
            MoveToEnd(all, draggedNo);
            return;
        }
        Swap(all, draggedNo, target);
        if (wasHidden) MoveToEnd(all, target);
    }

    /// <summary>Tiles use the low-resolution stream; the big tile and a 1-camera layout are as big as single view, so they use Main.</summary>
    public static StreamKind StreamFor(Cell cell, int size) => cell.Big || size <= 1 ? StreamKind.Main : StreamKind.Sub;
}
