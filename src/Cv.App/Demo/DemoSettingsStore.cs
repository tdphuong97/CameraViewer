using System.IO;
using System.Text.Json;
using Cv.Core;
using Cv.Core.Nvr;
using Cv.Core.Settings;

namespace Cv.App.Demo;

/// <summary>
/// Demo settings: plain JSON in AppPaths.Data\settings-demo.json (never the real settings file), one fake recorder
/// preconfigured, passwords kept in memory only.
/// </summary>
internal sealed class DemoSettingsStore : ISettingsStore
{
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    readonly Dictionary<string, string> _passwords = [];

    public string FilePath { get; } = Path.Combine(AppPaths.Data, "settings-demo.json");

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) is { } s) return s;
        }
        catch (Exception ex)
        {
            Log.Warn("Demo settings unreadable, using defaults", ex);
        }
        return new AppSettings
        {
            Nvrs = [new NvrConfig { Id = "demo", Name = "Đầu ghi", Host = "192.168.1.64", User = "admin" }],
        };
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Json));
        File.Move(tmp, FilePath, overwrite: true);
    }

    public string? GetPassword(string nvrId) => _passwords.TryGetValue(nvrId, out var p) ? p : "demo";

    public void SetPassword(string nvrId, string password) => _passwords[nvrId] = password;
}
