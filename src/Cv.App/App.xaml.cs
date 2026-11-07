using System.IO;
using System.Runtime;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Cv.App.Platform;
using Cv.Core;
using Cv.Media;

namespace Cv.App;

/// <summary>
/// Composition root: single instance, FFmpeg, settings, recorder client, video, export, updater, probe; --demo swaps
/// in the fakes from Demo/; --capture renders a screenshot and exits.
/// </summary>
public partial class App : Application
{
    const string Name = "Camera";
    Mutex? _single;
    EventWaitHandle? _show;
    Services? _services;
    MainWindow? _window;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var o = AppOptions.Parse(e.Args);

        DispatcherUnhandledException += OnUiException;
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Error("Unhandled exception", ex.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            Log.Warn("Unobserved task exception", ex.Exception);
            ex.SetObserved();
        };

        if (!o.Capture && !TakeSingleInstance(o.Demo))
        {
            Shutdown();
            return;
        }

        Log.Info($"Start {AppVersion.Current}{(o.Demo ? " demo" : "")}{(o.Capture ? " capture " + o.View : "")}");

        if (!o.Demo)
        {
            string? ffError;
            bool ok;
            try
            {
                ok = FFmpegRuntime.TryInitialize(out ffError);
            }
            catch (Exception ex)
            {
                Log.Error("FFmpeg init threw", ex);
                ok = false;
                ffError = null;
            }
            if (!ok)
            {
                Fatal("Không mở được phần phát video của phần mềm. Hãy cài lại phần mềm." + (ffError is null ? "" : "\n\nChi tiết: " + ffError));
                return;
            }
        }

        try
        {
            _services = o.Demo ? Services.CreateDemo() : Services.CreateReal();
        }
        catch (Exception ex)
        {
            Log.Error("Services not created", ex);
            Fatal("Không khởi động được phần mềm. Hãy đóng phần mềm rồi mở lại.\n\nChi tiết: " + ex.Message);
            return;
        }

        _window = new MainWindow(_services);
        MainWindow = _window;
        _window.Closed += (_, _) => Shutdown();
        if (o.Capture) CaptureMode.Prepare(_window, o);
        _window.Show();
        if (o.Capture)
        {
            _ = CaptureMode.RunAsync(_window, o);
            return;
        }
        _window.Start();
        if (o.Updated) _window.Toast($"Đã cập nhật lên phiên bản {AppVersion.Current}.", ToastKind.Ok);

        // Low RAM: hand back the start-up garbage once the first screen is up.
        Dispatcher.BeginInvoke(() =>
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect();
        }, DispatcherPriority.ApplicationIdle);
    }

    /// <summary>Second launch: wake the first window and leave. Demo and CV_DATA copies get their own names.</summary>
    bool TakeSingleInstance(bool demo)
    {
        var data = Environment.GetEnvironmentVariable(AppPaths.DataEnvVar);
        var suffix = (demo ? "-demo" : "") + (string.IsNullOrWhiteSpace(data) ? "" : "-" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(data).ToLowerInvariant())), 0, 4));
        _single = new Mutex(true, $@"Local\CameraViewer-{Environment.UserName}{suffix}", out var first);
        _show = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\CameraViewer-show-{Environment.UserName}{suffix}");
        if (!first)
        {
            try { _show.Set(); } catch (Exception) { }
            _show.Dispose();
            _show = null;
            _single.Dispose();
            _single = null;
            return false;
        }
        var waiter = new Thread(() =>
        {
            try
            {
                while (_show?.WaitOne() == true)
                    Dispatcher.BeginInvoke(() => { if (_window is not null) Native.BringToFront(_window); });
            }
            catch (ObjectDisposedException)
            {
            }
        }) { IsBackground = true, Name = "show-signal" };
        waiter.Start();
        return true;
    }

    void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("UI exception", e.Exception);
        e.Handled = true;
        if (CaptureMode.Active) return;
        MessageBox.Show("Phần mềm gặp lỗi nhỏ và vẫn chạy tiếp. Nếu lỗi lặp lại, hãy đóng rồi mở lại phần mềm.", Name,
            MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    void Fatal(string message)
    {
        if (!CaptureMode.Active) MessageBox.Show(message, Name, MessageBoxButton.OK, MessageBoxImage.Error);
        Shutdown(1);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _services?.Dispose(); } catch (Exception ex) { Log.Warn("Dispose at exit failed", ex); }
        try { _single?.ReleaseMutex(); } catch (Exception) { }
        _single?.Dispose();
        _show?.Dispose();
        Log.Info("Exit");
        base.OnExit(e);
    }
}
