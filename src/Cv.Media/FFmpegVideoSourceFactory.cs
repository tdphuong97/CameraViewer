using Cv.Core;
using Cv.Core.Video;
using Cv.Media.Internal;
using FFmpeg.AutoGen;

namespace Cv.Media;

/// <summary>Creates FFmpeg decoding pipelines. One shared D3D11VA device for all sources, created on first need and dropped when idle.</summary>
public sealed unsafe class FFmpegVideoSourceFactory : IVideoSourceFactory, IDisposable
{
    private readonly object _gate = new();
    private AVBufferRef* _hwDevice;
    private bool _hwFailed;
    private int _hwUsers;
    private bool _disposed;

    public IVideoSource Create(VideoSourceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ObjectDisposedException.ThrowIf(_disposed, this);
        FFmpegRuntime.EnsureInitialized();
        return new FFmpegVideoSource(options, this);
    }

    /// <summary>True while the shared D3D11VA device exists (created by the first wide stream, dropped when idle).</summary>
    internal bool HardwareDeviceCreated
    {
        get { lock (_gate) return _hwDevice != null; }
    }

    /// <summary>
    /// A new reference to the shared D3D11VA device, or null when unavailable. The caller owns the reference and calls
    /// ReleaseHardwareDevice once its decoder is freed.
    /// </summary>
    internal AVBufferRef* RefHardwareDevice()
    {
        lock (_gate)
        {
            if (_disposed || _hwFailed) return null;
            if (_hwDevice == null)
            {
                AVBufferRef* dev = null;
                int r = ffmpeg.av_hwdevice_ctx_create(&dev, AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA, null, null, 0);
                if (r < 0)
                {
                    _hwFailed = true; // no retry: the machine has no usable D3D11 video decoder
                    Log.Warn("D3D11VA unavailable, software decoding only: " + FFmpegError.Describe(r));
                    return null;
                }
                _hwDevice = dev;
                Log.Info("D3D11VA device created");
            }
            _hwUsers++;
            return ffmpeg.av_buffer_ref(_hwDevice);
        }
    }

    /// <summary>The device (tens of MB of driver memory) is dropped when no decoder uses it, e.g. after leaving single view.</summary>
    internal void ReleaseHardwareDevice()
    {
        lock (_gate)
        {
            if (_hwUsers > 0) _hwUsers--;
            if (_hwUsers == 0) DropDevice();
        }
    }

    private void DropDevice()
    {
        if (_hwDevice == null) return;
        var d = _hwDevice;
        ffmpeg.av_buffer_unref(&d);
        _hwDevice = null;
        Log.Info("D3D11VA device released");
    }

    /// <summary>Sources keep their own device reference, so disposing the factory first is safe.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            DropDevice();
        }
    }
}
