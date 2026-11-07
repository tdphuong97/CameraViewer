using System.Globalization;

namespace Cv.Core;

/// <summary>Vietnamese date, time and size texts for the UI.</summary>
public static class TimeText
{
    private static readonly string[] Days = ["Chủ Nhật", "Thứ Hai", "Thứ Ba", "Thứ Tư", "Thứ Năm", "Thứ Sáu", "Thứ Bảy"];
    // Vietnamese number style without depending on installed culture data.
    private static readonly NumberFormatInfo Vi = new() { NumberDecimalSeparator = ",", NumberGroupSeparator = "." };

    /// <summary>"Thứ Bảy".</summary>
    public static string DayName(DateTime d) => Days[(int)d.DayOfWeek];

    /// <summary>"07/11/2026".</summary>
    public static string Date(DateTime d) => d.ToString("dd'/'MM'/'yyyy", CultureInfo.InvariantCulture);

    /// <summary>"14:32:05 — Thứ Bảy 07/11/2026" (the playback clock).</summary>
    public static string Clock(DateTime t) =>
        $"{t.ToString("HH:mm:ss", CultureInfo.InvariantCulture)} — {DayName(t)} {Date(t)}";

    /// <summary>"512 KB", "12,3 MB", "1,25 GB".</summary>
    public static string Size(long bytes)
    {
        if (bytes < 1024 * 1024) return Math.Max(1, (bytes + 1023) / 1024).ToString(Vi) + " KB";
        if (bytes < 1024L * 1024 * 1024) return (bytes / 1048576.0).ToString("0.#", Vi) + " MB";
        return (bytes / 1073741824.0).ToString("0.##", Vi) + " GB";
    }
}
