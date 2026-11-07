using System.Net;
using System.Net.Http;
using Cv.Core.Hik;
using Cv.Core.Nvr;
using Cv.Core.Tests.Fakes;

namespace Cv.Core.Tests.Hik;

public class HikNvrClientTests
{
    [Fact]
    public async Task Digest_challenge_is_answered()
    {
        var f = new FakeRecorder();
        f.Handler.RequireDigest = true;
        using var c = f.Client();
        var info = await c.GetDeviceInfoAsync(default);
        Assert.Equal("DS-7732NI-M4", info.Model);
        var r = f.Handler.Requests;
        Assert.Equal(2, r.Count);
        Assert.Null(r[0].Authorization);
        var auth = r[1].Authorization!;
        Assert.Contains("qop=auth", auth);
        var ha1 = FakeIsapiHandler.Md5("admin:DS-Fake:secret");
        var ha2 = FakeIsapiHandler.Md5("GET:" + FakeIsapiHandler.Param(auth, "uri"));
        var expected = FakeIsapiHandler.Md5($"{ha1}:abc:{FakeIsapiHandler.Param(auth, "nc")}:{FakeIsapiHandler.Param(auth, "cnonce")}:auth:{ha2}");
        Assert.Equal(expected, FakeIsapiHandler.Param(auth, "response"));
    }

    [Fact]
    public async Task Wrong_password_is_one_failed_login_and_a_vietnamese_message()
    {
        var f = new FakeRecorder();
        f.Handler.RequireDigest = true;
        f.Handler.FailedLoginBody = Samples.WrongPassword;
        using var c = f.Client(password: "wrong");
        var e = await Assert.ThrowsAsync<NvrAuthException>(() => c.GetDeviceInfoAsync(default));
        Assert.Equal("Sai tài khoản hoặc mật khẩu đầu ghi. Kiểm tra lại trong Cài đặt. Còn 4 lần thử trước khi đầu ghi tạm khóa.", e.Message);
        Assert.Equal(2, f.Handler.Requests.Count);
    }

    [Fact]
    public async Task Rfc_style_wrong_password_says_wrong_password()
    {
        var f = new FakeRecorder();
        f.Handler.RequireDigest = true;
        using var c = f.Client(password: "wrong");
        var e = await Assert.ThrowsAsync<NvrAuthException>(() => c.GetDeviceInfoAsync(default));
        Assert.StartsWith("Sai tài khoản hoặc mật khẩu đầu ghi.", e.Message);
    }

    [Fact]
    public async Task Locked_recorder_says_how_many_minutes()
    {
        var f = new FakeRecorder();
        f.Handler.RequireDigest = true;
        f.Handler.FailedLoginBody = Samples.Locked;
        using var c = f.Client(password: "wrong");
        var e = await Assert.ThrowsAsync<NvrAuthException>(() => c.GetDeviceInfoAsync(default));
        Assert.Equal("Đầu ghi đang tạm khóa đăng nhập vì nhập sai mật khẩu nhiều lần. Thử lại sau khoảng 30 phút.", e.Message);
    }

    [Fact]
    public void Lock_minutes_round_up_and_default_to_30()
    {
        Assert.Contains("khoảng 2 phút", HikErrors.Auth("<userCheck><lockStatus>lock</lockStatus><unlockTime>61</unlockTime></userCheck>").Message);
        Assert.Contains("khoảng 30 phút", HikErrors.Auth("<userCheck><lockStatus>lock</lockStatus></userCheck>").Message);
        Assert.Contains("khoảng 1 phút", HikErrors.Auth("<userCheck><lockStatus>unlock</lockStatus><unlockTime>20</unlockTime></userCheck>").Message);
    }

    [Fact]
    public async Task Network_error_is_unreachable_with_the_host()
    {
        var f = new FakeRecorder();
        f.Handler.Throw = new HttpRequestException("No route to host");
        using var c = f.Client();
        var e = await Assert.ThrowsAsync<NvrUnreachableException>(() => c.GetDeviceInfoAsync(default));
        Assert.Equal("Không kết nối được đầu ghi 192.168.1.64. Kiểm tra mạng và địa chỉ IP.", e.Message);
    }

