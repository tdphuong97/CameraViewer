using System.Windows;

namespace Cv.App;

/// <summary>Attached properties the theme templates read: an icon glyph, its size, a badge and a placeholder.</summary>
public static class Ui
{
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.RegisterAttached(
        "Glyph", typeof(string), typeof(Ui), new FrameworkPropertyMetadata(null));
    public static string? GetGlyph(DependencyObject o) => (string?)o.GetValue(GlyphProperty);
    public static void SetGlyph(DependencyObject o, string? v) => o.SetValue(GlyphProperty, v);

    public static readonly DependencyProperty GlyphSizeProperty = DependencyProperty.RegisterAttached(
        "GlyphSize", typeof(double), typeof(Ui), new FrameworkPropertyMetadata(16.0));
    public static double GetGlyphSize(DependencyObject o) => (double)o.GetValue(GlyphSizeProperty);
    public static void SetGlyphSize(DependencyObject o, double v) => o.SetValue(GlyphSizeProperty, v);

    public static readonly DependencyProperty BadgeProperty = DependencyProperty.RegisterAttached(
        "Badge", typeof(string), typeof(Ui), new FrameworkPropertyMetadata(null));
    public static string? GetBadge(DependencyObject o) => (string?)o.GetValue(BadgeProperty);
    public static void SetBadge(DependencyObject o, string? v) => o.SetValue(BadgeProperty, v);

    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.RegisterAttached(
        "Placeholder", typeof(string), typeof(Ui), new FrameworkPropertyMetadata(null));
    public static string? GetPlaceholder(DependencyObject o) => (string?)o.GetValue(PlaceholderProperty);
    public static void SetPlaceholder(DependencyObject o, string? v) => o.SetValue(PlaceholderProperty, v);
}

/// <summary>Segoe Fluent Icons / MDL2 code points used by the app.</summary>
public static class Glyphs
{
    public const string Video = "";
    public const string History = "";
    public const string Download = "";
    public const string Settings = "";
    public const string Camera = "";
    public const string FullScreen = "";
    public const string Back = "";
    public const string Play = "";
    public const string Pause = "";
    public const string Undo = "";
    public const string Redo = "";
    public const string Save = "";
    public const string OpenFile = "";
    public const string FolderOpen = "";
    public const string Cancel = "";
    public const string Refresh = "";
    public const string Warning = "";
    public const string Error = "";
    public const string Volume = "";
    public const string Mute = "";
    public const string Check = "";
    public const string Completed = "";
    public const string Sync = "";
    public const string Calendar = "";
    public const string Clock = "";
    public const string Power = "";
    public const string Info = "";
    public const string ChevronLeft = "";
    public const string ChevronRight = "";
    public const string ChevronDown = "";
    public const string Diagnostic = "";
    public const string Network = "";
    public const string Delete = "";
    public const string Grid = "";
}
