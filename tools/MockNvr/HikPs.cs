using System.Buffers.Binary;
using System.Diagnostics;

namespace MockNvr;

/// <summary>One coded picture (its NAL units with 4-byte start codes, access unit delimiters removed).</summary>
public sealed record AccessUnit(ReadOnlyMemory<byte> Data, bool Key);

/// <summary>An Annex-B H.264/H.265 clip split into access units. Starts with a key frame; parameter sets repeat at every key frame.</summary>
public sealed class ElementaryClip
{
    public VideoCodec Codec { get; }
    public IReadOnlyList<AccessUnit> Units { get; }
    public long Bytes { get; }

    private ElementaryClip(VideoCodec codec, List<AccessUnit> units)
    {
        Codec = codec; Units = units; Bytes = units.Sum(u => (long)u.Data.Length);
    }

    public static ElementaryClip Load(string path, VideoCodec codec) => Parse(File.ReadAllBytes(path), codec);

    public static ElementaryClip Parse(byte[] data, VideoCodec codec)
    {
        var units = new List<AccessUnit>();
        var cur = new List<(int Start, int End)>();
        bool hasVcl = false, key = false;

        void Flush()
        {
            if (cur.Count > 0 && hasVcl)
            {
                var size = cur.Sum(n => 4 + n.End - n.Start);
                var au = new byte[size];
                var o = 0;
                foreach (var (s, e) in cur)
                {
                    au[o + 3] = 1;
                    data.AsSpan(s, e - s).CopyTo(au.AsSpan(o + 4));
                    o += 4 + e - s;
                }
                units.Add(new AccessUnit(au, key));
            }
            cur.Clear(); hasVcl = false; key = false;
        }

        foreach (var (s, e) in Nals(data))
        {
            if (e - s < 2) continue;
            var info = Classify(data, s, codec);
            if (info.Aud) { Flush(); continue; }
            if ((info.StartsAu && hasVcl) || (info.Vcl && info.FirstSlice && hasVcl)) Flush();
            cur.Add((s, e));
            hasVcl |= info.Vcl;
            key |= info.Key;
        }
        Flush();

        var first = units.FindIndex(u => u.Key);
        if (first < 0) throw new InvalidDataException("The clip has no key frame.");
        return new ElementaryClip(codec, units.GetRange(first, units.Count - first));
    }

    /// <summary>NAL payload ranges between 00 00 01 start codes (trailing zero bytes trimmed).</summary>
    private static IEnumerable<(int Start, int End)> Nals(byte[] d)
    {
        var start = -1;
        for (var i = 0; i + 2 < d.Length; i++)
        {
            if (d[i] != 0 || d[i + 1] != 0 || d[i + 2] != 1) continue;
            if (start >= 0)
            {
                var end = i;
                while (end > start && d[end - 1] == 0) end--;
                yield return (start, end);
            }
            start = i + 3;
            i += 2;
        }
        if (start >= 0 && start < d.Length) yield return (start, d.Length);
    }

    private readonly record struct NalInfo(bool Vcl, bool Key, bool Aud, bool StartsAu, bool FirstSlice);

    private static NalInfo Classify(byte[] d, int s, VideoCodec codec)
    {
        if (codec == VideoCodec.H264)
        {
            var t = d[s] & 0x1F;
            var vcl = t is >= 1 and <= 5;
            // first_mb_in_slice == 0 is ue(v) "1": the first payload bit is set.
            return new NalInfo(vcl, t == 5, t == 9, t is 6 or 7 or 8 or (>= 14 and <= 18), vcl && s + 1 < d.Length && (d[s + 1] & 0x80) != 0);
        }
        var h = (d[s] >> 1) & 0x3F;
        var hv = h <= 31;
        // first_slice_segment_in_pic_flag is the first bit after the 2-byte header.
        return new NalInfo(hv, h is >= 16 and <= 21, h == 35, h is 32 or 33 or 34 or 39 or (>= 41 and <= 44) or (>= 48 and <= 55),
            hv && s + 2 < d.Length && (d[s + 2] & 0x80) != 0);
    }
}

/// <summary>
/// Writes a Hikvision-style MPEG program stream: the 40-byte "IMKH" media header, then per video frame a pack header
/// (00 00 01 BA, 6 stuffing bytes ending in a frame counter), a system header and PSM before every key frame, the frame
/// in PES packets (00 00 01 E0, PTS+DTS on the first) and, optionally, 20 ms G.711 µ-law audio PES (00 00 01 C0).
/// </summary>
public static class HikPsWriter
{
    public const int Clock = 90_000;
    public const int MaxPesPayload = 16 * 1024 - 32;
    public const byte StreamTypeH264 = 0x1B, StreamTypeH265 = 0x24, StreamTypeG711U = 0x91;
    private const int AudioFrameTicks = Clock / 50, AudioFrameBytes = 160;
    private const int MuxRate = 20_000; // units of 50 bytes/s

