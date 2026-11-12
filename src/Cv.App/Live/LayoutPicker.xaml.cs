using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using Cv.App.Logic;

namespace Cv.App.Live;

/// <summary>The layout button of the live bar and its popup of thumbnails ("Chia đều" and "Khung lớn + khung nhỏ").</summary>
public sealed partial class LayoutPicker : UserControl
{
    /// <summary>A click on the button within this time of the popup closing is the click that closed it (see OnMainClick).</summary>
    const long ReopenGuardMs = 350;
    static readonly DependencyPropertyDescriptor PopupIsOpen = DependencyPropertyDescriptor.FromProperty(Popup.IsOpenProperty, typeof(Popup));

    int _setting;
    int _resolved = 1;
    long _closedAt;

    /// <summary>The layout code chosen: 0 = Tự động, else a code from GridLayout.Sizes.</summary>
    public event Action<int>? LayoutChosen;

    public LayoutPicker()
    {
        InitializeComponent();
        // Popup.Closed is raised well after IsOpen turns false (about 170 ms), later than the mouse-up of the click that
        // closed it, so the time is taken from the property change itself.
        PopupIsOpen.AddValueChanged(Pop, (_, _) => { if (!Pop.IsOpen) _closedAt = Environment.TickCount64; });
        Bind(0, _resolved);
    }

    /// <param name="setting">AppSettings.LiveGrid (0 = automatic).</param>
    /// <param name="resolved">GridLayout.Resolve(setting, cameraCount).</param>
    public void Bind(int setting, int resolved)
    {
        _setting = setting;
        _resolved = resolved;
        MainText.Text = GridLayout.Label(setting, resolved);
        MainThumb.Child = LayoutThumb.Create(GridLayout.Spec(resolved), 44, 28, dim: false);
        if (Pop.IsOpen) BuildTiles();
    }

    internal void TestChoose(int setting) => Choose(setting);

    void OnMainClick(object sender, RoutedEventArgs e)
    {
        if (Pop.IsOpen) { Pop.IsOpen = false; return; }
        // With StaysOpen=False a click on the button while the popup is open closes it on mouse-down and would then
        // open it again on mouse-up. Skip that second half.
        if (Environment.TickCount64 - _closedAt < ReopenGuardMs) return;
        BuildTiles();
        Pop.IsOpen = true;
    }

    void Choose(int setting)
    {
        Pop.IsOpen = false;
        LayoutChosen?.Invoke(setting);
    }

    void BuildTiles()
    {
        // An unknown setting counts as automatic (as Resolve does); a known code with no tile (20) highlights nothing.
        var current = Array.IndexOf(GridLayout.Sizes, _setting) >= 0 ? _setting : 0;
        // What automatic would show for the cameras in use: from the tile count of the layout now on screen.
        var auto = GridLayout.Spec(GridLayout.Resolve(0, GridLayout.Spec(_resolved).Size));
        Fill(EqualTiles, GridLayout.PickerSizes, current, auto);
        Fill(BigTiles, GridLayout.BigPickerSizes, current, auto);
    }

    void Fill(Panel host, int[] codes, int current, LayoutSpec auto)
    {
        host.Children.Clear();
        foreach (var code in codes) host.Children.Add(Tile(code, code == current, auto));
    }

    Button Tile(int code, bool current, LayoutSpec auto)
    {
        var content = new StackPanel { Width = 60 };
        content.Children.Add(LayoutThumb.Create(code == 0 ? auto : GridLayout.Spec(code), 52, 32, dim: code == 0));
        content.Children.Add(new TextBlock
        {
            Text = GridLayout.OptionLabel(code), FontSize = 12.5, HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0),
        });
        var b = new Button { Style = (Style)FindResource("LayoutTile"), Content = content };
        if (code == 0) b.ToolTip = "Tự chọn số khung vừa đủ mọi camera";
        if (current)
        {
            b.Background = (Brush)FindResource("AccentSoftBrush");
            b.BorderBrush = (Brush)FindResource("AccentBrush");
            b.Foreground = (Brush)FindResource("TextBrush");
        }
        b.Click += (_, _) => Choose(code);
        return b;
    }
}

/// <summary>Draws a layout as a small picture: one rectangle per tile, the big tile in the accent colour.</summary>
internal static class LayoutThumb
{
    /// <param name="dim">60 % opacity, for the Tự động tile (it is a guess at what automatic would show).</param>
    internal static FrameworkElement Create(LayoutSpec spec, double width, double height, bool dim)
    {
        var normal = (Brush)Application.Current.FindResource("BorderStrongBrush");
        var accent = (Brush)Application.Current.FindResource("AccentBrush");
        var grid = new Grid { Width = width, Height = height, Opacity = dim ? 0.6 : 1, IsHitTestVisible = false };
        for (var i = 0; i < spec.Cols; i++) grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (var i = 0; i < spec.Rows; i++) grid.RowDefinitions.Add(new RowDefinition());
        foreach (var c in spec.Cells)
        {
            // A 1px margin on the sides that touch another tile makes 2px gaps between tiles and none at the outer edge.
            var r = new Rectangle
            {
                Fill = c.Big ? accent : normal, RadiusX = 1.5, RadiusY = 1.5,
                Margin = new Thickness(
                    c.Col == 0 ? 0 : 1, c.Row == 0 ? 0 : 1,
                    c.Col + c.ColSpan == spec.Cols ? 0 : 1, c.Row + c.RowSpan == spec.Rows ? 0 : 1),
            };
            Grid.SetColumn(r, c.Col);
            Grid.SetRow(r, c.Row);
            Grid.SetColumnSpan(r, c.ColSpan);
            Grid.SetRowSpan(r, c.RowSpan);
            grid.Children.Add(r);
        }
        return grid;
    }
}
