using System.Buffers.Binary;
using Cv.Core.Export;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace MockNvr.Tests;

/// <summary>A mock recorder on a free loopback port with tiny synthetic clips, stopped on dispose.</summary>
internal sealed class RunningMock : IAsyncDisposable
{
    public MockOptions Options { get; }
    public int Port { get; private set; }
    public List<string> LogLines { get; } = [];
    private readonly WebApplication _app;
    private readonly string _data;

    private RunningMock(MockOptions o, string data)
    {
        Options = o;
        _data = data;
        _app = MockServer.Build(o, TimeProvider.System, line => { lock (LogLines) LogLines.Add(line); });
    }

    public static async Task<RunningMock> StartAsync(Action<MockOptions>? configure = null)
    {
        var data = Path.Combine(Path.GetTempPath(), "mocknvr-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(data);
        File.WriteAllBytes(Path.Combine(data, "es-h264.h264"), Clips.H264(30));
        File.WriteAllBytes(Path.Combine(data, "es-h265.hevc"), Clips.H265(30));
        var o = new MockOptions { Port = 0, DataDir = data, UtcOffset = TimeSpan.FromHours(7) };
        configure?.Invoke(o);
        var m = new RunningMock(o, data);
        await m._app.StartAsync();
        var address = m._app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        m.Port = new Uri(address).Port;
        return m;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        try { Directory.Delete(_data, true); } catch (IOException) { }
    }
}

/// <summary>Synthetic Annex-B streams: valid NAL structure (parameter sets, key frames, AUDs), dummy slice data.</summary>
internal static class Clips
{
    public static byte[] H264(int frames, bool aud = true)
    {
        var ms = new MemoryStream();
        for (var i = 0; i < frames; i++)
        {
            if (aud) Nal(ms, [0x09, 0xF0]);
            if (i % 30 == 0)
            {
                Nal(ms, [0x67, 0x4D, 0x40, 0x1F, 0x95, 0xA8]); // SPS
                Nal(ms, [0x68, 0xEE, 0x3C, 0x80]); // PPS
                Nal(ms, Slice(0x65, 1, 200)); // IDR
            }
            else Nal(ms, Slice(0x41, 1, 60)); // P
        }
        return ms.ToArray();
    }

    public static byte[] H265(int frames, bool aud = true)
    {
        var ms = new MemoryStream();
        for (var i = 0; i < frames; i++)
        {
            if (aud) Nal(ms, [0x46, 0x01, 0x50]); // AUD (type 35)
            if (i % 30 == 0)
            {
                Nal(ms, [0x40, 0x01, 0x0C, 0x01]); // VPS
                Nal(ms, [0x42, 0x01, 0x01, 0x01]); // SPS
                Nal(ms, [0x44, 0x01, 0xC1, 0x72]); // PPS
                Nal(ms, Slice(0x26, 2, 220)); // IDR_W_RADL (type 19)
            }
            else Nal(ms, Slice(0x02, 2, 70)); // TRAIL_R (type 1)
        }
        return ms.ToArray();
    }

    /// <summary>NAL header, then a payload whose first bit is set (first slice of the picture), never 00 00.</summary>
    private static byte[] Slice(byte type, int headerBytes, int size)
    {
        var b = new byte[size];
        b[0] = type;
        if (headerBytes == 2) b[1] = 0x01;
        for (var i = headerBytes; i < size; i++) b[i] = (byte)(0x80 | (i * 7 & 0x7F));
        return b;
    }

