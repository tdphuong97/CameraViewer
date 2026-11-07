using Cv.Core;
using Cv.Core.Export;
using Cv.Media.Internal;
using FFmpeg.AutoGen;

namespace Cv.Media;

/// <summary>
/// Remux / duration / record with the FFmpeg libraries in-process (stream copy only). Exceptions carry a short
/// Vietnamese Message that can be shown as is; cancellation throws OperationCanceledException.
/// </summary>
public sealed unsafe class FFmpegMediaTools : IMediaTools
{
    private static readonly TimeSpan FileOpenTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StreamOpenTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan StreamStall = TimeSpan.FromSeconds(15);
    private const string LostDuringDownload = "Mất kết nối đầu ghi khi đang tải";

    public Task RemuxAsync(IReadOnlyList<RemuxPart> parts, string outputPath, IProgress<double>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(parts);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);
        if (parts.Count == 0) throw new ArgumentException("Không có phần video nào để ghép", nameof(parts));
        return RunBlocking(() => { Remux(parts, outputPath, progress, ct); return true; }, ct);
    }

    public Task<TimeSpan?> GetDurationAsync(string path, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return RunBlocking(() => Duration(path, ct), ct);
    }

    public Task RecordAsync(string url, TimeSpan duration, string outputPath, IProgress<double>? progress, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(url);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);
        if (duration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
        return RunBlocking(() => { Record(url, duration, outputPath, progress, ct); return true; }, ct);
    }

    private static Task<T> RunBlocking<T>(Func<T> work, CancellationToken ct)
    {
        FFmpegRuntime.EnsureInitialized();
        return Task.Factory.StartNew(work, ct, TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
    }

    // ------------------------------------------------------------------ remux

    private static void Remux(IReadOnlyList<RemuxPart> parts, string outputPath, IProgress<double>? progress, CancellationToken ct)
    {
        var tmp = outputPath + ".part";
        var sizes = parts.Select(p => File.Exists(p.Path) ? new FileInfo(p.Path).Length : throw new FileNotFoundException(FFmpegError.Message(Failure.NoFile), p.Path)).ToArray();
        double total = Math.Max(1, sizes.Sum());
        long done = 0;
        var report = new ProgressThrottle(progress);
        using var irq = new InterruptState(ct);
        Mp4Writer? w = null;
        try
        {
            long cursorUs = 0;
            for (int i = 0; i < parts.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                using var input = InputFile.Open(parts[i].Path, irq, FileOpenTimeout, ct);
                if (w == null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(tmp))!);
                    w = Mp4Writer.Create(tmp, irq, input.Video, input.Audio);
                }
                else if (input.Video->codecpar->codec_id != w.VideoCodec)
                    throw new InvalidDataException("Các đoạn video không cùng định dạng, không ghép được");

                long before = done;
                cursorUs = CopyPart(input, w, parts[i].From, parts[i].To, cursorUs, isLive: false, irq, ct,
                    bytes => report.Report((before + bytes) / total), null);
                done += sizes[i];
                report.Report(done / total * 0.99);
            }
            w!.Finish();
            w.Dispose();
            w = null;
            File.Move(tmp, outputPath, overwrite: true);
            report.Complete();
            Log.Info($"Remux done: {parts.Count} part(s) -> {outputPath}");
        }
        catch (Exception ex)
        {
            w?.Dispose();
            TryDelete(tmp);
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            Log.Warn("Remux failed: " + outputPath, ex);
            throw;
        }
    }

    /// <summary>
    /// Copies one input onto the output timeline at <paramref name="cursorUs"/>, trimmed to [from, to) starting at the
    /// keyframe at or before from. Returns the new cursor (end of what was written).
    /// </summary>
    private static long CopyPart(InputFile input, Mp4Writer w, TimeSpan? from, TimeSpan? to, long cursorUs, bool isLive,
        InterruptState irq, CancellationToken ct, Action<long>? onBytes, Action<long>? onMediaUs, bool toFromStart = false)
    {
        AVFormatContext* ic = input.Ctx;
        int vi = input.VideoIndex, ai = w.HasAudio ? input.AudioIndex : -1;
        if (ai >= 0 && input.Audio->codecpar->codec_id != w.AudioCodec) ai = -1;
        AVStream* vst = input.Video;
        long frameDur = MediaUtil.FrameDuration(vst);
        var unwrapPts = new TimestampUnwrap[ic->nb_streams];
        var unwrapDts = new TimestampUnwrap[ic->nb_streams];
        var gop = new List<nint>();
        long gopStartUs = 0;
        long originUs = long.MinValue, fromUs = long.MinValue, toUs = long.MaxValue, startUs = 0;
        long lastVideoTs = ffmpeg.AV_NOPTS_VALUE;
        bool started = false;
        int count = 0;
        AVPacket* pkt = ffmpeg.av_packet_alloc();

        void Emit(AVPacket* p, int track, AVStream* st)
        {
            long offset = ffmpeg.av_rescale_q(cursorUs - startUs, MediaUtil.MicroBase, st->time_base);
            if (p->pts != ffmpeg.AV_NOPTS_VALUE) p->pts += offset;
            if (p->dts != ffmpeg.AV_NOPTS_VALUE) p->dts += offset;
            w.Write(p, track, st->time_base);
        }

        void FlushGop()
        {
            foreach (var g in gop)
            {
                var p = (AVPacket*)g;
                bool isVideo = p->stream_index == vi;
                AVStream* st = ic->streams[p->stream_index];
                long ts = p->pts != ffmpeg.AV_NOPTS_VALUE ? p->pts : p->dts;
                if (isVideo || ffmpeg.av_rescale_q(ts, st->time_base, MediaUtil.MicroBase) >= startUs) Emit(p, isVideo ? 0 : 1, st);
                ffmpeg.av_packet_free(&p);
            }
            gop.Clear();
        }

        void DropGop()
        {
            foreach (var g in gop) { var p = (AVPacket*)g; ffmpeg.av_packet_free(&p); }
            gop.Clear();
        }

        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (isLive) irq.SetDeadline(StreamStall);
                int r = ffmpeg.av_read_frame(ic, pkt);
                if (isLive)
                {
                    irq.ClearDeadline();
                    // An aborted read can still return the parser's buffered frame: the link is stalled anyway.
                    if (r >= 0 && irq.TimedOut) { ffmpeg.av_packet_unref(pkt); r = ffmpeg.AVERROR_EXIT; }
                }
                if (r == ffmpeg.AVERROR_EOF) break;
                if (r == ffmpeg.AVERROR(ffmpeg.EAGAIN)) continue;
                if (r < 0)
                {
                    if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                    if (isLive) throw new IOException(LostDuringDownload, new IOException(irq.TimedOut ? "stall" : FFmpegError.Describe(r)));
                    // A damaged tail is common in downloaded parts: keep what was read.
                    if (started) { Log.Warn("Input ended with an error, keeping what was read: " + FFmpegError.Describe(r)); break; }
                    throw FFmpegError.ToException(r, "read");
                }
                if (onBytes != null && (++count & 63) == 0 && ic->pb != null) onBytes(ffmpeg.avio_tell(ic->pb));

                int si = pkt->stream_index;
                int track = si == vi ? 0 : si == ai ? 1 : -1;
                if (track < 0) { ffmpeg.av_packet_unref(pkt); continue; }
                AVStream* st = ic->streams[si];
                pkt->pts = unwrapPts[si].Apply(pkt->pts, st->pts_wrap_bits);
                pkt->dts = unwrapDts[si].Apply(pkt->dts, st->pts_wrap_bits);
                long ts = pkt->pts != ffmpeg.AV_NOPTS_VALUE ? pkt->pts : pkt->dts;
                if (track == 0 && ts == ffmpeg.AV_NOPTS_VALUE && lastVideoTs != ffmpeg.AV_NOPTS_VALUE)
                {
                    ts = lastVideoTs + frameDur;
                    pkt->pts = ts;
                    if (pkt->dts == ffmpeg.AV_NOPTS_VALUE) pkt->dts = ts;
                }
                if (ts == ffmpeg.AV_NOPTS_VALUE) { ffmpeg.av_packet_unref(pkt); continue; }
                long tsUs = ffmpeg.av_rescale_q(ts, st->time_base, MediaUtil.MicroBase);
                bool key = (pkt->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0;

                if (track == 0)
                {
                    if (pkt->duration <= 0) pkt->duration = frameDur;
                    lastVideoTs = ts;
                    if (originUs == long.MinValue)
                    {
                        originUs = tsUs;
                        if (from is { } f && f > TimeSpan.Zero) fromUs = originUs + f.Ticks / 10;
                        if (to is { } t) toUs = originUs + t.Ticks / 10;
                    }
                    if (tsUs >= toUs && (started || gop.Count == 0)) { ffmpeg.av_packet_unref(pkt); break; }
                }

                if (!started)
                {
                    if (fromUs != long.MinValue)
                    {
                        if (track == 0 && tsUs <= fromUs)
                        {
                            if (key) { DropGop(); gopStartUs = tsUs; gop.Add((nint)ffmpeg.av_packet_clone(pkt)); }
                            else if (gop.Count > 0) gop.Add((nint)ffmpeg.av_packet_clone(pkt));
                            ffmpeg.av_packet_unref(pkt);
                            continue;
                        }
                        if (track == 1)
                        {
                            if (gop.Count > 0) gop.Add((nint)ffmpeg.av_packet_clone(pkt));
                            ffmpeg.av_packet_unref(pkt);
                            continue;
                        }
                        // First video packet past From: the buffered GOP (keyframe at or before From) starts the part.
                        if (gop.Count > 0) { started = true; startUs = gopStartUs; FlushGop(); }
                        else if (key) { started = true; startUs = tsUs; }
                        else { ffmpeg.av_packet_unref(pkt); continue; }
                        if (tsUs >= toUs) { ffmpeg.av_packet_unref(pkt); break; }
                    }
                    else
                    {
                        // No trim: start at the first keyframe (leading pictures cannot be decoded).
                        if (track == 0 && key)
                        {
                            started = true;
                            startUs = tsUs;
                            if (toFromStart && to is { } t) toUs = startUs + t.Ticks / 10;
                        }
                        else { ffmpeg.av_packet_unref(pkt); continue; }
                    }
                }

                if (track == 1 && (tsUs < startUs || tsUs >= toUs)) { ffmpeg.av_packet_unref(pkt); continue; }
                Emit(pkt, track, st);
                if (track == 0) onMediaUs?.Invoke(tsUs - startUs);
            }
            // From fell inside the last GOP of the file.
            if (!started && gop.Count > 0) { started = true; startUs = gopStartUs; FlushGop(); }
            if (!started) Log.Warn("Part had no usable video (no keyframe in range)");
            return started ? Math.Max(cursorUs, w.EndUs) : cursorUs;
        }
        finally
        {
            DropGop();
            ffmpeg.av_packet_free(&pkt);
        }
    }

    // ------------------------------------------------------------------ duration

    private static TimeSpan? Duration(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return null;
        using var irq = new InterruptState(ct);
        AVFormatContext* ic = ffmpeg.avformat_alloc_context();
        if (ic == null) return null;
        irq.Attach(ic);
        // Timestamps only: no parsers, no stream probing. PS duration estimates are unreliable, packets are not.
        ic->flags |= ffmpeg.AVFMT_FLAG_NOPARSE | ffmpeg.AVFMT_FLAG_NOFILLIN;
        AVPacket* pkt = null;
        try
        {
            int r = ffmpeg.avformat_open_input(&ic, path, MediaUtil.SniffFormat(path), null);
            if (r < 0) { Log.Warn($"Duration: cannot open {path}: {FFmpegError.Describe(r)}"); return null; }
            pkt = ffmpeg.av_packet_alloc();
            var first = new Dictionary<int, long>();
            var max = new Dictionary<int, long>();
            var counts = new Dictionary<int, int>();
            var unwrap = new Dictionary<int, TimestampUnwrap>();
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                r = ffmpeg.av_read_frame(ic, pkt);
                if (r == ffmpeg.AVERROR(ffmpeg.EAGAIN)) continue;
                if (r < 0) break; // EOF or damaged tail: use what was read
                int si = pkt->stream_index;
                AVStream* st = ic->streams[si];
                long ts = pkt->pts != ffmpeg.AV_NOPTS_VALUE ? pkt->pts : pkt->dts;
                if (st->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO && ts != ffmpeg.AV_NOPTS_VALUE)
                {
                    var u = unwrap.GetValueOrDefault(si);
                    ts = u.Apply(ts, st->pts_wrap_bits);
                    unwrap[si] = u;
                    if (!first.ContainsKey(si)) first[si] = ts;
                    if (!max.TryGetValue(si, out var m) || ts > m) max[si] = ts;
                    counts[si] = counts.GetValueOrDefault(si) + 1;
                }
                ffmpeg.av_packet_unref(pkt);
            }
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            if (counts.Count == 0) return null;
            int best = counts.MaxBy(kv => kv.Value).Key;
            long ticks = ffmpeg.av_rescale_q(max[best] - first[best], ic->streams[best]->time_base, MediaUtil.TicksBase);
            return TimeSpan.FromTicks(Math.Max(0, ticks));
        }
        finally
        {
            if (pkt != null) ffmpeg.av_packet_free(&pkt);
            if (ic != null) ffmpeg.avformat_close_input(&ic);
        }
    }

    // ------------------------------------------------------------------ record

    private static void Record(string url, TimeSpan duration, string outputPath, IProgress<double>? progress, CancellationToken ct)
    {
        var tmp = outputPath + ".part";
        var report = new ProgressThrottle(progress);
        using var irq = new InterruptState(ct);
        Mp4Writer? w = null;
        bool network = MediaUtil.IsUrl(url);
        try
        {
            using var input = InputFile.Open(url, irq, network ? StreamOpenTimeout : FileOpenTimeout, ct);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(tmp))!);
            w = Mp4Writer.Create(tmp, irq, input.Video, input.Audio);
            double durUs = duration.Ticks / 10.0;
            CopyPart(input, w, null, duration, 0, isLive: network, irq, ct, null, us => report.Report(us / durUs), toFromStart: true);
            if (w.EndUs <= 0) throw new InvalidDataException("Không nhận được hình từ đầu ghi");
            w.Finish();
            w.Dispose();
            w = null;
            File.Move(tmp, outputPath, overwrite: true);
            report.Complete();
            Log.Info($"Record done: {Log.Redact(url)} -> {outputPath}");
        }
        catch (Exception ex)
        {
            w?.Dispose();
            TryDelete(tmp);
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            Log.Warn("Record failed: " + Log.Redact(url), ex);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
