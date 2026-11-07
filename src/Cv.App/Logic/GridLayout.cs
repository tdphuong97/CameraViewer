using Cv.Core.Nvr;
using Cv.Core.Settings;

namespace Cv.App.Logic;

/// <summary>Live grid sizing and camera order. Pure logic, no WPF.</summary>
public static class GridLayout
{
    /// <summary>Grid sizes the settings accept (AppSettings.LiveGrid); 0 = automatic.</summary>
    public static readonly int[] Sizes = [1, 4, 6, 9, 16, 20, 25];

    /// <summary>Choices offered in the layout picker.</summary>
    public static readonly int[] PickerSizes = [0, 1, 4, 9, 16, 25];

    public const int Max = 25;

    /// <summary>Smallest size that fits every camera, at most 25 (17 cameras → 20 = 5×4).</summary>
    public static int AutoSize(int cameraCount)
    {
        foreach (var s in Sizes)
            if (s >= cameraCount) return s;
        return Max;
    }

    /// <summary>The size to show for a setting (0 or an unknown value = automatic).</summary>
    public static int Resolve(int setting, int cameraCount) =>
        setting != 0 && Array.IndexOf(Sizes, setting) >= 0 ? setting : AutoSize(cameraCount);

    /// <summary>Columns × rows, wider than tall to suit 16:9 screens.</summary>
    public static (int Cols, int Rows) Dimensions(int size) => size switch
    {
        <= 1 => (1, 1),
        4 => (2, 2),
        6 => (3, 2),
        9 => (3, 3),
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
    /// channel number, saved entries that no longer exist skipped.
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

    /// <summary>Swaps two positions and returns the new saved order for the whole list.</summary>
    public static List<CameraRef> Swap(List<ChannelInfo> ordered, int a, int b, string nvrId)
    {
        if (a != b && a >= 0 && b >= 0 && a < ordered.Count && b < ordered.Count)
            (ordered[a], ordered[b]) = (ordered[b], ordered[a]);
        return ordered.Select(c => new CameraRef(nvrId, c.No)).ToList();
    }

    /// <summary>Grid tiles use the low-resolution stream; a 1-camera layout is as big as single view, so it uses Main.</summary>
    public static StreamKind StreamFor(int size) => size <= 1 ? StreamKind.Main : StreamKind.Sub;
}
