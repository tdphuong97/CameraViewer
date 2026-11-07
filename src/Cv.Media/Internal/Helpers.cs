using System.Diagnostics;
using FFmpeg.AutoGen;

namespace Cv.Media.Internal;

/// <summary>Wall clock for recorded playback that stops while paused. Units: TimeSpan ticks.</summary>
internal sealed class PlaybackClock
{
    private long _baseWall;
    private long _baseMedia;
    private long _pausedAt = -1;

    private static long Now => (long)(Stopwatch.GetTimestamp() * (TimeSpan.TicksPerSecond / (double)Stopwatch.Frequency));

    public void Rebase(long media)
    {
        _baseMedia = media;
        _baseWall = Now;
        if (_pausedAt >= 0) _pausedAt = _baseWall;
    }

    /// <summary>Ticks until <paramref name="media"/> is due (negative = late).</summary>
    public long Due(long media) => (media - _baseMedia) - ((_pausedAt >= 0 ? _pausedAt : Now) - _baseWall);

    public void Pause()
    {
        if (_pausedAt < 0) _pausedAt = Now;
    }

    public void Resume()
    {
        if (_pausedAt < 0) return;
        _baseWall += Now - _pausedAt;
        _pausedAt = -1;
    }
}

/// <summary>Undoes the 33-bit wrap of MPEG timestamps (every 26.5 h of 90 kHz clock).</summary>
internal struct TimestampUnwrap
{
    private long _offset;
    private long _last;
    private bool _has;

    public long Apply(long ts, int wrapBits)
    {
        if (ts == ffmpeg.AV_NOPTS_VALUE || wrapBits <= 0 || wrapBits >= 63) return ts;
        long period = 1L << wrapBits;
        long v = ts + _offset;
        if (_has)
        {
            if (v < _last - period / 2) { _offset += period; v += period; }
            else if (v > _last + period / 2) { _offset -= period; v -= period; }
        }
        _last = v;
        _has = true;
        return v;
    }
}

internal static unsafe class MediaUtil
{
    public static readonly AVRational TicksBase = new() { num = 1, den = (int)TimeSpan.TicksPerSecond };
    public static readonly AVRational MicroBase = new() { num = 1, den = ffmpeg.AV_TIME_BASE };

    public static bool IsUrl(string s) => s.Contains("://", StringComparison.Ordinal) && !s.StartsWith("file:", StringComparison.OrdinalIgnoreCase);

    public static bool IsRtsp(string s) =>
        s.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("rtsps://", StringComparison.OrdinalIgnoreCase);

    /// <summary>Hikvision PS (IMKH header or bare pack start) goes to the mpeg demuxer directly: probing it is unreliable.</summary>
    public static AVInputFormat* SniffFormat(string path)
    {
        try
        {
            if (IsUrl(path) || !File.Exists(path)) return null;
            Span<byte> head = stackalloc byte[4];
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Read(head) < 4) return null;
            bool imkh = head[0] == (byte)'I' && head[1] == (byte)'M' && head[2] == (byte)'K' && head[3] == (byte)'H';
            bool pack = head[0] == 0 && head[1] == 0 && head[2] == 1 && head[3] == 0xBA;
            return imkh || pack ? ffmpeg.av_find_input_format("mpeg") : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Set(AVDictionary** d, string key, string value) => ffmpeg.av_dict_set(d, key, value, 0);

    /// <summary>Picks the frame duration from the stream rates (fallback 1/25 s), in the stream time base.</summary>
    public static long FrameDuration(AVStream* st)
    {
        AVRational r = st->avg_frame_rate.num > 0 && st->avg_frame_rate.den > 0 ? st->avg_frame_rate : st->r_frame_rate;
        if (r.num <= 0 || r.den <= 0 || r.num / (double)r.den > 120) r = new AVRational { num = 25, den = 1 };
        long d = ffmpeg.av_rescale_q(1, new AVRational { num = r.den, den = r.num }, st->time_base);
        return Math.Max(1, d);
    }

    public static string CodecLabel(AVCodecID id) => id switch
    {
        AVCodecID.AV_CODEC_ID_H264 => "H.264",
        AVCodecID.AV_CODEC_ID_HEVC => "H.265",
        _ => (ffmpeg.avcodec_get_name(id) ?? "?").ToUpperInvariant(),
    };
}