    private static void Nal(Stream s, byte[] nal)
    {
        s.Write([0, 0, 0, 1]);
        s.Write(nal);
    }
}

/// <summary>Reads back a PS written by the mock: header, PSM, video PTS range, audio packets.</summary>
internal sealed record PsInfo(bool Imkh, byte VideoStreamType, bool HasAudioInPsm, bool PsmCrcOk, int VideoFrames, int VideoPes,
    int AudioPes, long FirstPts, long LastPts, int KeyFramesWithPsm)
{
    public TimeSpan Duration => TimeSpan.FromSeconds((LastPts - FirstPts) / 90000.0);

    public static PsInfo Read(byte[] d)
    {
        var imkh = d.Length >= 40 && d[0] == 'I' && d[1] == 'M' && d[2] == 'K' && d[3] == 'H';
        var i = imkh ? 40 : 0;
        byte vtype = 0;
        bool audioPsm = false, crcOk = false;
        int frames = 0, vpes = 0, apes = 0, psms = 0;
        long first = -1, last = -1;
        while (i + 4 <= d.Length)
        {
            if (d[i] != 0 || d[i + 1] != 0 || d[i + 2] != 1) throw new InvalidDataException($"lost sync at {i}");
            var id = d[i + 3];
            if (id == 0xBA)
            {
                var stuffing = d[i + 13] & 7;
                i += 14 + stuffing;
                continue;
            }
            var len = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(i + 4));
            if (id == 0xBC)
            {
                psms++;
                var esLen = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(i + 10));
                for (var e = 0; e < esLen; e += 4)
                {
                    var type = d[i + 12 + e];
                    if (d[i + 13 + e] == 0xE0) vtype = type;
                    if (d[i + 13 + e] == 0xC0) audioPsm = true;
                }
                var crcAt = i + 6 + len - 4;
                crcOk = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(crcAt)) == HikPsWriter.Crc32Mpeg(d.AsSpan(i, crcAt - i));
            }
            else if (id == 0xE0)
            {
                vpes++;
                if ((d[i + 7] & 0x80) != 0)
                {
                    var pts = Pts(d.AsSpan(i + 9));
                    if (first < 0) first = pts;
                    last = pts;
                    frames++;
                }
            }
            else if (id == 0xC0) apes++;
            i += 6 + len;
        }
        return new PsInfo(imkh, vtype, audioPsm, crcOk, frames, vpes, apes, first, last, psms);
    }

    private static long Pts(ReadOnlySpan<byte> b) =>
        ((long)(b[0] >> 1) & 0x07) << 30 | (long)b[1] << 22 | (long)(b[2] >> 1) << 15 | (long)b[3] << 7 | (long)(b[4] >> 1);
}

/// <summary>IMediaTools without FFmpeg: durations read from the mock's PS, "MP4" files are text with the length.</summary>
internal sealed class PsMedia : IMediaTools
{
    public List<IReadOnlyList<RemuxPart>> Remuxed { get; } = [];

    public Task<TimeSpan?> GetDurationAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return Task.FromResult<TimeSpan?>(null);
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length > 4 && bytes[0] == 'M' && bytes[1] == 'P' && bytes[2] == '4' && bytes[3] == ' ')
            return Task.FromResult<TimeSpan?>(TimeSpan.FromMilliseconds(double.Parse(System.Text.Encoding.ASCII.GetString(bytes, 4, bytes.Length - 4))));
        try
        {
            var info = PsInfo.Read(bytes);
            return Task.FromResult<TimeSpan?>(info.VideoFrames == 0 ? null : info.Duration);
        }
        catch (InvalidDataException) { return Task.FromResult<TimeSpan?>(null); }
    }

    public async Task RemuxAsync(IReadOnlyList<RemuxPart> parts, string outputPath, IProgress<double>? progress, CancellationToken ct)
    {
        lock (Remuxed) Remuxed.Add(parts);
        double total = 0;
        foreach (var p in parts)
        {
            var d = (await GetDurationAsync(p.Path, ct)) ?? throw new InvalidDataException("unreadable part");
            var from = p.From ?? TimeSpan.Zero;
            var to = p.To is { } t && t < d ? t : d;
            total += Math.Max(0, (to - from).TotalMilliseconds);
        }
        await File.WriteAllTextAsync(outputPath, "MP4 " + total.ToString(System.Globalization.CultureInfo.InvariantCulture), ct);
    }

    public Task RecordAsync(string url, TimeSpan duration, string outputPath, IProgress<double>? progress, CancellationToken ct) =>
        File.WriteAllTextAsync(outputPath, "MP4 " + duration.TotalMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture), ct);
}
