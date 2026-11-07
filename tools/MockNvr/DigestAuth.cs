using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MockNvr;

public enum AuthOutcome
{
    Ok,
    /// <summary>No (or a non-digest) Authorization header: plain challenge, not a failed login.</summary>
    Challenge,
    /// <summary>Nonce unknown or older than its lifetime: not a failed login.</summary>
    StaleNonce,
    /// <summary>Wrong user or password: counted towards the lock-out.</summary>
    WrongPassword,
    /// <summary>This IP is locked after too many wrong passwords.</summary>
    Locked,
    BadRequest,
}

public readonly record struct AuthResult(AuthOutcome Outcome, int RetriesLeft = 0, int UnlockSeconds = 0, string? User = null);

/// <summary>
/// Server side of RFC 2617 digest (MD5, qop=auth) as Hikvision does it: realm = model, short-lived nonces (any number
/// of uses while fresh), failed logins counted per client IP and the IP locked for a while after too many.
/// </summary>
public sealed partial class DigestAuth(MockOptions options, TimeProvider time)
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _nonces = new();
    private readonly Dictionary<string, (int Failures, DateTimeOffset LockedUntil)> _ips = [];
    private readonly object _gate = new();

    /// <summary>A new nonce, Hikvision style: hex of a base64 text.</summary>
    public string NewNonce()
    {
        var now = time.GetUtcNow();
        foreach (var kv in _nonces)
            if (now - kv.Value > TimeSpan.FromMinutes(1)) _nonces.TryRemove(kv.Key, out _);
        var raw = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant() + ":" + now.ToUnixTimeMilliseconds();
        var nonce = Convert.ToHexString(Encoding.ASCII.GetBytes(Convert.ToBase64String(Encoding.ASCII.GetBytes(raw)))).ToLowerInvariant();
        _nonces[nonce] = now;
        return nonce;
    }

    public string Challenge(bool stale = false) =>
        $"Digest qop=\"auth\", realm=\"{options.Realm}\", nonce=\"{NewNonce()}\", stale=\"{(stale ? "TRUE" : "FALSE")}\"";

    /// <summary>Checks one request. <paramref name="rawTarget"/> is the request target as sent (path + query).</summary>
    public AuthResult Check(string method, string rawTarget, string? authorization, string clientIp)
    {
        var now = time.GetUtcNow();
        if (string.IsNullOrEmpty(authorization) || !authorization.StartsWith("Digest ", StringComparison.OrdinalIgnoreCase))
            return new AuthResult(AuthOutcome.Challenge);

        lock (_gate)
        {
            if (_ips.TryGetValue(clientIp, out var st) && st.LockedUntil > now)
                return new AuthResult(AuthOutcome.Locked, 0, (int)Math.Ceiling((st.LockedUntil - now).TotalSeconds));
        }

        var p = Fields(authorization["Digest ".Length..]);
        p.TryGetValue("username", out var user);
        p.TryGetValue("realm", out var realm);
        p.TryGetValue("nonce", out var nonce);
        p.TryGetValue("uri", out var uri);
        p.TryGetValue("response", out var response);
        p.TryGetValue("qop", out var qop);
        p.TryGetValue("nc", out var nc);
        p.TryGetValue("cnonce", out var cnonce);
        if (user is null || nonce is null || uri is null || response is null) return new AuthResult(AuthOutcome.BadRequest);
        if (!string.Equals(uri, rawTarget, StringComparison.Ordinal) && !string.Equals(Uri.UnescapeDataString(uri), Uri.UnescapeDataString(rawTarget), StringComparison.Ordinal))
            return new AuthResult(AuthOutcome.BadRequest);

        if (!_nonces.TryGetValue(nonce, out var issued) || now - issued > options.NonceLifetime)
            return new AuthResult(AuthOutcome.StaleNonce);

        var ha1 = Md5($"{user}:{realm}:{options.Password}");
        var ha2 = Md5($"{method}:{uri}");
        var expected = string.IsNullOrEmpty(qop) ? Md5($"{ha1}:{nonce}:{ha2}") : Md5($"{ha1}:{nonce}:{nc}:{cnonce}:{qop}:{ha2}");
        var ok = user == options.User && realm == options.Realm &&
                 CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(response.ToLowerInvariant()));

        lock (_gate)
        {
            if (ok)
            {
                _ips.Remove(clientIp);
                return new AuthResult(AuthOutcome.Ok, User: user);
            }
            var failures = (_ips.TryGetValue(clientIp, out var st) && st.LockedUntil <= now && st.LockedUntil != default ? 0 : st.Failures) + 1;
            if (failures >= options.MaxFailedLogins)
            {
                var until = now + options.LockDuration;
                _ips[clientIp] = (failures, until);
                return new AuthResult(AuthOutcome.Locked, 0, (int)Math.Ceiling(options.LockDuration.TotalSeconds));
            }
            _ips[clientIp] = (failures, default);
            return new AuthResult(AuthOutcome.WrongPassword, options.MaxFailedLogins - failures);
        }
    }

    private static Dictionary<string, string> Fields(string s)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in FieldRegex().Matches(s))
            d[m.Groups[1].Value] = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
        return d;
    }

    [GeneratedRegex("([A-Za-z0-9_-]+)\\s*=\\s*(?:\"([^\"]*)\"|([^,\\s]*))")]
    private static partial Regex FieldRegex();

    private static string Md5(string s) => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
}
