## 0.9.6

- Mới: nghe tiếng của camera. Màn xem lớn một camera và màn Xem lại có nút Bật tiếng, tắt sẵn mỗi lần mở phần mềm; lưới nhiều ô không có tiếng để máy nhẹ. Camera không gửi tiếng thì nút xám và ghi Không có tiếng (cần bật ghi tiếng trên camera hoặc đầu ghi).
- Mới: bố cục thêm lưới 6, 8, 12 ô và ba kiểu một ô lớn kèm các ô nhỏ (1+5, 1+7, 1+12). Nút Bố cục mở danh sách có hình mẫu; Tự động chọn số ô như trước.
- Mới: nút Camera ở góc dưới bên phải mở danh sách camera, kéo một camera thả vào ô (kể cả camera ở trang khác hoặc ô trống).
- Mới: ẩn camera không cần xem khỏi màn Trực tiếp (nút con mắt gạch chéo trên ô, có Hoàn tác; Hiện lại trong danh sách camera). Xem lại vẫn thấy đủ camera.
- Mới: Bộ sắp xếp có tên, ví dụ Trực đêm: lưu bố cục, thứ tự và camera ẩn, chuyển bằng nút Bộ ở thanh dưới. Mọi chỉnh sửa tự lưu vào bộ đang chọn; bộ Mặc định là cách sắp xếp đang dùng.
- Hướng dẫn sử dụng cập nhật các phần trên.

## 0.9.5

- Mới: Có người ở màn Xem lại. Nút cạnh thanh thời gian cho biết số lượt camera thấy có người trong ngày; bấm vào để xem ảnh từng lượt (ảnh đầu ghi chụp), bấm ảnh để xem lại đúng lúc đó. Những lúc có người hiện thành vạch xanh lá trên thanh thời gian. Dùng sẵn phát hiện người của camera và đầu ghi, không tốn thêm bộ nhớ máy tính.
- Nút Lưu video đổi tên thành Trích xuất video (nút trong bảng: Trích xuất).
- Xem lại tới sát giờ hiện tại: hết đoạn ghi thì chuyển tiếp ngay hoặc báo Đã xem đến thời điểm mới nhất, không còn đứng hình rồi báo Mất kết nối đầu ghi (đầu ghi ngừng gửi mà không đóng luồng ở file đang ghi).
- Cách dự phòng ghi lại qua RTSP lấy dư 5 giây ở cuối như tải theo giờ, và không còn báo lỗi khi đầu ghi ngừng gửi ở cuối đoạn.
- Chạy chẩn đoán có thêm dòng Lúc có người (số lần trong 24 giờ, kênh nào có, thử tải một ảnh).

## 0.9.4

- Lưu video dễ hơn: ở màn Xem lại chỉ còn một nút xanh Lưu video (cạnh Chụp ảnh). Bấm vào là có bảng ba bước đánh số bên cạnh video: 1 Từ lúc, 2 Đến lúc, 3 Lưu video. Bảng điền sẵn 5 phút quanh lúc đang xem (2 phút trước, 3 phút sau), nên phần lớn chỉ cần bấm Lưu video.
- Nút Giờ đang xem lấy đúng giây của video; gõ giờ kiểu 14:30, 14h30 hay 14:30:15 đều được; kéo hai đầu đoạn màu cam trên thanh thời gian cũng đổi giờ trong bảng. Thanh thời gian tự phóng to còn 1 giờ để thấy rõ đoạn chọn.
- Bỏ hai nút Đánh dấu bắt đầu, Đánh dấu kết thúc và hộp Lưu đoạn video cũ; thư mục lưu đổi trong Cài đặt. Video xem lại to hơn vì bớt một hàng nút.

## 0.9.3

- Lưu video nhanh hơn hẳn với đầu ghi DS-7732NI-M4 firmware V5.04: tải đúng đoạn theo giờ (10 giây chưa tới 1 giây) thay vì cả đoạn ghi 5 tiếng (khoảng 700 MB, nửa phút). Đã thử trên đầu ghi thật.
- Video lưu ra không còn khung hình vỡ ở cuối (đầu ghi cắt ngang khung cuối), và lấy dư khoảng 5 giây ở cuối để không thiếu cảnh đã đánh dấu.
- Chạy chẩn đoán báo đúng như khi lưu video thật: đầu ghi gửi cả đoạn ghi thì phần mềm vẫn cắt đúng chỗ, không còn báo lỗi nhầm.

## 0.9.2

- Hướng dẫn sử dụng (dấu hỏi hoặc F1) mở trong một cửa sổ riêng của phần mềm thay vì trình duyệt, đúng trang về màn hình đang xem. Đường dẫn ra Internet trong tài liệu vẫn mở bằng trình duyệt. Máy thiếu WebView2 thì mở bằng trình duyệt như cũ.

## 0.9.1

- Thêm nút Hướng dẫn sử dụng (dấu hỏi, góc trên bên phải) và phím F1: mở tài liệu hướng dẫn đi kèm phần mềm, đọc được cả khi không có Internet.
- Tài liệu hướng dẫn sử dụng đầy đủ: cài đặt, kết nối đầu ghi, xem trực tiếp, xem lại, lưu video, cập nhật, chẩn đoán, xử lý sự cố.

## 0.9.0

- Bản đầu tiên: xem camera của một đầu ghi Hikvision, thay cho phần mềm quản lý nặng nề, ít tốn RAM.
- Trực tiếp: lưới tất cả camera tự chia ô, bấm một camera để xem lớn với chất lượng cao; camera tắt được báo rõ, không làm chậm các ô khác.
- Xem lại: chọn camera, ngày và giờ; tạm dừng, lùi hoặc tới 30 giây; thanh thời gian 24 giờ thấy rõ đoạn có ghi hình, bấm để nhảy tới.
- Lưu video: đánh dấu bắt đầu và kết thúc rồi bấm Lưu đoạn video; ra file MP4 mở được ở mọi máy, giữ nguyên chất lượng (không nén lại).
- Lưu video tải thẳng từ đầu ghi; đầu ghi không cho tải thì phần mềm tự ghi lại khi phát.
- Mục Video đã lưu: xem tiến độ, mở video, mở thư mục, huỷ hoặc thử lại.
- Chụp ảnh đầy đủ độ phân giải khi xem trực tiếp hoặc xem lại.
- Cài đặt một lần: nhập địa chỉ, tài khoản đầu ghi, bấm Kiểm tra kết nối; mật khẩu được bảo vệ bằng Windows DPAPI.
- Chạy chẩn đoán: kiểm tra đầu ghi tại chỗ và lưu file báo cáo (không ghi mật khẩu).
- Cập nhật một chạm từ GitHub Releases.
- Lưu ý: đã kiểm thử bằng đầu ghi giả lập, chưa thử trên đầu ghi thật; sẽ chỉnh sau khi có báo cáo chẩn đoán tại chỗ.
