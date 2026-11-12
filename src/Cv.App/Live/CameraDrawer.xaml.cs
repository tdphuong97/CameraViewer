using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Cv.Core.Nvr;

namespace Cv.App.Live;

/// <summary>
/// "Ngăn Camera": a side panel of the live view listing every camera (shown, then hidden) so one can be dragged onto a tile.
/// Rows are rebuilt on every Bind (the lists are small); the drag carries the channel number under <see cref="DragFormat"/>.
/// </summary>
public partial class CameraDrawer : UserControl
{
    public const string DragFormat = "Cv.App.CameraNo";
    const double DragThreshold = 8;

    // Row look as styles, so hover follows the real mouse state (also right after a drag ends without a MouseLeave).
    readonly Style _plainRow, _pageRow;
    Border? _pressRow;
    Point _pressAt;
    int _rows;

    public CameraDrawer()
    {
        InitializeComponent();
        _plainRow = RowStyle(Brushes.Transparent);
        _pageRow = RowStyle(Res("AccentSoftBrush"));
        CloseButton.Click += (_, _) => CloseRequested?.Invoke();
    }

    public event Action? CloseRequested;

    /// <summary>"Hiện lại" on a hidden row; the argument is the channel number.</summary>
    public event Action<int>? UnhideRequested;

    /// <summary>Number of camera rows (shown + hidden).</summary>
    internal int TestRows => _rows;

    internal void TestUnhide(int channelNo) => Unhide(channelNo);

    void Unhide(int channelNo) => UnhideRequested?.Invoke(channelNo);

    /// <summary>
    /// Lists the cameras. The page label and the highlight use the position in <paramref name="shown"/> (the grid order):
    /// camera i sits on page i / pageSize.
    /// </summary>
    public void Bind(IReadOnlyList<ChannelInfo> shown, IReadOnlyList<ChannelInfo> hidden, int pageSize, int currentPage)
    {
        // Clearing and refilling in one pass already keeps the offset; restoring it explicitly guards against a clamp.
        var offset = Scroller.VerticalOffset;
        Items.Children.Clear();
        _rows = 0;
        _pressRow = null;

        var size = Math.Max(1, pageSize);
        var multiPage = shown.Count > size;

        AddHeader($"ĐANG HIỆN ({shown.Count})", 4);
        if (shown.Count == 0)
            Items.Children.Add(new TextBlock
            {
                Text = "Không có camera nào đang hiện.", FontSize = 13, Foreground = Res("MutedBrush"), Margin = new Thickness(10, 2, 10, 0),
                TextWrapping = TextWrapping.Wrap,
            });
        for (var i = 0; i < shown.Count; i++)
        {
            var page = i / size;
            var label = multiPage ? (page == currentPage ? "Trang này" : $"Trang {page + 1}") : null;
            // With a single page every row is "on the page", so the highlight would say nothing.
            Items.Children.Add(MakeRow(shown[i], hidden: false, onPage: multiPage && page == currentPage, label));
        }

        if (hidden.Count > 0)
        {
            AddHeader($"ĐÃ ẨN ({hidden.Count})", 16);
            foreach (var cam in hidden) Items.Children.Add(MakeRow(cam, hidden: true, onPage: false, null));
        }

        Scroller.ScrollToVerticalOffset(offset);
    }

    Brush Res(string key) => (Brush)FindResource(key);

    Style RowStyle(Brush background)
    {
        var over = new Trigger { Property = IsMouseOverProperty, Value = true };
        over.Setters.Add(new Setter(Border.BackgroundProperty, Res("RaisedBrush")));
        var style = new Style(typeof(Border));
        style.Setters.Add(new Setter(Border.BackgroundProperty, background));
        style.Triggers.Add(over);
        return style;
    }

    void AddHeader(string text, double top) =>
        Items.Children.Add(new TextBlock
        {
            Text = text, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = Res("MutedBrush"),
            Margin = new Thickness(10, top, 10, 6),
        });

    Border MakeRow(ChannelInfo cam, bool hidden, bool onPage, string? pageLabel)
    {
        var grid = new Grid();
        for (var c = 0; c < 4; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = c == 2 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });

        var grip = new TextBlock
        {
            Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 14, Foreground = Res("SubtleBrush"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
        };
        var dot = new Ellipse
        {
            Width = 8, Height = 8, Fill = Res(cam.Online ? "OkBrush" : "BorderStrongBrush"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 10, 0),
        };
        var name = new TextBlock
        {
            Text = cam.Name, FontSize = 13.5, Foreground = Res(hidden ? "MutedBrush" : "TextBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(dot, 1);
        Grid.SetColumn(name, 2);
        grid.Children.Add(grip);
        grid.Children.Add(dot);
        grid.Children.Add(name);

        FrameworkElement? right = null;
        if (hidden)
        {
            var b = new Button
            {
                Content = "Hiện lại", Style = (Style)FindResource("LinkButton"), Height = 26, MinHeight = 26, FontSize = 12.5,
                Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(6, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center,
            };
            b.Click += (_, _) => Unhide(cam.No);
            right = b;
        }
        else if (pageLabel is not null)
            right = new TextBlock
            {
                Text = pageLabel, FontSize = 12, Foreground = Res("MutedBrush"), VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 10, 0),
            };
        if (right is not null)
        {
            Grid.SetColumn(right, 3);
            grid.Children.Add(right);
        }

        var row = new Border
        {
            Height = 34, CornerRadius = new CornerRadius(6), Style = onPage ? _pageRow : _plainRow, Cursor = Cursors.Hand,
            Tag = cam.No, Child = grid,
        };
        // Bubbling on purpose: the "Hiện lại" button handles its own press, so it never arms a drag.
        row.MouseLeftButtonDown += OnDown;
        row.MouseLeftButtonUp += (_, _) => _pressRow = null;
        row.MouseEnter += (_, _) => _pressRow = null;
        row.MouseMove += TryDrag;
        // A quick pull out of a 34px row may never raise a MouseMove on it, so leaving counts as moving.
        row.MouseLeave += TryDrag;
        _rows++;
        return row;
    }

    void OnDown(object sender, MouseButtonEventArgs e)
    {
        _pressRow = (Border)sender;
        _pressAt = e.GetPosition(this);
    }

    void TryDrag(object sender, MouseEventArgs e)
    {
        if (_pressRow is not { Tag: int no } row || !ReferenceEquals(row, sender)) return;
        if (e.LeftButton != MouseButtonState.Pressed) { _pressRow = null; return; }
        // Leaving the row with the button held is drag intent even under the threshold: rows touch, so the next
        // row's MouseEnter would otherwise clear the press and a pull from near a row edge would never start.
        var d = e.GetPosition(this) - _pressAt;
        if (e.RoutedEvent != MouseLeaveEvent && Math.Abs(d.X) < DragThreshold && Math.Abs(d.Y) < DragThreshold) return;
        _pressRow = null;
        DragDrop.DoDragDrop(row, new DataObject(DragFormat, no), DragDropEffects.Move);
    }
}
