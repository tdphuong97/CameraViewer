using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Cv.Core.Hik;
using Cv.Core.Nvr;
using Cv.Core.Video;
using Cv.Diagnostics;
using Xunit.Abstractions;

namespace MockNvr.Tests;

/// <summary>People detections: the mock's eventRecordSearch and pictures, the app's client and the probe against it.</summary>
public sealed class PeopleAgainstMockTests(ITestOutputHelper output) : IDisposable
{
    private const string Password = "Mock@1234";
    private const string SearchPath = "/ISAPI/ContentMgmt/eventRecordSearch?format=json";
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "mocknvr-people-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { if (Directory.Exists(_temp)) Directory.Delete(_temp, true); } catch (IOException) { }
    }

    private static INvrClient Client(RunningMock m) =>
        new HikNvrClientFactory().Create(new NvrConfig { Host = "127.0.0.1", HttpPort = m.Port, User = "admin" }, Password);

    private static MockClock Clock(RunningMock m) => new(m.Options, TimeProvider.System);

    private static PeopleStore Store(RunningMock m) => new(new RecordingStore(Clock(m)));

    /// <summary>Yesterday 06:00-22:00, device time: finished recordings, at least ~200 detections per people channel.</summary>
    private static (DateTime From, DateTime To) Daytime(RunningMock m)
    {
        var day = Clock(m).LocalNow.Date.AddDays(-1);
        return (day.AddHours(6), day.AddHours(22));
    }

    [Fact]
    public async Task Client_pages_through_more_than_a_hundred_detections()
    {
        await using var m = await RunningMock.StartAsync();
        using var c = Client(m);
        var (from, to) = Daytime(m);
        var expected = Store(m).Find([1], from, to);
        Assert.True(expected.Count > 200, $"{expected.Count} detections: more than two pages");

        var events = await ((INvrDetections)c).SearchPeopleAsync(1, from, to, default);

        Assert.Equal(expected.Select(e => e.Time), events.Select(e => e.Time));
        Assert.All(events, e => Assert.Equal(1, e.Channel));
        Assert.All(events, e => Assert.StartsWith("/picture/Streaming/tracks/103/?name=ch00001_", e.PictureUri));
        Assert.All(events, e => Assert.Equal(DateTimeKind.Unspecified, e.Time.Kind));
        // The recorder's own cursor carried the pages (the mock ignores the position for a searchID it has seen).
        var pages = m.LogLines.Where(l => l.Contains("eventRecordSearch")).ToList();
        Assert.Equal((expected.Count + 99) / 100, pages.Count);
        Assert.Contains(pages, l => l.Contains("pos 100 (cursor 100)"));
        output.WriteLine(string.Join(Environment.NewLine, pages));
    }

    [Fact]
    public async Task Channels_stay_apart_and_quiet_channels_are_empty()
    {
        await using var m = await RunningMock.StartAsync();
        using var c = Client(m);
        var d = (INvrDetections)c;
        var (from, to) = Daytime(m);

        Assert.Empty(await d.SearchPeopleAsync(2, from, to, default));
        Assert.Empty(await d.SearchPeopleAsync(MockDevice.OfflineChannel, from, to, default));
        var d14 = await d.SearchPeopleAsync(14, from, to, default);
        Assert.NotEmpty(d14);
        Assert.Equal(Store(m).Find([14], from, to).Select(e => e.Time), d14.Select(e => e.Time));

        var o = await ((INvrPeopleOverview)c).PeopleOverviewAsync(MockDevice.Channels.Select(x => x.No).ToList(), from, to, default);
        Assert.Equal(Store(m).Find(MockDevice.Channels.Select(x => x.No), from, to).Count, o.Total);
        Assert.Equal(100, o.Events.Count);
        Assert.True(o.More);
        Assert.All(o.Events, e => Assert.Contains(e.Channel, PeopleStore.Channels));
    }

    [Fact]
    public async Task Picture_of_a_detection_is_the_jpeg_and_a_wrong_name_is_an_error()
    {
        await using var m = await RunningMock.StartAsync();
        using var c = Client(m);
        var d = (INvrDetections)c;
        var (from, to) = Daytime(m);
        var e = (await d.SearchPeopleAsync(3, from, to, default)).First();

        Assert.Equal(PeopleApi.Jpeg, await d.GetPictureAsync(e.PictureUri!, default));
        Assert.EndsWith($"size={PeopleApi.Jpeg.Length}", e.PictureUri);

        var wrong = e.PictureUri!.Replace("name=ch00003_", "name=ch00002_");
        var ex = await Assert.ThrowsAnyAsync<NvrException>(() => d.GetPictureAsync(wrong, default));
        Assert.Equal("Đầu ghi không trả về ảnh của lần phát hiện này.", ex.Message);
        Assert.Contains(m.LogLines, l => l.Contains("/picture/") && l.Contains("-> 404"));
    }

    [Fact]
    public async Task Recorder_without_people_search_says_so()
    {
        await using var m = await RunningMock.StartAsync(o => o.People = false);
        using var c = Client(m);
        var (from, to) = Daytime(m);

        var e = await Assert.ThrowsAsync<NvrDetectionUnsupportedException>(() => ((INvrDetections)c).SearchPeopleAsync(1, from, to, default));
        Assert.Equal("Đầu ghi này không cho tìm lúc có người.", e.Message);
        Assert.Contains(m.LogLines, l => l.Contains("eventRecordSearch") && l.Contains("-> 403"));
    }

    [Fact]
    public void No_people_option_is_parsed_and_documented()
    {
        Assert.False(MockOptions.Parse(["--no-people"], out _)!.People);
        Assert.True(MockOptions.Parse([], out _)!.People);
        Assert.Contains("--no-people", MockOptions.Usage);
    }

    /// <summary>The mock itself, on the wire: what the client relies on, as measured on the real recorder.</summary>
    [Fact]
    public async Task Mock_answers_like_the_real_recorder()
    {
        await using var m = await RunningMock.StartAsync();
        using var http = new HttpClient(new DigestHandler("admin", Password, new SocketsHttpHandler { UseProxy = false }))
        {
            BaseAddress = new Uri($"http://127.0.0.1:{m.Port}/"),
        };
        var (from, to) = Daytime(m);
        var total = Store(m).Find([1], from, to).Count;
        static string Local(DateTime t) => t.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + "+07:00";
        static string Utc(DateTime t) => (t - TimeSpan.FromHours(7)).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + "Z";
        async Task<JsonElement> Ask(string id, int position, string start, string end, string channels = "[1]", string type = "all", int max = 100)
        {
            var body = $$$"""{"EventSearchDescription":{"searchID":"{{{id}}}","searchResultPosition":{{{position}}},"maxResults":{{{max}}},"timeSpanList":[{"startTime":"{{{start}}}","endTime":"{{{end}}}"}],"type":"{{{type}}}","channels":{{{channels}}},"eventType":"peopleDetection"}}""";
            using var resp = await http.PostAsync(SearchPath, new StringContent(body, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            return doc.RootElement.Clone();
        }
        static int FirstId(JsonElement r) => r.GetProperty("EventSearchResult").GetProperty("Targets")[0].GetProperty("id").GetInt32();
        static string Status(JsonElement r) => r.GetProperty("EventSearchResult").GetProperty("responseStatusStrg").GetString()!;

        // Same searchID, position 0 twice: the second answer is the NEXT page. A new searchID starts over.
        var a = await Ask("A", 0, Local(from), Local(to));
        var b = await Ask("A", 0, Local(from), Local(to));
        var c = await Ask("B", 0, Local(from), Local(to));
        Assert.Equal((1, 101, 1), (FirstId(a), FirstId(b), FirstId(c)));
        Assert.Equal("MORE", Status(a));
        Assert.Equal(total, a.GetProperty("EventSearchResult").GetProperty("totalMatches").GetInt32());
        var t = a.GetProperty("EventSearchResult").GetProperty("Targets")[0];
        Assert.EndsWith("+07:00", t.GetProperty("alarmStartTime").GetString());
        Assert.StartsWith($"http://127.0.0.1:{m.Port}/picture/Streaming/tracks/103/?name=ch00001_", t.GetProperty("pictureUrl").GetString());

        // "Z" is real UTC on this endpoint: the same span written in UTC finds the same detections.
        var z = await Ask("C", 0, Utc(from), Utc(to));
        Assert.Equal(total, z.GetProperty("EventSearchResult").GetProperty("totalMatches").GetInt32());

        // A page holds at most 100; type "record" finds nothing on a continuously recording site; channels as text are refused.
        Assert.Equal(100, (await Ask("D", 0, Local(from), Local(to), max: 500)).GetProperty("EventSearchResult").GetProperty("numOfMatches").GetInt32());
        var record = await Ask("E", 0, Local(from), Local(to), type: "record");
        Assert.Equal("NO MATCH", Status(record));
        Assert.False(record.GetProperty("EventSearchResult").TryGetProperty("Targets", out _));
        var text = await Ask("F", 0, Local(from), Local(to), channels: "[\"1\"]");
        Assert.Equal("invalidContent", text.GetProperty("subStatusCode").GetString());

        // Pictures need the login too.
        using var plain = new HttpClient { BaseAddress = http.BaseAddress };
        Assert.Equal(HttpStatusCode.Unauthorized, (await plain.GetAsync("/picture/Streaming/tracks/103/?name=x")).StatusCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Probe_reports_people_and_a_recorder_without_them_still_passes(bool people)
    {
        await using var m = await RunningMock.StartAsync(o => { o.People = people; o.Audio = false; });
        var runner = new ProbeRunner(new HikNvrClientFactory(), new FramesFactory(), new PsMedia())
        {
            Timeouts = new ProbeTimeouts { Watch = TimeSpan.FromMilliseconds(30), Poll = TimeSpan.FromMilliseconds(2), MethodByteCap = 2L * 1024 * 1024 },
        };

        var report = await runner.RunAsync(new NvrConfig { Host = "127.0.0.1", HttpPort = m.Port, User = "admin" }, Password, _temp, null, default);

        output.WriteLine(report.Text);
        Assert.True(report.Passed, report.Text);
        Assert.DoesNotContain(Password, report.Text);
        var line = report.Text.Split('\n').Single(l => l.Contains("Tìm lúc có người") && l.Contains('|'));
        if (people)
        {
            Assert.Contains("[PASS]", line);
            Assert.Matches(@"có: \d+ lần trong 24 giờ qua, kênh D(1|3|8|14)\b", line);
            Assert.Contains($"{PeopleApi.Jpeg.Length} B JPEG", line);
        }
        else
        {
            Assert.Contains("[FAIL]", line);
            Assert.Contains("Đầu ghi này không cho tìm lúc có người.", line);
            Assert.Contains("đầu ghi không hỗ trợ", report.Text);
        }
    }

    /// <summary>Video sources that play at once (RTSP is MediaMTX's job, not tested here).</summary>
    private sealed class FramesFactory : IVideoSourceFactory
    {
        public IVideoSource Create(VideoSourceOptions options) => new Frames();

        private sealed class Frames : IVideoSource
        {
            private long _n;
            public VideoState State { get; private set; }
            public string? Error => null;
            public event Action<IVideoSource>? StateChanged;
            public VideoStats Stats => new("H.265", 1280, 720, 15, 1000, false);
            public TimeSpan Position => TimeSpan.Zero;
            public void Start() { State = VideoState.Playing; StateChanged?.Invoke(this); }
            public void SetTargetSize(int width, int height) { }
            public void Pause() { }
            public void Resume() { }
            public bool TryCopyLatest(ref long lastSequence, FrameCopier copier) { lastSequence = ++_n; return true; }
            public Task<FrameSnapshot> CaptureAsync(CancellationToken ct) => throw new NotSupportedException();
            public void Dispose() { }
        }
    }
}
