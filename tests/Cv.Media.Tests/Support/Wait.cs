using System.Diagnostics;
using Cv.Core.Video;

namespace Cv.Media.Tests.Support;

public static class Wait
{
    public static async Task<bool> For(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (condition()) return true;
            await Task.Delay(20);
        }
        return condition();
    }

    public static Task<bool> ForState(IVideoSource s, VideoState state, double seconds = 10) =>
        For(() => s.State == state, TimeSpan.FromSeconds(seconds));

    /// <summary>Copies the newest frame the way the UI would; null when none is newer than seq.</summary>
    public static (int W, int H, int Stride, byte[] Pixels)? Grab(IVideoSource s, ref long seq)
    {
        (int, int, int, byte[])? result = null;
        s.TryCopyLatest(ref seq, (p, w, h, stride) =>
        {
            var buf = new byte[stride * h];
            System.Runtime.InteropServices.Marshal.Copy(p, buf, 0, buf.Length);
            result = (w, h, stride, buf);
        });
        return result;
    }

    public static async Task<(int W, int H, int Stride, byte[] Pixels)> NextFrame(IVideoSource s, long after = 0, double seconds = 10)
    {
        long seq = after;
        (int, int, int, byte[])? f = null;
        bool ok = await For(() => (f = Grab(s, ref seq)) != null, TimeSpan.FromSeconds(seconds));
        Assert.True(ok, $"no frame within {seconds} s (state {s.State}, error {s.Error})");
        return f!.Value;
    }
}
