# Tự chẩn đoán và phục hồi: AI Lab

Ngày 16/09/2026. Thay đổi chỉ trong `experiments/H2AgentLab`, chưa tích hợp H2 Notes.

## Mục tiêu

Không coi lỗi đầu tiên là kết luận cuối, không coi gọi công cụ thành công là đúng yêu cầu. Mô hình cần đối chiếu mục tiêu, bằng chứng, giả định và cách gọi; chọn phép kiểm có ích; sửa phương pháp; kiểm kết quả. Skill và môi trường giúp quá trình này, không biến một model yếu thành model có năng lực ngang Codex.

## Đã làm

- Hướng dẫn phục hồi dùng chung trong vòng agent, không riêng một tên tệp hay loại tài liệu.
- Lỗi có mã, hướng kiểm tra và dấu hiệu có thể khắc phục. Bao gồm thiếu tệp/skill, sai tài nguyên/tham số, lỗi Python, kiểm cấu trúc không đạt, hash/token cũ, thiếu quyền và khả năng chưa có.
- Tìm tên gần giống từ hệ thống tệp thực, trong thư mục đã chọn. Trả nhiều ứng viên nếu mơ hồ, không đọc nội dung ngầm hoặc sửa tên. Báo quét thiếu/giới hạn/thư mục không truy cập được; không suy vắng mặt ở mọi nơi từ một lượt quét giới hạn.
- Giám sát kết luận sớm sau lỗi và nhắc tiếp tục kiểm tra tối đa hai lần, vẫn trong 24 vòng hiện có. Không chạy mã từ phản hồi stream chưa hoàn tất. JSON tham số sai được yêu cầu sửa tối đa hai lần; không thực thi một phần nhóm lệnh lỗi.
- Chặn lặp lệnh đã thất bại hai lần; thao tác ghi/bấm không rõ kết quả cần quan sát lại đúng đối tượng. Từ chối ghi một tệp chặn cả đường chuyển sang publish tệp đó trong cùng lượt.
- Nhật ký giữ bản trả lời chưa được xác minh và các bước phục hồi. Không yêu cầu/chế tác bản ghi suy nghĩ nội bộ. Hướng dẫn `coding/references/recovery.md` hỗ trợ chẩn đoán rộng hơn tên file.

## Bằng chứng tự động

Build Release cuối: 0 lỗi, 0 cảnh báo. Chạy lại đúng bản cuối: 14 bài phục hồi, 16 bài regression và 14 bài skill/runtime đều đạt. Những bài luồng model dùng phản hồi mô phỏng, còn đọc Word, tìm tệp, chạy Python AppContainer, hash và ghi tệp dùng thực thi thật. Không gọi 44 bài này là 44 tác vụ được Gemma tự giải quyết. [Bằng chứng](evidence/2026-09-16-recovery/README.md).

Tình huống gồm sai tên tệp rồi kết luận sớm, nhiều tên gần giống, quét vượt 200 tệp, chọn sai skill và tài nguyên, Python assertion lỗi rồi sửa/đọc lại, sai JSON hoàn chỉnh, lặp lỗi không tiến triển, từ chối quyền, hash cũ và bấm có kết quả chưa rõ. Cả Ollama native và Chat Completions giả lập được kiểm.

Bài quét đầu tiên có một assertion sai: giả định tìm tên chính xác sẽ không kèm tên gần giống. Đã sửa tiêu chí kiểm đúng hợp đồng là tìm được ứng viên chính xác và quét đủ 215 tệp; không bỏ khả năng gợi ý tên gần giống. Chạy lại cả 13 bài đạt.

## Giới hạn cần hiểu đúng

Phần giám sát dựa trên dấu vết công cụ, không hiểu ngữ nghĩa mọi tác vụ. Một phép đọc/sửa thành công có thể vẫn chọn sai tài liệu hoặc sai ý người dùng; model còn phải đối chiếu, kiểm độc lập và hỏi khi mơ hồ. Kết quả không đạt nhưng mã vẫn thoát 0, không có assertion/lỗi cấu trúc và model không nhận ra thì chưa thể tự động phát hiện hết.

Không tự mở rộng thư mục, vượt quyền, cài thư viện, truy cập Internet, đổi model/API hoặc gửi lại yêu cầu mạng đã đứt không rõ trạng thái. Khả năng nghiên cứu hiện là đọc dữ liệu, skill, thư viện đã có và dấu vết thực thi; Lab chưa có bộ duyệt web chung. Ngân sách bước và Dừng vẫn có hiệu lực. Hết ngân sách là chưa hoàn tất, không phải chứng minh yêu cầu bất khả thi.

Một benchmark local riêng tạo lỗi đọc `prompt.docx` có thật, trong khi thư mục fixture chỉ có `promt.docx`. Model phải tự dùng bằng chứng để đọc tệp thật và trả mã/việc chưa xong. Đây là đánh giá **phục hồi từ lỗi được chuẩn bị trước**, không khẳng định model tự sinh lỗi ban đầu. Kết quả model thật được ghi riêng, không suy ra từ mock tests.

## Kết quả Gemma4 thật

**Đạt một mẫu:** `gemma4:latest` qua Ollama local nhận lỗi đọc có kèm tên ứng viên thực, tự gọi `read_file` với `promt.docx`, nhận nội dung thật và trả đúng `H2-REC-731`, việc kiểm khối lượng và thông tin chưa có ngày hoàn thành. Không có ghi tệp; hash/byte tài liệu nguồn giữ nguyên. Log ghi `ACTUAL_RECOVERY_PASS=True`.

Model sử dụng ứng viên do lớp tìm tên của host trả về, không tự gọi một lượt tìm kiếm khác trong bài này. Không suy từ một mẫu này rằng đã giải được tác vụ Excel phức tạp còn lỗi ở chặng trước, hoặc ngang Codex. Chưa thử model online thật trong chặng này; kiểm giao thức online dùng máy chủ giả lập.
