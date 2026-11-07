namespace Cv.Media;

/// <summary>Loads the FFmpeg DLLs (Track B implements). Call once at app start, before any other Cv.Media type.</summary>
public static class FFmpegRuntime
{
    /// <summary>Loads FFmpeg from <paramref name="folder"/> (default Cv.Core.AppPaths.FFmpeg). False + a Vietnamese error when missing or broken.</summary>
    public static bool TryInitialize(out string? error, string? folder = null) =>
        throw new NotImplementedException("Track B: FFmpegRuntime");

    /// <summary>e.g. "9.0"; null before TryInitialize succeeded.</summary>
    public static string? Version => null;
}
