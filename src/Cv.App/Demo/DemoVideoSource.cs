using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Cv.Core.Video;
using Cv.Media;

namespace Cv.App.Demo;

/// <summary>
/// Creates fake pipelines for demo:// URLs. One background thread paces every demo source at ~15 fps (a real source
/// has its own decoder thread; the fake does not need one).
/// </summary>
internal sealed class DemoVideoSourceFactory : IVideoSourceFactory, IDisposable
{
    readonly Dictionary<string, TextMask[]> _names = [];
    TextMask[][]? _clock;
    readonly DemoFrameClock _clockThread = new();

    /// <summary>Sound output of the demo sources' beeps. The self-test swaps it for a silent counter.</summary>
    public Func<IAudioSink> AudioSinkFactory { get; set; } = static () => new WaveOutSink();

    /// <summary>Options of every source created (the self-test checks that only single view and playback ask for sound).</summary>
    public List<(int Channel, bool Live, bool Main, bool WithAudio)> Created { get; } = [];

    public IVideoSource Create(VideoSourceOptions options)
    {
        var uri = new Uri(options.Url);
        var parts = uri.AbsolutePath.Trim('/').Split('/');
        var channel = int.Parse(parts[0], CultureInfo.InvariantCulture);
        var main = parts.Length < 2 || parts[1] == "1";
        var live = uri.Host == "live";
        DateTime from = default, to = default;
        if (!live)
        {
            var q = System.Web.HttpUtility.ParseQueryString(uri.Query);
            from = DateTime.ParseExact(q["from"]!, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture);
            to = DateTime.ParseExact(q["to"]!, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture);
        }
        var name = DemoData.Name(channel);
        lock (Created) Created.Add((channel, live, main, options.WithAudio));
        return new DemoVideoSource(_clockThread, options, channel, main, live, from, to, NameMasks(name), ClockMasks(), AudioSinkFactory);
    }

    TextMask[]? NameMasks(string name)
    {
        if (_names.TryGetValue(name, out var m)) return m;
        if (!DemoText.CanRender) return null;
        m = DemoText.Sizes.Select(s => DemoText.Render(name, s)).ToArray();
        _names[name] = m;
        return m;
    }

    TextMask[][]? ClockMasks()
    {
        if (_clock is null && DemoText.CanRender) _clock = DemoText.Sizes.Select(DemoText.RenderClockAtlas).ToArray();
        return _clock;
    }

    public void Dispose() => _clockThread.Dispose();
}

/// <summary>The single pacing thread of all demo sources.</summary>
internal sealed class DemoFrameClock : IDisposable
{
    readonly object _gate = new();
    readonly List<DemoVideoSource> _sources = [];
    DemoVideoSource[] _snapshot = [];
    Thread? _thread;
    volatile bool _stop;

    public static readonly long Frequency = Stopwatch.Frequency;

    public void Add(DemoVideoSource s)
    {
        lock (_gate)
        {
            _sources.Add(s);
            _snapshot = [.. _sources];
            if (_thread is null)
            {
                _thread = new Thread(Run) { IsBackground = true, Name = "demo-frames", Priority = ThreadPriority.BelowNormal };
                _thread.Start();
            }
        }
    }

    public void Remove(DemoVideoSource s)
    {
        lock (_gate)
        {
            _sources.Remove(s);
            _snapshot = [.. _sources];
        }
    }

    void Run()
    {
        var period = Frequency / 15;
        var next = Stopwatch.GetTimestamp();
        while (!_stop)
        {
            var now = Stopwatch.GetTimestamp();
            foreach (var s in _snapshot) s.Tick(now);
            next += period;
            var wait = (next - Stopwatch.GetTimestamp()) * 1000 / Frequency;
            if (wait > 0) Thread.Sleep((int)wait);
            else next = Stopwatch.GetTimestamp();
        }
    }

    public void Dispose() => _stop = true;
}

/// <summary>
/// A fake pipeline: a moving gradient in the camera's colour, the camera name and a clock, drawn into native BGRA
/// buffers at the requested size (never larger than the fake source size). Recorded playback honours Pause/Resume and
/// ends at the requested end time.
/// </summary>
internal sealed unsafe class DemoVideoSource : IVideoSource, IAudioSource
{
    const int ToneRate = 8000;   // like a G.711 camera

    readonly bool _withAudio;
    readonly Func<IAudioSink> _sinkFactory;
    IAudioSink? _sink;
    bool _sinkFailed;
    bool _soundApplied;
    volatile bool _soundOn;
    long _toneSamples;           // samples written so far: keeps the beep pattern continuous
    short[] _toneBuffer = [];

    sealed class Buffer
    {
        public byte* Ptr;
        public int Width, Height, Stride;
    }

