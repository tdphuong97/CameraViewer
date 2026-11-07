using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cv.Core.Video;

namespace Cv.App.Video;

/// <summary>
/// Pulls new frames into every registered VideoView from one CompositionTarget.Rendering handler. Hooked only while
/// at least one view shows a source, so an idle app does not render at the display rate.
/// </summary>
internal static class FramePump
{
    static readonly List<VideoView> Views = [];
    static bool _hooked;
    static TimeSpan _last;

    public static int Count => Views.Count;

    public static void Add(VideoView v)
    {
        if (Views.Contains(v)) return;
        Views.Add(v);
        if (!_hooked)
        {
            CompositionTarget.Rendering += OnRendering;
            _hooked = true;
        }
    }

    public static void Remove(VideoView v)
    {
        Views.Remove(v);
        if (Views.Count == 0 && _hooked)
        {
            CompositionTarget.Rendering -= OnRendering;
            _hooked = false;
        }
    }

    static void OnRendering(object? sender, EventArgs e)
    {
        // Rendering can fire more than once per frame; pull once per frame.
        var t = ((RenderingEventArgs)e).RenderingTime;
        if (t == _last) return;
        _last = t;
        for (var i = 0; i < Views.Count; i++) Views[i].Pull();
    }
}

/// <summary>
/// Shows one IVideoSource: a WriteableBitmap filled from TryCopyLatest, drawn letterboxed on black. The bitmap is
/// reallocated only when the frame size changes; the source is told the device-pixel size after resizes settle.
/// </summary>
public sealed class VideoView : FrameworkElement
{
    readonly FrameCopier _copier;
    WriteableBitmap? _bitmap;
    IVideoSource? _source;
    long _sequence;
    bool _hasFrame;
    DispatcherTimer? _resizeTimer;

    public VideoView()
    {
        _copier = Copy;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.Linear);
        Loaded += (_, _) => UpdatePump();
        Unloaded += (_, _) => FramePump.Remove(this);
    }

    /// <summary>Raised on the UI thread when the first frame of the current source is shown.</summary>
    public event Action<VideoView>? FirstFrame;

    public bool HasFrame => _hasFrame;

    /// <summary>The source shown; the view does not own it (the caller disposes it).</summary>
    public IVideoSource? Source
    {
        get => _source;
        set
        {
            if (ReferenceEquals(_source, value)) return;
            _source = value;
            _sequence = 0;
            _hasFrame = false;
            UpdatePump();
        }
    }

    /// <summary>Drops the last picture (camera changed, screen left) so it frees memory and never shows stale video.</summary>
    public void ClearPicture()
    {
        if (_bitmap is null) return;
        _bitmap = null;
        _hasFrame = false;
        InvalidateVisual();
    }

    /// <summary>The bounding box in device pixels the source should scale to.</summary>
    public (int Width, int Height) TargetPixels()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var w = ActualWidth > 0 ? ActualWidth : 640;
        var h = ActualHeight > 0 ? ActualHeight : 360;
        return ((int)Math.Round(w * dpi.DpiScaleX), (int)Math.Round(h * dpi.DpiScaleY));
    }

    void UpdatePump()
    {
        if (_source is not null && IsLoaded) FramePump.Add(this);
        else FramePump.Remove(this);
    }

    internal void Pull()
    {
        var s = _source;
        if (s is null || !s.TryCopyLatest(ref _sequence, _copier)) return;
        if (_hasFrame) return;
        _hasFrame = true;
        FirstFrame?.Invoke(this);
    }

    void Copy(IntPtr bgra, int width, int height, int stride)
    {
        if (_bitmap is null || _bitmap.PixelWidth != width || _bitmap.PixelHeight != height)
        {
            _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr32, null);
            InvalidateVisual();
        }
        _bitmap.WritePixels(new Int32Rect(0, 0, width, height), bgra, stride * height, stride);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = RenderSize;
        dc.DrawRectangle(Brushes.Black, null, new Rect(size));
        var b = _bitmap;
        if (b is null || size.Width <= 0 || size.Height <= 0) return;
        var scale = Math.Min(size.Width / b.PixelWidth, size.Height / b.PixelHeight);
        var w = b.PixelWidth * scale;
        var h = b.PixelHeight * scale;
        dc.DrawImage(b, new Rect((size.Width - w) / 2, (size.Height - h) / 2, w, h));
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        InvalidateVisual();
        ScheduleTargetSize();
    }

    // Moving to a monitor with another scale changes the device-pixel size without a layout resize.
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        ScheduleTargetSize();
    }

    void ScheduleTargetSize()
    {
        if (_source is null) return;
        if (_resizeTimer is null)
        {
            _resizeTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
            _resizeTimer.Tick += (_, _) =>
            {
                _resizeTimer!.Stop();
                var (w, h) = TargetPixels();
                _source?.SetTargetSize(w, h);
            };
        }
        _resizeTimer.Stop();
        _resizeTimer.Start();
    }
}
