using System.Runtime.InteropServices;
using Cv.Core.Video;

namespace Cv.Media.Internal;

/// <summary>
/// Lock-free triple buffer of BGRA frames in native memory: the decoder owns one slot, the UI owns one slot, the third
/// is swapped atomically between them. The decoder never waits for the UI. The UI copies under the frame lock, which
/// only guards against the slots being freed.
/// </summary>
internal sealed unsafe class FrameExchange
{
    private sealed class Slot
    {
        public byte* Data;
        public nuint Capacity;
        public int Width, Height, Stride;
        public long Seq;
    }

    private const int Fresh = 4;
    private readonly Slot[] _slots = [new(), new(), new()];
    private readonly object _frameLock = new();
    private int _writer;          // decoder thread only
    private int _reader = 1;      // under _frameLock only
    private int _shared = 2;      // slot index | Fresh, swapped with Interlocked
    private long _seq;
    private bool _freed;

    /// <summary>Bytes held by the three slots (for diagnostics).</summary>
    public long AllocatedBytes => (long)(_slots[0].Capacity + _slots[1].Capacity + _slots[2].Capacity);

    public long LastSequence => Interlocked.Read(ref _seq);

    /// <summary>Decoder thread: returns the buffer to draw the next frame into (stride is 64-byte aligned).</summary>
    public byte* BeginWrite(int width, int height, out int stride)
    {
        var s = _slots[_writer];
        stride = (width * 4 + 63) & ~63;
        nuint need = (nuint)stride * (nuint)height;
        // Grow when needed, shrink when far too big (left single view): memory follows the tile size.
        if (s.Data == null || s.Capacity < need || s.Capacity > need * 2 + 65536)
        {
            if (s.Data != null) NativeMemory.AlignedFree(s.Data);
            s.Data = null;
            s.Capacity = 0;
            s.Data = (byte*)NativeMemory.AlignedAlloc(need, 64);
            s.Capacity = need;
        }
        s.Width = width;
        s.Height = height;
        s.Stride = stride;
        return s.Data;
    }

    /// <summary>Decoder thread: makes the slot written by BeginWrite the latest frame.</summary>
    public long Publish()
    {
        var s = _slots[_writer];
        long seq = Interlocked.Increment(ref _seq);
        s.Seq = seq;
        int prev = Interlocked.Exchange(ref _shared, _writer | Fresh);
        _writer = prev & 3;
        return seq;
    }

    public bool TryCopyLatest(ref long lastSequence, FrameCopier copier)
    {
        lock (_frameLock)
        {
            if (_freed) return false;
            if ((Volatile.Read(ref _shared) & Fresh) != 0)
            {
                int prev = Interlocked.Exchange(ref _shared, _reader);
                _reader = prev & 3;
            }
            var s = _slots[_reader];
            if (s.Data == null || s.Seq <= lastSequence) return false;
            copier((IntPtr)s.Data, s.Width, s.Height, s.Stride);
            lastSequence = s.Seq;
            return true;
        }
    }

    /// <summary>Decoder thread, after its last write: frees all slots (waits for a running copy).</summary>
    public void Free()
    {
        lock (_frameLock)
        {
            if (_freed) return;
            _freed = true;
            foreach (var s in _slots)
            {
                if (s.Data != null) NativeMemory.AlignedFree(s.Data);
                s.Data = null;
                s.Capacity = 0;
            }
        }
    }
}
