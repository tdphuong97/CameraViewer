using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Cv.App.Help;
using Cv.App.Live;
using Cv.App.Logic;
using Cv.App.Platform;
using Cv.App.Playback;
using Cv.App.Saved;
using Cv.App.Setup;
using Cv.Core;
using Cv.Core.Export;
using Cv.Core.Nvr;
using Cv.Core.Settings;
using Cv.Core.Update;
using Cv.Core.Video;

namespace Cv.App;

/// <summary>A screen in the main area. Deactivate must release every video source; Activate reopens them.</summary>
public interface IPage
{
    void Activate();
    void Deactivate();
    /// <summary>Esc pressed with no dialog open; true when the page used it.</summary>
    bool HandleEscape() => false;
}

public enum ToastKind { Info, Ok, Warn, Error }

/// <summary>What the screens may ask of the window.</summary>
public interface IShell
{
    Services Services { get; }
    void ShowLive();
    void ShowPlayback(ChannelInfo camera, DateTime at);
    void ShowSaved();
    void ShowSettings();
    void Toast(string text, ToastKind kind = ToastKind.Info, string? action = null, Action? onAction = null);
    void ShowModal(FrameworkElement content);
    void CloseModal();
    Task SnapshotAsync(IVideoSource? source, string cameraName, DateTime when);
    Task CheckUpdateAsync(bool manual);
}

public partial class MainWindow : Window, IShell
{
    readonly Services _s;
    readonly LiveView _live = new();
    readonly PlaybackView _playback = new();
    readonly SavedView _saved = new();
    readonly SettingsView _settings = new();
    readonly RamMeter _ram;
    readonly Dictionary<Guid, ExportStatus> _jobStatus = [];
    readonly DispatcherTimer _updateTimer;
    IPage? _current;
    bool _suspended;
    bool _jobsPending;
    UpdateInfo? _update;
    bool _updating;

    public MainWindow(Services services)
    {
        _s = services;
        InitializeComponent();
        Title = _s.Demo ? "Camera (bản thử)" : "Camera";
        _live.Init(this);
        _playback.Init(this);
        _saved.Init(this);
        _settings.Init(this);
        _ram = new RamMeter(b => RamText.Text = TimeFormat.Ram(b));

        foreach (var j in SafeJobs()) _jobStatus[j.Id] = j.Status;
        _s.Export.JobChanged += _ => QueueJobRefresh();
        UpdateBadge();

        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _updateTimer.Tick += (_, _) =>
        {
            _updateTimer.Stop();
            if (_s.Settings.CheckUpdates) _ = CheckUpdateAsync(manual: false);
        };

        ApplySavedBounds();
        SourceInitialized += (_, _) => Native.DarkTitleBar(this, 0x171A21, 0xE6E8EC);
        StateChanged += OnWindowStateChanged;
        PreviewKeyDown += OnPreviewKeyDown;
        Closing += OnClosing;
        Closed += (_, _) => Shutdown();
    }

    public Services Services => _s;

    /// <summary>Starts on Trực tiếp, or on Cài đặt when no recorder is configured yet.</summary>
    public void Start(bool checkUpdates = true)
    {
        if (_s.IsConfigured) ShowLive();
        else ShowSettings();
        if (checkUpdates) _updateTimer.Start();
    }

    // ---- Navigation ----

    void Navigate(IPage page, RadioButton tab)
    {
        if (!ReferenceEquals(_current, page))
        {
            _current?.Deactivate();
            _current = page;
            Host.Content = page;
            if (!_suspended) page.Activate();
        }
        tab.IsChecked = true;
    }

    public void ShowLive() => Navigate(_live, TabLive);
    public void ShowSaved() => Navigate(_saved, TabSaved);
    public void ShowSettings() => Navigate(_settings, TabSettings);

    public void ShowPlayback(ChannelInfo camera, DateTime at)
    {
        _playback.Request(camera, at);
        if (ReferenceEquals(_current, _playback)) _playback.Activate();
        else Navigate(_playback, TabPlayback);
    }

    internal LiveView LivePage => _live;
    internal PlaybackView PlaybackPage => _playback;
    internal SettingsView SettingsPage => _settings;

    void OnTabLive(object sender, RoutedEventArgs e) => ShowLive();
    void OnTabPlayback(object sender, RoutedEventArgs e) => ShowPlaybackTab();

