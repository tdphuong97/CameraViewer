using System.Net;
using System.Text.Json;
using Cv.Core.Hik;
using Cv.Core.Nvr;
using Cv.Core.Tests.Fakes;

namespace Cv.Core.Tests.Hik;

/// <summary>People detections (eventRecordSearch, JSON) against a fake that answers like DS-7732NI-M4 V5.04.081.</summary>
public class DetectionsTests
{
    private static readonly DateTime Day = new(2026, 11, 7); // the fake clock is 2026-11-07 19:45:12 +07:00
    private const string SearchPath = "/ISAPI/ContentMgmt/eventRecordSearch";
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 0xFF, 0xD9];

    private static string S(JsonElement e, string name) => e.GetProperty(name).GetString()!;

    [Fact]
    public async Task Request_is_the_one_the_real_recorder_accepts()
    {
        var f = new FakeRecorder().AddPeople(14, Day.AddHours(8).AddMinutes(2).AddSeconds(55));
        using var c = f.Client();

        var events = await c.SearchPeopleAsync(14, Day.AddHours(8), Day.AddHours(8).AddMinutes(30), default);

        var req = Assert.Single(f.Handler.Requests, r => r.PathAndQuery.StartsWith(SearchPath, StringComparison.Ordinal));
        Assert.Equal(SearchPath + "?format=json", req.PathAndQuery);
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("application/json", req.ContentType);
        var d = Assert.Single(f.PeopleRequests);
        Assert.Matches("^[0-9A-F]{8}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{12}$", S(d, "searchID"));
        Assert.Equal(0, d.GetProperty("searchResultPosition").GetInt32());
        Assert.Equal(100, d.GetProperty("maxResults").GetInt32());
        var span = Assert.Single(d.GetProperty("timeSpanList").EnumerateArray());
        Assert.Equal("2026-11-07T08:00:00+07:00", S(span, "startTime"));
        Assert.Equal("2026-11-07T08:30:00+07:00", S(span, "endTime"));
        Assert.Equal("all", S(d, "type"));
        var ch = Assert.Single(d.GetProperty("channels").EnumerateArray());
        Assert.Equal(JsonValueKind.Number, ch.ValueKind);
        Assert.Equal(14, ch.GetInt32());
        Assert.Equal("peopleDetection", S(d, "eventType"));

        var e = Assert.Single(events);
        Assert.Equal(new DetectionEvent(14, Day.AddHours(8).AddMinutes(2).AddSeconds(55),
            "/picture/Streaming/tracks/1403/?name=ch00014_20261107080255&size=278614"), e);
        Assert.Equal(DateTimeKind.Unspecified, e.Time.Kind);
    }

    [Fact]
    public async Task Pages_follow_the_recorder_cursor_with_one_search_id()
    {
        var f = new FakeRecorder().AddPeople(14, Day.AddHours(8), 250);
        using var c = f.Client();

        var events = await c.SearchPeopleAsync(14, Day.AddHours(8), Day.AddHours(9), default);

        Assert.Equal(250, events.Count);
        Assert.Equal(250, events.Select(e => e.Time).Distinct().Count());
        Assert.Equal(events.OrderBy(e => e.Time), events);
        Assert.Equal([0, 100, 200], f.PeopleRequests.Select(r => r.GetProperty("searchResultPosition").GetInt32()));
        Assert.Single(f.PeopleRequests.Select(r => S(r, "searchID")).Distinct());
    }

    [Fact]
    public async Task Every_search_starts_with_a_new_search_id()
    {
        // Reusing an ID would make the recorder continue its old cursor and miss the first pages.
        var f = new FakeRecorder().AddPeople(14, Day.AddHours(8), 150);
        using var c = f.Client();

        var a = await c.SearchPeopleAsync(14, Day.AddHours(8), Day.AddHours(9), default);
        var b = await c.SearchPeopleAsync(14, Day.AddHours(8), Day.AddHours(9), default);

        Assert.Equal(a, b);
        Assert.Equal(150, b.Count);
        Assert.Equal(2, f.PeopleRequests.Select(r => S(r, "searchID")).Distinct().Count());
    }

    [Fact]
    public async Task Times_in_any_notation_become_the_device_wall_clock()
    {
        var f = new FakeRecorder();
        f.People.Add(new(14, "2026-11-07T08:00:10+07:00", null));
        f.People.Add(new(14, "2026-11-07T01:00:20Z", null)); // real UTC on this endpoint
        f.People.Add(new(14, "2026-11-07T01:00:30+00:00", null));
        using var c = f.Client(TimeMode.LocalWithZ); // the recording search mode does not matter here

        var events = await c.SearchPeopleAsync(14, Day.AddHours(8), Day.AddHours(8).AddMinutes(1), default);

        Assert.Equal([Day.AddHours(8).AddSeconds(10), Day.AddHours(8).AddSeconds(20), Day.AddHours(8).AddSeconds(30)], events.Select(e => e.Time));
        Assert.All(events, e => Assert.Equal(DateTimeKind.Unspecified, e.Time.Kind));
        Assert.All(events, e => Assert.Null(e.PictureUri));
    }

    [Fact]
    public async Task Range_is_half_open_and_other_channels_and_events_are_dropped()
    {
        var f = new FakeRecorder { PeopleIgnoreFilters = true }
            .AddPeople(14, Day.AddHours(8))
            .AddPeople(14, Day.AddHours(8).AddMinutes(10)) // exactly the end
            .AddPeople(13, Day.AddHours(8).AddMinutes(5));
        f.People.Add(new(14, "2026-11-07T08:06:00+07:00", null, "vehicleDetection"));
        f.People.Add(new(14, "garbage", null));
        using var c = f.Client();

        var events = await c.SearchPeopleAsync(14, Day.AddHours(8), Day.AddHours(8).AddMinutes(10), default);

        Assert.Equal([Day.AddHours(8)], events.Select(e => e.Time));
    }

    [Fact]
    public async Task No_match_is_an_empty_list_after_one_request()
    {
        var f = new FakeRecorder().AddPeople(13, Day.AddHours(8));
        using var c = f.Client();

        Assert.Empty(await c.SearchPeopleAsync(14, Day, Day.AddDays(1), default));
        Assert.Single(f.PeopleRequests);
    }

    [Fact]
    public async Task Paging_stops_at_the_safety_cap()
    {
        var f = new FakeRecorder { PeopleEndlessMore = true };
        using var c = f.Client();

        var events = await c.SearchPeopleAsync(1, Day, Day.AddDays(1), default);

        Assert.Equal(HikNvrClient.PeopleMaxPages, f.PeopleRequests.Count);
        Assert.Single(events); // identical events collapse
    }

    [Theory]
    [InlineData(403, """{"requestURL":"/ISAPI/ContentMgmt/eventRecordSearch?format=json","statusCode":4,"statusString":"Invalid Operation","subStatusCode":"notSupport","errorCode":1073741825,"errorMsg":"notSupport"}""")]
    [InlineData(200, """{"statusCode":4,"statusString":"Invalid Operation","subStatusCode":"notSupport"}""")]
    [InlineData(404, "<html>Not Found</html>")]
    [InlineData(403, "")]
    [InlineData(200, """<ResponseStatus version="2.0"><statusCode>4</statusCode><statusString>Invalid Operation</statusString><subStatusCode>notSupport</subStatusCode></ResponseStatus>""")]
    public async Task Not_supported_answers_say_so_and_are_not_asked_again(int http, string body)
    {
        var f = new FakeRecorder { PeopleAnswer = _ => FakeRecorder.Json(body, (HttpStatusCode)http) };
        using var c = f.Client();

        var e = await Assert.ThrowsAsync<NvrDetectionUnsupportedException>(() => c.SearchPeopleAsync(14, Day, Day.AddDays(1), default));
        Assert.Equal("Đầu ghi này không cho tìm lúc có người.", e.Message);
        await Assert.ThrowsAsync<NvrDetectionUnsupportedException>(() => c.SearchPeopleAsync(13, Day, Day.AddDays(1), default));
        Assert.Single(f.PeopleRequests);
    }

    [Fact]
    public async Task Invalid_content_is_a_recorder_error_not_unsupported()
    {
        var f = new FakeRecorder
        {
            PeopleAnswer = _ => FakeRecorder.Json("""{"statusCode":4,"statusString":"Invalid Operation","subStatusCode":"invalidContent"}"""),
        };
        using var c = f.Client();

        var e = await Assert.ThrowsAnyAsync<NvrException>(() => c.SearchPeopleAsync(14, Day, Day.AddDays(1), default));
        Assert.IsNotType<NvrDetectionUnsupportedException>(e);
        Assert.Equal("Đầu ghi từ chối thao tác này.", e.Message);
    }

    [Fact]
    public async Task Low_privilege_is_a_permission_error_not_unsupported()
    {
        var f = new FakeRecorder
        {
            PeopleAnswer = _ => FakeRecorder.Json("""{"statusCode":4,"statusString":"Invalid Operation","subStatusCode":"lowPrivilege"}""", HttpStatusCode.Forbidden),
        };
        using var c = f.Client();

        var e = await Assert.ThrowsAnyAsync<NvrException>(() => c.SearchPeopleAsync(14, Day, Day.AddDays(1), default));
        Assert.IsNotType<NvrDetectionUnsupportedException>(e);
        Assert.Contains("không đủ quyền", e.Message);
    }

    [Fact]
    public async Task Unreadable_answer_is_a_recorder_error()
    {
        var f = new FakeRecorder { PeopleAnswer = _ => FakeRecorder.Json("""{"Something":{}}""") };
        using var c = f.Client();

        var e = await Assert.ThrowsAnyAsync<NvrException>(() => c.SearchPeopleAsync(14, Day, Day.AddDays(1), default));
        Assert.Equal(HikErrors.Unreadable, e.Message);
    }

    [Fact]
    public async Task Digest_login_carries_the_json_body()
    {
        var f = new FakeRecorder().AddPeople(14, Day.AddHours(8), 3);
        f.Handler.RequireDigest = true;
        using var c = f.Client();

        Assert.Equal(3, (await c.SearchPeopleAsync(14, Day, Day.AddDays(1), default)).Count);
        Assert.All(f.PeopleRequests, r => Assert.Equal("peopleDetection", S(r, "eventType")));
    }

    [Fact]
    public async Task Locked_account_in_a_json_401_says_for_how_long()
    {
        var f = new FakeRecorder();
        using var c = f.Client(password: "wrong");
        await c.GetClockAsync(default); // the device offset first: this test is about the JSON search
        f.Handler.RequireDigest = true;
        f.Handler.FailedLoginBody = """{"statusCode":4,"subStatusCode":"userLocked","lockStatus":"lock","unlockTime":1200,"retryLoginTime":0}""";

        var e = await Assert.ThrowsAsync<NvrAuthException>(() => c.SearchPeopleAsync(14, Day, Day.AddDays(1), default));
        Assert.Contains("20 phút", e.Message);
    }

    [Fact]
    public async Task Network_loss_and_timeouts_are_unreachable()
    {
        var f = new FakeRecorder();
        using var c = f.Client();
        await c.GetClockAsync(default); // offset known: the search itself meets the loss

        f.Handler.Throw = new HttpRequestException("connection reset");
        await Assert.ThrowsAsync<NvrUnreachableException>(() => c.SearchPeopleAsync(14, Day, Day.AddDays(1), default));
        await Assert.ThrowsAsync<NvrUnreachableException>(() => c.GetPictureAsync("/picture/Streaming/tracks/1403/?name=a", default));

        f.Handler.Throw = null;
        f.Handler.Delay = TimeSpan.FromSeconds(5);
        c.CallTimeout = TimeSpan.FromMilliseconds(100);
        await Assert.ThrowsAsync<NvrUnreachableException>(() => c.SearchPeopleAsync(14, Day, Day.AddDays(1), default));
    }

    [Fact]
    public async Task Caller_cancellation_is_not_a_lost_recorder()
    {
        var f = new FakeRecorder();
        using var c = f.Client();
        await c.GetClockAsync(default);
        f.Handler.Delay = TimeSpan.FromSeconds(5);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => c.SearchPeopleAsync(14, Day, Day.AddDays(1), cts.Token));
    }

    [Fact]
    public async Task Overview_is_one_request_over_every_channel()
    {
        var f = new FakeRecorder().AddPeople(1, Day.AddHours(8), 150).AddPeople(3, Day.AddHours(9), 20);
        using var c = f.Client();

        var o = await c.PeopleOverviewAsync([1, 2, 3], Day, Day.AddDays(1), default);

        var d = Assert.Single(f.PeopleRequests);
        Assert.Equal([1, 2, 3], d.GetProperty("channels").EnumerateArray().Select(x => x.GetInt32()));
        Assert.Equal(100, o.Events.Count);
        Assert.Equal(170, o.Total);
        Assert.True(o.More);
        Assert.Equal(o.Events.OrderBy(e => e.Time), o.Events);
    }

    [Fact]
    public async Task Overview_of_a_quiet_recorder_is_empty()
    {
        var f = new FakeRecorder();
        using var c = f.Client();

        var o = await c.PeopleOverviewAsync([1, 2, 3], Day, Day.AddDays(1), default);

        Assert.Empty(o.Events);
        Assert.Equal(0, o.Total);
        Assert.False(o.More);
    }

    // ---- Picture ----

    [Theory]
    [InlineData("/picture/Streaming/tracks/1403/?name=ch00014_02&size=278614")]
    [InlineData("http://10.9.0.254:80/picture/Streaming/tracks/1403/?name=ch00014_02&size=278614")]
    [InlineData("//10.9.0.254/picture/Streaming/tracks/1403/?name=ch00014_02&size=278614")]
    [InlineData("picture/Streaming/tracks/1403/?name=ch00014_02&size=278614")]
    public async Task Picture_comes_from_the_configured_recorder(string uri)
    {
        var f = new FakeRecorder();
        f.Handler.RequireDigest = true;
        f.Handler.On(HttpMethod.Get, "/picture/Streaming/tracks/1403/", _ => Image(Jpeg));
        using var c = f.Client();

        Assert.Equal(Jpeg, await c.GetPictureAsync(uri, default));

        var seen = f.Handler.Requests.Last();
        Assert.Equal("/picture/Streaming/tracks/1403/?name=ch00014_02&size=278614", seen.PathAndQuery);
        Assert.Equal("192.168.1.64", seen.Host);
        Assert.NotNull(seen.Authorization);
    }

    [Fact]
    public async Task Html_or_missing_picture_is_an_error()
    {
        var f = new FakeRecorder();
        f.Handler.On(HttpMethod.Get, "/picture/Streaming/tracks/1403/", s => s.PathAndQuery.Contains("gone")
            ? FakeIsapiHandler.Text("<html>404</html>", HttpStatusCode.NotFound, "text/html")
            : FakeIsapiHandler.Text("<!DOCTYPE html><html><head><title>Document Error</title></head><body>wrong name</body></html>", HttpStatusCode.OK, "text/html"));
        using var c = f.Client();

        var a = await Assert.ThrowsAnyAsync<NvrException>(() => c.GetPictureAsync("/picture/Streaming/tracks/1403/?name=wrong", default));
        var b = await Assert.ThrowsAnyAsync<NvrException>(() => c.GetPictureAsync("/picture/Streaming/tracks/1403/?name=gone", default));
        Assert.Equal(HikNvrClient.PictureMissing, a.Message);
        Assert.Equal(HikNvrClient.PictureMissing, b.Message);
    }

    [Fact]
    public async Task Oversized_picture_is_refused()
    {
        var f = new FakeRecorder();
        var big = new byte[5000];
        Jpeg.CopyTo(big, 0);
        f.Handler.On(HttpMethod.Get, "/picture/Streaming/tracks/1403/", _ => Image(big));
        using var c = f.Client();
        c.PictureMaxBytes = 4096;

        var e = await Assert.ThrowsAnyAsync<NvrException>(() => c.GetPictureAsync("/picture/Streaming/tracks/1403/?name=a", default));
        Assert.Equal(HikNvrClient.PictureTooBig, e.Message);
    }

    [Fact]
    public async Task Wrong_password_on_a_picture_is_an_auth_error()
    {
        var f = new FakeRecorder();
        f.Handler.RequireDigest = true;
        f.Handler.FailedLoginBody = "<userCheck><statusValue>401</statusValue><lockStatus>unlock</lockStatus><retryLoginTime>3</retryLoginTime></userCheck>";
        f.Handler.On(HttpMethod.Get, "/picture/Streaming/tracks/1403/", _ => Image(Jpeg));
        using var c = f.Client(password: "wrong");

        var e = await Assert.ThrowsAsync<NvrAuthException>(() => c.GetPictureAsync("/picture/Streaming/tracks/1403/?name=a", default));
        Assert.Contains("Còn 3 lần thử", e.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("http://10.9.0.254")]
    public async Task Picture_needs_a_path(string uri)
    {
        using var c = new FakeRecorder().Client();
        await Assert.ThrowsAsync<ArgumentException>(() => c.GetPictureAsync(uri, default));
    }

    [Theory]
    [InlineData("http://10.9.0.254:80/picture/Streaming/tracks/1403/?name=a&size=1", "/picture/Streaming/tracks/1403/?name=a&size=1")]
    [InlineData("https://[fe80::1]:443/picture/x?y=http://z", "/picture/x?y=http://z")]
    [InlineData("/picture/x?y=http://z", "/picture/x?y=http://z")]
    [InlineData("////evil.example/x", "/evil.example/x")]
    [InlineData("/\\evil.example/x", "/x")]
    [InlineData("  ", null)]
    [InlineData(null, null)]
    public void Picture_path_keeps_only_path_and_query(string? url, string? expected) =>
        Assert.Equal(expected, HikNvrClient.PicturePath(url));

    private static HttpResponseMessage Image(byte[] bytes)
    {
        var r = FakeIsapiHandler.Bytes(bytes);
        r.Content.Headers.ContentType = new("image/jpeg");
        return r;
    }
}
