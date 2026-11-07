using System.Globalization;

namespace MockNvr;

/// <summary>How the mock writes times in search results and playback URIs (see the design doc, Hikvision facts).</summary>
public enum TimeWriteMode
{
    /// <summary>Device local wall clock with a misleading "Z" (most NVR firmware).</summary>
    LocalZ,
    /// <summary>Real UTC with "Z" (some newer firmware).</summary>
    Utc,
    /// <summary>Local wall clock with an explicit offset such as +07:00 in search results; URIs stay local with "Z".</summary>
    Offset,
}

/// <summary>How ISAPI download answers.</summary>
public enum DownloadMode
{
    /// <summary>Cuts exactly the requested range, for time-only and segment URIs.</summary>
    Honor,
    /// <summary>Refuses time-only URIs; returns the whole segment file for segment URIs (some firmware).</summary>
    Whole,
    /// <summary>Answers every download with HTTP 200 and an XML error body (download not supported).</summary>
    XmlError,
}

public enum VideoCodec { H264, H265 }

public sealed class MockOptions
{
    public string Bind { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 8080;
    public string User { get; set; } = "admin";
    public string Password { get; set; } = "Mock@1234";
    public TimeWriteMode TimeMode { get; set; } = TimeWriteMode.LocalZ;
    public DownloadMode Download { get; set; } = DownloadMode.Honor;
    public VideoCodec DownloadCodec { get; set; } = VideoCodec.H265;
    /// <summary>Folder with the cached elementary streams (es-h265.hevc, es-h264.h264).</summary>
    public string? DataDir { get; set; }
    /// <summary>Only reported in /ISAPI/Security/adminAccesses; the RTSP server is MediaMTX.</summary>
    public int RtspPort { get; set; } = 554;
    /// <summary>Device time zone. Default: this PC's offset, so the drawtext clock of the test streams matches.</summary>
    public TimeSpan UtcOffset { get; set; } = TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow);
    /// <summary>Device clock minus real time (a recorder whose clock drifted).</summary>
    public TimeSpan ClockSkew { get; set; }
    public int MaxFailedLogins { get; set; } = 5;
    public TimeSpan LockDuration { get; set; } = TimeSpan.FromMinutes(3);
    /// <summary>Measured on Hikvision firmware: a nonce works ~2 s and is refused at ~3.4 s.</summary>
    public TimeSpan NonceLifetime { get; set; } = TimeSpan.FromSeconds(3);
    /// <summary>false: an expired nonce gets a bare 401 (no challenge), as seen on Hikvision. true: RFC stale="TRUE" challenge.</summary>
    public bool StaleChallenge { get; set; }
    /// <summary>Download throughput cap in Mbit/s; 0 = as fast as possible.</summary>
    public double DownloadMbps { get; set; }
    /// <summary>Adds a G.711 µ-law track to downloads, like most Hikvision recordings.</summary>
    public bool Audio { get; set; } = true;
    public string Realm { get; set; } = MockDevice.Model;
    public bool Quiet { get; set; }

    public const string Usage = """
        MockNvr: fake Hikvision DS-7732NI-M4 recorder (ISAPI over HTTP digest) for Camera Viewer tests.

          --bind <ip>              listen address (default 127.0.0.1; 0.0.0.0 for the LAN)
          --port <n>               HTTP port (default 8080)
          --user <name>            login (default admin)
          --password <text>        password (default Mock@1234)
          --time-mode <m>          localz | utc | offset (default localz)
          --download <m>           honor | whole | xml-error (default honor)
          --download-codec <c>     h265 | h264 (default h265)
          --download-mbps <n>      throttle downloads (default 0 = no limit)
          --no-audio               downloads without the G.711 track
          --data <folder>          cached elementary streams (default <repo>\tools\mock\data)
          --rtsp-port <n>          RTSP port reported by the device (default 554)
          --utc-offset <+hh:mm>    device time zone (default: this PC's)
          --clock-skew <seconds>   device clock minus real time (default 0)
          --lock-minutes <n>       lock-out after 5 wrong passwords lasts this long (default 3)
          --nonce-seconds <n>      digest nonce lifetime (default 3)
          --stale-challenge        answer expired nonces with stale="TRUE" instead of a bare 401
          --realm <text>           digest realm (default the model)
          --quiet                  no request log
        """;

