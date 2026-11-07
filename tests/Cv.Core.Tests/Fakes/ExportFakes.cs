using System.Globalization;
using Cv.Core.Export;
using Cv.Core.Nvr;

namespace Cv.Core.Tests.Fakes;

/// <summary>Counts how many calls run at the same time.</summary>
public sealed class ConcurrencyMeter
{
    private int _now;
    private int _max;
    public int Max => Volatile.Read(ref _max);

    public void Enter()
    {
        var n = Interlocked.Increment(ref _now);
        int m;
        while (n > (m = Volatile.Read(ref _max)) && Interlocked.CompareExchange(ref _max, n, m) != m) { }
    }

    public void Exit() => Interlocked.Decrement(ref _now);
}

public delegate Task<IReadOnlyList<DownloadedPart>> DownloadFunc(int channel, DateTime from, DateTime to, string folder, IProgress<long>? bytes, CancellationToken ct);

public sealed class FakeNvrClient(string id = "nvr1") : INvrClient
{
    private int _downloads;

    public NvrConfig Config { get; } = new() { Id = id, Host = "10.0.0.1" };
    public DownloadFunc? OnDownload { get; set; }
    public ConcurrencyMeter? Meter { get; set; }
    public int Downloads => Volatile.Read(ref _downloads);
    public List<string> Folders { get; } = [];

    public async Task<IReadOnlyList<DownloadedPart>> DownloadAsync(int channel, DateTime from, DateTime to, string folder, IProgress<long>? bytes, CancellationToken ct)
    {
        Interlocked.Increment(ref _downloads);
        lock (Folders) Folders.Add(folder);
        Meter?.Enter();
        try
        {
            if (OnDownload is not null) return await OnDownload(channel, from, to, folder, bytes, ct);
            bytes?.Report(1000);
            return [WritePart(folder, channel, from, to)];
        }
        finally { Meter?.Exit(); }
    }

    /// <summary>Writes a small .ps file the way the real client names it and returns its part.</summary>
    public static DownloadedPart WritePart(string folder, int channel, DateTime from, DateTime to,
        DownloadMethod method = DownloadMethod.ByTime, DateTime? nominal = null, int index = 1, DateTime? fileStamp = null)
    {
        var path = Path.Combine(folder, $"ch{channel}_{(fileStamp ?? from):yyyyMMddHHmmss}_{index}.ps");
        File.WriteAllBytes(path, Samples.Ps(2000));
        return new DownloadedPart(path, nominal ?? from, from, to, method);
    }

    public static Task<IReadOnlyList<DownloadedPart>> Parts(params DownloadedPart[] parts) =>
        Task.FromResult<IReadOnlyList<DownloadedPart>>(parts);

    public string PlaybackUrl(int channel, DateTime from, DateTime? to = null, StreamKind kind = StreamKind.Main) =>
        $"rtsp://admin:pw@10.0.0.1:554/Streaming/tracks/{channel * 100 + (int)kind}?starttime={from.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)}"
        + (to is { } t ? "&endtime=" + t.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) : "");

    public int Resolved { get; private set; }

    public Task<TimeMode> ResolveTimeModeAsync(CancellationToken ct)
    {
        Resolved++;
        return Task.FromResult(TimeMode.LocalWithZ);
    }

    public string LiveUrl(int channel, StreamKind kind) => $"rtsp://admin:pw@10.0.0.1:554/Streaming/Channels/{channel * 100 + (int)kind}";
    public Task<NvrDeviceInfo> GetDeviceInfoAsync(CancellationToken ct) => Task.FromResult(new NvrDeviceInfo("DS-7732NI-M4", "SN", "V4", "NVR"));
    public Task<DeviceClock> GetClockAsync(CancellationToken ct) => Task.FromResult(new DeviceClock(DateTime.Now, TimeSpan.FromHours(7)));
    public Task<IReadOnlyList<ChannelInfo>> GetChannelsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ChannelInfo>>([]);
    public Task<IReadOnlyList<RecordSegment>> SearchAsync(int channel, DateTime from, DateTime to, CancellationToken ct, StreamKind kind = StreamKind.Main) =>
        Task.FromResult<IReadOnlyList<RecordSegment>>([]);
    public void Dispose() { }
}

public sealed class FakeMediaTools : IMediaTools
{
    public Func<string, TimeSpan?> Duration { get; set; } = _ => null;
    public List<(IReadOnlyList<RemuxPart> Parts, string Output)> Remuxes { get; } = [];
    public List<(string Url, TimeSpan Duration, string Output)> Records { get; } = [];
    /// <summary>Thrown after the .part file was written.</summary>
    public Exception? RemuxError { get; set; }
    public Exception? RecordError { get; set; }
    /// <summary>Runs inside RemuxAsync before the file is written (to block or watch).</summary>
    public Func<CancellationToken, Task>? RemuxHook { get; set; }

    public async Task RemuxAsync(IReadOnlyList<RemuxPart> parts, string outputPath, IProgress<double>? progress, CancellationToken ct)
    {
        lock (Remuxes) Remuxes.Add((parts, outputPath));
        foreach (var p in parts) Assert.True(File.Exists(p.Path), "part exists during remux: " + p.Path);
        if (RemuxHook is not null) await RemuxHook(ct);
        await File.WriteAllBytesAsync(outputPath + ".part", [1, 2, 3], ct);
        progress?.Report(0.5);
        if (RemuxError is not null) throw RemuxError;
        File.Move(outputPath + ".part", outputPath);
        progress?.Report(1);
    }

    public Task<TimeSpan?> GetDurationAsync(string path, CancellationToken ct) => Task.FromResult(Duration(path));

    public async Task RecordAsync(string url, TimeSpan duration, string outputPath, IProgress<double>? progress, CancellationToken ct)
    {
        lock (Records) Records.Add((url, duration, outputPath));
        if (RecordError is not null) throw RecordError;
        await File.WriteAllBytesAsync(outputPath, [4, 5, 6], ct);
        progress?.Report(1);
    }
}
