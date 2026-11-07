using System.Diagnostics;
using System.Text;
using Cv.Core.Video;
using Cv.Media.Tests.Support;
using Xunit.Abstractions;

namespace Cv.Media.Tests;

[CollectionDefinition("perf", DisableParallelization = true)]
public class PerfCollection;

/// <summary>
/// RAM/CPU of the decoding pipelines (run alone: set CV_MEDIA_PERF=1 and filter on this class). Streams come through
/// MediaMTX when installed, else from files opened as live (paced loop).
/// </summary>
[Collection("perf")]
public class MemoryTests(ITestOutputHelper output)
{
    private static readonly Lazy<string> ClipDir = new(() =>
    {
        var dir = TestEnv.NewTempDir();
        var src = "-f lavfi -i testsrc2=size={0}:rate=15:duration=20 -pix_fmt yuv420p";
        TestEnv.RunFFmpeg(string.Format(src, "640x360") + " -c:v libx264 -preset veryfast -b:v 512k -g 30 -bf 0 " + Q(dir, "sub640.mp4"));
        TestEnv.RunFFmpeg(string.Format(src, "1280x720") + " -c:v libx265 -preset fast -b:v 2M -x265-params keyint=30:bframes=0:log-level=error -tag:v hvc1 " + Q(dir, "main720.mp4"));
        TestEnv.RunFFmpeg(string.Format(src, "1920x1080") + " -c:v libx265 -preset fast -b:v 4M -x265-params keyint=30:bframes=0:log-level=error -tag:v hvc1 " + Q(dir, "main1080.mp4"));
        return dir;
    });

    private static string Q(string dir, string name) => "\"" + Path.Combine(dir, name) + "\"";
    private static string Clip(string name) => Path.Combine(ClipDir.Value, name);
    private static double Mb(long b) => b / 1048576.0;

    private readonly StringBuilder _report = new();

