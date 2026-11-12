using System.Diagnostics;
using System.Runtime.InteropServices;
using WaveFormatEx = Cv.Media.WaveOutNative.WaveFormatEx;
using WaveHdr = Cv.Media.WaveOutNative.WaveHdr;

namespace Cv.Media;

/// <summary>
/// Live audio through winmm waveOut, with no extra thread and no driver callback.
/// <para>
/// Ring of <see cref="Slots"/> native buffers of about <see cref="BufferMs"/> ms. Everything the driver touches (headers and
/// sample memory) is unmanaged and never moves. Finished buffers are found by polling WHDR_DONE on every call: a callback
/// would run on a driver thread where calling waveOut* can deadlock, and a thread would cost RAM for no gain.
/// </para>
/// <para>
/// Playback starts only after <see cref="PreBufferMs"/> ms are waiting (also after an underrun), so a jittery network does
/// not turn into a stream of glitchy fragments. More than <see cref="MaxQueued"/> waiting means the NEW samples are dropped.
/// </para>
/// All calls come from the decoder thread except Dispose; one lock makes that safe.
/// </summary>
public sealed unsafe class WaveOutSink : IAudioSink
{
    /// <summary>12 x 40 ms = 480 ms of ring, more than MaxQueued so the cap (not the ring) decides what is dropped.</summary>
    private const int Slots = 12;
    private const int BufferMs = 40;
    private const int PreBufferMs = 120;
    private const int MaxQueuedMs = 400;

    private const string NoDeviceMessage = "Máy không có loa hoặc tai nghe";
    private const string BadFormatMessage = "Tiếng của camera này chưa phát được";

    /// <summary>Most audio that may be waiting; beyond it Write drops the new samples.</summary>
    public static readonly TimeSpan MaxQueued = TimeSpan.FromMilliseconds(MaxQueuedMs);

    /// <summary>Length of one ring buffer (the granularity of <see cref="Queued"/>: a buffer counts until it is fully played).</summary>
    internal static readonly TimeSpan BufferDuration = TimeSpan.FromMilliseconds(BufferMs);

    /// <summary>Audio collected before the first write to the device.</summary>
    internal static readonly TimeSpan PreBuffer = TimeSpan.FromMilliseconds(PreBufferMs);

    private readonly object _gate = new();
    private bool _disposed;
    private string? _error;

    // Open device. _hwo == 0 means closed; _hdrs/_buf exist exactly while the device is open.
    private nint _hwo;
    private WaveHdr* _hdrs;
    private byte* _buf;
    private int _prepared; // headers 0.._prepared-1 are prepared and must be unprepared before the memory goes away

    private int _rate, _frameBytes, _bufBytes, _preBytes, _maxBytes;

    // Ring bookkeeping. Slot order: [_oldest .. _oldest+_count) are complete buffers; the last _pending of them are not yet
    // written to the device (only while waiting for the pre-buffer); slot _oldest+_count is the partial buffer being filled.
    private int _oldest, _count, _pending, _fill;
    private int _queuedBytes; // bytes in the _count complete buffers (the partial one is _fill)
    private bool _playing;    // false = device paused, waiting for the pre-buffer

    public string? Error
    {
        get { lock (_gate) return _error; }
    }

    public TimeSpan Queued
    {
        get
        {
            lock (_gate)
            {
                if (_hwo == 0) return TimeSpan.Zero;
                Poll();
                return ToTime(_queuedBytes + _fill);
            }
        }
    }

    public bool Start(int sampleRate, int channels)
    {
        lock (_gate)
        {
            CloseDevice();
            _error = null;
            if (_disposed)
            {
                _error = NoDeviceMessage;
                return false;
            }
            // Mono and stereo only: that is all the app produces, and a wrong "no speakers" message would mislead for others.
            if (channels is < 1 or > 2 || sampleRate is < 1000 or > 384000)
            {
                _error = BadFormatMessage;
                return false;
            }

            int frameBytes = channels * sizeof(short);
            var format = new WaveFormatEx
            {
                FormatTag = 1, // WAVE_FORMAT_PCM
                Channels = (ushort)channels,
                SamplesPerSec = (uint)sampleRate,
                AvgBytesPerSec = (uint)(sampleRate * frameBytes),
                BlockAlign = (ushort)frameBytes,
                BitsPerSample = 16,
                Size = 0,
            };

            uint r;
            nint hwo;
            try
            {
                r = WaveOutNative.WaveOutOpen(out hwo, WaveOutNative.WaveMapper, &format, 0, 0, WaveOutNative.CallbackNull);
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
                // No winmm at all (stripped-down Windows): same outcome for the user as no device.
                return Fail();
            }
            if (r != 0) return Fail();

            _hwo = hwo;
            _rate = sampleRate;
            _frameBytes = frameBytes;
            _bufBytes = FramesFor(BufferMs) * frameBytes;
            _preBytes = FramesFor(PreBufferMs) * frameBytes;
            _maxBytes = FramesFor(MaxQueuedMs) * frameBytes;

            // Zeroed memory: whatever a bug leaves unwritten plays as silence, never as noise.
            _hdrs = (WaveHdr*)NativeMemory.AllocZeroed((nuint)Slots, (nuint)sizeof(WaveHdr));
            _buf = (byte*)NativeMemory.AllocZeroed((nuint)(Slots * _bufBytes));
            for (int i = 0; i < Slots; i++)
            {
                _hdrs[i].Data = _buf + (long)i * _bufBytes;
                _hdrs[i].BufferLength = (uint)_bufBytes;
                if (WaveOutNative.WaveOutPrepareHeader(hwo, &_hdrs[i], (uint)sizeof(WaveHdr)) != 0)
                {
                    CloseDevice();
                    return Fail();
                }
                _prepared = i + 1;
            }

            // Paused until the pre-buffer is queued, so the first buffers start together instead of one by one.
            WaveOutNative.WaveOutPause(hwo);
            return true;
        }

        bool Fail()
        {
            _error = NoDeviceMessage;
            return false;
        }
    }

