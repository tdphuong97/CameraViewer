using System.Net;
using System.Text;
using System.Xml.Linq;
using Cv.Core.Nvr;

namespace Cv.Core.Hik;

/// <summary>
/// ISAPI client of one Hikvision NVR (HTTP digest) plus its RTSP URLs. Partial: search and time mode in
/// HikNvrClient.Search.cs, download in HikNvrClient.Download.cs.
/// </summary>
internal sealed partial class HikNvrClient : INvrClient, INvrDiagnostics
{
    private readonly HttpClient? _http;
    private readonly string _password;
    // One request phase (login + headers) at a time: concurrent digest logins made Hikvision firmware refuse one of
    // them as a wrong password (seen on site with access-control terminals). Download bodies stream outside the gate.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public HikNvrClient(NvrConfig config, string password, HttpMessageHandler? inner = null, TimeProvider? time = null)
    {
        Config = config;
        _password = password ?? "";
        var host = HostOnly(config.Host);
        if (host.Length > 0 && Uri.TryCreate($"{(config.UseHttps ? "https" : "http")}://{UriHost(host)}:{config.HttpPort}/", UriKind.Absolute, out var baseUri))
        {
            inner ??= CreateHandler(config.UseHttps);
            _http = new HttpClient(new DigestHandler(config.User ?? "", _password, inner, time), disposeHandler: true)
            {
                BaseAddress = baseUri,
                Timeout = Timeout.InfiniteTimeSpan, // per-call and stall timeouts are applied per request
            };
        }
        else inner?.Dispose();
    }

    public NvrConfig Config { get; }

    /// <summary>Per ISAPI call, counted after the gate is acquired.</summary>
    internal TimeSpan CallTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>A download fails when no byte arrives for this long (headers included).</summary>
    internal TimeSpan StallTimeout { get; set; } = TimeSpan.FromSeconds(30);

