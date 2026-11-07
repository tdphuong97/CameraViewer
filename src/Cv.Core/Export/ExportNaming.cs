using System.Globalization;

namespace Cv.Core.Export;

/// <summary>Export file names: "{CameraName}_{yyyy-MM-dd}_{HH'h'mm}-{HH'h'mm}" (+ "mm'm'ss" when a bound has seconds).</summary>
public static class ExportNaming
{
    private const int MaxCameraChars = 80;
    private static readonly HashSet<char> Invalid = [.. Path.GetInvalidFileNameChars()];

    /// <summary>The file name without folder, " (n)" suffix or extension.</summary>
    public static string BaseName(string? cameraName, int channel, DateTime from, DateTime to)
    {
        var cam = Sanitize(cameraName);
        if (cam.Length == 0) cam = $"Camera {channel:00}";
        if (cam.Length > MaxCameraChars) cam = cam[..MaxCameraChars].TrimEnd(' ', '.');
        // Both bounds get seconds when either has them, so the two times read alike.
        var seconds = from.Second != 0 || to.Second != 0;
        string T(DateTime t) => t.ToString(seconds ? "HH'h'mm'm'ss" : "HH'h'mm", CultureInfo.InvariantCulture);
        return $"{cam}_{from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}_{T(from)}-{T(to)}";
    }

    /// <summary>Characters Windows refuses in file names become "_"; leading/trailing spaces and dots go.</summary>
    public static string Sanitize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var chars = name.Select(c => Invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars).Trim().Trim('.', ' ');
    }
}
