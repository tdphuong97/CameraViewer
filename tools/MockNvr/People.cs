using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MockNvr;

/// <summary>A peopleDetection event: a camera saw a person at Time (device-local wall clock, whole seconds).</summary>
public sealed record PeopleEvent(int Channel, DateTime Time)
{
    /// <summary>Picture name, like the recorder's "ch00014_...": channel and time, so the picture endpoint can check it.</summary>
    public string PictureName => string.Create(CultureInfo.InvariantCulture, $"ch{Channel:D5}_{Time:yyyyMMddHHmmss}");

    public static bool TryParseName(string? name, out int channel, out DateTime time)
    {
        channel = 0; time = default;
        if (name is null || name.Length != 22 || !name.StartsWith("ch", StringComparison.Ordinal) || name[7] != '_') return false;
        return int.TryParse(name.AsSpan(2, 5), NumberStyles.None, CultureInfo.InvariantCulture, out channel)
            && DateTime.TryParseExact(name[8..], "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out time);
    }
}

/// <summary>
/// Deterministic fake people detections on a few online channels: between 06:00 and 22:00, a burst of 5–20 detections
/// one second apart (a person walking through, the recorder logs about one a second) every 7–20 minutes; only where
/// the channel recorded and never in the future.
/// </summary>
public sealed class PeopleStore(RecordingStore recordings)
{
    /// <summary>Channels whose camera has human detection on; the others never log a person.</summary>
    public static IReadOnlyList<int> Channels { get; } = [1, 3, 8, 14];

    /// <summary>Detections of the channels with from &lt;= Time &lt; to, by time then channel.</summary>
    public IReadOnlyList<PeopleEvent> Find(IEnumerable<int> channels, DateTime from, DateTime to)
    {
        var list = new List<PeopleEvent>();
        foreach (var ch in channels.Distinct().Where(Channels.Contains))
        {
            var segs = recordings.Find(ch, from, to); // sorted, ends at now
            var i = 0;
            for (var day = from.Date; day < to; day = day.AddDays(1))
                foreach (var t in Day(ch, day))
                {
                    if (t < from) continue;
                    if (t >= to) break;
                    while (i < segs.Count && segs[i].End <= t) i++;
                    if (i < segs.Count && segs[i].Start <= t) list.Add(new PeopleEvent(ch, t));
                }
        }
        list.Sort((a, b) => a.Time != b.Time ? a.Time.CompareTo(b.Time) : a.Channel.CompareTo(b.Channel));
        return list;
    }

    public bool Exists(int channel, DateTime time) => Find([channel], time, time.AddSeconds(1)).Count > 0;

    private static IEnumerable<DateTime> Day(int channel, DateTime day)
    {
        var rng = new Random(unchecked((int)(day.Ticks / TimeSpan.TicksPerDay) * 31 + channel * 1_000_003));
        var t = day.AddHours(6).AddSeconds(rng.Next(0, 600));
        var end = day.AddHours(22);
        while (t < end)
        {
            var n = rng.Next(5, 21);
            for (var k = 0; k < n; k++) yield return t.AddSeconds(k);
            t = t.AddSeconds(n + rng.Next(7 * 60, 20 * 60 + 1));
        }
    }
}

/// <summary>
/// /ISAPI/ContentMgmt/eventRecordSearch (JSON) and /picture/... as DS-7732NI-M4 V5.04.081 answered them on 2026-11-08.
/// </summary>
public static class PeopleApi
{
    private const string SearchUrl = "/ISAPI/ContentMgmt/eventRecordSearch?format=json";
    private const string JsonType = "application/json; charset=\"UTF-8\"";
    /// <summary>The recorder answers at most 100 per page, even when asked for 500.</summary>
    public const int PageCap = 100;

