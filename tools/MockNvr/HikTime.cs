using System.Globalization;
using System.Text.RegularExpressions;

namespace MockNvr;

/// <summary>Reads and writes times the way the configured firmware does. All DateTime values here are device-local.</summary>
public sealed partial class HikTime(MockClock clock, TimeWriteMode mode)
{
    public TimeWriteMode Mode => mode;

    /// <summary>Search results: "2026-11-07T10:00:00Z" (local or UTC) or "2026-11-07T10:00:00+07:00".</summary>
    public string FormatIso(DateTime local) => mode switch
    {
        TimeWriteMode.Utc => clock.ToUtc(local).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
        TimeWriteMode.Offset => local.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + clock.OffsetText,
        _ => local.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
    };

    /// <summary>Playback URIs: "20261107T100000Z"; local wall clock in LocalZ mode, real UTC otherwise.</summary>
    public string FormatCompact(DateTime local) =>
        (mode == TimeWriteMode.LocalZ ? local : clock.ToUtc(local)).ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// Lenient parse of an ISO or compact time. An explicit offset is honored; "Z" is local wall clock for LocalZ
    /// firmware and real UTC for the time-zone aware ones (Utc, Offset); no designator = local.
    /// </summary>
    public bool TryParse(string? text, out DateTime local)
    {
        local = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var m = TimeRegex().Match(text.Trim());
        if (!m.Success) return false;
        try
        {
            var t = new DateTime(
                int.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups["mo"].Value, CultureInfo.InvariantCulture),
                int.Parse(m.Groups["d"].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture),
                int.Parse(m.Groups["mi"].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture),
                DateTimeKind.Unspecified);
            var zone = m.Groups["z"].Value;
            if (zone is "Z" or "z")
                local = mode == TimeWriteMode.LocalZ ? t : clock.ToLocal(t);
            else if (zone.Length > 0)
            {
                var offset = MockOptions.ParseOffset(zone);
                local = DateTime.SpecifyKind(t - offset + clock.Offset, DateTimeKind.Unspecified);
            }
            else local = t;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
        {
            return false;
        }
    }

    [GeneratedRegex(@"^(?<y>\d{4})-?(?<mo>\d{2})-?(?<d>\d{2})[T ](?<h>\d{2}):?(?<mi>\d{2}):?(?<s>\d{2})(?:\.\d+)?(?<z>Z|[+-]\d{2}:?\d{2})?$", RegexOptions.IgnoreCase)]
    private static partial Regex TimeRegex();
}
