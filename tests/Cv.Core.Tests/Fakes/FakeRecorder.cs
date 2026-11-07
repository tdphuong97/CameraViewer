using System.Globalization;
using System.Net;
using System.Security;
using System.Text;
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
