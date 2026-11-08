using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Cv.Core.Tests.Fakes;

/// <summary>In-process fake of a Hikvision ISAPI endpoint. Routes by method + path (query ignored); can require Digest.</summary>
public sealed class FakeIsapiHandler : HttpMessageHandler
{
    public sealed record Seen(HttpMethod Method, string PathAndQuery, string? Authorization, string Body, string? ContentType = null, string? Host = null);

    public const string Realm = "DS-Fake";
    public const string Nonce = "abc";

    private readonly object _lock = new();
    private readonly Dictionary<string, Func<Seen, CancellationToken, HttpResponseMessage>> _routes = new();
    private readonly List<Seen> _requests = [];

    public IReadOnlyList<Seen> Requests { get { lock (_lock) return _requests.ToArray(); } }
    public bool RequireDigest { get; set; }
    public string User { get; set; } = "admin";
    public string Password { get; set; } = "secret";
    /// <summary>Wrong password answered like real firmware: a bare 401 with this body (lockStatus, retryLoginTime...).</summary>
    public string? FailedLoginBody { get; set; }
    /// <summary>Thrown from SendAsync (network error).</summary>
    public Exception? Throw { get; set; }
    /// <summary>Delay before every answer (timeouts).</summary>
    public TimeSpan Delay { get; set; }

    public FakeIsapiHandler On(HttpMethod m, string path, Func<Seen, CancellationToken, HttpResponseMessage> reply)
    {
        lock (_lock) _routes[m.Method + " " + path] = reply;
        return this;
    }

    public FakeIsapiHandler On(HttpMethod m, string path, Func<Seen, HttpResponseMessage> reply) => On(m, path, (s, _) => reply(s));

    public FakeIsapiHandler Xml(string path, string xml, HttpStatusCode status = HttpStatusCode.OK) =>
        On(HttpMethod.Get, path, _ => Text(xml, status));

    public int Count(string path) => Requests.Count(r => r.PathAndQuery.StartsWith(path, StringComparison.Ordinal));

    public static HttpResponseMessage Text(string body, HttpStatusCode status = HttpStatusCode.OK, string type = "application/xml") =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, type) };

    public static HttpResponseMessage Bytes(byte[] body, HttpStatusCode status = HttpStatusCode.OK)
    {
        var c = new ByteArrayContent(body);
        c.Headers.TryAddWithoutValidation("Content-Type", "application/octet-stream");
        return new HttpResponseMessage(status) { Content = c };
    }

    /// <summary>Sends <paramref name="head"/> and then never another byte (a stalled download).</summary>
    public static HttpResponseMessage Stalling(byte[] head) =>
        new(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream(head)) };

    public static string Md5(string s) => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    public static string? Param(string header, string name)
    {
        var m = Regex.Match(header, "(?:^|[\\s,])" + name + "=\"?([^\",]*)\"?");
        return m.Success ? m.Groups[1].Value : null;
    }

    private bool DigestOk(Seen s)
    {
        var h = s.Authorization;
        if (h is null || !h.StartsWith("Digest ", StringComparison.Ordinal)) return false;
        var uri = Param(h, "uri");
        var ha1 = Md5($"{User}:{Realm}:{Password}");
        var ha2 = Md5($"{s.Method.Method}:{uri}");
        return Param(h, "username") == User && Param(h, "nonce") == Nonce
            && Param(h, "response") == Md5($"{ha1}:{Nonce}:{Param(h, "nc")}:{Param(h, "cnonce")}:auth:{ha2}");
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (Throw is not null) throw Throw;
        if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
        ct.ThrowIfCancellationRequested();
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        var seen = new Seen(request.Method, request.RequestUri!.PathAndQuery, request.Headers.Authorization?.ToString(), body,
            request.Content?.Headers.ContentType?.MediaType, request.RequestUri.Authority);
        lock (_lock) _requests.Add(seen);

        if (RequireDigest && !DigestOk(seen))
        {
            if (seen.Authorization is not null && FailedLoginBody is not null)
                return Text(FailedLoginBody, HttpStatusCode.Unauthorized);
            var r401 = new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("") };
            r401.Headers.TryAddWithoutValidation("WWW-Authenticate", $"Digest realm=\"{Realm}\",nonce=\"{Nonce}\",qop=\"auth\"");
            return r401;
        }
        Func<Seen, CancellationToken, HttpResponseMessage>? f;
        lock (_lock) _routes.TryGetValue(request.Method.Method + " " + request.RequestUri.AbsolutePath, out f);
        if (f is null) return Text("<html>Not Found</html>", HttpStatusCode.NotFound, "text/html");
        return f(seen, ct);
    }

    private sealed class StallingStream(byte[] head) : Stream
    {
        private int _pos;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_pos < head.Length)
            {
                var n = Math.Min(buffer.Length, head.Length - _pos);
                head.AsMemory(_pos, n).CopyTo(buffer);
                _pos += n;
                return n;
            }
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
    }
}
