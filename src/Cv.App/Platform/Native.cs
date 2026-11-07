using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Cv.App.Platform;

/// <summary>Small Win32 helpers: dark system title bar, taskbar flash, bring to front.</summary>
internal static partial class Native
{
    const int DwmwaUseImmersiveDarkMode = 20;
    const int DwmwaCaptionColor = 35;
    const int DwmwaTextColor = 36;

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Dark title bar in the panel colour (keeps the system bar, so snap and maximize behave normally).</summary>
    public static void DarkTitleBar(Window w, uint captionRgb, uint textRgb)
    {
        try
        {
            var hwnd = new WindowInteropHelper(w).EnsureHandle();
            var on = 1;
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref on, sizeof(int));
            var caption = ToColorRef(captionRgb);
            DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref caption, sizeof(int));
            var text = ToColorRef(textRgb);
            DwmSetWindowAttribute(hwnd, DwmwaTextColor, ref text, sizeof(int));
        }
        catch (Exception)
        {
            // Older Windows: the default title bar is fine.
        }
    }

    static int ToColorRef(uint rgb) => (int)(((rgb & 0xFF) << 16) | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF));

    [StructLayout(LayoutKind.Sequential)]
    struct FlashInfo
    {
        public uint Size;
        public IntPtr Hwnd;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FlashWindowEx(ref FlashInfo info);

    /// <summary>Flashes the taskbar button until the window comes to the foreground.</summary>
    public static void FlashTaskbar(Window w)
    {
        try
        {
            var info = new FlashInfo
            {
                Size = (uint)Marshal.SizeOf<FlashInfo>(),
                Hwnd = new WindowInteropHelper(w).Handle,
                Flags = 0x2 | 0xC, // FLASHW_TRAY | FLASHW_TIMERNOFG
                Count = uint.MaxValue,
            };
            FlashWindowEx(ref info);
        }
        catch (Exception)
        {
        }
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr hwnd);

    /// <summary>Restores and activates the window (second launch of the app).</summary>
    public static void BringToFront(Window w)
    {
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Show();
        w.Activate();
        // Topmost toggle defeats the foreground lock when another app is active.
        w.Topmost = true;
        w.Topmost = false;
        SetForegroundWindow(new WindowInteropHelper(w).Handle);
    }
}
