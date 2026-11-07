using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Cv.Core.Settings;

/// <summary>
/// JSON settings with DPAPI-protected passwords: {"version":1,"settings":{...},"secrets":{nvrId: base64}}.
/// Thread-safe. SetPassword saves the file at once (with the settings last loaded or saved).
/// </summary>
public sealed class SettingsStore(string filePath) : ISettingsStore
{
    public const int Version = 1;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CameraViewer");

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        // Vietnamese names stay readable in the file (it is local, never embedded in HTML).
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly object _lock = new();
    private JsonNode? _settings;                 // last loaded or saved settings, as JSON (not the caller's live object)
    private Dictionary<string, string>? _secrets; // nvrId -> base64 DPAPI blob

    public string FilePath { get; } = filePath;

    public AppSettings Load()
    {
        lock (_lock)
        {
            var (settings, secrets) = ReadFile();
            _settings = JsonSerializer.SerializeToNode(settings, Json);
            _secrets = secrets;
            return settings;
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_lock)
        {
            EnsureLoaded();
            _settings = JsonSerializer.SerializeToNode(settings, Json);
            WriteFile();
        }
    }

    public string? GetPassword(string nvrId)
    {
        lock (_lock)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(nvrId) || !_secrets!.TryGetValue(nvrId, out var b64)) return null;
            try { return Encoding.UTF8.GetString(Unprotect(Convert.FromBase64String(b64))); }
            catch (Exception e) when (e is CryptographicException or FormatException or PlatformNotSupportedException)
            {
                // Another Windows user or machine: the password must be typed again.
                Log.Warn($"Saved password of recorder {nvrId} cannot be read: {e.Message}");
                return null;
            }
        }
    }

    public void SetPassword(string nvrId, string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(nvrId);
        lock (_lock)
        {
            EnsureLoaded();
            if (password is null) _secrets!.Remove(nvrId);
            else _secrets![nvrId] = Convert.ToBase64String(Protect(Encoding.UTF8.GetBytes(password)));
            WriteFile();
        }
    }

    private void EnsureLoaded()
    {
        if (_settings is not null && _secrets is not null) return;
        var (settings, secrets) = ReadFile();
        _settings = JsonSerializer.SerializeToNode(settings, Json);
        _secrets = secrets;
    }

    private (AppSettings, Dictionary<string, string>) ReadFile()
    {
        var secrets = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(FilePath)) return (Normalize(new AppSettings()), secrets);
        string text;
        try { text = File.ReadAllText(FilePath, Encoding.UTF8); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Settings file unreadable, using defaults", e);
            return (Normalize(new AppSettings()), secrets);
        }
        try
        {
            if (JsonNode.Parse(text) is not JsonObject root) throw new JsonException("Settings root is not an object.");
            var settings = root["settings"]?.Deserialize<AppSettings>(Json) ?? throw new JsonException("No settings.");
            if (root["secrets"] is JsonObject so)
                foreach (var (k, v) in so)
                    if (v is JsonValue jv && jv.TryGetValue<string>(out var s) && !string.IsNullOrEmpty(s)) secrets[k] = s;
            return (Normalize(settings), secrets);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or NotSupportedException or ArgumentException)
        {
            Log.Warn("Settings file is corrupt; kept as settings.json.bad, using defaults", e);
            try { File.Move(FilePath, FilePath + ".bad", overwrite: true); }
            catch (Exception m) when (m is IOException or UnauthorizedAccessException) { Log.Warn("Could not keep the corrupt settings file", m); }
            return (Normalize(new AppSettings()), new Dictionary<string, string>(StringComparer.Ordinal));
        }
    }

    /// <summary>Nulls written by hand or older versions become defaults.</summary>
    private static AppSettings Normalize(AppSettings s)
    {
        s.Nvrs = (s.Nvrs ?? []).Where(n => n is not null).Select(n => n with
        {
            Id = string.IsNullOrWhiteSpace(n.Id) ? Guid.NewGuid().ToString("N") : n.Id,
            Name = n.Name ?? "",
            Host = n.Host ?? "",
            User = n.User ?? "",
        }).ToList();
        s.LiveOrder = (s.LiveOrder ?? []).Where(c => c is not null && c.NvrId is not null).ToList();
        if (string.IsNullOrWhiteSpace(s.ExportFolder)) s.ExportFolder = AppSettings.DefaultExportFolder;
        if (string.IsNullOrWhiteSpace(s.SnapshotFolder)) s.SnapshotFolder = AppSettings.DefaultSnapshotFolder;
        return s;
    }

    /// <summary>Writes a temp file next to the target, flushed to disk, then swaps it in.</summary>
    private void WriteFile()
    {
        var secrets = new JsonObject();
        foreach (var (k, v) in _secrets!.OrderBy(p => p.Key, StringComparer.Ordinal)) secrets[k] = v;
        var root = new JsonObject
        {
            ["version"] = Version,
            ["settings"] = _settings!.DeepClone(),
            ["secrets"] = secrets,
        };
        var full = Path.GetFullPath(FilePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var tmp = full + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var bytes = new UTF8Encoding(false).GetBytes(root.ToJsonString(Json));
            fs.Write(bytes);
            fs.Flush(flushToDisk: true);
        }
        if (File.Exists(full))
        {
            try { File.Replace(tmp, full, null, ignoreMetadataErrors: true); }
            catch (Exception e) when (e is IOException or PlatformNotSupportedException)
            {
                // Some file systems cannot replace; a move with overwrite is still a single rename.
                File.Move(tmp, full, overwrite: true);
            }
        }
        else File.Move(tmp, full);
    }

    private static byte[] Protect(byte[] data) => OperatingSystem.IsWindows()
        ? ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser)
        : throw new PlatformNotSupportedException("DPAPI is Windows only.");

    private static byte[] Unprotect(byte[] data) => OperatingSystem.IsWindows()
        ? ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser)
        : throw new PlatformNotSupportedException("DPAPI is Windows only.");
}
