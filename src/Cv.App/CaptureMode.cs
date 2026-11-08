using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cv.Core;

namespace Cv.App;

/// <summary>
/// --capture: lays the window out at exactly the requested size (off screen, no frame), opens the requested view,
/// waits, renders the window content to PNG and exits. For screenshots in reviews.
/// </summary>
internal static class CaptureMode
{
    public static bool Active { get; private set; }

    public static void Prepare(MainWindow w, AppOptions o)
    {
        Active = true;
        w.WindowStyle = WindowStyle.None;
        w.ResizeMode = ResizeMode.NoResize;
        w.ShowInTaskbar = false;
        w.ShowActivated = false;
        w.WindowState = WindowState.Normal;
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.MinWidth = w.MinHeight = 0;
        w.Width = o.Width;
        w.Height = o.Height;
        w.Left = -o.Width - 200;
        w.Top = 0;
    }

    public static async Task RunAsync(MainWindow w, AppOptions o)
    {
        var code = 0;
        try
        {
            switch (o.View)
            {
                case "single":
                    w.LivePage.ShowSingleWhenReady(1);
                    w.ShowLive();
                    break;
                case "playback":
                    w.ShowPlaybackTab();
                    break;
                case "export":
                    w.ShowPlaybackTab();
                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(1.5, o.DelaySeconds / 2)));
                    w.PlaybackPage.OpenSavePanel();
                    break;
                case "people":
                    w.ShowPlaybackTab();
                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(1.5, o.DelaySeconds / 2)));
                    w.PlaybackPage.OpenPeoplePanel();
                    break;
                case "update":
                    w.ShowLive();
                    await w.CheckUpdateAsync(manual: false);
                    break;
                case "saved":
                    w.ShowSaved();
                    break;
                case "firstrun":
                    w.SettingsPage.ForceFirstRun = true;
                    w.ShowSettings();
                    break;
                case "settings":
                    w.ShowSettings();
                    break;
                default:
                    w.ShowLive();
                    break;
            }
            await Task.Delay(TimeSpan.FromSeconds(o.DelaySeconds));
            if (o.View is "live" or "update") w.LivePage.ShowHoverOnFirstTile();
            await Task.Delay(200);
            Save(w.Root, o.Width, o.Height, o.CaptureFile!);
            Log.Info($"Capture saved: {o.CaptureFile} ({o.View}, {o.Width}x{o.Height}, content {w.Root.ActualWidth}x{w.Root.ActualHeight})");
        }
        catch (Exception ex)
        {
            Log.Error("Capture failed", ex);
            code = 1;
        }
        w.Close();
        Application.Current.Shutdown(code);
    }

    static void Save(FrameworkElement root, int width, int height, string file)
    {
        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
            dc.DrawRectangle(new VisualBrush(root) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        var dir = Path.GetDirectoryName(Path.GetFullPath(file));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        using var fs = File.Create(file);
        enc.Save(fs);
    }
}
