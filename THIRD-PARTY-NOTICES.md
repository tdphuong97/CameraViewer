# Thành phần bên thứ ba

Camera Viewer dùng các thành phần sau. Cảm ơn các tác giả.

| Thành phần | Dùng để | Giấy phép |
|---|---|---|
| FFmpeg 9.0 (thư viện avcodec, avformat, avutil, swscale, swresample, avfilter, avdevice) | Giải mã video, đọc luồng RTSP, chuyển video sang MP4 | GNU LGPL 2.1 trở lên. Kèm theo dưới dạng các file DLL **không sửa đổi**, lấy từ bản dựng "lgpl-shared" của BtbN/FFmpeg-Builds. Các DLL nằm riêng trong thư mục `ffmpeg\` cạnh `CameraViewer.exe` và được nạp lúc chạy, nên người dùng có thể thay bằng bản FFmpeg tương thích khác. Văn bản giấy phép: `ffmpeg\LICENSE.txt` |
| FFmpeg.AutoGen 9.0.1.1 | Gọi thư viện FFmpeg từ .NET | MIT (Copyright (c) 2025 Ruslan Balanukhin; theo file LICENSE.txt trong gói NuGet, từ bản 9.0 dự án chuyển từ LGPL sang MIT) |
| .NET (runtime, thư viện chuẩn, WPF) | Nền tảng chạy phần mềm | MIT |
| System.Security.Cryptography.ProtectedData | Bảo vệ mật khẩu đầu ghi bằng Windows DPAPI | MIT |
| Inno Setup | Tạo bộ cài (chỉ dùng khi xây dựng bộ cài) | Giấy phép Inno Setup (miễn phí, xem jrsoftware.org/files/is/license.txt) |
| Bản dịch tiếng Việt của Inno Setup (`build/Vietnamese.isl`), do memecoder thực hiện | Chữ tiếng Việt trong bộ cài | Bản dịch cộng đồng (không chính thức) của Inno Setup, dùng theo điều khoản phân phối của Inno Setup |

## Mã nguồn FFmpeg

Theo LGPL, mã nguồn của FFmpeg đi kèm phải lấy được tự do:

- Trang tải chính thức: https://ffmpeg.org/download.html
- Kho mã nguồn: https://github.com/FFmpeg/FFmpeg (nhánh/thẻ `n9.0`)
- Kịch bản dựng ra đúng các DLL được kèm theo: https://github.com/BtbN/FFmpeg-Builds

Camera Viewer không sửa đổi FFmpeg.

## Chỉ dùng khi phát triển, không kèm trong bộ cài

| Thành phần | Dùng để | Giấy phép |
|---|---|---|
| MediaMTX | Máy chủ RTSP của đầu ghi giả lập (`tools/mock`) | MIT |
| FFmpeg bản GPL tĩnh (libx264, libx265, drawtext) | Chỉ để tạo luồng video thử H.264/H.265 cho đầu ghi giả lập | GNU GPL (v2 trở lên, do libx264/libx265) |

Hai thành phần này được tải riêng trên máy phát triển bằng `build\fetch-dev-tools.ps1`, không nằm trong kho mã và không bao giờ được kèm trong bộ cài.

Văn bản đầy đủ của các giấy phép có trong gói của từng thành phần hoặc tại trang chủ của họ.

Camera Viewer được phát hành theo giấy phép MIT (xem `LICENSE`).
