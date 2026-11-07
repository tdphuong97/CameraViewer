using Cv.Core;
using Cv.Core.Video;
using Cv.Media.Internal;
using FFmpeg.AutoGen;

namespace Cv.Media;

/// <summary>Creates FFmpeg decoding pipelines. One shared D3D11VA device for all sources, created on first need.</summary>
public sealed unsafe class FFmpegVideoSourceFactory : IVideoSourceFactory, IDisposable
{
    private readonly object _gate = new();
    private AVBufferRef* _hwDevice;
    private bool _hwTried;
    private bool _disposed;

    public IVideoSource Create(VideoSourceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ObjectDisposedException.ThrowIf(_disposed, this);
        FFmpegRuntime.EnsureInitialized();
        return new FFmpegVideoSource(options, this);
    }

    /// <summary>True once the shared D3D11VA device exists (it is created by the first wide stream).</summary>
    internal bool HardwareDeviceCreated
    {
        get { lock (_gate) return _hwDevice != null; }
    }

    /// <summary>A new reference to the shared D3D11VA device, or null when unavailable. The caller owns the reference.</summary>
    internal AVBufferRef* RefHardwareDevice()
    {
        lock (_gate)
        {
            if (_disposed) return null;
            if (!_hwTried)
            {
                _hwTried = true;
                AVBufferRef* dev = null;
                int r = ffmpeg.av_hwdevice_ctx_create(&dev, AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA, null, null, 0);
                if (r < 0)
                    Log.Warn("D3D11VA unavailable, software decoding only: " + FFmpegError.Describe(r));
                else
                {
                    _hwDevice = dev;
                    Log.Info("D3D11VA device created");
                }
            }
            return _hwDevice == null ? null : ffmpeg.av_buffer_ref(_hwDevice);
        }
    }

    /// <summary>Sources keep their own device reference, so disposing the factory first is safe.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_hwDevice != null)
            {
                var d = _hwDevice;
                ffmpeg.av_buffer_unref(&d);
                _hwDevice = null;
            }
        }
    }
}
