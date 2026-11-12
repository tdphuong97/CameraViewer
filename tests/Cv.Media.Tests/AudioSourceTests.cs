using Cv.Core.Video;
using Cv.Media.Tests.Support;

namespace Cv.Media.Tests;

/// <summary>
/// Sound through the real decoder, but into a counting sink: nothing here ever reaches the speakers of the machine the
/// tests run on. The clips: 1 kHz sine as AAC (16 kHz mono) and Hikvision-style PS with G.711 silence (8 kHz mono).
/// </summary>
public class AudioSourceTests : IDisposable
{
    private readonly FFmpegVideoSourceFactory _factory = new();
    private readonly List<IVideoSource> _sources = [];
    private readonly List<CountingSink> _sinks = [];

    public AudioSourceTests()
    {
        _factory.AudioSinkFactory = () =>
        {
            var s = new CountingSink();
            lock (_sinks) _sinks.Add(s);
            return s;
        };
    }

    public void Dispose()
    {
        foreach (var s in _sources) s.Dispose();
        _factory.Dispose();
    }

    private IVideoSource Open(string clip, bool withAudio, bool live = false)
    {
        var s = _factory.Create(new VideoSourceOptions { Url = TestClips.Resolve(clip), IsLive = live, WithAudio = withAudio });
        _sources.Add(s);
        return s;
    }

    private CountingSink Sink()
    {
        lock (_sinks) return Assert.Single(_sinks);
    }

    /// <summary>For wait conditions: the output may not exist yet.</summary>
    private long SamplesSoFar()
    {
        lock (_sinks) return _sinks.Count == 0 ? 0 : _sinks[0].Samples;
    }

    private int FlushesSoFar()
    {
        lock (_sinks) return _sinks.Count == 0 ? 0 : _sinks[0].Flushes;
    }

    [NativeFact]
    public async Task A_sound_track_is_found_and_decoded_into_16_bit_samples_at_its_own_rate()
    {
        var s = Open("h264_aac.ts", withAudio: true);
        var audio = Assert.IsAssignableFrom<IAudioSource>(s);
        audio.SoundOn = true;
        s.Start();
        await Wait.NextFrame(s);
        Assert.True(audio.HasAudio);
        Assert.Null(audio.AudioError);

        // 5 s of clip, played at real speed: a couple of seconds of sound is plenty.
        Assert.True(await Wait.For(() => SamplesSoFar() >= 16000 * 2, TimeSpan.FromSeconds(8)));
        var sink = Sink();
        Assert.Equal((16000, 1), (sink.Rate, sink.Channels));
        Assert.Equal(1, sink.Starts);
        // ffmpeg's sine source is 1 kHz at 1/8 of full scale (4096): the converter neither zeroed nor rescaled it, and the pitch is right.
        Assert.InRange(sink.Peak, 3500, 5200);
        Assert.InRange(sink.ZeroCrossingsPerSecond, 1800, 2200);
    }

    [NativeFact]
    public async Task Hikvision_PS_with_G711_audio_decodes_at_8_kHz_mono()
    {
        var s = Open("h264_g711.ps", withAudio: true);
        var audio = (IAudioSource)s;
        audio.SoundOn = true;
        s.Start();
        await Wait.NextFrame(s);
        Assert.True(audio.HasAudio);
        Assert.True(await Wait.For(() => SamplesSoFar() >= 8000, TimeSpan.FromSeconds(8)));
        Assert.Equal((8000, 1), (Sink().Rate, Sink().Channels));
    }

    [NativeFact]
    public async Task Sound_is_not_decoded_or_played_until_it_is_switched_on_and_stops_when_switched_off()
    {
        var s = Open("h264_aac.ts", withAudio: true);
        var audio = (IAudioSource)s;
        s.Start();
        await Wait.NextFrame(s);
        Assert.True(audio.HasAudio);
        await Task.Delay(500);
        lock (_sinks) Assert.Empty(_sinks); // off: no output was even created

        audio.SoundOn = true;
        Assert.True(await Wait.For(() => { lock (_sinks) return _sinks.Count == 1 && _sinks[0].Samples > 0; }, TimeSpan.FromSeconds(6)));

        audio.SoundOn = false;
        Assert.True(await Wait.For(() => FlushesSoFar() >= 1, TimeSpan.FromSeconds(3)));
        var after = Sink().Samples;
        await Task.Delay(600);
        Assert.Equal(after, Sink().Samples); // nothing more while off
    }

    [NativeFact]
    public async Task A_source_not_asked_for_sound_never_has_any()
    {
        var s = Open("h264_aac.ts", withAudio: false);
        s.Start();
        await Wait.NextFrame(s);
        var audio = (IAudioSource)s;
        audio.SoundOn = true;
        await Task.Delay(500);
        Assert.False(audio.HasAudio);
        lock (_sinks) Assert.Empty(_sinks);
    }

    [NativeFact]
    public async Task A_clip_without_a_sound_track_reports_no_sound_and_keeps_playing()
    {
        var s = Open("h264.mp4", withAudio: true);
        var audio = (IAudioSource)s;
        audio.SoundOn = true;
        s.Start();
        await Wait.NextFrame(s);
        Assert.False(audio.HasAudio);
        Assert.Equal(VideoState.Playing, s.State);
        lock (_sinks) Assert.Empty(_sinks);
    }

