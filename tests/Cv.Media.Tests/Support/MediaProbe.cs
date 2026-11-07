using FFmpeg.AutoGen;

namespace Cv.Media.Tests.Support;

public sealed record StreamInfo(AVMediaType Type, AVCodecID Codec, string Tag, int Width, int Height, int Packets,
    double FirstPts, double LastPts, bool FirstIsKey, bool DtsMonotonic);

public sealed record ProbeResult(List<StreamInfo> Streams, string[] TopLevelBoxes)
{
    public StreamInfo Video => Streams.Single(s => s.Type == AVMediaType.AVMEDIA_TYPE_VIDEO);
    public double VideoSpan => Video.LastPts - Video.FirstPts;
}

/// <summary>Reads an output file packet by packet to check what the tools wrote.</summary>
public static unsafe class MediaProbe
{
    public static ProbeResult Probe(string path)
    {
        AVFormatContext* ic = null;
        int r = ffmpeg.avformat_open_input(&ic, path, null, null);
        if (r < 0) throw new InvalidOperationException($"open {path}: {r}");
        AVPacket* pkt = ffmpeg.av_packet_alloc();
        try
        {
            ffmpeg.avformat_find_stream_info(ic, null);
            int n = (int)ic->nb_streams;
            var count = new int[n];
            var first = new double[n];
            var last = new double[n];
            var firstKey = new bool[n];
            var lastDts = new long[n];
            var mono = new bool[n];
            for (int i = 0; i < n; i++) { first[i] = double.NaN; lastDts[i] = long.MinValue; mono[i] = true; }
            while (ffmpeg.av_read_frame(ic, pkt) >= 0)
            {
                int i = pkt->stream_index;
                var tb = ic->streams[i]->time_base;
                double pts = pkt->pts == ffmpeg.AV_NOPTS_VALUE ? double.NaN : pkt->pts * ffmpeg.av_q2d(tb);
                if (count[i] == 0) { first[i] = pts; firstKey[i] = (pkt->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0; }
                if (!double.IsNaN(pts) && (count[i] == 0 || pts > last[i])) last[i] = pts;
                if (pkt->dts != ffmpeg.AV_NOPTS_VALUE)
                {
                    if (pkt->dts <= lastDts[i]) mono[i] = false;
                    lastDts[i] = pkt->dts;
                }
                count[i]++;
                ffmpeg.av_packet_unref(pkt);
            }
            var list = new List<StreamInfo>();
            for (int i = 0; i < n; i++)
            {
                var par = ic->streams[i]->codecpar;
                uint tag = par->codec_tag;
                string tagText = tag == 0 ? "" : new string([(char)(tag & 0xFF), (char)((tag >> 8) & 0xFF), (char)((tag >> 16) & 0xFF), (char)(tag >> 24)]);
                list.Add(new StreamInfo(par->codec_type, par->codec_id, tagText, par->width, par->height, count[i], first[i], last[i], firstKey[i], mono[i]));
            }
            return new ProbeResult(list, TopLevelBoxes(path));
        }
        finally
        {
            ffmpeg.av_packet_free(&pkt);
            ffmpeg.avformat_close_input(&ic);
        }
    }

    /// <summary>MP4 top-level box names in file order (faststart = moov before mdat).</summary>
    public static string[] TopLevelBoxes(string path)
    {
        var names = new List<string>();
        using var fs = File.OpenRead(path);
        Span<byte> h = stackalloc byte[16];
        long pos = 0;
        while (pos + 8 <= fs.Length && names.Count < 32)
        {
            fs.Position = pos;
            if (fs.Read(h[..8]) < 8) break;
            long size = (uint)(h[0] << 24 | h[1] << 16 | h[2] << 8 | h[3]);
            string type = System.Text.Encoding.ASCII.GetString(h.Slice(4, 4));
            if (!type.All(c => c is >= ' ' and <= '~')) break;
            if (size == 1)
            {
                if (fs.Read(h[..8]) < 8) break;
                size = (long)((ulong)h[0] << 56 | (ulong)h[1] << 48 | (ulong)h[2] << 40 | (ulong)h[3] << 32 | (ulong)h[4] << 24 | (ulong)h[5] << 16 | (ulong)h[6] << 8 | h[7]);
            }
            else if (size == 0) size = fs.Length - pos;
            if (size < 8) break;
            names.Add(type);
            pos += size;
        }
        return names.ToArray();
    }
}
