using System.Runtime.InteropServices;
using Cv.Core.Video;
using Cv.Media.Internal;
using Cv.Media.Tests.Support;
using FFmpeg.AutoGen;

namespace Cv.Media.Tests;

/// <summary>Logic that needs no FFmpeg DLLs.</summary>
public class ManagedTests
{
    [Fact]
    public void FrameExchange_returns_only_newer_frames_and_latest_wins()
    {
        var x = new FrameExchange();
        long seq = 0;
        int calls = 0;
        FrameCopier copier = (_, _, _, _) => calls++;
        Assert.False(x.TryCopyLatest(ref seq, copier));
        Assert.Equal(0, calls);

        Fill(x, 10, 4, 1);
        Fill(x, 20, 6, 2);
        int w = 0, h = 0, stride = 0;
        byte first = 0;
        Assert.True(x.TryCopyLatest(ref seq, (p, ww, hh, s) => { w = ww; h = hh; stride = s; first = Marshal.ReadByte(p); }));
        Assert.Equal((20, 6), (w, h));
        Assert.Equal(2, first);
        Assert.Equal(0, stride % 64);
        Assert.True(stride >= 20 * 4);
        Assert.Equal(2, seq);

        calls = 0;
        Assert.False(x.TryCopyLatest(ref seq, copier));
        Assert.Equal(0, calls);

        Fill(x, 8, 2, 3);
        Assert.True(x.TryCopyLatest(ref seq, (p, ww, _, _) => { w = ww; first = Marshal.ReadByte(p); }));
        Assert.Equal((8, 3), (w, (int)first));
        Assert.Equal(3, seq);

        x.Free();
        Fill(x, 8, 2, 4); // writer after free must not crash the reader
        Assert.False(x.TryCopyLatest(ref seq, copier));
    }

    [Fact]
    public async Task FrameExchange_writer_never_blocks_on_a_slow_reader()
    {
        var x = new FrameExchange();
        Fill(x, 4, 4, 1);
        long seq = 0;
        using var inCopy = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var reader = Task.Run(() => x.TryCopyLatest(ref seq, (_, _, _, _) => { inCopy.Set(); release.Wait(); }));
        Assert.True(inCopy.Wait(2000));
        // While the UI copies, the decoder keeps publishing.
        var writer = Task.Run(() => { for (int i = 0; i < 100; i++) Fill(x, 4, 4, (byte)i); });
        await writer.WaitAsync(TimeSpan.FromSeconds(2));
        release.Set();
        Assert.True(await reader);
        long seq2 = 1;
        byte last = 0;
        Assert.True(x.TryCopyLatest(ref seq2, (p, _, _, _) => last = Marshal.ReadByte(p)));
        Assert.Equal(99, last);
        x.Free();
    }

    private static unsafe void Fill(FrameExchange x, int w, int h, byte value)
    {
        byte* p = x.BeginWrite(w, h, out int stride);
        if (p == null) return;
        new Span<byte>(p, stride * h).Fill(value);
        x.Publish();
    }

    [Theory]
    [InlineData(320, 180, 0, 0, 320, 180)]       // 0 = source size
    [InlineData(320, 180, 160, 160, 160, 90)]    // width-bound
    [InlineData(320, 180, 1000, 50, 88, 50)]     // height-bound, even
    [InlineData(320, 180, 1920, 1080, 320, 180)] // never upscale
    [InlineData(1920, 1080, 383, 215, 382, 214)] // odd box -> even, inside the box
    [InlineData(321, 181, 0, 0, 320, 180)]       // odd source -> even
    [InlineData(704, 576, 0, 0, 704, 576)]
    [InlineData(3, 3, 1, 1, 2, 2)]               // minimum 2x2
    public void FitBox_keeps_aspect_within_box(int sw, int sh, int bw, int bh, int ew, int eh)
    {
        Assert.Equal((ew, eh), FFmpegVideoSource.FitBox(sw, sh, 1, 1, bw, bh));
    }

    [Fact]
    public void FitBox_applies_sample_aspect_ratio()
    {
        // 704x576 with SAR 12:11 displays as 768x576.
        Assert.Equal((768, 576), FFmpegVideoSource.FitBox(704, 576, 12, 11, 0, 0));
    }

    [Fact]
    public void Errors_map_to_short_vietnamese_messages()
    {
        Assert.Equal(Failure.Auth, FFmpegError.Classify(ffmpeg.AVERROR_HTTP_UNAUTHORIZED));
        Assert.Equal(Failure.NotFound, FFmpegError.Classify(ffmpeg.AVERROR_HTTP_NOT_FOUND));
        Assert.Equal(Failure.Forbidden, FFmpegError.Classify(ffmpeg.AVERROR_HTTP_FORBIDDEN));
        Assert.Equal(Failure.Network, FFmpegError.Classify(-10060)); // WSAETIMEDOUT
        Assert.Equal(Failure.Network, FFmpegError.Classify(-10061)); // WSAECONNREFUSED
        Assert.Equal(Failure.Network, FFmpegError.Classify(-138));   // ETIMEDOUT
        Assert.Equal(Failure.DiskFull, FFmpegError.Classify(-28));
        Assert.True(FFmpegError.IsPermanent(Failure.Auth));
        Assert.False(FFmpegError.IsPermanent(Failure.Network));
        Assert.Equal("Sai tài khoản hoặc mật khẩu đầu ghi", FFmpegError.Message(Failure.Auth));
        Assert.Equal("Không kết nối được đầu ghi", FFmpegError.Message(Failure.Network));
        foreach (Failure f in Enum.GetValues<Failure>()) Assert.False(string.IsNullOrWhiteSpace(FFmpegError.Message(f)));
    }

    [Fact]
    public void Timestamp_unwrap_follows_33_bit_wrap()
    {
        var u = new TimestampUnwrap();
        long period = 1L << 33;
        Assert.Equal(period - 3000, u.Apply(period - 3000, 33));
        Assert.Equal(period + 3000, u.Apply(3000, 33));
        Assert.Equal(period + 2000, u.Apply(2000, 33)); // small reorder stays put
    }

    [Fact]
    public void Ps_writer_produces_hikvision_layout()
    {
        if (!File.Exists(TestClips.H265Es)) return; // assets not generated yet
        var dir = TestEnv.NewTempDir();
        var ps = Path.Combine(dir, "x.ps");
        HikPsWriter.Write(TestClips.H265Es, hevc: true, 15, ps, HikPsWriter.StreamTypeG711A);
        var b = File.ReadAllBytes(ps);
        Assert.Equal("IMKH"u8.ToArray(), b[..4]);
        Assert.Equal(new byte[] { 0, 0, 1, 0xBA }, b[40..44]);
        int psm = IndexOf(b, [0, 0, 1, 0xBC]);
        Assert.True(psm > 40);
        Assert.Equal(0x24, b[psm + 12]); // first ES map entry: H.265
        Assert.Equal(0xE0, b[psm + 13]);
        Assert.Equal(0x90, b[psm + 16]); // G.711
        var frames = HikPsWriter.SplitAccessUnits(File.ReadAllBytes(TestClips.H265Es), hevc: true);
        Assert.Equal(TestClips.Frames, frames.Count);
        Assert.Equal(5, frames.Count(f => f.Key));
        Directory.Delete(dir, true);
    }

    private static int IndexOf(byte[] hay, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= hay.Length; i++)
            if (hay.AsSpan(i, needle.Length).SequenceEqual(needle)) return i;
        return -1;
    }
}
