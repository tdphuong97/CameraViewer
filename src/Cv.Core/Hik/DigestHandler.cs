using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Cv.Core.Hik;

/// <summary>
/// RFC 2617 Digest (MD5, qop=auth). The nonce from the first challenge is cached and reused with an increasing nc;
/// a 401 on a request that used the cached nonce (stale) is answered once with the fresh challenge, or, when that 401
/// carries no challenge (Hikvision firmware drops nonces after a few seconds that way), the login starts over with a
/// plain request first. A 401 on the answered request is returned to the caller as-is (HikClient turns it into
/// HikAuthException), so there is never a loop.
/// After that final 401 the cached nonce is dropped: the next call then starts without credentials (a plain
/// challenge, not a failed login) and costs at most one failed login instead of two, so a wrong password does not
/// walk the device towards its IP lock-out twice as fast.
/// </summary>
public sealed class DigestHandler : DelegatingHandler
{
    /// <summary>
    /// Measured on DS-K1T9342MFWX V4.39: a nonce still worked 2.2 s after it was issued and was refused at 3.4 s (any
    /// number of uses is fine). An older cached nonce is not sent; the call asks for a fresh challenge instead.
    /// </summary>
    public static readonly TimeSpan NonceMaxAge = TimeSpan.FromSeconds(2);

    private readonly string _user, _password;
    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private string? _realm, _nonce, _opaque;
    private DateTimeOffset _issuedAt;
    private int _nc;

    public DigestHandler(string user, string password, HttpMessageHandler inner, TimeProvider? time = null) : base(inner)
    {
        _user = user; _password = password; _time = time ?? TimeProvider.System;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        byte[]? body = null;
        if (request.Content is not null) body = await request.Content.ReadAsByteArrayAsync(ct);

        string? cached = Authorize(request);
        var first = Clone(request, body, cached);
        var resp = await base.SendAsync(first, ct);
        if (resp.StatusCode != HttpStatusCode.Unauthorized) return resp;

        var challenge = DigestChallenge(resp);
        if (challenge is null && cached is not null)
        {
            if (await IsFailedLoginAsync(resp, ct)) { Forget(); return resp; }
            // The device dropped the cached nonce and sent no new one: DS-K1T9342MFWX V4.39 keeps a nonce ~3 s and
            // then answers a request still carrying it with a bare 401. Not a wrong password: start over with a plain
            // request (no credentials) for a fresh challenge.
            Forget();
            resp.Dispose();
            resp = await base.SendAsync(Clone(request, body, null), ct);
            if (resp.StatusCode != HttpStatusCode.Unauthorized) return resp;
            challenge = DigestChallenge(resp);
        }
        for (var login = 1; ; login++)
        {
            if (challenge?.Parameter is null) { Forget(); return resp; }
            lock (_lock)
            {
                _realm = Field(challenge.Parameter, "realm") ?? "";
                _nonce = Field(challenge.Parameter, "nonce") ?? "";
                _opaque = Field(challenge.Parameter, "opaque");
                _issuedAt = _time.GetUtcNow();
                _nc = 0;
            }
            resp.Dispose();
            var answered = await base.SendAsync(Clone(request, body, Authorize(request)), ct);
            if (answered.StatusCode != HttpStatusCode.Unauthorized) return answered;
            Forget();
            // A wrong password comes back with a new challenge (RFC) or, on Hikvision, with the failed-login counters
            // (lockStatus, retryLoginTime): stop at once. A bare 401 without them means another client logged in to
            // the device between our challenge and our answer and the device dropped our nonce (seen on site): one
            // more login, then give up.
            if (login == 2 || DigestChallenge(answered) is not null || await IsFailedLoginAsync(answered, ct)) return answered;
            answered.Dispose();
            resp = await base.SendAsync(Clone(request, body, null), ct);
            if (resp.StatusCode != HttpStatusCode.Unauthorized) return resp;
            challenge = DigestChallenge(resp);
        }
    }

    /// <summary>
    /// True when a 401 body carries Hikvision's failed-login counters (&lt;lockStatus&gt;, &lt;retryLoginTime&gt;):
    /// the device counted a wrong password or is locked. The body is buffered so the caller can still read it.
    /// </summary>
    private static async Task<bool> IsFailedLoginAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        await resp.Content.LoadIntoBufferAsync(ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        return text.Contains("retryLoginTime", StringComparison.OrdinalIgnoreCase)
            || text.Contains("lockStatus", StringComparison.OrdinalIgnoreCase);
    }

    private static AuthenticationHeaderValue? DigestChallenge(HttpResponseMessage resp) =>
        resp.Headers.WwwAuthenticate.FirstOrDefault(h => h.Scheme.Equals("Digest", StringComparison.OrdinalIgnoreCase));

    private void Forget()
    {
        lock (_lock) { _realm = null; _nonce = null; _opaque = null; _nc = 0; }
    }

    private string? Authorize(HttpRequestMessage req)
    {
        lock (_lock)
        {
            if (_nonce is null) return null;
            if (_time.GetUtcNow() - _issuedAt > NonceMaxAge) { _realm = null; _nonce = null; _opaque = null; _nc = 0; return null; }
            var nc = (++_nc).ToString("x8");
            var cnonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
            var uri = req.RequestUri!.PathAndQuery;
            var ha1 = Md5($"{_user}:{_realm}:{_password}");
            var ha2 = Md5($"{req.Method.Method}:{uri}");
            var response = Md5($"{ha1}:{_nonce}:{nc}:{cnonce}:auth:{ha2}");
            var sb = new StringBuilder();
            sb.Append($"username=\"{_user}\", realm=\"{_realm}\", nonce=\"{_nonce}\", uri=\"{uri}\", algorithm=MD5, ");
            sb.Append($"qop=auth, nc={nc}, cnonce=\"{cnonce}\", response=\"{response}\"");
            if (!string.IsNullOrEmpty(_opaque)) sb.Append($", opaque=\"{_opaque}\"");
            return sb.ToString();
        }
    }

    private static HttpRequestMessage Clone(HttpRequestMessage src, byte[]? body, string? digest)
    {
        var c = new HttpRequestMessage(src.Method, src.RequestUri) { Version = src.Version };
        foreach (var h in src.Headers) c.Headers.TryAddWithoutValidation(h.Key, h.Value);
        if (body is not null)
        {
            c.Content = new ByteArrayContent(body);
            foreach (var h in src.Content!.Headers) c.Content.Headers.TryAddWithoutValidation(h.Key, h.Value);
        }
        if (digest is not null) c.Headers.Authorization = new AuthenticationHeaderValue("Digest", digest);
        return c;
    }

    private static string? Field(string s, string name)
    {
        var m = Regex.Match(s, "(?:^|[\\s,])" + name + "=(?:\"([^\"]*)\"|([^,\\s]*))", RegexOptions.IgnoreCase);
        return m.Success ? (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value) : null;
    }

    private static string Md5(string s) => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
}
