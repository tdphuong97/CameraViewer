using Cv.Core.Export;
using Cv.Core.Nvr;
using Cv.Core.Video;

namespace Cv.Diagnostics.Tests;

/// <summary>A recorder in memory: every call can be replaced; calls are recorded.</summary>
internal sealed class FakeNvr : INvrClient, INvrDiagnostics
{
    public const string Host = "192.168.1.64";
    public readonly List<string> Calls = [];
    public static readonly DateTime Now = new(2026, 11, 7, 19, 45, 12);

    public NvrConfig Config { get; init; } = new() { Host = Host, User = "admin" };
    public string Password { get; set; } = "";

    public Func<NvrDeviceInfo> DeviceInfo = () => new("DS-7732NI-M4", "SERIAL123", "V4.71.120", "Network Video Recorder");
    public Func<DeviceClock> Clock = () => new(Now, TimeSpan.FromHours(7));
    public Func<TimeMode> Mode = () => TimeMode.LocalWithZ;
    public Func<IReadOnlyList<ChannelInfo>> Channels = () =>
        Enumerable.Range(1, 17).Select(n => new ChannelInfo(n, $"Camera {n:00}", n != 5, $"192.168.254.{n + 1}", "DS-2CD1043G2-LIU")).ToList();
    public Func<int, IReadOnlyList<RecordSegment>> Search = ch =>
        Enumerable.Range(0, 20).Select(i =>
        {
            var start = Now.Date.AddHours(-4 + i);
            return new RecordSegment(start, start.AddHours(1), $"rtsp://{Host}/Streaming/tracks/{ch}01/?name={i}");
        }).Where(s => s.Start < Now).Select(s => s.End > Now ? s with { End = Now } : s).ToList();
    /// <summary>method null = DownloadAsync (the client picks), else DownloadWithAsync(method).</summary>
    public Func<DownloadMethod?, DateTime, DateTime, string, IProgress<long>?, CancellationToken, IReadOnlyList<DownloadedPart>> Download =
        (method, from, to, folder, bytes, ct) =>
        {
            var path = Path.Combine(folder, $"{method?.ToString() ?? "auto"}.ps");
            File.WriteAllBytes(path, [(byte)'I', (byte)'M', (byte)'K', (byte)'H', 0, 0, 0, 0]);
            bytes?.Report(8);
            return [new DownloadedPart(path, from, from, to, method ?? DownloadMethod.ByTime)];
        };
    public DownloadMethod? WorkingMethod { get; set; }

    public Task<NvrDeviceInfo> GetDeviceInfoAsync(CancellationToken ct) => Call("deviceInfo", DeviceInfo);
    public Task<DeviceClock> GetClockAsync(CancellationToken ct) => Call("time", Clock);
    public Task<IReadOnlyList<ChannelInfo>> GetChannelsAsync(CancellationToken ct) => Call("channels", Channels);
    public Task<IReadOnlyList<RecordSegment>> SearchAsync(int channel, DateTime from, DateTime to, CancellationToken ct, StreamKind kind = StreamKind.Main) =>
        Call($"search {channel}", () => Search(channel));
    public Task<IReadOnlyList<DownloadedPart>> DownloadAsync(int channel, DateTime from, DateTime to, string folder, IProgress<long>? bytes, CancellationToken ct) =>
        Call($"download {channel} {from:HH:mm:ss}-{to:HH:mm:ss}", () =>
        {
            var parts = Download(null, from, to, folder, bytes, ct);
            WorkingMethod = parts.FirstOrDefault()?.Method;
            return parts;
        });
    public Task<IReadOnlyList<DownloadedPart>> DownloadWithAsync(DownloadMethod method, int channel, DateTime from, DateTime to, string folder, IProgress<long>? bytes, CancellationToken ct) =>
        Call($"downloadWith {method}", () => Download(method, from, to, folder, bytes, ct));
    public Task<TimeMode> ResolveTimeModeAsync(CancellationToken ct) => Call("timeMode", Mode);

    public string LiveUrl(int channel, StreamKind kind) =>
        $"rtsp://admin:{Uri.EscapeDataString(Password)}@{Host}:554/Streaming/Channels/{channel}0{(int)kind}";

    public string PlaybackUrl(int channel, DateTime from, DateTime? to = null, StreamKind kind = StreamKind.Main) =>
        $"rtsp://admin:{Uri.EscapeDataString(Password)}@{Host}:554/Streaming/tracks/{channel}0{(int)kind}?starttime={from:yyyyMMdd'T'HHmmss'Z'}";

    public bool Disposed { get; private set; }
    public void Dispose() => Disposed = true;

    private Task<T> Call<T>(string name, Func<T> f)
    {
        lock (Calls) Calls.Add(name);
        return Task.FromResult(f());
    }
}

internal sealed class FakeNvrFactory(FakeNvr nvr, bool diagnostics = true) : INvrClientFactory
{
    public INvrClient Create(NvrConfig config, string password)
    {
        nvr.Password = password;
        return diagnostics ? nvr : new PlainClient(nvr);
    }

