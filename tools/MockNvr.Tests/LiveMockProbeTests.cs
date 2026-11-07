using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Cv.Core;
using Cv.Core.Export;
using Cv.Core.Hik;
using Cv.Core.Nvr;
using Cv.Core.Video;
using Cv.Diagnostics;
using Xunit.Abstractions;

namespace MockNvr.Tests;

/// <summary>
/// The probe against the mock started by tools\mock\start-mock.ps1 (MediaMTX + MockNvr), with the real Cv.Core client
/// and FFmpeg command-line stand-ins for the media layer. Opt-in: set CV_MOCK_E2E=1 with the mock running.
/// </summary>
public sealed class LiveMockProbeTests(ITestOutputHelper output)
{
    private static readonly string Dev = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CameraViewer-dev", "ffmpeg", "bin");

    [Fact]
    public async Task Probe_against_the_running_mock_with_real_ffmpeg()
    {
        var state = Path.Combine(RepoRoot(), "tools", "mock", "data", "mock-state.json");
        if (Environment.GetEnvironmentVariable("CV_MOCK_E2E") != "1" || !File.Exists(state) || !File.Exists(Path.Combine(Dev, "ffprobe.exe")))
        {
            output.WriteLine("Skipped: set CV_MOCK_E2E=1 and run tools\\mock\\start-mock.ps1 first.");
            return;
        }
        using var doc = JsonDocument.Parse(File.ReadAllText(state));
        var s = doc.RootElement;
        var config = new NvrConfig
        {
            Host = "127.0.0.1", HttpPort = s.GetProperty("httpPort").GetInt32(), RtspPort = s.GetProperty("rtspPort").GetInt32(),
            User = s.GetProperty("user").GetString()!,
        };
        var password = s.GetProperty("password").GetString()!;
        var mode = s.GetProperty("download").GetString();
        var folder = Path.Combine(Path.GetTempPath(), "mocknvr-live-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var report = await new ProbeRunner(new HikNvrClientFactory(), new CliVideoFactory(), new CliMedia())
                .RunAsync(config, password, folder, new Progress<string>(), CancellationToken.None);

            output.WriteLine(report.Text);
            Assert.DoesNotContain(password, report.Text);
            Assert.DoesNotContain(Uri.EscapeDataString(password), report.Text);
            foreach (var step in new[] { "Xem trực tiếp luồng phụ", "Xem trực tiếp luồng chính", "Xem lại qua RTSP", "Ghi lại" })
                Assert.Contains(report.Text.Split('\n'), l => l.Contains(step) && l.Contains("[PASS]"));
            if (mode == "honor") Assert.True(report.Passed, report.Text);
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }

    private static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "tools", "mock"))) return d.FullName;
        return Directory.GetCurrentDirectory();
    }

    internal static async Task<(int Code, string Out, string Err)> RunAsync(string exe, IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(Path.Combine(Dev, exe)) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        try { await p.WaitForExitAsync(ct); }
        catch (OperationCanceledException) { p.Kill(true); throw; }
        return (p.ExitCode, await stdout, await stderr);
    }

    /// <summary>"Plays" a stream by probing it once with ffprobe; then a new frame on every poll.</summary>
    private sealed class CliVideoFactory : IVideoSourceFactory
    {
        public IVideoSource Create(VideoSourceOptions options) => new Source(options);

        private sealed class Source(VideoSourceOptions o) : IVideoSource
        {
            private long _n;
            private readonly CancellationTokenSource _cts = new();
            public VideoState State { get; private set; } = VideoState.Idle;
            public string? Error { get; private set; }
            public event Action<IVideoSource>? StateChanged;
            public VideoStats Stats { get; private set; }
            public TimeSpan Position => TimeSpan.Zero;

            public void Start()
            {
                State = VideoState.Connecting;
                _ = Task.Run(async () =>
                {
                    var r = await RunAsync("ffprobe.exe", ["-v", "error", "-rtsp_transport", "tcp", "-rw_timeout", "10000000", "-select_streams", "v:0",
                        "-show_entries", "stream=codec_name,width,height,avg_frame_rate", "-of", "default=nw=1", o.Url], _cts.Token);
                    var kv = r.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim().Split('=', 2)).Where(x => x.Length == 2)
                        .ToDictionary(x => x[0], x => x[1]);
                    if (r.Code != 0 || !kv.ContainsKey("width"))
                    {
                        Error = "ffprobe: " + Log.Redact(r.Err.Trim());
                        State = VideoState.Failed;
                    }
                    else
                    {
                        var fr = kv["avg_frame_rate"].Split('/');
                        var fps = fr.Length == 2 && double.Parse(fr[1], CultureInfo.InvariantCulture) > 0
                            ? double.Parse(fr[0], CultureInfo.InvariantCulture) / double.Parse(fr[1], CultureInfo.InvariantCulture) : 0;
                        Stats = new VideoStats(kv["codec_name"] == "hevc" ? "H.265" : "H.264", int.Parse(kv["width"], CultureInfo.InvariantCulture),
                            int.Parse(kv["height"], CultureInfo.InvariantCulture), fps, 0, false);
                        State = VideoState.Playing;
                    }
                    StateChanged?.Invoke(this);
                });
            }

            public void SetTargetSize(int width, int height) { }
            public void Pause() { }
            public void Resume() { }
            public bool TryCopyLatest(ref long lastSequence, FrameCopier copier)
            {
                if (State != VideoState.Playing) return false;
                lastSequence = ++_n;
                return true;
            }
            public Task<FrameSnapshot> CaptureAsync(CancellationToken ct) => throw new NotSupportedException();
            public void Dispose() => _cts.Cancel();
        }
    }

    /// <summary>IMediaTools with the ffmpeg/ffprobe executables (stream copy only, video only).</summary>
    private sealed class CliMedia : IMediaTools
    {
        public async Task<TimeSpan?> GetDurationAsync(string path, CancellationToken ct)
        {
            var r = await RunAsync("ffprobe.exe", ["-v", "error", "-show_entries", "format=duration", "-of", "default=nw=1:nk=1", path], ct);
            return r.Code == 0 && double.TryParse(r.Out.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? TimeSpan.FromSeconds(s) : null;
        }

        public async Task RemuxAsync(IReadOnlyList<RemuxPart> parts, string outputPath, IProgress<double>? progress, CancellationToken ct)
        {
            var dir = Path.GetDirectoryName(outputPath)!;
            var pieces = new List<string>();
            for (var i = 0; i < parts.Count; i++)
            {
                var p = parts[i];
                var piece = Path.Combine(dir, $"piece{i}.ts");
                var args = new List<string> { "-v", "error", "-y" };
                if (p.From is { } f) args.AddRange(["-ss", f.TotalSeconds.ToString(CultureInfo.InvariantCulture)]);
                if (p.To is { } t) args.AddRange(["-to", t.TotalSeconds.ToString(CultureInfo.InvariantCulture)]);
                args.AddRange(["-i", p.Path, "-map", "0:v", "-c", "copy", piece]);
                var r = await RunAsync("ffmpeg.exe", args, ct);
                if (r.Code != 0) throw new IOException(r.Err);
                pieces.Add(piece);
            }
            var list = Path.Combine(dir, "pieces.txt");
            await File.WriteAllLinesAsync(list, pieces.Select(x => $"file '{x.Replace('\\', '/')}'"), ct);
            var c = await RunAsync("ffmpeg.exe", ["-v", "error", "-y", "-f", "concat", "-safe", "0", "-i", list, "-c", "copy", "-tag:v", "hvc1", "-f", "mp4", outputPath], ct);
            if (c.Code != 0)
                c = await RunAsync("ffmpeg.exe", ["-v", "error", "-y", "-f", "concat", "-safe", "0", "-i", list, "-c", "copy", "-f", "mp4", outputPath], ct);
            if (c.Code != 0) throw new IOException(c.Err);
        }

        public async Task RecordAsync(string url, TimeSpan duration, string outputPath, IProgress<double>? progress, CancellationToken ct)
        {
            var r = await RunAsync("ffmpeg.exe", ["-v", "error", "-y", "-rtsp_transport", "tcp", "-i", url, "-t", duration.TotalSeconds.ToString(CultureInfo.InvariantCulture),
                "-map", "0:v", "-c", "copy", "-f", "mp4", outputPath], ct);
            if (r.Code != 0) throw new IOException(Log.Redact(r.Err));
        }
    }
}
