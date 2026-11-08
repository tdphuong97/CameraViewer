using System.Globalization;
using System.Net;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Cv.Core.Hik;
using Cv.Core.Nvr;

namespace Cv.Core.Tests.Fakes;

/// <summary>
/// A whole fake NVR on top of FakeIsapiHandler: device info, clock, channels, paged search (filtered by the raw
/// times, as firmware compares in its own frame) and a configurable download endpoint.
/// </summary>
public sealed class FakeRecorder
{
    public sealed record Item(string Start, string End, string Uri);

    public FakeIsapiHandler Handler { get; } = new();
    /// <summary>trackID → recorded items (raw strings as the firmware writes them).</summary>
    public Dictionary<int, List<Item>> Tracks { get; } = new();
    public List<XElement> SearchRequests { get; } = [];
    /// <summary>Download answer; default: a PS stream.</summary>
    public Func<FakeIsapiHandler.Seen, CancellationToken, HttpResponseMessage> Download { get; set; } = (_, _) => FakeIsapiHandler.Bytes(Samples.Ps());
    public List<(HttpMethod Method, string Uri)> Downloads { get; } = [];
    /// <summary>Always answer MORE with one item (to test the page cap).</summary>
    public bool EndlessMore { get; set; }

    /// <summary>A peopleDetection event as the recorder writes it (AlarmStart with its offset, "Z" = real UTC).</summary>
    public sealed record Detection(int Channel, string AlarmStart, string? PictureUrl, string EventType = "peopleDetection");
    /// <summary>The events eventRecordSearch knows, in the recorder's order (time).</summary>
    public List<Detection> People { get; } = [];
    /// <summary>Bodies of the eventRecordSearch requests.</summary>
    public List<JsonElement> PeopleRequests { get; } = [];
    /// <summary>Replaces the eventRecordSearch answer (errors, other firmware).</summary>
    public Func<FakeIsapiHandler.Seen, HttpResponseMessage>? PeopleAnswer { get; set; }
    /// <summary>Always answer MORE with one target (to test the page cap).</summary>
    public bool PeopleEndlessMore { get; set; }
    /// <summary>Ignore the channels and the event type of the request (to test the client's own filter).</summary>
    public bool PeopleIgnoreFilters { get; set; }
    private readonly Dictionary<string, int> _peopleCursors = [];

    public FakeRecorder(string timeXml = Samples.Time, string channels = Samples.Channels, string status = Samples.Status)
    {
        Handler.Xml("/ISAPI/System/deviceInfo", Samples.DeviceInfo)
            .Xml("/ISAPI/System/time", timeXml)
            .Xml("/ISAPI/ContentMgmt/InputProxy/channels", channels)
            .Xml("/ISAPI/ContentMgmt/InputProxy/channels/status", status)
            .Xml("/ISAPI/Streaming/channels", Samples.StreamingChannels)
            .On(HttpMethod.Post, "/ISAPI/ContentMgmt/search", Search);
        Handler.On(HttpMethod.Get, "/ISAPI/ContentMgmt/download", DownloadRoute);
        Handler.On(HttpMethod.Post, "/ISAPI/ContentMgmt/download", DownloadRoute);
        Handler.On(HttpMethod.Post, "/ISAPI/ContentMgmt/eventRecordSearch", PeopleSearch);
    }

    public static NvrConfig Config(TimeMode mode = TimeMode.LocalWithZ, string host = "192.168.1.64", int rtsp = 554) =>
        new() { Id = "nvr1", Host = host, User = "admin", TimeMode = mode, RtspPort = rtsp };

    internal HikNvrClient Client(TimeMode mode = TimeMode.LocalWithZ, string password = "secret") =>
        new(Config(mode), password, Handler) { CallTimeout = TimeSpan.FromSeconds(5), StallTimeout = TimeSpan.FromSeconds(5) };

    public FakeRecorder Add(int track, string start, string end, string? uri = null)
    {
        if (!Tracks.TryGetValue(track, out var list)) Tracks[track] = list = [];
        uri ??= $"rtsp://192.168.1.64/Streaming/tracks/{track}/?starttime={Compact(start)}&endtime={Compact(end)}&name=0000{list.Count}&size=524288000";
        list.Add(new Item(start, end, uri));
        return this;
    }

