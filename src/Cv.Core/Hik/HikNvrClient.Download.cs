using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Cv.Core.Nvr;

namespace Cv.Core.Hik;

internal sealed partial class HikNvrClient
{
    private const string DownloadPath = "/ISAPI/ContentMgmt/download";

    private sealed record PartPlan(string Uri, DateTime NominalStart, DateTime CoveredFrom, DateTime CoveredTo);

    private sealed record Attempt(DownloadMethod Method, HttpMethod Verb);

    private readonly object _methodLock = new();
    private Attempt? _working; // the method (and verb) that worked last on this recorder

    /// <summary>The method that worked last, if any (for the log and tests).</summary>
    internal DownloadMethod? WorkingMethod { get { lock (_methodLock) return _working?.Method; } }

    public async Task<IReadOnlyList<DownloadedPart>> DownloadAsync(int channel, DateTime from, DateTime to, string folder, IProgress<long>? bytes, CancellationToken ct)
    {
        if (to <= from) throw new ArgumentException("Thời điểm kết thúc phải sau thời điểm bắt đầu.", nameof(to));
        var mode = await ResolveTimeModeAsync(ct);
        var offset = mode == TimeMode.Utc ? await OffsetAsync(ct) : TimeSpan.Zero;
        var segments = (await SearchAsync(channel, from, to, ct)).Where(s => s.End > from && s.Start < to).ToList();
        if (segments.Count == 0) throw new NvrException(HikErrors.NothingRecorded);
        Directory.CreateDirectory(folder);

        string Wire(DateTime t) => HikTime.UriZ(HikTime.ToWire(t, mode, offset));
        DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
        DateTime Min(DateTime a, DateTime b) => a < b ? a : b;

        var host = UriHost(HostOnly(Config.Host)) + (Config.RtspPort == 554 ? "" : ":" + Config.RtspPort.ToString(CultureInfo.InvariantCulture));
        var plans = new Dictionary<DownloadMethod, List<PartPlan>?>
        {
            [DownloadMethod.ByTime] = RecordingTimeline.Merge(segments)
                .Select(s => (A: Max(from, s.Start), B: Min(to, s.End))).Where(r => r.B > r.A)
                .Select(r => new PartPlan(
                    $"rtsp://{host}/Streaming/tracks/{TrackId(channel, StreamKind.Main)}/?starttime={Wire(r.A)}&endtime={Wire(r.B)}",
                    r.A, r.A, r.B))
                .ToList(),
            [DownloadMethod.BySegmentRange] = segments.Any(s => s.PlaybackUri.Length == 0) ? null : segments
                .Select(s => (S: s, A: Max(from, s.Start), B: Min(to, s.End))).Where(r => r.B > r.A)
                .Select(r => new PartPlan(Narrow(r.S.PlaybackUri, Wire(r.A), Wire(r.B)), r.A, r.A, r.B))
                .ToList(),
            [DownloadMethod.BySegmentWhole] = segments.Any(s => s.PlaybackUri.Length == 0) ? null : segments
                .Select(s => (S: s, A: Max(from, s.Start), B: Min(to, s.End))).Where(r => r.B > r.A)
                .Select(r => new PartPlan(r.S.PlaybackUri, r.S.Start, r.A, r.B))
                .ToList(),
        };

        Attempt? working;
        lock (_methodLock) working = _working;
        var order = new List<DownloadMethod> { DownloadMethod.ByTime, DownloadMethod.BySegmentRange, DownloadMethod.BySegmentWhole };
        if (working is not null) { order.Remove(working.Method); order.Insert(0, working.Method); }

        var reasons = new List<string>();
        foreach (var method in order)
        {
            if (plans[method] is not { Count: > 0 } plan) { reasons.Add($"{method}: no playback URI"); continue; }
            var verbs = working?.Method == method && working.Verb == HttpMethod.Post
                ? new[] { HttpMethod.Post, HttpMethod.Get }
                : new[] { HttpMethod.Get, HttpMethod.Post };
            var parts = await TryMethodAsync(method, verbs, plan, channel, from, folder, bytes, reasons, ct);
            if (parts is not null) return parts;
        }
        Log.Warn($"Download ch{channel} {from:yyyy-MM-dd HH:mm:ss}-{to:HH:mm:ss}: no ISAPI method worked: {string.Join(" | ", reasons)}");
        throw new NvrDownloadUnsupportedException(HikErrors.Unsupported);
    }

