// FROZEN CONTRACT. Only the lead changes this file; tracks report needed changes instead of editing it.
using Cv.Core.Nvr;
using Cv.Core.Video;

namespace Cv.Core.Settings;

/// <summary>A camera on a recorder.</summary>
public sealed record CameraRef(string NvrId, int Channel);

/// <summary>A named live arrangement: layout code (see AppSettings.LiveGrid), camera order and hidden cameras.</summary>
public sealed class LiveArrangement
{
    public string Name { get; set; } = "";
    public int Grid { get; set; }
    public List<CameraRef> Order { get; set; } = [];
    public List<CameraRef> Hidden { get; set; } = [];
}

/// <summary>Everything the app remembers. Stored as JSON (System.Text.Json, camelCase, indented) in AppPaths.SettingsFile.</summary>
public sealed class AppSettings
{
    /// <summary>v1 UI manages exactly one recorder (the first); the list keeps the door open.</summary>
    public List<NvrConfig> Nvrs { get; set; } = [];

    /// <summary>Where exports go. Default: %USERPROFILE%\Videos\Camera.</summary>
    public string ExportFolder { get; set; } = DefaultExportFolder;

    /// <summary>Where snapshots go. Default: %USERPROFILE%\Pictures\Camera.</summary>
    public string SnapshotFolder { get; set; } = DefaultSnapshotFolder;

    public HwDecodeMode HwDecode { get; set; } = HwDecodeMode.Auto;

    /// <summary>Stop every stream while the window is minimized (saves RAM, CPU and network).</summary>
    public bool PauseWhenMinimized { get; set; } = true;

    public bool CheckUpdates { get; set; } = true;

    /// <summary>Name of the arrangement kept in LiveGrid / LiveOrder / LiveHidden (always present, cannot be renamed or deleted).</summary>
    public const string DefaultArrangementName = "Mặc định";

    /// <summary>
    /// Live layout code: 0 = automatic (smallest grid that fits every camera, max 25); 1, 4, 6, 8, 9, 12, 16, 20 or 25 =
    /// that many equal tiles; 105, 107 or 112 = one big tile plus 5, 7 or 12 small ones. Unknown codes count as automatic.
    /// </summary>
    public int LiveGrid { get; set; }

    /// <summary>Camera order in the live grid. Empty = by channel number. Unknown cameras are skipped, new ones appended.</summary>
    public List<CameraRef> LiveOrder { get; set; } = [];

    /// <summary>Cameras left out of the live grid (they stay available in Xem lại).</summary>
    public List<CameraRef> LiveHidden { get; set; } = [];

    /// <summary>Extra named arrangements ("Bộ"); the default one is LiveGrid / LiveOrder / LiveHidden above.</summary>
    public List<LiveArrangement> LivePresets { get; set; } = [];

    /// <summary>Name of the arrangement in use; empty (or a name that no longer exists) = the default one.</summary>
    public string LivePreset { get; set; } = "";

    /// <summary>Main window bounds and state, restored on start; null = centered default.</summary>
    public WindowBounds? Window { get; set; }

    public static string DefaultExportFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Camera");

    public static string DefaultSnapshotFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Camera");
}

public sealed record WindowBounds(double Left, double Top, double Width, double Height, bool Maximized);

/// <summary>
/// Loads and saves AppSettings; keeps recorder passwords DPAPI-protected (CurrentUser) inside the same file.
/// Save writes a temp file and replaces atomically. A missing or corrupt file loads as defaults (the corrupt one is
/// kept as settings.json.bad).
/// </summary>
public interface ISettingsStore
{
    AppSettings Load();
    void Save(AppSettings settings);
    string? GetPassword(string nvrId);
    void SetPassword(string nvrId, string password);
}
