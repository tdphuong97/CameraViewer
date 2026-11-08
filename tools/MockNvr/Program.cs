using System.Globalization;
using MockNvr;

var o = MockOptions.Parse(args, out var error);
if (o is null)
{
    if (error is not null) Console.Error.WriteLine("error: " + error + Environment.NewLine);
    Console.WriteLine(MockOptions.Usage);
    return error is null ? 0 : 2;
}

Console.OutputEncoding = System.Text.Encoding.UTF8;
var dataDir = o.ResolveDataDir();
var clips = new ClipCache(dataDir);
Console.WriteLine($"MockNvr {MockDevice.Model} on http://{o.Bind}:{o.Port}  user {o.User}  (password not shown)");
Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
    $"  time mode {o.TimeMode}, device zone UTC{MockClock.FormatOffset(o.UtcOffset)}, download {o.Download} ({o.DownloadCodec}{(o.Audio ? " + G.711" : "")}{(o.DownloadMbps > 0 ? $", {o.DownloadMbps} Mbit/s" : "")})"));
Console.WriteLine($"  people detection {(o.People ? "on channels " + string.Join(", ", PeopleStore.Channels) : "off (eventRecordSearch answers notSupport)")}");
Console.WriteLine($"  lock-out after {o.MaxFailedLogins} wrong passwords for {o.LockDuration.TotalMinutes:0.#} min, nonce lifetime {o.NonceLifetime.TotalSeconds:0.#} s");
Console.WriteLine($"  data {dataDir}");
foreach (var c in new[] { VideoCodec.H265, VideoCodec.H264 })
    if (!File.Exists(clips.PathOf(c))) Console.WriteLine($"  warning: {clips.PathOf(c)} missing, downloads in {c} will fail (tools\\mock\\start-mock.ps1 creates it)");

var app = MockServer.Build(o);
await app.RunAsync();
return 0;
