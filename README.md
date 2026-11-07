# Camera Viewer

Phần mềm Windows nhẹ để **xem camera của một đầu ghi Hikvision**: xem trực tiếp tất cả camera, xem lại theo ngày giờ, cắt một đoạn ghi hình ra file **MP4** mở được ở mọi máy. Làm ra để thay phần mềm quản lý nặng (máy chủ, cơ sở dữ liệu, nhiều RAM) khi chỉ cần xem và lưu video.

- Không cần máy chủ, không cần cơ sở dữ liệu: cài là chạy.
- Ít RAM: lưới camera dùng luồng phụ, chỉ camera đang hiện trên màn hình mới được mở; góc phải luôn hiện RAM đang dùng.
- Giao diện tiếng Việt, viết cho người không rành kỹ thuật.

Mã nguồn công khai, giấy phép MIT. Phiên bản hiện tại là **0.9.0**: đã kiểm thử bằng đầu ghi giả lập, **chưa thử trên đầu ghi thật** (xem [Báo cáo chẩn đoán](#báo-cáo-chẩn-đoán)). Đầu ghi dùng để thiết kế: DS-7732NI-M4 (32 kênh IP); các đầu ghi Hikvision có ISAPI và RTSP khác thường cũng chạy được.

## Cài đặt

1. Vào trang **Releases** của dự án trên GitHub, tải `CameraViewer-Setup-<phiên bản>.exe`.
2. Chạy file vừa tải. Lần đầu Windows có thể hiện cảnh báo xanh **"Windows đã bảo vệ máy tính của bạn"** vì bộ cài chưa có chữ ký số. Bấm **Thông tin thêm**, rồi **Vẫn chạy**. Chỉ phải làm một lần.
3. Bộ cài không cần quyền quản trị (cài cho tài khoản Windows đang dùng). Mục **Tạo biểu tượng ngoài màn hình** đã được chọn sẵn.
4. Bấm **Mở Camera Viewer ngay**.

Cần Windows 10 hoặc 11 bản 64-bit, cùng mạng nội bộ với đầu ghi.

## Thiết lập lần đầu (IT làm một lần)

Lần đầu mở, phần mềm vào thẳng **Cài đặt** với tiêu đề "Kết nối đầu ghi lần đầu":

1. **Tên** (tuỳ ý), **Địa chỉ IP** của đầu ghi (ví dụ `192.168.1.64`), **Cổng HTTP** (mặc định 80), **Cổng RTSP** (mặc định 554).
2. **Tài khoản** và **Mật khẩu** của đầu ghi (nên tạo tài khoản riêng, xem mục dưới).
3. Bấm **Kiểm tra kết nối**. Đúng thì hiện "Đã kết nối: DS-7732NI-M4 · 17 camera"; sai thì hiện lý do bằng tiếng Việt.
4. Chọn thư mục **Lưu video vào** và **Lưu ảnh vào** nếu muốn khác mặc định (`Video\Camera` và `Ảnh\Camera` của tài khoản Windows), rồi **Lưu**.

Người dùng hằng ngày chỉ cần bấm biểu tượng là thấy camera; không phải nhập lại gì.

## Yêu cầu phía đầu ghi

Tên mục trong menu có thể khác chút tuỳ firmware.

| Việc cần | Ở đâu trên đầu ghi | Ghi chú |
|---|---|---|
| Bật **RTSP**, cổng 554 | Mạng → Cổng (Port) | Xác thực RTSP kiểu digest là được (mặc định của Hikvision) |
| Bật **ISAPI / giao thức tích hợp** | Mạng → Cài đặt nâng cao → Giao thức tích hợp (Integration Protocol / Hikvision-CGI), xác thực digest | Dùng để đọc danh sách camera, tìm ghi hình và tải video |
| **Tài khoản riêng** cho phần mềm, không dùng `admin` | Quản lý người dùng → Thêm, cấp Operator hoặc User | Chỉ cần quyền đọc: **Xem trực tiếp từ xa** (Remote Live View) và **Xem lại từ xa** (Remote Playback; nếu có mục **Xuất/tải video từ xa** thì bật luôn) cho các camera cần xem. Không cần quyền cấu hình |
| Mở cổng **HTTP (80)** và **RTSP (554)** từ máy tính tới đầu ghi | Tường lửa, VLAN | Nếu đã đổi cổng trên đầu ghi, nhập đúng cổng đó trong Cài đặt |
| **Luồng phụ** bật cho mọi camera | Ghi hình/Mã hoá → Luồng phụ | Lưới camera dùng luồng phụ để nhẹ máy |
| **Đồng bộ giờ (NTP)** | Hệ thống → Thời gian | Giờ đầu ghi lệch nhiều thì xem lại, lưu video bị lệch giờ |

Hikvision **khoá địa chỉ IP** sau vài lần sai mật khẩu (thường 30 phút). Phần mềm dừng ngay sau lần sai đầu tiên và báo phải chờ bao lâu, không tự thử lại.

## Dùng hằng ngày

- **Trực tiếp**: lưới tất cả camera. Bấm một ô để xem lớn (chất lượng cao), **← Tất cả camera** hoặc phím Esc để quay lại. Di chuột lên ô để có nút **Xem lại**, **Chụp ảnh**, **Phóng to**.
- **Xem lại**: chọn camera, ngày, giờ rồi **Xem**. Lùi hoặc tới 30 giây, tạm dừng, bấm vào thanh thời gian 24 giờ để nhảy (vạch xanh là có ghi hình).
- **Lưu đoạn video**: khi đang xem lại, bấm **Đánh dấu bắt đầu**, **Đánh dấu kết thúc**, rồi **Lưu đoạn video** → **Lưu**. Video ra file MP4, giữ nguyên chất lượng.
- **Video đã lưu**: xem tiến độ, **Mở video**, **Mở thư mục**, **Hủy**, **Thử lại**.

Lưu video tải thẳng từ đầu ghi (nhanh). Đầu ghi nào không cho tải thì phần mềm tự ghi lại trong lúc phát, mất đúng bằng độ dài đoạn video.

## Xử lý sự cố

| Hiện tượng | Nguyên nhân thường gặp | Cách làm |
|---|---|---|
| "Không kết nối được đầu ghi" | Sai IP hoặc cổng, đầu ghi tắt, mạng không thông | Mở `http://<IP đầu ghi>` bằng trình duyệt trên cùng máy; được thì kiểm tra lại cổng trong Cài đặt |
| "Sai tài khoản hoặc mật khẩu" | Gõ sai, tài khoản bị đổi mật khẩu | Sửa trong Cài đặt rồi Kiểm tra kết nối |
| "Đầu ghi đang tạm khóa đăng nhập…" | Sai mật khẩu nhiều lần | Chờ đúng số phút phần mềm báo, sửa mật khẩu rồi thử lại |
| Ô camera "Mất kết nối, đang thử lại…" | Camera rớt mạng, RTSP tắt, luồng phụ tắt | Xem camera đó trên đầu ghi; bật RTSP và luồng phụ |
| Ô "Camera đang tắt" | Đầu ghi báo camera ngoại tuyến | Kiểm tra camera và dây mạng/PoE của nó |
| "Không có ghi hình lúc này" | Lịch ghi không ghi giờ đó, ổ cứng đầy hoặc hỏng | Kiểm tra lịch ghi và ổ cứng trên đầu ghi |
| Giờ xem lại lệch vài tiếng | Firmware ghi giờ theo cách khác, hoặc múi giờ đầu ghi sai | Chạy chẩn đoán, gửi báo cáo; bật NTP và đặt đúng múi giờ trên đầu ghi |
| Lưu video rất chậm | Đầu ghi không cho tải video qua ISAPI, phần mềm phải ghi lại khi phát | Bật giao thức tích hợp và quyền tải video cho tài khoản; chạy chẩn đoán để biết đầu ghi hỗ trợ cách nào |

Nhật ký của phần mềm nằm trong `%LOCALAPPDATA%\CameraViewer\logs` (giữ 14 ngày, không chứa mật khẩu).

## Báo cáo chẩn đoán

Vào **Cài đặt** → **Chạy chẩn đoán**. Phần mềm kiểm tra lần lượt, mỗi bước ghi **PASS** (đạt), **FAIL** (lỗi) hoặc **SKIP** (bỏ qua), kèm thời gian và lý do ngắn:

1. Thông tin đầu ghi (model, firmware), đồng hồ đầu ghi và độ lệch so với máy tính, cách firmware ghi giờ.
2. Danh sách camera (số camera, camera đang tắt, model camera; không ghi tên camera).
3. Tìm ghi hình 24 giờ qua trên một camera đang chạy.
4. Xem trực tiếp luồng phụ và luồng chính, xem lại qua RTSP: mở, chờ vài hình, ghi lại định dạng, độ phân giải, số hình/giây, tốc độ bit.
5. Lưu thử 10 giây như phần mềm vẫn làm, rồi lần lượt từng cách tải của đầu ghi (theo thời gian, theo đoạn có cắt, cả đoạn), chuyển sang MP4 và kiểm tra độ dài; cuối cùng ghi lại 10 giây qua RTSP (cách dự phòng).

Báo cáo lưu thành `probe-report-yyyyMMdd-HHmmss.txt` ra màn hình (Desktop), phần tóm tắt ở đầu file. **Mật khẩu không bao giờ được ghi vào báo cáo** (cả dạng mã hoá trong địa chỉ RTSP). Chẩn đoán **chỉ đọc**: không thay đổi gì trên đầu ghi. Thường mất 1–3 phút; lâu hơn nếu đầu ghi chỉ cho tải cả đoạn ghi 1 giờ. Nếu gặp lỗi đăng nhập, chẩn đoán dừng ngay để đầu ghi không khoá tài khoản. Gửi file này cho người hỗ trợ để chỉnh phần mềm theo đầu ghi thật.

## Dữ liệu và quyền riêng tư

- Cài đặt nằm trong `%LOCALAPPDATA%\CameraViewer\settings.json`. **Mật khẩu đầu ghi được mã hoá bằng Windows DPAPI** theo tài khoản Windows: chỉ đúng tài khoản đó trên đúng máy đó giải mã được. Chép file cài đặt sang máy khác thì phải nhập lại mật khẩu.
- Video và ảnh lưu vào thư mục đã chọn trong Cài đặt. Đây là hình ảnh có người: giữ và chia sẻ theo quy định của đơn vị.
- Phần mềm chỉ nói chuyện với đầu ghi trong mạng nội bộ và với GitHub để kiểm tra bản mới (tắt được trong Cài đặt). Không gửi dữ liệu đi đâu khác, không mở cổng mạng nào.
- Gỡ cài đặt không xoá cài đặt, nhật ký, video, ảnh. Muốn xoá hẳn thì xoá thư mục `%LOCALAPPDATA%\CameraViewer` và thư mục video/ảnh.

## Cập nhật

Phần mềm kiểm tra bản mới trên GitHub khoảng 10 giây sau khi mở (tắt được trong Cài đặt). Có bản mới thì hiện thanh **Có bản mới x.y.z — Cập nhật**. Bấm là xong: phần mềm tải về, kiểm tra kích thước và SHA-256, cài im lặng rồi mở lại. Cài đặt được giữ nguyên.

## Phát triển

Cần .NET 10 SDK, Inno Setup 6 (bộ cài), Python (vẽ biểu tượng), PowerShell.

1. Một lần trên mỗi máy: `powershell -ExecutionPolicy Bypass -File build\fetch-dev-tools.ps1`. Lệnh này tải vào `%LOCALAPPDATA%\CameraViewer-dev`: FFmpeg 9.0 bản LGPL (các DLL kèm theo phần mềm, cùng `ffmpeg.exe`/`ffprobe.exe` để kiểm tra), FFmpeg bản GPL và MediaMTX (chỉ cho đầu ghi giả lập). Không có gì trong số đó vào kho mã.
2. Xây dựng: `.\build.ps1` chạy test, đóng gói phần mềm vào `dist\app` (một file exe, FFmpeg DLL trong `dist\app\ffmpeg\`) và tạo bộ cài `dist\CameraViewer-Setup-<phiên bản>.exe`. Tuỳ chọn: `-SkipTests`, `-SkipInstaller`, `-Publish` (đăng bản lên GitHub Releases kèm `latest.json` cho cập nhật một chạm).
3. Chạy thử không cần đầu ghi: `CameraViewer.exe --demo`. Đặt biến môi trường `CV_DATA` trỏ tới thư mục tạm để không đụng cài đặt thật.
4. Biểu tượng: `python build\make-icon.py` vẽ lại `src\Cv.App\Assets\cv.ico`.

Cấu trúc: `src\Cv.Core` (giao tiếp ISAPI, cài đặt, hàng đợi lưu video, cập nhật), `src\Cv.Media` (FFmpeg: giải mã, chuyển MP4), `src\Cv.Diagnostics` (chẩn đoán), `src\Cv.App` (giao diện WPF), `tools\MockNvr` (đầu ghi giả lập), `tests\` (xUnit).

### Đầu ghi giả lập

Không có đầu ghi thật vẫn chạy được toàn bộ phần mềm: `tools\mock` dựng một DS-7732NI-M4 giả trên chính máy phát triển.

```
powershell -ExecutionPolicy Bypass -File tools\mock\start-mock.ps1
powershell -ExecutionPolicy Bypass -File tools\mock\verify-mock.ps1
powershell -ExecutionPolicy Bypass -File tools\mock\stop-mock.ps1
```

`start-mock.ps1` in ra thông số để nhập vào Cài đặt: địa chỉ `127.0.0.1`, cổng HTTP `8080`, cổng RTSP `554` (tự chuyển sang `8554` nếu 554 bận), tài khoản `admin`, mật khẩu `Mock@1234` (có ký tự `@` để thử mã hoá địa chỉ). Lần chạy đầu tạo các đoạn video mẫu trong `tools\mock\data` (vài chục giây, chỉ một lần). `stop-mock.ps1` tắt đúng những tiến trình mà `start-mock.ps1` đã mở. `verify-mock.ps1` kiểm tra từng lệnh ISAPI bằng `curl --digest`, tải video bằng GET kèm nội dung và bằng POST, rồi dùng `ffprobe` đọc file tải về và các luồng RTSP.

Đầu ghi giả mô phỏng:

| Phần | Như đầu ghi thật |
|---|---|
| Đăng nhập | HTTP digest (MD5, qop=auth), realm là model, nonce sống khoảng 3 giây (nonce hết hạn trả về 401 không kèm thử thách, như firmware thật); sai mật khẩu 5 lần thì khoá IP vài phút, trả 401 kèm `lockStatus`, `unlockTime`, `retryLoginTime` |
| ISAPI | `deviceInfo`, `time` (múi giờ kiểu POSIX `CST-7:00:00`), `InputProxy/channels` và `channels/status` (17 camera tên tiếng Việt chung chung, kênh 5 ngoại tuyến), `Streaming/channels`, `Security/adminAccesses` |
| Ghi hình | 7 ngày gần nhất, file khoảng 1 giờ (ranh giới lệch vài phút như chia theo dung lượng), mỗi ngày nghỉ 02:00–02:30 và vài khoảng trống ngẫu nhiên, file cuối kết thúc đúng lúc hiện tại, kênh 5 không có gì trong 24 giờ qua. Tìm kiếm chia trang (tối đa 50 kết quả một trang dù `maxResults` lớn hơn), `MORE` / `OK` / `NO MATCHES` |
| Tải video | Luồng PS kiểu Hikvision: đầu 40 byte `IMKH`, gói `00 00 01 BA`, PSM khai báo H.265 (0x24) hoặc H.264 (0x1B) và âm thanh G.711 µ-law, PES có PTS/DTS; dài đúng khoảng yêu cầu (lặp đoạn mẫu) |
| RTSP (MediaMTX) | `Streaming/Channels/N01` H.265 1280×720 15 hình/giây, `N02` H.264 640×360, có đồng hồ chạy trên hình; mỗi kênh chỉ chuyển tiếp hai luồng chung (không mã hoá lại); `Streaming/tracks/N01?starttime=...` phát một đoạn mẫu lặp lại (bỏ qua giờ yêu cầu) |

Tuỳ chọn của `start-mock.ps1` để thử các kiểu firmware:

- `-TimeMode localz` (mặc định: giờ địa phương kèm chữ Z, như đa số đầu ghi), `utc` (giờ UTC thật), `offset` (kết quả tìm kiếm ghi kèm `+07:00`, chữ Z trong yêu cầu là UTC).
- `-Download honor` (cắt đúng khoảng yêu cầu), `whole` (từ chối địa chỉ chỉ có giờ, trả cả file 1 giờ cho địa chỉ có `name=`, như một số firmware), `xml-error` (trả HTTP 200 kèm lỗi XML: không tải được cách nào, phần mềm phải ghi lại qua RTSP).
- `-DownloadCodec h265|h264`, `-DownloadMbps <n>` (giới hạn tốc độ tải để xem thanh tiến độ), `-HttpPort`, `-RtspPort`, `-Password`, `-Lan` (cho máy khác trong mạng vào), `-NewClips` (tạo lại đoạn mẫu).

Chỉ chạy phần ISAPI: `dotnet run --project tools\MockNvr -- --port 8080 --time-mode utc --download whole` (`--help` để xem mọi tuỳ chọn). Mỗi yêu cầu in một dòng (giờ, IP, lệnh, mã trả về, dung lượng, kết quả đăng nhập; không bao giờ in mật khẩu); khi chạy bằng `start-mock.ps1` các dòng này nằm trong `tools\mock\data\logs\mocknvr.log`. Test của đầu ghi giả (kể cả phần giao tiếp ISAPI thật của phần mềm và chẩn đoán chạy trên đầu ghi giả): `dotnet test tools\MockNvr.Tests`.

## Giấy phép

MIT (xem [LICENSE](LICENSE)). Thành phần bên thứ ba và giấy phép của chúng: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). Lịch sử thay đổi: [CHANGELOG.md](CHANGELOG.md).
