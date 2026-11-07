using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Cv.Core;
using FFmpeg.AutoGen;

namespace Cv.Media;

/// <summary>Loads the FFmpeg DLLs. Call once at app start, before any other Cv.Media type.</summary>
public static unsafe partial class FFmpegRuntime
{
    private static readonly object Gate = new();
    private static string? _version;

    /// <summary>Loads FFmpeg from <paramref name="folder"/> (default Cv.Core.AppPaths.FFmpeg). False + a Vietnamese error when missing or broken.</summary>
    public static bool TryInitialize(out string? error, string? folder = null)
    {
        lock (Gate)
        {
            error = null;
            if (_version is not null) return true;
            folder = Path.GetFullPath(folder ?? AppPaths.FFmpeg);
            if (!ValidateFolder(folder, out error)) { Log.Error("FFmpeg: " + error + " (" + folder + ")"); return false; }
            try
            {
                ffmpeg.RootPath = folder;
                // Each call loads its library; a wrong major version means an ABI mismatch with the bindings.
                CheckMajor("avutil", ffmpeg.avutil_version(), ffmpeg.LIBAVUTIL_VERSION_MAJOR);
                CheckMajor("avcodec", ffmpeg.avcodec_version(), ffmpeg.LIBAVCODEC_VERSION_MAJOR);
                CheckMajor("avformat", ffmpeg.avformat_version(), ffmpeg.LIBAVFORMAT_VERSION_MAJOR);
                CheckMajor("swscale", ffmpeg.swscale_version(), ffmpeg.LIBSWSCALE_VERSION_MAJOR);

                ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);
                var cb = new av_log_set_callback_callback_func { Pointer = (IntPtr)(delegate* unmanaged[Cdecl]<void*, int, byte*, byte*, void>)&OnLog };
                ffmpeg.av_log_set_callback(cb);
                ffmpeg.avformat_network_init();

                var info = ffmpeg.av_version_info() ?? "";
                var m = VersionRegex().Match(info);
                _version = m.Success ? m.Value : $"{ffmpeg.LIBAVCODEC_VERSION_MAJOR}";
                Log.Info($"FFmpeg {info} loaded from {folder}");
                return true;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or NotSupportedException or InvalidOperationException)
            {
                error = "Thư viện FFmpeg bị hỏng hoặc sai phiên bản. Hãy cài lại ứng dụng.";
                Log.Error("FFmpeg load failed from " + folder, ex);
                return false;
            }
        }
    }

    /// <summary>e.g. "9.0"; null before TryInitialize succeeded.</summary>
    public static string? Version => _version;

    internal static bool IsInitialized => _version is not null;

    /// <summary>Throws with the Vietnamese message when FFmpeg cannot be loaded from the default folder.</summary>
    internal static void EnsureInitialized()
    {
        if (_version is not null) return;
        if (!TryInitialize(out var error)) throw new InvalidOperationException(error);
    }

    /// <summary>Checks the expected DLL names exist (no loading).</summary>
    internal static bool ValidateFolder(string folder, out string? error)
    {
        error = null;
        string Expected(string lib) => Path.Combine(folder, $"{lib}-{ffmpeg.LibraryVersionMap[lib]}.dll");
        string[] libs = ["avutil", "swresample", "avcodec", "avformat", "swscale"];
        if (libs.All(l => File.Exists(Expected(l)))) return true;

        var other = Directory.Exists(folder) ? Directory.GetFiles(folder, "avcodec-*.dll") : [];
        error = other.Length > 0
            ? "Thư viện FFmpeg sai phiên bản. Hãy cài lại ứng dụng."
            : "Thiếu thư viện FFmpeg (thư mục ffmpeg cạnh chương trình). Hãy cài lại ứng dụng.";
        return false;
    }

    private static void CheckMajor(string lib, uint version, int expected)
    {
        if ((int)(version >> 16) != expected)
            throw new InvalidOperationException($"{lib} major {version >> 16}, bindings need {expected}");
    }

    [GeneratedRegex(@"\d+\.\d+")]
    private static partial Regex VersionRegex();

    // ---- log bridge: FFmpeg calls this from any thread, for every level (the level filter is ours) ----

    private static readonly object LogGate = new();
    private static long _windowStart;
    private static int _windowCount;
    private static int _suppressed;
    private static string? _lastLine;
    private const int MaxLinesPerWindow = 20;
    private static readonly long Window = System.Diagnostics.Stopwatch.Frequency * 60;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnLog(void* avcl, int level, byte* fmt, byte* vl)
    {
        try
        {
            if (level > ffmpeg.AV_LOG_ERROR || fmt == null) return;
            const int size = 1024;
            byte* line = stackalloc byte[size];
            int printPrefix = 1;
            ffmpeg.av_log_format_line2(avcl, level, Marshal.PtrToStringUTF8((IntPtr)fmt), vl, line, size, &printPrefix);
            var text = Marshal.PtrToStringUTF8((IntPtr)line)?.Trim();
            if (string.IsNullOrEmpty(text)) return;
            WriteLimited(text, level <= ffmpeg.AV_LOG_FATAL);
        }
        catch
        {
            // Never throw back into native code.
        }
    }

    internal static void WriteLimited(string text, bool fatal)
    {
        int skipped;
        lock (LogGate)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (now - _windowStart > Window) { _windowStart = now; _windowCount = 0; }
            // Repeated lines (one per corrupt frame) and floods only count.
            if (text == _lastLine || _windowCount >= MaxLinesPerWindow) { _suppressed++; return; }
            _lastLine = text;
            _windowCount++;
            skipped = _suppressed;
            _suppressed = 0;
        }
        var msg = "FFmpeg: " + Log.Redact(text) + (skipped > 0 ? $" ({skipped} similar lines skipped)" : "");
        if (fatal) Log.Error(msg); else Log.Warn(msg);
    }
}
