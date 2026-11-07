---
description: Tải bộ cài Camera Viewer từ GitHub và cài lên máy người xem camera.
---

# Cài đặt phần mềm

**Ai làm:** Bộ phận IT · **Mất khoảng:** 3 phút

{% hint style="info" %}
**Trước khi bắt đầu:** máy chạy Windows 10 hoặc 11 bản 64-bit, cùng mạng nội bộ với đầu ghi. Không cần quyền quản trị Windows: phần mềm cài cho tài khoản Windows đang đăng nhập.
{% endhint %}

{% stepper %}
{% step %}
### Tải bộ cài

Mở trang `https://github.com/tdphuong97/CameraViewer/releases`, ở bản mới nhất bấm **CameraViewer-Setup-0.9.0.exe** trong mục **Assets** để tải về (khoảng 77 MB).
{% endstep %}

{% step %}
### Chạy bộ cài

Mở file vừa tải. Nếu Windows hiện màn hình xanh **Windows đã bảo vệ máy tính của bạn**, bấm **Thông tin thêm**, rồi bấm **Vẫn chạy**. Cảnh báo này hiện vì bộ cài chưa có chữ ký số; chỉ gặp ở lần cài đầu.
{% endstep %}

{% step %}
### Cài và mở phần mềm

Ô **Tạo biểu tượng ngoài màn hình** đã được chọn sẵn, để nguyên. Bấm qua các bước, đến màn hình cuối giữ ô **Mở Camera Viewer ngay** rồi bấm hoàn tất. Phần mềm mở ra ở trang **Cài đặt**.
{% endstep %}
{% endstepper %}

{% hint style="success" %}
**Kết quả:** biểu tượng **Camera Viewer** có ngoài màn hình và trong menu Start. Tiếp theo: [Kết nối đầu ghi lần đầu](ket-noi-dau-ghi.md).
{% endhint %}

## Gỡ cài đặt

Mở **Cài đặt** của Windows > **Ứng dụng** > **Ứng dụng đã cài đặt**, tìm **Camera Viewer**, bấm **Gỡ cài đặt**.

Gỡ cài đặt không xoá cài đặt, video và ảnh đã lưu. Muốn xoá hẳn, xoá thêm thư mục `%LOCALAPPDATA%\CameraViewer` và các thư mục video, ảnh đã chọn trong phần mềm.

## Lỗi thường gặp

<details>

<summary>Không thấy nút Vẫn chạy</summary>

Phải bấm **Thông tin thêm** trước thì nút **Vẫn chạy** mới hiện. Nếu máy do công ty quản lý chặn hẳn phần mềm chưa ký số, cần quản trị máy cho phép file này.

</details>

<details>

<summary>Phần mềm báo thiếu thư viện FFmpeg</summary>

Thư mục cài bị thiếu file. Tải lại bộ cài và cài đè lên; cài đặt đã lưu được giữ nguyên.

</details>
