using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Cv.App.Logic;
using Cv.Core;
using Cv.Core.Export;
using Cv.Core.Nvr;

namespace Cv.App.Playback;

/// <summary>
/// "Trích xuất video" beside the playback video: three numbered steps (from, to, save), prefilled around the moment on screen.
/// The range is drawn on the timeline, whose handles can be dragged too; the video keeps playing next to it.
/// </summary>
public partial class SavePanel : UserControl
{
    IShell _shell = null!;
    Func<DateTime?> _position = () => null;
    string _nvrId = "";
    ChannelInfo? _camera;
    DateTime _day;
    bool _setting;

    public SavePanel()
    {
        InitializeComponent();
        Visibility = Visibility.Collapsed;
    }

    /// <summary>A valid range typed or picked in the panel (for the timeline).</summary>
    public event Action<DateTime, DateTime>? RangeEdited;

    /// <summary>The panel closed (saved or not).</summary>
    public event Action? Closed;

    public bool IsOpen => Visibility == Visibility.Visible;

    /// <param name="position">The moment on screen, null when nothing plays.</param>
    public void Init(IShell shell, Func<DateTime?> position)
    {
        _shell = shell;
        _position = position;
    }

    public void Open(string nvrId, ChannelInfo camera, DateTime day, DateTime from, DateTime to)
    {
        _nvrId = nvrId;
        _day = day.Date;
        SetCamera(camera);
        var folder = _shell.Services.Settings.ExportFolder;
        FolderText.Text = "Lưu vào " + folder;
        FolderText.ToolTip = folder + Environment.NewLine + "Đổi thư mục trong Cài đặt.";
        ErrorBox.Visibility = Visibility.Collapsed;
        SetRange(from, to);
        Visibility = Visibility.Visible;
    }

    /// <summary>Another camera picked while open: same times, that camera.</summary>
    public void SetCamera(ChannelInfo camera)
    {
        _camera = camera;
        CameraText.Text = $"{camera.Name}  ·  {TimeFormat.LongDate(_day)}";
    }

    /// <summary>Times from outside (opening, timeline handles): shown without echoing back.</summary>
    public void SetRange(DateTime from, DateTime to)
    {
        _setting = true;
        FromBox.Text = TimeFormat.Clock(from);
        ToBox.Text = TimeFormat.Clock(to);
        _setting = false;
        Refresh(raise: false);
    }

    public void FocusSave() => SaveButton.Focus();

    public void Close()
    {
        if (!IsOpen) return;
        Visibility = Visibility.Collapsed;
        Closed?.Invoke();
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
        if (_setting || LengthText is null) return;
        ErrorBox.Visibility = Visibility.Collapsed;
        Refresh(raise: true);
    }

    void Refresh(bool raise)
    {
        var t = Times();
        if (t is null)
        {
            Say("Gõ giờ như 14:30 hoặc 14:30:15.", warn: true);
            return;
        }
        var (from, to) = t.Value;
        if (to <= from)
        {
            Say("Giờ ở bước 2 phải sau giờ ở bước 1.", warn: true);
            return;
        }
        Say($"Đoạn dài {TimeFormat.Duration(to - from)} (màu cam trên thanh thời gian).", warn: false);
        if (raise) RangeEdited?.Invoke(from, to);
    }

    void Say(string text, bool warn)
    {
        LengthText.Text = text;
        LengthText.Foreground = (Brush)FindResource(warn ? "WarnBrush" : "SubtleBrush");
    }

    void OnFromHere(object sender, RoutedEventArgs e) => TakePosition(FromBox);
    void OnToHere(object sender, RoutedEventArgs e) => TakePosition(ToBox);

    void TakePosition(TextBox box)
    {
        if (_position() is not { } p || p.Date != _day) return;
        box.Text = TimeFormat.Clock(p); // TextChanged redraws the range
    }

    void OnClose(object sender, RoutedEventArgs e) => Close();

    void OnSave(object sender, RoutedEventArgs e)
    {
        var t = Times();
        if (t is null || _camera is not { } cam)
        {
            ShowError("Giờ chưa đúng. Hãy gõ giờ như 14:30 hoặc 14:30:15.");
            return;
        }
        try
        {
            _shell.Services.Export.Enqueue(new ExportRequest(_nvrId, cam.No, cam.Name, t.Value.From, t.Value.To,
                _shell.Services.Settings.ExportFolder));
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
            ShowError("Chưa trích xuất được đoạn video này. Hãy thử lại.");
            return;
        }
        Close();
        _shell.Toast("Đang trích xuất video… Xem ở mục Video đã lưu.", ToastKind.Info, "Xem", _shell.ShowSaved);
    }

    void ShowError(string text)
    {
        ErrorText.Text = text;
        ErrorBox.Visibility = Visibility.Visible;
    }

    // ---- Self-test hooks ----

    internal (DateTime From, DateTime To)? TestRange => Times();
    internal void TestFromHere() => OnFromHere(this, new RoutedEventArgs());
    internal void TestToHere() => OnToHere(this, new RoutedEventArgs());
    internal void TestSave() => OnSave(this, new RoutedEventArgs());
}
