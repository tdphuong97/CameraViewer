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
/// Xem lại: one camera, one day. Pickers on top, the video, a big clock and transport, the 24 h timeline, and
/// Lưu video: a panel beside the video with three numbered steps, prefilled around the moment on screen.
/// </summary>
public partial class PlaybackView : UserControl, IPage
{
    IShell _shell = null!;
    Services _s = null!;
    PlaybackController _ctrl = null!;
    readonly DispatcherTimer _tick;
    (ChannelInfo Camera, DateTime At)? _request;
    DateTime? _resumeAt;
    bool _active;
    // People seen by the cameras (recorder-side detection): current camera and day.
    CancellationTokenSource? _peopleCts;
    readonly Dictionary<(string Nvr, int Channel, DateTime Day), IReadOnlyList<PeopleVisit>> _peopleCache = [];
    IReadOnlyList<PeopleVisit> _visits = [];
    bool _settingCamera;
    long _shownSecond = -1;

    public PlaybackView()
    {
        InitializeComponent();
        _tick = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(250) };
        _tick.Tick += (_, _) => UpdatePosition();
        Timeline.SeekRequested += t => _ctrl.Jump(t);
        // Dragging the orange handles on the timeline changes the times in the panel.
        Timeline.RangeChanged += (a, b) =>
        {
            if (!SavePanel.IsOpen || a is not { } from || b is not { } to) return;
            Timeline.SetRange(from, to);
            SavePanel.SetRange(from, to);
        };
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
        SavePanel.Init(shell, () => _ctrl.Camera is null ? null : _ctrl.Position);
        SavePanel.RangeEdited += (a, b) => Timeline.SetRange(a, b);
        SavePanel.Closed += () =>
        {
            Timeline.SetRange(null, null);
            UpdateNameChip();
        };
        PeoplePanel.Closed += UpdateNameChip;
        PeoplePanel.Picked += v => _ctrl.Jump(v.Start.AddSeconds(-3)); // a moment before, to see them come in
        PeoplePanel.RetryRequested += () => { if (_ctrl.Camera is { } c) LoadPeople(c, _ctrl.Day, force: true); };
        _s.NvrChanged += () =>
        {
            SavePanel.Close();
            PeoplePanel.Close();
            _peopleCts?.Cancel();
            _peopleCache.Clear();
            _ctrl.Reset();
            _resumeAt = null;
            CameraBox.ItemsSource = null;
            if (_active) _ = ActivateAsync();
        };
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
        LoadPeople(cam, day);
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
        if (SavePanel.IsOpen) SavePanel.SetCamera(cam); // same times, this camera
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
        SavePanel.Close();
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

    // ---- Lưu video ----

    static DateTime Whole(DateTime t) => new(t.Ticks - t.Ticks % TimeSpan.TicksPerSecond);

    void OnSaveClip(object sender, RoutedEventArgs e) => OpenSavePanel();

    /// <summary>Esc closes the open panel.</summary>
    public bool HandleEscape()
    {
        if (SavePanel.IsOpen) SavePanel.Close();
        else if (PeoplePanel.IsOpen) PeoplePanel.Close();
        else return false;
        return true;
    }

    internal bool SavePanelOpen => SavePanel.IsOpen;
    internal bool PeoplePanelOpen => PeoplePanel.IsOpen;

    /// <summary>The narrower video fills its width with a panel open: the name chip would cover the camera's own date
    /// and time (the panels name the camera anyway).</summary>
    void UpdateNameChip() =>
        NameChip.Visibility = SavePanel.IsOpen || PeoplePanel.IsOpen ? Visibility.Collapsed : Visibility.Visible;

    internal void OpenSavePanel()
    {
        if (_ctrl.Camera is not { } cam || _s.NvrConfig is not { } nvr)
        {
            _shell.Toast("Hãy chọn camera trước.", ToastKind.Warn);
            return;
        }
        if (SavePanel.IsOpen)
        {
            SavePanel.FocusSave(); // keep what was set
            return;
        }
        var (from, to) = DefaultRange(Whole(_ctrl.Position), _ctrl.Day, _s.DeviceNow);
        PeoplePanel.Close();
        SavePanel.Open(nvr.Id, cam, _ctrl.Day, from, to);
        UpdateNameChip();
        Timeline.SetRange(from, to);
        // A 5-minute range is a sliver on 24 hours: show one hour around it.
        if (Timeline.View.Length > TimeSpan.FromHours(1)) Timeline.Zoom(TimeSpan.FromHours(1));
    }

