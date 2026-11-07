using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace Cv.App.Demo;

/// <summary>
/// Answers the real Updater in demo mode: a feed announcing 0.9.1 and a slow 3 MB "installer" whose hash matches, so the
/// banner, the progress and the verification all run for real without touching the network.
/// </summary>
internal sealed class DemoUpdateHandler : HttpMessageHandler
{
    public const string Feed = "https://demo.invalid/latest.json";
    const string Setup = "https://demo.invalid/CameraViewer-Setup-0.9.1.exe";
    const int PayloadSize = 3 * 1024 * 1024;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        await Task.Delay(300, ct);
        var url = request.RequestUri!.GetLeftPart(UriPartial.Path);
        if (url == Feed)
        {
            var sha = Convert.ToHexString(SHA256.HashData(new byte[PayloadSize]));
            var json = "{\"version\":\"0.9.1\",\"notes\":\"Bản thử\",\"page\":\"https://demo.invalid/\"," +
                $"\"windows\":{{\"url\":\"{Setup}\",\"size\":{PayloadSize},\"sha256\":\"{sha}\"}}}}";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
        if (url == Setup)
        {
            var content = new StreamContent(new SlowStream(new byte[PayloadSize]));
            content.Headers.ContentLength = PayloadSize;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    /// <summary>Reads 64 KB every 60 ms so the progress bar has something to show.</summary>
    sealed class SlowStream(byte[] data) : MemoryStream(data, false)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await Task.Delay(60, ct);
            return await base.ReadAsync(buffer[..Math.Min(buffer.Length, 64 * 1024)], ct);
        }
    }
}
