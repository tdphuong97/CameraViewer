using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cv.Core.Nvr;

namespace Cv.App.Demo;

/// <summary>
/// The demo recorder's "person seen" events: a visit every half hour or so in the day, one or two at night, one a few
/// minutes ago (so today always has one), each a burst of detections a second or two apart, only on recorded time.
/// Pictures are drawn on the fly: the camera's colour, its date and name, and a figure.
/// </summary>
internal static class DemoDetections
{
    const string Prefix = "/picture/demo/";

    public static List<DetectionEvent> People(int channel, DateTime from, DateTime to, DateTime now)
    {
        var events = new List<DetectionEvent>();
        if (channel == DemoData.OfflineChannel) return events;
        if (to > now) to = now;
        for (var day = from.Date; day < to; day = day.AddDays(1))
        {
            var recorded = DemoNvrClient.Segments(channel, day, day.AddDays(1), now);
            foreach (var start in VisitStarts(channel, day, now))
            {
                var rnd = new Random(channel * 31 + start.Hour * 60 + start.Minute);
                var t = start;
                for (var i = rnd.Next(2, 13); i > 0; i--, t = t.AddSeconds(rnd.Next(1, 4)))
                {
                    if (t < from || t >= to || !recorded.Any(s => s.Start <= t && t < s.End)) continue;
                    events.Add(new DetectionEvent(channel, t, Prefix + channel.ToString(CultureInfo.InvariantCulture) + "/" +
                        t.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)));
                }
            }
        }
        events.Sort((a, b) => a.Time.CompareTo(b.Time));
        return events;
    }

    static IEnumerable<DateTime> VisitStarts(int channel, DateTime day, DateTime now)
    {
        var rnd = new Random(channel * 104729 + day.DayOfYear * 13 + day.Year);
        for (var t = day.AddHours(6.5).AddMinutes(rnd.Next(0, 40)); t < day.AddHours(21.5); t = t.AddMinutes(rnd.Next(22, 75)))
            yield return t.AddSeconds(rnd.Next(0, 60));
        yield return day.AddHours(rnd.Next(0, 5)).AddMinutes(rnd.Next(0, 60));
        if (now.Date == day && now - day > TimeSpan.FromMinutes(10))
        {
            var recent = now.AddMinutes(-8);
            yield return new DateTime(recent.Year, recent.Month, recent.Day, recent.Hour, recent.Minute, recent.Second);
        }
    }

    /// <summary>A 640x360 JPEG for a demo picture URI (drawn on the UI thread: WPF drawing objects need it).</summary>
    public static Task<byte[]> PictureAsync(string uri, CancellationToken ct)
    {
        if (!uri.StartsWith(Prefix, StringComparison.Ordinal)) throw new NvrException("Không tải được ảnh này.");
        var parts = uri[Prefix.Length..].Split('/');
        var channel = int.Parse(parts[0], CultureInfo.InvariantCulture);
        var time = DateTime.ParseExact(parts[1], "yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        return Application.Current.Dispatcher.InvokeAsync(() => Draw(channel, time), System.Windows.Threading.DispatcherPriority.Background, ct).Task;
    }

    static byte[] Draw(int channel, DateTime time)
    {
        const int W = 640, H = 360;
        var hue = DemoData.Hue(channel);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var bg = new LinearGradientBrush(FromHsv(hue, 0.55, 0.62), FromHsv(hue, 0.55, 0.26), 35);
            dc.DrawRectangle(bg, null, new Rect(0, 0, W, H));
            // A figure walking through, a little to one side.
            var x = 220 + time.Second % 7 * 30;
            var body = new SolidColorBrush(Color.FromRgb(0x2A, 0x2E, 0x36));
            dc.DrawEllipse(body, null, new Point(x, 150), 22, 24);
            dc.DrawRoundedRectangle(body, null, new Rect(x - 34, 178, 68, 120), 22, 22);
            dc.DrawRectangle(body, null, new Rect(x - 26, 280, 20, 70));
            dc.DrawRectangle(body, null, new Rect(x + 6, 280, 20, 70));
            var dpi = 1.0;
            var face = new Typeface("Segoe UI Semibold");
            var stamp = new FormattedText(time.ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, face, 20, Brushes.White, dpi);
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)), null, new Rect(10, 10, stamp.Width + 12, stamp.Height + 6));
            dc.DrawText(stamp, new Point(16, 13));
            var name = new FormattedText(DemoData.Name(channel), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 20, Brushes.White, dpi);
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)), null, new Rect(W - name.Width - 22, H - name.Height - 16, name.Width + 12, name.Height + 6));
            dc.DrawText(name, new Point(W - name.Width - 16, H - name.Height - 13));
        }
        var bmp = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        var enc = new JpegBitmapEncoder { QualityLevel = 85 };
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    static Color FromHsv(double h, double s, double v)
    {
        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = v - c;
        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }
}