    private static SocketsHttpHandler CreateHandler(bool https)
    {
        var h = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(5),
            UseProxy = false, // LAN recorder: never route the digest login through a system proxy
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
        };
        // Recorders ship self-signed certificates; accepted only when the admin chose HTTPS for a LAN device.
        if (https) h.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        return h;
    }

    /// <summary>Host as typed, without scheme, path or spaces.</summary>
    private static string HostOnly(string? host)
    {
        var h = (host ?? "").Trim();
        var i = h.IndexOf("://", StringComparison.Ordinal);
        if (i >= 0) h = h[(i + 3)..];
        var slash = h.IndexOf('/');
        if (slash >= 0) h = h[..slash];
        return h;
    }

    /// <summary>IPv6 literals need brackets inside a URL.</summary>
    private static string UriHost(string host) =>
        host.Contains(':') && !host.StartsWith('[') ? "[" + host + "]" : host;

    private string DisplayHost => HostOnly(Config.Host) is { Length: > 0 } h ? h : "(chưa nhập địa chỉ)";

    public void Dispose() => _http?.Dispose();

    // ---- HTTP ----

    private HttpClient Http => _http ?? throw new NvrUnreachableException(
        "Chưa nhập đúng địa chỉ IP của đầu ghi. Kiểm tra lại trong Cài đặt.");

    private NvrUnreachableException Unreachable(Exception? inner) => new(HikErrors.Unreachable(DisplayHost), inner);

    /// <summary>
    /// One ISAPI call with a small body: network errors and timeouts become NvrUnreachableException, 401 becomes
    /// NvrAuthException, error answers NvrStatusException. A cancelled caller token propagates as is.
    /// </summary>
    internal async Task<string> SendAsync(HttpMethod method, string path, string? xmlBody, CancellationToken ct)
    {
        var http = Http;
        await _gate.WaitAsync(ct);
        int code;
        byte[] bytes;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(CallTimeout);
            using var req = new HttpRequestMessage(method, path);
            if (xmlBody is not null) req.Content = new StringContent(xmlBody, Encoding.UTF8, "application/xml");
            try
            {
                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token);
                code = (int)resp.StatusCode;
                bytes = await resp.Content.ReadAsByteArrayAsync(cts.Token);
            }
            catch (OperationCanceledException e) when (!ct.IsCancellationRequested) { throw Unreachable(e); }
            catch (HttpRequestException e) { throw Unreachable(e); }
            catch (IOException e) { throw Unreachable(e); }
            catch (ObjectDisposedException e) { throw Unreachable(e); }
        }
        finally { _gate.Release(); }

        var text = HikXml.Decode(bytes);
        if (code == 401) throw LogAuth(path, text);
        if (HikErrors.FromResponse(code, text) is { } err)
        {
            Log.Warn($"ISAPI {method} {path} -> {code}: {HikErrors.Trim(text)}");
            throw err;
        }
        return text;
    }

    private static NvrAuthException LogAuth(string path, string? body)
    {
        var e = HikErrors.Auth(body);
        Log.Warn($"ISAPI {path} -> 401: {HikErrors.Trim(body)}");
        return e;
    }

    internal async Task<XElement> GetXmlAsync(string path, CancellationToken ct) =>
        HikXml.TryParse(await SendAsync(HttpMethod.Get, path, null, ct)) ?? throw new NvrException(HikErrors.Unreadable);

    private static bool IsFatal(NvrException e) => e is NvrAuthException or NvrUnreachableException;

    // ---- Device ----

    public async Task<NvrDeviceInfo> GetDeviceInfoAsync(CancellationToken ct) =>
        HikXml.ParseDeviceInfo(await GetXmlAsync("/ISAPI/System/deviceInfo", ct));

    private long _offsetTicks = long.MinValue; // device UTC offset once known

    private TimeSpan? KnownOffset
    {
        get { var t = Interlocked.Read(ref _offsetTicks); return t == long.MinValue ? null : TimeSpan.FromTicks(t); }
        set => Interlocked.Exchange(ref _offsetTicks, value?.Ticks ?? long.MinValue);
    }

    public async Task<DeviceClock> GetClockAsync(CancellationToken ct)
    {
        var (localText, tz) = HikXml.ParseTime(await GetXmlAsync("/ISAPI/System/time", ct));
        if (!HikTime.TryParse(localText, out var stamp)) throw new NvrException("Đầu ghi trả về giờ không đọc được.");
        var posix = HikTime.ParsePosixOffset(tz);
        DeviceClock clock = stamp.Kind switch
        {
            // Preferred: localTime carries its own offset.
            StampKind.Offset => new(stamp.Wall, stamp.Offset),
            // "Z" here is read as real UTC (ISO meaning); with the zone known it becomes the device wall clock.
            StampKind.Z when posix is { } off => new(HikTime.Unspec(stamp.Wall + off), off),
            _ => new(stamp.Wall, posix ?? HikTime.PcOffset(stamp.Wall)),
        };
        KnownOffset = clock.UtcOffset;
        return clock;
    }

    /// <summary>The device offset, read once; PC offset when the recorder does not answer the time call sensibly.</summary>
    private async Task<TimeSpan> OffsetAsync(CancellationToken ct)
    {
        if (KnownOffset is { } o) return o;
        try { return (await GetClockAsync(ct)).UtcOffset; }
        catch (NvrException e) when (!IsFatal(e))
        {
            Log.Warn("Device time unreadable, using the PC time zone: " + e.Message);
            var pc = HikTime.PcOffset(DateTime.Now);
            KnownOffset = pc;
            return pc;
        }
    }

    public async Task<IReadOnlyList<ChannelInfo>> GetChannelsAsync(CancellationToken ct)
    {
        List<ProxyChannel>? proxies = null;
        Dictionary<int, string>? streamNames = null;
        NvrStatusException? notSupported = null;
        try
        {
            proxies = HikXml.ParseInputProxyChannels(await GetXmlAsync("/ISAPI/ContentMgmt/InputProxy/channels", ct));
        }
        catch (NvrStatusException e) when (e.NotSupported) { notSupported = e; }
        if (proxies is null)
        {
            // No IP-channel list on this firmware: fall back to the streaming channels.
            var names = await StreamNamesAsync(ct);
            if (names is null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(notSupported!);
            streamNames = names;
            proxies = names.Select(kv => new ProxyChannel(kv.Key, kv.Value, null, null)).ToList();
        }

        Dictionary<int, bool> online = [];
        try
        {
            online = HikXml.ParseChannelStatus(await GetXmlAsync("/ISAPI/ContentMgmt/InputProxy/channels/status", ct));
        }
        catch (NvrException e) when (e is not NvrAuthException)
        {
            Log.Warn("Channel status unavailable, assuming every channel online: " + e.Message);
        }

        if (streamNames is null && proxies.Any(p => string.IsNullOrWhiteSpace(p.Name)))
            streamNames = await StreamNamesAsync(ct);

        return proxies
            .GroupBy(p => p.No).Select(g => g.First())
            .OrderBy(p => p.No)
            .Select(p => new ChannelInfo(
                p.No,
                !string.IsNullOrWhiteSpace(p.Name) ? p.Name.Trim()
                    : streamNames is not null && streamNames.TryGetValue(p.No, out var n) ? n
                    : $"Camera {p.No:00}",
                !online.TryGetValue(p.No, out var on) || on,
                p.Ip,
                p.Model))
            .ToList();
    }

    private async Task<Dictionary<int, string>?> StreamNamesAsync(CancellationToken ct)
    {
        try { return HikXml.ParseStreamingChannelNames(await GetXmlAsync("/ISAPI/Streaming/channels", ct)); }
        catch (NvrException e) when (e is not NvrAuthException)
        {
            Log.Warn("Streaming channel names unavailable: " + e.Message);
            return null;
        }
    }

    // ---- RTSP URLs ----

    private string Credentials => Uri.EscapeDataString(Config.User ?? "") + ":" + Uri.EscapeDataString(_password);

    private string RtspAuthority => $"{Credentials}@{UriHost(HostOnly(Config.Host))}:{Config.RtspPort}";

    public string LiveUrl(int channel, StreamKind kind) =>
        $"rtsp://{RtspAuthority}/Streaming/Channels/{TrackId(channel, kind)}";

    public string PlaybackUrl(int channel, DateTime from, DateTime? to = null, StreamKind kind = StreamKind.Main)
    {
        var mode = CurrentMode;
        var offset = mode == TimeMode.Utc ? KnownOffset ?? HikTime.PcOffset(from) : TimeSpan.Zero;
        var sb = new StringBuilder($"rtsp://{RtspAuthority}/Streaming/tracks/{TrackId(channel, kind)}");
        sb.Append("?starttime=").Append(HikTime.UriZ(HikTime.ToWire(from, mode, offset)));
        if (to is { } t) sb.Append("&endtime=").Append(HikTime.UriZ(HikTime.ToWire(t, mode, offset)));
        return sb.ToString();
    }

    private static int TrackId(int channel, StreamKind kind) => channel * 100 + (int)kind;
}
