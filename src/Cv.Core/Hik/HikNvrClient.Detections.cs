using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Cv.Core.Nvr;

namespace Cv.Core.Hik;

/// <summary>First page of one people search over several channels (see <see cref="INvrPeopleOverview"/>).</summary>
/// <param name="Events">Up to one page (100) of detections, device wall clock, oldest first.</param>
/// <param name="Total">How many detections the recorder counted for the whole search (at least Events.Count).</param>
/// <param name="More">The recorder has more pages than the one fetched.</param>
public sealed record PeopleOverview(IReadOnlyList<DetectionEvent> Events, int Total, bool More);

/// <summary>
/// One look at the people detections of several channels in a single request, for the on-site probe: whether and
/// where the cameras detect people, without paging through thousands of events. Not part of the frozen contract;
/// HikNvrClient implements it, callers test `client is INvrPeopleOverview`.
/// </summary>
public interface INvrPeopleOverview
{
    /// <summary>The first page of a people search over <paramref name="channels"/> with from &lt;= Time &lt; to
    /// (device local). Errors as INvrDetections.SearchPeopleAsync.</summary>
    Task<PeopleOverview> PeopleOverviewAsync(IReadOnlyCollection<int> channels, DateTime from, DateTime to, CancellationToken ct);
}

/// <summary>
/// People detections the recorder logged (the cameras' Motion Detection 2.0 with target human), searched with
/// eventRecordSearch (JSON). Measured on DS-7732NI-M4 V5.04.081 (2026-11-08): see the comments below.
/// </summary>
internal sealed partial class HikNvrClient : INvrPeopleOverview
{
    private const string PeopleSearchPath = "/ISAPI/ContentMgmt/eventRecordSearch?format=json";

    /// <summary>The recorder answers at most 100 detections per page, even when asked for 500.</summary>
    internal const int PeoplePageSize = 100;

    /// <summary>Safety cap: 10,000 detections (a busy channel logs ~190 a day).</summary>
    internal const int PeopleMaxPages = 100;

    internal const string PeopleUnsupported = "Đầu ghi này không cho tìm lúc có người.";
    internal const string PictureMissing = "Đầu ghi không trả về ảnh của lần phát hiện này.";
    internal const string PictureTooBig = "Ảnh đầu ghi trả về quá lớn.";

    /// <summary>A full frame (2560x1440) is ~250 KB; anything far bigger is not a picture.</summary>
    internal int PictureMaxBytes { get; set; } = 8 * 1024 * 1024;

    /// <summary>A page of 100 detections is ~100 KB of JSON.</summary>
    private const int SearchAnswerMaxBytes = 8 * 1024 * 1024;

    private const string PeopleTooBig = "Đầu ghi trả về kết quả tìm kiếm quá lớn.";

    // Once the recorder said it cannot search people, later searches fail at once instead of asking again.
    private volatile bool _peopleUnsupported;

    public async Task<IReadOnlyList<DetectionEvent>> SearchPeopleAsync(int channel, DateTime from, DateTime to, CancellationToken ct) =>
        (await SearchPeopleCoreAsync([channel], from, to, PeopleMaxPages, ct)).Events;

    public async Task<PeopleOverview> PeopleOverviewAsync(IReadOnlyCollection<int> channels, DateTime from, DateTime to, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(channels);
        var (events, total, more) = await SearchPeopleCoreAsync(channels.Distinct().ToArray(), from, to, 1, ct);
        return new PeopleOverview(events, Math.Max(total, events.Count), more);
    }