    /// <summary>Parses the command line; null + error on a bad option, null + no error for --help.</summary>
    public static MockOptions? Parse(string[] args, out string? error)
    {
        error = null;
        var o = new MockOptions();
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            string? inline = null;
            var eq = a.IndexOf('=');
            if (a.StartsWith("--") && eq > 0) { inline = a[(eq + 1)..]; a = a[..eq]; }
            string Value()
            {
                if (inline is not null) return inline;
                if (i + 1 >= args.Length) throw new FormatException($"{a} needs a value");
                return args[++i];
            }
            try
            {
                switch (a.ToLowerInvariant())
                {
                    case "-h": case "--help": case "/?": return null;
                    case "--bind": o.Bind = Value(); break;
                    case "--port": o.Port = int.Parse(Value(), CultureInfo.InvariantCulture); break;
                    case "--user": o.User = Value(); break;
                    case "--password": o.Password = Value(); break;
                    case "--time-mode":
                        o.TimeMode = Value().ToLowerInvariant() switch
                        {
                            "localz" or "local" => TimeWriteMode.LocalZ,
                            "utc" => TimeWriteMode.Utc,
                            "offset" => TimeWriteMode.Offset,
                            var v => throw new FormatException($"unknown time mode '{v}'"),
                        };
                        break;
                    case "--download":
                        o.Download = Value().ToLowerInvariant() switch
                        {
                            "honor" => DownloadMode.Honor,
                            "whole" => DownloadMode.Whole,
                            "xml-error" or "xmlerror" => DownloadMode.XmlError,
                            var v => throw new FormatException($"unknown download mode '{v}'"),
                        };
                        break;
                    case "--download-codec":
                        o.DownloadCodec = Value().ToLowerInvariant() switch
                        {
                            "h265" or "hevc" => VideoCodec.H265,
                            "h264" or "avc" => VideoCodec.H264,
                            var v => throw new FormatException($"unknown codec '{v}'"),
                        };
                        break;
                    case "--download-mbps": o.DownloadMbps = double.Parse(Value(), CultureInfo.InvariantCulture); break;
                    case "--no-audio": o.Audio = false; break;
                    case "--data": o.DataDir = Value(); break;
                    case "--rtsp-port": o.RtspPort = int.Parse(Value(), CultureInfo.InvariantCulture); break;
                    case "--utc-offset": o.UtcOffset = ParseOffset(Value()); break;
                    case "--clock-skew": o.ClockSkew = TimeSpan.FromSeconds(double.Parse(Value(), CultureInfo.InvariantCulture)); break;
                    case "--lock-minutes": o.LockDuration = TimeSpan.FromMinutes(double.Parse(Value(), CultureInfo.InvariantCulture)); break;
                    case "--nonce-seconds": o.NonceLifetime = TimeSpan.FromSeconds(double.Parse(Value(), CultureInfo.InvariantCulture)); break;
                    case "--stale-challenge": o.StaleChallenge = true; break;
                    case "--realm": o.Realm = Value(); break;
                    case "--quiet": o.Quiet = true; break;
                    default: throw new FormatException($"unknown option '{args[i]}'");
                }
            }
            catch (FormatException ex) { error = ex.Message; return null; }
            catch (OverflowException ex) { error = ex.Message; return null; }
        }
        return o;
    }

    /// <summary>"+07:00", "-05:30", "7", "+0700".</summary>
    public static TimeSpan ParseOffset(string s)
    {
        s = s.Trim();
        var sign = 1;
        if (s.StartsWith('+')) s = s[1..];
        else if (s.StartsWith('-')) { sign = -1; s = s[1..]; }
        if (s.Length == 4 && !s.Contains(':')) s = s[..2] + ":" + s[2..];
        var parts = s.Split(':');
        var h = int.Parse(parts[0], CultureInfo.InvariantCulture);
        var m = parts.Length > 1 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 0;
        if (h > 14 || m > 59) throw new FormatException($"bad UTC offset '{s}'");
        return sign * new TimeSpan(h, m, 0);
    }

    /// <summary>Default data folder: tools\mock\data of the repo this binary was built from, else .\data.</summary>
    public string ResolveDataDir()
    {
        if (!string.IsNullOrWhiteSpace(DataDir)) return Path.GetFullPath(DataDir);
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "tools", "mock");
            if (Directory.Exists(candidate)) return Path.Combine(candidate, "data");
        }
        return Path.Combine(Directory.GetCurrentDirectory(), "data");
    }
}
