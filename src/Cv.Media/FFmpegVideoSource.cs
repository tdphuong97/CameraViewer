using System.Diagnostics;
using Cv.Core;
using Cv.Core.Video;
using Cv.Media.Internal;
using FFmpeg.AutoGen;

namespace Cv.Media;

/// <summary>
/// One RTSP stream or file: open, demux (video only), decode (D3D11VA or software), scale into the target box as BGRA,
/// hand frames to the UI through a triple buffer. Everything native is owned by one background thread.
/// </summary>
internal sealed unsafe class FFmpegVideoSource : IVideoSource
{
    private static readonly TimeSpan ReadStall = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ControlTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(2);
    private static readonly int[] BackoffSeconds = [1, 2, 5, 10];
    private const long MaxPacingGap = 3 * TimeSpan.TicksPerSecond;   // larger timestamp jumps are rebased, not waited for
    private const long MaxLate = TimeSpan.TicksPerSecond;
    private const long MaxBackStep = TimeSpan.TicksPerSecond / 2;
    private const int HwWidthThreshold = 1280;
    private const string NoFrameMessage = "Chưa có hình để chụp";

    private enum End { Stopped, Ended, Loop, Lost }

    private readonly record struct Outcome(End Kind, Failure Failure = Failure.Other, bool HadFrames = false);

    private readonly VideoSourceOptions _opt;
    private readonly FFmpegVideoSourceFactory _factory;
    private readonly bool _isRtsp;
    private readonly bool _isFile;
    private readonly bool _paced;
    private readonly FrameExchange _frames = new();
    private readonly InterruptState _irq = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly RateMeter _rate = new();
    private readonly PlaybackClock _clock = new();
    private readonly object _gate = new();

    private Thread? _thread;
    private int _started;
    private int _disposed;
    private volatile bool _stopping;
    private VideoState _state = VideoState.Idle;
    private string? _error;
    private VideoStats _stats;
    private long _positionTicks;
    private long _targetSize;
    private volatile bool _pauseWanted;
    private volatile bool _hasFrame;
    private TaskCompletionSource<FrameSnapshot>? _capture;

    // Decoder thread only.
    private AVFormatContext* _fmt;
    private AVCodecContext* _dec;
    private AVPacket* _pkt;
    private AVFrame* _frame;
    private AVFrame* _swFrame;
    private AVFrame* _held;
    private AVFrame* _last;
    private SwsContext* _sws;
    private int _swsSrcW, _swsSrcH, _swsSrcFmt = -1, _swsDstW, _swsDstH, _swsCs = -1, _swsRange = -1;
    private long _appliedTarget = -1;
    private int _videoIndex;
    private AVRational _timeBase;
    private string _codecLabel = "";
    private bool _hw;
    private bool _hwDisabled;
    private bool _holdsHwDevice;
    private bool _everPlayed;
    private bool _reanchor;
    private bool _gotHwFrame;
    private int _decodeErrors;
    private long _firstMedia = long.MinValue;
    private long _lastRel;
    private long _frameTicks = TimeSpan.TicksPerSecond / 25;
    private long _liveStart;
    private readonly byte*[] _srcData = new byte*[4];
    private readonly int[] _srcStride = new int[4];
    private readonly byte*[] _dstData = new byte*[4];
    private readonly int[] _dstStride = new int[4];

    public FFmpegVideoSource(VideoSourceOptions options, FFmpegVideoSourceFactory factory)
    {
        _opt = options;
        _factory = factory;
        _isRtsp = MediaUtil.IsRtsp(options.Url);
        _isFile = !MediaUtil.IsUrl(options.Url);
        // Recorded playback is paced; a local file opened as "live" loops at real speed (demo / tests).
        _paced = !options.IsLive || _isFile;
        _targetSize = Pack(options.TargetWidth, options.TargetHeight);
    }

    public VideoState State { get { lock (_gate) return _state; } }
    public string? Error { get { lock (_gate) return _error; } }
    public event Action<IVideoSource>? StateChanged;
    public VideoStats Stats { get { lock (_gate) return _stats; } }
    public TimeSpan Position => TimeSpan.FromTicks(Interlocked.Read(ref _positionTicks));

