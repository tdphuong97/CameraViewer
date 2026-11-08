using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cv.App.Logic;
using Cv.Core;
using Cv.Core.Nvr;

namespace Cv.App.Playback;

/// <summary>
/// "Có người" beside the playback video: the times the camera saw a person that day (the recorder's own human
/// detection), each with the picture the recorder took. Click one to play that moment. Pictures load only for the
/// cards on screen, at thumbnail size, and only the last few dozen stay in memory (RAM rule).
/// </summary>
public partial class PeoplePanel : UserControl
{
    const int MaxThumbs = 40;
    const int ThumbWidth = 320;

    readonly SemaphoreSlim _fetch = new(2); // the recorder also streams video: a couple of pictures at a time
    readonly LinkedList<VisitItem> _withThumb = new();
    Func<string, CancellationToken, Task<byte[]>>? _getPicture;
    CancellationTokenSource _cts = new();
    bool _selecting;
    bool _pictureFailed;

    public PeoplePanel()
    {
        InitializeComponent();
        Visibility = Visibility.Collapsed;
    }

    /// <summary>The user picked a visit: play it.</summary>
    public event Action<PeopleVisit>? Picked;

    public event Action? Closed;

    /// <summary>Thử lại after an error.</summary>
    public event Action? RetryRequested;

    public bool IsOpen => Visibility == Visibility.Visible;

    public void Open() => Visibility = Visibility.Visible;

    public void Close()
    {
        if (!IsOpen) return;
        Visibility = Visibility.Collapsed;
        Closed?.Invoke();
    }

    public void ShowLoading(string camera, DateTime day)
    {
        Reset(camera, day, null);
        ShowState(Glyphs.Sync, "Đang tìm những lúc có người…", "", retry: false);
    }

