using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Cv.Core;
using Cv.Core.Nvr;
using Cv.Core.Video;

namespace Cv.App.Live;

/// <summary>
/// One camera in the live grid (or the big single view): video, name strip, state overlay, hover buttons, drag to swap.
/// Opens its source only when told to, and never for an offline camera.
/// </summary>
public partial class CameraTile : UserControl
{
    const string DragFormat = "Cv.App.TileSlot";
    Point? _pressAt;

    public CameraTile()
    {
        InitializeComponent();
        Video.FirstFrame += _ => UpdateOverlay();
        Root.MouseEnter += (_, _) => UpdateHover(true);
        Root.MouseLeave += (_, _) => UpdateHover(false);
        Root.MouseLeftButtonDown += OnDown;
        Root.MouseMove += OnMove;
        Root.MouseLeftButtonUp += OnUp;
        Root.DragEnter += OnDragOver;
        Root.DragOver += OnDragOver;
        Root.DragLeave += (_, _) => DropHighlight.Visibility = Visibility.Collapsed;
        Root.Drop += OnDrop;
    }

    public ChannelInfo? Camera { get; private set; }
    public StreamKind Kind { get; private set; } = StreamKind.Sub;
    public IVideoSource? Source { get; private set; }

    /// <summary>Position in the grid page (for drag to swap).</summary>
    public int Slot { get; set; }

    /// <summary>Single view: no name strip, no hover buttons, no drag.</summary>
    public bool IsSingle { get; set; }

    public event Action<CameraTile>? Clicked;
    public event Action<CameraTile>? PlaybackRequested;
    public event Action<CameraTile>? SnapshotRequested;
    public event Action<int, int>? SwapRequested;

    /// <summary>Shows a camera (or an empty slot). Keeps the open source when nothing changed.</summary>
    public void Bind(ChannelInfo? camera, StreamKind kind)
    {
        if (Camera == camera && Kind == kind) return;
        Close();
        Camera = camera;
        Kind = kind;
        NameText.Text = camera?.Name ?? "";
        NameStrip.Visibility = camera is null || IsSingle ? Visibility.Collapsed : Visibility.Visible;
        EmptyFill.Visibility = camera is null ? Visibility.Visible : Visibility.Collapsed;
        Root.Cursor = camera is null || IsSingle ? null : Cursors.Hand;
        UpdateOverlay();
    }

    /// <summary>Starts the stream (live, this tile's kind). Offline cameras and empty slots are never opened.</summary>
    public void Open(Services s)
    {
        if (Source is not null || Camera is not { Online: true } cam || s.Nvr is null) return;
        try
        {
            var src = s.CreateSource(s.Nvr.LiveUrl(cam.No, Kind), live: true, Video.TargetPixels());
            src.StateChanged += OnSourceState;
            Source = src;
            Video.Source = src;
            src.Start();
        }
        catch (Exception ex)
        {
            Log.Error($"Live source for channel {cam.No} not created", ex);
            Source = null;
            Video.Source = null;
            ShowOverlay(Glyphs.Error, "Không xem được camera này", "DangerBrush");
            return;
        }
        UpdateOverlay();
    }

    /// <summary>Stops and frees the stream and its picture.</summary>
    public void Close()
    {
        var src = Source;
        if (src is not null)
        {
            src.StateChanged -= OnSourceState;
            Video.Source = null;
            Source = null;
            try { src.Dispose(); } catch (Exception ex) { Log.Warn("Live source dispose failed", ex); }
        }
        Video.ClearPicture();
        UpdateOverlay();
    }

    void OnSourceState(IVideoSource src) =>
        Dispatcher.BeginInvoke(() =>
        {
            if (ReferenceEquals(src, Source)) UpdateOverlay();
        });

    void UpdateOverlay()
    {
        var cam = Camera;
        if (cam is null) { Overlay.Visibility = Visibility.Collapsed; return; }
        if (!cam.Online) { ShowOverlay(Glyphs.Power, "Camera đang tắt", "MutedBrush"); return; }
        var src = Source;
        if (src is null) { ShowOverlay(Glyphs.Sync, "Đang kết nối…", "MutedBrush"); return; }
        switch (src.State)
        {
            case VideoState.Reconnecting:
                ShowOverlay(Glyphs.Warning, "Mất kết nối, đang thử lại…", "WarnBrush");
                break;
            case VideoState.Failed:
            case VideoState.Ended:
                ShowOverlay(Glyphs.Error, src.Error ?? "Không xem được camera này", "DangerBrush");
                break;
            case VideoState.Playing when Video.HasFrame:
            case VideoState.Paused when Video.HasFrame:
                Overlay.Visibility = Visibility.Collapsed;
                break;
            default:
                ShowOverlay(Glyphs.Sync, "Đang kết nối…", "MutedBrush");
                break;
        }
    }

    void ShowOverlay(string glyph, string text, string brush)
    {
        OverlayIcon.Text = glyph;
        OverlayIcon.Foreground = (Brush)FindResource(brush);
        OverlayText.Text = text;
        Overlay.Visibility = Visibility.Visible;
    }

    void UpdateHover(bool over)
    {
        if (IsSingle || Camera is null || !over)
        {
            HoverBar.Visibility = Visibility.Collapsed;
            return;
        }
        var online = Camera.Online;
        SnapshotButton.Visibility = online ? Visibility.Visible : Visibility.Collapsed;
        ZoomButton.Visibility = online ? Visibility.Visible : Visibility.Collapsed;
        HoverBar.Visibility = Visibility.Visible;
    }

    void OnPlayback(object sender, RoutedEventArgs e) => PlaybackRequested?.Invoke(this);
    void OnSnapshot(object sender, RoutedEventArgs e) => SnapshotRequested?.Invoke(this);
    void OnZoom(object sender, RoutedEventArgs e) => Clicked?.Invoke(this);

    // ---- Click and drag to swap ----

    void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (Camera is null) return;
        if (IsSingle)
        {
            if (e.ClickCount == 2) Clicked?.Invoke(this);
            return;
        }
        _pressAt = e.GetPosition(Root);
    }

    void OnMove(object sender, MouseEventArgs e)
    {
        if (_pressAt is not { } p || e.LeftButton != MouseButtonState.Pressed) return;
        var d = e.GetPosition(Root) - p;
        if (Math.Abs(d.X) < 8 && Math.Abs(d.Y) < 8) return;
        _pressAt = null;
        DragDrop.DoDragDrop(Root, new DataObject(DragFormat, Slot), DragDropEffects.Move);
    }

    void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (_pressAt is null) return;
        _pressAt = null;
        Clicked?.Invoke(this);
    }

    void OnDragOver(object sender, DragEventArgs e)
    {
        var ok = !IsSingle && Camera is not null && e.Data.GetDataPresent(DragFormat) && (int)e.Data.GetData(DragFormat) != Slot;
        e.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
        DropHighlight.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        DropHighlight.Visibility = Visibility.Collapsed;
        if (IsSingle || !e.Data.GetDataPresent(DragFormat)) return;
        var from = (int)e.Data.GetData(DragFormat);
        if (from != Slot) SwapRequested?.Invoke(from, Slot);
    }
}
