using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Cv.App.Logic;
using Cv.Core;
using Cv.Core.Nvr;
using Cv.Core.Video;

namespace Cv.App.Live;

/// <summary>
/// Trực tiếp: the cameras of the arrangement in use (layout, order, hidden cameras) on a grid of sub streams, one
/// camera big (main stream). Only tiles on screen have a source; leaving the screen closes them all.
/// </summary>
public partial class LiveView : UserControl, IPage
{
    readonly List<CameraTile> _tiles = [];
    IShell _shell = null!;
    Services _s = null!;
    IReadOnlyList<ChannelInfo> _channels = [];
    List<ChannelInfo> _all = [];       // every camera in the saved order, hidden ones included
    HashSet<int> _hidden = [];         // channel numbers left out of the grid
    List<ChannelInfo> _ordered = [];   // what the grid shows: _all without the hidden ones
    int _gridSetting;                  // layout code of the arrangement in use (0 = automatic)
    LayoutSpec _spec = GridLayout.Spec(1);
    int _size = 1;
    int _page;
    bool _drawerOpen;
    bool _active;
    bool _loaded;
    int _loadVersion;
    ChannelInfo? _single;
    int? _pendingSingle;

    public LiveView()
    {
        InitializeComponent();
        SingleTile.Clicked += _ => ExitSingle();
        SingleTile.SourceChanged += UpdateSound;
        LayoutPick.LayoutChosen += OnLayoutChosen;
        Presets.Changed += OnPresetChanged;
        Drawer.CloseRequested += () => SetDrawer(false);
        Drawer.UnhideRequested += Unhide;
    }