    private static string Compact(string iso) => iso.Replace("-", "").Replace(":", "")[..15] + "Z";

    public static string PlaybackUriOf(string body) =>
        XDocument.Parse(body).Root!.Descendants().First(e => e.Name.LocalName == "playbackURI").Value;

    private HttpResponseMessage DownloadRoute(FakeIsapiHandler.Seen s, CancellationToken ct)
    {
        lock (Downloads) Downloads.Add((s.Method, PlaybackUriOf(s.Body)));
        return Download(s, ct);
    }

    private HttpResponseMessage Search(FakeIsapiHandler.Seen s)
    {
        var req = XDocument.Parse(s.Body).Root!;
        lock (SearchRequests) SearchRequests.Add(req);
        string V(string n) => req.Descendants().First(e => e.Name.LocalName == n).Value;
        var track = int.Parse(V("trackID"), CultureInfo.InvariantCulture);
        var pos = int.Parse(V("searchResultPostion"), CultureInfo.InvariantCulture);
        var max = int.Parse(V("maxResults"), CultureInfo.InvariantCulture);
        if (EndlessMore) return FakeIsapiHandler.Text(Page(V("searchID"), "MORE", [new Item("2026-11-07T00:00:00Z", "2026-11-07T01:00:00Z", "rtsp://x")]));

        var from = Wall(V("startTime"))!.Value;
        var to = Wall(V("endTime"))!.Value;
        var all = (Tracks.TryGetValue(track, out var l) ? l : [])
            .Where(i => i.Start.Contains('+') || Wall(i.Start) is null || Wall(i.End) is null || (Wall(i.End) > from && Wall(i.Start) < to))
            .ToList();
        if (all.Count == 0) return FakeIsapiHandler.Text(Page(V("searchID"), "NO MATCHES", []));
        var page = all.Skip(pos).Take(max).ToList();
        var status = pos + page.Count < all.Count ? "MORE" : "OK";
        return FakeIsapiHandler.Text(Page(V("searchID"), status, page));
    }

    /// <summary><paramref name="count"/> detections <paramref name="step"/> apart from <paramref name="first"/> (device
    /// wall clock, written with +07:00 like the real recorder).</summary>
    public FakeRecorder AddPeople(int channel, DateTime first, int count = 1, TimeSpan? step = null)
    {
        for (var i = 0; i < count; i++)
        {
            var t = first + i * (step ?? TimeSpan.FromSeconds(1));
            People.Add(new Detection(channel, t.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + "+07:00",
                $"http://10.9.0.254:80/picture/Streaming/tracks/{channel}03/?name=ch{channel:D5}_{t:yyyyMMddHHmmss}&size=278614"));
        }
        return this;
    }

