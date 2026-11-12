using Cv.Core.Settings;

namespace Cv.App.Logic;

/// <summary>
/// The "Bộ" (named arrangements) of the live view. The default one lives in AppSettings.LiveGrid / LiveOrder / LiveHidden
/// (so settings from older versions load as the default); the others in AppSettings.LivePresets. Every edit is saved
/// straight into the arrangement in use. Pure logic, no WPF.
/// </summary>
public static class LiveArrangements
{
    public const string DefaultName = AppSettings.DefaultArrangementName;
    public const int MaxNameLength = 24;

    /// <summary>The arrangement in use (a copy: changing it changes nothing until SaveActive).</summary>
    public static LiveArrangement Active(AppSettings s)
    {
        var p = Find(s, s.LivePreset);
        return p is null
            ? new LiveArrangement { Name = DefaultName, Grid = s.LiveGrid, Order = [.. s.LiveOrder], Hidden = [.. s.LiveHidden] }
            : new LiveArrangement { Name = p.Name, Grid = p.Grid, Order = [.. p.Order], Hidden = [.. p.Hidden] };
    }

    public static string ActiveName(AppSettings s) => Find(s, s.LivePreset)?.Name ?? DefaultName;

    public static bool IsDefault(AppSettings s) => Find(s, s.LivePreset) is null;

    /// <summary>Names in menu order: the default first, then the others as created.</summary>
    public static List<string> Names(AppSettings s) => [DefaultName, .. s.LivePresets.Select(p => p.Name)];

    /// <summary>Every arrangement, the default first (copies), for the menu.</summary>
    public static List<LiveArrangement> All(AppSettings s) =>
    [
        new LiveArrangement { Name = DefaultName, Grid = s.LiveGrid, Order = [.. s.LiveOrder], Hidden = [.. s.LiveHidden] },
        .. s.LivePresets.Select(p => new LiveArrangement { Name = p.Name, Grid = p.Grid, Order = [.. p.Order], Hidden = [.. p.Hidden] }),
    ];

    /// <summary>Writes layout, order and hidden cameras into the arrangement in use.</summary>
    public static void SaveActive(AppSettings s, int grid, List<CameraRef> order, List<CameraRef> hidden)
    {
        var p = Find(s, s.LivePreset);
        if (p is null) { s.LiveGrid = grid; s.LiveOrder = order; s.LiveHidden = hidden; }
        else { p.Grid = grid; p.Order = order; p.Hidden = hidden; }
    }

    /// <summary>Makes an arrangement the one in use. False when no such name.</summary>
    public static bool Switch(AppSettings s, string name)
    {
        if (name == DefaultName) { s.LivePreset = ""; return true; }
        var p = Find(s, name);
        if (p is null) return false;
        s.LivePreset = p.Name;
        return true;
    }

    /// <summary>Null when the name is fine, else the message to show. <paramref name="renaming"/> = the arrangement being renamed.</summary>
    public static string? ValidateName(AppSettings s, string? name, bool renaming)
    {
        name = name?.Trim() ?? "";
        if (name.Length == 0) return "Hãy nhập tên cho bộ.";
        if (name.Length > MaxNameLength) return $"Tên dài quá {MaxNameLength} ký tự.";
        var current = ActiveName(s);
        var clash = Names(s).Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)
                                      && !(renaming && string.Equals(n, current, StringComparison.OrdinalIgnoreCase)));
        return clash ? "Tên này đã có, hãy chọn tên khác." : null;
    }

    /// <summary>Saves the arrangement in use as a new one and switches to it. Returns the error, or null.</summary>
    public static string? AddFromActive(AppSettings s, string name)
    {
        var err = ValidateName(s, name, renaming: false);
        if (err is not null) return err;
        var a = Active(s);
        a.Name = name.Trim();
        s.LivePresets.Add(a);
        s.LivePreset = a.Name;
        return null;
    }

    /// <summary>Renames the arrangement in use (never the default). Returns the error, or null.</summary>
    public static string? RenameActive(AppSettings s, string name)
    {
        var p = Find(s, s.LivePreset);
        if (p is null) return "Bộ Mặc định không đổi tên được.";
        var err = ValidateName(s, name, renaming: true);
        if (err is not null) return err;
        p.Name = name.Trim();
        s.LivePreset = p.Name;
        return null;
    }

    /// <summary>Deletes the arrangement in use (never the default) and goes back to the default. Returns what was deleted, or null.</summary>
    public static string? DeleteActive(AppSettings s)
    {
        var p = Find(s, s.LivePreset);
        if (p is null) return null;
        s.LivePresets.Remove(p);
        s.LivePreset = "";
        return p.Name;
    }

    static LiveArrangement? Find(AppSettings s, string name) =>
        string.IsNullOrEmpty(name) ? null : s.LivePresets.FirstOrDefault(p => p.Name == name);
}
