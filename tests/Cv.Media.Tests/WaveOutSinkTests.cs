using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Cv.Media.Tests;

/// <summary>
/// WaveOutSink against the real default output device. Every sample written here is zero, so the tests are silent.
/// xUnit v2 has no dynamic skip: on a machine with no output device each test returns early (see <see cref="HasDevice"/>).
/// </summary>
public class WaveOutSinkTests
{
    [DllImport("winmm.dll")]
    private static extern uint waveOutGetNumDevs();

    private static bool HasDevice() => waveOutGetNumDevs() > 0;

    private static short[] Silence(int sampleRate, int channels, int ms) => new short[sampleRate * ms / 1000 * channels];

    /// <summary>Polls Queued (polling is what finds finished buffers) until it is zero; returns the last value seen.</summary>
    private static TimeSpan WaitUntilEmpty(WaveOutSink sink, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        var q = sink.Queued;
        while (q > TimeSpan.Zero && sw.Elapsed < timeout)
        {
            Thread.Sleep(5);
            q = sink.Queued;
        }
        return q;
    }

    [Fact]
    public void Paced_writes_stay_bounded_and_play_out_in_about_real_time()
    {
        if (!HasDevice()) return;
        using var sink = new WaveOutSink();
        Assert.True(sink.Start(8000, 1));
        Assert.Null(sink.Error);

        // 500 ms in 20 ms pieces, each due at i * 20 ms on one absolute schedule (Thread.Sleep(20) drifts with the 15.6 ms timer).
        var piece = Silence(8000, 1, 20);
        var clock = Stopwatch.StartNew();
        var max = TimeSpan.Zero;
        for (int i = 0; i < 25; i++)
        {
            while (clock.Elapsed < TimeSpan.FromMilliseconds(i * 20)) Thread.Sleep(1);
            sink.Write(piece);
            var q = sink.Queued;
            if (q > max) max = q;
        }

        Assert.True(max > TimeSpan.Zero);
        Assert.True(max <= WaveOutSink.MaxQueued + WaveOutSink.BufferDuration, $"Queued reached {max.TotalMilliseconds:F0} ms");

        // 500 ms of audio, about 120 ms still ahead of the device at the end: it must be gone well inside a second.
        var left = WaitUntilEmpty(sink, TimeSpan.FromSeconds(1));
        Assert.Equal(TimeSpan.Zero, left);
    }

    [Fact]
    public void Queued_falls_at_about_real_time()
    {
        if (!HasDevice()) return;
        using var sink = new WaveOutSink();
        Assert.True(sink.Start(8000, 1));

        // 10 whole buffers at once. If the driver reported buffers done early or never, this would be near 0 or still 400 ms.
        sink.Write(Silence(8000, 1, 400));
        Assert.InRange(sink.Queued.TotalMilliseconds, 300, 400);
        Thread.Sleep(200);
        Assert.InRange(sink.Queued.TotalMilliseconds, 100, 340);
        Assert.Equal(TimeSpan.Zero, WaitUntilEmpty(sink, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void After_an_underrun_the_sink_waits_for_the_pre_buffer_again()
    {
        if (!HasDevice()) return;
        using var sink = new WaveOutSink();
        Assert.True(sink.Start(8000, 1));

        sink.Write(Silence(8000, 1, 200)); // 5 whole buffers, so no partial tail
        Assert.Equal(TimeSpan.Zero, WaitUntilEmpty(sink, TimeSpan.FromSeconds(2)));

        // Below the 120 ms pre-buffer: one buffer is complete but must not be played yet.
        sink.Write(Silence(8000, 1, 40));
        Thread.Sleep(300);
        var held = sink.Queued;
        Assert.InRange(held.TotalMilliseconds, 35, 45);

        // 40 + 80 = 120 ms reaches the pre-buffer: playback starts and drains.
        sink.Write(Silence(8000, 1, 80));
        Assert.Equal(TimeSpan.Zero, WaitUntilEmpty(sink, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void Flush_drops_the_queue_and_waits_for_the_pre_buffer_again()
    {
        if (!HasDevice()) return;
        using var sink = new WaveOutSink();
        Assert.True(sink.Start(8000, 1));

        sink.Write(Silence(8000, 1, 300));
        Assert.True(sink.Queued > TimeSpan.Zero);
        sink.Flush();
        Assert.Equal(TimeSpan.Zero, sink.Queued);

        sink.Write(Silence(8000, 1, 80)); // below the pre-buffer: stays queued, not played
        Thread.Sleep(200);
        Assert.InRange(sink.Queued.TotalMilliseconds, 75, 85);

        sink.Flush();
        Assert.Equal(TimeSpan.Zero, sink.Queued);
    }

    [Fact]
    public void A_huge_write_is_capped_and_never_throws()
    {
        if (!HasDevice()) return;
        using var sink = new WaveOutSink();
        Assert.True(sink.Start(8000, 1));

        var big = Silence(8000, 1, 3000);
        sink.Write(big);
        var first = sink.Queued;
        Assert.InRange(first.TotalMilliseconds, 1, 450);

        sink.Write(big); // already full: the new samples are dropped, not queued behind
        Assert.True(sink.Queued <= TimeSpan.FromMilliseconds(450), $"Queued {sink.Queued.TotalMilliseconds:F0} ms");
    }

    [Fact]
    public void Start_again_with_another_format_then_dispose_twice_and_use_after_dispose()
    {
        if (!HasDevice()) return;
        var sink = new WaveOutSink();
        Assert.True(sink.Start(8000, 1));
        sink.Write(Silence(8000, 1, 200));

        // Stereo at another rate: the previous device is closed first and the queue starts empty.
        Assert.True(sink.Start(44100, 2));
        Assert.Null(sink.Error);
        Assert.Equal(TimeSpan.Zero, sink.Queued);
        sink.Write(Silence(44100, 2, 200));
        Assert.True(sink.Queued > TimeSpan.Zero);

        sink.Dispose();
        sink.Dispose();
        sink.Write(Silence(44100, 2, 50));
        sink.Flush();
        Assert.Equal(TimeSpan.Zero, sink.Queued);
        Assert.False(sink.Start(8000, 1));
    }

    [Fact]
    public void Calls_before_Start_are_ignored()
    {
        // Needs no device: nothing is opened.
        using var sink = new WaveOutSink();
        sink.Write(Silence(8000, 1, 100));
        sink.Flush();
        Assert.Equal(TimeSpan.Zero, sink.Queued);
        Assert.Null(sink.Error);
    }

    [Fact]
    public void A_format_it_cannot_play_is_refused_with_a_message()
    {
        // Refused before any device is touched, so this runs everywhere.
        using var sink = new WaveOutSink();
        Assert.False(sink.Start(8000, 6));
        Assert.False(string.IsNullOrEmpty(sink.Error));
        Assert.False(sink.Start(0, 1));
        Assert.False(string.IsNullOrEmpty(sink.Error));
    }
}
