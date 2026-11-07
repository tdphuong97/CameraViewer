using Cv.Core.Video;

namespace Cv.Media;

/// <summary>Creates FFmpeg decoding pipelines (Track B implements). One shared D3D11VA device for all sources.</summary>
public sealed class FFmpegVideoSourceFactory : IVideoSourceFactory, IDisposable
{
    public IVideoSource Create(VideoSourceOptions options) => throw new NotImplementedException("Track B: FFmpegVideoSource");
    public void Dispose() { }
}
