using Cv.Core.Nvr;

namespace Cv.Core.Hik;

internal sealed partial class HikNvrClient
{
    internal const int SearchPageSize = 50;
    internal const int SearchMaxPages = 200;
    private static readonly TimeSpan ModeTolerance = TimeSpan.FromMinutes(15);
    private const string SearchPath = "/ISAPI/ContentMgmt/search";

    private volatile int _resolvedMode; // TimeMode; 0 (Auto) = not detected yet
    private readonly SemaphoreSlim _resolveGate = new(1, 1);

    /// <summary>The mode used right now: the configured one, else the detected one, else LocalWithZ.</summary>
    private TimeMode CurrentMode =>
        Config.TimeMode != TimeMode.Auto ? Config.TimeMode
        : _resolvedMode != 0 ? (TimeMode)_resolvedMode
        : TimeMode.LocalWithZ;

    public async Task<TimeMode> ResolveTimeModeAsync(CancellationToken ct)
    {
        if (Config.TimeMode != TimeMode.Auto)
        {
            // Utc needs the device offset for every conversion, PlaybackUrl included.
            if (Config.TimeMode == TimeMode.Utc) await OffsetAsync(ct);
            return Config.TimeMode;
        }
        if (_resolvedMode != 0) return (TimeMode)_resolvedMode;
        await _resolveGate.WaitAsync(ct);
        try
        {
            if (_resolvedMode != 0) return (TimeMode)_resolvedMode;
            var mode = await DetectTimeModeAsync(ct);
            _resolvedMode = (int)mode;
            return mode;
        }
        finally { _resolveGate.Release(); }
    }

    /// <summary>
    /// Reads the clock, then the latest raw segment end of up to 3 online channels over device now ± 1 day (wide
    /// enough for either reading). Near device local now: LocalWithZ; near it once the offset is added: Utc;
    /// otherwise LocalWithZ (the common firmware). Network and login errors propagate and nothing is cached.
    /// </summary>
    private async Task<TimeMode> DetectTimeModeAsync(CancellationToken ct)
    {
        DeviceClock clock;
        IReadOnlyList<ChannelInfo> channels;
        try
        {
            clock = await GetClockAsync(ct);
            channels = await GetChannelsAsync(ct);
        }
        catch (NvrException e) when (!IsFatal(e))
        {
            Log.Warn("Time mode: clock or channels unavailable, using LocalWithZ: " + e.Message);
            return TimeMode.LocalWithZ;
        }

        var start = HikTime.IsoZ(clock.LocalNow.AddDays(-1));
        var end = HikTime.IsoZ(clock.LocalNow.AddDays(1));
        DateTime? latest = null;
        foreach (var ch in channels.Where(c => c.Online).Take(3))
        {
            List<RawSearchItem> items;
            try { items = await SearchRawAsync(TrackId(ch.No, StreamKind.Main), start, end, ct); }
            catch (NvrException e) when (!IsFatal(e)) { Log.Warn($"Time mode: search ch{ch.No} failed: {e.Message}"); continue; }
            foreach (var i in items)
                if (HikTime.TryParse(i.End, out var s) && (latest is null || s.Raw > latest)) latest = s.Raw;
            if (latest is { } l && Decide(l, clock) is { } decided) return Logged(decided, l, clock);
        }
        return Logged(TimeMode.LocalWithZ, latest, clock);
    }

    private static TimeMode? Decide(DateTime rawEnd, DeviceClock clock)
    {
        if ((rawEnd - clock.LocalNow).Duration() <= ModeTolerance) return TimeMode.LocalWithZ;
        if ((rawEnd + clock.UtcOffset - clock.LocalNow).Duration() <= ModeTolerance) return TimeMode.Utc;
        return null;
    }

    private TimeMode Logged(TimeMode mode, DateTime? latest, DeviceClock clock)
    {
        Log.Info($"Time mode {mode} for {DisplayHost} (latest raw segment end {latest:yyyy-MM-dd HH:mm:ss}, device now {clock.LocalNow:yyyy-MM-dd HH:mm:ss}, offset {clock.UtcOffset})");
        return mode;
    }

    public async Task<IReadOnlyList<RecordSegment>> SearchAsync(int channel, DateTime from, DateTime to, CancellationToken ct, StreamKind kind = StreamKind.Main)
    {
        var mode = await ResolveTimeModeAsync(ct);
        var offset = mode == TimeMode.Utc ? await OffsetAsync(ct) : TimeSpan.Zero;
        var items = await SearchRawAsync(TrackId(channel, kind),
            HikTime.IsoZ(HikTime.ToWire(from, mode, offset)), HikTime.IsoZ(HikTime.ToWire(to, mode, offset)), ct);

        var parsed = new List<(HikStamp S, HikStamp E, string Uri)>(items.Count);
        foreach (var i in items)
            if (HikTime.TryParse(i.Start, out var s) && HikTime.TryParse(i.End, out var e))
                parsed.Add((s, e, i.PlaybackUri ?? ""));
        // Explicit offsets convert to device local, which needs the device offset.
        if (mode != TimeMode.Utc && parsed.Any(p => p.S.Kind == StampKind.Offset || p.E.Kind == StampKind.Offset))
            offset = await OffsetAsync(ct);

        return parsed
            .Select(p => new RecordSegment(HikTime.ToLocal(p.S, mode, offset), HikTime.ToLocal(p.E, mode, offset), p.Uri))
            .Distinct()
            .OrderBy(s => s.Start).ThenBy(s => s.End)
            .ToList();
    }

    /// <summary>
    /// All pages of one search: same searchID, searchResultPostion += numOfMatches while the answer says MORE
    /// (at most SearchMaxPages pages). "NO MATCHES" ends the search.
    /// </summary>
    private async Task<List<RawSearchItem>> SearchRawAsync(int trackId, string start, string end, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("D").ToUpperInvariant();
        var all = new List<RawSearchItem>();
        var position = 0;
        for (var page = 0; ; page++)
        {
            if (page >= SearchMaxPages)
            {
                Log.Warn($"Search track {trackId}: stopped after {SearchMaxPages} pages");
                break;
            }
            var body = HikXml.BuildSearchRequest(id, trackId, start, end, SearchPageSize, position);
            var root = HikXml.TryParse(await SendAsync(HttpMethod.Post, SearchPath, body, ct))
                       ?? throw new NvrException(HikErrors.Unreadable);
            var result = HikXml.ParseSearchResult(root);
            if (string.Equals(result.Status, "NO MATCHES", StringComparison.OrdinalIgnoreCase)) break;
            all.AddRange(result.Items);
            if (!string.Equals(result.Status, "MORE", StringComparison.OrdinalIgnoreCase)) break;
            var n = result.NumOfMatches > 0 ? result.NumOfMatches : result.Items.Count;
            if (n <= 0) break;
            position += n;
        }
        return all;
    }
}
