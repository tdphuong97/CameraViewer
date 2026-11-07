using System.Runtime.CompilerServices;
using Cv.Core;

namespace MockNvr.Tests;

internal static class TestSetup
{
    public static readonly string Root = Path.Combine(Path.GetTempPath(), "mocknvr-tests");

    /// <summary>Keeps the logs and app data of the client under test away from the user's real data folder.</summary>
    [ModuleInitializer]
    internal static void Init()
    {
        Directory.CreateDirectory(Root);
        Environment.SetEnvironmentVariable(AppPaths.DataEnvVar, Path.Combine(Root, "data"));
        Log.Folder = Path.Combine(Root, "logs");
    }
}