    /// <summary>
    /// eventRecordSearch like DS-7732NI-M4 V5.04.081: numbers only in channels (["8"] is invalidContent), type "record"
    /// finds nothing on a continuously recording site, pages of at most 100, and the cursor quirk: a searchID seen
    /// before continues where it stopped whatever searchResultPosition says; a new one starts at the first result.
    /// </summary>
    private HttpResponseMessage PeopleSearch(FakeIsapiHandler.Seen s)
    {
        using var doc = JsonDocument.Parse(s.Body);
        var d = doc.RootElement.GetProperty("EventSearchDescription");
        lock (PeopleRequests) PeopleRequests.Add(d.Clone());
        if (PeopleAnswer is not null) return PeopleAnswer(s);
        var id = d.GetProperty("searchID").GetString()!;
        if (PeopleEndlessMore)
            return Json(PeopleResult("MORE", 1, 1000, [new Detection(1, "2026-11-07T08:00:00+07:00", null)], 0));

        var channels = d.TryGetProperty("channels", out var chs) ? chs.EnumerateArray().ToList() : [];
        if (channels.Any(c => c.ValueKind != JsonValueKind.Number))
            return Json("""{"requestURL":"/ISAPI/ContentMgmt/eventRecordSearch?format=json","statusCode":4,"statusString":"Invalid Operation","subStatusCode":"invalidContent","errorCode":1073741826,"errorMsg":"channels"}""");
        var wanted = channels.Select(c => c.GetInt32()).ToHashSet();
        var span = d.GetProperty("timeSpanList")[0];
        var from = DateTimeOffset.Parse(span.GetProperty("startTime").GetString()!, CultureInfo.InvariantCulture);
        var to = DateTimeOffset.Parse(span.GetProperty("endTime").GetString()!, CultureInfo.InvariantCulture);
        var type = d.TryGetProperty("type", out var ty) ? ty.GetString() : "record";
        var eventType = d.GetProperty("eventType").GetString();
        var max = Math.Min(d.GetProperty("maxResults").GetInt32(), 100);

        var all = type != "all" ? [] : People
            .Where(p => PeopleIgnoreFilters || ((wanted.Count == 0 || wanted.Contains(p.Channel)) && p.EventType == eventType))
            // End inclusive (the client cuts); unreadable times pass, for the client to skip.
            .Where(p => !DateTimeOffset.TryParse(p.AlarmStart, CultureInfo.InvariantCulture, out var t) || (t >= from && t <= to))
            .ToList();
        int index;
        lock (_peopleCursors) index = _peopleCursors.TryGetValue(id, out var c) ? c : 0;
        var page = all.Skip(index).Take(max).ToList();
        lock (_peopleCursors) _peopleCursors[id] = index + page.Count;
        if (page.Count == 0) return Json(PeopleResult("NO MATCH", 0, all.Count, [], index));
        return Json(PeopleResult(index + page.Count < all.Count ? "MORE" : "OK", page.Count, all.Count, page, index));
    }

    private static string PeopleResult(string status, int n, int total, List<Detection> page, int index)
    {
        var r = new JsonObject { ["searchID"] = "x", ["responseStatusStrg"] = status, ["numOfMatches"] = n, ["totalMatches"] = total };
        if (page.Count > 0)
            r["Targets"] = new JsonArray(page.Select((p, i) =>
            {
                var t = new JsonObject
                {
                    ["id"] = index + i + 1, ["channel"] = p.Channel, ["alarmStartTime"] = p.AlarmStart, ["alarmEndTime"] = p.AlarmStart,
                    ["triggerChannel"] = p.Channel, ["eventType"] = p.EventType, ["metadataMatches"] = new JsonObject(),
                };
                if (p.PictureUrl is not null) t["pictureUrl"] = p.PictureUrl;
                return (JsonNode?)t;
            }).ToArray());
        return new JsonObject { ["EventSearchResult"] = r }.ToJsonString();
    }

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        FakeIsapiHandler.Text(body, status, "application/json");

    private static DateTime? Wall(string s) =>
        s.Length >= 19 && DateTime.TryParseExact(s[..19], "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;

    private static string Page(string id, string status, List<Item> items)
    {
        var sb = new StringBuilder();
        sb.Append("<CMSearchResult version=\"2.0\" xmlns=\"http://www.hikvision.com/ver20/XMLSchema\">");
        sb.Append($"<searchID>{{{id}}}</searchID><responseStatus>true</responseStatus>");
        sb.Append($"<responseStatusStrg>{status}</responseStatusStrg><numOfMatches>{items.Count}</numOfMatches><matchList>");
        foreach (var i in items)
            sb.Append("<searchMatchItem><trackID>101</trackID><timeSpan>")
              .Append($"<startTime>{i.Start}</startTime><endTime>{i.End}</endTime></timeSpan>")
              .Append($"<mediaSegmentDescriptor><contentType>video</contentType><playbackURI>{SecurityElement.Escape(i.Uri)}</playbackURI></mediaSegmentDescriptor>")
              .Append("</searchMatchItem>");
        sb.Append("</matchList></CMSearchResult>");
        return sb.ToString();
    }

    public static HttpResponseMessage Xml(string body, HttpStatusCode status = HttpStatusCode.OK) => FakeIsapiHandler.Text(body, status);
}
