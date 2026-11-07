using Cv.Core.Export;
using Cv.Media.Tests.Support;
using FFmpeg.AutoGen;

namespace Cv.Media.Tests;

public class MediaToolsTests : IDisposable
{
    private readonly FFmpegMediaTools _tools = new();
    private readonly string _dir = TestEnv.NewTempDir();
    private static readonly double Span = TestClips.Span.TotalSeconds;
    private const double Frame = 1.0 / TestClips.Fps;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string Out(string name) => Path.Combine(_dir, name);

    private sealed class Recorder : IProgress<double>
    {
        public readonly List<double> Values = [];
        public void Report(double value) { lock (Values) Values.Add(value); }
    }

    [NativeTheory]
    [InlineData("h264.ps")]
    [InlineData("h265.ps")]
    [InlineData("h264_g711.ps")]
    [InlineData("h264.mp4")]
    [InlineData("h265.mp4")]
    [InlineData("h264_aac.ts")]
    public async Task Duration_scans_packets(string clip)
    {
        var d = await _tools.GetDurationAsync(TestClips.Resolve(clip), CancellationToken.None);
        Assert.NotNull(d);
        Assert.InRange(d!.Value.TotalSeconds, Span - 0.01, Span + 0.01);
    }

    [NativeFact]
    public async Task Duration_is_null_for_missing_or_non_video_files()
    {
        Assert.Null(await _tools.GetDurationAsync(Out("nope.ps"), CancellationToken.None));
        var junk = Out("junk.ps");
        await File.WriteAllTextAsync(junk, string.Concat(Enumerable.Repeat("<ResponseStatus>Invalid Operation</ResponseStatus>", 50)));
        Assert.Null(await _tools.GetDurationAsync(junk, CancellationToken.None));
    }

    [NativeTheory]
    [InlineData("h264.ps", AVCodecID.AV_CODEC_ID_H264, "avc1")]
    [InlineData("h265.ps", AVCodecID.AV_CODEC_ID_HEVC, "hvc1")]
    [InlineData("h265.mp4", AVCodecID.AV_CODEC_ID_HEVC, "hvc1")]
    public async Task Remux_whole_file_to_faststart_mp4(string clip, AVCodecID codec, string tag)
    {
        var output = Out("out.mp4");
        var progress = new Recorder();
        await _tools.RemuxAsync([new RemuxPart(TestClips.Resolve(clip))], output, progress, CancellationToken.None);

        Assert.True(File.Exists(output));
        Assert.False(File.Exists(output + ".part"));
        var p = MediaProbe.Probe(output);
        Assert.Single(p.Streams);
        Assert.Equal(codec, p.Video.Codec);
        Assert.Equal(tag, p.Video.Tag);
        Assert.Equal((320, 180), (p.Video.Width, p.Video.Height));
        Assert.Equal(TestClips.Frames, p.Video.Packets);
        Assert.True(p.Video.FirstIsKey);
        Assert.True(p.Video.DtsMonotonic);
        Assert.InRange(p.Video.FirstPts, -0.001, 0.001);
        Assert.InRange(p.VideoSpan, Span - 0.01, Span + 0.01);
        Assert.True(Array.IndexOf(p.TopLevelBoxes, "moov") < Array.IndexOf(p.TopLevelBoxes, "mdat"), string.Join(",", p.TopLevelBoxes));

        lock (progress.Values)
        {
            Assert.Equal(1.0, progress.Values[^1]);
            Assert.Equal(progress.Values.OrderBy(v => v), progress.Values);
        }
    }

    [NativeTheory]
    [InlineData("h264.ps")]
    [InlineData("h265.ps")]
    public async Task Remux_trims_from_the_keyframe_before_From(string clip)
    {
        var output = Out("trim.mp4");
        // Keyframes every 1 s: From 2.5 s starts at the 2.0 s keyframe; To 4.0 s is exclusive.
        await _tools.RemuxAsync([new RemuxPart(TestClips.Resolve(clip), TimeSpan.FromSeconds(2.5), TimeSpan.FromSeconds(4))], output, null, CancellationToken.None);
        var p = MediaProbe.Probe(output);
        Assert.True(p.Video.FirstIsKey);
        Assert.Equal(30, p.Video.Packets); // frames at 2.0 .. 3.933
        Assert.InRange(p.VideoSpan, 2 - Frame - 0.01, 2 - Frame + 0.01);
        Assert.InRange(p.Video.FirstPts, -0.001, 0.001);
    }

