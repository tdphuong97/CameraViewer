namespace Cv.Core;

/// <summary>Where the app keeps its data. The CV_DATA environment variable overrides the data folder (tests, dev).</summary>
public static class AppPaths
{
    public const string DataEnvVar = "CV_DATA";

    public static string Data
    {
        get
        {
            var v = Environment.GetEnvironmentVariable(DataEnvVar);
            return string.IsNullOrWhiteSpace(v)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CameraViewer")
                : v;
        }
    }

    public static string SettingsFile => Path.Combine(Data, "settings.json");
    public static string Logs => Path.Combine(Data, "logs");
    public static string Updates => Path.Combine(Data, "updates");
    /// <summary>Export work files (downloaded PS parts); emptied at start.</summary>
    public static string Temp => Path.Combine(Data, "temp");
    /// <summary>FFmpeg DLLs shipped next to the exe.</summary>
    public static string FFmpeg => Path.Combine(AppContext.BaseDirectory, "ffmpeg");
}
