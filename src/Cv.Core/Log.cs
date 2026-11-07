using System.Text;
using System.Text.RegularExpressions;

namespace Cv.Core;

/// <summary>
/// Plain text log, one file per day in AppPaths.Logs (app-yyyyMMdd.log), 14 days kept. Thread-safe, never throws.
/// Never log a password: messages and exceptions pass through Redact.
/// </summary>
public static partial class Log
{
    private static readonly object Gate = new();
    private static string? _folder;
    private static bool _pruned;

    /// <summary>Overrides the folder (tests). Default AppPaths.Logs.</summary>
    public static string Folder { get => _folder ?? AppPaths.Logs; set => _folder = value; }

    public static void Info(string message) => Write("INFO", message, null);
    public static void Warn(string message, Exception? ex = null) => Write("WARN", message, ex);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    /// <summary>rtsp://user:secret@host/... becomes rtsp://user:***@host/...; password=... query values become ***.</summary>
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        text = UserInfo().Replace(text, m => m.Groups[1].Value + m.Groups[2].Value + ":***@");
        return PasswordQuery().Replace(text, m => m.Groups[1].Value + "***");
    }

    private static void Write(string level, string message, Exception? ex)
    {
        try
        {
            var now = DateTime.Now;
            var sb = new StringBuilder();
            sb.Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(' ').Append(level).Append(' ').Append(Redact(message));
            if (ex is not null) sb.AppendLine().Append(Redact(ex.ToString()));
            sb.AppendLine();
            lock (Gate)
            {
                Directory.CreateDirectory(Folder);
                File.AppendAllText(Path.Combine(Folder, $"app-{now:yyyyMMdd}.log"), sb.ToString(), Encoding.UTF8);
                if (!_pruned)
                {
                    _pruned = true;
                    foreach (var f in Directory.EnumerateFiles(Folder, "app-*.log"))
                        if (File.GetLastWriteTime(f) < now.AddDays(-14)) File.Delete(f);
                }
            }
        }
        catch
        {
            // Logging must never break the app.
        }
    }

    [GeneratedRegex(@"(\b[a-z][a-z0-9+.-]*://)([^:/@\s]+):[^@\s/]*@", RegexOptions.IgnoreCase)]
    private static partial Regex UserInfo();

    [GeneratedRegex(@"((?:password|pwd|pass)=)[^&\s""<]*", RegexOptions.IgnoreCase)]
    private static partial Regex PasswordQuery();
}
