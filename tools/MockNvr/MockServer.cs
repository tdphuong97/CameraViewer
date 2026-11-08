using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.Http.Features;

namespace MockNvr;

/// <summary>Builds the fake recorder's web server (also used in-process by the tests).</summary>
public static class MockServer
{
    private const string XmlType = "application/xml; charset=\"UTF-8\"";

    public static WebApplication Build(MockOptions o, TimeProvider? time = null, Action<string>? log = null)
    {
        time ??= TimeProvider.System;
        log ??= o.Quiet ? _ => { } : Console.WriteLine;
        var clock = new MockClock(o, time);
        var hikTime = new HikTime(clock, o.TimeMode);
        var store = new RecordingStore(clock);
        var people = new PeopleStore(store);
        var cursors = new ConcurrentDictionary<string, int>(); // eventRecordSearch: searchID -> next result
        var auth = new DigestAuth(o, time);
        var clips = new ClipCache(o.ResolveDataDir());

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Limits.MaxRequestBodySize = 1024 * 1024;
            k.Listen(IPAddress.Parse(o.Bind), o.Port);
        });
        var app = builder.Build();

        // Request log + digest auth for everything under /ISAPI and /picture.
        app.Use(async (ctx, next) =>
        {
            var sw = Stopwatch.StartNew();
            var counting = new CountingStream(ctx.Response.Body);
            ctx.Response.Body = counting;
            ctx.Response.Headers.Server = "App-webs/";
            var target = ctx.Features.Get<IHttpRequestFeature>()?.RawTarget ?? ctx.Request.Path + ctx.Request.QueryString;
            var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "?";
            var note = "";
            try
            {
                if (ctx.Request.Path.StartsWithSegments("/ISAPI") || ctx.Request.Path.StartsWithSegments("/picture"))
                {
                    var r = auth.Check(ctx.Request.Method, target, ctx.Request.Headers.Authorization, ip);
                    note = r.Outcome switch
                    {
                        AuthOutcome.Ok => $"[{r.User}]",
                        AuthOutcome.Challenge => "[challenge]",
                        AuthOutcome.StaleNonce => "[stale nonce]",
                        AuthOutcome.WrongPassword => $"[WRONG PASSWORD, {r.RetriesLeft} tries left]",
                        AuthOutcome.Locked => $"[LOCKED {r.UnlockSeconds} s]",
                        _ => "[bad digest header]",
                    };
                    if (r.Outcome != AuthOutcome.Ok)
                    {
                        await Reject(ctx, r, auth, o);
                        return;
                    }
                }
                await next(ctx);
                if (ctx.Items.TryGetValue("note", out var n)) note += " " + n;
            }
            finally
            {
                log($"{time.GetLocalNow():HH:mm:ss.fff} {ip,-15} {ctx.Request.Method,-4} {target} -> {ctx.Response.StatusCode} {Size(counting.Count)} {sw.ElapsedMilliseconds} ms {note}".TrimEnd());
            }
        });

        app.MapGet("/ISAPI/System/deviceInfo", () => Xml(IsapiXml.DeviceInfo()));
        app.MapGet("/ISAPI/System/time", () => Xml(IsapiXml.Time(clock)));
        app.MapGet("/ISAPI/ContentMgmt/InputProxy/channels", () => Xml(IsapiXml.InputProxyChannels()));
        app.MapGet("/ISAPI/ContentMgmt/InputProxy/channels/status", () => Xml(IsapiXml.InputProxyStatus()));
        app.MapGet("/ISAPI/Streaming/channels", () => Xml(IsapiXml.StreamingChannels()));
        app.MapGet("/ISAPI/Security/adminAccesses", () => Xml(IsapiXml.AdminAccesses(o)));
        app.MapGet("/ISAPI/Security/userCheck", () => Xml(IsapiXml.UserCheckOk()));
        app.MapPost("/ISAPI/ContentMgmt/search", (Func<HttpContext, Task<IResult>>)(ctx => SearchAsync(ctx, store, hikTime, o)));
        app.MapMethods("/ISAPI/ContentMgmt/download", ["GET", "POST"], (HttpContext ctx) => DownloadAsync(ctx, store, hikTime, clips, o));
        app.MapPost("/ISAPI/ContentMgmt/eventRecordSearch", (Func<HttpContext, Task<IResult>>)(ctx => PeopleApi.SearchAsync(ctx, people, clock, o, cursors)));
        app.MapGet("/ISAPI/ContentMgmt/eventRecordSearch/capabilities", () => PeopleApi.Capabilities(o));
        app.MapGet("/picture/{**rest}", (HttpContext ctx) => PeopleApi.Picture(ctx, people));
        app.MapFallback((HttpContext ctx) => ctx.Request.Path.StartsWithSegments("/ISAPI")
            ? Xml(IsapiXml.ResponseStatus(ctx.Request.Path, 4, "Invalid Operation", "notSupport"), 403)
            : Results.NotFound());
        return app;
    }

    private static async Task Reject(HttpContext ctx, AuthResult r, DigestAuth auth, MockOptions o)
    {
        ctx.Response.StatusCode = r.Outcome == AuthOutcome.BadRequest ? 400 : 401;
        switch (r.Outcome)
        {
            case AuthOutcome.Challenge:
                ctx.Response.Headers.WWWAuthenticate = auth.Challenge();
                ctx.Response.ContentType = "text/html";
                await ctx.Response.WriteAsync(IsapiXml.UnauthorizedHtml);
                break;
            case AuthOutcome.StaleNonce:
                // Hikvision answers a dropped nonce with a bare 401 (no challenge, no counters): not a failed login.
                if (o.StaleChallenge) ctx.Response.Headers.WWWAuthenticate = auth.Challenge(stale: true);
                ctx.Response.ContentType = "text/html";
                await ctx.Response.WriteAsync(IsapiXml.UnauthorizedHtml);
                break;
            case AuthOutcome.WrongPassword:
            case AuthOutcome.Locked:
                ctx.Response.Headers.WWWAuthenticate = auth.Challenge();
                ctx.Response.ContentType = XmlType;
                await ctx.Response.WriteAsync(IsapiXml.UserCheckFailed(r.Outcome == AuthOutcome.Locked, r.RetriesLeft, r.UnlockSeconds));
                break;
            default:
                ctx.Response.ContentType = XmlType;
                await ctx.Response.WriteAsync(IsapiXml.ResponseStatus(ctx.Request.Path, 6, "Invalid Content", "badAuthorization"));
                break;
        }
    }

    private static IResult Xml(string body, int status = 200) => Results.Text(body, XmlType, Encoding.UTF8, status);

    private static async Task<IResult> SearchAsync(HttpContext ctx, RecordingStore store, HikTime t, MockOptions o)
    {
        var doc = await ReadXmlAsync(ctx);
        if (doc is null) return Xml(IsapiXml.ResponseStatus("/ISAPI/ContentMgmt/search", 6, "Invalid XML Content", "badXmlContent"), 400);
        var searchId = Text(doc, "searchID") ?? Guid.NewGuid().ToString().ToUpperInvariant();
        var trackText = Text(doc, "trackID");
        var max = int.TryParse(Text(doc, "maxResults"), out var m) ? m : 40;
        var pos = int.TryParse(Text(doc, "searchResultPostion") ?? Text(doc, "searchResultPosition"), out var p) ? p : 0;
        if (!int.TryParse(trackText, out var track) || !t.TryParse(Text(doc, "startTime"), out var from) || !t.TryParse(Text(doc, "endTime"), out var to) || max <= 0 || pos < 0)
            return Xml(IsapiXml.ResponseStatus("/ISAPI/ContentMgmt/search", 6, "Invalid Content", "badParameters"), 400);

        // Only the main stream is recorded (dual-stream recording off, the factory default).
        var all = track % 100 == 1 ? store.Find(track / 100, from, to) : [];
        var pageSize = Math.Min(max, 50); // firmware caps a page at 50 whatever maxResults says
        var page = pos < all.Count ? all.Skip(pos).Take(pageSize).ToList() : [];
        ctx.Items["note"] = $"search track {track} {from:yyyy-MM-dd HH:mm:ss}..{to:HH:mm:ss} pos {pos} max {max}: {page.Count} of {all.Count}";
        var host = ctx.Request.Host.Host;
        return Xml(IsapiXml.SearchResult(searchId, page, pos, all.Count, t, host, o.DownloadCodec == VideoCodec.H265 ? "H.265" : "H.264"));
    }

    private static async Task DownloadAsync(HttpContext ctx, RecordingStore store, HikTime t, ClipCache clips, MockOptions o)
    {
        const string url = "/ISAPI/ContentMgmt/download";
        async Task Fail(int http, int code, string text, string sub, string note)
        {
            ctx.Items["note"] = note;
            ctx.Response.StatusCode = http;
            ctx.Response.ContentType = XmlType;
            await ctx.Response.WriteAsync(IsapiXml.ResponseStatus(url, code, text, sub));
        }

        var doc = await ReadXmlAsync(ctx);
        var uriText = doc is null ? null : Text(doc, "playbackURI");
        if (uriText is null || !Uri.TryCreate(uriText.Trim(), UriKind.Absolute, out var uri))
        {
            await Fail(400, 6, "Invalid XML Content", "badXmlContent", "download: no playbackURI");
            return;
        }
        var q = ParseQuery(uri.Query);
        q.TryGetValue("name", out var name);
        var segmentUri = !string.IsNullOrEmpty(name);
        var trackText = uri.AbsolutePath.TrimEnd('/').Split('/').LastOrDefault();
        var kind = segmentUri ? "segment" : "time";

        if (o.Download == DownloadMode.XmlError)
        {
            // The sneaky case: HTTP 200 with an XML error instead of video.
            await Fail(200, 4, "Invalid Operation", "notSupport", $"download {kind} uri: refused (xml-error mode, 200 + XML)");
            return;
        }
        if (!int.TryParse(trackText, out var track) || track % 100 != 1)
        {
            await Fail(400, 6, "Invalid Content", "badParameters", $"download: bad track '{trackText}'");
            return;
        }
        var channel = track / 100;

        DateTime from, to;
        if (o.Download == DownloadMode.Whole || (o.Download == DownloadMode.Framed && segmentUri))
        {
            if (!segmentUri)
            {
                await Fail(403, 4, "Invalid Operation", "notSupport", "download time uri: refused (whole mode only serves segment files)");
                return;
            }
            var seg = store.FindByName(name);
            if (seg is null || seg.Channel != channel)
            {
                await Fail(404, 4, "Invalid Operation", "noRecord", $"download segment {name}: not found");
                return;
            }
            (from, to) = (seg.Start, seg.End);
        }
        else
        {
            if (!q.TryGetValue("starttime", out var st) || !q.TryGetValue("endtime", out var et) || !t.TryParse(st, out from) || !t.TryParse(et, out to) || to <= from)
            {
                await Fail(400, 6, "Invalid Content", "badParameters", "download: bad starttime/endtime");
                return;
            }
            if (segmentUri && store.FindByName(name) is { } seg)
            {
                if (from < seg.Start) from = seg.Start;
                if (to > seg.End) to = seg.End;
            }
            var recorded = store.Find(channel, from, to);
            if (recorded.Count == 0 || to <= from)
            {
                await Fail(404, 4, "Invalid Operation", "noRecord", $"download ch{channel} {from:HH:mm:ss}..{to:HH:mm:ss}: nothing recorded");
                return;
            }
            if (from < recorded[0].Start) from = recorded[0].Start;
            if (to > recorded[^1].End) to = recorded[^1].End;
        }

        var clip = clips.Get(o.DownloadCodec);
        if (clip is null)
        {
            await Fail(500, 3, "Device Error", "clipMissing", $"download: {clips.PathOf(o.DownloadCodec)} missing (run tools\\mock\\start-mock.ps1 once)");
            return;
        }

        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "application/octet-stream";
        var startPts = (long)(from - from.Date).TotalSeconds * HikPsWriter.Clock;
        var duration = to - from;
        var note = $"download {kind} uri ch{channel} {from:yyyy-MM-dd HH:mm:ss}..{to:HH:mm:ss} ({duration.TotalSeconds:0} s, {(clip.Codec == VideoCodec.H265 ? "H.265" : "H.264")})";
        ctx.Items["note"] = note;
        try
        {
            var body = ctx.Response.Body;
            if (o.Download == DownloadMode.Framed && !segmentUri)
            {
                // The firmware's own block first, then the stream cut a little before its end (inside the last frame).
                await body.WriteAsync(FramedPrefix, ctx.RequestAborted);
                body = new HoldBackStream(body, 40);
                ctx.Items["note"] = note + " [framed: 24-byte block, last frame cut]";
            }
            await HikPsWriter.WriteAsync(body, clip, duration, startPts, o.Audio, o.DownloadMbps, ctx.RequestAborted);
        }
        catch (OperationCanceledException)
        {
            ctx.Items["note"] = note + " - client went away";
        }
        catch (IOException)
        {
            ctx.Items["note"] = note + " - client went away";
        }
    }

    private static async Task<XDocument?> ReadXmlAsync(HttpContext ctx)
    {
        using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
        var body = await reader.ReadToEndAsync(ctx.RequestAborted);
        if (string.IsNullOrWhiteSpace(body)) return null;
        try { return XDocument.Parse(body); }
        catch (System.Xml.XmlException) { return null; }
    }

    /// <summary>First element with this local name, any namespace.</summary>
    private static string? Text(XDocument doc, string localName) =>
        doc.Descendants().FirstOrDefault(e => e.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase))?.Value.Trim();

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = part.IndexOf('=');
            if (i > 0) d[Uri.UnescapeDataString(part[..i])] = Uri.UnescapeDataString(part[(i + 1)..]);
        }
        return d;
    }

    /// <summary>The 24 bytes DS-7732NI-M4 V5.04.081 sends before "IMKH" when downloading by time.</summary>
    private static readonly byte[] FramedPrefix = [0, 0, 0, 0x40, 0, 0, 0, 0x13, 0, 0x0A, 0x3B, 0xC0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x28];

    private static string Size(long bytes) => bytes switch
    {
        < 10_000 => $"{bytes} B",
        < 10_000_000 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0.0} KB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1048576.0:0.0} MB"),
    };
}