    [NativeFact]
    public async Task No_speakers_is_reported_and_the_picture_keeps_playing()
    {
        _factory.AudioSinkFactory = () =>
        {
            var sink = new CountingSink { FailStart = true };
            lock (_sinks) _sinks.Add(sink);
            return sink;
        };
        var s = Open("h264_aac.ts", withAudio: true);
        var audio = (IAudioSource)s;
        var changes = 0;
        audio.AudioChanged += () => Interlocked.Increment(ref changes);
        audio.SoundOn = true;
        s.Start();
        await Wait.NextFrame(s);
        Assert.True(await Wait.For(() => audio.AudioError == "Máy không có loa hoặc tai nghe", TimeSpan.FromSeconds(6)));
        Assert.True(audio.HasAudio); // the track is there, only the output is missing
        Assert.True(changes >= 1);
        await Wait.NextFrame(s, after: 5); // frames keep coming
        Assert.Equal(1, Sink().Starts); // and the output is not retried on every packet

        audio.SoundOn = false; // the message belongs to that attempt
        Assert.True(await Wait.For(() => audio.AudioError is null, TimeSpan.FromSeconds(3)));
    }

    [MediaMtxFact]
    public async Task Live_RTSP_with_G711_sound_plays_and_the_first_picture_is_not_held_back()
    {
        using var mtx = new MediaMtx();
        mtx.PublishWithSound("cam", TestClips.H264Mp4);
        await Task.Delay(1500);

        async Task<(double Seconds, IVideoSource Source)> FirstFrame(bool withAudio, bool soundOn)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var s = _factory.Create(new VideoSourceOptions { Url = mtx.ReaderUrl("cam"), IsLive = true, WithAudio = withAudio });
            _sources.Add(s);
            if (s is IAudioSource a) a.SoundOn = soundOn;
            s.Start();
            await Wait.NextFrame(s, 0, 15);
            return (sw.Elapsed.TotalSeconds, s);
        }

        var (plain, s0) = await FirstFrame(false, false);
        s0.Dispose();
        var (withSound, s1) = await FirstFrame(true, true);
        var audio = (IAudioSource)s1;
        Assert.True(audio.HasAudio);
        Assert.True(await Wait.For(() => SamplesSoFar() >= 8000 * 2, TimeSpan.FromSeconds(8)));
        var sink = Sink();
        Assert.Equal((8000, 1), (sink.Rate, sink.Channels));
        Assert.InRange(sink.ZeroCrossingsPerSecond, 900, 1100); // 500 Hz
        Assert.InRange(sink.Peak, 2000, 12000);
        // Allowing the sound track must not hold the first picture back (probing waits for every allowed track).
        Assert.True(withSound < plain + 1.5, $"first picture {withSound:F2} s with sound vs {plain:F2} s without");
    }

    [NativeFact]
    public async Task Disposing_a_source_closes_its_sound_output()
    {
        var s = Open("h264_aac.ts", withAudio: true);
        ((IAudioSource)s).SoundOn = true;
        s.Start();
        await Wait.NextFrame(s);
        Assert.True(await Wait.For(() => { lock (_sinks) return _sinks.Count == 1; }, TimeSpan.FromSeconds(5)));
        s.Dispose();
        Assert.True(await Wait.For(() => { lock (_sinks) return _sinks.Count == 1 && _sinks[0].Disposed; }, TimeSpan.FromSeconds(3)));
    }
}

/// <summary>Counts what a source would have played.</summary>
internal sealed class CountingSink : IAudioSink
{
    private long _samples;
    private long _crossings;
    private short _last;
    private int _peak;
    private int _flushes;

    public bool FailStart { get; init; }
    public int Starts { get; private set; }
    public int Rate { get; private set; }
    public int Channels { get; private set; }
    public bool Disposed { get; private set; }
    public string? Error => FailStart ? "Máy không có loa hoặc tai nghe" : null;
    public TimeSpan Queued => TimeSpan.Zero;

    /// <summary>Samples per channel written so far.</summary>
    public long Samples => Interlocked.Read(ref _samples);
    public int Peak => Volatile.Read(ref _peak);
    public int Flushes => Volatile.Read(ref _flushes);

    /// <summary>Sign changes of the first channel per second of sound: 2 × frequency for a sine.</summary>
    public double ZeroCrossingsPerSecond => Samples == 0 ? 0 : Interlocked.Read(ref _crossings) * (double)Rate / Samples;

    public bool Start(int sampleRate, int channels)
    {
        Starts++;
        Rate = sampleRate;
        Channels = channels;
        return !FailStart;
    }

    public void Write(ReadOnlySpan<short> interleaved)
    {
        if (Disposed || Channels == 0) return;
        int n = interleaved.Length / Channels;
        int peak = _peak;
        long crossings = 0;
        short last = _last;
        for (int i = 0; i < n; i++)
        {
            short v = interleaved[i * Channels];
            peak = Math.Max(peak, Math.Abs((int)v));
            if ((last < 0) != (v < 0) && v != 0 && last != 0) crossings++;
            if (v != 0) last = v;
        }
        _last = last;
        Volatile.Write(ref _peak, peak);
        Interlocked.Add(ref _crossings, crossings);
        Interlocked.Add(ref _samples, n);
    }

    public void Flush() => Interlocked.Increment(ref _flushes);

    public void Dispose() => Disposed = true;
}
