using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Cv.App.Logic;
using Cv.Core.Settings;

namespace Cv.App.Live;

/// <summary>
/// The "Bộ" button of the live bar and its menu: switch, save as new, rename, delete. It edits the AppSettings it is
/// bound to directly and raises Changed once per change; the owner saves the settings and reloads the live grid.
/// </summary>
public sealed partial class PresetMenu : UserControl
{
    /// <summary>A click on the button within this time of the popup closing is the click that closed it (see OnMainClick).</summary>
    const long ReopenGuardMs = 350;
    const string DeleteGlyph = "\uE74D";
    static readonly DependencyPropertyDescriptor PopupIsOpen = DependencyPropertyDescriptor.FromProperty(Popup.IsOpenProperty, typeof(Popup));

    enum FormMode { None, New, Rename }

    readonly DispatcherTimer _confirmTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    AppSettings? _settings;
    FormMode _mode;
    bool _confirmingDelete;
    long _closedAt;

    /// <summary>Raised after any change; the argument is the message for a toast.</summary>
    public event Action<string>? Changed;

    public PresetMenu()
    {
        InitializeComponent();
        NameBox.MaxLength = LiveArrangements.MaxNameLength;
        // Popup.Closed is raised well after IsOpen turns false (about 170 ms), later than the mouse-up of the click that
        // closed it, so the time is taken from the property change itself. The form and the delete question start fresh then too.
        PopupIsOpen.AddValueChanged(Pop, (_, _) =>
        {
            if (Pop.IsOpen) return;
            _closedAt = Environment.TickCount64;
            ResetTransient();
        });
        _confirmTimer.Tick += (_, _) => ResetDelete();
    }

    /// <summary>Keeps the reference; refreshes the button and, while the menu is open, its list.</summary>
    public void Bind(AppSettings settings)
    {
        _settings = settings;
        Refresh();
    }

    internal void TestSwitch(string name) => SwitchTo(name);

    /// <summary>As typing <paramref name="name"/> in the new-arrangement form and pressing Lưu; "" when it was added, else the error.</summary>
    internal string TestAdd(string name)
    {
        _mode = FormMode.New;
        return Save(name) ?? "";
    }

    /// <summary>As the two clicks on Xoá.</summary>
    internal void TestDeleteActive()
    {
        OnDeleteClick(this, new RoutedEventArgs());
        OnDeleteClick(this, new RoutedEventArgs());
    }

    void OnMainClick(object sender, RoutedEventArgs e)
    {
        if (Pop.IsOpen) { Pop.IsOpen = false; return; }
        // With StaysOpen=False a click on the button while the popup is open closes it on mouse-down and would then
        // open it again on mouse-up. Skip that second half.
        if (Environment.TickCount64 - _closedAt < ReopenGuardMs) return;
        ResetTransient();
        Refresh();
        Pop.IsOpen = true;
    }

    /// <summary>Button text, list rows and which buttons are enabled, from the settings.</summary>
    void Refresh()
    {
        if (_settings is null) return;
        var name = LiveArrangements.ActiveName(_settings);
        MainText.Text = name;
        Main.ToolTip = $"Bộ sắp xếp: {name}";
        var custom = !LiveArrangements.IsDefault(_settings);
        RenameButton.IsEnabled = custom;
        DeleteButton.IsEnabled = custom;
        BuildRows(name);
    }

    void BuildRows(string active)
    {
        Rows.Children.Clear();
        if (_settings is null) return;
        foreach (var a in LiveArrangements.All(_settings)) Rows.Children.Add(Row(a, a.Name == active));
    }

    Button Row(LiveArrangement a, bool active)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (active)
            grid.Children.Add(new TextBlock
            {
                Text = "\uE73E", Style = (Style)FindResource("Icon"), FontSize = 13, Foreground = (Brush)FindResource("AccentBrush"),
            });
        var name = new TextBlock
        {
            Text = a.Name, FontSize = 14, Foreground = (Brush)FindResource("TextBrush"), VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);
        var meta = new TextBlock
        {
            Text = GridLayout.ShortLabel(a.Grid) + (a.Hidden.Count > 0 ? $" · {a.Hidden.Count} ẩn" : ""),
            FontSize = 12.5, Foreground = (Brush)FindResource("MutedBrush"), VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
        };
        Grid.SetColumn(meta, 2);
        grid.Children.Add(meta);

