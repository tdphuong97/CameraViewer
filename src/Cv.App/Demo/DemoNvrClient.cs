using System.Globalization;
using Cv.Core.Nvr;

namespace Cv.App.Demo;

internal sealed class DemoNvrClientFactory : INvrClientFactory
{
    public INvrClient Create(NvrConfig config, string password) => new DemoNvrClient(config);
}

/// <summary>
/// A recorder that does not exist: 17 cameras, channel 5 offline, 1-hour recordings with a daily gap 02:00–02:30 and a
/// few random gaps per camera and day. URLs use the demo:// scheme understood by DemoVideoSourceFactory.
/// </summary>
internal sealed class DemoNvrClient(NvrConfig config) : INvrClient
{
    const string Stamp = "yyyyMMdd'T'HHmmss";
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public NvrConfig Config { get; } = config;

    public async Task<NvrDeviceInfo> GetDeviceInfoAsync(CancellationToken ct)
    {
        await Task.Delay(250, ct);
        return new NvrDeviceInfo("DS-7732NI-M4", "DEMO0000000000000000", "V4.71.120", "Network Video Recorder");
    }

    public Task<DeviceClock> GetClockAsync(CancellationToken ct)
    {
        var now = DateTime.Now;
        return Task.FromResult(new DeviceClock(DateTime.SpecifyKind(now, DateTimeKind.Unspecified), TimeZoneInfo.Local.GetUtcOffset(now)));
    }

    public async Task<IReadOnlyList<ChannelInfo>> GetChannelsAsync(CancellationToken ct)
    {
        await Task.Delay(200, ct);
        var list = new List<ChannelInfo>();
        for (var ch = 1; ch <= DemoData.CameraCount; ch++)
            list.Add(new ChannelInfo(ch, DemoData.Name(ch), ch != DemoData.OfflineChannel, $"192.168.254.{ch + 1}", "DS-2CD1043G2-LIU"));
        return list;
    }

    public async Task<IReadOnlyList<RecordSegment>> SearchAsync(int channel, DateTime from, DateTime to, CancellationToken ct, StreamKind kind = StreamKind.Main)
    {
        await Task.Delay(150, ct);
        return Segments(channel, from, to, DateTime.Now);
    }

    /// <summary>The fake recordings of a channel overlapping [from, to), never past <paramref name="now"/>.</summary>
    internal static List<RecordSegment> Segments(int channel, DateTime from, DateTime to, DateTime now)
    {
        var result = new List<RecordSegment>();
        if (to > now) to = now;
        var hour = from.Date.AddHours(from.Hour);
        for (; hour < to; hour = hour.AddHours(1))
        {
            var pieces = new List<(DateTime S, DateTime E)> { (hour, hour.AddHours(1)) };
            foreach (var gap in Gaps(channel, hour.Date, now)) pieces = Subtract(pieces, gap);
            foreach (var (s, e0) in pieces)
            {
                var e = e0 > now ? now : e0;
                if (e <= s || e <= from || s >= to) continue;
                var uri = $"rtsp://demo/Streaming/tracks/{channel}01/?starttime={s.ToString(Stamp, Inv)}Z&endtime={e.ToString(Stamp, Inv)}Z";
                result.Add(new RecordSegment(s, e, uri));
            }
        }
        return result;
    }

    static IEnumerable<(DateTime S, DateTime E)> Gaps(int channel, DateTime day, DateTime now)
    {
        yield return (day.AddHours(2), day.AddHours(2.5));
        var rnd = new Random(channel * 7919 + day.DayOfYear * 31 + day.Year);
        for (var i = 0; i < 2; i++)
        {
            var start = day.AddHours(rnd.Next(5, 22)).AddMinutes(rnd.Next(0, 60));
            yield return (start, start.AddMinutes(rnd.Next(5, 41)));
        }
        if (channel == DemoData.OfflineChannel) yield return (now.AddHours(-24), now.AddDays(1));
    }

    static List<(DateTime, DateTime)> Subtract(List<(DateTime S, DateTime E)> pieces, (DateTime S, DateTime E) gap)
    {
        var r = new List<(DateTime, DateTime)>();
        foreach (var (s, e) in pieces)
        {
            if (gap.E <= s || gap.S >= e) { r.Add((s, e)); continue; }
            if (gap.S > s) r.Add((s, gap.S));
            if (gap.E < e) r.Add((gap.E, e));
        }
        return r;
    }

    public Task<IReadOnlyList<DownloadedPart>> DownloadAsync(int channel, DateTime from, DateTime to, string folder, IProgress<long>? bytes, CancellationToken ct) =>
        throw new NvrDownloadUnsupportedException("Bản thử không tải được video thật.");

    public string LiveUrl(int channel, StreamKind kind) => $"demo://live/{channel}/{(int)kind}";

    public string PlaybackUrl(int channel, DateTime from, DateTime? to = null, StreamKind kind = StreamKind.Main) =>
        $"demo://playback/{channel}/{(int)kind}?from={from.ToString(Stamp, Inv)}&to={(to ?? from.AddHours(1)).ToString(Stamp, Inv)}";

    public Task<TimeMode> ResolveTimeModeAsync(CancellationToken ct) => Task.FromResult(TimeMode.LocalWithZ);

    public void Dispose() { }
}
