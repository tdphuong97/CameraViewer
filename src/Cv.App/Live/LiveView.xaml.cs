using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Cv.App.Logic;
using Cv.Core;
using Cv.Core.Nvr;

namespace Cv.App.Live;

/// <summary>
/// Trực tiếp: every camera in an automatic grid (sub streams), one camera big (main stream). Only tiles on screen
/// have a source; leaving the screen closes them all.
/// </summary>
public partial class LiveView : UserControl, IPage
{
    readonly List<CameraTile> _tiles = [];
    IShell _shell = null!;
    Services _s = null!;
    List<ChannelInfo> _ordered = [];
    int _size = 1;
    int _page;
    bool _active;
    bool _loaded;
    int _loadVersion;
    ChannelInfo? _single;
    int? _pendingSingle;

    public LiveView()
    {
        InitializeComponent();
        SingleTile.Clicked += _ => ExitSingle();
        foreach (var size in GridLayout.PickerSizes)
        {
            var b = new RadioButton
            {
                Style = (Style)FindResource("SegmentButton"),
                Content = size == 0 ? "Tự động" : size.ToString(CultureInfo.InvariantCulture),
                Tag = size,
                GroupName = "layout",
                ToolTip = size == 0 ? "Tự chọn số ô vừa đủ mọi camera" : $"{size} ô",
            };
            if (size != 0) b.MinWidth = 42;
            b.Click += OnLayoutClick;
            LayoutButtons.Children.Add(b);
        }
    }

    public void Init(IShell shell)
    {
        _shell = shell;
        _s = shell.Services;
        _s.NvrChanged += () =>
        {
            _loaded = false;
            CloseAll();
            _ordered = [];
            if (_active) _ = LoadAsync();
        };
    }

    public void Activate()
    {
        _active = true;
        if (!_loaded) _ = LoadAsync();
        else
        {
            ShowCurrent();
            if (_s.ChannelsStale) _ = RefreshQuietlyAsync();
        }
    }

    public void Deactivate()
    {
        _active = false;
        CloseAll();
    }

    public bool HandleEscape()
    {
        if (_single is null) return false;
        ExitSingle();
        return true;
    }

    // ---- Self-test hooks (demo --selftest drives the same paths a click would) ----

    internal IReadOnlyList<ChannelInfo> TestOrder => _ordered;
    internal bool InSingle => _single is not null;
    internal bool TestInSingle => InSingle;
    internal bool TestSingleHasFrame => SingleTile.Video.HasFrame;
    internal Cv.Core.Video.IVideoSource? TestSingleSource => SingleTile.Source;
    internal void TestEnterSingle(int slot) { if (_tiles[slot].Camera is { } c) EnterSingle(c); }
    internal void TestLayout(int size) { _s.Settings.LiveGrid = size; _page = 0; Rebuild(); }
    internal void TestNextPage() => OnNextPage(this, new RoutedEventArgs());
    internal void TestSwap(int a, int b) => Swap(a, b);
    internal int TestPageCount => GridLayout.PageCount(_ordered.Count, _size);

    /// <summary>Capture mode: the hover buttons of the first tile.</summary>
    internal void ShowHoverOnFirstTile()
    {
        if (_tiles.Count > 0) _tiles[0].ShowHover();
    }

    /// <summary>Capture mode: opens single view of a channel as soon as the list is loaded.</summary>
    internal void ShowSingleWhenReady(int channel)
    {
        _pendingSingle = channel;
        if (_loaded) ApplyPendingSingle();
    }

