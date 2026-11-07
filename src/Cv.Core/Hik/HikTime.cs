using System.Globalization;
using System.Text.RegularExpressions;
using Cv.Core.Nvr;

namespace Cv.Core.Hik;

internal enum StampKind { None, Z, Offset }

/// <summary>A time as the recorder wrote it: the wall-clock digits plus what followed them (nothing, "Z" or "+07:00").</summary>
internal readonly record struct HikStamp(DateTime Wall, StampKind Kind, TimeSpan Offset)
{
    /// <summary>Digits read as written; for an explicit offset, the UTC instant (so "+07:00" firmware detects as Utc).</summary>
    public DateTime Raw => Kind == StampKind.Offset ? HikTime.Unspec(Wall - Offset) : Wall;
}

/// <summary>Hikvision time formats and the conversion between device wall clock and what goes on the wire.</summary>
internal static partial class HikTime
{
    public static DateTime Unspec(DateTime t) => DateTime.SpecifyKind(t, DateTimeKind.Unspecified);

    /// <summary>2026-11-07T14:30:00Z (search requests).</summary>
    public static string IsoZ(DateTime t) => t.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>20261107T143000Z (playback URIs).</summary>
    public static string UriZ(DateTime t) => t.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    /// <summary>Device-local wall clock to the value written with a "Z" in the given mode.</summary>
    public static DateTime ToWire(DateTime local, TimeMode mode, TimeSpan offset) =>
        Unspec(mode == TimeMode.Utc ? local - offset : local);

    /// <summary>A time read from the recorder to device-local wall clock.</summary>
    public static DateTime ToLocal(HikStamp s, TimeMode mode, TimeSpan offset) => s.Kind switch
    {
        StampKind.Z => Unspec(mode == TimeMode.Utc ? s.Wall + offset : s.Wall),
        StampKind.Offset => Unspec(s.Wall - s.Offset + offset),
        _ => Unspec(s.Wall),
    };

    /// <summary>Lenient ISO 8601: date, 'T' or space, time, optional fraction, optional Z or ±hh[:mm].</summary>
    public static bool TryParse(string? text, out HikStamp stamp)
    {
        stamp = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var m = IsoRegex().Match(text.Trim());
        if (!m.Success) return false;
        int I(int g) => int.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture);
        DateTime wall;
        try { wall = new DateTime(I(1), I(2), I(3), I(4), I(5), I(6), DateTimeKind.Unspecified); }
        catch (ArgumentOutOfRangeException) { return false; }
        var zone = m.Groups[7].Value;
        if (zone.Length == 0) { stamp = new(wall, StampKind.None, TimeSpan.Zero); return true; }
        if (zone is "Z" or "z") { stamp = new(wall, StampKind.Z, TimeSpan.Zero); return true; }
        var sign = zone[0] == '-' ? -1 : 1;
        var digits = zone[1..].Replace(":", "");
        var hh = int.Parse(digits[..2], CultureInfo.InvariantCulture);
        var mm = digits.Length >= 4 ? int.Parse(digits[2..4], CultureInfo.InvariantCulture) : 0;
        stamp = new(wall, StampKind.Offset, sign * new TimeSpan(hh, mm, 0));
        return true;
    }

    /// <summary>
    /// POSIX TZ ("CST-7:00:00" = UTC+7, "EST5EDT,..." = UTC-5): the sign is inverted; any DST rule is ignored.
    /// </summary>
    public static TimeSpan? ParsePosixOffset(string? tz)
    {
        if (string.IsNullOrWhiteSpace(tz)) return null;
        var m = PosixRegex().Match(tz.Trim());
        if (!m.Success) return null;
        var h = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        var mi = m.Groups[3].Success ? int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) : 0;
        var s = m.Groups[4].Success ? int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture) : 0;
        if (h > 14 || mi > 59 || s > 59) return null;
        var west = new TimeSpan(h, mi, s);
        return m.Groups[1].Value == "-" ? west : -west;
    }

    /// <summary>The PC's offset at that wall-clock time; the fallback when the recorder does not say.</summary>
    public static TimeSpan PcOffset(DateTime wall) => TimeZoneInfo.Local.GetUtcOffset(Unspec(wall));

    [GeneratedRegex(@"^(\d{4})-(\d{1,2})-(\d{1,2})[T ](\d{1,2}):(\d{2}):(\d{2})(?:[.,]\d+)?\s*(Z|z|[+-]\d{2}:?\d{2})?$")]
    private static partial Regex IsoRegex();

    [GeneratedRegex(@"^(?:<[^>]*>|[A-Za-z]+)([+-]?)(\d{1,2})(?::(\d{1,2}))?(?::(\d{1,2}))?")]
    private static partial Regex PosixRegex();
}
