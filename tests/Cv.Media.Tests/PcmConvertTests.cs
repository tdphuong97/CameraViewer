using FFmpeg.AutoGen;

namespace Cv.Media.Tests;

public unsafe class PcmConvertTests
{
    [Fact]
    public void Packed_16_bit_stereo_is_copied_as_is()
    {
        short[] src = [100, -100, 200, -200, 300, -300];
        var dst = new short[6];
        fixed (short* s = src)
        {
            byte*[] planes = [(byte*)s];
            fixed (byte** pp = planes)
                Assert.True(PcmConvert.ToS16(AVSampleFormat.AV_SAMPLE_FMT_S16, pp, 2, 3, dst));
        }
        Assert.Equal(src, dst);
    }

    [Fact]
    public void Planar_16_bit_is_interleaved()
    {
        short[] left = [1, 2, 3];
        short[] right = [-1, -2, -3];
        var dst = new short[6];
        fixed (short* l = left)
        fixed (short* r = right)
        {
            byte*[] planes = [(byte*)l, (byte*)r];
            fixed (byte** pp = planes)
                Assert.True(PcmConvert.ToS16(AVSampleFormat.AV_SAMPLE_FMT_S16P, pp, 2, 3, dst));
        }
        Assert.Equal([1, -1, 2, -2, 3, -3], dst);
    }

    [Fact]
    public void Planar_float_AAC_style_scales_and_clips()
    {
        float[] ch = [0f, 0.5f, -0.5f, 1.0f, -1.0f, 1.7f, -1.7f, float.NaN];
        var dst = new short[ch.Length];
        fixed (float* c = ch)
        {
            byte*[] planes = [(byte*)c];
            fixed (byte** pp = planes)
                Assert.True(PcmConvert.ToS16(AVSampleFormat.AV_SAMPLE_FMT_FLTP, pp, 1, ch.Length, dst));
        }
        Assert.Equal([0, 16384, -16384, 32767, -32768, 32767, -32768, 0], dst);
    }

    [Fact]
    public void Packed_float_stereo_and_wider_layouts_play_as_their_first_two_channels()
    {
        // 3 channels packed: L R C per sample; only L and R are kept.
        float[] src = [0.25f, -0.25f, 0.9f, 0.5f, -0.5f, 0.9f];
        var dst = new short[4];
        fixed (float* s = src)
        {
            byte*[] planes = [(byte*)s];
            fixed (byte** pp = planes)
                Assert.True(PcmConvert.ToS16(AVSampleFormat.AV_SAMPLE_FMT_FLT, pp, 3, 2, dst));
        }
        Assert.Equal([8192, -8192, 16384, -16384], dst);
        Assert.Equal(2, PcmConvert.OutputChannels(6));
        Assert.Equal(1, PcmConvert.OutputChannels(1));
    }

    [Fact]
    public void Other_integer_widths_are_scaled_to_16_bit()
    {
        int[] s32 = [int.MaxValue, int.MinValue, 0x00010000];
        var dst = new short[3];
        fixed (int* s = s32)
        {
            byte*[] planes = [(byte*)s];
            fixed (byte** pp = planes)
                Assert.True(PcmConvert.ToS16(AVSampleFormat.AV_SAMPLE_FMT_S32, pp, 1, 3, dst));
        }
        Assert.Equal([32767, -32768, 1], dst);

        byte[] u8 = [128, 255, 0];
        fixed (byte* u = u8)
        {
            byte*[] planes = [u];
            fixed (byte** pp = planes)
                Assert.True(PcmConvert.ToS16(AVSampleFormat.AV_SAMPLE_FMT_U8, pp, 1, 3, dst));
        }
        Assert.Equal([0, 127 << 8, -128 << 8], dst);
    }

    [Fact]
    public void An_unsupported_format_or_a_short_buffer_is_refused_without_writing()
    {
        var dst = new short[2];
        byte*[] planes = [null];
        fixed (byte** pp = planes)
        {
            Assert.False(PcmConvert.ToS16(AVSampleFormat.AV_SAMPLE_FMT_S64, pp, 1, 2, dst));
            Assert.False(PcmConvert.ToS16(AVSampleFormat.AV_SAMPLE_FMT_S16, pp, 2, 2, dst)); // needs 4 shorts
        }
        Assert.All(dst, v => Assert.Equal(0, v));
        Assert.False(PcmConvert.IsSupported(AVSampleFormat.AV_SAMPLE_FMT_S64));
    }
}