    async Task LoadAsync()
    {
        var version = ++_loadVersion;
        if (!_s.IsConfigured)
        {
            ShowMessage(Glyphs.Settings, "Chưa kết nối đầu ghi", "Bộ phận IT cần nhập thông tin đầu ghi trong Cài đặt.", buttons: true);
            return;
        }
        ShowMessage(Glyphs.Sync, "Đang tải danh sách camera…", "", buttons: false);
        try
        {
            var channels = await _s.GetChannelsAsync();
            if (version != _loadVersion) return;
            _ordered = GridLayout.Order(channels, _s.Settings.LiveOrder, _s.NvrConfig!.Id);
            _loaded = true;
            MessagePanel.Visibility = Visibility.Collapsed;
            if (_ordered.Count == 0)
            {
                ShowMessage(Glyphs.Camera, "Đầu ghi chưa có camera nào", "Hãy kiểm tra lại đầu ghi hoặc nhờ bộ phận IT.", buttons: true);
                return;
            }
            ApplyPendingSingle();
            if (_active) ShowCurrent();
        }
        catch (Exception ex)
        {
            if (version != _loadVersion) return;
            Log.Warn("Channel list failed", ex);
            var text = ex is NvrException ? ex.Message : "Hãy kiểm tra đầu ghi đã bật và máy tính có mạng, rồi bấm Thử lại.";
            ShowMessage(Glyphs.Warning, "Không kết nối được đầu ghi", text, buttons: true);
        }
    }

    void ApplyPendingSingle()
    {
        if (_pendingSingle is not { } ch) return;
        _pendingSingle = null;
        var cam = _ordered.FirstOrDefault(c => c.No == ch && c.Online) ?? _ordered.FirstOrDefault(c => c.Online);
        if (cam is not null) _single = cam;
    }

    void ShowMessage(string glyph, string title, string text, bool buttons)
    {
        CloseAll();
        TileGrid.Visibility = Visibility.Collapsed;
        SinglePanel.Visibility = Visibility.Collapsed;
        BottomBar.Visibility = Visibility.Collapsed;
        MessageIcon.Text = glyph;
        MessageTitle.Text = title;
        MessageText.Text = text;
        MessageText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        MessageButtons.Visibility = buttons ? Visibility.Visible : Visibility.Collapsed;
        MessagePanel.Visibility = Visibility.Visible;
    }

    void ShowCurrent()
    {
        if (!_loaded || _ordered.Count == 0) return;
        if (_single is not null) EnterSingle(_single);
        else ShowGrid();
    }

    void ShowGrid()
    {
        SingleTile.Close();
        SinglePanel.Visibility = Visibility.Collapsed;
        TileGrid.Visibility = Visibility.Visible;
        BottomBar.Visibility = Visibility.Visible;
        Rebuild();
    }

    /// <summary>Lays out the current page and opens the streams of the tiles on it.</summary>
    void Rebuild()
    {
        var count = _ordered.Count;
        _size = GridLayout.Resolve(_s.Settings.LiveGrid, count);
        var (cols, rows) = GridLayout.Dimensions(_size);
        var pages = GridLayout.PageCount(count, _size);
        _page = Math.Clamp(_page, 0, pages - 1);
        var kind = GridLayout.StreamFor(_size);

        if (TileGrid.ColumnDefinitions.Count != cols || TileGrid.RowDefinitions.Count != rows)
        {
            TileGrid.ColumnDefinitions.Clear();
            TileGrid.RowDefinitions.Clear();
            for (var c = 0; c < cols; c++) TileGrid.ColumnDefinitions.Add(new ColumnDefinition());
            for (var r = 0; r < rows; r++) TileGrid.RowDefinitions.Add(new RowDefinition());
        }

        while (_tiles.Count < _size) _tiles.Add(NewTile());
        for (var i = _tiles.Count - 1; i >= _size; i--)
        {
            _tiles[i].Close();
            _tiles[i].Bind(null, kind);
            TileGrid.Children.Remove(_tiles[i]);
        }

        for (var slot = 0; slot < _size; slot++)
        {
            var tile = _tiles[slot];
            var index = _page * _size + slot;
            tile.Slot = slot;
            tile.Bind(index < count ? _ordered[index] : null, kind);
            Grid.SetColumn(tile, slot % cols);
            Grid.SetRow(tile, slot / cols);
            if (!TileGrid.Children.Contains(tile)) TileGrid.Children.Add(tile);
        }
        // Size the tiles before opening, so sources start at the right scale.
        TileGrid.UpdateLayout();
        if (_active)
            for (var slot = 0; slot < _size; slot++) _tiles[slot].Open(_s);

        var offline = _ordered.Count(c => !c.Online);
        CountText.Text = offline > 0 ? $"{count} camera · {offline} đang tắt" : $"{count} camera";
        foreach (RadioButton b in LayoutButtons.Children) b.IsChecked = (int)b.Tag == _s.Settings.LiveGrid;
        Pager.Visibility = pages > 1 ? Visibility.Visible : Visibility.Collapsed;
        PageText.Text = $"Trang {_page + 1}/{pages}";
        PrevPage.IsEnabled = _page > 0;
        NextPage.IsEnabled = _page < pages - 1;
    }

