using System.Diagnostics;
using Cv.Core;
using FFmpeg.AutoGen;

namespace Cv.Media.Internal;

/// <summary>An opened input for stream copy: video stream + optional AAC/MP3 audio, everything else discarded.</summary>
internal sealed unsafe class InputFile : IDisposable
{
    public AVFormatContext* Ctx;
    public int VideoIndex = -1;
    public int AudioIndex = -1;

    public AVStream* Video => Ctx->streams[VideoIndex];
    public AVStream* Audio => AudioIndex >= 0 ? Ctx->streams[AudioIndex] : null;

    public static InputFile Open(string url, InterruptState irq, TimeSpan openTimeout, CancellationToken ct)
    {
        var input = new InputFile();
        try
        {
            AVFormatContext* ic = ffmpeg.avformat_alloc_context();
            if (ic == null) throw new OutOfMemoryException();
            irq.Attach(ic);
            AVDictionary* o = null;
            if (MediaUtil.IsRtsp(url))
            {
                MediaUtil.Set(&o, "rtsp_transport", "tcp");
                MediaUtil.Set(&o, "timeout", "10000000");
                MediaUtil.Set(&o, "max_delay", "500000");
                MediaUtil.Set(&o, "probesize", "5000000");
                MediaUtil.Set(&o, "analyzeduration", "5000000");
            }
            else
            {
                // Generous: PS streams appear as their packets do, audio can come late.
                MediaUtil.Set(&o, "probesize", "20000000");
                MediaUtil.Set(&o, "analyzeduration", "20000000");
            }
            AVInputFormat* ifmt = MediaUtil.SniffFormat(url);
            irq.SetDeadline(openTimeout);
            int r = ffmpeg.avformat_open_input(&ic, url, ifmt, &o);
            ffmpeg.av_dict_free(&o);
            if (r < 0) throw Fail(r, irq, ct, "open " + Log.Redact(url));
            input.Ctx = ic;
            r = ffmpeg.avformat_find_stream_info(ic, null);
            if (r < 0) throw Fail(r, irq, ct, "probe " + Log.Redact(url));
            irq.ClearDeadline();

            int vi = ffmpeg.av_find_best_stream(ic, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
            if (vi < 0) throw new InvalidDataException(FFmpegError.Message(Failure.NoVideo));
            input.VideoIndex = vi;
            for (int i = 0; i < ic->nb_streams; i++)
            {
                var par = ic->streams[i]->codecpar;
                if (input.AudioIndex < 0 && par->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO &&
                    par->codec_id is AVCodecID.AV_CODEC_ID_AAC or AVCodecID.AV_CODEC_ID_MP3)
                    input.AudioIndex = i;
            }
            for (int i = 0; i < ic->nb_streams; i++)
                if (i != input.VideoIndex && i != input.AudioIndex) ic->streams[i]->discard = AVDiscard.AVDISCARD_ALL;
            return input;
        }
        catch
        {
            input.Dispose();
            throw;
        }
    }

    public static Exception Fail(int code, InterruptState irq, CancellationToken ct, string context)
    {
        if (ct.IsCancellationRequested) return new OperationCanceledException(ct);
        if (irq.TimedOut) return new IOException(FFmpegError.Message(Failure.Network), new TimeoutException(context));
        return FFmpegError.ToException(code, context);
    }

    public void Dispose()
    {
        if (Ctx == null) return;
        var c = Ctx;
        ffmpeg.avformat_close_input(&c);
        Ctx = null;
    }
}

/// <summary>
/// MP4 output by stream copy: one video track (H.265 tagged hvc1) and optionally one AAC/MP3 track, +faststart.
/// Packets come in already placed on the output timeline; this fixes non-monotonic DTS and tracks the end time.
/// </summary>
internal sealed unsafe class Mp4Writer : IDisposable
{
    private sealed class Track
    {
        public int Index;
        public AVRational OutTb;
        public long LastDts = long.MinValue;
        public long EndUs;
        public AVCodecID Codec;
    }

    private AVFormatContext* _oc;
    private readonly Track?[] _tracks = new Track?[2];
    private bool _ioOpen;

    public AVCodecID VideoCodec => _tracks[0]!.Codec;
    public bool HasAudio => _tracks[1] != null;
    public AVCodecID AudioCodec => _tracks[1]?.Codec ?? AVCodecID.AV_CODEC_ID_NONE;

    /// <summary>Latest end time (pts + duration) over all tracks, microseconds on the output timeline.</summary>
    public long EndUs => Math.Max(_tracks[0]?.EndUs ?? 0, _tracks[1]?.EndUs ?? 0);

