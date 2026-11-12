namespace Cv.Media;

/// <summary>
/// Audio output for a live stream. The decoder thread pushes interleaved 16-bit PCM; the sink keeps latency bounded
/// instead of buffering without limit, because a live picture with sound that is seconds late is worse than a gap.
/// </summary>
public interface IAudioSink : IDisposable
{
    /// <summary>
    /// Opens the device for this format. False when there is no usable output (no device, RDP session...): the caller then
    /// plays without sound. Call again with another format after a format change (it closes the previous one first).
    /// Never throws for device problems.
    /// </summary>
    bool Start(int sampleRate, int channels);

    /// <summary>
    /// Queues interleaved samples. Never blocks. Drops the NEW samples when more than MaxQueued is already waiting, so live
    /// audio cannot lag by seconds. Safe to call before Start (ignored) or after Dispose (ignored).
    /// </summary>
    void Write(ReadOnlySpan<short> interleaved);

    /// <summary>Drops everything queued and goes back to waiting for the pre-buffer (used on pause, seek, reconnect, mute).</summary>
    void Flush();

    /// <summary>Audio queued and not yet played.</summary>
    TimeSpan Queued { get; }

    /// <summary>Message for the user when Start returned false, else null. Vietnamese: "Máy không có loa hoặc tai nghe".</summary>
    string? Error { get; }
}
