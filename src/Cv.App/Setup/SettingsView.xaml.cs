using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Cv.App.Platform;
using Cv.Core;
using Cv.Core.Nvr;
using Cv.Core.Video;

namespace Cv.App.Setup;

/// <summary>Cài đặt (IT only): recorder, folders, decoding, minimize behaviour, updates, diagnostics, version.</summary>
public partial class SettingsView : UserControl, IPage
{
    IShell _shell = null!;
    Services _s = null!;
    bool _busy;

    public SettingsView() => InitializeComponent();

    public void Init(IShell shell)
    {
        _shell = shell;
        _s = shell.Services;
    }

    /// <summary>Capture mode: show the first-run header even with a recorder configured.</summary>
    internal bool ForceFirstRun { get; set; }

    public void Activate() => Fill();

    public void Deactivate() { }

    void Fill()
    {
        var cfg = _s.NvrConfig;
        FirstRunBanner.Visibility = !_s.IsConfigured || ForceFirstRun ? Visibility.Visible : Visibility.Collapsed;
        NameBox.Text = cfg?.Name ?? "Đầu ghi";
        HostBox.Text = cfg?.Host ?? "";
        HttpPortBox.Text = (cfg?.HttpPort ?? 80).ToString(CultureInfo.InvariantCulture);
        RtspPortBox.Text = (cfg?.RtspPort ?? 554).ToString(CultureInfo.InvariantCulture);
        UserBox.Text = cfg?.User ?? "admin";
        PasswordBox.Clear();
        PasswordHint.Visibility = HasStoredPassword() ? Visibility.Visible : Visibility.Collapsed;
        ExportFolderBox.Text = _s.Settings.ExportFolder;
        SnapshotFolderBox.Text = _s.Settings.SnapshotFolder;
        HwAuto.IsChecked = _s.Settings.HwDecode == HwDecodeMode.Auto;
        HwOff.IsChecked = _s.Settings.HwDecode == HwDecodeMode.Off;
        PauseMinimizedBox.IsChecked = _s.Settings.PauseWhenMinimized;
        CheckUpdatesBox.IsChecked = _s.Settings.CheckUpdates;
        VersionText.Text = "Phiên bản " + _s.Version + (_s.Demo ? " (bản thử)" : "");
        ShowResult(null, "", "TextBrush");
    }

    bool HasStoredPassword()
    {
        if (_s.NvrConfig is not { } cfg) return false;
        try { return !string.IsNullOrEmpty(_s.Store.GetPassword(cfg.Id)); }
        catch (Exception) { return false; }
    }

    void OnPasswordChanged(object sender, RoutedEventArgs e) =>
        PasswordHint.Visibility = PasswordBox.SecurePassword.Length == 0 && HasStoredPassword() ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The recorder as typed; the stored password when the box is left empty.</summary>
    (NvrConfig Config, string Password)? ReadForm()
    {
        var host = HostBox.Text.Trim();
        if (host.Length == 0) return Fail("Hãy nhập địa chỉ IP của đầu ghi.");
        if (!Port(HttpPortBox.Text, 80, out var http)) return Fail("Cổng HTTP phải là số từ 1 đến 65535.");
        if (!Port(RtspPortBox.Text, 554, out var rtsp)) return Fail("Cổng RTSP phải là số từ 1 đến 65535.");
        var user = UserBox.Text.Trim();
        if (user.Length == 0) return Fail("Hãy nhập tài khoản đầu ghi.");
        var existing = _s.NvrConfig;
        var password = PasswordBox.Password;
        if (password.Length == 0 && existing is not null)
        {
            try { password = _s.Store.GetPassword(existing.Id) ?? ""; } catch (Exception) { password = ""; }
        }
        var name = NameBox.Text.Trim();
        var cfg = (existing ?? new NvrConfig()) with
        {
            Name = name.Length > 0 ? name : "Đầu ghi", Host = host, HttpPort = http, RtspPort = rtsp, User = user,
        };
        return (cfg, password);
    }

    (NvrConfig, string)? Fail(string message)
    {
        ShowResult(Glyphs.Warning, message, "WarnBrush");
        return null;
    }

    static bool Port(string text, int fallback, out int port)
    {
        text = text.Trim();
        if (text.Length == 0) { port = fallback; return true; }
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is >= 1 and <= 65535;
    }

    void ShowResult(string? glyph, string text, string brush)
    {
        TestIcon.Text = glyph ?? "";
        TestIcon.Visibility = glyph is null ? Visibility.Collapsed : Visibility.Visible;
        TestIcon.Foreground = (Brush)FindResource(brush);
        TestText.Text = text;
        TestText.Foreground = (Brush)FindResource(brush);
    }

    async void OnTest(object sender, RoutedEventArgs e)
    {
        if (_busy || ReadForm() is not { } form) return;
        _busy = true;
        TestButton.IsEnabled = SaveButton.IsEnabled = false;
        ShowResult(Glyphs.Sync, "Đang kiểm tra…", "MutedBrush");
        try
        {
            using var client = _s.NvrFactory.Create(form.Config, form.Password);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var info = await client.GetDeviceInfoAsync(cts.Token);
            var channels = await client.GetChannelsAsync(cts.Token);
            ShowResult(Glyphs.Completed, $"Đã kết nối: {info.Model} · {channels.Count} camera", "OkBrush");
        }
        catch (NvrException ex)
        {
            ShowResult(Glyphs.Error, ex.Message, "DangerBrush");
        }
        catch (OperationCanceledException)
        {
            ShowResult(Glyphs.Error, "Đầu ghi không trả lời. Hãy kiểm tra địa chỉ IP và cổng.", "DangerBrush");
        }
        catch (NotImplementedException)
        {
            ShowResult(Glyphs.Error, "Bản này chưa có phần kết nối đầu ghi.", "DangerBrush");
        }
        catch (Exception ex)
        {
            Log.Warn("Connection test failed", ex);
            ShowResult(Glyphs.Error, "Không kết nối được đầu ghi. Hãy kiểm tra địa chỉ IP và cổng.", "DangerBrush");
        }
        finally
        {
            _busy = false;
            TestButton.IsEnabled = SaveButton.IsEnabled = true;
        }
    }

