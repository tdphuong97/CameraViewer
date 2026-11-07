using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace Cv.Core.Update;

/// <summary>A newer version, as the newest release's latest.json describes it.</summary>
public sealed class UpdateInfo
{
    public string Version { get; init; } = "";
    public string? Notes { get; init; }
    /// <summary>The release page, for reading what changed or downloading by hand.</summary>
    public string? Page { get; init; }
    /// <summary>The installer for this system; null where there is none.</summary>
    public UpdatePackage? Package { get; init; }
}

public sealed class UpdatePackage
{
    public string Url { get; init; } = "";
    public long Size { get; init; }
    public string Sha256 { get; init; } = "";
}

public sealed class UpdateException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// New versions are GitHub releases. Each carries latest.json naming the installer for each system with
/// its size and SHA-256; the "latest/download" address always points at the newest release's copy.
/// </summary>
public sealed class Updater(HttpClient http, string feedUrl, TimeProvider? time = null)
{
    /// <summary>The GitHub repository the releases are published to.</summary>
    public const string Repo = "tdphuong97/CameraViewer";
    public const string FeedEnvVar = "CV_UPDATE_FEED";
    public static readonly string DefaultFeed = $"https://github.com/{Repo}/releases/latest/download/latest.json";

    /// <summary>The feed to use: the CV_UPDATE_FEED environment variable if set, otherwise the GitHub release.</summary>
    public static string ResolveFeed()
    {
        var v = Environment.GetEnvironmentVariable(FeedEnvVar);
        return string.IsNullOrWhiteSpace(v) ? DefaultFeed : v;
    }

    /// <summary>Adds ?t=unix-seconds (or &amp;t= when the URL already has a query) so caches never serve an old feed.</summary>
    public static string WithCacheBuster(string url, TimeProvider? time = null)
    {
        var t = (time ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var hash = url.IndexOf('#');
        var main = hash >= 0 ? url[..hash] : url;
        var frag = hash >= 0 ? url[hash..] : "";
        return main + (main.Contains('?') ? "&" : "?") + "t=" + t + frag;
    }

    public async Task<UpdateInfo?> CheckAsync(string currentVersion, string platform, CancellationToken ct)
    {
        JsonElement root;
        try
        {
            using var res = await http.GetAsync(WithCacheBuster(feedUrl, time), ct);
            if (!res.IsSuccessStatusCode) throw new UpdateException($"Không kiểm tra được bản mới (máy chủ trả lời {(int)res.StatusCode}).");
            root = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct)).RootElement.Clone();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            throw new UpdateException("Không kiểm tra được bản mới. Hãy thử lại khi có mạng.", ex);
        }

        var version = Text(root, "version");
        if (version is null || !IsNewer(version, currentVersion)) return null;
        UpdatePackage? package = null;
        if (root.TryGetProperty(platform, out var p) && Text(p, "url") is { } url && Text(p, "sha256") is { } sha)
            package = new UpdatePackage { Url = url, Sha256 = sha, Size = p.TryGetProperty("size", out var size) ? size.GetInt64() : 0 };
        return new UpdateInfo { Version = version, Notes = Text(root, "notes"), Page = Text(root, "page"), Package = package };
    }

    static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>1.10.0 is newer than 1.9.3: versions compare part by part, as numbers.</summary>
    public static bool IsNewer(string candidate, string current) =>
        System.Version.TryParse(candidate, out var a) && System.Version.TryParse(current, out var b) && Normal(a) > Normal(b);

    static Version Normal(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

    /// <summary>
    /// Downloads the installer into <paramref name="folder"/> and returns its path, once its size and
    /// SHA-256 match latest.json; anything else is deleted. Progress goes from 0 to 1.
    /// </summary>
    public async Task<string> DownloadAsync(UpdateInfo info, string folder, Action<double>? progress, CancellationToken ct)
    {
        var package = info.Package ?? throw new UpdateException("Bản mới chưa có bộ cài cho máy này.");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"CameraViewer-Setup-{info.Version}.exe");
        var part = path + ".part";
        try
        {
            using (var res = await http.GetAsync(package.Url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                if (!res.IsSuccessStatusCode) throw new UpdateException($"Không tải được bản mới (máy chủ trả lời {(int)res.StatusCode}).");
                var total = package.Size > 0 ? package.Size : res.Content.Headers.ContentLength ?? 0;
                await using var input = await res.Content.ReadAsStreamAsync(ct);
                await using var output = File.Create(part);
                var buffer = new byte[256 * 1024];
                long done = 0;
                int n;
                while ((n = await input.ReadAsync(buffer, ct)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, n), ct);
                    done += n;
                    if (total > 0) progress?.Invoke(Math.Min(0.99, (double)done / total));
                }
            }

            await using (var check = File.OpenRead(part))
            {
                if (package.Size > 0 && check.Length != package.Size)
                    throw new UpdateException("Bản mới tải về không đủ, hãy thử lại.");
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(check, ct));
                if (!hash.Equals(package.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new UpdateException("Bản mới tải về bị hỏng hoặc không đúng, phần mềm đã bỏ nó đi.");
            }
            File.Move(part, path, overwrite: true);
            progress?.Invoke(1);
            return path;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            try { File.Delete(part); } catch { }
            throw ex as UpdateException ?? new UpdateException("Không tải được bản mới. Hãy thử lại khi có mạng.", ex);
        }
        catch (OperationCanceledException)
        {
            try { File.Delete(part); } catch { }
            throw;
        }
    }
}