    /// <summary>
    /// 2 minutes before to 3 minutes after the moment on screen, kept inside the day and before the recorder's now.
    /// </summary>
    internal static (DateTime From, DateTime To) DefaultRange(DateTime at, DateTime day, DateTime now)
    {
        day = day.Date;
        var last = day.AddDays(1).AddSeconds(-1);
        if (now < last) last = Whole(now);
        var from = at.AddMinutes(-2);
        var to = at.AddMinutes(3);
        if (to > last) to = last;
        if (from < day) from = day;
        if (to - from < TimeSpan.FromMinutes(1)) from = to.AddMinutes(-5) < day ? day : to.AddMinutes(-5);
        return (from, to);
    }

    // ---- Có người: the times the camera saw a person ----

    void OnPeople(object sender, RoutedEventArgs e) => OpenPeoplePanel();

    internal void OpenPeoplePanel()
    {
        if (_ctrl.Camera is not { } cam)
        {
            _shell.Toast("Hãy chọn camera trước.", ToastKind.Warn);
            return;
        }
        SavePanel.Close();
        PeoplePanel.Open();
        UpdateNameChip();
        // Today keeps growing: look again; an earlier day comes from the cache.
        LoadPeople(cam, _ctrl.Day, force: _ctrl.Day == _s.DeviceNow.Date);
    }

    /// <summary>Asks the recorder for the day's detections of this camera: green marks on the timeline, the count on
    /// the button, the list in the panel when open. Hidden for recorders that cannot search them.</summary>
    async void LoadPeople(ChannelInfo cam, DateTime day, bool force = false)
    {
        _peopleCts?.Cancel();
        var cts = _peopleCts = new CancellationTokenSource();
        day = day.Date;
        _visits = [];
        Timeline.Marks = [];
        if (_s.Nvr is not INvrDetections detections || _s.NvrConfig is not { } nvr)
        {
            PeopleButton.Visibility = Visibility.Collapsed;
            PeoplePanel.Close();
            return;
        }
        PeopleButton.Visibility = Visibility.Visible;
        PeopleButton.Content = "Có người · …";
        if (PeoplePanel.IsOpen) PeoplePanel.ShowLoading(cam.Name, day);
        var key = (nvr.Id, cam.No, day);
        try
        {
            if (force || !_peopleCache.TryGetValue(key, out var visits))
            {
                var now = _s.DeviceNow;
                var end = day.AddDays(1) < now ? day.AddDays(1) : now;
                visits = PeopleTimeline.Merge(await detections.SearchPeopleAsync(cam.No, day, end, cts.Token));
                if (day < now.Date) _peopleCache[key] = visits; // a past day no longer changes
            }
            if (cts.IsCancellationRequested) return;
            _visits = visits;
            Timeline.Marks = visits.Select(v => new TimeRange(v.Start, v.End)).ToList();
            PeopleButton.Content = $"Có người · {visits.Count}";
            if (PeoplePanel.IsOpen) PeoplePanel.ShowVisits(cam.Name, day, visits, detections.GetPictureAsync, _ctrl.Camera is null ? null : _ctrl.Position);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (NvrDetectionUnsupportedException)
        {
            if (cts.IsCancellationRequested) return;
            PeopleButton.Visibility = Visibility.Collapsed;
            if (PeoplePanel.IsOpen) PeoplePanel.ShowUnsupported(cam.Name, day);
        }
        catch (Exception ex)
        {
            if (cts.IsCancellationRequested) return;
            Log.Warn($"People search failed (ch{cam.No} {day:yyyy-MM-dd})", ex);
            PeopleButton.Content = "Có người";
            if (PeoplePanel.IsOpen)
                PeoplePanel.ShowError(cam.Name, day, ex is NvrException ? ex.Message : "Chưa tìm được những lúc có người. Hãy thử lại.");
        }
    }

    // ---- Self-test and capture hooks ----

    internal PlaybackController TestController => _ctrl;
    internal SavePanel TestPanel => SavePanel;
    internal PeoplePanel TestPeople => PeoplePanel;
    internal IReadOnlyList<PeopleVisit> TestVisits => _visits;
    internal int TestMarks => Timeline.Marks.Count;
    internal string TestPeopleButton => PeopleButton.Visibility == Visibility.Visible ? PeopleButton.Content as string ?? "" : "";
    internal (DateTime? Start, DateTime? End) TestTimelineRange => Timeline.Range;
    internal void TestTogglePause() => OnPlayPause(this, new RoutedEventArgs());
}