/// <summary>Loads each cached elementary stream once.</summary>
public sealed class ClipCache(string dataDir)
{
    private readonly object _gate = new();
    private readonly Dictionary<VideoCodec, ElementaryClip> _clips = [];

    public string PathOf(VideoCodec c) => Path.Combine(dataDir, c == VideoCodec.H265 ? "es-h265.hevc" : "es-h264.h264");

    public ElementaryClip? Get(VideoCodec c)
    {
        lock (_gate)
        {
            if (_clips.TryGetValue(c, out var clip)) return clip;
            var path = PathOf(c);
            if (!File.Exists(path)) return null;
            return _clips[c] = ElementaryClip.Load(path, c);
        }
    }
}

/// <summary>Writes everything but the last <paramref name="hold"/> bytes: a stream that stops early.</summary>
internal sealed class HoldBackStream(Stream inner, int hold) : Stream
{
    private readonly byte[] _held = new byte[hold];
    private int _n;
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        var all = new byte[_n + buffer.Length];
        _held.AsSpan(0, _n).CopyTo(all);
        buffer.Span.CopyTo(all.AsSpan(_n));
        var emit = Math.Max(0, all.Length - hold);
        if (emit > 0) await inner.WriteAsync(all.AsMemory(0, emit), ct);
        _n = all.Length - emit;
        all.AsSpan(emit).CopyTo(_held);
    }
}

/// <summary>Counts the bytes of a response for the request log.</summary>
internal sealed class CountingStream(Stream inner) : Stream
{
    public long Count { get; private set; }
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) { inner.Write(buffer, offset, count); Count += count; }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        await inner.WriteAsync(buffer, ct);
        Count += buffer.Length;
    }
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();
}