    public void Write(ReadOnlySpan<short> interleaved)
    {
        if (interleaved.IsEmpty) return;
        lock (_gate)
        {
            if (_hwo == 0) return;
            Poll();

            var bytes = MemoryMarshal.AsBytes(interleaved);
            bytes = bytes[..(bytes.Length / _frameBytes * _frameBytes)]; // never split a frame between channels
            while (!bytes.IsEmpty)
            {
                int room = _maxBytes - _queuedBytes - _fill;
                // Over the cap (or no free slot, which the cap makes unreachable): drop the NEW samples instead of waiting.
                if (room < _frameBytes || _count >= Slots) break;

                int take = Math.Min(Math.Min(bytes.Length, room), _bufBytes - _fill);
                var dest = new Span<byte>(_buf + (long)((_oldest + _count) % Slots) * _bufBytes + _fill, take);
                bytes[..take].CopyTo(dest);
                bytes = bytes[take..];
                _fill += take;

                if (_fill == _bufBytes) CompleteCurrent();
                if (!_playing && _queuedBytes + _fill >= _preBytes) StartPlayback();
            }
        }
    }

    public void Flush()
    {
        lock (_gate)
        {
            if (_hwo == 0) return;
            if (_count - _pending > 0)
            {
                // Only reset cancels buffers already handed to the driver. It returns them marked done.
                WaveOutNative.WaveOutReset(_hwo);
                long deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 10;
                while (true)
                {
                    ReapDone();
                    if (_count - _pending == 0 || Stopwatch.GetTimestamp() > deadline) break;
                    Thread.Sleep(1);
                }
            }
            // Buffers not yet given to the device were never in the driver's hands: just forget them.
            while (_pending > 0)
            {
                _queuedBytes -= (int)_hdrs[(_oldest + _count - 1) % Slots].BufferLength;
                _count--;
                _pending--;
            }
            _fill = 0;
            EnterPrebuffer();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            CloseDevice();
        }
    }

    /// <summary>Finds finished buffers, then handles the two end-of-data cases while playing.</summary>
    private void Poll()
    {
        ReapDone();
        if (!_playing) return;
        int inFlight = _count - _pending;
        if (inFlight == 0)
        {
            // Underrun: everything played and nothing new. Wait for the pre-buffer again rather than play fragments.
            EnterPrebuffer();
        }
        else if (inFlight <= 1 && _fill > 0)
        {
            // The device is about to run dry and a partial buffer is waiting: send it as a short buffer. Otherwise the tail
            // of a stream whose length is not a whole number of buffers would never play.
            CompleteCurrent();
        }
    }

    /// <summary>Takes back buffers the driver finished. Driver completes them in the order they were written.</summary>
    private void ReapDone()
    {
        while (_count - _pending > 0)
        {
            WaveHdr* h = &_hdrs[_oldest];
            // Another thread (the driver's) sets this flag.
            if ((Volatile.Read(ref h->Flags) & WaveOutNative.WhdrDone) == 0) break;
            _queuedBytes -= (int)h->BufferLength;
            _oldest = (_oldest + 1) % Slots;
            _count--;
        }
    }

    /// <summary>Closes the partial buffer. It goes to the device now when playing, else it waits with the other pending ones.</summary>
    private void CompleteCurrent()
    {
        int slot = (_oldest + _count) % Slots;
        _hdrs[slot].BufferLength = (uint)_fill;
        _queuedBytes += _fill;
        _fill = 0;
        _count++;
        if (_playing) WriteSlot(slot);
        else _pending++;
    }