        var b = new Button { Style = (Style)FindResource("PresetRow"), Content = grid };
        if (active)
        {
            b.Background = (Brush)FindResource("AccentSoftBrush");
            b.BorderBrush = (Brush)FindResource("AccentBrush");
        }
        b.Click += (_, _) => SwitchTo(a.Name);
        return b;
    }

    void SwitchTo(string name)
    {
        if (_settings is null) return;
        if (name == LiveArrangements.ActiveName(_settings)) { Pop.IsOpen = false; return; }
        if (!LiveArrangements.Switch(_settings, name)) { Refresh(); return; }
        Done($"Đã chuyển sang bộ “{name}”");
    }

    /// <summary>Closes the menu, refreshes, and raises the one Changed event.</summary>
    void Done(string message)
    {
        ResetTransient();
        Pop.IsOpen = false;
        Refresh();
        Changed?.Invoke(message);
    }

    void OnNewClick(object sender, RoutedEventArgs e) => ShowForm(FormMode.New);

    void OnRenameClick(object sender, RoutedEventArgs e) => ShowForm(FormMode.Rename);

    void ShowForm(FormMode mode)
    {
        if (_settings is null) return;
        ResetDelete();
        _mode = mode;
        NameBox.Text = mode == FormMode.New ? NextName(_settings) : LiveArrangements.ActiveName(_settings);
        ErrorText.Visibility = Visibility.Collapsed;
        Form.Visibility = Visibility.Visible;
        // Focus once the form is laid out and the popup window can take the keyboard.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            NameBox.Focus();
            Keyboard.Focus(NameBox);
            NameBox.SelectAll();
        });
    }

    /// <summary>"Bộ N" for the first N after the number of arrangements that is not taken yet.</summary>
    static string NextName(AppSettings s)
    {
        var names = LiveArrangements.Names(s);
        for (var n = names.Count + 1; ; n++)
        {
            var candidate = FormattableString.Invariant($"Bộ {n}");
            if (!names.Any(x => string.Equals(x, candidate, StringComparison.OrdinalIgnoreCase))) return candidate;
        }
    }

    /// <summary>Adds or renames (by the form mode). Null on success (Changed raised), else the error to show.</summary>
    string? Save(string name)
    {
        if (_settings is null) return "Chưa có cài đặt.";
        var renaming = _mode == FormMode.Rename;
        var err = renaming ? LiveArrangements.RenameActive(_settings, name) : LiveArrangements.AddFromActive(_settings, name);
        if (err is not null) return err;
        name = name.Trim();
        Done(renaming ? $"Đã đổi tên thành “{name}”" : $"Đã lưu bộ mới “{name}”");
        return null;
    }

    void OnSaveClick(object sender, RoutedEventArgs e) => SaveForm();

    void SaveForm()
    {
        var err = Save(NameBox.Text);
        if (err is null) return;
        ErrorText.Text = err;
        ErrorText.Visibility = Visibility.Visible;
        NameBox.Focus();
    }

    void OnCancelClick(object sender, RoutedEventArgs e) => CloseForm();

    void OnNameChanged(object sender, TextChangedEventArgs e) => ErrorText.Visibility = Visibility.Collapsed;

    void OnNameKeyDown(object sender, KeyEventArgs e)
    {
        // Handled, so Esc closes only the form (LiveView also uses Esc) and Enter does not reach anything behind the popup.
        if (e.Key == Key.Enter) { SaveForm(); e.Handled = true; }
        else if (e.Key == Key.Escape) { CloseForm(); e.Handled = true; }
    }

    void CloseForm()
    {
        _mode = FormMode.None;
        Form.Visibility = Visibility.Collapsed;
        ErrorText.Visibility = Visibility.Collapsed;
    }

    /// <summary>Two steps: the first click asks for confirmation for 3 seconds, the second deletes.</summary>
    void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (_settings is null || LiveArrangements.IsDefault(_settings)) return;
        if (!_confirmingDelete)
        {
            _confirmingDelete = true;
            DeleteButton.Content = "Bấm lại để xoá";
            Ui.SetGlyph(DeleteButton, null); // the icon would not leave room for the longer text
            DeleteButton.Foreground = (Brush)FindResource("DangerBrush");
            DeleteButton.BorderBrush = (Brush)FindResource("DangerBrush");
            _confirmTimer.Stop();
            _confirmTimer.Start();
            return;
        }
        ResetDelete();
        var name = LiveArrangements.DeleteActive(_settings);
        if (name is null) Refresh();
        else Done($"Đã xoá bộ “{name}”");
    }

    void ResetDelete()
    {
        _confirmTimer.Stop();
        if (!_confirmingDelete) return;
        _confirmingDelete = false;
        DeleteButton.Content = "Xoá";
        Ui.SetGlyph(DeleteButton, DeleteGlyph);
        DeleteButton.ClearValue(ForegroundProperty);
        DeleteButton.ClearValue(BorderBrushProperty);
    }

    /// <summary>The form, its error and the delete confirmation start fresh each time the menu opens.</summary>
    void ResetTransient()
    {
        CloseForm();
        ResetDelete();
    }
}
