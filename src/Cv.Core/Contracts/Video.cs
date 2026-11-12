// FROZEN CONTRACT. Only the lead changes this file; tracks report needed changes instead of editing it.
namespace Cv.Core.Video;

public enum VideoState { Idle, Connecting, Playing, Paused, Reconnecting, Ended, Failed }

/// <summary>Auto = hardware (D3D11VA) for streams wider than 1280 px when available, software otherwise. Off = software only.</summary>
public enum HwDecodeMode { Auto, Off }

public sealed record VideoSourceOptions
{
    /// <summary>rtsp:// URL (credentials inside, percent-encoded) or a local file path.</summary>
    public required string Url { get; init; }

    /// <summary>
    /// true: live view, show each frame as soon as it is decoded, lowest latency, reconnect on loss.
    /// false: recorded playback, frames paced by their timestamps, Pause/Resume work, end of stream = Ended.
    /// </summary>
    public bool IsLive { get; init; } = true;

    public HwDecodeMode HwDecode { get; init; } = HwDecodeMode.Auto;

    /// <summary>Bounding box in device pixels the frames are scaled down into (aspect kept, never scaled up). 0 = source size.</summary>
    public int TargetWidth { get; init; }
    public int TargetHeight { get; init; }

    public TimeSpan OpenTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Live only: on loss, retry with backoff (1, 2, 5, 10 s ...) until disposed. State = Reconnecting meanwhile.</summary>
    public bool Reconnect { get; init; } = true;

    /// <summary>
    /// Also take the recorder's sound track (single view and playback only; grid tiles never ask, so they stay light).
    /// Nothing is decoded or played until <see cref="IAudioSource.SoundOn"/> is set.
    /// </summary>
    public bool WithAudio { get; init; }
}

public readonly record struct VideoStats(string Codec, int Width, int Height, double Fps, double Kbps, bool HardwareDecoding);

/// <summary>Called under the source's frame lock: copy the pixels and return quickly. bgra is 32-bit BGRA, top-down.</summary>
public delegate void FrameCopier(IntPtr bgra, int width, int height, int stride);

public sealed record FrameSnapshot(byte[] Bgra, int Width, int Height, int Stride);

/// <summary>
/// One decoding pipeline (one RTSP stream or file) running on its own background thread. The UI pulls the newest
/// frame from CompositionTarget.Rendering via TryCopyLatest; nothing is pushed onto the UI thread.
/// Dispose stops the thread and frees native memory within ~1 s even when the network hangs.
/// </summary>
public interface IVideoSource : IDisposable
{
    VideoState State { get; }

    /// <summary>Vietnamese message for the user when State is Failed or Reconnecting; null otherwise.</summary>
    string? Error { get; }

    /// <summary>Raised on a background thread whenever State changes.</summary>
    event Action<IVideoSource>? StateChanged;

    /// <summary>Codec and source size are known once Playing; Fps/Kbps are averages over the last few seconds.</summary>
    VideoStats Stats { get; }

    /// <summary>Recorded playback: media time of the last shown frame, first frame = 0. Live: time since first frame.</summary>
    TimeSpan Position { get; }

    /// <summary>Starts connecting. Call once.</summary>
    void Start();

    /// <summary>Changes the scaling box (tile resized, single view toggled). Takes effect on the next frame.</summary>
    void SetTargetSize(int width, int height);

    /// <summary>Recorded playback only (RTSP PAUSE / PLAY); ignored for live.</summary>
    void Pause();
    void Resume();

    /// <summary>
    /// If the newest frame's sequence number is greater than <paramref name="lastSequence"/>, calls the copier with it,
    /// updates lastSequence and returns true; otherwise returns false without calling it.
    /// </summary>
    bool TryCopyLatest(ref long lastSequence, FrameCopier copier);

    /// <summary>The current frame at full source resolution, BGRA, for a snapshot. Faults if no frame was decoded yet.</summary>
    Task<FrameSnapshot> CaptureAsync(CancellationToken ct);
}

/// <summary>
/// Optional second face of a source opened with <see cref="VideoSourceOptions.WithAudio"/>: ask with
/// <c>source as IAudioSource</c>. Sound plays through the default Windows output and follows the system volume.
/// </summary>
public interface IAudioSource
{
    /// <summary>True once the stream is open and carries a sound track this app can play. Can change after a reconnect.</summary>
    bool HasAudio { get; }

    /// <summary>Off by default. Setting it before the stream is open takes effect when it opens.</summary>
    bool SoundOn { get; set; }

    /// <summary>
    /// Vietnamese message when sound cannot play although it was asked for (no speakers, a sound format this app cannot
    /// play); null otherwise.
    /// </summary>
    string? AudioError { get; }

    /// <summary>Raised on a background thread when HasAudio or AudioError changed.</summary>
    event Action? AudioChanged;
}

public interface IVideoSourceFactory
{
    IVideoSource Create(VideoSourceOptions options);
}
