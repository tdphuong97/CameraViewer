using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Cv.App.Logic;
using Cv.Core.Nvr;

namespace Cv.App.Controls;

/// <summary>
/// The playback day: recorded spans as bars, gaps visible, playhead, hover time, click to jump, wheel to zoom, drag to
/// pan when zoomed, and the export range in orange with draggable handles.
/// </summary>
public sealed class TimelineControl : FrameworkElement
{
    const double LabelHeight = 18;
    const double TrackTop = 22;
    const double TrackHeight = 34;
    const double HandleHit = 8;

    static readonly Brush TrackBrush = Frozen(Color.FromRgb(0x0F, 0x11, 0x15));
    static readonly Pen TrackPen = FrozenPen(Color.FromRgb(0x2A, 0x2F, 0x3A), 1);
    static readonly Brush SpanBrush = Frozen(Color.FromRgb(0x3B, 0x82, 0xF6));
    static readonly Brush LabelBrush = Frozen(Color.FromRgb(0x8A, 0x93, 0xA6));
    static readonly Pen TickPen = FrozenPen(Color.FromRgb(0x3A, 0x41, 0x50), 1);
    static readonly Brush RangeFill = Frozen(Color.FromArgb(0x55, 0xF5, 0x9E, 0x0B));
    static readonly Brush RangeBrush = Frozen(Color.FromRgb(0xF5, 0x9E, 0x0B));
    static readonly Pen RangePen = FrozenPen(Color.FromRgb(0xF5, 0x9E, 0x0B), 2);
    static readonly Pen PlayheadPen = FrozenPen(Colors.White, 2);
    static readonly Brush PlayheadBrush = Frozen(Colors.White);
    static readonly Pen HoverPen = FrozenPen(Color.FromArgb(0xB0, 0xE6, 0xE8, 0xEC), 1);
    static readonly Brush BubbleBrush = Frozen(Color.FromRgb(0x2A, 0x2F, 0x3A));
    static readonly Brush TextBrush = Frozen(Color.FromRgb(0xE6, 0xE8, 0xEC));
    static readonly Brush FutureBrush = Frozen(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF));
    static readonly Typeface Face = new("Segoe UI");

    enum DragMode { None, Press, Pan, Start, End }

    readonly Dictionary<string, FormattedText> _labels = [];
    IReadOnlyList<TimeRange> _spans = [];
    DateTime? _position, _rangeStart, _rangeEnd;
    DateTime? _now;
    double? _hoverX;
    DragMode _drag;
    double _downX, _lastX;

    public TimelineControl()
    {
        Focusable = false;
        Cursor = Cursors.Hand;
        Height = LabelHeight + 4 + TrackHeight + 10;
    }

    public TimelineWindow View { get; } = new(DateTime.Today);

    /// <summary>The user clicked a time.</summary>
    public event Action<DateTime>? SeekRequested;

    /// <summary>The user dragged a range handle.</summary>
    public event Action<DateTime?, DateTime?>? RangeChanged;

    /// <summary>Zoom or pan changed (zoom buttons follow).</summary>
    public event Action? ViewChanged;

    public DateTime Day
    {
        get => View.DayStart;
        set
        {
            View.SetDay(value);
            InvalidateVisual();
        }
    }

    public IReadOnlyList<TimeRange> Spans
    {
        get => _spans;
        set
        {
            _spans = value;
            InvalidateVisual();
        }
    }

    /// <summary>The recorder's "now"; time after it is drawn lighter (nothing can be recorded there yet).</summary>
    public DateTime? Now
    {
        get => _now;
        set { _now = value; InvalidateVisual(); }
    }

    public DateTime? Position
    {
        get => _position;
        set
        {
            if (_position == value) return;
            _position = value;
            if (_drag == DragMode.None && value is { } p && View.Follow(p)) ViewChanged?.Invoke();
            InvalidateVisual();
        }
    }

    /// <summary>The orange range as drawn.</summary>
    public (DateTime? Start, DateTime? End) Range => (_rangeStart, _rangeEnd);

    public void SetRange(DateTime? start, DateTime? end)
    {
        _rangeStart = start;
        _rangeEnd = end;
        InvalidateVisual();
    }

    /// <summary>Zoom buttons: shows <paramref name="length"/> centered on the playhead.</summary>
    public void Zoom(TimeSpan length)
    {
        var center = _position ?? View.ViewStart + TimeSpan.FromTicks(View.Length.Ticks / 2);
        View.ZoomCentered(length, center);
        ViewChanged?.Invoke();
        InvalidateVisual();
    }

    // ---- Drawing ----

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        var track = new Rect(0.5, TrackTop + 0.5, w - 1, TrackHeight - 1);
        dc.DrawRoundedRectangle(TrackBrush, TrackPen, track, 5, 5);

        dc.PushClip(new RectangleGeometry(new Rect(1, TrackTop, w - 2, TrackHeight)));
        foreach (var s in _spans)
        {
            if (s.End <= View.ViewStart || s.Start >= View.ViewEnd) continue;
            var x0 = Math.Max(0, View.ToX(s.Start, w));
            var x1 = Math.Min(w, View.ToX(s.End, w));
            dc.DrawRectangle(SpanBrush, null, new Rect(x0, TrackTop + 7, Math.Max(1.5, x1 - x0), TrackHeight - 14));
        }
        if (_now is { } now && now < View.ViewEnd)
        {
            var nx = Math.Max(0, View.ToX(now, w));
            dc.DrawRectangle(FutureBrush, null, new Rect(nx, TrackTop, w - nx, TrackHeight));
        }
        if (_rangeStart is { } rs && _rangeEnd is { } re)
        {
            var x0 = View.ToX(rs, w);
            var x1 = View.ToX(re, w);
            dc.DrawRectangle(RangeFill, null, new Rect(x0, TrackTop, Math.Max(1, x1 - x0), TrackHeight));
        }
        dc.Pop();

        DrawTicks(dc, w);
        if (_rangeStart is { } a) DrawHandle(dc, View.ToX(a, w), w);
        if (_rangeEnd is { } b) DrawHandle(dc, View.ToX(b, w), w);

        if (_position is { } p && p >= View.ViewStart && p <= View.ViewEnd)
        {
            var x = Snap(View.ToX(p, w));
            dc.DrawLine(PlayheadPen, new Point(x, TrackTop - 3), new Point(x, TrackTop + TrackHeight + 3));
            var head = new StreamGeometry();
            using (var g = head.Open())
            {
                g.BeginFigure(new Point(x - 6, TrackTop - 8), true, true);
                g.LineTo(new Point(x + 6, TrackTop - 8), true, false);
                g.LineTo(new Point(x, TrackTop - 1), true, false);
            }
            head.Freeze();
            dc.DrawGeometry(PlayheadBrush, null, head);
        }

        if (_hoverX is { } hx && _drag is DragMode.None or DragMode.Press)
        {
            dc.DrawLine(HoverPen, new Point(Snap(hx), TrackTop), new Point(Snap(hx), TrackTop + TrackHeight));
            var t = View.ToTime(hx, w);
            var ft = Text(t.ToString("HH:mm:ss", CultureInfo.InvariantCulture), TextBrush, 12);
            var bw = ft.Width + 14;
            var bx = Math.Clamp(hx - bw / 2, 0, w - bw);
            dc.DrawRoundedRectangle(BubbleBrush, null, new Rect(bx, 0, bw, LabelHeight + 1), 4, 4);
            dc.DrawText(ft, new Point(bx + 7, (LabelHeight + 1 - ft.Height) / 2));
        }
    }

    void DrawTicks(DrawingContext dc, double w)
    {
        var step = View.TickStep(w, 72);
        for (var t = View.FirstTick(step); t <= View.ViewEnd; t += step)
        {
            var x = Snap(View.ToX(t, w));
            dc.DrawLine(TickPen, new Point(x, TrackTop - 4), new Point(x, TrackTop));
            var label = t == View.DayEnd ? "24:00" : t.ToString("HH:mm", CultureInfo.InvariantCulture);
            var ft = Label(label);
            var lx = Math.Clamp(x - ft.Width / 2, 0, w - ft.Width);
            dc.DrawText(ft, new Point(lx, 1));
        }
    }

    static void DrawHandle(DrawingContext dc, double x, double w)
    {
        if (x < -HandleHit || x > w + HandleHit) return;
        x = Snap(x);
        dc.DrawLine(RangePen, new Point(x, TrackTop - 2), new Point(x, TrackTop + TrackHeight + 2));
        dc.DrawRoundedRectangle(RangeBrush, null, new Rect(x - 4, TrackTop + TrackHeight / 2 - 8, 8, 16), 2.5, 2.5);
    }

    static double Snap(double x) => Math.Round(x) + 0.5;

    FormattedText Label(string s)
    {
        if (_labels.TryGetValue(s, out var ft)) return ft;
        if (_labels.Count > 400) _labels.Clear();
        return _labels[s] = Text(s, LabelBrush, 11.5);
    }

    FormattedText Text(string s, Brush brush, double size) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    // ---- Mouse ----

    DragMode HitHandle(double x)
    {
        var w = ActualWidth;
        if (_rangeEnd is { } e && Math.Abs(View.ToX(e, w) - x) <= HandleHit) return DragMode.End;
        if (_rangeStart is { } s && Math.Abs(View.ToX(s, w) - x) <= HandleHit) return DragMode.Start;
        return DragMode.None;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var x = e.GetPosition(this).X;
        var hit = HitHandle(x);
        _drag = hit == DragMode.None ? DragMode.Press : hit;
        _downX = _lastX = x;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var x = e.GetPosition(this).X;
        var w = ActualWidth;
        _hoverX = x;
        switch (_drag)
        {
            case DragMode.Start when _rangeStart is not null:
            {
                var t = Whole(View.ToTime(x, w));
                if (_rangeEnd is { } end && t > end.AddSeconds(-1)) t = end.AddSeconds(-1);
                _rangeStart = t;
                RangeChanged?.Invoke(_rangeStart, _rangeEnd);
                break;
            }
            case DragMode.End when _rangeEnd is not null:
            {
                var t = Whole(View.ToTime(x, w));
                if (_rangeStart is { } start && t < start.AddSeconds(1)) t = start.AddSeconds(1);
                _rangeEnd = t;
                RangeChanged?.Invoke(_rangeStart, _rangeEnd);
                break;
            }
            case DragMode.Press when View.IsZoomed && Math.Abs(x - _downX) > 4:
                _drag = DragMode.Pan;
                Cursor = Cursors.ScrollWE;
                break;
            case DragMode.Pan:
                View.PanBy(x - _lastX, w);
                ViewChanged?.Invoke();
                break;
            case DragMode.None:
                Cursor = HitHandle(x) != DragMode.None ? Cursors.SizeWE : Cursors.Hand;
                break;
        }
        _lastX = x;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        var x = e.GetPosition(this).X;
        var was = _drag;
        _drag = DragMode.None;
        ReleaseMouseCapture();
        Cursor = Cursors.Hand;
        if (was == DragMode.Press) SeekRequested?.Invoke(Whole(View.ToTime(x, ActualWidth)));
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        _drag = DragMode.None;
        base.OnLostMouseCapture(e);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _hoverX = null;
        InvalidateVisual();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        View.ZoomBy(e.Delta > 0 ? 0.6 : 1 / 0.6, e.GetPosition(this).X, ActualWidth);
        ViewChanged?.Invoke();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        InvalidateVisual();
    }

    static DateTime Whole(DateTime t) => new(t.Ticks - t.Ticks % TimeSpan.TicksPerSecond, t.Kind);

    static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    static Pen FrozenPen(Color c, double thickness)
    {
        var p = new Pen(Frozen(c), thickness);
        p.Freeze();
        return p;
    }
}