    CameraTile NewTile()
    {
        var t = new CameraTile { Margin = new Thickness(2) };
        t.Clicked += tile => { if (tile.Camera is { } c) EnterSingle(c); };
        t.PlaybackRequested += tile => { if (tile.Camera is { } c) _shell.ShowPlayback(c, _s.DeviceNow.AddMinutes(-5)); };
        t.SnapshotRequested += tile => { if (tile.Camera is { } c) _ = _shell.SnapshotAsync(tile.Source, c.Name, DateTime.Now); };
        t.SwapRequested += Swap;
        return t;
    }

    void CloseAll()
    {
        foreach (var t in _tiles) t.Close();
        SingleTile.Close();
    }

    void EnterSingle(ChannelInfo cam)
    {
        _single = cam;
        foreach (var t in _tiles) t.Close();
        TileGrid.Visibility = Visibility.Collapsed;
        BottomBar.Visibility = Visibility.Collapsed;
        MessagePanel.Visibility = Visibility.Collapsed;
        SinglePanel.Visibility = Visibility.Visible;
        SingleName.Text = cam.Name;
        SingleSnapshot.IsEnabled = cam.Online;
        SingleTile.Bind(cam, StreamKind.Main);
        SinglePanel.UpdateLayout();
        if (_active) SingleTile.Open(_s);
    }

    void ExitSingle()
    {
        _single = null;
        if (_active) ShowGrid();
    }

    void Swap(int fromSlot, int toSlot)
    {
        var a = _page * _size + fromSlot;
        var b = _page * _size + toSlot;
        if (a >= _ordered.Count || b >= _ordered.Count) return;
        _s.Settings.LiveOrder = GridLayout.Swap(_ordered, a, b, _s.NvrConfig!.Id);
        _s.SaveSettings();
        // Swap the tiles themselves so both streams keep running.
        (_tiles[fromSlot], _tiles[toSlot]) = (_tiles[toSlot], _tiles[fromSlot]);
        Rebuild();
    }

    void OnLayoutClick(object sender, RoutedEventArgs e)
    {
        _s.Settings.LiveGrid = (int)((RadioButton)sender).Tag;
        _s.SaveSettings();
        _page = 0;
        Rebuild();
    }

    void OnPrevPage(object sender, RoutedEventArgs e) { _page--; Rebuild(); }
    void OnNextPage(object sender, RoutedEventArgs e) { _page++; Rebuild(); }
    void OnBack(object sender, RoutedEventArgs e) => ExitSingle();

    void OnSinglePlayback(object sender, RoutedEventArgs e)
    {
        if (_single is { } c) _shell.ShowPlayback(c, _s.DeviceNow.AddMinutes(-5));
    }

    void OnSingleSnapshot(object sender, RoutedEventArgs e)
    {
        if (_single is { } c) _ = _shell.SnapshotAsync(SingleTile.Source, c.Name, DateTime.Now);
    }

    void OnRetry(object sender, RoutedEventArgs e)
    {
        _loaded = false;
        _ = LoadAsync();
    }

    /// <summary>Cameras go on and off line: refresh the list quietly when it is old, re-layout only if it changed.</summary>
    async Task RefreshQuietlyAsync()
    {
        try
        {
            var channels = await _s.GetChannelsAsync(refresh: true);
            var ordered = GridLayout.Order(channels, _s.Settings.LiveOrder, _s.NvrConfig!.Id);
            if (!_active || ordered.SequenceEqual(_ordered)) return;
            _ordered = ordered;
            if (_single is not null) _single = _ordered.FirstOrDefault(c => c.No == _single.No);
            ShowCurrent();
        }
        catch (Exception ex)
        {
            Log.Warn("Channel refresh failed", ex);
        }
    }

    void OnOpenSettings(object sender, RoutedEventArgs e) => _shell.ShowSettings();
}
