---
description: Kiểm tra kỹ đầu ghi và lưu báo cáo để gửi cho người hỗ trợ khi phần mềm chạy không đúng.
---

# Chạy chẩn đoán

**Ai làm:** Bộ phận IT · **Mất khoảng:** 1–3 phút

Chạy chẩn đoán khi cài lần đầu với một đầu ghi mới, hoặc khi camera, xem lại, lưu video không chạy đúng. Chẩn đoán **chỉ đọc**: không thay đổi gì trên đầu ghi.

{% hint style="info" %}
**Trước khi bắt đầu:** đầu ghi đã được nhập và **Kiểm tra kết nối** thành công. Xem [Kết nối đầu ghi lần đầu](../bat-dau/ket-noi-dau-ghi.md).
{% endhint %}

1. Vào **Cài đặt**, kéo xuống khung **Cập nhật và hỗ trợ**.
2. Bấm **Chạy chẩn đoán** (1). Phần mềm hiện **Đang chẩn đoán…**.

<figure><img src="../.gitbook/assets/cai-dat-chan-doan.svg" alt="Dòng Chẩn đoán với nút Chạy chẩn đoán (1)"><figcaption></figcaption></figure>

3. Chờ đến khi phần mềm báo **Mọi mục đều đạt. Báo cáo đã lưu ra Màn hình.** hoặc **Có mục chưa đạt. Báo cáo đã lưu ra Màn hình, hãy gửi cho bộ phận IT.**

{% hint style="success" %}
**Kết quả:** file `probe-report-` + ngày giờ + `.txt` nằm trên Màn hình (Desktop). Gửi file này cho người hỗ trợ phần mềm. Báo cáo **không bao giờ chứa mật khẩu** và không ghi tên camera.
{% endhint %}

## Chẩn đoán kiểm tra những gì

Mỗi bước ghi **PASS** (đạt), **FAIL** (lỗi) hoặc **SKIP** (bỏ qua), kèm thời gian và lý do ngắn; phần tóm tắt nằm ở đầu file.

| Bước | Kiểm tra |
| --- | --- |
| Thông tin đầu ghi | Model, firmware |
| Đồng hồ | Giờ đầu ghi, độ lệch so với máy tính, cách đầu ghi ghi giờ trong kết quả tìm kiếm |
| Danh sách camera | Số camera, camera đang tắt, model camera |
| Tìm ghi hình | Ghi hình 24 giờ qua trên một camera đang chạy |
| Xem trực tiếp, xem lại | Mở luồng phụ, luồng chính và xem lại; ghi định dạng, độ phân giải, số hình mỗi giây |
| Lưu video | Lưu thử 10 giây như phần mềm vẫn làm, thử từng cách tải của đầu ghi, chuyển sang MP4 và kiểm tra độ dài |

Nếu gặp lỗi đăng nhập, chẩn đoán dừng ngay để đầu ghi không khoá tài khoản. Chẩn đoán thường xong trong khoảng 1 phút; có thể lâu hơn 3 phút nếu đầu ghi chỉ cho tải cả đoạn ghi.
