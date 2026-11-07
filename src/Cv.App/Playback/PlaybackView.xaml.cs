using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Cv.App.Logic;
using Cv.Core;
using Cv.Core.Nvr;

namespace Cv.App.Playback;

/// <summary>
/// Xem lại: one camera, one day. Pickers on top, the video, a big clock and transport, the 24 h timeline, and the
/// simple export (mark start, mark end, Lưu đoạn video).
/// </summary>
public partial class PlaybackView : UserControl, IPage
{
    IShell _shell = null!;
    Services _s = null!;
    PlaybackController _ctrl = null!;
    readonly DispatcherTimer _tick;
    (ChannelInfo Camera, DateTime At)? _request;
    DateTime? _resumeAt;
    DateTime? _rangeStart, _rangeEnd;
    bool _active;
    bool _settingCamera;
    long _shownSecond = -1;
    bool _capturePreset;

    public PlaybackView()
    {
        InitializeComponent();
        _tick = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(250) };
        _tick.Tick += (_, _) => UpdatePosition();
        Timeline.SeekRequested += t => _ctrl.Jump(t);
        Timeline.RangeChanged += (a, b) => { _rangeStart = a; _rangeEnd = b; UpdateRange(); };
        Timeline.ViewChanged += UpdateZoomButtons;
        DayPicker.Picked += d => { DayPopup.IsOpen = false; ChangeDay(d); };
        DayPopup.Closed += (_, _) => UpdateDayButtons();
        PreviewKeyDown += OnKey;
    }

    public void Init(IShell shell)
    {
        _shell = shell;
        _s = shell.Services;
        _ctrl = new PlaybackController(_s, Video);
        _ctrl.Changed += OnChanged;
        _s.NvrChanged += () =>
        {
            _ctrl.Stop();
            CameraBox.ItemsSource = null;
        };
        UpdateRange();
        UpdateZoomButtons();
    }

    /// <summary>Next activation plays this camera at this time (tile's Xem lại button).</summary>
    public void Request(ChannelInfo camera, DateTime at) => _request = (camera, at);

    public void Activate()
    {
        _active = true;
        _tick.Start();
        _ = ActivateAsync();
    }

    async Task ActivateAsync()
    {
        IReadOnlyList<ChannelInfo> channels;
        try
        {
            channels = await _s.GetChannelsAsync();
        }
        catch (Exception ex)
        {
            Log.Warn("Channel list failed (playback)", ex);
            ShowOverlay(Glyphs.Warning, ex is NvrException ? ex.Message : "Không kết nối được đầu ghi.", "WarnBrush");
            return;
        }
        if (!_active) return;
        _settingCamera = true;
        if (!ReferenceEquals(CameraBox.ItemsSource, channels)) CameraBox.ItemsSource = channels;
        _settingCamera = false;

        if (_request is { } r)
        {
            _request = null;
            _resumeAt = null;
            await Load(channels.FirstOrDefault(c => c.No == r.Camera.No) ?? r.Camera, r.At.Date, r.At);
        }
        else if (_ctrl.Camera is { } cam && _resumeAt is { } resume)
        {
            _resumeAt = null;
            SelectCamera(cam);
            if (_ctrl.HasSpans && _ctrl.Day == resume.Date) _ctrl.Jump(resume);
            else await Load(cam, _ctrl.Day, resume);
        }
        else if (_ctrl.Camera is null && channels.Count > 0)
        {
            var first = channels.FirstOrDefault(c => c.Online) ?? channels[0];
            var at = _s.DeviceNow.AddMinutes(-5);
            await Load(first, at.Date, at);
        }
        if (_capturePreset) ApplyCapturePreset();
    }

    public void Deactivate()
    {
        _active = false;
        _tick.Stop();
        DayPopup.IsOpen = false;
        if (_ctrl.Camera is not null) _resumeAt = _ctrl.Suspend();
    }

    async Task Load(ChannelInfo cam, DateTime day, DateTime at)
    {
        SelectCamera(cam);
        TimeBox.Text = TimeFormat.HourMinute(at);
        Timeline.Day = day;
        NameChipText.Text = cam.Name;
        UpdateDayButtons(day);
        await _ctrl.LoadAsync(cam, day, at);
    }

    void SelectCamera(ChannelInfo cam)
    {
        _settingCamera = true;
        if (CameraBox.ItemsSource is IEnumerable<ChannelInfo> list)
            CameraBox.SelectedItem = list.FirstOrDefault(c => c.No == cam.No);
        _settingCamera = false;
    }

    void OnChanged()
    {
        Timeline.Spans = _ctrl.Spans;
        Timeline.Now = _ctrl.Day == _s.DeviceNow.Date ? _s.DeviceNow : null;
        var playing = _ctrl.Status == PlaybackStatus.Playing;
        Ui.SetGlyph(PlayButton, playing || _ctrl.Status == PlaybackStatus.Loading ? Glyphs.Pause : Glyphs.Play);
        PlayButton.ToolTip = playing ? "Tạm dừng" : "Phát";
        SnapshotButton.IsEnabled = _ctrl.Status is PlaybackStatus.Playing or PlaybackStatus.Paused && Video.HasFrame;
        switch (_ctrl.Status)
        {
            case PlaybackStatus.Idle:
                ShowOverlay(Glyphs.History, "Chọn camera và thời điểm cần xem lại", "MutedBrush");
                break;
            case PlaybackStatus.Searching:
            case PlaybackStatus.Loading:
                ShowOverlay(Glyphs.Sync, _ctrl.Message ?? "Đang tải…", "MutedBrush");
                break;
            case PlaybackStatus.Playing:
            case PlaybackStatus.Paused:
                if (Video.HasFrame) Overlay.Visibility = Visibility.Collapsed;
                else ShowOverlay(Glyphs.Sync, "Đang tải…", "MutedBrush");
                break;
            case PlaybackStatus.NoRecording:
            case PlaybackStatus.Ended:
                ShowOverlay(Glyphs.Info, _ctrl.Message ?? "Không có ghi hình lúc này", "AccentHoverBrush");
                break;
            case PlaybackStatus.Error:
                ShowOverlay(Glyphs.Warning, _ctrl.Message ?? "Không phát được đoạn ghi hình này.", "WarnBrush");
                break;
        }
        UpdatePosition();
    }

    void ShowOverlay(string glyph, string text, string brush)
    {
        OverlayIcon.Text = glyph;
        OverlayIcon.Foreground = (Brush)FindResource(brush);
        OverlayText.Text = text;
        Overlay.Visibility = Visibility.Visible;
    }

    void UpdatePosition()
    {
        if (_ctrl.Camera is null) return;
        var p = _ctrl.Position;
        Timeline.Position = p;
        var second = p.Ticks / TimeSpan.TicksPerSecond;
        if (second == _shownSecond) return;
        _shownSecond = second;
        ClockText.Text = TimeFormat.Clock(p);
        DateText.Text = TimeFormat.LongDate(p);
        if (Timeline.Now is not null) Timeline.Now = _s.DeviceNow;
    }

    // ---- Pickers ----

    void OnCameraChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_settingCamera || CameraBox.SelectedItem is not ChannelInfo cam || _ctrl.Camera?.No == cam.No) return;
        // Same moment, other camera: what did that one see?
        var at = _ctrl.Camera is null ? _s.DeviceNow.AddMinutes(-5) : _ctrl.Position;
        _ = Load(cam, _ctrl.Day, at);
    }

    void OnToday(object sender, RoutedEventArgs e) => ChangeDay(_s.DeviceNow.Date);
    void OnYesterday(object sender, RoutedEventArgs e) => ChangeDay(_s.DeviceNow.Date.AddDays(-1));

    void OnPickDay(object sender, RoutedEventArgs e)
    {
        DayPicker.Show(_ctrl.Day, _s.DeviceNow.Date);
        DayPopup.IsOpen = true;
    }

    void ChangeDay(DateTime day)
    {
        if (_ctrl.Camera is not { } cam) return;
        if (day == _ctrl.Day) { UpdateDayButtons(); return; }
        var at = day + _ctrl.Position.TimeOfDay;
        var now = _s.DeviceNow;
        if (at > now) at = now.AddMinutes(-5);
        _rangeStart = _rangeEnd = null;
        UpdateRange();
        _ = Load(cam, day, at);
    }

    void UpdateDayButtons() => UpdateDayButtons(_ctrl.Day);

    void UpdateDayButtons(DateTime day)
    {
        var today = _s.DeviceNow.Date;
        TodayButton.IsChecked = day == today;
        YesterdayButton.IsChecked = day == today.AddDays(-1);
        var other = day != today && day != today.AddDays(-1);
        PickDayButton.IsChecked = other;
        PickDayButton.Content = other ? TimeFormat.Date(day) : "Chọn ngày";
    }

    void OnTimeKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        Go();
        e.Handled = true;
    }

    void OnGo(object sender, RoutedEventArgs e) => Go();

    void Go()
    {
        var tod = TimeFormat.ParseTimeOfDay(TimeBox.Text);
        if (tod is null)
        {
            _shell.Toast("Giờ chưa đúng. Hãy gõ theo dạng giờ:phút, ví dụ 14:30.", ToastKind.Warn);
            TimeBox.Focus();
            TimeBox.SelectAll();
            return;
        }
        if (_ctrl.Camera is not { } cam) return;
        TimeBox.Text = $"{tod.Value.Hours:00}:{tod.Value.Minutes:00}";
        var at = _ctrl.Day + tod.Value;
        if (_ctrl.HasSpans) _ctrl.Jump(at);
        else _ = Load(cam, _ctrl.Day, at);
    }

    // ---- Transport ----

    void OnPlayPause(object sender, RoutedEventArgs e) => _ctrl.TogglePause();
    void OnBack30(object sender, RoutedEventArgs e) => _ctrl.Skip(TimeSpan.FromSeconds(-30));
    void OnForward30(object sender, RoutedEventArgs e) => _ctrl.Skip(TimeSpan.FromSeconds(30));

    void OnSnapshot(object sender, RoutedEventArgs e)
    {
        if (_ctrl.Camera is { } cam) _ = _shell.SnapshotAsync(_ctrl.Source, cam.Name, _ctrl.Position);
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || e.OriginalSource is TextBox) return;
        _ctrl.TogglePause();
        e.Handled = true;
    }

    // ---- Zoom ----

    void OnZoom(object sender, RoutedEventArgs e)
    {
        var hours = int.Parse((string)((RadioButton)sender).Tag, System.Globalization.CultureInfo.InvariantCulture);
        Timeline.Zoom(TimeSpan.FromHours(hours));
    }

    void UpdateZoomButtons()
    {
        var len = Timeline.View.Length;
        foreach (RadioButton b in ZoomButtons.Children)
            b.IsChecked = Math.Abs(len.TotalHours - int.Parse((string)b.Tag, System.Globalization.CultureInfo.InvariantCulture)) < 0.01;
    }

    // ---- Range and export ----

    static DateTime Whole(DateTime t) => new(t.Ticks - t.Ticks % TimeSpan.TicksPerSecond);

    void OnMarkStart(object sender, RoutedEventArgs e)
    {
        if (_ctrl.Camera is null) return;
        _rangeStart = Whole(_ctrl.Position);
        if (_rangeEnd is { } end && end <= _rangeStart) _rangeEnd = null;
        UpdateRange();
    }

    void OnMarkEnd(object sender, RoutedEventArgs e)
    {
        if (_ctrl.Camera is null) return;
        _rangeEnd = Whole(_ctrl.Position);
        if (_rangeStart is { } start && start >= _rangeEnd) _rangeStart = null;
        UpdateRange();
    }

    void OnClearRange(object sender, RoutedEventArgs e)
    {
        _rangeStart = _rangeEnd = null;
        UpdateRange();
    }

    void UpdateRange()
    {
        Timeline.SetRange(_rangeStart, _rangeEnd);
        ClearRangeButton.Visibility = _rangeStart is null && _rangeEnd is null ? Visibility.Collapsed : Visibility.Visible;
        RangeText.Foreground = (Brush)FindResource("TextBrush");
        if (_rangeStart is { } a && _rangeEnd is { } b)
            RangeText.Text = $"Đoạn đã chọn: {TimeFormat.Clock(a)} → {TimeFormat.Clock(b)}  ·  {TimeFormat.Duration(b - a)}";
        else if (_rangeStart is { } s)
            RangeText.Text = $"Bắt đầu {TimeFormat.Clock(s)} · hãy đánh dấu kết thúc";
        else if (_rangeEnd is { } en)
            RangeText.Text = $"Kết thúc {TimeFormat.Clock(en)} · hãy đánh dấu bắt đầu";
        else
        {
            RangeText.Text = "Đánh dấu đoạn cần lưu trong lúc xem";
            RangeText.Foreground = (Brush)FindResource("MutedBrush");
        }
    }

    void OnSaveClip(object sender, RoutedEventArgs e) => OpenExportDialog();

    internal void OpenExportDialog()
    {
        if (_ctrl.Camera is not { } cam || _s.NvrConfig is not { } nvr)
        {
            _shell.Toast("Hãy chọn camera trước.", ToastKind.Warn);
            return;
        }
        var from = _rangeStart ?? (_rangeEnd is { } end ? end.AddMinutes(-5) : Whole(_ctrl.Position));
        var to = _rangeEnd ?? from.AddMinutes(5);
        var dialog = new ExportDialog(_shell, nvr.Id, cam, from, to);
        _shell.ShowModal(dialog);
    }

    // ---- Capture mode ----

    /// <summary>Screenshots: shows a marked range around the playhead once playback runs.</summary>
    internal void PresetForCapture() => _capturePreset = true;

    void ApplyCapturePreset()
    {
        _capturePreset = false;
        var p = Whole(_ctrl.Position);
        _rangeStart = p.AddMinutes(-2);
        _rangeEnd = p.AddMinutes(3);
        UpdateRange();
    }
}
