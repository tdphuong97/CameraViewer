using System.Diagnostics;

namespace Cv.Media.Internal;

/// <summary>Frames and bytes per second averaged over the last few one-second buckets. Decoder thread only; no allocations.</summary>
internal sealed class RateMeter
{
    private const int Buckets = 3;
    private readonly int[] _frames = new int[Buckets];
    private readonly long[] _bytes = new long[Buckets];
    private readonly double[] _seconds = new double[Buckets];
    private int _filled, _next;
    private long _bucketStart = Stopwatch.GetTimestamp();
    private int _curFrames;
    private long _curBytes;

    public double Fps { get; private set; }
    public double Kbps { get; private set; }

    public void AddBytes(int n) => _curBytes += n;

    /// <summary>Counts a shown frame; returns true when the averages were refreshed (about once a second).</summary>
    public bool AddFrame()
    {
        _curFrames++;
        long now = Stopwatch.GetTimestamp();
        double elapsed = (now - _bucketStart) / (double)Stopwatch.Frequency;
        if (elapsed < 1.0) return false;
        _frames[_next] = _curFrames;
        _bytes[_next] = _curBytes;
        _seconds[_next] = elapsed;
        _next = (_next + 1) % Buckets;
        if (_filled < Buckets) _filled++;
        _curFrames = 0;
        _curBytes = 0;
        _bucketStart = now;
        double s = 0, f = 0, b = 0;
        for (int i = 0; i < _filled; i++) { s += _seconds[i]; f += _frames[i]; b += _bytes[i]; }
        Fps = s > 0 ? f / s : 0;
        Kbps = s > 0 ? b * 8 / 1000.0 / s : 0;
        return true;
    }

    public void Reset()
    {
        Array.Clear(_frames);
        Array.Clear(_bytes);
        Array.Clear(_seconds);
        _filled = _next = 0;
        _curFrames = 0;
        _curBytes = 0;
        _bucketStart = Stopwatch.GetTimestamp();
        Fps = Kbps = 0;
    }
}