    [Fact]
    public async Task Timeout_is_unreachable_but_caller_cancellation_is_not()
    {
        var f = new FakeRecorder();
        f.Handler.Delay = TimeSpan.FromSeconds(5);
        using var c = f.Client();
        c.CallTimeout = TimeSpan.FromMilliseconds(100);
        await Assert.ThrowsAsync<NvrUnreachableException>(() => c.GetDeviceInfoAsync(default));

        using var cts = new CancellationTokenSource(100);
        c.CallTimeout = TimeSpan.FromSeconds(10);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => c.GetDeviceInfoAsync(cts.Token));
    }

    [Fact]
    public async Task Response_status_errors_become_readable_messages()
    {
        var f = new FakeRecorder();
        f.Handler.Xml("/ISAPI/System/deviceInfo", Samples.NotSupported);
        using var c = f.Client();
        var e = await Assert.ThrowsAsync<NvrStatusException>(() => c.GetDeviceInfoAsync(default));
        Assert.Equal("Đầu ghi không hỗ trợ chức năng này.", e.Message);
        Assert.True(e.NotSupported);

        f.Handler.Xml("/ISAPI/System/deviceInfo", "<ResponseStatus><statusCode>2</statusCode></ResponseStatus>", HttpStatusCode.ServiceUnavailable);
        Assert.Equal("Đầu ghi đang bận. Thử lại sau ít phút.", (await Assert.ThrowsAnyAsync<NvrException>(() => c.GetDeviceInfoAsync(default))).Message);

        f.Handler.Xml("/ISAPI/System/deviceInfo", "", HttpStatusCode.Forbidden);
        Assert.Contains("không đủ quyền", (await Assert.ThrowsAnyAsync<NvrException>(() => c.GetDeviceInfoAsync(default))).Message);

        f.Handler.Xml("/ISAPI/System/deviceInfo", "garbage");
        Assert.Equal("Đầu ghi trả về dữ liệu không đọc được.", (await Assert.ThrowsAnyAsync<NvrException>(() => c.GetDeviceInfoAsync(default))).Message);
    }

    [Fact]
    public async Task A_response_status_ok_is_not_an_error()
    {
        Assert.Null(HikErrors.FromResponse(200, Samples.Ok));
        Assert.NotNull(HikErrors.FromResponse(200, Samples.NotSupported));
        var f = new FakeRecorder();
        using var c = f.Client();
        Assert.Equal("DS-7732NI-M4", (await c.GetDeviceInfoAsync(default)).Model);
    }

    [Fact]
    public async Task No_host_means_unreachable_without_any_request()
    {
        var h = new FakeIsapiHandler();
        using var c = new HikNvrClient(new NvrConfig { Host = "  " }, "x", h);
        var e = await Assert.ThrowsAsync<NvrUnreachableException>(() => c.GetDeviceInfoAsync(default));
        Assert.Contains("Chưa nhập đúng địa chỉ IP", e.Message);
    }

    [Fact]
    public async Task Clock_prefers_the_offset_in_local_time()
    {
        using var c = new FakeRecorder().Client();
        var clock = await c.GetClockAsync(default);
        Assert.Equal(new DateTime(2026, 11, 7, 19, 45, 12), clock.LocalNow);
        Assert.Equal(DateTimeKind.Unspecified, clock.LocalNow.Kind);
        Assert.Equal(TimeSpan.FromHours(7), clock.UtcOffset);
    }

    [Fact]
    public async Task Clock_without_offset_uses_the_posix_zone()
    {
        using var c = new FakeRecorder(Samples.TimeAt("2026-11-07T19:45:12", "CST-7:00:00")).Client();
        var clock = await c.GetClockAsync(default);
        Assert.Equal(new DateTime(2026, 11, 7, 19, 45, 12), clock.LocalNow);
        Assert.Equal(TimeSpan.FromHours(7), clock.UtcOffset);
    }

    [Fact]
    public async Task Clock_in_utc_is_converted_with_the_zone()
    {
        using var c = new FakeRecorder(Samples.TimeAt("2026-11-07T12:45:12Z", "CST-7:00:00")).Client();
        var clock = await c.GetClockAsync(default);
        Assert.Equal(new DateTime(2026, 11, 7, 19, 45, 12), clock.LocalNow);
        Assert.Equal(TimeSpan.FromHours(7), clock.UtcOffset);
    }

    [Fact]
    public async Task Channels_merge_status_and_fall_back_for_names_sorted()
    {
        var f = new FakeRecorder();
        using var c = f.Client();
        var ch = await c.GetChannelsAsync(default);
        Assert.Equal([1, 2, 3], ch.Select(x => x.No));
        Assert.Equal(new ChannelInfo(1, "Cong chinh", true, "192.168.254.2", "DS-2CD1043G2-LIU"), ch[0]);
        Assert.Equal("Sanh tang 1", ch[1].Name); // blank name → Streaming/channels channelName
        Assert.False(ch[1].Online);
        Assert.True(ch[2].Online); // chanDetectResult=connect
        Assert.Equal("Ham xe", ch[2].Name);
    }

    [Fact]
    public async Task Channel_name_falls_back_to_camera_number()
    {
        var f = new FakeRecorder();
        f.Handler.Xml("/ISAPI/Streaming/channels", "", HttpStatusCode.NotFound);
        using var c = f.Client();
        var ch = await c.GetChannelsAsync(default);
        Assert.Equal("Camera 02", ch[1].Name);
    }

    [Fact]
    public async Task A_failing_status_call_means_every_channel_online()
    {
        var f = new FakeRecorder();
        f.Handler.Xml("/ISAPI/ContentMgmt/InputProxy/channels/status", Samples.NotSupported, HttpStatusCode.BadRequest);
        using var c = f.Client();
        var ch = await c.GetChannelsAsync(default);
        Assert.All(ch, x => Assert.True(x.Online));
        Assert.Equal(3, ch.Count);
    }

    [Fact]
    public async Task Without_input_proxy_the_streaming_channels_are_used()
    {
        var f = new FakeRecorder();
        f.Handler.Xml("/ISAPI/ContentMgmt/InputProxy/channels", Samples.NotSupported);
        using var c = f.Client();
        var ch = await c.GetChannelsAsync(default);
        Assert.Equal(["Camera 01", "Sanh tang 1", "Ham xe B1"], ch.Select(x => x.Name));
    }

    [Fact]
    public async Task Auth_errors_on_channels_are_not_swallowed()
    {
        var f = new FakeRecorder();
        f.Handler.RequireDigest = true;
        f.Handler.FailedLoginBody = Samples.WrongPassword;
        using var c = f.Client(password: "wrong");
        await Assert.ThrowsAsync<NvrAuthException>(() => c.GetChannelsAsync(default));
    }

    [Fact]
    public void Factory_creates_a_client_for_the_config()
    {
        using var c = new HikNvrClientFactory().Create(FakeRecorder.Config(), "pw");
        Assert.Equal("192.168.1.64", c.Config.Host);
        Assert.Equal("rtsp://admin:pw@192.168.1.64:554/Streaming/Channels/101", c.LiveUrl(1, StreamKind.Main));
    }
}
