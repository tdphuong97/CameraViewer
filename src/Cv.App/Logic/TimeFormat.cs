using System.Globalization;
using System.Text.RegularExpressions;

namespace Cv.App.Logic;

/// <summary>Vietnamese date, time, duration and size text. Pure logic.</summary>
public static partial class TimeFormat
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    static readonly string[] DayNames = ["Chủ Nhật", "Thứ Hai", "Thứ Ba", "Thứ Tư", "Thứ Năm", "Thứ Sáu", "Thứ Bảy"];

    public static string DayName(DateTime d) => DayNames[(int)d.DayOfWeek];

    /// <summary>"Thứ Bảy 07/11/2026".</summary>
    public static string LongDate(DateTime d) => DayName(d) + " " + d.ToString("dd/MM/yyyy", Inv);

    /// <summary>"07/11/2026".</summary>
    public static string Date(DateTime d) => d.ToString("dd/MM/yyyy", Inv);

    /// <summary>"14:32:05".</summary>
    public static string Clock(DateTime t) => t.ToString("HH:mm:ss", Inv);

    /// <summary>"14:32".</summary>
    public static string HourMinute(DateTime t) => t.ToString("HH:mm", Inv);

    /// <summary>"Hôm nay", "Hôm qua", or the date.</summary>
    public static string RelativeDay(DateTime d, DateTime today)
    {
        var days = (today.Date - d.Date).Days;
        return days switch { 0 => "Hôm nay", 1 => "Hôm qua", _ => DayName(d) + " " + d.ToString("dd/MM", Inv) };
    }

    /// <summary>"45 giây", "5 phút", "2 phút 30 giây", "1 giờ 5 phút".</summary>
    public static string Duration(TimeSpan d)
    {
        if (d < TimeSpan.Zero) d = TimeSpan.Zero;
        var total = (long)Math.Round(d.TotalSeconds);
        long h = total / 3600, m = total % 3600 / 60, s = total % 60;
        if (h > 0) return m > 0 ? $"{h} giờ {m} phút" : $"{h} giờ";
        if (m > 0) return s > 0 ? $"{m} phút {s} giây" : $"{m} phút";
        return $"{s} giây";
    }

    /// <summary>"48,2 MB", "1,3 GB", "820 KB" (Vietnamese decimal comma).</summary>
    public static string Size(long bytes)
    {
        const double K = 1024, M = K * 1024, G = M * 1024;
        string Num(double v) => v.ToString(v >= 100 ? "0" : "0.0", Inv).Replace('.', ',');
        if (bytes >= G) return Num(bytes / G) + " GB";
        if (bytes >= M) return Num(bytes / M) + " MB";
        if (bytes >= K) return Math.Round(bytes / K).ToString(Inv) + " KB";
        return bytes.ToString(Inv) + " B";
    }

    /// <summary>"RAM 286 MB".</summary>
    public static string Ram(long bytes) => "RAM " + (bytes / (1024 * 1024)).ToString(Inv) + " MB";

    /// <summary>
    /// Reads a time of day typed by hand: "14:30", "14:30:15", "1430", "14h30", "14", "9:05". Null when not a valid time.
    /// </summary>
    public static TimeSpan? ParseTimeOfDay(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim().ToLowerInvariant().Replace('h', ':').Replace('.', ':').Replace(' ', ':');
        int h, m = 0, s = 0;
        var m1 = Colon().Match(t);
        if (m1.Success)
        {
            h = int.Parse(m1.Groups[1].Value, Inv);
            if (m1.Groups[2].Success && m1.Groups[2].Value.Length > 0) m = int.Parse(m1.Groups[2].Value, Inv);
            if (m1.Groups[3].Success) s = int.Parse(m1.Groups[3].Value, Inv);
        }
        else if (Digits().IsMatch(t))
        {
            switch (t.Length)
            {
                case <= 2: h = int.Parse(t, Inv); break;
                case 3: h = int.Parse(t[..1], Inv); m = int.Parse(t[1..], Inv); break;
                case 4: h = int.Parse(t[..2], Inv); m = int.Parse(t[2..], Inv); break;
                case 6: h = int.Parse(t[..2], Inv); m = int.Parse(t[2..4], Inv); s = int.Parse(t[4..], Inv); break;
                default: return null;
            }
        }
        else return null;
        if (h is < 0 or > 23 || m is < 0 or > 59 || s is < 0 or > 59) return null;
        return new TimeSpan(h, m, s);
    }

    [GeneratedRegex(@"^(\d{1,2}):(\d{0,2})(?::(\d{1,2}))?$")]
    private static partial Regex Colon();

    [GeneratedRegex(@"^\d+$")]
    private static partial Regex Digits();
}
