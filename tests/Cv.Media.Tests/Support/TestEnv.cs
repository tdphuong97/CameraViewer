using System.Diagnostics;
using System.Runtime.InteropServices;
using Cv.Core;

namespace Cv.Media.Tests.Support;

/// <summary>Where the native bits are, and one-time FFmpeg init for the whole test run.</summary>
public static class TestEnv
{
    private static readonly Lazy<string?> InitError = new(() =>
    {
        Log.Folder = Path.Combine(Path.GetTempPath(), "cv-media-tests", "logs");
        if (!FFmpegRuntime.TryInitialize(out var error, FFmpegDir)) return error ?? "FFmpeg init failed";
        return null;
    });

    public static string DevTools => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CameraViewer-dev");

    /// <summary>The LGPL DLLs copied next to the test assembly by Cv.Media.csproj.</summary>
    public static string FFmpegDir => Path.Combine(AppContext.BaseDirectory, "ffmpeg");

    public static string GplFFmpegExe => Path.Combine(DevTools, "mock", "ffmpeg", "bin", "ffmpeg.exe");
    public static string MediaMtxExe => Path.Combine(DevTools, "mock", "mediamtx", "mediamtx.exe");

    public static string Assets => Path.Combine(AppContext.BaseDirectory, "Assets");
    public static string Asset(string name) => Path.Combine(Assets, name);

    public static bool FFmpegReady => InitError.Value is null;

    public static string? SkipNative => InitError.Value is null
        ? null
        : $"FFmpeg DLLs not available in {FFmpegDir} ({InitError.Value}). Run build\\fetch-dev-tools.ps1 and rebuild.";

    public static string? SkipGpl => SkipNative ?? (File.Exists(GplFFmpegExe) ? null : $"GPL ffmpeg.exe missing at {GplFFmpegExe} (build\\fetch-dev-tools.ps1).");

    public static string? SkipMediaMtx => SkipGpl ?? (File.Exists(MediaMtxExe) ? null : $"MediaMTX missing at {MediaMtxExe} (build\\fetch-dev-tools.ps1).");

    public static string NewTempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "cv-media-tests", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    public static int FreeTcpPort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        int port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>Runs the GPL ffmpeg.exe to completion (test asset generation).</summary>
    public static void RunFFmpeg(string args, int timeoutMs = 120_000)
    {
        var psi = new ProcessStartInfo(GplFFmpegExe, "-hide_banner -loglevel error -y " + args)
        {
            UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs)) { p.Kill(true); throw new TimeoutException("ffmpeg " + args); }
        if (p.ExitCode != 0) throw new InvalidOperationException($"ffmpeg failed ({p.ExitCode}): {err.Result}");
    }

    // ---- memory (private bytes = commit; private working set = what Task Manager shows) ----

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCountersEx2
    {
        public uint cb;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
            QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage,
            PrivateWorkingSetSize;
        public ulong SharedCommitUsage;
    }

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool GetProcessMemoryInfo(IntPtr process, ref ProcessMemoryCountersEx2 counters, uint size);

    public static (long PrivateBytes, long PrivateWorkingSet) Memory()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var c = new ProcessMemoryCountersEx2 { cb = (uint)Marshal.SizeOf<ProcessMemoryCountersEx2>() };
        using var self = Process.GetCurrentProcess();
        if (!GetProcessMemoryInfo(self.Handle, ref c, c.cb)) return (self.PrivateMemorySize64, 0);
        return ((long)c.PrivateUsage, (long)c.PrivateWorkingSetSize);
    }
}

public sealed class NativeFactAttribute : FactAttribute
{
    public NativeFactAttribute() { Skip = TestEnv.SkipNative; }
}

public sealed class NativeTheoryAttribute : TheoryAttribute
{
    public NativeTheoryAttribute() { Skip = TestEnv.SkipNative; }
}

public sealed class GplFactAttribute : FactAttribute
{
    public GplFactAttribute() { Skip = TestEnv.SkipGpl; }
}

public sealed class MediaMtxFactAttribute : FactAttribute
{
    public MediaMtxFactAttribute() { Skip = TestEnv.SkipMediaMtx; }
}

/// <summary>Memory measurements: slow, run on demand with CV_MEDIA_PERF=1.</summary>
public sealed class PerfFactAttribute : FactAttribute
{
    public PerfFactAttribute()
    {
        Skip = Environment.GetEnvironmentVariable("CV_MEDIA_PERF") == "1" ? TestEnv.SkipGpl : "Memory measurement: set CV_MEDIA_PERF=1 to run.";
    }
}
