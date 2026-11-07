using System.Diagnostics;
using System.IO;
using Cv.Core;

namespace Cv.App.Platform;

/// <summary>Opens files and folders with Windows. Returns a Vietnamese message on failure, null on success.</summary>
internal static class Shell
{
    public static string? OpenFile(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return "Không tìm thấy tệp. Có thể tệp đã bị xóa hoặc chuyển đi.";
        return Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    /// <summary>Opens the folder (created when missing).</summary>
    public static string? OpenFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex)
        {
            Log.Warn("Cannot create folder " + folder, ex);
            return "Không mở được thư mục này.";
        }
        return Start(new ProcessStartInfo("explorer.exe", Quote(folder)) { UseShellExecute = true });
    }

    /// <summary>Opens the folder of a file with the file selected (falls back to the folder).</summary>
    public static string? ShowInFolder(string? path, string fallbackFolder)
    {
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
            return Start(new ProcessStartInfo("explorer.exe", "/select," + Quote(path)) { UseShellExecute = true });
        var folder = string.IsNullOrEmpty(path) ? fallbackFolder : Path.GetDirectoryName(path) ?? fallbackFolder;
        return OpenFolder(folder);
    }

    static string Quote(string s) => "\"" + s + "\"";

    static string? Start(ProcessStartInfo psi)
    {
        try
        {
            Process.Start(psi)?.Dispose();
            return null;
        }
        catch (Exception ex)
        {
            Log.Warn("Shell open failed: " + psi.FileName, ex);
            return "Windows không mở được mục này.";
        }
    }

    /// <summary>A file name made of the camera name and a time, safe for Windows, with " (2)" added when taken.</summary>
    public static string UniquePath(string folder, string baseName, string extension)
    {
        baseName = Cv.Core.Export.ExportNaming.Sanitize(baseName);
        var path = Path.Combine(folder, baseName + extension);
        for (var i = 2; File.Exists(path); i++) path = Path.Combine(folder, $"{baseName} ({i}){extension}");
        return path;
    }
}