    void OnSave(object sender, RoutedEventArgs e)
    {
        if (_busy || ReadForm() is not { } form) return;
        var firstRun = !_s.IsConfigured;
        if (_s.Settings.Nvrs.Count == 0) _s.Settings.Nvrs.Add(form.Config);
        else _s.Settings.Nvrs[0] = form.Config;
        try
        {
            // Save first so the recorder exists in the file, then the password (SetPassword saves at once).
            _s.Store.Save(_s.Settings);
            if (PasswordBox.Password.Length > 0) _s.Store.SetPassword(form.Config.Id, PasswordBox.Password);
        }
        catch (Exception ex)
        {
            Log.Error("Recorder settings not saved", ex);
            ShowResult(Glyphs.Error, "Không lưu được cài đặt. Hãy thử lại.", "DangerBrush");
            return;
        }
        _s.ReloadNvr();
        PasswordBox.Clear();
        PasswordHint.Visibility = HasStoredPassword() ? Visibility.Visible : Visibility.Collapsed;
        FirstRunBanner.Visibility = Visibility.Collapsed;
        ShowResult(Glyphs.Completed, "Đã lưu.", "OkBrush");
        if (firstRun)
        {
            _shell.Toast("Đã lưu đầu ghi. Đang mở camera…", ToastKind.Ok);
            _shell.ShowLive();
        }
    }

    string? PickFolder(string title, string current)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = title, InitialDirectory = current };
        return dlg.ShowDialog(Window.GetWindow(this)) == true ? dlg.FolderName : null;
    }

    void OnChangeExportFolder(object sender, RoutedEventArgs e)
    {
        if (PickFolder("Chọn thư mục lưu video", _s.Settings.ExportFolder) is not { } f) return;
        _s.Settings.ExportFolder = f;
        ExportFolderBox.Text = f;
        _s.SaveSettings();
    }

    void OnChangeSnapshotFolder(object sender, RoutedEventArgs e)
    {
        if (PickFolder("Chọn thư mục lưu ảnh", _s.Settings.SnapshotFolder) is not { } f) return;
        _s.Settings.SnapshotFolder = f;
        SnapshotFolderBox.Text = f;
        _s.SaveSettings();
    }

    void OnHwChanged(object sender, RoutedEventArgs e)
    {
        _s.Settings.HwDecode = HwOff.IsChecked == true ? HwDecodeMode.Off : HwDecodeMode.Auto;
        _s.SaveSettings();
    }

    void OnPauseMinimized(object sender, RoutedEventArgs e)
    {
        _s.Settings.PauseWhenMinimized = PauseMinimizedBox.IsChecked == true;
        _s.SaveSettings();
    }

    void OnCheckUpdatesChanged(object sender, RoutedEventArgs e)
    {
        _s.Settings.CheckUpdates = CheckUpdatesBox.IsChecked == true;
        _s.SaveSettings();
    }

    async void OnCheckNow(object sender, RoutedEventArgs e)
    {
        CheckNowButton.IsEnabled = false;
        try { await _shell.CheckUpdateAsync(manual: true); }
        finally { CheckNowButton.IsEnabled = true; }
    }

    async void OnProbe(object sender, RoutedEventArgs e)
    {
        if (_s.NvrConfig is not { } cfg || !_s.IsConfigured)
        {
            ProbeText.Text = "Hãy nhập và lưu thông tin đầu ghi trước.";
            return;
        }
        string password;
        try { password = _s.Store.GetPassword(cfg.Id) ?? ""; } catch (Exception) { password = ""; }
        ProbeButton.IsEnabled = false;
        ProbeButton.Content = "Đang chẩn đoán…";
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var progress = new Progress<string>(line => ProbeText.Text = line);
        try
        {
            var report = await Task.Run(() => _s.Probe.RunAsync(cfg, password, desktop, progress, CancellationToken.None));
            ProbeText.Text = report.Passed
                ? "Mọi mục đều đạt. Báo cáo đã lưu ra Màn hình."
                : "Có mục chưa đạt. Báo cáo đã lưu ra Màn hình, hãy gửi cho bộ phận IT.";
            if (report.SavedPath is { } path && Shell.OpenFile(path) is { } err) _shell.Toast(err, ToastKind.Warn);
        }
        catch (NotImplementedException)
        {
            ProbeText.Text = "Bản này chưa có phần chẩn đoán.";
        }
        catch (Exception ex)
        {
            Log.Warn("Probe failed", ex);
            ProbeText.Text = "Chẩn đoán bị dừng giữa chừng. Hãy thử lại.";
        }
        finally
        {
            ProbeButton.IsEnabled = true;
            ProbeButton.Content = "Chạy chẩn đoán";
        }
    }
}