    /// <summary>The 40-byte media header (HIK_MEDIAINFO): fourcc, version, system format PS, video and audio format.</summary>
    public static byte[] Header(VideoCodec codec, bool audio)
    {
        var h = new byte[40];
        "IMKH"u8.CopyTo(h);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(4), codec == VideoCodec.H265 ? (ushort)0x0102 : (ushort)0x0101);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(8), 0x0002); // MPEG2-PS
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(10), codec == VideoCodec.H265 ? (ushort)0x0005 : (ushort)0x0100);
        if (audio)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(12), 0x7110); // G.711 µ-law
            h[14] = 1;
            h[15] = 16;
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(16), 8000);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(20), 64000);
        }
        return h;
    }

    public static long FrameCount(TimeSpan duration) => Math.Max(1, (long)Math.Round(duration.TotalSeconds * MockDevice.Fps));

    /// <summary>
    /// Writes <paramref name="duration"/> of video (the clip looped from its first frame) starting at
    /// <paramref name="startPts"/> (90 kHz, at least 1 s so the SCR stays positive). Returns the bytes written.
    /// </summary>
    public static async Task<long> WriteAsync(Stream output, ElementaryClip clip, TimeSpan duration, long startPts, bool audio,
        double mbps, CancellationToken ct)
    {
        if (startPts < Clock) startPts += Clock;
        var frameTicks = Clock / MockDevice.Fps;
        var frames = FrameCount(duration);
        var sysHeader = SystemHeader(audio);
        var psm = Psm(clip.Codec, audio);
        var buf = new MemoryStream(512 * 1024);
        long written = 0;
        uint packNo = 0;
        var audioPts = startPts;
        var sw = Stopwatch.StartNew();

        buf.Write(Header(clip.Codec, audio));
        for (long i = 0; i < frames; i++)
        {
            ct.ThrowIfCancellationRequested();
            var au = clip.Units[(int)(i % clip.Units.Count)];
            var pts = startPts + i * frameTicks;
            PackHeader(buf, pts - 3600, packNo++);
            if (au.Key) { buf.Write(sysHeader); buf.Write(psm); }
            VideoPes(buf, au.Data.Span, pts);
            while (audio && audioPts < pts + frameTicks)
            {
                PackHeader(buf, audioPts - 3600, packNo++);
                AudioPes(buf, audioPts);
                audioPts += AudioFrameTicks;
            }
            if (buf.Length >= 256 * 1024 || i == frames - 1)
            {
                await output.WriteAsync(buf.GetBuffer().AsMemory(0, (int)buf.Length), ct);
                written += buf.Length;
                buf.SetLength(0);
                if (mbps > 0)
                {
                    var due = TimeSpan.FromSeconds(written * 8 / (mbps * 1_000_000));
                    if (due > sw.Elapsed) await Task.Delay(due - sw.Elapsed, ct);
                }
            }
        }
        await output.FlushAsync(ct);
        return written;
    }

    private static void PackHeader(Stream s, long scr, uint packNo)
    {
        if (scr < 0) scr = 0;
        Span<byte> p = stackalloc byte[20];
        p[0] = 0; p[1] = 0; p[2] = 1; p[3] = 0xBA;
        p[4] = (byte)(0x44 | ((scr >> 27) & 0x38) | ((scr >> 28) & 0x03));
        p[5] = (byte)(scr >> 20);
        p[6] = (byte)(((scr >> 12) & 0xF8) | 0x04 | ((scr >> 13) & 0x03));
        p[7] = (byte)(scr >> 5);
        p[8] = (byte)(((scr & 0x1F) << 3) | 0x04); // SCR extension 0
        p[9] = 0x01;
        p[10] = (byte)((MuxRate >> 14) & 0xFF);
        p[11] = (byte)((MuxRate >> 6) & 0xFF);
        p[12] = (byte)(((MuxRate & 0x3F) << 2) | 0x03);
        p[13] = 0xFE; // reserved + 6 stuffing bytes
        p[14] = 0xFF; p[15] = 0xFF;
        BinaryPrimitives.WriteUInt32BigEndian(p[16..], packNo); // Hikvision keeps a frame counter in the stuffing
        s.Write(p);
    }

    private static byte[] SystemHeader(bool audio)
    {
        var streams = audio ? 2 : 1;
        var h = new List<byte> { 0, 0, 1, 0xBB, 0, (byte)(6 + 3 * streams) };
        const int rateBound = MuxRate;
        h.Add((byte)(0x80 | ((rateBound >> 15) & 0x7F)));
        h.Add((byte)((rateBound >> 7) & 0xFF));
        h.Add((byte)(((rateBound & 0x7F) << 1) | 1));
        h.Add((byte)((audio ? 1 : 0) << 2)); // audio_bound, fixed 0, CSPS 0
        h.Add(0xE1); // audio lock, video lock, marker, video_bound 1
        h.Add(0xFF);
        h.AddRange([0xE0, 0xE0, 0xE8]); // video: buffer scale 1024, 232 KB
        if (audio) h.AddRange([0xC0, 0xC0, 0x20]); // audio: scale 128, 4 KB
        return [.. h];
    }

    private static byte[] Psm(VideoCodec codec, bool audio)
    {
        var es = new List<byte> { codec == VideoCodec.H265 ? StreamTypeH265 : StreamTypeH264, 0xE0, 0, 0 };
        if (audio) es.AddRange([StreamTypeG711U, 0xC0, 0, 0]);
        var psmLength = 10 + es.Count; // flags(2) + ps_info_length(2) + es_map_length(2) + es map + CRC(4)
        var p = new List<byte> { 0, 0, 1, 0xBC, (byte)(psmLength >> 8), (byte)psmLength, 0xE0, 0xFF, 0, 0, (byte)(es.Count >> 8), (byte)es.Count };
        p.AddRange(es);
        var crc = Crc32Mpeg(p.ToArray());
        p.AddRange([(byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc]);
        return [.. p];
    }

    private static void VideoPes(Stream s, ReadOnlySpan<byte> frame, long pts)
    {
        var first = true;
        Span<byte> h = stackalloc byte[19];
        while (!frame.IsEmpty)
        {
            var n = Math.Min(frame.Length, MaxPesPayload);
            var headerData = first ? 10 : 0;
            var len = 3 + headerData + n;
            h[0] = 0; h[1] = 0; h[2] = 1; h[3] = 0xE0;
            h[4] = (byte)(len >> 8); h[5] = (byte)len;
            h[6] = first ? (byte)0x8C : (byte)0x88; // '10', priority, data_alignment on the first packet of a frame
            h[7] = first ? (byte)0xC0 : (byte)0x00; // PTS + DTS
            h[8] = (byte)headerData;
            if (first)
            {
                Timestamp(h[9..], 0x3, pts);
                Timestamp(h[14..], 0x1, pts); // no B-frames: DTS = PTS
            }
            s.Write(h[..(9 + headerData)]);
            s.Write(frame[..n]);
            frame = frame[n..];
            first = false;
        }
    }

    private static void AudioPes(Stream s, long pts)
    {
        Span<byte> p = stackalloc byte[14 + AudioFrameBytes];
        p[0] = 0; p[1] = 0; p[2] = 1; p[3] = 0xC0;
        var len = 3 + 5 + AudioFrameBytes;
        p[4] = (byte)(len >> 8); p[5] = (byte)len;
        p[6] = 0x8C; p[7] = 0x80; p[8] = 5;
        Timestamp(p[9..], 0x2, pts);
        p[14..].Fill(0xFF); // µ-law silence
        s.Write(p);
    }

    private static void Timestamp(Span<byte> b, int prefix, long t)
    {
        b[0] = (byte)((prefix << 4) | (int)(((t >> 30) & 0x07) << 1) | 1);
        b[1] = (byte)(t >> 22);
        b[2] = (byte)((((t >> 15) & 0x7F) << 1) | 1);
        b[3] = (byte)(t >> 7);
        b[4] = (byte)(((t & 0x7F) << 1) | 1);
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(i =>
    {
        var c = (uint)i << 24;
        for (var k = 0; k < 8; k++) c = (c & 0x80000000) != 0 ? (c << 1) ^ 0x04C11DB7 : c << 1;
        return c;
    }).ToArray();

    /// <summary>CRC-32/MPEG-2 (no reflection, no final xor) as PSM/PSI sections use.</summary>
    public static uint Crc32Mpeg(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data) crc = (crc << 8) ^ CrcTable[((crc >> 24) ^ b) & 0xFF];
        return crc;
    }
}
