using System.Diagnostics;
using System.Net.Sockets;

namespace Cv.Media.Tests.Support;

/// <summary>
/// A private MediaMTX on a free port with a reader whose password contains '@', plus GPL ffmpeg publishers looping
/// clips into it. Every process started here is killed on Dispose.
/// </summary>
public sealed class MediaMtx : IDisposable
{
    public const string ReaderUser = "viewer";
    public const string ReaderPassword = "Mock@1234";
    private const string PubUser = "pub", PubPassword = "pub123";

    private readonly string _dir;
    private readonly Process _server;
    private readonly Dictionary<string, Process> _publishers = [];

    public int Port { get; }

    public MediaMtx(string authMethod = "basic")
    {
        _dir = TestEnv.NewTempDir();
        Port = TestEnv.FreeTcpPort();
        var yml = Path.Combine(_dir, "mediamtx.yml");
        File.WriteAllText(yml, $"""
            logLevel: warn
            logDestinations: [stdout]
            api: false
            metrics: false
            pprof: false
            playback: false
            rtsp: true
            rtspTransports: [tcp]
            rtspAddress: 127.0.0.1:{Port}
            rtspAuthMethods: [{authMethod}]
            rtmp: false
            hls: false
            webrtc: false
            srt: false
            moq: false
            authMethod: internal
            authInternalUsers:
              - user: {PubUser}
                pass: {PubPassword}
                ips: []
                permissions:
                  - action: publish
                    path:
              - user: {ReaderUser}
                pass: "{ReaderPassword}"
                ips: []
                permissions:
                  - action: read
                    path:
            paths:
              all_others:
            """);
        _server = Start(TestEnv.MediaMtxExe, $"\"{yml}\"");
        if (!WaitPort(Port, TimeSpan.FromSeconds(10)))
        {
            var log = Output(_server);
            Kill(_server);
            throw new InvalidOperationException("MediaMTX did not start: " + log);
        }
    }

    /// <summary>rtsp URL for readers, password percent-encoded like the app does.</summary>
    public string ReaderUrl(string path) => $"rtsp://{ReaderUser}:{Uri.EscapeDataString(ReaderPassword)}@127.0.0.1:{Port}/{path}";

    /// <summary>Loops a clip into /path at real speed (stream copy).</summary>
    public void Publish(string path, string clip)
    {
        StopPublisher(path);
        var p = Start(TestEnv.GplFFmpegExe,
            $"-hide_banner -loglevel error -re -stream_loop -1 -i \"{clip}\" -map 0:v -c copy -f rtsp -rtsp_transport tcp rtsp://{PubUser}:{PubPassword}@127.0.0.1:{Port}/{path}");
        _publishers[path] = p;
    }

    public void StopPublisher(string path)
    {
        if (_publishers.Remove(path, out var p)) Kill(p);
    }

    private Process Start(string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true,
            WorkingDirectory = _dir,
        };
        var p = new Process { StartInfo = psi };
        var log = new System.Text.StringBuilder();
        p.OutputDataReceived += (_, e) => { lock (log) log.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { lock (log) log.AppendLine(e.Data); };
        Logs[p] = log;
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        return p;
    }

    private readonly Dictionary<Process, System.Text.StringBuilder> Logs = [];

    public string Output(Process p)
    {
        var sb = Logs.GetValueOrDefault(p);
        if (sb is null) return "";
        lock (sb) return sb.ToString();
    }

    public string ServerLog => Output(_server);

    private static bool WaitPort(int port, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            try
            {
                using var c = new TcpClient();
                c.Connect("127.0.0.1", port);
                return true;
            }
            catch (SocketException) { Thread.Sleep(100); }
        }
        return false;
    }

    private static void Kill(Process p)
    {
        try { if (!p.HasExited) { p.Kill(true); p.WaitForExit(5000); } }
        catch (InvalidOperationException) { }
        p.Dispose();
    }

    public void Dispose()
    {
        foreach (var p in _publishers.Values) Kill(p);
        _publishers.Clear();
        Kill(_server);
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }
}