    private static readonly JsonSerializerOptions Write = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>A 32x18 grey JPEG standing in for the recorder's full frame (2560x1440, ~250 KB).</summary>
    public static readonly byte[] Jpeg = Convert.FromBase64String(
        "/9j/4AAQSkZJRgABAQEAYABgAAD/2wBDABALDA4MChAODQ4SERATGCgaGBYWGDEjJR0oOjM9PDkzODdASFxOQERXRTc4UG1RV19iZ2hnPk1xeXBkeFxlZ2P/wAALCAASACABAREA/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/9oACAEBAAA/AMG3i6VqW8XStS3i6VqW8XSuPt4ulalvF0rUt4ulalvF0ri7ftWpb9q1LftWpb9q/9k=");

    /// <summary>
    /// The search. Quirk of the real recorder (verified 3 times): asked again with a searchID it has seen, it ignores
    /// searchResultPosition and continues its own cursor; a new searchID starts at the first result. Channels must be
    /// numbers (["8"] gets a 200 invalidContent); type "record" (the default) finds nothing here because the site
    /// records continuously; "Z" in the time span is real UTC; pages hold at most 100.
    /// </summary>
    public static async Task<IResult> SearchAsync(HttpContext ctx, PeopleStore store, MockClock clock, MockOptions o, ConcurrentDictionary<string, int> cursors)
    {
        if (!o.People)
        {
            ctx.Items["note"] = "people search: notSupport (--no-people)";
            return Json(Status(4, "Invalid Operation", "notSupport"), 403);
        }
        JsonElement d;
        try
        {
            using var reader = new StreamReader(ctx.Request.Body);
            using var doc = JsonDocument.Parse(await reader.ReadToEndAsync(ctx.RequestAborted));
            if (Prop(doc.RootElement, "EventSearchDescription") is not { ValueKind: JsonValueKind.Object } desc) throw new JsonException("no EventSearchDescription");
            d = desc.Clone();
        }
        catch (JsonException)
        {
            ctx.Items["note"] = "people search: bad JSON";
            return Json(Status(6, "Invalid Content", "badJsonContent"), 400);
        }

        var parse = new HikTime(clock, TimeWriteMode.Utc); // "Z" = UTC, explicit offsets honored, none = local
        var span = Prop(d, "timeSpanList") is { ValueKind: JsonValueKind.Array } spans && spans.GetArrayLength() > 0 ? spans[0] : default;
        var max = Number(d, "maxResults") ?? 0;
        if (span.ValueKind != JsonValueKind.Object || !parse.TryParse(Text(span, "startTime"), out var from) || !parse.TryParse(Text(span, "endTime"), out var to) || max <= 0)
        {
            ctx.Items["note"] = "people search: bad time span or maxResults";
            return Json(Status(6, "Invalid Content", "badParameters"), 400);
        }
        IReadOnlyList<int> channels = MockDevice.Channels.Select(c => c.No).ToList(); // no list: every channel
        if (Prop(d, "channels") is { ValueKind: JsonValueKind.Array } chs)
        {
            if (chs.EnumerateArray().Any(c => c.ValueKind != JsonValueKind.Number))
            {
                ctx.Items["note"] = "people search: channels as text -> invalidContent";
                return Json(Status(4, "Invalid Operation", "invalidContent"), 200);
            }
            channels = chs.EnumerateArray().Select(c => c.GetInt32()).ToList();
        }
        var type = Text(d, "type") ?? "record";
        var eventType = Text(d, "eventType");
        var id = Text(d, "searchID") ?? Guid.NewGuid().ToString("D").ToUpperInvariant();
        var position = Number(d, "searchResultPosition") ?? 0;

        var all = type == "all" && string.Equals(eventType, "peopleDetection", StringComparison.OrdinalIgnoreCase)
            ? store.Find(channels, from, to)
            : [];
        // Keeps the cursor table small; a forgotten ID simply starts over, like a new one.
        if (cursors.Count > 1000) cursors.Clear();
        var known = cursors.TryGetValue(id, out var index);
        var page = all.Skip(index).Take(Math.Min(max, PageCap)).ToList();
        cursors[id] = index + page.Count;
        var status = page.Count == 0 ? "NO MATCH" : index + page.Count < all.Count ? "MORE" : "OK";
        ctx.Items["note"] = string.Create(CultureInfo.InvariantCulture,
            $"people ch {string.Join(",", channels)} {from:yyyy-MM-dd HH:mm:ss}..{to:HH:mm:ss} type {type} pos {position}{(known ? $" (cursor {index})" : "")} max {max}: {status} {page.Count} of {all.Count}");

        var result = new JsonObject
        {
            ["searchID"] = id,
            ["responseStatusStrg"] = status,
            ["numOfMatches"] = page.Count,
            ["totalMatches"] = all.Count,
        };
        if (page.Count > 0)
        {
            var write = new HikTime(clock, TimeWriteMode.Offset); // the recorder writes its own offset here
            var host = $"{ctx.Request.Host.Host}:{ctx.Request.Host.Port ?? 80}";
            result["Targets"] = new JsonArray(page.Select((e, i) => (JsonNode?)new JsonObject
            {
                ["id"] = index + i + 1,
                ["channel"] = e.Channel,
                ["alarmStartTime"] = write.FormatIso(e.Time),
                ["alarmEndTime"] = write.FormatIso(e.Time),
                ["triggerChannel"] = e.Channel,
                ["pictureUrl"] = string.Create(CultureInfo.InvariantCulture,
                    $"http://{host}/picture/Streaming/tracks/{e.Channel}03/?name={e.PictureName}&size={Jpeg.Length}"),
                ["startTime"] = write.FormatIso(e.Time.AddSeconds(-30)),
                ["endTime"] = write.FormatIso(e.Time.AddSeconds(30)),
                ["eventType"] = "peopleDetection",
                ["metadataMatches"] = new JsonObject(),
            }).ToArray());
        }
        return Json(new JsonObject { ["EventSearchResult"] = result }, 200);
    }