    /// <summary>Downloads every part with one method; null (and no file left) when the recorder refuses it.</summary>
    private async Task<IReadOnlyList<DownloadedPart>?> TryMethodAsync(DownloadMethod method, HttpMethod[] verbs, List<PartPlan> plan,
        int channel, DateTime from, string folder, IProgress<long>? bytes, List<string> reasons, CancellationToken ct)
    {
        var written = new List<string>();
        var parts = new List<DownloadedPart>(plan.Count);
        long done = 0;
        HttpMethod? verb = null;
        bytes?.Report(0);
        try
        {
            for (var i = 0; i < plan.Count; i++)
            {
                var path = Path.Combine(folder, $"ch{channel}_{from:yyyyMMddHHmmss}_{i + 1}.ps");
                written.Add(path);
                long got = -1;
                foreach (var v in verb is null ? verbs : [verb])
                {
                    var before = done;
                    var r = await TryOneAsync(v, plan[i].Uri, path, n => bytes?.Report(before + n), ct);
                    if (r.Bytes >= 0) { verb = v; got = r.Bytes; break; }
                    reasons.Add($"{method}/{v.Method} part {i + 1}: {r.Reason}");
                }
                if (got < 0)
                {
                    DeleteAll(written);
                    return null;
                }
                done += got;
                parts.Add(new DownloadedPart(path, plan[i].NominalStart, plan[i].CoveredFrom, plan[i].CoveredTo, method));
            }
        }
        catch
        {
            DeleteAll(written);
            throw;
        }
        lock (_methodLock) _working = new Attempt(method, verb!);
        Log.Info($"Download ch{channel}: {method} via {verb!.Method}, {parts.Count} part(s), {done} bytes");
        return parts;
    }

    private readonly record struct OneResult(long Bytes, string Reason);

    /// <summary>
    /// One download request. Returns the bytes written, or -1 with a reason when the answer is not a PS stream (XML
    /// error, HTTP error, other content). Throws on 401, network loss, stall and cancellation (partial file deleted).
    /// </summary>
    private async Task<OneResult> TryOneAsync(HttpMethod verb, string playbackUri, string path, Action<long> onBytes, CancellationToken ct)
    {
        var http = Http;
        using var req = new HttpRequestMessage(verb, DownloadPath)
        {
            Content = new StringContent(HikXml.BuildDownloadRequest(playbackUri), Encoding.UTF8, "application/xml"),
        };
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        HttpResponseMessage resp;
        await _gate.WaitAsync(ct);
        try
        {
            stall.CancelAfter(StallTimeout);
            resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, stall.Token);
        }
        catch (OperationCanceledException e) when (!ct.IsCancellationRequested) { throw Unreachable(e); }
        catch (HttpRequestException e) { throw Unreachable(e); }
        catch (IOException e) { throw Unreachable(e); }
        catch (ObjectDisposedException e) { throw Unreachable(e); }
        finally { _gate.Release(); }