    /// <summary>Pre-buffer reached: hand everything waiting to the device, then let it run.</summary>
    private void StartPlayback()
    {
        if (_fill > 0) CompleteCurrent(); // while !_playing this just counts it as pending
        int first = (_oldest + _count - _pending) % Slots;
        for (int k = 0; k < _pending; k++) WriteSlot((first + k) % Slots);
        _pending = 0;
        _playing = true;
        WaveOutNative.WaveOutRestart(_hwo);
    }

    private void WriteSlot(int slot)
    {
        WaveHdr* h = &_hdrs[slot];
        if (WaveOutNative.WaveOutWrite(_hwo, h, (uint)sizeof(WaveHdr)) != 0)
        {
            // The driver refused it, so it is ours again. Mark it done ourselves or the ring would lose a slot per failure.
            Volatile.Write(ref h->Flags, (h->Flags & ~WaveOutNative.WhdrInQueue) | WaveOutNative.WhdrDone);
        }
    }

    /// <summary>Pause explicitly: after waveOutReset or an idle spell the device may be running, and then the first buffers would start one by one.</summary>
    private void EnterPrebuffer()
    {
        _playing = false;
        WaveOutNative.WaveOutPause(_hwo);
    }

    /// <summary>Order matters: reset (returns queued buffers), unprepare every header, close, and only then free the memory.</summary>
    private void CloseDevice()
    {
        if (_hwo == 0) return;
        nint hwo = _hwo;
        WaveOutNative.WaveOutReset(hwo);

        // One shared time budget, so a stuck driver costs about a second in total and not a second per header.
        long deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
        bool driverReleased = true;
        for (int i = 0; i < _prepared; i++)
        {
            uint r = WaveOutNative.WaveOutUnprepareHeader(hwo, &_hdrs[i], (uint)sizeof(WaveHdr));
            while (r == WaveOutNative.WaveErrStillPlaying && Stopwatch.GetTimestamp() < deadline)
            {
                Thread.Sleep(2);
                r = WaveOutNative.WaveOutUnprepareHeader(hwo, &_hdrs[i], (uint)sizeof(WaveHdr));
            }
            if (r != 0) driverReleased = false;
        }
        WaveOutNative.WaveOutClose(hwo);

        // If the driver may still hold a header, freeing its memory could corrupt the heap. Leaking a few KB is harmless.
        if (driverReleased)
        {
            NativeMemory.Free(_hdrs);
            NativeMemory.Free(_buf);
        }
        _hdrs = null;
        _buf = null;
        _hwo = 0;
        _prepared = 0;
        _oldest = _count = _pending = _fill = _queuedBytes = 0;
        _playing = false;
    }

    private int FramesFor(int ms) => (int)((long)_rate * ms / 1000);

    private TimeSpan ToTime(int bytes) => new((long)(bytes / _frameBytes) * TimeSpan.TicksPerSecond / _rate);
}

/// <summary>winmm waveOut declarations. Headers follow the Windows layout: natural packing for WAVEHDR (48 bytes on x64, 32 on x86), packed WAVEFORMATEX (18 bytes).</summary>
internal static unsafe partial class WaveOutNative
{
    internal const uint WaveMapper = 0xFFFFFFFF; // default output device
    internal const uint CallbackNull = 0;
    internal const uint WhdrDone = 0x1;
    internal const uint WhdrInQueue = 0x10;
    internal const uint WaveErrStillPlaying = 33;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct WaveFormatEx
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSec;
        public uint AvgBytesPerSec;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WaveHdr
    {
        public byte* Data;
        public uint BufferLength;
        public uint BytesRecorded;
        public nuint User;
        public uint Flags;
        public uint Loops;
        public WaveHdr* Next;
        public nuint Reserved;
    }

    [LibraryImport("winmm.dll", EntryPoint = "waveOutOpen"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial uint WaveOutOpen(out nint hwo, uint deviceId, WaveFormatEx* format, nint callback, nint instance, uint flags);

    [LibraryImport("winmm.dll", EntryPoint = "waveOutClose"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial uint WaveOutClose(nint hwo);

    [LibraryImport("winmm.dll", EntryPoint = "waveOutPrepareHeader"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial uint WaveOutPrepareHeader(nint hwo, WaveHdr* header, uint size);

    [LibraryImport("winmm.dll", EntryPoint = "waveOutUnprepareHeader"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial uint WaveOutUnprepareHeader(nint hwo, WaveHdr* header, uint size);

    [LibraryImport("winmm.dll", EntryPoint = "waveOutWrite"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial uint WaveOutWrite(nint hwo, WaveHdr* header, uint size);

    [LibraryImport("winmm.dll", EntryPoint = "waveOutPause"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial uint WaveOutPause(nint hwo);

    [LibraryImport("winmm.dll", EntryPoint = "waveOutRestart"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial uint WaveOutRestart(nint hwo);

    [LibraryImport("winmm.dll", EntryPoint = "waveOutReset"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial uint WaveOutReset(nint hwo);
}
