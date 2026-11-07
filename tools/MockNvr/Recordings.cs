using System.Globalization;

namespace MockNvr;

/// <summary>A recorded file on the fake disk. Start/End are device-local wall clock.</summary>
public sealed record Segment(int Channel, DateTime Start, DateTime End)
{
    private static readonly DateTime NameEpoch = new(2000, 1, 1);

    public TimeSpan Duration => End - Start;

    /// <summary>17 digits like Hikvision file names: channel (2) + seconds since 2000-01-01 local (15).</summary>
    public string Name => string.Create(CultureInfo.InvariantCulture, $"{Channel:D2}{(long)(Start - NameEpoch).TotalSeconds:D15}");

    /// <summary>File size as the recorder reports it (main stream bit rate × duration).</summary>
    public long Size => (long)(Duration.TotalSeconds * RecordingStore.MainKbps * 1000 / 8);

    public static bool TryParseName(string? name, out int channel, out DateTime start)
    {
        channel = 0; start = default;
        if (name is null || name.Length != 17 || !name.All(char.IsAsciiDigit)) return false;
        channel = int.Parse(name.AsSpan(0, 2), CultureInfo.InvariantCulture);
        start = NameEpoch.AddSeconds(long.Parse(name.AsSpan(2), CultureInfo.InvariantCulture));
        return true;
    }
}

/// <summary>
/// Deterministic fake recordings: the last 7 days in ~1-hour files (boundaries a little after each hour, contiguous),
/// a daily gap 02:00–02:30, 0–2 random gaps a day per channel, the current file ending now, and the offline channel
/// without anything in the last 24 hours.
/// </summary>
public sealed class RecordingStore(MockClock clock)
{
    public const int RetentionDays = 7;
    public const int MainKbps = 1000;

    /// <summary>Segments of the channel overlapping [from, to), sorted, not clipped. Empty for unknown channels.</summary>
    public IReadOnlyList<Segment> Find(int channel, DateTime from, DateTime to)
    {
        if (MockDevice.Channel(channel) is null) return [];
        var now = TruncateToSecond(clock.LocalNow);
        var end = channel == MockDevice.OfflineChannel ? now.AddHours(-24) : now;
        var oldest = now.AddDays(-RetentionDays);
        if (to > end) to = end;
        if (to <= from) return [];

        var list = new List<Segment>();
        for (var h = FloorHour(from).AddHours(-1); h < to; h = h.AddHours(1))
        {
            var s = Boundary(channel, h);
            var e = Boundary(channel, h.AddHours(1));
            if (e <= oldest) continue;
            if (e > end) e = end;
            if (s >= e) continue;
            foreach (var (ps, pe) in Subtract(channel, s, e))
                if (pe > from && ps < to && pe - ps >= TimeSpan.FromSeconds(1))
                    list.Add(new Segment(channel, ps, pe));
        }
        return list;
    }

    /// <summary>The segment a playbackURI's name= points at, with its current end (the newest file is still growing).</summary>
    public Segment? FindByName(string? name)
    {
        if (!Segment.TryParseName(name, out var ch, out var start)) return null;
        return Find(ch, start, start.AddSeconds(1)).FirstOrDefault(s => s.Start == start);
    }

    /// <summary>Files start 0–179 s after the hour, per channel and hour, so boundaries look like real size-based splits.</summary>
    private static DateTime Boundary(int channel, DateTime hour) =>
        hour.AddSeconds(Hash(channel, hour.Ticks / TimeSpan.TicksPerHour) % 180);

    private static IEnumerable<(DateTime, DateTime)> Subtract(int channel, DateTime s, DateTime e)
    {
        var gaps = new List<(DateTime Start, DateTime End)>();
        for (var d = s.Date.AddDays(-1); d <= e.Date; d = d.AddDays(1)) gaps.AddRange(Gaps(channel, d));
        gaps.Sort();
        var cur = s;
        foreach (var (gs, ge) in gaps)
        {
            if (ge <= cur || gs >= e) continue;
            if (gs > cur) yield return (cur, gs);
            cur = ge > cur ? ge : cur;
            if (cur >= e) yield break;
        }
        if (cur < e) yield return (cur, e);
    }

    /// <summary>The daily 02:00–02:30 gap plus 0–2 random 3–20 minute gaps between 06:00 and 22:00.</summary>
    internal static IEnumerable<(DateTime Start, DateTime End)> Gaps(int channel, DateTime day)
    {
        yield return (day.AddHours(2), day.AddHours(2).AddMinutes(30));
        var rng = new Random((int)(Hash(channel, day.Ticks / TimeSpan.TicksPerDay) & 0x7FFFFFFF));
        var n = rng.Next(0, 3);
        for (var i = 0; i < n; i++)
        {
            var start = day.AddHours(6).AddMinutes(rng.Next(0, 16 * 60)).AddSeconds(rng.Next(0, 60));
            yield return (start, start.AddMinutes(rng.Next(3, 21)).AddSeconds(rng.Next(0, 60)));
        }
    }

    private static ulong Hash(int a, long b)
    {
        // splitmix64: stable across runs and platforms (unlike string.GetHashCode).
        var z = (ulong)b * 0x9E3779B97F4A7C15UL + (ulong)a * 0xBF58476D1CE4E5B9UL + 0x94D049BB133111EBUL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    private static DateTime FloorHour(DateTime t) => new(t.Year, t.Month, t.Day, t.Hour, 0, 0, t.Kind);

    private static DateTime TruncateToSecond(DateTime t) => new(t.Ticks - t.Ticks % TimeSpan.TicksPerSecond, t.Kind);
}
