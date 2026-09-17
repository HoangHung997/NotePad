# Đánh giá hướng trợ lý độc lập

Ngày 16/09/2026. Yêu cầu: trợ lý có thể đọc ngữ cảnh/tệp, sửa tệp, lập trình, Word, điều khiển máy tính, kiểm tra và tự đánh giá; phát triển app riêng rồi nghiệm thu trước khi tích hợp. Người dùng chọn **Ollama và API tùy chọn**, không chọn lõi Codex.

## Kết luận nghiên cứu

Khả thi để xây một trợ lý có **nhóm chức năng tương tự**. Không có cơ sở để hứa “giống y hệt Codex” về chất lượng, công cụ độc quyền hoặc độ ổn định. Model là bộ phận suy luận; app còn phải thực thi công cụ, quản lý quyền, duy trì trạng thái, chọn ngữ cảnh và kiểm chứng. Việc nối API vào khung chat không tự tạo ra các khả năng này.

Ollama có giao thức tool calling: model đề xuất lệnh, chương trình thực thi và đưa kết quả vào lượt tiếp theo. Điều này phù hợp cho local/LAN nhưng không chứng minh mọi model chọn đúng công cụ hoặc đọc đúng kết quả. Lab dùng giao thức native, không tự chạy đoạn mã được model viết trong một code block. [Tài liệu Ollama](https://docs.ollama.com/capabilities/tool-calling).

Codex có App Server giao tiếp hai chiều và cơ chế request/approval; SDK cũng hỗ trợ tạo, tiếp tục và khôi phục phiên. Có thể dùng làm nền tảng một giao diện khác, nhưng đó không phải một lõi độc lập với Codex nên **không chọn** theo quyết định của người dùng. Tính năng của app Codex không đồng nghĩa mọi phần đều có sẵn qua SDK hoặc miễn phí. [App Server](https://learn.chatgpt.com/docs/app-server), [SDK](https://learn.chatgpt.com/docs/codex-sdk).

Computer-use cần môi trường thực thi, quan sát kết quả và kiểm tra hành động; tài liệu OpenAI đề xuất môi trường biệt lập khi dựng thử. Microsoft UI Automation cung cấp truy cập control; Invoke/Value chỉ hoạt động khi ứng dụng hỗ trợ pattern, không phải nút nào cũng điều khiển được và provider có thể treo. Lab đặt phần UIA trong tiến trình riêng có giới hạn thời gian, không thay việc thử thực tế. [Computer use](https://developers.openai.com/api/docs/guides/tools-computer-use), [UI Automation](https://learn.microsoft.com/en-us/dotnet/framework/ui-automation/ui-automation-overview), [Invoke](https://learn.microsoft.com/en-us/dotnet/api/system.windows.automation.invokepattern.invoke?view=windowsdesktop-10.0).

## Kiến trúc đề xuất

```text
Giao diện chat / tệp / lịch sử / duyệt thao tác
                    |
          Điều phối tác vụ có giới hạn bước
           /                         \
Ollama native tool calling    API Chat Completions
           \                         /
       Registry công cụ + xác nhận + kiểm phạm vi
           |             |              |
       Tệp / Word     Build / test    Windows UIA
           \             |              /
          Kết quả thực tế + hash + nhật ký
                         |
         Đọc lại / kiểm cấu trúc / đánh giá kết quả
```

Sơ đồ là hướng xây dựng, không chứng nhận các adapter đều nghiệm thu. Chỉ model đã qua bài thử mới được ghi nhận đạt cho loại tác vụ tương ứng. Không lấy một bài tổng hợp đạt để suy ra model sẽ làm đúng mọi tài liệu.

## So với yêu cầu

| Nhóm | Lab đầu tiên | Cần thêm để gần một trợ lý hoàn chỉnh |
| --- | --- | --- |
| Ngữ cảnh | Lịch sử gần đây, tệp trong thư mục đã chọn, kết quả công cụ | Index có trích nguồn, tìm lịch sử cũ, working set, checkpoint có kiểm chứng; không bịa hoạt động ngoài app |
| Tệp | Đọc/tìm text/code, Word/Excel qua adapter, ghi có duyệt/hash/backup | Diff tốt, undo/restore UI, file lớn, nhiều người sửa, sandbox thật |
| Lập trình | Đọc/sửa mã, yêu cầu build/test .NET có xác nhận | Bộ bài lỗi có test oracle, các ngôn ngữ khác, sandbox/container, đo tỷ lệ hồi quy |
| Word | Tạo A4 đơn giản, sửa đoạn đơn giản, kiểm OpenXML | Template/style/table/section/track changes; renderer tích hợp và kiểm trực quan, OCR/bố cục phức tạp |
| Máy tính | Chọn một cửa sổ, đọc control, invoke/set-value có xác nhận | End-to-end trên app thật, vision/canvas, browser adapter, recovery khi UI đổi, chặn prompt injection và truyền dữ liệu trái ý |
| Kỹ năng | Quy trình host-owned cho code/Word/context/desktop | Gói kỹ năng có version, nguồn tin cậy, quyền từng kỹ năng, kiểm trước khi cài |
| Tự kiểm | Công cụ có kết quả thật, bộ test, nhật ký | Oracle độc lập, benchmark lặp, theo dõi sai số/chi phí/thời gian; không để model tự chấm là bằng chứng duy nhất |

## Điều kiện kết luận OK

Các ngưỡng sau là **đề xuất nghiệm thu**, chưa phải số đo đạt:

1. Tối thiểu 30 tác vụ đại diện trên dữ liệu mẫu/bản sao, có đáp án và tiêu chí trước khi chạy: 10 tệp/ngữ cảnh, 8 Word/PDF, 6 mã nguồn, 6 thao tác desktop/browser.
2. Mỗi nhóm chạy lại ít nhất ba lần trên từng cấu hình model đã chọn. Tách tỷ lệ hoàn thành, lỗi sai nội dung, thời gian, phí và số lần cần người dùng can thiệp. Không gộp các nhóm để che nhóm yếu.
3. Không ghi ngoài phạm vi, không gửi dữ liệu/khóa sai đích, không tự thực hiện thao tác bị từ chối trong bộ kiểm bảo mật. Mọi sai phạm loại này chặn tích hợp, dù tác vụ có vẻ hoàn thành.
4. Word phải qua kiểm nội dung và render đối chiếu; code phải qua test hành vi và hồi quy; desktop phải đọc lại trạng thái sau hành động. “Build thành công”, “API trả 200” hoặc “nút invoke không lỗi” không đủ.
5. Restart giữ lịch sử; Dừng không tạo thao tác muộn; mất mạng giữ bằng chứng và không tự gửi lại; đổi dự án/model không trộn phạm vi.
6. Người dùng chốt nhóm chức năng đã đạt, rồi mới tích hợp từng adapter. Không thay toàn bộ chat H2 Notes bằng Lab khi gate còn chưa đạt.

## Đánh đổi model

Local giữ việc xử lý ở máy/host được cấu hình, nhưng máy yếu có thể mất nhiều phút cho nhiều vòng công cụ. API có thể nhanh/mạnh hơn nhưng phụ thuộc model, chi phí, quyền dùng và dữ liệu được gửi. Không kết luận model A hơn B chỉ từ tên hoặc kích thước. Ưu tiên độ đúng trên tác vụ của người dùng, đo tại máy thực, và giữ khả năng đổi model mà không mất lịch sử.

Lab chưa tự nối vào hồ sơ online đã lưu trong H2 Notes. Khi cần benchmark online, người dùng nhập key vào UI Lab và chọn endpoint/model; không đọc key của app khác hay dùng dữ liệu thật để thử ngầm.
