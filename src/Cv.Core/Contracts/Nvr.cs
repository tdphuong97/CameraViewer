// FROZEN CONTRACT. Only the lead changes this file; tracks report needed changes instead of editing it.
namespace Cv.Core.Nvr;

/// <summary>Main = high resolution (single view, export); Sub = low resolution (grid tiles).</summary>
public enum StreamKind { Main = 1, Sub = 2 }

/// <summary>
/// How the recorder writes times in ISAPI search results and playback URIs.
/// Most Hikvision NVR firmware writes the device's local wall clock with a misleading "Z" (LocalWithZ);
/// some newer firmware writes real UTC. Auto = detect once per connection (see INvrClient.ResolveTimeModeAsync).
/// </summary>
public enum TimeMode { Auto = 0, LocalWithZ = 1, Utc = 2 }

/// <summary>A recorder the app talks to. The password is not here: ISettingsStore keeps it DPAPI-protected.</summary>
public sealed record NvrConfig
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "Đầu ghi";
    public string Host { get; init; } = "";
    public int HttpPort { get; init; } = 80;
    public int RtspPort { get; init; } = 554;
    public bool UseHttps { get; init; }
    public string User { get; init; } = "admin";
    public TimeMode TimeMode { get; init; } = TimeMode.Auto;
}

public sealed record NvrDeviceInfo(string Model, string SerialNumber, string Firmware, string DeviceName);

/// <summary>The recorder's clock. LocalNow has DateTimeKind.Unspecified (device wall clock).</summary>
public sealed record DeviceClock(DateTime LocalNow, TimeSpan UtcOffset);

/// <summary>An IP channel of the recorder. No is 1-based (D1 = 1); RTSP ids are No*100+1 (main) and No*100+2 (sub).</summary>
public sealed record ChannelInfo(int No, string Name, bool Online, string? CameraIp = null, string? CameraModel = null);

/// <summary>
/// One recorded piece as the recorder lists it. Start/End are device-local wall clock (Kind Unspecified),
/// already converted from whatever the firmware wrote. PlaybackUri is the recorder's own rtsp URI for this piece
/// (without credentials), used for segment-based download.
/// </summary>
public sealed record RecordSegment(DateTime Start, DateTime End, string PlaybackUri);

/// <summary>A continuous recorded span: contiguous segments merged (see RecordingTimeline.Merge).</summary>
public readonly record struct TimeRange(DateTime Start, DateTime End)
{
    public TimeSpan Duration => End - Start;
    public bool Contains(DateTime t) => t >= Start && t < End;
}

public enum DownloadMethod
{
    /// <summary>ISAPI download by time range; the recorder cut the range itself.</summary>
    ByTime,
    /// <summary>ISAPI download of a segment's playbackURI with starttime/endtime narrowed to the request.</summary>
    BySegmentRange,
    /// <summary>ISAPI download of a whole segment file; the export must trim it.</summary>
    BySegmentWhole,
    /// <summary>Real-time recording of the RTSP playback stream (slowest fallback, done by IMediaTools.RecordAsync).</summary>
    RtspRecord,
}

/// <summary>
/// A file written by INvrClient.DownloadAsync. NominalStart is the device-local time the file is expected to begin
/// at: the requested From for ByTime/BySegmentRange, the segment Start for BySegmentWhole. The export compares the
/// file's real duration with (CoveredTo - NominalStart) to decide whether the recorder honored the range.
/// </summary>
public sealed record DownloadedPart(string FilePath, DateTime NominalStart, DateTime CoveredFrom, DateTime CoveredTo, DownloadMethod Method);

/// <summary>Base of every recorder error. Message is Vietnamese, written for a non-technical user.</summary>
public class NvrException(string message, Exception? inner = null) : Exception(message, inner);
/// <summary>Wrong user/password, or the recorder locked this IP after failed logins.</summary>
public sealed class NvrAuthException(string message, Exception? inner = null) : NvrException(message, inner);
/// <summary>No answer: wrong IP/port, recorder off, network down, timeout.</summary>
public sealed class NvrUnreachableException(string message, Exception? inner = null) : NvrException(message, inner);
/// <summary>None of the ISAPI download methods worked; the caller may fall back to RTSP recording.</summary>
public sealed class NvrDownloadUnsupportedException(string message, Exception? inner = null) : NvrException(message, inner);

/// <summary>
/// Talks to one Hikvision NVR over ISAPI (HTTP digest) and builds its RTSP URLs. Thread-safe: the UI, the export
/// queue and the probe may call it concurrently. All DateTime values in and out are device-local wall clock.
/// </summary>
public interface INvrClient : IDisposable
{
    NvrConfig Config { get; }

    /// <summary>GET /ISAPI/System/deviceInfo.</summary>
    Task<NvrDeviceInfo> GetDeviceInfoAsync(CancellationToken ct);

    /// <summary>GET /ISAPI/System/time.</summary>
    Task<DeviceClock> GetClockAsync(CancellationToken ct);

    /// <summary>
    /// Configured IP channels with names and online state (InputProxy/channels + channels/status; names fall back to
    /// /ISAPI/Streaming/channels channelName, then "Camera {No:00}"). Sorted by No. Unconfigured channels are omitted.
    /// </summary>
    Task<IReadOnlyList<ChannelInfo>> GetChannelsAsync(CancellationToken ct);

    /// <summary>
    /// Recorded segments of the channel overlapping [from, to), all result pages fetched, sorted by Start,
    /// not merged and not clipped to the range.
    /// </summary>
    Task<IReadOnlyList<RecordSegment>> SearchAsync(int channel, DateTime from, DateTime to, CancellationToken ct, StreamKind kind = StreamKind.Main);

    /// <summary>
    /// Downloads [from, to) of the main stream over ISAPI into <paramref name="folder"/> (Hikvision PS files, kept as
    /// *.ps), trying ByTime, then BySegmentRange, then BySegmentWhole. Returns the parts in time order.
    /// Throws NvrDownloadUnsupportedException when no method works, NvrException when nothing is recorded in the range.
    /// <paramref name="bytes"/> reports the running total of bytes written.
    /// </summary>
    Task<IReadOnlyList<DownloadedPart>> DownloadAsync(int channel, DateTime from, DateTime to, string folder, IProgress<long>? bytes, CancellationToken ct);

    /// <summary>rtsp://user:pass@host:rtspPort/Streaming/Channels/{No}0{kind} with user and password percent-encoded.</summary>
    string LiveUrl(int channel, StreamKind kind);

    /// <summary>
    /// rtsp://user:pass@host:rtspPort/Streaming/tracks/{No}0{kind}?starttime=yyyyMMddTHHmmssZ[&amp;endtime=...] in the
    /// resolved time mode (LocalWithZ until ResolveTimeModeAsync says otherwise).
    /// </summary>
    string PlaybackUrl(int channel, DateTime from, DateTime? to = null, StreamKind kind = StreamKind.Main);

    /// <summary>
    /// Config.TimeMode unless it is Auto; for Auto, detects once (latest segment end of an online channel compared
    /// with the device clock read as local vs UTC; LocalWithZ when unsure) and caches the answer for this client.
    /// </summary>
    Task<TimeMode> ResolveTimeModeAsync(CancellationToken ct);
}

public interface INvrClientFactory
{
    INvrClient Create(NvrConfig config, string password);
}