    readonly DemoFrameClock _clock;
    readonly int _channel;
    readonly bool _live;
    readonly DateTime _from, _to;
    readonly int _srcW, _srcH;
    readonly TextMask[]? _name;
    readonly TextMask[][]? _digits;
    readonly object _frameLock = new();
    readonly object _tickLock = new();
    readonly long _connectAt;
    readonly long _bornAt;
    readonly char[] _clockText = new char[19];

    Buffer? _front, _back;
    int[] _palette = [];
    int _paletteFor;
    long _seq;
    volatile int _targetW, _targetH;
    volatile bool _paused;
    bool _disposed;
    long _lastTick;
    long _playedTicks;
    long _frames;
    long _reconnectUntil;
    VideoState _state = VideoState.Idle;

    public DemoVideoSource(DemoFrameClock clock, VideoSourceOptions o, int channel, bool main, bool live, DateTime from, DateTime to,
        TextMask[]? name, TextMask[][]? digits, Func<IAudioSink> sinkFactory)
    {
        _sinkFactory = sinkFactory;
        _withAudio = o.WithAudio && channel != DemoData.SilentChannel;
        _clock = clock;
        _channel = channel;
        _live = live;
        _from = from;
        _to = to;
        (_srcW, _srcH) = main ? (1920, 1080) : (640, 360);
        _name = name;
        _digits = digits;
        _targetW = o.TargetWidth;
        _targetH = o.TargetHeight;
        _bornAt = Stopwatch.GetTimestamp();
        _connectAt = _bornAt + DemoFrameClock.Frequency * (250 + Random.Shared.Next(650)) / 1000;
        Stats = new VideoStats(main ? "H.265" : "H.264", _srcW, _srcH, 15, main ? 4096 : 512, false);
    }

    public VideoState State => _state;
    public string? Error { get; private set; }
    public event Action<IVideoSource>? StateChanged;
    public VideoStats Stats { get; }

    public TimeSpan Position => TimeSpan.FromTicks(Interlocked.Read(ref _playedTicks));

    // ---- Sound: a soft beep per camera (pitch = channel), 0.2 s every second, 8 kHz mono like a G.711 camera ----

    public bool HasAudio => _withAudio;
    public string? AudioError { get; private set; }
    public event Action? AudioChanged;

    public bool SoundOn
    {
        get => _soundOn;
        set => _soundOn = value;
    }

    /// <summary>Called from Tick (the pacing thread, ~15 times a second): writes the tone for the elapsed time.</summary>
    void PlayTone(long now)
    {
        var on = _soundOn && _withAudio;
        if (on != _soundApplied)
        {
            _soundApplied = on;
            if (_sinkFailed)
            {
                // A new attempt gets a new output (the failed one would still refuse).
                try { _sink?.Dispose(); } catch (Exception) { }
                _sink = null;
                _sinkFailed = false;
            }
            _sink?.Flush();
            if (!on && AudioError is not null) { AudioError = null; AudioChanged?.Invoke(); }
        }
        if (!on || _sinkFailed || _state != VideoState.Playing) return;
        if (_sink is null)
        {
            _sink = _sinkFactory();
            if (!_sink.Start(ToneRate, 1))
            {
                _sinkFailed = true;
                AudioError = _sink.Error ?? "Máy không có loa hoặc tai nghe";
                AudioChanged?.Invoke();
                return;
            }
        }
        // The first tick, or the first after a pause or a reconnect, writes one frame's worth instead of the whole gap.
        var gap = now - _lastToneAt;
        _lastToneAt = now;
        var n = gap is > 0 && gap < DemoFrameClock.Frequency / 4 ? (int)(gap * ToneRate / DemoFrameClock.Frequency) : ToneRate / 15;
        if (n <= 0) return;
        if (_toneBuffer.Length < n) _toneBuffer = new short[n * 2];
        var freq = 330 + _channel * 22;
        for (var i = 0; i < n; i++)
        {
            var t = _toneSamples++;
            var beep = t % ToneRate < ToneRate / 5;
            var env = beep ? Math.Sin(Math.PI * (t % ToneRate) / (ToneRate / 5.0)) : 0;
            _toneBuffer[i] = (short)(env * 0.12 * short.MaxValue * Math.Sin(2 * Math.PI * freq * t / ToneRate));
        }
        _sink.Write(_toneBuffer.AsSpan(0, n));
    }

    long _lastToneAt;

    public void Start()
    {
        SetState(VideoState.Connecting, null);
        _clock.Add(this);
    }

    public void SetTargetSize(int width, int height)
    {
        _targetW = width;
        _targetH = height;
    }

    public void Pause()
    {
        if (_live) return;
        _paused = true;
        if (_state == VideoState.Playing) SetState(VideoState.Paused, null);
    }

