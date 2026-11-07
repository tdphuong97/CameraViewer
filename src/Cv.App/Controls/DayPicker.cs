using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Cv.App.Controls;

/// <summary>A small month calendar in the app's style (Monday first, future days disabled).</summary>
public sealed class DayPicker : Border
{
    static readonly string[] WeekDays = ["T2", "T3", "T4", "T5", "T6", "T7", "CN"];

    readonly TextBlock _title = new() { FontSize = 15, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    readonly UniformGrid _days = new() { Columns = 7, Rows = 6 };
    readonly Button _next;
    DateTime _month, _selected, _today;

    public event Action<DateTime>? Picked;

    public DayPicker()
    {
        Background = (Brush)Application.Current.FindResource("RaisedBrush");
        BorderBrush = (Brush)Application.Current.FindResource("BorderStrongBrush");
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(10);
        Padding = new Thickness(14);
        Margin = new Thickness(0, 6, 0, 0);

        var prev = NavButton(Glyphs.ChevronLeft, "Tháng trước", -1);
        _next = NavButton(Glyphs.ChevronRight, "Tháng sau", 1);
        var header = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_title, 1);
        Grid.SetColumn(_next, 2);
        header.Children.Add(prev);
        header.Children.Add(_title);
        header.Children.Add(_next);

        var names = new UniformGrid { Columns = 7, Margin = new Thickness(0, 0, 0, 4) };
        foreach (var d in WeekDays)
            names.Children.Add(new TextBlock
            {
                Text = d, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = (Brush)Application.Current.FindResource("MutedBrush"), Margin = new Thickness(0, 2, 0, 4),
            });

        var panel = new StackPanel { Width = 7 * 38 };
        panel.Children.Add(header);
        panel.Children.Add(names);
        panel.Children.Add(_days);
        Child = panel;
    }

    Button NavButton(string glyph, string tip, int delta)
    {
        var b = new Button { Style = (Style)Application.Current.FindResource("IconButton"), Width = 34, Height = 34, MinHeight = 34, ToolTip = tip };
        Ui.SetGlyph(b, glyph);
        Ui.SetGlyphSize(b, 13);
        b.Click += (_, _) => { _month = _month.AddMonths(delta); Build(); };
        return b;
    }

    public void Show(DateTime selected, DateTime today)
    {
        _selected = selected.Date;
        _today = today.Date;
        _month = new DateTime(_selected.Year, _selected.Month, 1);
        Build();
    }

    void Build()
    {
        _title.Text = $"Tháng {_month.Month}/{_month.Year}";
        _next.IsEnabled = _month < new DateTime(_today.Year, _today.Month, 1);
        _days.Children.Clear();
        var offset = ((int)_month.DayOfWeek + 6) % 7;
        var start = _month.AddDays(-offset);
        var ghost = (Style)Application.Current.FindResource("GhostButton");
        for (var i = 0; i < 42; i++)
        {
            var d = start.AddDays(i);
            var b = new Button
            {
                Style = ghost, Content = d.Day.ToString(CultureInfo.InvariantCulture), Width = 36, Height = 34, MinHeight = 34,
                Padding = new Thickness(0), Margin = new Thickness(1), IsEnabled = d <= _today,
            };
            if (d.Month != _month.Month) b.Foreground = (Brush)Application.Current.FindResource("MutedBrush");
            if (d == _selected)
            {
                b.Style = (Style)Application.Current.FindResource("PrimaryButton");
                b.Width = 36;
                b.Height = 34;
            }
            else if (d == _today) b.BorderBrush = (Brush)Application.Current.FindResource("AccentBrush");
            b.Click += (_, _) => Picked?.Invoke(d);
            _days.Children.Add(b);
        }
    }
}
