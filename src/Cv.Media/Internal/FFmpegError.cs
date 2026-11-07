using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace Cv.Media.Internal;

internal enum Failure { Auth, Forbidden, NotFound, Rejected, ServerError, Network, Data, NoFile, Unsupported, NoVideo, DiskFull, Other }

/// <summary>FFmpeg error codes to log text and to short Vietnamese messages for the user.</summary>
internal static unsafe class FFmpegError
{
    // Windows CRT (mingw/MSVC) errno values and the WinSock values FFmpeg's network code returns.
    private const int ENOENT = 2, EIO = 5, EACCES = 13, ENOSPC = 28, EPIPE = 32;
    private const int ECONNABORTED = 106, ECONNREFUSED = 107, ECONNRESET = 108, EHOSTUNREACH = 110, ENETUNREACH = 118, ETIMEDOUT = 138;
    private const int WSAENETDOWN = 10050, WSAENETUNREACH = 10051, WSAECONNABORTED = 10053, WSAECONNRESET = 10054,
        WSAETIMEDOUT = 10060, WSAECONNREFUSED = 10061, WSAEHOSTDOWN = 10064, WSAEHOSTUNREACH = 10065;

    public static int Averror(int errno) => -errno;

    public static string Describe(int code)
    {
        const int size = 256;
        byte* buf = stackalloc byte[size];
        if (ffmpeg.av_strerror(code, buf, size) < 0) return $"error {code}";
        return $"{Marshal.PtrToStringUTF8((IntPtr)buf)} ({code})";
    }

    public static Failure Classify(int code)
    {
        if (code == ffmpeg.AVERROR_HTTP_UNAUTHORIZED) return Failure.Auth;
        if (code == ffmpeg.AVERROR_HTTP_FORBIDDEN) return Failure.Forbidden;
        if (code == ffmpeg.AVERROR_HTTP_NOT_FOUND || code == ffmpeg.AVERROR_STREAM_NOT_FOUND) return Failure.NotFound;
        if (code == ffmpeg.AVERROR_HTTP_BAD_REQUEST || code == ffmpeg.AVERROR_HTTP_OTHER_4XX || code == ffmpeg.AVERROR_HTTP_TOO_MANY_REQUESTS) return Failure.Rejected;
        if (code == ffmpeg.AVERROR_HTTP_SERVER_ERROR) return Failure.ServerError;
        if (code == ffmpeg.AVERROR_INVALIDDATA) return Failure.Data;
        if (code == ffmpeg.AVERROR_DECODER_NOT_FOUND || code == ffmpeg.AVERROR_DEMUXER_NOT_FOUND || code == ffmpeg.AVERROR_PROTOCOL_NOT_FOUND || code == ffmpeg.AVERROR_PATCHWELCOME)
            return Failure.Unsupported;
        switch (-code)
        {
            case ENOENT: return Failure.NoFile;
            case ENOSPC: return Failure.DiskFull;
            case EIO or EPIPE or ECONNABORTED or ECONNREFUSED or ECONNRESET or EHOSTUNREACH or ENETUNREACH or ETIMEDOUT
                or WSAENETDOWN or WSAENETUNREACH or WSAECONNABORTED or WSAECONNRESET or WSAETIMEDOUT or WSAECONNREFUSED
                or WSAEHOSTDOWN or WSAEHOSTUNREACH:
                return Failure.Network;
            case EACCES: return Failure.NoFile;
        }
        if (code == ffmpeg.AVERROR_EXIT || code == ffmpeg.AVERROR_EOF) return Failure.Network;
        return Failure.Other;
    }

    /// <summary>Wrong credentials, missing stream or refused access do not heal by retrying (and retries can lock the account).</summary>
    public static bool IsPermanent(Failure f) => f is Failure.Auth or Failure.Forbidden or Failure.NotFound or Failure.NoFile or Failure.Unsupported or Failure.NoVideo;

    public static string Message(Failure f) => f switch
    {
        Failure.Auth => "Sai tài khoản hoặc mật khẩu đầu ghi",
        Failure.Forbidden => "Tài khoản không có quyền xem camera này",
        Failure.NotFound => "Không tìm thấy video của camera này",
        Failure.Rejected => "Đầu ghi từ chối yêu cầu xem video (có thể quá nhiều người đang xem)",
        Failure.ServerError => "Đầu ghi đang bận, hãy thử lại sau",
        Failure.Network => "Không kết nối được đầu ghi",
        Failure.Data => "Dữ liệu video bị lỗi",
        Failure.NoFile => "Không tìm thấy tệp video",
        Failure.Unsupported => "Không hỗ trợ định dạng video này",
        Failure.NoVideo => "Không có hình ảnh trong video này",
        Failure.DiskFull => "Ổ đĩa đầy, không lưu được video",
        _ => "Lỗi khi đọc video",
    };

    /// <summary>Exception for IMediaTools callers: Message is Vietnamese and safe to show.</summary>
    public static Exception ToException(int code, string context)
    {
        var f = Classify(code);
        var msg = Message(f);
        var detail = $"{context}: {Describe(code)}";
        return f switch
        {
            Failure.Auth or Failure.Forbidden => new UnauthorizedAccessException(msg, new IOException(detail)),
            Failure.Data or Failure.Unsupported or Failure.NoVideo => new InvalidDataException(msg, new IOException(detail)),
            Failure.NoFile => new FileNotFoundException(msg, new IOException(detail)),
            _ => new IOException(msg, new IOException(detail)),
        };
    }
}
