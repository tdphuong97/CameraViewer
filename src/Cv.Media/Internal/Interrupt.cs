using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace Cv.Media.Internal;

/// <summary>
/// Backs AVFormatContext.interrupt_callback: FFmpeg polls it during every blocking I/O call, so a stop flag or a
/// deadline turns a hung network read into AVERROR_EXIT within ~100 ms.
/// </summary>
internal sealed unsafe class InterruptState : IDisposable
{
    private GCHandle _handle;
    private long _deadline; // Stopwatch timestamp, 0 = none
    private volatile bool _stop;
    private volatile bool _timedOut;
    private readonly CancellationToken _token;

    public InterruptState(CancellationToken token = default)
    {
        _token = token;
        _handle = GCHandle.Alloc(this, GCHandleType.Normal);
    }

    public bool Stopped => _stop || _token.IsCancellationRequested;

    /// <summary>True when the last abort came from the deadline (not from Stop/cancel).</summary>
    public bool TimedOut => _timedOut;

    public void Stop() => _stop = true;

    public void SetDeadline(TimeSpan fromNow)
    {
        _timedOut = false;
        Volatile.Write(ref _deadline, Stopwatch.GetTimestamp() + (long)(fromNow.TotalSeconds * Stopwatch.Frequency));
    }

    public void ClearDeadline() => Volatile.Write(ref _deadline, 0);

    /// <summary>Puts the callback into a format context (call before avformat_open_input / avio_open2).</summary>
    public void Attach(AVFormatContext* ctx)
    {
        ctx->interrupt_callback.callback = Callback;
        ctx->interrupt_callback.opaque = (void*)GCHandle.ToIntPtr(_handle);
    }

    public AVIOInterruptCB Native => new()
    {
        callback = Callback,
        opaque = (void*)GCHandle.ToIntPtr(_handle),
    };

    private static AVIOInterruptCB_callback_func Callback =>
        new() { Pointer = (IntPtr)(delegate* unmanaged[Cdecl]<void*, int>)&OnInterrupt };

    private int Check()
    {
        if (_stop || _token.IsCancellationRequested) return 1;
        long d = Volatile.Read(ref _deadline);
        if (d != 0 && Stopwatch.GetTimestamp() > d) { _timedOut = true; return 1; }
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnInterrupt(void* opaque)
    {
        try
        {
            return GCHandle.FromIntPtr((IntPtr)opaque).Target is InterruptState s ? s.Check() : 1;
        }
        catch
        {
            return 1;
        }
    }

    /// <summary>Only after every context using it is closed.</summary>
    public void Dispose()
    {
        if (_handle.IsAllocated) _handle.Free();
    }
}
