---
description: Hiện tượng thường gặp, nguyên nhân và cách xử lý, cho người xem camera và bộ phận IT.
---

# Xử lý sự cố

Tìm hiện tượng hoặc đúng câu phần mềm báo trong bảng. Cột **Ai làm** cho biết người xem camera tự xử lý được hay cần bộ phận IT.

## Xem camera

| Hiện tượng | Nguyên nhân thường gặp | Cách làm | Ai làm |
| --- | --- | --- | --- |
| **Chưa kết nối đầu ghi** | Chưa nhập đầu ghi, hoặc mất mạng tới đầu ghi | Bấm **Thử lại**. Không được thì kiểm tra mạng và [Kết nối đầu ghi](../bat-dau/ket-noi-dau-ghi.md) | Người xem, rồi IT |
| Một ô báo **Mất kết nối, đang thử lại…** | Camera rớt mạng, luồng phụ hoặc RTSP bị tắt | Chờ 1 phút. Kéo dài thì xem camera đó trên đầu ghi; bật luồng phụ và RTSP | IT |
| Ô báo **Camera đang tắt** | Đầu ghi báo camera ngoại tuyến | Kiểm tra camera, dây mạng hoặc nguồn PoE của nó | IT |
| Ô báo **Không xem được camera này** | Tài khoản không có quyền xem camera đó, hoặc camera không có luồng phát | Cấp quyền xem cho tài khoản trên đầu ghi; kiểm tra camera | IT |
| Mọi ô đều lỗi sau khi đổi mật khẩu đầu ghi | Phần mềm vẫn dùng mật khẩu cũ | Nhập mật khẩu mới trong **Cài đặt**, bấm **Kiểm tra kết nối**, rồi **Lưu** | IT |
| Máy chạy chậm khi mở phần mềm | Máy yếu, đang xem lớn camera độ phân giải cao | Quay lại lưới; thử đặt **Giải mã bằng card đồ họa** là **Tự động** | Người xem, rồi IT |

## Xem lại và trích xuất video

| Hiện tượng | Nguyên nhân thường gặp | Cách làm | Ai làm |
| --- | --- | --- | --- |
| **Không có ghi hình lúc này** | Lịch ghi không ghi giờ đó, ổ cứng đầu ghi đầy hoặc hỏng | Chọn giờ nằm trên vạch xanh. Kéo dài nhiều ngày thì kiểm tra lịch ghi và ổ cứng | Người xem, rồi IT |
| Giờ xem lại lệch vài tiếng so với thực tế | Múi giờ hoặc đồng hồ đầu ghi sai, hoặc firmware ghi giờ khác thường | Bật NTP và đặt đúng múi giờ trên đầu ghi; [Chạy chẩn đoán](chay-chan-doan.md) và gửi báo cáo | IT |
| Trích xuất video rất chậm, mất đúng bằng độ dài đoạn video | Đầu ghi không cho tải video trực tiếp, phần mềm phải ghi lại trong lúc phát | Bật giao thức tích hợp và quyền tải video cho tài khoản; chạy chẩn đoán để biết đầu ghi hỗ trợ cách nào | IT |
| **Mất kết nối đầu ghi khi đang tải video** | Mạng chập chờn | Bấm **Thử lại** khi mạng ổn định | Người xem |
| **Ổ đĩa đầy** | Ổ chứa thư mục video hết chỗ | Xoá hoặc chuyển bớt video cũ, rồi **Thử lại** | Người xem |
| **Không thấy người nào trong ngày này.** dù có người qua lại | Camera chưa bật nhận người: phát hiện chuyển động đang tắt, vùng phát hiện chưa vẽ, hoặc mục tiêu chưa chọn người | Trên trang web của đầu ghi, mở phần phát hiện chuyển động của kênh đó (thường ở Cấu hình > Sự kiện; tên mục có thể khác theo firmware): bật, vẽ vùng, chọn mục tiêu **Người** (Human). Chạy chẩn đoán để xem kênh nào đã có lượt có người | IT |
| Không có nút **Có người** | Đầu ghi không cho tìm sự kiện có người (đời cũ hoặc hãng khác) | Không cần làm gì; xem lại và trích xuất video vẫn bình thường | IT |
| Nút tiếng xám, ghi **Không có tiếng** (xem lớn hoặc xem lại) | Camera không gửi tiếng: tiếng chưa được bật trên camera hoặc đầu ghi (kiểu luồng phải là Video & Audio), hoặc camera không có micro. Đoạn ghi từ trước lúc bật tiếng cũng không có tiếng | Bật ghi tiếng cho kênh đó trên trang web của đầu ghi hoặc camera, rồi xem lại đoạn mới ghi | IT |
| Bấm **Bật tiếng** mà vẫn im, có báo **Máy không có loa hoặc tai nghe** | Máy không có loa, tai nghe, hoặc đang điều khiển từ xa nên không có thiết bị âm thanh | Cắm loa hoặc tai nghe; kiểm tra Windows có thiết bị phát âm thanh; âm lượng không bị tắt | Người xem, rồi IT |
| Bật tiếng nhưng không nghe gì, nút không báo lỗi | Âm lượng Windows thấp hoặc đang tắt, hoặc camera chỉ truyền im lặng | Chỉnh âm lượng Windows; thử camera khác để so | Người xem |
| Video lưu ra không có tiếng | Camera không thu tiếng, hoặc thu tiếng kiểu G.711 mà file MP4 không chứa được | Bình thường với đa số camera; hình vẫn đầy đủ | |

## Kết nối đầu ghi (bộ phận IT)

| Phần mềm báo | Cách làm |
| --- | --- |
| **Không kết nối được đầu ghi … Kiểm tra mạng và địa chỉ IP.** | Mở `http://` + địa chỉ IP đầu ghi bằng trình duyệt trên cùng máy. Vào được thì kiểm tra **Cổng HTTP**; không vào được thì kiểm tra mạng, VLAN, tường lửa |
| **Sai tài khoản hoặc mật khẩu đầu ghi.** | Sửa trong **Cài đặt**; dòng báo lỗi cho biết còn bao nhiêu lần thử |
| **Đầu ghi đang tạm khóa đăng nhập…** | Chờ đúng số phút được báo rồi thử lại |
| **Tài khoản này không đủ quyền trên đầu ghi.** | Cấp quyền xem trực tiếp, xem lại và tải video cho tài khoản |
| **Đầu ghi đang bận. Thử lại sau ít phút.** | Đầu ghi đang phục vụ nhiều phiên xem hoặc tải. Thử lại sau |
| **Đầu ghi báo lỗi thiết bị.** | Kiểm tra ổ cứng và tình trạng đầu ghi trên giao diện của đầu ghi |

Nhật ký của phần mềm nằm ở `%LOCALAPPDATA%\CameraViewer\logs` (giữ 14 ngày, không chứa mật khẩu). Gửi kèm nhật ký và [báo cáo chẩn đoán](chay-chan-doan.md) khi cần hỗ trợ.
