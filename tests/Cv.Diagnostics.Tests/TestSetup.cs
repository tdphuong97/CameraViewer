using System.Runtime.CompilerServices;
using Cv.Core;

namespace Cv.Diagnostics.Tests;

internal static class TestSetup
{
    public static readonly string Root = Path.Combine(Path.GetTempPath(), "cv-diagnostics-tests");

    /// <summary>Keeps logs and app data of the tests away from the user's real data folder.</summary>
    [ModuleInitializer]
    internal static void Init()
    {
        Directory.CreateDirectory(Root);
        Environment.SetEnvironmentVariable(AppPaths.DataEnvVar, Path.Combine(Root, "data"));
        Log.Folder = Path.Combine(Root, "logs");
    }
}