    /// <summary>
    /// Pages of one search, strictly in order. DS-7732NI-M4 V5.04.081 keeps its own cursor per searchID: asked again
    /// with the same searchID it ignores searchResultPosition and returns the NEXT page (verified 3 times), and a new
    /// searchID starts at the first result. So: a fresh searchID per search, position += numOfMatches (what a
    /// firmware honoring the position needs too), never a page asked twice.
    /// </summary>
    private async Task<(List<DetectionEvent> Events, int Total, bool More)> SearchPeopleCoreAsync(
        IReadOnlyCollection<int> channels, DateTime from, DateTime to, int maxPages, CancellationToken ct)
    {
        if (_peopleUnsupported) throw new NvrDetectionUnsupportedException(PeopleUnsupported);
        if (to <= from || channels.Count == 0) return ([], 0, false);
        var offset = await OffsetAsync(ct);
        var id = Guid.NewGuid().ToString("D").ToUpperInvariant();
        var start = WithOffset(from, offset);
        var end = WithOffset(CeilSecond(to), offset);
        var single = channels.Count == 1 ? channels.First() : (int?)null;

        var events = new List<DetectionEvent>();
        var position = 0;
        var total = 0;
        var more = false;
        for (var page = 0; page < maxPages; page++)
        {
            using var doc = await PeoplePageAsync(PeopleRequest(id, position, start, end, channels), ct);
            var result = ParsePeoplePage(doc.RootElement, single, offset);
            total = Math.Max(total, result.Total ?? 0);
            more = false;
            if (result.Status?.StartsWith("NO MATCH", StringComparison.OrdinalIgnoreCase) == true) break;
            events.AddRange(result.Events);
            total = Math.Max(total, position + result.Count);
            var n = result.NumOfMatches > 0 ? result.NumOfMatches : result.Count;
            if (!string.Equals(result.Status, "MORE", StringComparison.OrdinalIgnoreCase) || n <= 0) break;
            more = true;
            position += n;
        }
        if (more && maxPages > 1) Log.Warn($"People search ch {string.Join(",", channels)}: stopped after {maxPages} pages");

        var wanted = channels.ToHashSet();
        var list = events
            .Where(e => wanted.Contains(e.Channel) && e.Time >= from && e.Time < to)
            .Distinct()
            .OrderBy(e => e.Time).ThenBy(e => e.Channel)
            .ToList();
        return (list, total, more);
    }

    /// <summary>
    /// The body measured on the real recorder. Times carry the device offset ("+07:00"): on this endpoint "Z" means real
    /// UTC (unlike /ISAPI/ContentMgmt/search), and an explicit offset leaves no doubt. Channels are numbers (["8"] is
    /// refused with invalidContent). type "all": the default "record" only lists events that have an event recording,
    /// and the site records continuously.
    /// </summary>
    private static string PeopleRequest(string searchId, int position, string start, string end, IEnumerable<int> channels)
    {
        var list = string.Join(",", channels.Select(c => c.ToString(CultureInfo.InvariantCulture)));
        return "{\"EventSearchDescription\":{" +
               $"\"searchID\":\"{searchId}\",\"searchResultPosition\":{position.ToString(CultureInfo.InvariantCulture)}," +
               $"\"maxResults\":{PeoplePageSize.ToString(CultureInfo.InvariantCulture)}," +
               $"\"timeSpanList\":[{{\"startTime\":\"{start}\",\"endTime\":\"{end}\"}}]," +
               $"\"type\":\"all\",\"channels\":[{list}],\"eventType\":\"peopleDetection\"}}}}";
    }