    public void Resume()
    {
        if (_live) return;
        _paused = false;
        if (_state == VideoState.Paused) SetState(VideoState.Playing, null);
    }

    /// <summary>Called by the pacing thread ~15 times per second.</summary>
    internal void Tick(long now)
    {
        lock (_tickLock)
        {
            if (_disposed) return;
            switch (_state)
            {
                case VideoState.Connecting:
                    if (now < _connectAt) return;
                    _lastTick = now;
                    SetState(_paused ? VideoState.Paused : VideoState.Playing, null);
                    Render(now);
                    return;
                case VideoState.Reconnecting:
                    if (now < _reconnectUntil) return;
                    _lastTick = now;
                    SetState(VideoState.Playing, null);
                    return;
                case VideoState.Paused:
                    _lastTick = now;
                    _lastToneAt = 0;
                    _sink?.Flush();
                    return;
                case VideoState.Playing:
                    var played = _playedTicks + (now - _lastTick) * TimeSpan.TicksPerSecond / DemoFrameClock.Frequency;
                    _lastTick = now;
                    if (!_live && _from + TimeSpan.FromTicks(played) >= _to)
                    {
                        Interlocked.Exchange(ref _playedTicks, (_to - _from).Ticks);
                        SetState(VideoState.Ended, null);
                        return;
                    }
                    Interlocked.Exchange(ref _playedTicks, played);
                    if (_live && _channel == DemoData.FlakyChannel && TimeSpan.FromTicks(played).TotalSeconds % 75 > 72)
                    {
                        _reconnectUntil = now + DemoFrameClock.Frequency * 3;
                        Interlocked.Exchange(ref _playedTicks, played + TimeSpan.TicksPerSecond * 4);
                        SetState(VideoState.Reconnecting, "Mất kết nối, đang thử lại…");
                        return;
                    }
                    Render(now);
                    PlayTone(now);
                    return;
            }
        }
    }

    void SetState(VideoState s, string? error)
    {
        _state = s;
        Error = error;
        StateChanged?.Invoke(this);
    }

    (int W, int H) FrameSize()
    {
        int tw = _targetW, th = _targetH;
        if (tw <= 0 || th <= 0 || (tw >= _srcW && th >= _srcH)) return (_srcW, _srcH);
        var scale = Math.Min(tw / (double)_srcW, th / (double)_srcH);
        return (Math.Max(2, (int)(_srcW * scale) & ~1), Math.Max(2, (int)(_srcH * scale) & ~1));
    }

    void Render(long now)
    {
        var (w, h) = FrameSize();
        var back = _back;
        if (back is null || back.Width != w || back.Height != h)
        {
            Free(back);
            back = Alloc(w, h);
        }
        var shown = _live ? DateTime.Now : _from + Position;
        Draw(back.Ptr, w, h, back.Stride, _frames++, shown);
        lock (_frameLock)
        {
            _back = _front;
            _front = back;
            _seq++;
        }
    }

    void Draw(byte* p, int w, int h, int stride, long frame, DateTime shown)
    {
        EnsurePalette(w);
        var period = w * 2;
        var shift = (int)(frame * Math.Max(1, w / 120) % period);
        fixed (int* pal = _palette)
        {
            for (var y = 0; y < h; y++)
            {
                var off = (shift + y) % period;
                System.Buffer.MemoryCopy(pal + off, p + (long)y * stride, stride, w * 4L);
            }
        }

        var cls = DemoText.SizeClass(h);
        var pad = cls == 0 ? 6 : cls == 1 ? 10 : 16;
        if (_digits is not null)
        {
            FormatClock(shown);
            var atlas = _digits[cls];
            var tw = 0;
            foreach (var c in _clockText) tw += atlas[DemoText.ClockChars.IndexOf(c)].Width;
            Darken(p, w, h, stride, pad - 4, pad - 2, tw + 8, atlas[0].Height + 4);
            var x = pad;
            foreach (var c in _clockText)
            {
                var m = atlas[DemoText.ClockChars.IndexOf(c)];
                Blit(p, w, h, stride, m, x, pad);
                x += m.Width;
            }
        }
        // Tiles show the name in the UI strip; the picture carries it only when it is big enough to read.
        if (_name is not null && cls > 0)
        {
            var m = _name[cls];
            var nx = w - pad - m.Width;
            var ny = h - pad - m.Height;
            Darken(p, w, h, stride, nx - 4, ny - 2, m.Width + 8, m.Height + 4);
            Blit(p, w, h, stride, m, nx, ny);
        }
    }

