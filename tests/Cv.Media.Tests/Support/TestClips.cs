namespace Cv.Media.Tests.Support;

/// <summary>The committed clips plus Hikvision-like PS files built from them once per test run.</summary>
public static class TestClips
{
    public const int Width = 320, Height = 180, Fps = 15, Frames = 75;

    /// <summary>First to last frame of a 75-frame 15 fps clip.</summary>
    public static readonly TimeSpan Span = TimeSpan.FromSeconds((Frames - 1) / (double)Fps);

    public static string H264Es => TestEnv.Asset("h264_320x180_15fps.h264");
    public static string H265Es => TestEnv.Asset("h265_320x180_15fps.h265");
    public static string H264Mp4 => TestEnv.Asset("h264.mp4");
    public static string H265Mp4 => TestEnv.Asset("h265.mp4");
    public static string H264AacTs => TestEnv.Asset("h264_aac.ts");

    private static readonly Lazy<string> PsDir = new(() =>
    {
        var dir = TestEnv.NewTempDir();
        HikPsWriter.Write(H264Es, hevc: false, Fps, Path.Combine(dir, "h264.ps"));
        HikPsWriter.Write(H265Es, hevc: true, Fps, Path.Combine(dir, "h265.ps"));
        HikPsWriter.Write(H264Es, hevc: false, Fps, Path.Combine(dir, "h264_g711.ps"), HikPsWriter.StreamTypeG711U);
        HikPsWriter.Write(H265Es, hevc: true, Fps, Path.Combine(dir, "h265_g711.ps"), HikPsWriter.StreamTypeG711A);
        return dir;
    });

    public static string H264Ps => Path.Combine(PsDir.Value, "h264.ps");
    public static string H265Ps => Path.Combine(PsDir.Value, "h265.ps");
    public static string H264G711Ps => Path.Combine(PsDir.Value, "h264_g711.ps");
    public static string H265G711Ps => Path.Combine(PsDir.Value, "h265_g711.ps");

    public static string Resolve(string name) => name switch
    {
        "h264.mp4" => H264Mp4,
        "h265.mp4" => H265Mp4,
        "h264.ps" => H264Ps,
        "h265.ps" => H265Ps,
        "h264_g711.ps" => H264G711Ps,
        "h265_g711.ps" => H265G711Ps,
        "h264_aac.ts" => H264AacTs,
        _ => throw new ArgumentException(name),
    };
}