    private void Line(string s)
    {
        output.WriteLine(s);
        _report.AppendLine(s);
        File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "perf-results.txt"), s + Environment.NewLine);
    }

    [PerfFact]
    public async Task Measure_memory_per_source()
    {
        _ = TestEnv.FFmpegReady;
        _ = ClipDir.Value;
        MediaMtx? mtx = File.Exists(TestEnv.MediaMtxExe) ? new MediaMtx() : null;
        try
        {
            string Url(string path, string clip)
            {
                if (mtx is null) return Clip(clip);
                return mtx.ReaderUrl(path);
            }
            if (mtx != null)
            {
                mtx.Publish("sub", Clip("sub640.mp4"));
                mtx.Publish("main720", Clip("main720.mp4"));
                mtx.Publish("main1080", Clip("main1080.mp4"));
                await Task.Delay(2000);
            }
            Line($"=== {DateTime.Now:yyyy-MM-dd HH:mm} source: {(mtx != null ? "RTSP via MediaMTX" : "files opened as live")}, FFmpeg {FFmpegRuntime.Version}, {Environment.ProcessorCount} logical CPUs");

            await Scenario("20 x 640x360 H.264 -> 384x216 tiles", 20, Url("sub", "sub640.mp4"), HwDecodeMode.Auto, 384, 216);
            await Scenario("1 x 1280x720 H.265 -> 1600x900 (Auto: software, <= 1280 px)", 1, Url("main720", "main720.mp4"), HwDecodeMode.Auto, 1600, 900);
            await Scenario("1 x 1920x1080 H.265 -> 1600x900, software", 1, Url("main1080", "main1080.mp4"), HwDecodeMode.Off, 1600, 900);
            await Scenario("1 x 1920x1080 H.265 -> 1600x900, Auto (D3D11VA)", 1, Url("main1080", "main1080.mp4"), HwDecodeMode.Auto, 1600, 900);
            await Cycles("open/close 20 x 640x360 tiles, 6 cycles of 4 s", Url("sub", "sub640.mp4"), 6);
        }
        finally
        {
            mtx?.Dispose();
        }
    }

    private async Task Cycles(string name, string url, int cycles)
    {
        using var factory = new FFmpegVideoSourceFactory();
        var mem = new List<string>();
        for (int c = 0; c < cycles; c++)
        {
            var sources = Enumerable.Range(0, 20)
                .Select(_ => factory.Create(new VideoSourceOptions { Url = url, IsLive = true, TargetWidth = 384, TargetHeight = 216 }))
                .ToList();
            foreach (var s in sources) s.Start();
            await Task.Delay(4000);
            foreach (var s in sources) s.Dispose();
            await Task.Delay(1000);
            mem.Add($"{Mb(TestEnv.Memory().PrivateBytes):F1}");
        }
        Line($"{name}: private bytes after each cycle (MB): {string.Join(", ", mem)}");
    }

    private async Task Scenario(string name, int count, string url, HwDecodeMode hw, int tw, int th)
    {
        var factory = new FFmpegVideoSourceFactory();
        await Task.Delay(500);
        var before = TestEnv.Memory();
        using var self = Process.GetCurrentProcess();
        var cpu0 = self.TotalProcessorTime;
        var sources = Enumerable.Range(0, count)
            .Select(_ => factory.Create(new VideoSourceOptions { Url = url, IsLive = true, HwDecode = hw, TargetWidth = tw, TargetHeight = th }))
            .ToList();
        foreach (var s in sources) s.Start();
        var sw = Stopwatch.StartNew();
        // Pull frames like the UI does (copy into one reusable managed buffer) for 15 s.
        var seqs = new long[count];
        var buffer = new byte[tw * th * 4 + 4096];
        long copied = 0;
        while (sw.Elapsed < TimeSpan.FromSeconds(15))
        {
            for (int i = 0; i < count; i++)
                if (sources[i].TryCopyLatest(ref seqs[i], (p, w, h, stride) => System.Runtime.InteropServices.Marshal.Copy(p, buffer, 0, Math.Min(buffer.Length, stride * h))))
                    copied++;
            await Task.Delay(16);
        }
        self.Refresh();
        var cpu = (self.TotalProcessorTime - cpu0).TotalSeconds / sw.Elapsed.TotalSeconds;
        var during = TestEnv.Memory();
        var playing = sources.Count(s => s.State == VideoState.Playing);
        var st = sources[0].Stats;
        foreach (var s in sources) s.Dispose();
        await Task.Delay(1500);
        var after = TestEnv.Memory();
        Line($"{name}: playing {playing}/{count}, {st.Codec} {st.Width}x{st.Height} {st.Fps:F1} fps {st.Kbps:F0} kbps hw={st.HardwareDecoding}, frames copied {copied}");
        Line($"   private bytes  {Mb(before.PrivateBytes),7:F1} -> {Mb(during.PrivateBytes),7:F1} MB (+{Mb(during.PrivateBytes - before.PrivateBytes):F1}, {Mb(during.PrivateBytes - before.PrivateBytes) / count:F1}/source); after dispose {Mb(after.PrivateBytes):F1} MB");
        Line($"   private WS     {Mb(before.PrivateWorkingSet),7:F1} -> {Mb(during.PrivateWorkingSet),7:F1} MB (+{Mb(during.PrivateWorkingSet - before.PrivateWorkingSet):F1}, {Mb(during.PrivateWorkingSet - before.PrivateWorkingSet) / count:F1}/source); after dispose {Mb(after.PrivateWorkingSet):F1} MB");
        factory.Dispose();
        await Task.Delay(1000);
        var afterFactory = TestEnv.Memory();
        Line($"   after factory dispose: private bytes {Mb(afterFactory.PrivateBytes):F1} MB, private WS {Mb(afterFactory.PrivateWorkingSet):F1} MB");
        Line($"   CPU {cpu * 100:F0} % of one core");
        Assert.Equal(count, playing);
    }
}
