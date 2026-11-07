using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Cv.Core.Nvr;

namespace Cv.Core.Hik;

internal sealed record ProxyChannel(int No, string? Name, string? Ip, string? Model);

internal sealed record RawSearchItem(string? Start, string? End, string? PlaybackUri);

internal sealed record SearchPage(string? Status, int NumOfMatches, IReadOnlyList<RawSearchItem> Items);

internal sealed record IsapiStatus(int? StatusCode, string? StatusString, string? SubStatusCode);

internal sealed record LoginStatus(string? LockStatus, int? UnlockSeconds, int? RetriesLeft);

/// <summary>ISAPI XML in and out. Namespaces are ignored (LocalName, case-insensitive) and missing fields tolerated.</summary>
internal static class HikXml
{
    /// <summary>The root element, or null when the text is not XML (BOM and leading blanks allowed).</summary>
    public static XElement? TryParse(string? text)
    {
        if (text is null) return null;
        var t = text.TrimStart('﻿', ' ', '\t', '\r', '\n', '\0');
        if (!t.StartsWith('<')) return null;
        try { return XDocument.Parse(t).Root; }
        catch (XmlException) { return null; }
    }

    public static XElement? TryParse(byte[] bytes) => TryParse(Decode(bytes));

    /// <summary>UTF-8 without a leading BOM.</summary>
    public static string Decode(byte[] b) =>
        b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF ? Encoding.UTF8.GetString(b, 3, b.Length - 3) : Encoding.UTF8.GetString(b);

    public static bool Is(this XElement e, string name) => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase);

    public static XElement? El(this XElement e, string name) => e.Elements().FirstOrDefault(x => x.Is(name));

    public static IEnumerable<XElement> Desc(this XElement e, string name) => e.Descendants().Where(x => x.Is(name));

    /// <summary>Trimmed text of the first child with that name; null when absent or blank.</summary>
    public static string? Val(this XElement e, string name) => Clean(e.El(name)?.Value);

    /// <summary>Trimmed text of the first descendant with that name.</summary>
    public static string? DescVal(this XElement e, string name) => Clean(e.Desc(name).FirstOrDefault()?.Value);

    public static int? Int(string? s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    public static bool? Bool(string? s) => s?.ToLowerInvariant() switch
    {
        "true" or "1" or "yes" => true,
        "false" or "0" or "no" => false,
        _ => null,
    };

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    public static NvrDeviceInfo ParseDeviceInfo(XElement root)
    {
        var fw = root.Val("firmwareVersion") ?? "";
        var build = root.Val("firmwareReleasedDate");
        if (build is not null) fw = fw.Length == 0 ? build : fw + " " + build;
        return new NvrDeviceInfo(root.Val("model") ?? "", root.Val("serialNumber") ?? "", fw, root.Val("deviceName") ?? "");
    }

    public static (string? LocalTime, string? TimeZone) ParseTime(XElement root) => (root.DescVal("localTime"), root.DescVal("timeZone"));

    public static List<ProxyChannel> ParseInputProxyChannels(XElement root)
    {
        var list = new List<ProxyChannel>();
        foreach (var c in root.DescendantsAndSelf().Where(x => x.Is("InputProxyChannel")))
        {
            if (Int(c.Val("id")) is not { } no || no <= 0) continue;
            var src = c.El("sourceInputPortDescriptor");
            list.Add(new ProxyChannel(no, c.Val("name"), src?.Val("ipAddress") ?? src?.Val("hostName"), src?.Val("model")));
        }
        return list;
    }

    /// <summary>Channel number to online flag. "online" wins; otherwise chanDetectResult == "connect".</summary>
    public static Dictionary<int, bool> ParseChannelStatus(XElement root)
    {
        var map = new Dictionary<int, bool>();
        foreach (var c in root.DescendantsAndSelf().Where(x => x.Is("InputProxyChannelStatus")))
        {
            if (Int(c.Val("id")) is not { } no) continue;
            var online = Bool(c.Val("online"));
            if (online is null && c.Val("chanDetectResult") is { } det) online = det.Equals("connect", StringComparison.OrdinalIgnoreCase);
            if (online is not null) map[no] = online.Value;
        }
        return map;
    }

    /// <summary>Channel number (id 101 → 1) to channelName, main streams preferred.</summary>
    public static Dictionary<int, string> ParseStreamingChannelNames(XElement root)
    {
        var map = new Dictionary<int, string>();
        foreach (var c in root.DescendantsAndSelf().Where(x => x.Is("StreamingChannel")).OrderBy(x => Int(x.Val("id")) ?? 0))
        {
            if (Int(c.Val("id")) is not { } id || id < 100) continue;
            var name = c.Val("channelName");
            if (name is not null) map.TryAdd(id / 100, name);
        }
        return map;
    }

    public static SearchPage ParseSearchResult(XElement root)
    {
        var items = new List<RawSearchItem>();
        foreach (var m in root.Desc("searchMatchItem"))
        {
            var span = m.Desc("timeSpan").FirstOrDefault();
            items.Add(new RawSearchItem(span?.Val("startTime"), span?.Val("endTime"), m.DescVal("playbackURI")));
        }
        return new SearchPage(root.Val("responseStatusStrg"), Int(root.Val("numOfMatches")) ?? items.Count, items);
    }

    /// <summary>The fields of a &lt;ResponseStatus&gt; document, or null when the root is something else.</summary>
    public static IsapiStatus? ParseResponseStatus(XElement? root)
    {
        if (root is null || !root.Is("ResponseStatus")) return null;
        return new IsapiStatus(Int(root.Val("statusCode")), root.Val("statusString"), root.Val("subStatusCode"));
    }

    /// <summary>Hikvision's failed-login counters in a 401 body (userCheck or ResponseStatus), when present.</summary>
    public static LoginStatus ParseLoginStatus(XElement? root) => root is null
        ? new LoginStatus(null, null, null)
        : new LoginStatus(root.DescVal("lockStatus"), Int(root.DescVal("unlockTime")), Int(root.DescVal("retryLoginTime")));

    public static string BuildSearchRequest(string searchId, int trackId, string start, string end, int maxResults, int position)
    {
        var x = new XElement("CMSearchDescription",
            new XElement("searchID", searchId),
            new XElement("trackList", new XElement("trackID", trackId.ToString(CultureInfo.InvariantCulture))),
            new XElement("timeSpanList", new XElement("timeSpan", new XElement("startTime", start), new XElement("endTime", end))),
            new XElement("maxResults", maxResults.ToString(CultureInfo.InvariantCulture)),
            new XElement("searchResultPostion", position.ToString(CultureInfo.InvariantCulture)),
            new XElement("metadataList", new XElement("metadataDescriptor", "//recordType.meta.std-cgi.com")));
        return "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" + x.ToString(SaveOptions.DisableFormatting);
    }

    public static string BuildDownloadRequest(string playbackUri)
    {
        XNamespace ns = "http://www.isapi.org/ver20/XMLSchema";
        var x = new XElement(ns + "downloadRequest", new XAttribute("version", "1.0"), new XElement(ns + "playbackURI", playbackUri));
        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" + x.ToString(SaveOptions.DisableFormatting);
    }
}
