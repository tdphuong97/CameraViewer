using Cv.Core.Nvr;

namespace Cv.Core.Hik;

/// <summary>An ISAPI error answer (ResponseStatus or HTTP error); the codes are kept for internal decisions.</summary>
internal sealed class NvrStatusException(string message, int http, IsapiStatus? status)
    : NvrException(message)
{
    public int Http { get; } = http;
    public IsapiStatus? Status { get; } = status;

    public bool NotSupported =>
        Http is 404 or 405 or 501
        || string.Equals(Status?.SubStatusCode, "notSupport", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Vietnamese messages for recorder errors, written for a non-technical user.</summary>
internal static class HikErrors
{
    public const string NothingRecorded = "Không có ghi hình trong khoảng thời gian này.";
    public const string Unsupported = "Đầu ghi không cho tải video trực tiếp theo khoảng thời gian.";
    public const string LostDuringDownload = "Mất kết nối đầu ghi khi đang tải video. Kiểm tra mạng rồi bấm Thử lại.";
    public const string Unreadable = "Đầu ghi trả về dữ liệu không đọc được.";

    public static string Unreachable(string host) =>
        $"Không kết nối được đầu ghi {host}. Kiểm tra mạng và địa chỉ IP.";

    /// <summary>
    /// 401 after the digest login: locked (lockStatus=lock or unlockTime &gt; 0, in seconds) or a wrong password
    /// (retryLoginTime = attempts left before the lock).
    /// </summary>
    public static NvrAuthException Auth(string? body)
    {
        var s = HikXml.ParseLoginStatus(HikXml.TryParse(body));
        var locked = string.Equals(s.LockStatus, "lock", StringComparison.OrdinalIgnoreCase) || s.UnlockSeconds > 0;
        if (locked)
        {
            // 30 minutes is Hikvision's default lock time, used when the device does not say.
            var minutes = s.UnlockSeconds > 0 ? (int)Math.Ceiling(s.UnlockSeconds.Value / 60.0) : 30;
            return new NvrAuthException(
                $"Đầu ghi đang tạm khóa đăng nhập vì nhập sai mật khẩu nhiều lần. Thử lại sau khoảng {minutes} phút.");
        }
        var msg = "Sai tài khoản hoặc mật khẩu đầu ghi. Kiểm tra lại trong Cài đặt.";
        if (s.RetriesLeft > 0) msg += $" Còn {s.RetriesLeft} lần thử trước khi đầu ghi tạm khóa.";
        return new NvrAuthException(msg);
    }

    /// <summary>Error for a non-401 answer, or null when it is a success (2xx and no failing ResponseStatus).</summary>
    public static NvrStatusException? FromResponse(int http, string? body)
    {
        var status = HikXml.ParseResponseStatus(HikXml.TryParse(body));
        var failed = http is < 200 or >= 300 || (status?.StatusCode is { } code && code != 1);
        if (!failed) return null;
        return new NvrStatusException(Message(http, status), http, status);
    }

    public static string Message(int http, IsapiStatus? status)
    {
        switch (status?.SubStatusCode?.ToLowerInvariant())
        {
            case "notsupport":
            case "methodnotallowed":
                return "Đầu ghi không hỗ trợ chức năng này.";
            case "lowprivilege":
            case "nopermission":
                return "Tài khoản này không đủ quyền trên đầu ghi. Hãy dùng tài khoản quản trị.";
            case "devicebusy":
            case "busy":
            case "nomemory":
                return "Đầu ghi đang bận. Thử lại sau ít phút.";
            case "badxmlformat":
            case "badxmlcontent":
            case "badparameters":
                return "Đầu ghi không hiểu yêu cầu của phần mềm.";
        }
        switch (status?.StatusCode)
        {
            case 2: return "Đầu ghi đang bận. Thử lại sau ít phút.";
            case 3: return "Đầu ghi báo lỗi thiết bị. Kiểm tra ổ cứng và tình trạng đầu ghi.";
            case 4: return "Đầu ghi từ chối thao tác này.";
            case 5:
            case 6: return "Đầu ghi không hiểu yêu cầu của phần mềm.";
            case 7: return "Đầu ghi cần khởi động lại.";
        }
        return http switch
        {
            403 => "Tài khoản này không đủ quyền trên đầu ghi. Hãy dùng tài khoản quản trị.",
            404 or 405 or 501 => "Đầu ghi không hỗ trợ chức năng này.",
            >= 500 => "Đầu ghi đang bận hoặc gặp lỗi tạm thời. Thử lại sau ít phút.",
            _ when status?.StatusCode is { } c => $"Đầu ghi báo lỗi (mã {c}).",
            _ => $"Đầu ghi báo lỗi (mã HTTP {http}).",
        };
    }

    /// <summary>Short raw text for the log (never shown to the user).</summary>
    public static string Trim(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Replace('\r', ' ').Replace('\n', ' ');
        return s.Length <= 300 ? s : s[..300];
    }
}
