namespace Cv.Media.Tests.Support;

/// <summary>
/// Writes a Hikvision-like MPEG program stream from an Annex-B H.264/H.265 elementary stream: 40-byte "IMKH" file
/// header, then per frame a pack header, (system header + PSM before keyframes) and PES packets of at most ~4 KB where
/// only the first carries PTS/DTS. FFmpeg's mpeg muxer cannot write H.265 PS with a PSM, hence this helper.
/// The ES must have an access unit delimiter at the start of every frame (x264/x265 "aud=1").
/// </summary>
public static class HikPsWriter
{
    public const byte StreamTypeH264 = 0x1B, StreamTypeH265 = 0x24, StreamTypeG711A = 0x90, StreamTypeG711U = 0x91;
    private const int MaxPesPayload = 4000;

    public sealed record Frame(byte[] Data, bool Key);

    public static void Write(string esPath, bool hevc, int fps, string outPath, byte? audioType = null)
    {
        var frames = SplitAccessUnits(File.ReadAllBytes(esPath), hevc);
        using var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write);
        WriteFileHeader(fs, hevc);
        long frameTicks = 90000 / fps;
        long audioPts = 0;
        const int audioChunk = 160; // 20 ms of 8 kHz G.711
        for (int i = 0; i < frames.Count; i++)
        {
            long pts = 90000 + i * frameTicks; // non-zero start like a real recorder
            WritePackHeader(fs, pts - 3600);
            if (frames[i].Key)
            {
                WriteSystemHeader(fs, audioType.HasValue);
                WritePsm(fs, hevc ? StreamTypeH265 : StreamTypeH264, audioType);
            }
            WriteFramePes(fs, 0xE0, frames[i].Data, pts);
            if (audioType.HasValue)
            {
                // Audio covering this frame's span, in 20 ms chunks.
                while (audioPts <= i * frameTicks)
                {
                    var chunk = new byte[audioChunk];
                    Array.Fill(chunk, audioType == StreamTypeG711U ? (byte)0xFF : (byte)0xD5); // silence
                    WriteFramePes(fs, 0xC0, chunk, 90000 + audioPts, withDts: false);
                    audioPts += 1800;
                }
            }
        }
    }

    public static List<Frame> SplitAccessUnits(byte[] es, bool hevc)
    {
        var starts = new List<(int Pos, int Type)>();
        for (int i = 0; i + 3 < es.Length; i++)
        {
            if (es[i] != 0 || es[i + 1] != 0) continue;
            int sc = es[i + 2] == 1 ? 3 : (es[i + 2] == 0 && i + 4 < es.Length && es[i + 3] == 1 ? 4 : 0);
            if (sc == 0) continue;
            byte h = es[i + sc];
            int type = hevc ? (h >> 1) & 0x3F : h & 0x1F;
            starts.Add((i, type));
            i += sc;
        }
        int aud = hevc ? 35 : 9;
        var frames = new List<Frame>();
        int auStart = -1;
        bool key = false;
        foreach (var (pos, type) in starts)
        {
            if (type == aud)
            {
                if (auStart >= 0) frames.Add(new Frame(es[auStart..pos], key));
                auStart = pos;
                key = false;
            }
            else if (hevc ? type is >= 16 and <= 21 : type == 5) key = true;
        }
        if (auStart >= 0) frames.Add(new Frame(es[auStart..], key));
        if (frames.Count == 0) throw new InvalidDataException("no access unit delimiters in the elementary stream");
        return frames;
    }

    private static void WriteFileHeader(Stream s, bool hevc)
    {
        var h = new byte[40];
        "IMKH"u8.CopyTo(h);
        h[4] = 0x01; h[5] = 0x01;            // version
        h[8] = 0x02;                          // system format: PS
        h[10] = hevc ? (byte)0x05 : (byte)0x01; // video codec hint
        h[12] = 0x11; h[13] = 0x71;           // audio codec hint (G.711)
        h[14] = 0x01; h[16] = 0x40; h[17] = 0x1F;
        s.Write(h);
    }

    private static void WritePackHeader(Stream s, long scr)
    {
        if (scr < 0) scr = 0;
        Span<byte> b = stackalloc byte[14];
        b[0] = 0; b[1] = 0; b[2] = 1; b[3] = 0xBA;
        b[4] = (byte)(0x44 | ((scr >> 27) & 0x38) | ((scr >> 28) & 0x03));
        b[5] = (byte)(scr >> 20);
        b[6] = (byte)(((scr >> 12) & 0xF8) | 0x04 | ((scr >> 13) & 0x03));
        b[7] = (byte)(scr >> 5);
        b[8] = (byte)(((scr << 3) & 0xF8) | 0x04);
        b[9] = 0x01;
        const int muxRate = 0x3FFF; // 50-byte units
        b[10] = (byte)(muxRate >> 14);
        b[11] = (byte)(muxRate >> 6);
        b[12] = (byte)(((muxRate << 2) & 0xFC) | 0x03);
        b[13] = 0xF8; // no stuffing
        s.Write(b);
    }

    private static void WriteSystemHeader(Stream s, bool audio)
    {
        var body = new List<byte>
        {
            0x80, 0x1F, 0xFF,              // marker + rate_bound + marker
            (byte)(audio ? 0x04 : 0x00),   // audio_bound, fixed, CSPS
            0xE1,                          // locks, marker, video_bound = 1
            0x7F,                          // packet_rate_restriction + reserved
            0xE0, 0xE0, 0x80,              // video stream bound
        };
        if (audio) body.AddRange([0xC0, 0xC0, 0x08]);
        s.Write([0, 0, 1, 0xBB, (byte)(body.Count >> 8), (byte)body.Count]);
        s.Write(body.ToArray());
    }

    private static void WritePsm(Stream s, byte videoType, byte? audioType)
    {
        var es = new List<byte> { videoType, 0xE0, 0, 0 };
        if (audioType is { } a) es.AddRange([a, 0xC0, 0, 0]);
        int psmLength = 10 + es.Count; // flags(2) + info len(2) + es map len(2) + es map + crc(4)
        var psm = new List<byte> { 0, 0, 1, 0xBC, (byte)(psmLength >> 8), (byte)psmLength, 0xE0, 0xFF, 0, 0, (byte)(es.Count >> 8), (byte)es.Count };
        psm.AddRange(es);
        uint crc = Crc32Mpeg(psm.ToArray());
        psm.AddRange([(byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc]);
        s.Write(psm.ToArray());
    }

    private static void WriteFramePes(Stream s, byte streamId, byte[] data, long pts, bool withDts = true)
    {
        for (int off = 0; off < data.Length; off += MaxPesPayload)
        {
            int len = Math.Min(MaxPesPayload, data.Length - off);
            bool first = off == 0;
            int headerData = first ? (withDts ? 10 : 5) : 0;
            int pesLength = 3 + headerData + len;
            var h = new List<byte> { 0, 0, 1, streamId, (byte)(pesLength >> 8), (byte)pesLength, (byte)(first ? 0x84 : 0x80) };
            if (first && withDts)
            {
                h.Add(0xC0);
                h.Add(10);
                h.AddRange(Timestamp(0x3, pts));
                h.AddRange(Timestamp(0x1, pts));
            }
            else if (first)
            {
                h.Add(0x80);
                h.Add(5);
                h.AddRange(Timestamp(0x2, pts));
            }
            else
            {
                h.Add(0x00);
                h.Add(0);
            }
            s.Write(h.ToArray());
            s.Write(data, off, len);
        }
    }

    private static byte[] Timestamp(int prefix, long ts) =>
    [
        (byte)((prefix << 4) | (int)(((ts >> 30) & 0x07) << 1) | 1),
        (byte)(ts >> 22),
        (byte)((((ts >> 15) & 0x7F) << 1) | 1),
        (byte)(ts >> 7),
        (byte)(((ts & 0x7F) << 1) | 1),
    ];

    private static uint Crc32Mpeg(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in data)
        {
            crc ^= (uint)b << 24;
            for (int k = 0; k < 8; k++) crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ 0x04C11DB7 : crc << 1;
        }
        return crc;
    }
}