    [NativeFact]
    public async Task Remux_joins_two_parts_with_continuous_timestamps()
    {
        var output = Out("two.mp4");
        var progress = new Recorder();
        await _tools.RemuxAsync([new RemuxPart(TestClips.H264Ps), new RemuxPart(TestClips.H264G711Ps)], output, progress, CancellationToken.None);
        var p = MediaProbe.Probe(output);
        Assert.Single(p.Streams); // G.711 dropped
        Assert.Equal(2 * TestClips.Frames, p.Video.Packets);
        Assert.True(p.Video.DtsMonotonic);
        // Second part starts one frame after the first ends: no gap, no overlap.
        Assert.InRange(p.VideoSpan, 2 * Span + Frame - 0.01, 2 * Span + Frame + 0.01);
        lock (progress.Values) Assert.Equal(1.0, progress.Values[^1]);
    }

    [NativeFact]
    public async Task Remux_joins_two_trimmed_parts()
    {
        var output = Out("two-trim.mp4");
        await _tools.RemuxAsync(
        [
            new RemuxPart(TestClips.H265Ps, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)),     // 1.0 .. 2.933 (30 frames)
            new RemuxPart(TestClips.H265G711Ps, TimeSpan.FromSeconds(3.2), null),                   // 3.0 .. 4.933 (30 frames)
        ], output, null, CancellationToken.None);
        var p = MediaProbe.Probe(output);
        Assert.Equal(60, p.Video.Packets);
        Assert.True(p.Video.DtsMonotonic);
        Assert.InRange(p.VideoSpan, 4 - Frame - 0.01, 4 - Frame + 0.01);
        Assert.Equal("hvc1", p.Video.Tag);
    }

    [NativeFact]
    public async Task Remux_copies_aac_audio()
    {
        var output = Out("aac.mp4");
        await _tools.RemuxAsync([new RemuxPart(TestClips.H264AacTs)], output, null, CancellationToken.None);
        var p = MediaProbe.Probe(output);
        Assert.Equal(2, p.Streams.Count);
        var a = p.Streams.Single(s => s.Type == AVMediaType.AVMEDIA_TYPE_AUDIO);
        Assert.Equal(AVCodecID.AV_CODEC_ID_AAC, a.Codec);
        Assert.True(a.DtsMonotonic);
        Assert.InRange(a.LastPts - a.FirstPts, 4.0, 5.1);
        Assert.Equal(TestClips.Frames, p.Video.Packets);
    }

    [NativeFact]
    public async Task Remux_cancel_leaves_no_files()
    {
        var output = Out("cancel.mp4");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _tools.RemuxAsync([new RemuxPart(TestClips.H264Ps)], output, null, cts.Token));
        Assert.False(File.Exists(output));
        Assert.False(File.Exists(output + ".part"));
    }

    [NativeFact]
    public async Task Remux_rejects_garbage_with_vietnamese_message()
    {
        var junk = Out("junk.ps");
        await File.WriteAllBytesAsync(junk, Enumerable.Range(0, 200_000).Select(i => (byte)(i * 31 % 251)).ToArray());
        var output = Out("junk.mp4");
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => _tools.RemuxAsync([new RemuxPart(junk)], output, null, CancellationToken.None));
        Assert.Contains(ex.Message, c => c > 127); // Vietnamese, for the user
        Assert.False(File.Exists(output + ".part"));
        await Assert.ThrowsAsync<FileNotFoundException>(() => _tools.RemuxAsync([new RemuxPart(Out("missing.ps"))], output, null, CancellationToken.None));
    }

    [NativeFact]
    public async Task Record_from_file_url_stops_at_duration()
    {
        var output = Out("rec.mp4");
        var progress = new Recorder();
        await _tools.RecordAsync(TestClips.H264AacTs, TimeSpan.FromSeconds(3), output, progress, CancellationToken.None);
        var p = MediaProbe.Probe(output);
        Assert.Equal(45, p.Video.Packets); // [0, 3) s at 15 fps
        Assert.True(p.Video.FirstIsKey);
        Assert.Contains(p.Streams, s => s.Codec == AVCodecID.AV_CODEC_ID_AAC);
        lock (progress.Values) Assert.Equal(1.0, progress.Values[^1]);

        // Longer than the source: stops at EOF with everything.
        var all = Out("rec-all.mp4");
        await _tools.RecordAsync(TestClips.H265Ps, TimeSpan.FromMinutes(1), all, null, CancellationToken.None);
        Assert.Equal(TestClips.Frames, MediaProbe.Probe(all).Video.Packets);
    }
}
