using FFmpeg.AutoGen;

namespace Cv.Media;

/// <summary>
/// Turns what FFmpeg's audio decoders hand out (8/16/32-bit integer, float or double; packed or planar) into the
/// interleaved 16-bit samples the sound output plays. No resampling: the output is opened at the stream's own rate
/// (G.711 8 kHz, G.722 16 kHz, AAC 44.1/48 kHz all work), which keeps swresample out of the app.
/// </summary>
internal static unsafe class PcmConvert
{
    /// <summary>Mono stays mono; anything wider plays as its first two channels (stereo), which is all a camera has.</summary>
    public static int OutputChannels(int channels) => channels >= 2 ? 2 : 1;

    public static bool IsSupported(AVSampleFormat fmt) => fmt is
        AVSampleFormat.AV_SAMPLE_FMT_U8 or AVSampleFormat.AV_SAMPLE_FMT_U8P or
        AVSampleFormat.AV_SAMPLE_FMT_S16 or AVSampleFormat.AV_SAMPLE_FMT_S16P or
        AVSampleFormat.AV_SAMPLE_FMT_S32 or AVSampleFormat.AV_SAMPLE_FMT_S32P or
        AVSampleFormat.AV_SAMPLE_FMT_FLT or AVSampleFormat.AV_SAMPLE_FMT_FLTP or
        AVSampleFormat.AV_SAMPLE_FMT_DBL or AVSampleFormat.AV_SAMPLE_FMT_DBLP;

    static bool IsPlanar(AVSampleFormat fmt) => fmt is
        AVSampleFormat.AV_SAMPLE_FMT_U8P or AVSampleFormat.AV_SAMPLE_FMT_S16P or AVSampleFormat.AV_SAMPLE_FMT_S32P or
        AVSampleFormat.AV_SAMPLE_FMT_FLTP or AVSampleFormat.AV_SAMPLE_FMT_DBLP;

    /// <summary>
    /// Writes <paramref name="samples"/> frames of <see cref="OutputChannels"/> channels into <paramref name="dst"/>
    /// (which must hold samples × OutputChannels shorts). <paramref name="planes"/> is the frame's extended_data: one
    /// pointer per channel when planar, one pointer to the interleaved data otherwise. Returns false for a sample format
    /// that is not supported.
    /// </summary>
    public static bool ToS16(AVSampleFormat fmt, byte** planes, int channels, int samples, Span<short> dst)
    {
        if (!IsSupported(fmt) || channels < 1 || samples < 0) return false;
        int outCh = OutputChannels(channels);
        if (dst.Length < samples * outCh) return false;
        bool planar = IsPlanar(fmt);
        for (int c = 0; c < outCh; c++)
        {
            byte* p = planar ? planes[c] : planes[0];
            long stride = planar ? 1 : channels;
            long first = planar ? 0 : c;
            for (int i = 0; i < samples; i++)
                dst[i * outCh + c] = Read(fmt, p, first + i * stride);
        }
        return true;
    }

    private static short Read(AVSampleFormat fmt, byte* p, long index) => fmt switch
    {
        AVSampleFormat.AV_SAMPLE_FMT_S16 or AVSampleFormat.AV_SAMPLE_FMT_S16P => ((short*)p)[index],
        AVSampleFormat.AV_SAMPLE_FMT_FLT or AVSampleFormat.AV_SAMPLE_FMT_FLTP => FromFloat(((float*)p)[index]),
        AVSampleFormat.AV_SAMPLE_FMT_S32 or AVSampleFormat.AV_SAMPLE_FMT_S32P => (short)(((int*)p)[index] >> 16),
        AVSampleFormat.AV_SAMPLE_FMT_U8 or AVSampleFormat.AV_SAMPLE_FMT_U8P => (short)((p[index] - 128) << 8),
        AVSampleFormat.AV_SAMPLE_FMT_DBL or AVSampleFormat.AV_SAMPLE_FMT_DBLP => FromFloat((float)((double*)p)[index]),
        _ => 0,
    };

    /// <summary>-1.0..1.0 to 16 bit, clipped (a loud AAC peak can pass 1.0).</summary>
    internal static short FromFloat(float v)
    {
        if (float.IsNaN(v)) return 0;
        var s = (int)MathF.Round(v * 32768f);
        return (short)Math.Clamp(s, short.MinValue, short.MaxValue);
    }
}
