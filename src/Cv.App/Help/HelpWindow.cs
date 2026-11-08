using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Cv.App.Platform;
using Cv.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Cv.App.Help;

/// <summary>
/// The user guide (docs\ built into help\huong-dan.html) in its own window over the app, opened on the page about the
/// screen in use. WebView2 (part of Windows) shows the offline file; links to the web open in the browser.
/// Nothing of WebView2 loads before the first open, and closing the window ends its browser processes (RAM rule).
/// </summary>
internal sealed class HelpWindow : Window
{
    public const string HostName = "help.cameraviewer";
    public const string FileName = "huong-dan.html";

    static HelpWindow? _open;

    readonly WebView2 _web = new();
    readonly Action _fallback;
    string _page;

    public static string Folder => Path.Combine(AppContext.BaseDirectory, "help");
    public static string FilePath => Path.Combine(Folder, FileName);

    /// <summary>The address of a guide page, "" for the home page.</summary>
    public static string Url(string page) => $"https://{HostName}/{FileName}#/{page}";

    /// <summary>True when the WebView2 runtime is installed (Windows 11 and updated Windows 10 have it).</summary>
    public static bool RuntimeAvailable()
    {
        try
        {
            return !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString());
        }
        catch (Exception ex)
        {
            Log.Warn("WebView2 runtime not found", ex);
            return false;
        }
    }

    /// <summary>Opens the guide on <paramref name="page"/>, or turns the open guide to it. <paramref name="fallback"/> runs
    /// when WebView2 cannot start (the caller then opens the file in the browser).</summary>
    public static void Open(Window owner, string page, Action fallback)
    {
        if (_open is { } w)
        {
            w.Go(page);
            if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
            w.Activate();
            return;
        }
        _open = new HelpWindow(owner, page, fallback);
        _open.Show();
    }

    HelpWindow(Window owner, string page, Action fallback)
    {
        _page = page;
        _fallback = fallback;
        Owner = owner;
        Title = "Hướng dẫn sử dụng";
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Width = Math.Clamp(owner.ActualWidth - 120, 760, 1240);
        Height = Math.Clamp(owner.ActualHeight - 60, 520, 1000);
        MinWidth = 600;
        MinHeight = 420;
        Background = new SolidColorBrush(Color.FromRgb(0x0E, 0x11, 0x16));
        UseLayoutRounding = true;
        _web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x0E, 0x11, 0x16);
        Content = _web;

        SourceInitialized += (_, _) => Native.DarkTitleBar(this, 0x171A21, 0xE6E8EC);
        Loaded += OnLoaded;
        // F1 inside the guide is already where it leads.
        PreviewKeyDown += (_, e) => { if (e.Key == Key.F1) e.Handled = true; };
        Closed += (_, _) =>
        {
            _open = null;
            _web.Dispose();
        };
    }

    void Go(string page)
    {
        _page = page;
        _web.CoreWebView2?.Navigate(Url(page));
    }

    async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            // A fresh environment each time: the previous one ended with its window.
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(AppPaths.Data, "WebView2"));
            await _web.EnsureCoreWebView2Async(env);
            var core = _web.CoreWebView2;
            var s = core.Settings;
            s.AreDefaultContextMenusEnabled = false;
            s.IsStatusBarEnabled = false;
            s.AreBrowserAcceleratorKeysEnabled = false;
            s.IsPasswordAutosaveEnabled = false;
            s.IsGeneralAutofillEnabled = false;
#if !DEBUG
            s.AreDevToolsEnabled = false;
#endif
            // Dark like the app, until the reader picks a theme in the guide (it remembers the choice).
            core.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Dark;
            core.SetVirtualHostNameToFolderMapping(HostName, Folder, CoreWebView2HostResourceAccessKind.Allow);
            core.NewWindowRequested += (_, a) =>
            {
                a.Handled = true;
                OpenOutside(a.Uri);
            };
            core.NavigationStarting += (_, a) =>
            {
                if (IsGuide(a.Uri)) return;
                a.Cancel = true;
                OpenOutside(a.Uri);
            };
            core.NavigationCompleted += (_, a) => TestNavigated.TrySetResult(a.IsSuccess);
            core.Navigate(Url(_page));
        }
        catch (Exception ex)
        {
            Log.Warn("Help window: WebView2 did not start, opening the guide in the browser", ex);
            TestNavigated.TrySetResult(false);
            Close();
            _fallback();
        }
    }

    internal static bool IsGuide(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var u)
        && u.Scheme == Uri.UriSchemeHttps
        && string.Equals(u.Host, HostName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Links out of the guide (GitHub, releases) open in the user's browser; nothing else is followed.</summary>
    static void OpenOutside(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u)) return;
        if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeMailto) return;
        var error = Shell.OpenUrl(u.AbsoluteUri);
        if (error is not null) Log.Warn("Cannot open link from the guide: " + u.AbsoluteUri);
    }

    // ---- Self-test hooks ----

    internal static HelpWindow? Current => _open;
    internal readonly TaskCompletionSource<bool> TestNavigated = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal int TestBrowserProcessId => _web.CoreWebView2?.BrowserProcessId is { } id ? (int)id : 0;
    internal async Task<string> TestScriptAsync(string js) => _web.CoreWebView2 is { } c ? await c.ExecuteScriptAsync(js) : "";

    internal async Task TestCaptureAsync(string png)
    {
        if (_web.CoreWebView2 is not { } c) return;
        await using var fs = File.Create(png);
        await c.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, fs);
    }
}