    /// <summary>2026-11-07T08:00:00+07:00.</summary>
    private static string WithOffset(DateTime local, TimeSpan offset) =>
        local.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)
        + (offset < TimeSpan.Zero ? "-" : "+") + offset.Duration().ToString(@"hh\:mm", CultureInfo.InvariantCulture);

    /// <summary>The wire has whole seconds: the end goes up so the last partial second is still asked.</summary>
    private static DateTime CeilSecond(DateTime t) =>
        t.Ticks % TimeSpan.TicksPerSecond == 0 ? t : new DateTime(t.Ticks - t.Ticks % TimeSpan.TicksPerSecond + TimeSpan.TicksPerSecond, t.Kind);

    /// <summary>
    /// One page: a JSON answer, or an error. A "not supported" answer (subStatusCode notSupport, HTTP 403/404/405/501;
    /// the real recorder answers unknown JSON resources with 403 notSupport) becomes NvrDetectionUnsupportedException.
    /// </summary>
    private async Task<JsonDocument> PeoplePageAsync(string body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, PeopleSearchPath)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        var (code, bytes, _) = await ExchangeAsync(req, SearchAnswerMaxBytes, PeopleTooBig, ct);
        var text = HikXml.Decode(bytes);
        var json = TryJson(text);
        try
        {
            if (code == 401) throw LogAuth(PeopleSearchPath, json is null ? text : LoginXml(json.RootElement));
            var status = json is null ? HikXml.ParseResponseStatus(HikXml.TryParse(text)) : JsonStatus(json.RootElement);
            if (code is < 200 or >= 300 || (status?.StatusCode is { } sc && sc != 1))
            {
                Log.Warn($"ISAPI POST {PeopleSearchPath} -> {code}: {HikErrors.Trim(text)}");
                var err = new NvrStatusException(HikErrors.Message(code, status), code, status);
                if (!IsUnsupported(err)) throw err;
                _peopleUnsupported = true;
                throw new NvrDetectionUnsupportedException(PeopleUnsupported, err);
            }
            if (json is null) throw new NvrException(HikErrors.Unreadable);
            var result = json;
            json = null;
            return result;
        }
        finally { json?.Dispose(); }
    }

    private static bool IsUnsupported(NvrStatusException e) =>
        e.NotSupported
        || (e.Http == 403 && e.Status?.SubStatusCode?.ToLowerInvariant() is not ("lowprivilege" or "nopermission"));

    private sealed record PeoplePage(string? Status, int NumOfMatches, int? Total, int Count, List<DetectionEvent> Events);

    /// <summary>
    /// {"EventSearchResult":{"responseStatusStrg":"MORE"|"OK"|"NO MATCH","numOfMatches":100,"totalMatches":187,
    /// "Targets":[{"channel":14,"alarmStartTime":"2026-11-07T08:02:55+07:00","pictureUrl":"http://...","eventType":
    /// "peopleDetection",...}]}}. A NO MATCH answer has no Targets. Count is every target, Events the readable ones.
    /// </summary>
    private static PeoplePage ParsePeoplePage(JsonElement root, int? single, TimeSpan offset)
    {
        var r = JsonProp(root, "EventSearchResult") ?? throw new NvrException(HikErrors.Unreadable);
        var events = new List<DetectionEvent>();
        var count = 0;
        if (JsonProp(r, "Targets") is { ValueKind: JsonValueKind.Array } targets)
        {
            foreach (var t in targets.EnumerateArray())
            {
                count++;
                if (t.ValueKind != JsonValueKind.Object) continue;
                if (JsonStr(t, "eventType") is { } type && !type.Equals("peopleDetection", StringComparison.OrdinalIgnoreCase)) continue;
                var channel = JsonInt(t, "channel") ?? JsonInt(t, "triggerChannel") ?? single;
                // "Z" is real UTC on this endpoint; the recorder writes its own offset, i.e. its own wall clock.
                if (channel is not { } ch || !HikTime.TryParse(JsonStr(t, "alarmStartTime") ?? JsonStr(t, "startTime"), out var stamp)) continue;
                events.Add(new DetectionEvent(ch, HikTime.ToLocal(stamp, TimeMode.Utc, offset), PicturePath(JsonStr(t, "pictureUrl"))));
            }
        }
        return new PeoplePage(JsonStr(r, "responseStatusStrg"), JsonInt(r, "numOfMatches") ?? count, JsonInt(r, "totalMatches"), count, events);
    }

    // ---- Picture ----

    public async Task<byte[]> GetPictureAsync(string pictureUri, CancellationToken ct)
    {
        var path = PicturePath(pictureUri) ?? throw new ArgumentException("Thiếu đường dẫn ảnh.", nameof(pictureUri));
        // The digest login only ever goes to the configured recorder.
        var http = Http;
        if (!Uri.TryCreate(http.BaseAddress, path, out var target) || !string.Equals(target.Authority, http.BaseAddress!.Authority, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Đường dẫn ảnh không hợp lệ.", nameof(pictureUri));

        using var req = new HttpRequestMessage(HttpMethod.Get, target);
        var (code, bytes, type) = await ExchangeAsync(req, PictureMaxBytes, PictureTooBig, ct);
        if (code == 401) throw LogAuth(target.AbsolutePath, HikXml.Decode(bytes));
        if (code is < 200 or >= 300 || !IsImage(type, bytes))
        {
            // DS-7732NI-M4 V5.04.081 answers a wrong name with 334 bytes of text/html.
            Log.Warn($"Picture {target.PathAndQuery} -> {code} {type}, {bytes.Length} bytes: {HikErrors.Trim(HikXml.Decode(bytes[..Math.Min(bytes.Length, 300)]))}");
            throw new NvrException(PictureMissing);
        }
        return bytes;
    }

    private static bool IsImage(string? type, byte[] b) =>
        b.Length >= 4
        && ((b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) // JPEG
            || (b[0] == 0x89 && b[1] == 'P' && b[2] == 'N' && b[3] == 'G')
            || (type?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true && b[0] != '<'));

    /// <summary>
    /// Path + query of a picture URL ("http://10.9.0.254:80/picture/...?name=..&amp;size=.." becomes
    /// "/picture/...?name=..&amp;size=.."): the recorder may report an address this PC cannot reach, so the picture is
    /// fetched from the configured host. Null when there is no path.
    /// </summary>
    internal static string? PicturePath(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var u = AuthorityRegex().Replace(url.Trim(), "", 1);
        u = u.TrimStart('/', '\\');
        return u.Length == 0 ? null : "/" + u;
    }

    [GeneratedRegex(@"^(?:[A-Za-z][A-Za-z0-9+.-]*:)?[/\\]{2}[^/\\?#]*")]
    private static partial Regex AuthorityRegex();

    // ---- HTTP ----

    /// <summary>
    /// One request whose answer is read whole, up to <paramref name="cap"/> bytes (more: NvrException
    /// <paramref name="tooBig"/>). Login and headers inside the gate, like SendAsync; the body after it, failing when no
    /// byte comes for CallTimeout. Network errors and timeouts become NvrUnreachableException; a cancelled caller token
    /// propagates as is.
    /// </summary>
    private async Task<(int Code, byte[] Body, string? Type)> ExchangeAsync(HttpRequestMessage req, int cap, string tooBig, CancellationToken ct)
    {
        var http = Http;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        HttpResponseMessage resp;
        await _gate.WaitAsync(ct);
        try
        {
            cts.CancelAfter(CallTimeout);
            resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        }
        catch (Exception e) when (IsLoss(e, ct)) { throw Unreachable(e); }
        finally { _gate.Release(); }

        using (resp)
        {
            if (resp.Content.Headers.ContentLength > cap) throw new NvrException(tooBig);
            try
            {
                await using var s = await resp.Content.ReadAsStreamAsync(cts.Token);
                using var ms = new MemoryStream();
                var buf = new byte[64 * 1024];
                while (true)
                {
                    cts.CancelAfter(CallTimeout);
                    var n = await s.ReadAsync(buf, cts.Token);
                    if (n == 0) break;
                    if (ms.Length + n > cap) throw new NvrException(tooBig);
                    ms.Write(buf, 0, n);
                }
                return ((int)resp.StatusCode, ms.ToArray(), resp.Content.Headers.ContentType?.MediaType);
            }
            catch (Exception e) when (IsLoss(e, ct)) { throw Unreachable(e); }
        }
    }

    // ---- JSON ----

    private static JsonDocument? TryJson(string text)
    {
        var t = text.TrimStart('﻿', ' ', '\t', '\r', '\n', '\0');
        if (!t.StartsWith('{')) return null;
        try { return JsonDocument.Parse(t); }
        catch (JsonException) { return null; }
    }

    /// <summary>{"statusCode":4,"statusString":"Invalid Operation","subStatusCode":"notSupport",...}; null on a data answer.</summary>
    private static IsapiStatus? JsonStatus(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && JsonProp(root, "statusCode") is not null
            ? new IsapiStatus(JsonInt(root, "statusCode"), JsonStr(root, "statusString"), JsonStr(root, "subStatusCode"))
            : null;

    /// <summary>HikErrors.Auth reads the failed-login counters from XML; a JSON 401 body carries the same fields.</summary>
    private static string LoginXml(JsonElement root) => new XElement("userCheck",
        new XElement("lockStatus", JsonFind(root, "lockStatus")?.ToString() ?? ""),
        new XElement("unlockTime", JsonFind(root, "unlockTime")?.ToString() ?? ""),
        new XElement("retryLoginTime", JsonFind(root, "retryLoginTime")?.ToString() ?? "")).ToString(SaveOptions.DisableFormatting);

    /// <summary>A property by name, case-insensitive (firmware differ in case).</summary>
    private static JsonElement? JsonProp(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        if (e.TryGetProperty(name, out var exact)) return exact;
        foreach (var p in e.EnumerateObject())
            if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return p.Value;
        return null;
    }

    /// <summary>The first property with that name at any depth.</summary>
    private static JsonElement? JsonFind(JsonElement e, string name)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            if (JsonProp(e, name) is { } hit) return hit;
            foreach (var p in e.EnumerateObject())
                if (JsonFind(p.Value, name) is { } deep) return deep;
        }
        else if (e.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in e.EnumerateArray())
                if (JsonFind(item, name) is { } deep) return deep;
        }
        return null;
    }

    /// <summary>Text of a string or number property; null when absent or blank.</summary>
    private static string? JsonStr(JsonElement e, string name) => JsonProp(e, name) switch
    {
        { ValueKind: JsonValueKind.String } s => string.IsNullOrWhiteSpace(s.GetString()) ? null : s.GetString()!.Trim(),
        { ValueKind: JsonValueKind.Number } n => n.GetRawText(),
        _ => null,
    };

    /// <summary>A number, written as a number or as text.</summary>
    private static int? JsonInt(JsonElement e, string name) => JsonProp(e, name) switch
    {
        { ValueKind: JsonValueKind.Number } n when n.TryGetInt32(out var i) => i,
        { ValueKind: JsonValueKind.String } s => HikXml.Int(s.GetString()?.Trim()),
        _ => null,
    };
}