    /// <summary>Native bytes held by the BGRA buffers (tests, diagnostics).</summary>
    internal long FrameBufferBytes => _frames.AllocatedBytes;

    /// <summary>Managed bytes the decoder thread has allocated so far, sampled per frame (tests: must not grow per frame).</summary>
    internal long DecoderThreadAllocatedBytes => Interlocked.Read(ref _threadAllocated);
    private long _threadAllocated;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        _thread = new Thread(Run) { IsBackground = true, Name = "cv-video" };
        _thread.Start();
    }

    public void SetTargetSize(int width, int height)
    {
        Interlocked.Exchange(ref _targetSize, Pack(width, height));
        _wake.SafeSet();
    }

    public void Pause()
    {
        if (_opt.IsLive) return;
        _pauseWanted = true;
        _wake.SafeSet();
    }

    public void Resume()
    {
        if (_opt.IsLive) return;
        _pauseWanted = false;
        _wake.SafeSet();
    }

    public bool TryCopyLatest(ref long lastSequence, FrameCopier copier) => _frames.TryCopyLatest(ref lastSequence, copier);

    public Task<FrameSnapshot> CaptureAsync(CancellationToken ct)
    {
        if (_disposed != 0) return Task.FromException<FrameSnapshot>(new ObjectDisposedException(nameof(FFmpegVideoSource)));
        if (!_hasFrame) return Task.FromException<FrameSnapshot>(new InvalidOperationException(NoFrameMessage));
        TaskCompletionSource<FrameSnapshot> tcs;
        lock (_gate)
        {
            _capture ??= new TaskCompletionSource<FrameSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            tcs = _capture;
        }
        _wake.SafeSet();
        return ct.CanBeCanceled ? tcs.Task.WaitAsync(ct) : tcs.Task;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stopping = true;
        _irq.Stop();
        TaskCompletionSource<FrameSnapshot>? cap;
        lock (_gate) { cap = _capture; _capture = null; }
        cap?.TrySetException(new ObjectDisposedException(nameof(FFmpegVideoSource)));

        var t = _thread;
        if (t is null)
        {
            // Never started: nothing native beyond the buffers.
            _frames.Free();
            _irq.Dispose();
            _wake.Dispose();
            return;
        }
        _wake.SafeSet();
        // FFmpeg polls the interrupt callback about every 100 ms; the thread frees everything when it leaves.
        if (t != Thread.CurrentThread && !t.Join(TimeSpan.FromMilliseconds(900)))
            Log.Warn($"Video thread still busy after Dispose ({Log.Redact(_opt.Url)}); it frees its memory when the call returns.");
    }

    // ------------------------------------------------------------------ thread

    private void Run()
    {
        try
        {
            _pkt = ffmpeg.av_packet_alloc();
            _frame = ffmpeg.av_frame_alloc();
            _swFrame = ffmpeg.av_frame_alloc();
            _held = ffmpeg.av_frame_alloc();
            SetState(VideoState.Connecting, null);
            int backoff = 0;
            while (!_stopping)
            {
                var o = OpenAndPlay();
                CloseInput();
                if (_stopping) break;
                if (o.Kind == End.Loop) continue;
                if (o.Kind == End.Ended)
                {
                    SetState(VideoState.Ended, null);
                    IdleUntilStopped();
                    break;
                }
                if (o.HadFrames) { backoff = 0; _everPlayed = true; }
                // A stream that played before and now answers 404 is being restarted (recorder reboot, camera
                // re-plugged): keep trying. Wrong credentials never heal and retries could lock the account.
                bool permanent = FFmpegError.IsPermanent(o.Failure) && !(_everPlayed && o.Failure == Failure.NotFound);
                bool retry = _opt.IsLive && _opt.Reconnect && !permanent;
                if (!retry)
                {
                    SetState(VideoState.Failed, o.HadFrames && o.Failure == Failure.Network ? "Mất kết nối đầu ghi" : FFmpegError.Message(o.Failure));
                    IdleUntilStopped();
                    break;
                }
                var msg = o.HadFrames ? "Mất kết nối, đang thử lại…" : FFmpegError.Message(o.Failure) + ", đang thử lại…";
                SetState(VideoState.Reconnecting, msg);
                WaitServicing(TimeSpan.FromSeconds(BackoffSeconds[Math.Min(backoff++, BackoffSeconds.Length - 1)]));
            }
        }
        catch (Exception ex)
        {
            Log.Error("Video source crashed: " + Log.Redact(_opt.Url), ex);
            SetState(VideoState.Failed, FFmpegError.Message(Failure.Other));
        }
        finally
        {
            FreeNative();
        }
    }

    private Outcome OpenAndPlay()
    {
        bool hadFrames = false;
        int r = OpenInput();
        if (r < 0)
        {
            if (_stopping) return new Outcome(End.Stopped);
            var f = r == ffmpeg.AVERROR_STREAM_NOT_FOUND && _fmt != null ? Failure.NoVideo
                : _irq.TimedOut ? Failure.Network : FFmpegError.Classify(r);
            Log.Warn($"Open failed ({Log.Redact(_opt.Url)}): {(_irq.TimedOut ? "timeout" : FFmpegError.Describe(r))}");
            return new Outcome(End.Lost, f);
        }
        r = OpenDecoder(allowHw: !_hwDisabled);
        if (r < 0)
        {
            Log.Warn($"Decoder open failed ({Log.Redact(_opt.Url)}): {FFmpegError.Describe(r)}");
            return new Outcome(End.Lost, Failure.Unsupported);
        }
        _firstMedia = long.MinValue;
        _rate.Reset();
        _decodeErrors = 0;

        while (true)
        {
            if (_stopping) return new Outcome(End.Stopped);
            Service();
            if (!_opt.IsLive && _pauseWanted && !PauseLoop()) return new Outcome(End.Stopped);

            _irq.SetDeadline(ReadStall);
            r = ffmpeg.av_read_frame(_fmt, _pkt);
            _irq.ClearDeadline();
            if (r >= 0 && _irq.TimedOut)
            {
                // The aborted read still hands out the parser's last buffered frame; the link is stalled all the same.
                ffmpeg.av_packet_unref(_pkt);
                r = ffmpeg.AVERROR_EXIT;
            }
            if (r < 0)
            {
                if (_stopping) return new Outcome(End.Stopped);
                if (r == FFmpegError.EAgain) { _wake.WaitOne(10); continue; }
                if (r == ffmpeg.AVERROR_EOF)
                {
                    if (!Drain(ref hadFrames)) return new Outcome(End.Stopped);
                    if (_opt.IsLive) return _isFile ? new Outcome(End.Loop, HadFrames: hadFrames) : new Outcome(End.Lost, Failure.Network, hadFrames);
                    return new Outcome(End.Ended, HadFrames: hadFrames);
                }
                Log.Warn($"Read failed ({Log.Redact(_opt.Url)}): {(_irq.TimedOut ? "no data for 10 s" : FFmpegError.Describe(r))}");
                return new Outcome(End.Lost, _irq.TimedOut ? Failure.Network : FFmpegError.Classify(r), hadFrames);
            }
            if (_pkt->stream_index != _videoIndex)
            {
                ffmpeg.av_packet_unref(_pkt);
                continue;
            }
            _rate.AddBytes(_pkt->size);
            r = ffmpeg.avcodec_send_packet(_dec, _pkt);
            ffmpeg.av_packet_unref(_pkt);
            if (r < 0 && r != FFmpegError.EAgain)
            {
                _decodeErrors++;
                // A hardware decoder that never produced a picture gets replaced by software.
                if (_hw && !_gotHwFrame && _decodeErrors >= 30 && !SwitchToSoftware("decode errors"))
                    return new Outcome(End.Lost, Failure.Unsupported, hadFrames);
                continue;
            }
            if (!ReceiveFrames(ref hadFrames)) return new Outcome(_stopping ? End.Stopped : End.Lost, Failure.Unsupported, hadFrames);
        }
    }

    private int OpenInput()
    {
        _fmt = ffmpeg.avformat_alloc_context();
        if (_fmt == null) return FFmpegError.ENoMem;
        _irq.Attach(_fmt);
        AVDictionary* o = null;
        if (_isRtsp)
        {
            MediaUtil.Set(&o, "rtsp_transport", "tcp");
            MediaUtil.Set(&o, "timeout", "10000000");               // socket I/O, microseconds
            MediaUtil.Set(&o, "allowed_media_types", "video");      // audio is not even set up
            MediaUtil.Set(&o, "max_delay", "500000");
        }
        if (_opt.IsLive && !_isFile)
        {
            // Probe stops as soon as the first keyframe gives the size; no frame-rate probing. "nobuffer" is not used:
            // it drops the packets read while probing, which delays the first picture to the next keyframe.
            MediaUtil.Set(&o, "probesize", "1000000");
            MediaUtil.Set(&o, "analyzeduration", "3000000");
            MediaUtil.Set(&o, "fpsprobesize", "0");
        }
        else
        {
            MediaUtil.Set(&o, "probesize", "5000000");
            MediaUtil.Set(&o, "analyzeduration", "5000000");
        }
        AVInputFormat* ifmt = _isFile ? MediaUtil.SniffFormat(_opt.Url) : null;
        _irq.SetDeadline(_opt.OpenTimeout);
        var f = _fmt;
        int r = ffmpeg.avformat_open_input(&f, _opt.Url, ifmt, &o);
        _fmt = f; // freed and null on failure
        ffmpeg.av_dict_free(&o);
        if (r < 0) return r;
        r = ffmpeg.avformat_find_stream_info(_fmt, null);
        if (r < 0) return r;
        _irq.ClearDeadline();

        AVCodec* codec = null;
        int vi = ffmpeg.av_find_best_stream(_fmt, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &codec, 0);
        if (vi < 0) return vi;
        for (int i = 0; i < _fmt->nb_streams; i++)
            if (i != vi) _fmt->streams[i]->discard = AVDiscard.AVDISCARD_ALL;
        _videoIndex = vi;
        _timeBase = _fmt->streams[vi]->time_base;
        return 0;
    }

    private int OpenDecoder(bool allowHw)
    {
        AVStream* st = _fmt->streams[_videoIndex];
        AVCodecParameters* par = st->codecpar;
        AVCodec* codec = ffmpeg.avcodec_find_decoder(par->codec_id);
        if (codec == null) return ffmpeg.AVERROR_DECODER_NOT_FOUND;
        _dec = ffmpeg.avcodec_alloc_context3(codec);
        if (_dec == null) return FFmpegError.ENoMem;
        int r = ffmpeg.avcodec_parameters_to_context(_dec, par);
        if (r < 0) return r;
        _dec->pkt_timebase = st->time_base;
        if (_opt.IsLive) _dec->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;

        bool hw = false;
        if (allowHw && _opt.HwDecode == HwDecodeMode.Auto && par->width > HwWidthThreshold && SupportsD3D11(codec))
        {
            var dev = _factory.RefHardwareDevice();
            if (dev != null)
            {
                _holdsHwDevice = true;
                _dec->hw_device_ctx = dev; // the codec context owns this reference
                _dec->thread_count = 1;
                hw = true;
            }
        }
        if (!hw)
        {
            if (par->width > HwWidthThreshold)
            {
                _dec->thread_type = ffmpeg.FF_THREAD_FRAME | ffmpeg.FF_THREAD_SLICE;
                _dec->thread_count = Math.Clamp(Environment.ProcessorCount, 1, 4);
            }
            else
            {
                // Slice threads share one picture pool; frame threads would multiply it.
                _dec->thread_type = ffmpeg.FF_THREAD_SLICE;
                _dec->thread_count = 2;
            }
        }
        r = ffmpeg.avcodec_open2(_dec, codec, null);
        if (r < 0 && hw)
        {
            Log.Warn("Hardware decoder open failed, using software: " + FFmpegError.Describe(r));
            FreeDecoder();
            _hwDisabled = true;
            return OpenDecoder(allowHw: false);
        }
        _hw = hw && r >= 0;
        _gotHwFrame = false;
        _codecLabel = MediaUtil.CodecLabel(par->codec_id);
        return r;
    }

    /// <summary>Frees the decoder and hands the shared device reference back (the factory drops an idle device).</summary>
    private void FreeDecoder()
    {
        if (_dec != null)
        {
            var d = _dec;
            ffmpeg.avcodec_free_context(&d);
            _dec = null;
        }
        if (_holdsHwDevice)
        {
            _holdsHwDevice = false;
            _factory.ReleaseHardwareDevice();
        }
    }

    private static bool SupportsD3D11(AVCodec* codec)
    {
        for (int i = 0; ; i++)
        {
            AVCodecHWConfig* c = ffmpeg.avcodec_get_hw_config(codec, i);
            if (c == null) return false;
            if (c->device_type == AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA &&
                (c->methods & (int)AvCodecHwConfigMethod.AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX) != 0)
                return true;
        }
    }

    private bool SwitchToSoftware(string why)
    {
        Log.Warn($"Hardware decoding dropped ({why}), using software: {Log.Redact(_opt.Url)}");
        _hwDisabled = true;
        FreeDecoder();
        _decodeErrors = 0;
        return OpenDecoder(allowHw: false) >= 0;
    }

    /// <summary>False when the thread must stop (or the decoder could not be replaced).</summary>
    private bool ReceiveFrames(ref bool hadFrames)
    {
        while (true)
        {
            int r = ffmpeg.avcodec_receive_frame(_dec, _frame);
            if (r == FFmpegError.EAgain || r == ffmpeg.AVERROR_EOF) return true;
            if (r < 0) { _decodeErrors++; return true; }
            int shown = Present(_frame);
            if (shown < 0) return false;
            if (shown > 0) hadFrames = true;
        }
    }

    private bool Drain(ref bool hadFrames)
    {
        if (_dec == null) return true;
        ffmpeg.avcodec_send_packet(_dec, null);
        bool ok = ReceiveFrames(ref hadFrames);
        ffmpeg.avcodec_flush_buffers(_dec);
        return ok && !_stopping;
    }

    /// <summary>1 shown, 0 skipped, -1 stop.</summary>
    private int Present(AVFrame* frame)
    {
        // Pace on the decoded frame first: while waiting, the previous picture stays intact for capture/re-scale.
        long ts = frame->best_effort_timestamp != ffmpeg.AV_NOPTS_VALUE ? frame->best_effort_timestamp : frame->pts;
        bool first = _firstMedia == long.MinValue;
        long media = ts != ffmpeg.AV_NOPTS_VALUE ? ffmpeg.av_rescale_q(ts, _timeBase, MediaUtil.TicksBase) : (first ? 0 : _firstMedia + _lastRel + _frameTicks);
        if (first)
        {
            _firstMedia = media;
            _lastRel = 0;
            _clock.Rebase(0);
            if (_liveStart == 0) _liveStart = Stopwatch.GetTimestamp();
        }
        // FFmpeg restarts the RTP timestamp base on PLAY (after a pause), and a timeline never runs backwards in
        // recorded playback: continue one frame after the last picture instead.
        if (!first && (_reanchor || media - _firstMedia < _lastRel - MaxBackStep))
        {
            _reanchor = false;
            _firstMedia = media - (_lastRel + _frameTicks);
            _clock.Rebase(_lastRel + _frameTicks);
        }
        long rel = media - _firstMedia;
        if (!first && rel > _lastRel && rel - _lastRel < TimeSpan.TicksPerSecond) _frameTicks = rel - _lastRel;

        if (_paced)
        {
            long due = _clock.Due(rel);
            if (due > MaxPacingGap || due < -MaxLate) _clock.Rebase(rel);
            else if (due > 0 && !WaitUntilDue(rel)) return -1;
        }
        if (_stopping) return -1;

        AVFrame* src = frame;
        bool hwFrame = frame->format == (int)AVPixelFormat.AV_PIX_FMT_D3D11;
        if (hwFrame)
        {
            if (_swFrame->buf[0] != null && (_swFrame->width != frame->width || _swFrame->height != frame->height))
            {
                if (_last == _swFrame) _last = null;
                ffmpeg.av_frame_unref(_swFrame);
            }
            int r = ffmpeg.av_hwframe_transfer_data(_swFrame, frame, 0);
            if (r < 0)
            {
                ffmpeg.av_frame_unref(frame);
                return SwitchToSoftware("transfer: " + FFmpegError.Describe(r)) ? 0 : -1;
            }
            _swFrame->colorspace = frame->colorspace;
            _swFrame->color_range = frame->color_range;
            _swFrame->sample_aspect_ratio = frame->sample_aspect_ratio;
            ffmpeg.av_frame_unref(frame); // give the surface back to the decoder pool now
            src = _swFrame;
            _gotHwFrame = true;
        }

        // Position, stats and state first: whoever sees the frame also sees them.
        _lastRel = rel;
        long pos = _opt.IsLive
            ? (long)((Stopwatch.GetTimestamp() - _liveStart) * (TimeSpan.TicksPerSecond / (double)Stopwatch.Frequency))
            : rel;
        Interlocked.Exchange(ref _positionTicks, Math.Max(0, pos));
        bool refreshed = _rate.AddFrame();
        if (first || refreshed)
        {
            lock (_gate) _stats = new VideoStats(_codecLabel, src->width, src->height, _rate.Fps, _rate.Kbps, hwFrame);
        }
        if (first || State is not (VideoState.Playing or VideoState.Paused)) SetState(VideoState.Playing, null);

        Scale(src);
        // Keep the shown picture for capture and for re-scaling while paused.
        ffmpeg.av_frame_unref(_held);
        if (hwFrame) _last = _swFrame;
        else
        {
            ffmpeg.av_frame_move_ref(_held, frame);
            _last = _held;
        }
        _hasFrame = true;
        Interlocked.Exchange(ref _threadAllocated, GC.GetAllocatedBytesForCurrentThread());
        return 1;
    }

    /// <summary>Waits for the frame's due time, servicing requests and pausing. False = stop.</summary>
    private bool WaitUntilDue(long rel)
    {
        while (true)
        {
            if (_stopping) return false;
            if (!_opt.IsLive && _pauseWanted)
            {
                if (!PauseLoop()) return false;
                continue;
            }
            Service();
            long due = _clock.Due(rel);
            if (due <= 0) return true;
            _wake.WaitOne((int)Math.Clamp(due / TimeSpan.TicksPerMillisecond, 1, 200));
        }
    }

    /// <summary>Recorded playback: RTSP PAUSE, frozen clock, then PLAY. False = stop.</summary>
    private bool PauseLoop()
    {
        if (_isRtsp && _fmt != null)
        {
            _irq.SetDeadline(ControlTimeout);
            int r = ffmpeg.av_read_pause(_fmt);
            _irq.ClearDeadline();
            if (r < 0 && !_stopping) Log.Warn("RTSP PAUSE failed: " + FFmpegError.Describe(r));
        }
        _clock.Pause();
        SetState(VideoState.Paused, null);
        while (_pauseWanted && !_stopping)
        {
            Service();
            _wake.WaitOne(250);
        }
        if (_stopping) return false;
        if (_isRtsp && _fmt != null)
        {
            _irq.SetDeadline(ControlTimeout);
            int r = ffmpeg.av_read_play(_fmt);
            _irq.ClearDeadline();
            // A failed PLAY (session expired during a long pause) surfaces as a read error next.
            if (r < 0 && !_stopping) Log.Warn("RTSP PLAY failed: " + FFmpegError.Describe(r));
            _reanchor = true;
        }
        _clock.Resume();
        SetState(VideoState.Playing, null);
        return true;
    }

    private void WaitServicing(TimeSpan delay)
    {
        long end = Stopwatch.GetTimestamp() + (long)(delay.TotalSeconds * Stopwatch.Frequency);
        while (!_stopping)
        {
            Service();
            long left = end - Stopwatch.GetTimestamp();
            if (left <= 0) return;
            _wake.WaitOne((int)Math.Clamp(left * 1000 / Stopwatch.Frequency, 1, 250));
        }
    }

    private void IdleUntilStopped()
    {
        while (!_stopping)
        {
            Service();
            _wake.WaitOne(500);
        }
    }

    /// <summary>Pending capture; re-scale of the shown picture when the box changed while nothing is decoding.</summary>
    private void Service()
    {
        if (Volatile.Read(ref _capture) != null)
        {
            TaskCompletionSource<FrameSnapshot>? cap;
            lock (_gate) { cap = _capture; _capture = null; }
            if (cap != null) DoCapture(cap);
        }

        if (_last != null && _hasFrame && Interlocked.Read(ref _targetSize) != _appliedTarget)
        {
            var s = State;
            if (s is VideoState.Paused or VideoState.Ended or VideoState.Failed or VideoState.Reconnecting) Scale(_last);
        }
    }

    // ------------------------------------------------------------------ pixels

    internal static (int W, int H) FitBox(int srcW, int srcH, int sarNum, int sarDen, int boxW, int boxH)
    {
        double baseW = srcW, baseH = srcH;
        if (sarNum > 0 && sarDen > 0 && sarNum != sarDen) baseW = srcW * (double)sarNum / sarDen;
        double scale = 1.0;
        if (boxW > 0) scale = Math.Min(scale, boxW / baseW);
        if (boxH > 0) scale = Math.Min(scale, boxH / baseH);
        int w = Math.Max(2, (int)Math.Floor(baseW * scale + 1e-6) & ~1);
        int h = Math.Max(2, (int)Math.Floor(baseH * scale + 1e-6) & ~1);
        return (w, h);
    }

    private void Scale(AVFrame* src)
    {
        int sw = src->width, sh = src->height;
        if (sw <= 0 || sh <= 0 || src->data[0] == null) return;
        long packed = Interlocked.Read(ref _targetSize);
        var (dw, dh) = FitBox(sw, sh, src->sample_aspect_ratio.num, src->sample_aspect_ratio.den, (int)(packed >> 32), (int)(packed & 0xFFFFFFFF));
        int fmt = src->format;
        int cs = SwsColorspace(src);
        int range = IsFullRange(src) ? 1 : 0;
        if (_sws == null || sw != _swsSrcW || sh != _swsSrcH || fmt != _swsSrcFmt || dw != _swsDstW || dh != _swsDstH || cs != _swsCs || range != _swsRange)
        {
            _sws = ffmpeg.sws_getCachedContext(_sws, sw, sh, (AVPixelFormat)fmt, dw, dh, AVPixelFormat.AV_PIX_FMT_BGRA,
                (int)SwsFlags.SWS_FAST_BILINEAR, null, null, null);
            if (_sws == null) return;
            ApplyColorspace(_sws, cs, range);
            (_swsSrcW, _swsSrcH, _swsSrcFmt, _swsDstW, _swsDstH, _swsCs, _swsRange) = (sw, sh, fmt, dw, dh, cs, range);
        }
        byte* dst = _frames.BeginWrite(dw, dh, out int stride);
        for (uint i = 0; i < 4; i++)
        {
            _srcData[i] = src->data[i];
            _srcStride[i] = src->linesize[i];
        }
        _dstData[0] = dst;
        _dstStride[0] = stride;
        ffmpeg.sws_scale(_sws, _srcData, _srcStride, 0, sh, _dstData, _dstStride);
        _frames.Publish();
        _appliedTarget = packed;
    }

    private static bool IsFullRange(AVFrame* f) =>
        f->color_range == AVColorRange.AVCOL_RANGE_JPEG ||
        f->format is (int)AVPixelFormat.AV_PIX_FMT_YUVJ420P or (int)AVPixelFormat.AV_PIX_FMT_YUVJ422P or (int)AVPixelFormat.AV_PIX_FMT_YUVJ444P;

    private static int SwsColorspace(AVFrame* f) => f->colorspace switch
    {
        AVColorSpace.AVCOL_SPC_BT709 => ffmpeg.SWS_CS_ITU709,
        AVColorSpace.AVCOL_SPC_BT2020_NCL or AVColorSpace.AVCOL_SPC_BT2020_CL => ffmpeg.SWS_CS_BT2020,
        AVColorSpace.AVCOL_SPC_SMPTE240M => ffmpeg.SWS_CS_SMPTE240M,
        AVColorSpace.AVCOL_SPC_FCC => ffmpeg.SWS_CS_FCC,
        // Unspecified HD material is BT.709 in practice (players assume the same).
        AVColorSpace.AVCOL_SPC_UNSPECIFIED when f->height >= 720 => ffmpeg.SWS_CS_ITU709,
        _ => ffmpeg.SWS_CS_DEFAULT,
    };

    internal static void ApplyColorspace(SwsContext* ctx, int cs, int fullRange)
    {
        int* inv, table;
        int srcRange, dstRange, brightness, contrast, saturation;
        if (ffmpeg.sws_getColorspaceDetails(ctx, &inv, &srcRange, &table, &dstRange, &brightness, &contrast, &saturation) < 0) return;
        int* coef = ffmpeg.sws_getCoefficients(cs);
        var src = new int_array4();
        var dst = new int_array4();
        for (uint i = 0; i < 4; i++)
        {
            src[i] = coef[i];
            dst[i] = table[i];
        }
        ffmpeg.sws_setColorspaceDetails(ctx, in src, fullRange, in dst, dstRange, brightness, contrast, saturation);
    }

    private void DoCapture(TaskCompletionSource<FrameSnapshot> tcs)
    {
        try
        {
            AVFrame* f = _last;
            if (f == null || !_hasFrame || f->data[0] == null) { tcs.TrySetException(new InvalidOperationException(NoFrameMessage)); return; }
            int w = f->width & ~1, h = f->height & ~1;
            SwsContext* ctx = ffmpeg.sws_getContext(f->width, f->height, (AVPixelFormat)f->format, w, h, AVPixelFormat.AV_PIX_FMT_BGRA,
                (int)SwsFlags.SWS_BILINEAR, null, null, null);
            if (ctx == null) { tcs.TrySetException(new InvalidOperationException("Không chụp được ảnh")); return; }
            try
            {
                ApplyColorspace(ctx, SwsColorspace(f), IsFullRange(f) ? 1 : 0);
                var buf = new byte[w * h * 4];
                var src = new byte*[4];
                var srcStride = new int[4];
                for (uint i = 0; i < 4; i++) { src[i] = f->data[i]; srcStride[i] = f->linesize[i]; }
                fixed (byte* p = buf)
                {
                    var dst = new byte*[] { p, null, null, null };
                    var dstStride = new[] { w * 4, 0, 0, 0 };
                    ffmpeg.sws_scale(ctx, src, srcStride, 0, f->height, dst, dstStride);
                }
                tcs.TrySetResult(new FrameSnapshot(buf, w, h, w * 4));
            }
            finally
            {
                ffmpeg.sws_freeContext(ctx);
            }
        }
        catch (Exception ex)
        {
            tcs.TrySetException(ex);
        }
    }

    // ------------------------------------------------------------------ state / cleanup

    private void SetState(VideoState s, string? error)
    {
        lock (_gate)
        {
            if (_stopping || (_state == s && _error == error)) return;
            _state = s;
            _error = error;
        }
        try
        {
            StateChanged?.Invoke(this);
        }
        catch (Exception ex)
        {
            Log.Error("StateChanged handler threw", ex);
        }
    }

    private void CloseInput()
    {
        FreeDecoder();
        if (_fmt != null)
        {
            // RTSP sends TEARDOWN here; bounded so a dead link cannot hold the thread.
            if (!_stopping) _irq.SetDeadline(CloseTimeout);
            var f = _fmt;
            ffmpeg.avformat_close_input(&f);
            _fmt = null;
            _irq.ClearDeadline();
        }
    }

    private void FreeNative()
    {
        try
        {
            CloseInput();
            var p = _pkt; ffmpeg.av_packet_free(&p); _pkt = null;
            var f1 = _frame; ffmpeg.av_frame_free(&f1); _frame = null;
            var f2 = _swFrame; ffmpeg.av_frame_free(&f2); _swFrame = null;
            var f3 = _held; ffmpeg.av_frame_free(&f3); _held = null;
            _last = null;
            if (_sws != null) { ffmpeg.sws_freeContext(_sws); _sws = null; }
        }
        catch (Exception ex)
        {
            Log.Error("Video source cleanup failed", ex);
        }
        _frames.Free();
        lock (_gate)
        {
            _capture?.TrySetException(new ObjectDisposedException(nameof(FFmpegVideoSource)));
            _capture = null;
        }
        _irq.Dispose();
        _wake.Dispose();
    }

    private static long Pack(int w, int h) => ((long)Math.Max(0, w) << 32) | (uint)Math.Max(0, h);
}

internal static class WaitHandleExtensions
{
    /// <summary>Set that tolerates a handle already disposed by the exiting thread.</summary>
    public static void SafeSet(this EventWaitHandle h)
    {
        try { h.Set(); } catch (ObjectDisposedException) { }
    }
}