    /// <param name="around">The moment on screen: the list opens at the visit just before it.</param>
    public void ShowVisits(string camera, DateTime day, IReadOnlyList<PeopleVisit> visits, Func<string, CancellationToken, Task<byte[]>> getPicture,
        DateTime? around = null)
    {
        Reset(camera, day, visits.Count);
        _getPicture = getPicture;
        if (visits.Count == 0)
        {
            ShowState(Glyphs.Info, "Không thấy người nào trong ngày này.",
                "Nếu camera có người qua lại mà vẫn không thấy, nhờ bộ phận IT bật phát hiện người (người, xe) trên camera.", retry: false);
            return;
        }
        StateBox.Visibility = Visibility.Collapsed;
        HintText.Visibility = Visibility.Visible;
        var items = visits.Select(v => new VisitItem(v)).ToList();
        List.ItemsSource = items;
        if (around is { } t && items.LastOrDefault(i => i.Visit.Start <= t) is { } near)
            Dispatcher.BeginInvoke(() => List.ScrollIntoView(near), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    public void ShowUnsupported(string camera, DateTime day)
    {
        Reset(camera, day, null);
        ShowState(Glyphs.Info, "Đầu ghi này không cho tìm những lúc có người.", "Vẫn xem lại và lưu video bình thường.", retry: false);
    }

    public void ShowError(string camera, DateTime day, string message)
    {
        Reset(camera, day, null);
        ShowState(Glyphs.Warning, message, "", retry: true);
    }

    /// <summary>Highlights the visit being played (no Picked event).</summary>
    public void Select(PeopleVisit? visit)
    {
        if (List.ItemsSource is not IEnumerable<VisitItem> items) return;
        _selecting = true;
        List.SelectedItem = visit is null ? null : items.FirstOrDefault(i => i.Visit == visit);
        if (List.SelectedItem is { } s) List.ScrollIntoView(s);
        _selecting = false;
    }

    void Reset(string camera, DateTime day, int? count)
    {
        _cts.Cancel();
        _cts = new CancellationTokenSource();
        _withThumb.Clear();
        _pictureFailed = false;
        List.ItemsSource = null; // drops every thumbnail with the old items
        CameraText.Text = $"{camera}  ·  {TimeFormat.LongDate(day)}" + (count is { } n ? $"  ·  {n} lượt" : "");
    }

    void ShowState(string glyph, string text, string hint, bool retry)
    {
        HintText.Visibility = Visibility.Collapsed;
        StateIcon.Text = glyph;
        StateText.Text = text;
        StateHint.Text = hint;
        StateHint.Visibility = string.IsNullOrEmpty(hint) ? Visibility.Collapsed : Visibility.Visible;
        RetryButton.Visibility = retry ? Visibility.Visible : Visibility.Collapsed;
        StateBox.Visibility = Visibility.Visible;
    }

    void OnPick(object sender, SelectionChangedEventArgs e)
    {
        if (_selecting || List.SelectedItem is not VisitItem item) return;
        Picked?.Invoke(item.Visit);
    }

    void OnClose(object sender, RoutedEventArgs e) => Close();
    void OnRetry(object sender, RoutedEventArgs e) => RetryRequested?.Invoke();

    // ---- Thumbnails: fetched when a card shows, decoded small ----

    void OnThumbShown(object sender, RoutedEventArgs e) => EnsureThumb((sender as FrameworkElement)?.DataContext as VisitItem);
    void OnThumbContext(object sender, DependencyPropertyChangedEventArgs e) => EnsureThumb(e.NewValue as VisitItem);

    void EnsureThumb(VisitItem? item)
    {
        if (item is null || item.Thumb is not null || item.Loading || item.Visit.PictureUri is not { } uri || _getPicture is not { } get) return;
        _ = LoadThumbAsync(item, uri, get, _cts.Token);
    }

    async Task LoadThumbAsync(VisitItem item, string uri, Func<string, CancellationToken, Task<byte[]>> get, CancellationToken ct)
    {
        item.Loading = true;
        try
        {
            await _fetch.WaitAsync(ct);
            byte[] bytes;
            try { bytes = await get(uri, ct); }
            finally { _fetch.Release(); }
            var bitmap = await Task.Run(() => Decode(bytes), ct);
            if (ct.IsCancellationRequested) return;
            item.Thumb = bitmap;
            _withThumb.AddLast(item);
            while (_withThumb.Count > MaxThumbs)
            {
                var old = _withThumb.First!.Value;
                _withThumb.RemoveFirst();
                old.Thumb = null; // reloads if scrolled back to
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_pictureFailed) Log.Warn("People picture not loaded: " + uri, ex);
            _pictureFailed = true;
        }
        finally
        {
            item.Loading = false;
        }
    }

    static BitmapSource Decode(byte[] data)
    {
        var b = new BitmapImage();
        b.BeginInit();
        b.CacheOption = BitmapCacheOption.OnLoad;
        b.DecodePixelWidth = ThumbWidth;
        b.StreamSource = new MemoryStream(data);
        b.EndInit();
        b.Freeze();
        return b;
    }

    // ---- Self-test hooks ----

    internal IReadOnlyList<PeopleVisit> TestVisits => (List.ItemsSource as IEnumerable<VisitItem>)?.Select(i => i.Visit).ToList() ?? [];
    internal string TestState => StateBox.Visibility == Visibility.Visible ? StateText.Text : "";
    internal int TestThumbs => _withThumb.Count;
    internal void TestPick(int index)
    {
        if (List.ItemsSource is IList<VisitItem> items && index < items.Count) List.SelectedItem = items[index];
    }
}

/// <summary>One card of the list.</summary>
public sealed class VisitItem(PeopleVisit visit) : INotifyPropertyChanged
{
    ImageSource? _thumb;

    public PeopleVisit Visit { get; } = visit;
    public string Clock => TimeFormat.Clock(Visit.Start);

    /// <summary>How long the person stayed in view, when it is more than a moment.</summary>
    public string Length => Visit.End - Visit.Start >= TimeSpan.FromSeconds(5) ? TimeFormat.Duration(Visit.End - Visit.Start) : "";

    public ImageSource? Thumb
    {
        get => _thumb;
        set
        {
            _thumb = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumb)));
        }
    }

    internal bool Loading { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;
}
