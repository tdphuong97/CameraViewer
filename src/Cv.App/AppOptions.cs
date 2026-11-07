using System.Globalization;

namespace Cv.App;

/// <summary>
/// Command line: --demo, --updated, and the screenshot hook
/// --capture &lt;file.png&gt; [--view live|single|playback|export|saved|settings|firstrun|update] [--size 1600x900] [--delay 3].
/// </summary>
public sealed class AppOptions
{
    public bool Demo { get; private init; }
    public bool Updated { get; private init; }
    /// <summary>Demo only: scripted walk through every screen, results in the log, exit code = failures.</summary>
    public bool SelfTest { get; private init; }
    public string? CaptureFile { get; private init; }
    public string View { get; private init; } = "live";
    public int Width { get; private init; } = 1600;
    public int Height { get; private init; } = 900;
    public double DelaySeconds { get; private init; } = 3;

    public bool Capture => CaptureFile is not null;

    public static AppOptions Parse(IReadOnlyList<string> args)
    {
        bool demo = false, updated = false, selfTest = false;
        string? file = null;
        string view = "live";
        int w = 1600, h = 900;
        double delay = 3;
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i].ToLowerInvariant();
            string? Next() => i + 1 < args.Count ? args[++i] : null;
            switch (a)
            {
                case "--demo": demo = true; break;
                case "--updated": updated = true; break;
                case "--selftest": selfTest = true; break;
                case "--capture": file = Next(); break;
                case "--view": view = (Next() ?? "live").ToLowerInvariant(); break;
                case "--delay":
                    if (double.TryParse(Next(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d >= 0) delay = d;
                    break;
                case "--size":
                    var parts = (Next() ?? "").ToLowerInvariant().Split('x');
                    if (parts.Length == 2 && int.TryParse(parts[0], CultureInfo.InvariantCulture, out var pw) &&
                        int.TryParse(parts[1], CultureInfo.InvariantCulture, out var ph) && pw >= 320 && ph >= 240)
                    {
                        w = pw;
                        h = ph;
                    }
                    break;
            }
        }
        return new AppOptions { Demo = demo, Updated = updated, SelfTest = selfTest && demo, CaptureFile = file, View = view, Width = w, Height = h, DelaySeconds = delay };
    }
}
