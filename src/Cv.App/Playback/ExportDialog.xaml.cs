using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Cv.App.Logic;
using Cv.Core;
using Cv.Core.Export;
using Cv.Core.Nvr;

namespace Cv.App.Playback;

/// <summary>"Lưu đoạn video": prefilled from the marks (or playhead +5 min), editable times, quick lengths, folder.</summary>
public partial class ExportDialog : UserControl
{
    readonly IShell _shell;
    readonly string _nvrId;
    readonly ChannelInfo _camera;
    readonly DateTime _day;
    string _folder;

    public ExportDialog(IShell shell, string nvrId, ChannelInfo camera, DateTime from, DateTime to)
    {
        InitializeComponent();
        _shell = shell;
        _nvrId = nvrId;
        _camera = camera;
        _day = from.Date;
        _folder = shell.Services.Settings.ExportFolder;
        CameraText.Text = camera.Name;
        DayText.Text = TimeFormat.LongDate(_day);
        FromBox.Text = TimeFormat.Clock(from);
        ToBox.Text = TimeFormat.Clock(to);
        FolderText.Text = _folder;
        FolderText.ToolTip = _folder;
        Loaded += (_, _) => { FromBox.Focus(); FromBox.CaretIndex = FromBox.Text.Length; };
    }

    (DateTime From, DateTime To)? Times()
    {
        var a = TimeFormat.ParseTimeOfDay(FromBox.Text);
        var b = TimeFormat.ParseTimeOfDay(ToBox.Text);
        if (a is null || b is null) return null;
        return (_day + a.Value, _day + b.Value);
    }

    void OnTimesChanged(object sender, TextChangedEventArgs e)
    {
        if (LengthText is null) return;
        ErrorBox.Visibility = Visibility.Collapsed;
        var t = Times();
        if (t is null)
        {
            LengthText.Text = "Gõ giờ theo dạng giờ:phút:giây, ví dụ 14:30:00";
            return;
        }
        var len = t.Value.To - t.Value.From;
        LengthText.Text = len > TimeSpan.Zero ? "Độ dài: " + TimeFormat.Duration(len) : "Giờ kết thúc phải sau giờ bắt đầu";
        foreach (RadioButton b in QuickButtons.Children)
            b.IsChecked = Math.Abs(len.TotalMinutes - int.Parse((string)b.Tag, CultureInfo.InvariantCulture)) < 0.001;
    }

    void OnQuick(object sender, RoutedEventArgs e)
    {
        var minutes = int.Parse((string)((RadioButton)sender).Tag, CultureInfo.InvariantCulture);
        var from = TimeFormat.ParseTimeOfDay(FromBox.Text);
        if (from is null)
        {
            ShowError("Hãy gõ giờ bắt đầu trước, ví dụ 14:30:00.");
            return;
        }
        var to = from.Value + TimeSpan.FromMinutes(minutes);
        if (to >= TimeSpan.FromDays(1)) to = new TimeSpan(23, 59, 59);
        ToBox.Text = $"{to.Hours:00}:{to.Minutes:00}:{to.Seconds:00}";
    }

    void OnChangeFolder(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Chọn thư mục lưu video", InitialDirectory = _folder };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        _folder = dlg.FolderName;
        FolderText.Text = _folder;
        FolderText.ToolTip = _folder;
        var s = _shell.Services;
        s.Settings.ExportFolder = _folder;
        s.SaveSettings();
    }

    void OnCancel(object sender, RoutedEventArgs e) => _shell.CloseModal();

    internal void TestSave() => OnSave(this, new RoutedEventArgs());

    void OnSave(object sender, RoutedEventArgs e)
    {
        var t = Times();
        if (t is null)
        {
            ShowError("Giờ chưa đúng. Hãy gõ theo dạng giờ:phút:giây, ví dụ 14:30:00.");
            return;
        }
        try
        {
            _shell.Services.Export.Enqueue(new ExportRequest(_nvrId, _camera.No, _camera.Name, t.Value.From, t.Value.To, _folder));
        }
        catch (ArgumentException ex)
        {
            // The service's message is for the user; drop .NET's " (Parameter 'x')" suffix.
            ShowError(ex.ParamName is null ? ex.Message : ex.Message.Replace($" (Parameter '{ex.ParamName}')", ""));
            return;
        }
        catch (Exception ex)
        {
            Log.Error("Export not queued", ex);
            ShowError("Chưa lưu được đoạn video này. Hãy thử lại.");
            return;
        }
        _shell.CloseModal();
        _shell.Toast("Đang lưu video… Xem ở mục Video đã lưu.", ToastKind.Info, "Xem", _shell.ShowSaved);
    }

    void ShowError(string text)
    {
        ErrorText.Text = text;
        ErrorBox.Visibility = Visibility.Visible;
    }
}
