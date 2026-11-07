namespace Cv.App.Logic;

/// <summary>
/// The visible part of one day on the playback timeline: maps times to x and back, zooms around a point, pans.
/// Pure logic, no WPF. Width is the drawing width in DIPs.
/// </summary>
public sealed class TimelineWindow
{
    public static readonly TimeSpan Day = TimeSpan.FromHours(24);
    public static readonly TimeSpan MinLength = TimeSpan.FromMinutes(10);

    static readonly TimeSpan[] TickSteps =
    [
        TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10),
        TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30), TimeSpan.FromHours(1), TimeSpan.FromHours(2),
        TimeSpan.FromHours(3), TimeSpan.FromHours(4), TimeSpan.FromHours(6),
    ];

    public DateTime DayStart { get; private set; }

    /// <summary>Offset of the left edge from the start of the day.</summary>
    public TimeSpan Offset { get; private set; }

    public TimeSpan Length { get; private set; } = Day;

    public DateTime ViewStart => DayStart + Offset;
    public DateTime ViewEnd => ViewStart + Length;
    public DateTime DayEnd => DayStart + Day;
    public bool IsZoomed => Length < Day;

    public TimelineWindow(DateTime day) => DayStart = day.Date;

    /// <summary>New day: keeps the zoom, keeps the same time of day in view.</summary>
    public void SetDay(DateTime day) => DayStart = day.Date;

    public double ToX(DateTime t, double width) =>
        width <= 0 ? 0 : (t - ViewStart).Ticks / (double)Length.Ticks * width;

    public DateTime ToTime(double x, double width)
    {
        if (width <= 0) return ViewStart;
        var t = ViewStart + TimeSpan.FromTicks((long)(x / width * Length.Ticks));
        return t < DayStart ? DayStart : t > DayEnd ? DayEnd : t;
    }

    /// <summary>Sets the visible length, keeping <paramref name="anchor"/> at the same fraction of the width.</summary>
    public void Zoom(TimeSpan length, DateTime anchor, double anchorFraction)
    {
        length = Clamp(length, MinLength, Day);
        anchorFraction = Math.Clamp(anchorFraction, 0, 1);
        Length = length;
        Offset = anchor - DayStart - TimeSpan.FromTicks((long)(length.Ticks * anchorFraction));
        ClampOffset();
    }

    /// <summary>Sets the visible length centered on <paramref name="center"/> (zoom buttons).</summary>
    public void ZoomCentered(TimeSpan length, DateTime center) => Zoom(length, center, 0.5);

    /// <summary>Mouse wheel: factor &lt; 1 zooms in around x.</summary>
    public void ZoomBy(double factor, double x, double width)
    {
        if (width <= 0) return;
        var anchor = ToTime(x, width);
        Zoom(TimeSpan.FromTicks((long)(Length.Ticks * factor)), anchor, x / width);
    }

    /// <summary>Drag to pan: moving the mouse right by dx shows earlier times.</summary>
    public void PanBy(double dx, double width)
    {
        if (width <= 0) return;
        Offset -= TimeSpan.FromTicks((long)(dx / width * Length.Ticks));
        ClampOffset();
    }

    /// <summary>When zoomed, scrolls so <paramref name="t"/> stays in view (playhead follow). Returns true when it moved.</summary>
    public bool Follow(DateTime t)
    {
        if (!IsZoomed) return false;
        var margin = TimeSpan.FromTicks(Length.Ticks / 20);
        if (t >= ViewStart + margin && t <= ViewEnd - margin) return false;
        Offset = t - DayStart - TimeSpan.FromTicks(Length.Ticks / 5);
        ClampOffset();
        return true;
    }

    /// <summary>The smallest step whose labels are at least <paramref name="minPx"/> apart.</summary>
    public TimeSpan TickStep(double width, double minPx = 64)
    {
        if (width <= 0) return TimeSpan.FromHours(6);
        foreach (var s in TickSteps)
            if (s.Ticks / (double)Length.Ticks * width >= minPx) return s;
        return TickSteps[^1];
    }

    /// <summary>The first tick at or after the left edge, aligned to the step.</summary>
    public DateTime FirstTick(TimeSpan step)
    {
        var ticks = Offset.Ticks;
        var aligned = (ticks + step.Ticks - 1) / step.Ticks * step.Ticks;
        return DayStart + TimeSpan.FromTicks(aligned);
    }

    void ClampOffset()
    {
        if (Offset < TimeSpan.Zero) Offset = TimeSpan.Zero;
        if (Offset + Length > Day) Offset = Day - Length;
    }

    static TimeSpan Clamp(TimeSpan v, TimeSpan min, TimeSpan max) => v < min ? min : v > max ? max : v;
}
