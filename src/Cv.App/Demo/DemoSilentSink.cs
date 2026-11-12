using Cv.Media;

namespace Cv.App.Demo;

/// <summary>A sound output that plays nothing and counts: the self-test must stay silent on the machine it runs on.</summary>
internal sealed class DemoSilentSink : IAudioSink
{
    long _samples;

    /// <summary>True: Start fails like a PC without speakers.</summary>
    public bool Fail { get; init; }
    public int Starts { get; private set; }
    public int Flushes { get; private set; }
    public bool Disposed { get; private set; }
    public long Samples => Interlocked.Read(ref _samples);
    public TimeSpan Queued => TimeSpan.Zero;
    public string? Error => Fail ? "Máy không có loa hoặc tai nghe" : null;

    public bool Start(int sampleRate, int channels)
    {
        Starts++;
        return !Fail;
    }

    public void Write(ReadOnlySpan<short> interleaved) => Interlocked.Add(ref _samples, interleaved.Length);

    public void Flush() => Flushes++;

    public void Dispose() => Disposed = true;
}
