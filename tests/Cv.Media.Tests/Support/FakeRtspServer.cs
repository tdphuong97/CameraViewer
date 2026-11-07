using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Cv.Media.Tests.Support;

/// <summary>
/// Minimal RTSP responder for error-path tests: hangs, answers 401/404, or checks Basic/Digest credentials
/// (to prove FFmpeg percent-decodes the password in the URL) and then answers 404.
/// </summary>
public sealed class FakeRtspServer : IDisposable
{
    public enum Mode { Hang, Unauthorized, NotFound, BasicAuth, DigestAuth }

    private const string Realm = "TestRealm";
    private const string Nonce = "3f5c0d9e7b1a";
    private readonly TcpListener _listener;
    private readonly Mode _mode;
    private readonly string _user, _password;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<TcpClient> _clients = [];

    public int Port { get; }
    public int Requests;
    public string? BasicCredentials { get; private set; }
    public bool? DigestValid { get; private set; }

    public FakeRtspServer(Mode mode, string user = "admin", string password = "Mock@1234")
    {
        _mode = mode;
        _user = user;
        _password = password;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptLoop();
    }

    public string Url(string userInfo = "") => $"rtsp://{userInfo}127.0.0.1:{Port}/Streaming/Channels/101";

    private async Task AcceptLoop()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var c = await _listener.AcceptTcpClientAsync(_cts.Token);
                lock (_clients) _clients.Add(c);
                _ = Serve(c);
            }
        }
        catch (Exception) { /* stopped */ }
    }

    private async Task Serve(TcpClient c)
    {
        try
        {
            var stream = c.GetStream();
            var reader = new StreamReader(stream, Encoding.ASCII);
            while (!_cts.IsCancellationRequested)
            {
                var first = await reader.ReadLineAsync(_cts.Token);
                if (first is null) return;
                if (first.Length == 0) continue;
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                string? line;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(_cts.Token)))
                {
                    int colon = line.IndexOf(':');
                    if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
                }
                Interlocked.Increment(ref Requests);
                if (_mode == Mode.Hang) continue; // read forever, answer nothing
                var parts = first.Split(' ');
                string method = parts[0], uri = parts.Length > 1 ? parts[1] : "";
                string cseq = headers.GetValueOrDefault("CSeq", "0");
                string reply = method == "OPTIONS"
                    ? Reply(cseq, "200 OK", "Public: OPTIONS, DESCRIBE, SETUP, PLAY, PAUSE, TEARDOWN\r\n")
                    : Answer(method, uri, cseq, headers);
                var bytes = Encoding.ASCII.GetBytes(reply);
                await stream.WriteAsync(bytes, _cts.Token);
            }
        }
        catch (Exception) { /* client gone */ }
    }

    private string Answer(string method, string uri, string cseq, Dictionary<string, string> headers)
    {
        switch (_mode)
        {
            case Mode.Unauthorized:
                return Reply(cseq, "401 Unauthorized", $"WWW-Authenticate: Digest realm=\"{Realm}\", nonce=\"{Nonce}\"\r\n");
            case Mode.NotFound:
                return Reply(cseq, "404 Not Found", "");
            case Mode.BasicAuth:
                if (headers.TryGetValue("Authorization", out var basic) && basic.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
                {
                    BasicCredentials = Encoding.UTF8.GetString(Convert.FromBase64String(basic[6..].Trim()));
                    return Reply(cseq, "404 Not Found", "");
                }
                return Reply(cseq, "401 Unauthorized", $"WWW-Authenticate: Basic realm=\"{Realm}\"\r\n");
            case Mode.DigestAuth:
                if (headers.TryGetValue("Authorization", out var digest) && digest.StartsWith("Digest ", StringComparison.OrdinalIgnoreCase))
                {
                    DigestValid = CheckDigest(method, digest[7..]);
                    return DigestValid == true ? Reply(cseq, "404 Not Found", "") : Reply(cseq, "401 Unauthorized", "");
                }
                return Reply(cseq, "401 Unauthorized", $"WWW-Authenticate: Digest realm=\"{Realm}\", nonce=\"{Nonce}\"\r\n");
            default:
                return Reply(cseq, "500 Internal Server Error", "");
        }
    }

    private bool CheckDigest(string method, string paramText)
    {
        var p = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in SplitParams(paramText))
        {
            int eq = item.IndexOf('=');
            if (eq > 0) p[item[..eq].Trim()] = item[(eq + 1)..].Trim().Trim('"');
        }
        if (p.GetValueOrDefault("username") != _user) return false;
        string ha1 = Md5($"{_user}:{Realm}:{_password}");
        string ha2 = Md5($"{method}:{p.GetValueOrDefault("uri")}");
        string expected = p.TryGetValue("qop", out var qop)
            ? Md5($"{ha1}:{Nonce}:{p["nc"]}:{p["cnonce"]}:{qop}:{ha2}")
            : Md5($"{ha1}:{Nonce}:{ha2}");
        return string.Equals(expected, p.GetValueOrDefault("response"), StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> SplitParams(string s)
    {
        var sb = new StringBuilder();
        bool quoted = false;
        foreach (char ch in s)
        {
            if (ch == '"') quoted = !quoted;
            if (ch == ',' && !quoted) { yield return sb.ToString(); sb.Clear(); continue; }
            sb.Append(ch);
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    private static string Md5(string s) => Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(s)));

    private static string Reply(string cseq, string status, string extra) => $"RTSP/1.0 {status}\r\nCSeq: {cseq}\r\n{extra}\r\n";

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        lock (_clients) foreach (var c in _clients) c.Dispose();
    }
}