    public static Mp4Writer Create(string path, InterruptState irq, AVStream* video, AVStream* audio)
    {
        var w = new Mp4Writer();
        try
        {
            AVFormatContext* oc = null;
            int r = ffmpeg.avformat_alloc_output_context2(&oc, null, "mp4", path);
            if (r < 0 || oc == null) throw FFmpegError.ToException(r < 0 ? r : FFmpegError.ENoMem, "mp4 context");
            w._oc = oc;
            oc->interrupt_callback = irq.Native;
            w._tracks[0] = w.AddStream(video, isVideo: true);
            if (audio != null) w._tracks[1] = w.AddStream(audio, isVideo: false);

            var cb = irq.Native;
            r = ffmpeg.avio_open2(&oc->pb, path, ffmpeg.AVIO_FLAG_WRITE, &cb, null);
            if (r < 0) throw FFmpegError.ToException(r, "create " + path);
            w._ioOpen = true;
            AVDictionary* o = null;
            MediaUtil.Set(&o, "movflags", "+faststart");
            r = ffmpeg.avformat_write_header(oc, &o);
            ffmpeg.av_dict_free(&o);
            if (r < 0) throw FFmpegError.ToException(r == FFmpegError.EInval ? ffmpeg.AVERROR_PATCHWELCOME : r, "mp4 header");
            foreach (var t in w._tracks)
                if (t != null) t.OutTb = oc->streams[t.Index]->time_base;
            return w;
        }
        catch
        {
            w.Dispose();
            throw;
        }
    }

    private Track AddStream(AVStream* src, bool isVideo)
    {
        AVStream* st = ffmpeg.avformat_new_stream(_oc, null);
        if (st == null) throw new OutOfMemoryException();
        int r = ffmpeg.avcodec_parameters_copy(st->codecpar, src->codecpar);
        if (r < 0) throw FFmpegError.ToException(r, "copy codec parameters");
        // Let the muxer pick the tag, except H.265: hvc1 is what Apple/Windows players expect.
        st->codecpar->codec_tag = isVideo && src->codecpar->codec_id == AVCodecID.AV_CODEC_ID_HEVC
            ? (uint)('h' | ('v' << 8) | ('c' << 16) | ('1' << 24))
            : 0;
        st->time_base = src->time_base;
        if (isVideo)
        {
            st->avg_frame_rate = src->avg_frame_rate;
            st->r_frame_rate = src->r_frame_rate;
        }
        return new Track { Index = st->index, Codec = src->codecpar->codec_id };
    }

    /// <summary>Writes (and consumes) a packet whose pts/dts are on the output timeline in <paramref name="inTb"/>.</summary>
    public void Write(AVPacket* pkt, int track, AVRational inTb)
    {
        var t = _tracks[track] ?? throw new InvalidOperationException("no such track");
        ffmpeg.av_packet_rescale_ts(pkt, inTb, t.OutTb);
        if (pkt->dts == ffmpeg.AV_NOPTS_VALUE) pkt->dts = pkt->pts;
        if (pkt->pts == ffmpeg.AV_NOPTS_VALUE) pkt->pts = pkt->dts;
        if (t.LastDts != long.MinValue && pkt->dts <= t.LastDts) pkt->dts = t.LastDts + 1;
        if (pkt->pts < pkt->dts) pkt->pts = pkt->dts;
        t.LastDts = pkt->dts;
        long end = ffmpeg.av_rescale_q(pkt->pts + Math.Max(0, pkt->duration), t.OutTb, MediaUtil.MicroBase);
        if (end > t.EndUs) t.EndUs = end;
        pkt->stream_index = t.Index;
        pkt->pos = -1;
        int r = ffmpeg.av_interleaved_write_frame(_oc, pkt);
        if (r < 0) throw FFmpegError.ToException(r, "write packet");
    }

    public void Finish()
    {
        int r = ffmpeg.av_write_trailer(_oc);
        if (r < 0) throw FFmpegError.ToException(r, "mp4 trailer");
        CloseIo();
    }

    private void CloseIo()
    {
        if (!_ioOpen) return;
        _ioOpen = false;
        ffmpeg.avio_closep(&_oc->pb);
    }

    public void Dispose()
    {
        if (_oc == null) return;
        CloseIo();
        ffmpeg.avformat_free_context(_oc);
        _oc = null;
    }
}

/// <summary>Monotonic, throttled progress (IProgress may post to the UI).</summary>
internal sealed class ProgressThrottle(IProgress<double>? target)
{
    private double _last = -1;
    private long _lastAt;

    public void Report(double value)
    {
        if (target is null) return;
        value = Math.Clamp(value, 0, 1);
        long now = Stopwatch.GetTimestamp();
        if (value <= _last) return;
        if (value - _last < 0.01 && now - _lastAt < Stopwatch.Frequency / 4) return;
        _last = value;
        _lastAt = now;
        target.Report(value);
    }

    public void Complete()
    {
        if (target is null || _last >= 1) return;
        _last = 1;
        target.Report(1);
    }
}