    public static IResult Capabilities(MockOptions o) => o.People
        ? Json(new JsonObject { ["type"] = new JsonObject { ["@opt"] = new JsonArray("record", "all"), ["@def"] = "record" } }, 200)
        : Json(Status(4, "Invalid Operation", "notSupport"), 403);

    /// <summary>GET /picture/Streaming/tracks/{ch}03/?name=...&amp;size=...: the JPEG of a known detection, else 404.</summary>
    public static IResult Picture(HttpContext ctx, PeopleStore store)
    {
        var parts = (ctx.Request.Path.Value ?? "").Trim('/').Split('/');
        var name = ctx.Request.Query["name"].ToString();
        if (parts is ["picture", "Streaming", "tracks", var trackText] && int.TryParse(trackText, out var track) && track % 100 == 3
            && PeopleEvent.TryParseName(name, out var ch, out var time) && ch == track / 100 && store.Exists(ch, time))
        {
            ctx.Items["note"] = $"picture ch{ch} {time:yyyy-MM-dd HH:mm:ss}";
            return Results.Bytes(Jpeg, "image/jpeg");
        }
        ctx.Items["note"] = $"picture '{name}': not found";
        return Results.Text("<!DOCTYPE html><html><head><title>Document Error: Not Found</title></head><body><h2>Access Error: 404 -- Not Found</h2></body></html>",
            "text/html", null, 404);
    }

    private static JsonObject Status(int code, string text, string sub) => new()
    {
        ["requestURL"] = SearchUrl,
        ["statusCode"] = code,
        ["statusString"] = text,
        ["subStatusCode"] = sub,
    };

    private static IResult Json(JsonObject body, int status) => Results.Text(body.ToJsonString(Write), JsonType, null, status);

    private static JsonElement? Prop(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in e.EnumerateObject())
            if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return p.Value;
        return null;
    }

    private static string? Text(JsonElement e, string name) => Prop(e, name) is { ValueKind: JsonValueKind.String } s ? s.GetString() : null;

    private static int? Number(JsonElement e, string name) => Prop(e, name) is { ValueKind: JsonValueKind.Number } n && n.TryGetInt32(out var v) ? v : null;
}