    public void Init(IShell shell)
    {
        _shell = shell;
        _s = shell.Services;
        Presets.Bind(_s.Settings);
        _s.NvrChanged += () =>
        {
            _loaded = false;
            CloseAll();
            _all = [];
            _ordered = [];
            _hidden = [];
            _channels = [];
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

    /// <summary>The cameras the grid shows, in order (hidden ones left out).</summary>
    internal IReadOnlyList<ChannelInfo> TestOrder => _ordered;
    internal IReadOnlyList<ChannelInfo> TestAll => _all;
    internal int TestHiddenCount => _all.Count - _ordered.Count;
    internal int TestLayoutSetting => _gridSetting;
    internal int TestTileCount => _size;
    internal bool InSingle => _single is not null;
    internal bool TestInSingle => InSingle;
    internal bool TestSingleHasFrame => SingleTile.Video.HasFrame;
    internal Cv.Core.Video.IVideoSource? TestSingleSource => SingleTile.Source;
    internal void TestEnterSingle(int slot) { if (_tiles[slot].Camera is { } c) EnterSingle(c); }
    internal void TestLayout(int setting) => OnLayoutChosen(setting);
    internal void TestNextPage() => OnNextPage(this, new RoutedEventArgs());
    internal void TestSwap(int a, int b) => Swap(a, b);
    internal int TestPageCount => GridLayout.PageCount(_ordered.Count, _size);
    internal void TestHide(int slot) { if (_tiles[slot].Camera is { } c) Hide(c); }
    internal void TestUnhide(int channel) => Unhide(channel);
    internal void TestDrop(int slot, int channel) => OnCameraDropped(slot, channel);
    internal void TestSetDrawer(bool open) => SetDrawer(open);
    internal CameraDrawer TestDrawer => Drawer;
    internal LayoutPicker TestLayoutPicker => LayoutPick;
    internal PresetMenu TestPresets => Presets;
    internal bool TestAllHiddenMessage => MessagePanel.Visibility == Visibility.Visible;

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
            _channels = channels;
            ApplyChannels(channels);
            _loaded = true;
            MessagePanel.Visibility = Visibility.Collapsed;
            if (_all.Count == 0)
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

    /// <summary>
    /// Takes the camera list and the arrangement in use (layout, order, hidden) from the settings. True when anything
    /// the grid would show changed.
    /// </summary>
    bool ApplyChannels(IReadOnlyList<ChannelInfo> channels)
    {
        var nvrId = _s.NvrConfig!.Id;
        var arrangement = LiveArrangements.Active(_s.Settings);
        var all = GridLayout.Order(channels, arrangement.Order, nvrId);
        var hidden = GridLayout.HiddenSet(arrangement.Hidden, nvrId);
        var changed = !all.SequenceEqual(_all) || !hidden.SetEquals(_hidden) || arrangement.Grid != _gridSetting;
        _all = all;
        _hidden = hidden;
        _gridSetting = arrangement.Grid;
        Recompute();
        return changed;
    }

    void Recompute() => _ordered = GridLayout.Visible(_all, _hidden);

    /// <summary>Saves layout, order and hidden cameras into the arrangement in use (every edit is saved at once).</summary>
    void Persist()
    {
        var nvrId = _s.NvrConfig!.Id;
        LiveArrangements.SaveActive(_s.Settings, _gridSetting, GridLayout.ToRefs(_all, nvrId), GridLayout.ToRefs(_hidden.Order(), nvrId));
        _s.SaveSettings();
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
        Drawer.Visibility = Visibility.Collapsed;
        MessageIcon.Text = glyph;
        MessageTitle.Text = title;
        MessageText.Text = text;
        MessageText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        RetryButton.Visibility = SettingsButton.Visibility = Visibility.Visible;
        OpenDrawerButton.Visibility = Visibility.Collapsed;
        MessageButtons.Visibility = buttons ? Visibility.Visible : Visibility.Collapsed;
        MessagePanel.Visibility = Visibility.Visible;
    }

    /// <summary>Every camera is hidden: say so, keep the bottom bar (to pick another Bộ) and offer the camera list.</summary>
    void ShowAllHidden()
    {
        CloseAll();
        TileGrid.Visibility = Visibility.Collapsed;
        MessageIcon.Text = Glyphs.Camera;
        MessageTitle.Text = "Tất cả camera đang ẩn";
        MessageText.Text = "Chọn một bộ khác ở thanh dưới, hoặc mở danh sách camera để hiện lại.";
        MessageText.Visibility = Visibility.Visible;
        RetryButton.Visibility = SettingsButton.Visibility = Visibility.Collapsed;
        OpenDrawerButton.Visibility = Visibility.Visible;
        MessageButtons.Visibility = Visibility.Visible;
        MessagePanel.Visibility = Visibility.Visible;
    }

    void ShowCurrent()
    {
        if (!_loaded || _all.Count == 0) return;
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
        var resolved = GridLayout.Resolve(_gridSetting, count);
        _spec = GridLayout.Spec(resolved);
        _size = _spec.Size;
        var pages = GridLayout.PageCount(count, _size);
        _page = Math.Clamp(_page, 0, pages - 1);
        UpdateBar(resolved, pages);

        if (count == 0)
        {
            ShowAllHidden();
            return;
        }
        MessagePanel.Visibility = Visibility.Collapsed;
        TileGrid.Visibility = Visibility.Visible;

        if (TileGrid.ColumnDefinitions.Count != _spec.Cols || TileGrid.RowDefinitions.Count != _spec.Rows)
        {
            TileGrid.ColumnDefinitions.Clear();
            TileGrid.RowDefinitions.Clear();
            for (var c = 0; c < _spec.Cols; c++) TileGrid.ColumnDefinitions.Add(new ColumnDefinition());
            for (var r = 0; r < _spec.Rows; r++) TileGrid.RowDefinitions.Add(new RowDefinition());
        }

        while (_tiles.Count < _size) _tiles.Add(NewTile());
        for (var i = _tiles.Count - 1; i >= _size; i--)
        {
            _tiles[i].Close();
            _tiles[i].Bind(null, StreamKind.Sub);
            TileGrid.Children.Remove(_tiles[i]);
            _tiles.RemoveAt(i);
        }

        for (var slot = 0; slot < _size; slot++)
        {
            var tile = _tiles[slot];
            var cell = _spec.Cells[slot];
            var index = _page * _size + slot;
            tile.Slot = slot;
            tile.Bind(index < count ? _ordered[index] : null, GridLayout.StreamFor(cell, _size));
            // Spans are set every time: a tile that was the big one must not keep its span in another layout.
            Grid.SetColumn(tile, cell.Col);
            Grid.SetRow(tile, cell.Row);
            Grid.SetColumnSpan(tile, cell.ColSpan);
            Grid.SetRowSpan(tile, cell.RowSpan);
            if (!TileGrid.Children.Contains(tile)) TileGrid.Children.Add(tile);
        }
        // Size the tiles before opening, so sources start at the right scale.
        TileGrid.UpdateLayout();
        if (_active)
            for (var slot = 0; slot < _size; slot++) _tiles[slot].Open(_s);
    }

    /// <summary>Everything under the grid: counts, layout, Bộ, pages, camera panel.</summary>
    void UpdateBar(int resolved, int pages)
    {
        var offline = _ordered.Count(c => !c.Online);
        var hidden = _all.Count - _ordered.Count;
        CountText.Text = $"{_ordered.Count} camera" + (offline > 0 ? $" · {offline} đang tắt" : "") + (hidden > 0 ? $" · {hidden} đã ẩn" : "");
        LayoutPick.Bind(_gridSetting, resolved);
        Presets.Bind(_s.Settings);
        Pager.Visibility = pages > 1 ? Visibility.Visible : Visibility.Collapsed;
        PageText.Text = $"Trang {_page + 1}/{pages}";
        PrevPage.IsEnabled = _page > 0;
        NextPage.IsEnabled = _page < pages - 1;
        UpdateDrawer();
    }

    void UpdateDrawer()
    {
        if (_drawerOpen)
        {
            DrawerButton.Background = (Brush)FindResource("AccentSoftBrush");
            DrawerButton.BorderBrush = (Brush)FindResource("AccentBrush");
        }
        else
        {
            DrawerButton.ClearValue(BackgroundProperty);
            DrawerButton.ClearValue(BorderBrushProperty);
        }
        var show = _drawerOpen && _single is null && BottomBar.Visibility == Visibility.Visible;
        Drawer.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show) Drawer.Bind(_ordered, _all.Where(c => _hidden.Contains(c.No)).ToList(), _size, _page);
    }

    CameraTile NewTile()
    {
        var t = new CameraTile { Margin = new Thickness(2) };
        t.Clicked += tile => { if (tile.Camera is { } c) EnterSingle(c); };
        t.PlaybackRequested += tile => { if (tile.Camera is { } c) _shell.ShowPlayback(c, _s.DeviceNow.AddMinutes(-5)); };
        t.SnapshotRequested += tile => { if (tile.Camera is { } c) _ = _shell.SnapshotAsync(tile.Source, c.Name, DateTime.Now); };
        t.SwapRequested += Swap;
        t.HideRequested += tile => { if (tile.Camera is { } c) Hide(c); };
        t.CameraDropped += OnCameraDropped;
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
        Drawer.Visibility = Visibility.Collapsed;
        MessagePanel.Visibility = Visibility.Collapsed;
        SinglePanel.Visibility = Visibility.Visible;
        SingleName.Text = cam.Name;
        SingleSnapshot.IsEnabled = cam.Online;
        SingleTile.Bind(cam, StreamKind.Main);
        SinglePanel.UpdateLayout();
        if (_active) SingleTile.Open(_s);
        UpdateSound();
    }

    // ---- Sound (single view only; the grid never plays sound) ----

    void UpdateSound() => Controls.SoundButton.Refresh(SingleSound, SingleTile.Source, _s.SoundOn);

    void OnSingleSound(object sender, RoutedEventArgs e)
    {
        Controls.SoundButton.Toggle(_s, SingleTile.Source);
        UpdateSound();
        if (SingleTile.Source is IAudioSource { SoundOn: true, AudioError: { } err }) _shell.Toast(err, ToastKind.Warn);
    }

    internal string TestSoundText => (string)SingleSound.Content;
    internal bool TestSoundEnabled => SingleSound.IsEnabled;
    internal void TestToggleSound() => OnSingleSound(this, new RoutedEventArgs());

    void ExitSingle()
    {
        _single = null;
        if (_active) ShowGrid();
    }

    // ---- Changing the arrangement: every edit is saved into the arrangement in use ----

    /// <summary>Drag one tile onto another: the two cameras swap places (the tiles swap too, so both streams keep running).</summary>
    void Swap(int fromSlot, int toSlot)
    {
        var a = _page * _size + fromSlot;
        var b = _page * _size + toSlot;
        if (a >= _ordered.Count || b >= _ordered.Count) return;
        GridLayout.Swap(_all, _ordered[a].No, _ordered[b].No);
        Recompute();
        Persist();
        (_tiles[fromSlot], _tiles[toSlot]) = (_tiles[toSlot], _tiles[fromSlot]);
        Rebuild();
    }

    /// <summary>A camera from the camera panel dropped on a tile (empty tiles too, cameras on other pages too).</summary>
    void OnCameraDropped(int slot, int channel)
    {
        if (_all.All(c => c.No != channel)) return;
        var index = _page * _size + slot;
        int? target = index < _ordered.Count ? _ordered[index].No : null;
        var from = _ordered.FindIndex(c => c.No == channel) - _page * _size;
        var samePageSwap = target is not null && from >= 0 && from < _size && from < _tiles.Count;
        GridLayout.Place(_all, _hidden, channel, target);
        Recompute();
        Persist();
        if (samePageSwap) (_tiles[from], _tiles[slot]) = (_tiles[slot], _tiles[from]);
        Rebuild();
    }

    void Hide(ChannelInfo cam)
    {
        _hidden.Add(cam.No);
        Recompute();
        Persist();
        Rebuild();
        _shell.Toast($"Đã ẩn “{cam.Name}” khỏi Trực tiếp. Xem lại vẫn thấy camera này.", ToastKind.Info, "Hoàn tác", () => Unhide(cam.No));
    }

    void Unhide(int channel)
    {
        if (!_hidden.Remove(channel)) return;
        Recompute();
        Persist();
        Rebuild();
        if (_all.FirstOrDefault(c => c.No == channel) is { } cam) _shell.Toast($"Đã hiện lại “{cam.Name}”.");
    }

    void OnLayoutChosen(int setting)
    {
        _gridSetting = setting;
        _page = 0;
        Persist();
        Rebuild();
    }

    /// <summary>The Bộ menu switched, added, renamed or deleted an arrangement in the settings: load the one in use.</summary>
    void OnPresetChanged(string message)
    {
        _s.SaveSettings();
        ApplyChannels(_channels);
        _page = 0;
        if (_active && _single is null) Rebuild();
        _shell.Toast(message);
    }

    void SetDrawer(bool open)
    {
        _drawerOpen = open;
        UpdateDrawer();
    }

    void OnDrawerClick(object sender, RoutedEventArgs e) => SetDrawer(!_drawerOpen);
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
            _channels = channels;
            if (!_active || !ApplyChannels(channels)) return;
            if (_single is not null) _single = _all.FirstOrDefault(c => c.No == _single.No);
            ShowCurrent();
        }
        catch (Exception ex)
        {
            Log.Warn("Channel refresh failed", ex);
        }
    }

    void OnOpenSettings(object sender, RoutedEventArgs e) => _shell.ShowSettings();
}