    internal void ShowPlaybackTab() => Navigate(_playback, TabPlayback);
    void OnTabSaved(object sender, RoutedEventArgs e) => ShowSaved();
    void OnTabSettings(object sender, RoutedEventArgs e) => ShowSettings();

    // ---- User guide: the offline HTML shipped in help\, shown in its own window (WebView2); without WebView2 it opens
    // in the browser, and without the file the copy on GitHub does ----

    internal const string HelpUrl = "https://github.com/tdphuong97/CameraViewer/blob/main/docs/README.md";

    void OnHelp(object sender, RoutedEventArgs e) => OpenHelp();

    internal void OpenHelp()
    {
        var local = HelpWindow.FilePath;
        if (!System.IO.File.Exists(local)) ShowError(Shell.OpenUrl(HelpUrl));
        else if (HelpWindow.RuntimeAvailable()) HelpWindow.Open(this, HelpPage(), () => ShowError(Shell.OpenFile(local)));
        else ShowError(Shell.OpenFile(local));
    }

    /// <summary>The guide page about what is on screen (paths in docs\, without .md).</summary>
    internal string HelpPage()
    {
        return _current switch
        {
            LiveView l => l.InSingle ? "xem-camera/xem-lon-mot-camera" : "xem-camera/xem-tat-ca-camera",
            PlaybackView p => p.SavePanelOpen ? "xem-lai/luu-doan-video" : p.PeoplePanelOpen ? "xem-lai/tim-nguoi" : "xem-lai/xem-lai-camera",
            SavedView => "xem-lai/video-da-luu",
            SettingsView => _s.IsConfigured ? "tham-khao/cac-muc-cai-dat" : "bat-dau/ket-noi-dau-ghi",
            _ => "",
        };
    }

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F1)
        {
            OpenHelp();
            e.Handled = true;
            return;
        }
        if (e.Key != Key.Escape) return;
        if (ModalHost.Visibility == Visibility.Visible)
        {
            CloseModal();
            e.Handled = true;
        }
        else if (_current?.HandleEscape() == true) e.Handled = true;
    }

    // ---- Minimize: release every stream (RAM rule), reopen on restore ----

    void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            if (!_s.Settings.PauseWhenMinimized || _suspended) return;
            _suspended = true;
            _current?.Deactivate();
            // Give the freed buffers back now rather than at the next allocation.
            Dispatcher.BeginInvoke(() => GC.Collect(), DispatcherPriority.ApplicationIdle);
        }
        else if (_suspended)
        {
            _suspended = false;
            _current?.Activate();
        }
    }

    // ---- Toasts, dialogs, snapshots ----

    public void Toast(string text, ToastKind kind = ToastKind.Info, string? action = null, Action? onAction = null)
    {
        var (glyph, brush) = kind switch
        {
            ToastKind.Ok => (Glyphs.Completed, "OkBrush"),
            ToastKind.Warn => (Glyphs.Warning, "WarnBrush"),
            ToastKind.Error => (Glyphs.Error, "DangerBrush"),
            _ => (Glyphs.Info, "AccentHoverBrush"),
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = new TextBlock
        {
            Text = glyph, Style = (Style)FindResource("Icon"), FontSize = 18, Foreground = (Brush)FindResource(brush),
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 12, 0),
        };
        var msg = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, LineHeight = 20 };
        Grid.SetColumn(msg, 1);
        grid.Children.Add(icon);
        grid.Children.Add(msg);
        var card = new Border
        {
            Background = (Brush)FindResource("RaisedBrush"), BorderBrush = (Brush)FindResource("BorderStrongBrush"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(16, 12, 8, 12),
            Margin = new Thickness(0, 0, 0, 10), Child = grid,
        };
        void Close() => Toasts.Children.Remove(card);
        if (action is not null && onAction is not null)
        {
            var b = new Button { Content = action, Style = (Style)FindResource("LinkButton"), Margin = new Thickness(8, -4, 0, -4), VerticalAlignment = VerticalAlignment.Center };
            b.Click += (_, _) => { Close(); onAction(); };
            Grid.SetColumn(b, 2);
            grid.Children.Add(b);
        }
        var x = new Button
        {
            Style = (Style)FindResource("GhostButton"), Width = 30, Height = 30, MinHeight = 30, Padding = new Thickness(0),
            Margin = new Thickness(4, -6, 0, -6), VerticalAlignment = VerticalAlignment.Center, ToolTip = "Đóng",
        };
        Ui.SetGlyph(x, Glyphs.Cancel);
        Ui.SetGlyphSize(x, 11);
        x.Click += (_, _) => Close();
        Grid.SetColumn(x, 3);
        grid.Children.Add(x);

        while (Toasts.Children.Count >= 3) Toasts.Children.RemoveAt(0);
        Toasts.Children.Add(card);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(action is null ? 5 : 9) };
        timer.Tick += (_, _) => { timer.Stop(); Close(); };
        timer.Start();
    }

    public void ShowModal(FrameworkElement content)
    {
        ModalContent.Content = content;
        ModalHost.Visibility = Visibility.Visible;
        content.Focus();
    }

    public void CloseModal()
    {
        ModalHost.Visibility = Visibility.Collapsed;
        ModalContent.Content = null;
    }

    public async Task SnapshotAsync(IVideoSource? source, string cameraName, DateTime when)
    {
        if (source is null)
        {
            Toast("Chưa có hình để chụp.", ToastKind.Warn);
            return;
        }
        try
        {
            var path = await Snapshots.SaveAsync(source, _s.Settings.SnapshotFolder, cameraName, when);
            Toast($"Đã chụp ảnh {cameraName}.", ToastKind.Ok, "Mở ảnh", () => ShowError(Shell.OpenFile(path)));
        }
        catch (InvalidOperationException ex)
        {
            Toast(ex.Message, ToastKind.Warn);
        }
    }

    internal void ShowError(string? message)
    {
        if (message is not null) Toast(message, ToastKind.Warn);
    }

    // ---- Export jobs: badge, finished toast, taskbar flash ----

    IReadOnlyList<ExportJob> SafeJobs()
    {
        try
        {
            return [.. _s.Export.Jobs];
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotImplementedException)
        {
            return [];
        }
    }

    void QueueJobRefresh()
    {
        // JobChanged comes from a background thread, often many times a second: coalesce into one UI update.
        if (_jobsPending) return;
        _jobsPending = true;
        Dispatcher.BeginInvoke(() =>
        {
            _jobsPending = false;
            var jobs = SafeJobs();
            foreach (var j in jobs)
            {
                _jobStatus.TryGetValue(j.Id, out var before);
                if (before != j.Status && j.Status == ExportStatus.Done) OnJobDone(j);
                else if (before != j.Status && j.Status == ExportStatus.Failed) OnJobFailed(j);
                _jobStatus[j.Id] = j.Status;
            }
            UpdateBadge(jobs);
            _saved.Refresh(jobs);
        }, DispatcherPriority.Background);
    }

    void OnJobDone(ExportJob j)
    {
        var r = j.Request;
        Toast($"Đã lưu video {r.CameraName} {TimeFormat.HourMinute(r.From)}–{TimeFormat.HourMinute(r.To)}.", ToastKind.Ok, "Mở video",
            () => ShowError(Shell.OpenFile(j.OutputPath)));
        if (!IsActive) Native.FlashTaskbar(this);
    }

    void OnJobFailed(ExportJob j)
    {
        Toast($"Không lưu được video {j.Request.CameraName}. {j.Error}", ToastKind.Error, "Xem", ShowSaved);
        if (!IsActive) Native.FlashTaskbar(this);
    }

    void UpdateBadge(IReadOnlyList<ExportJob>? jobs = null)
    {
        jobs ??= SafeJobs();
        var running = jobs.Count(j => j.Status is ExportStatus.Queued or ExportStatus.Downloading or ExportStatus.Converting);
        Ui.SetBadge(TabSaved, running > 0 ? running.ToString(System.Globalization.CultureInfo.InvariantCulture) : null);
    }

    // ---- Update ----

    public async Task CheckUpdateAsync(bool manual)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var info = await _s.Updater.CheckAsync(_s.Version, Services.UpdatePlatform, cts.Token);
            if (info is null)
            {
                if (manual) Toast($"Đang dùng bản mới nhất ({_s.Version}).", ToastKind.Ok);
                return;
            }
            _update = info;
            ShowBanner($"Có bản mới {info.Version}", showButtons: true);
        }
        catch (Exception ex)
        {
            Log.Warn("Update check failed", ex);
            if (manual) Toast(ex is UpdateException ? ex.Message : "Không kiểm tra được bản mới. Hãy thử lại khi có mạng.", ToastKind.Warn);
        }
    }

    void ShowBanner(string text, bool showButtons, double? progress = null, bool error = false)
    {
        UpdateBanner.Visibility = Visibility.Visible;
        UpdateText.Text = text;
        UpdateText.Foreground = (Brush)FindResource(error ? "DangerBrush" : "TextBrush");
        UpdateButton.Visibility = showButtons ? Visibility.Visible : Visibility.Collapsed;
        UpdateLater.Visibility = showButtons ? Visibility.Visible : Visibility.Collapsed;
        UpdateProgress.Visibility = progress is null ? Visibility.Collapsed : Visibility.Visible;
        if (progress is { } p) UpdateProgress.Value = p;
    }

    internal void TestUpdateClick() => OnUpdateClick(this, new RoutedEventArgs());

    void OnUpdateLater(object sender, RoutedEventArgs e) => UpdateBanner.Visibility = Visibility.Collapsed;

    async void OnUpdateClick(object sender, RoutedEventArgs e)
    {
        if (_updating || _update is null) return;
        _updating = true;
        try
        {
            ShowBanner("Đang tải bản mới… 0%", showButtons: false, progress: 0);
            var info = _update;
            var path = await _s.Updater.DownloadAsync(info, AppPaths.Updates, f => Dispatcher.BeginInvoke(() =>
            {
                if (!_updating) return;
                UpdateText.Text = $"Đang tải bản mới… {(int)(f * 100)}%";
                UpdateProgress.Value = f;
            }), CancellationToken.None);
            ShowBanner("Đang cài bản mới, phần mềm sẽ tự mở lại…", showButtons: false, progress: 1);
            await RunInstallerAsync(path);
        }
        catch (Exception ex)
        {
            Log.Warn("Update failed", ex);
            ShowBanner(ex is UpdateException ? ex.Message : "Không cập nhật được. Hãy thử lại sau.", showButtons: true, error: true);
            UpdateButton.Content = "Thử lại";
        }
        finally
        {
            _updating = false;
        }
    }

    async Task RunInstallerAsync(string path)
    {
        if (_s.Demo)
        {
            await Task.Delay(800);
            try { System.IO.File.Delete(path); } catch (Exception) { }
            UpdateBanner.Visibility = Visibility.Collapsed;
            Toast("Bản thử: đã tải và kiểm tra xong bản mới, không cài đặt.", ToastKind.Ok);
            return;
        }
        // Start the installer first: if that fails (UAC cancelled, file missing) the app keeps running.
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path, "/SILENT /SUPPRESSMSGBOXES /NORESTART /UPDATE=1")
            {
                UseShellExecute = true,
            })?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("Installer did not start", ex);
            throw new UpdateException("Không chạy được bộ cài bản mới. Hãy thử lại.");
        }
        Application.Current.Shutdown();
    }

    // ---- Window bounds ----

    void ApplySavedBounds()
    {
        if (_s.Settings.Window is not { } b || b.Width < 400 || b.Height < 300) return;
        var vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var r = new Rect(b.Left, b.Top, b.Width, b.Height);
        if (!vs.IntersectsWith(r)) return;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = b.Left;
        Top = b.Top;
        Width = b.Width;
        Height = b.Height;
        if (b.Maximized) WindowState = WindowState.Maximized;
    }

    void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!CaptureMode.Active && SafeJobs().Any(j => j.Status is ExportStatus.Queued or ExportStatus.Downloading or ExportStatus.Converting))
        {
            var answer = MessageBox.Show(this,
                "Phần mềm đang lưu video. Nếu đóng bây giờ, video đang lưu sẽ bị hủy.\n\nVẫn đóng phần mềm?",
                "Camera", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }
        SaveBounds();
    }

    void SaveBounds()
    {
        if (!IsLoaded || CaptureMode.Active) return;
        var r = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        _s.Settings.Window = new WindowBounds(r.Left, r.Top, r.Width, r.Height, WindowState == WindowState.Maximized);
        _s.SaveSettings();
    }

    void Shutdown()
    {
        _updateTimer.Stop();
        _current?.Deactivate();
        _ram.Dispose();
    }
}
