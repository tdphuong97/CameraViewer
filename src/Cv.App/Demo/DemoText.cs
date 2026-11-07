using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Cv.App.Demo;

/// <summary>An 8-bit coverage mask of white text, blitted onto demo frames.</summary>
internal sealed class TextMask(byte[] alpha, int width, int height)
{
    public readonly byte[] Alpha = alpha;
    public readonly int Width = width;
    public readonly int Height = height;
}

/// <summary>
/// Text for the demo picture. WPF can only render text on an STA thread, so masks are rendered up front on the UI
/// thread and the frame thread just blends them (no allocations per frame).
/// </summary>
internal static class DemoText
{
    /// <summary>Font sizes in pixels for small, medium and large frames.</summary>
    public static readonly double[] Sizes = [12, 18, 28];

    public const string ClockChars = "0123456789:/ ";

    static readonly Typeface Face = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    public static int SizeClass(int frameHeight) => frameHeight < 280 ? 0 : frameHeight < 620 ? 1 : 2;

    public static bool CanRender => Thread.CurrentThread.GetApartmentState() == ApartmentState.STA;

    public static TextMask Render(string text, double size)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, size, Brushes.White, 1.0);
        var w = Math.Max(1, (int)Math.Ceiling(ft.WidthIncludingTrailingWhitespace));
        var h = Math.Max(1, (int)Math.Ceiling(ft.Height));
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen()) dc.DrawText(ft, new Point(0, 0));
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var px = new byte[w * h * 4];
        rtb.CopyPixels(px, w * 4, 0);
        var alpha = new byte[w * h];
        for (var i = 0; i < alpha.Length; i++) alpha[i] = px[i * 4 + 3];
        return new TextMask(alpha, w, h);
    }

    /// <summary>One mask per clock character, all the same width (Segoe UI digits are tabular).</summary>
    public static TextMask[] RenderClockAtlas(double size)
    {
        var masks = new TextMask[ClockChars.Length];
        for (var i = 0; i < ClockChars.Length; i++) masks[i] = Render(ClockChars[i].ToString(), size);
        return masks;
    }
}