    void FormatClock(DateTime t)
    {
        // dd/MM/yyyy HH:mm:ss without allocating a string.
        Two(0, t.Day); _clockText[2] = '/'; Two(3, t.Month); _clockText[5] = '/';
        Two(6, t.Year / 100); Two(8, t.Year % 100); _clockText[10] = ' ';
        Two(11, t.Hour); _clockText[13] = ':'; Two(14, t.Minute); _clockText[16] = ':'; Two(17, t.Second);
    }

    void Two(int i, int v)
    {
        _clockText[i] = (char)('0' + v / 10 % 10);
        _clockText[i + 1] = (char)('0' + v % 10);
    }

    void EnsurePalette(int w)
    {
        if (_paletteFor == w) return;
        var period = w * 2;
        var pal = new int[period * 2];
        var (r0, g0, b0) = Hsv(DemoData.Hue(_channel), 0.55, 1.0);
        for (var i = 0; i < pal.Length; i++)
        {
            var phase = i % period / (double)period * Math.PI * 2;
            var v = 0.22 + 0.30 * (0.5 + 0.5 * Math.Sin(phase)) + 0.08 * (0.5 + 0.5 * Math.Sin(phase * 3));
            pal[i] = unchecked((int)0xFF000000u) | (int)(r0 * v) << 16 | (int)(g0 * v) << 8 | (int)(b0 * v);
        }
        _palette = pal;
        _paletteFor = w;
    }

    static (double R, double G, double B) Hsv(double h, double s, double v)
    {
        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = v - c;
        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        return ((r + m) * 255, (g + m) * 255, (b + m) * 255);
    }

    static void Darken(byte* p, int w, int h, int stride, int x0, int y0, int rw, int rh)
    {
        int x1 = Math.Min(w, x0 + rw), y1 = Math.Min(h, y0 + rh);
        x0 = Math.Max(0, x0);
        y0 = Math.Max(0, y0);
        for (var y = y0; y < y1; y++)
        {
            var row = p + (long)y * stride;
            for (var x = x0; x < x1; x++)
            {
                var px = row + x * 4;
                px[0] = (byte)(px[0] * 2 / 5);
                px[1] = (byte)(px[1] * 2 / 5);
                px[2] = (byte)(px[2] * 2 / 5);
            }
        }
    }

    static void Blit(byte* p, int w, int h, int stride, TextMask m, int x0, int y0)
    {
        for (var y = 0; y < m.Height; y++)
        {
            var dy = y0 + y;
            if (dy < 0 || dy >= h) continue;
            var row = p + (long)dy * stride;
            for (var x = 0; x < m.Width; x++)
            {
                var dx = x0 + x;
                if (dx < 0 || dx >= w) continue;
                int a = m.Alpha[y * m.Width + x];
                if (a == 0) continue;
                var px = row + dx * 4;
                px[0] = (byte)(px[0] + (255 - px[0]) * a / 255);
                px[1] = (byte)(px[1] + (255 - px[1]) * a / 255);
                px[2] = (byte)(px[2] + (255 - px[2]) * a / 255);
            }
        }
    }

    static Buffer Alloc(int w, int h) =>
        new() { Ptr = (byte*)NativeMemory.Alloc((nuint)(w * 4 * h)), Width = w, Height = h, Stride = w * 4 };

    static void Free(Buffer? b)
    {
        if (b is null || b.Ptr is null) return;
        NativeMemory.Free(b.Ptr);
        b.Ptr = null;
    }

    public bool TryCopyLatest(ref long lastSequence, FrameCopier copier)
    {
        lock (_frameLock)
        {
            var f = _front;
            if (_disposed || f is null || _seq <= lastSequence) return false;
            copier((IntPtr)f.Ptr, f.Width, f.Height, f.Stride);
            lastSequence = _seq;
            return true;
        }
    }

    public Task<FrameSnapshot> CaptureAsync(CancellationToken ct)
    {
        if (_frames == 0) return Task.FromException<FrameSnapshot>(new InvalidOperationException("Chưa có hình để chụp."));
        var stride = _srcW * 4;
        var bytes = new byte[stride * _srcH];
        lock (_tickLock)
        {
            fixed (byte* p = bytes)
            {
                var savedPalette = _paletteFor;
                Draw(p, _srcW, _srcH, stride, _frames, _live ? DateTime.Now : _from + Position);
                if (savedPalette != _srcW) _paletteFor = 0;
            }
        }
        return Task.FromResult(new FrameSnapshot(bytes, _srcW, _srcH, stride));
    }

    public void Dispose()
    {
        _clock.Remove(this);
        lock (_tickLock)
        {
            if (_disposed) return;
            lock (_frameLock)
            {
                _disposed = true;
                Free(_front);
                Free(_back);
                _front = _back = null;
            }
            _palette = [];
            _paletteFor = 0;
            try { _sink?.Dispose(); } catch (Exception) { }
            _sink = null;
        }
    }
}