    /// <summary>A client without INvrDiagnostics (only the contract every client has).</summary>
    private sealed class PlainClient(FakeNvr n) : INvrClient
    {
        public NvrConfig Config => n.Config;
        public Task<NvrDeviceInfo> GetDeviceInfoAsync(CancellationToken ct) => n.GetDeviceInfoAsync(ct);
        public Task<DeviceClock> GetClockAsync(CancellationToken ct) => n.GetClockAsync(ct);
        public Task<IReadOnlyList<ChannelInfo>> GetChannelsAsync(CancellationToken ct) => n.GetChannelsAsync(ct);
        public Task<IReadOnlyList<RecordSegment>> SearchAsync(int channel, DateTime from, DateTime to, CancellationToken ct, StreamKind kind = StreamKind.Main) =>
            n.SearchAsync(channel, from, to, ct, kind);
        public Task<IReadOnlyList<DownloadedPart>> DownloadAsync(int channel, DateTime from, DateTime to, string folder, IProgress<long>? bytes, CancellationToken ct) =>
            n.DownloadAsync(channel, from, to, folder, bytes, ct);
        public string LiveUrl(int channel, StreamKind kind) => n.LiveUrl(channel, kind);
        public string PlaybackUrl(int channel, DateTime from, DateTime? to = null, StreamKind kind = StreamKind.Main) => n.PlaybackUrl(channel, from, to, kind);
        public Task<TimeMode> ResolveTimeModeAsync(CancellationToken ct) => n.ResolveTimeModeAsync(ct);
        public void Dispose() => n.Dispose();
    }
}

/// <summary>A source that is Playing right after Start and has a new frame on every poll (or fails).</summary>
internal sealed class FakeVideoSource(VideoSourceOptions options, string? fail) : IVideoSource
{
    private long _seq;
    public VideoSourceOptions Options => options;
    public VideoState State { get; private set; } = VideoState.Idle;
    public string? Error { get; private set; }
    public event Action<IVideoSource>? StateChanged;
    public VideoStats Stats => options.Url.Contains("02") && options.IsLive
        ? new VideoStats("H.264", 640, 360, 15, 384, false)
        : new VideoStats("H.265", 1280, 720, 15, 1024, false);
    public TimeSpan Position => TimeSpan.FromMilliseconds(_seq * 66);
    public bool Disposed { get; private set; }

    public void Start()
    {
        State = fail is null ? VideoState.Playing : VideoState.Failed;
        Error = fail;
        StateChanged?.Invoke(this);
    }

    public void SetTargetSize(int width, int height) { }
    public void Pause() { }
    public void Resume() { }

    public bool TryCopyLatest(ref long lastSequence, FrameCopier copier)
    {
        if (State != VideoState.Playing) return false;
        _seq++;
        copier(IntPtr.Zero, 4, 4, 16);
        lastSequence = _seq;
        return true;
    }

    public Task<FrameSnapshot> CaptureAsync(CancellationToken ct) => Task.FromResult(new FrameSnapshot(new byte[64], 4, 4, 16));
    public void Dispose() => Disposed = true;
}

internal sealed class FakeVideoFactory : IVideoSourceFactory
{
    public readonly List<FakeVideoSource> Created = [];
    /// <summary>Returns an error text for URLs that must fail, null otherwise.</summary>
    public Func<string, string?> Fail = _ => null;

    public IVideoSource Create(VideoSourceOptions options)
    {
        var s = new FakeVideoSource(options, Fail(options.Url));
        lock (Created) Created.Add(s);
        return s;
    }
}

internal sealed class FakeMedia : IMediaTools
{
    public readonly List<IReadOnlyList<RemuxPart>> Remuxed = [];
    public readonly List<string> Recorded = [];
    /// <summary>Duration per file name (not path); default = 10 s.</summary>
    public Func<string, TimeSpan?> Duration = _ => TimeSpan.FromSeconds(10);

    public Task RemuxAsync(IReadOnlyList<RemuxPart> parts, string outputPath, IProgress<double>? progress, CancellationToken ct)
    {
        Remuxed.Add(parts);
        File.WriteAllBytes(outputPath, new byte[2048]);
        return Task.CompletedTask;
    }

    public Task<TimeSpan?> GetDurationAsync(string path, CancellationToken ct) => Task.FromResult(Duration(Path.GetFileName(path)));

    public Task RecordAsync(string url, TimeSpan duration, string outputPath, IProgress<double>? progress, CancellationToken ct)
    {
        Recorded.Add(url);
        File.WriteAllBytes(outputPath, new byte[4096]);
        return Task.CompletedTask;
    }
}

/// <summary>Fixed wall clock in UTC+07:00; timers stay real.</summary>
internal sealed class FixedTime(DateTimeOffset now) : TimeProvider
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("UTC+7", TimeSpan.FromHours(7), "UTC+7", "UTC+7");
    public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
    public override TimeZoneInfo LocalTimeZone => Zone;
}
