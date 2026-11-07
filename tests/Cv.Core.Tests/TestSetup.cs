using System.Runtime.CompilerServices;

namespace Cv.Core.Tests;

internal static class TestSetup
{
    public static readonly string Root = Path.Combine(Path.GetTempPath(), "cv-core-tests");

    /// <summary>Keeps logs and app data of the tests away from the user's real data folder.</summary>
    [ModuleInitializer]
    internal static void Init()
    {
        Directory.CreateDirectory(Root);
        Environment.SetEnvironmentVariable(AppPaths.DataEnvVar, Path.Combine(Root, "data"));
        Log.Folder = Path.Combine(Root, "logs");
    }
}

/// <summary>A fresh folder deleted at the end of the test.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(TestSetup.Root, Guid.NewGuid().ToString("N")[..12]);

    public TempDir() => Directory.CreateDirectory(Path);

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Polls until a condition holds (background workers, event pump).</summary>
public static class Wait
{
    public static async Task For(Func<bool> condition, int timeoutMs = 5000, string? what = null)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("Timed out waiting for " + (what ?? "condition"));
            await Task.Delay(10);
        }
    }
}