        using (resp)
        {
            var code = (int)resp.StatusCode;
            if (code == 401) throw LogAuth(DownloadPath, await SmallTextAsync(resp, ct));
            if (code is < 200 or >= 300) return new(-1, $"HTTP {code} {HikErrors.Trim(await SmallTextAsync(resp, ct))}");

            Stream body;
            try { body = await resp.Content.ReadAsStreamAsync(stall.Token); }
            catch (Exception e) when (IsLoss(e, ct)) { throw new NvrUnreachableException(HikErrors.LostDuringDownload, e); }

            await using (body)
            {
                var buf = new byte[64 * 1024];
                var head = 0;
                while (head < 4)
                {
                    var n = await ReadAsync(body, buf.AsMemory(head), stall, ct);
                    if (n == 0) break;
                    head += n;
                }
                if (!IsProgramStream(buf, head))
                    return new(-1, head == 0 ? "empty body" : "not a PS stream: " + HikErrors.Trim(await RestTextAsync(body, buf, head, stall, ct)));

                var ok = false;
                try
                {
                    await using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16, useAsync: true))
                    {
                        await file.WriteAsync(buf.AsMemory(0, head), ct);
                        long total = head;
                        onBytes(total);
                        while (true)
                        {
                            var n = await ReadAsync(body, buf, stall, ct);
                            if (n == 0) break;
                            await file.WriteAsync(buf.AsMemory(0, n), ct);
                            total += n;
                            onBytes(total);
                        }
                        ok = true;
                        return new(total, "");
                    }
                }
                finally
                {
                    if (!ok) TryDelete(path);
                }
            }
        }
    }

    /// <summary>A body read under the stall timer: a stall or a dropped connection is a lost recorder, not a refusal.</summary>
    private async Task<int> ReadAsync(Stream s, Memory<byte> buf, CancellationTokenSource stall, CancellationToken ct)
    {
        stall.CancelAfter(StallTimeout);
        try { return await s.ReadAsync(buf, stall.Token); }
        catch (Exception e) when (IsLoss(e, ct)) { throw new NvrUnreachableException(HikErrors.LostDuringDownload, e); }
    }

    /// <summary>Hikvision PS: the 40-byte "IMKH" header, or straight MPEG-PS pack headers (00 00 01 BA).</summary>
    internal static bool IsProgramStream(byte[] b, int n) =>
        n >= 4 && ((b[0] == 'I' && b[1] == 'M' && b[2] == 'K' && b[3] == 'H') || (b[0] == 0 && b[1] == 0 && b[2] == 1 && b[3] == 0xBA));

    private async Task<string> RestTextAsync(Stream body, byte[] buf, int head, CancellationTokenSource stall, CancellationToken ct)
    {
        try
        {
            while (head < 4096)
            {
                var n = await ReadAsync(body, buf.AsMemory(head, 4096 - head), stall, ct);
                if (n == 0) break;
                head += n;
            }
        }
        catch (NvrUnreachableException) { /* the start is enough for the log */ }
        var text = HikXml.Decode(buf[..head]);
        var status = HikXml.ParseResponseStatus(HikXml.TryParse(text));
        return status is null ? text : $"{status.StatusCode} {status.StatusString} {status.SubStatusCode}";
    }

    private async Task<string> SmallTextAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(CallTimeout);
            await using var s = await resp.Content.ReadAsStreamAsync(cts.Token);
            var buf = new byte[8192];
            var n = 0;
            while (n < buf.Length)
            {
                var r = await s.ReadAsync(buf.AsMemory(n), cts.Token);
                if (r == 0) break;
                n += r;
            }
            return HikXml.Decode(buf[..n]);
        }
        catch (Exception e) when (IsLoss(e, ct))
        {
            return "";
        }
    }

    /// <summary>Network trouble or our own timeout; never the caller's cancellation.</summary>
    private static bool IsLoss(Exception e, CancellationToken ct) => e is OperationCanceledException
        ? !ct.IsCancellationRequested
        : e is IOException or HttpRequestException or ObjectDisposedException;

    /// <summary>The segment URI with starttime/endtime replaced (added when missing); name, size and the rest kept.</summary>
    internal static string Narrow(string uri, string start, string end)
    {
        uri = SetQuery(uri, "starttime", start);
        return SetQuery(uri, "endtime", end);
    }

    private static string SetQuery(string uri, string key, string value)
    {
        var re = new Regex(@"([?&])" + key + @"=[^&#]*", RegexOptions.IgnoreCase);
        if (re.IsMatch(uri)) return re.Replace(uri, m => m.Groups[1].Value + key + "=" + value, 1);
        return uri + (uri.Contains('?') ? "&" : "?") + key + "=" + value;
    }

    private static void DeleteAll(IEnumerable<string> paths)
    {
        foreach (var p in paths) TryDelete(p);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Warn("Could not delete " + path, e); }
    }
}
