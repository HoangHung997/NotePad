# Kiểm thử chức năng Agent — 22/09/2026

Đã kiểm thử bằng **Agent production + gemma4:cloud**, trên dữ liệu mẫu riêng tại `D:\VSstudio\H2Notes\.artifacts\agent-full-test-2026-09-22`. Các lượt thất bại được giữ lại cùng các lượt chạy lại sau sửa. Không dùng tài liệu cá nhân làm mẫu; tài liệu Word `3.NKKS.docx` không thuộc phạm vi kiểm thử.

**Kết quả:** bộ kiểm tra ứng dụng cuối cùng **584 đạt, 0 lỗi**. Các nhóm thực tế dưới đây đã chạy ba lượt. **Chưa thể kết luận mọi chức năng Agent đều đạt:** Excel đang mở bị chặn bởi hộp thoại; điều khiển chuột/phím trên desktop và kết nối tài khoản bên ngoài chưa được xác nhận thực tế.

## Các lượt thực tế và đọc lại độc lập

| Nhóm | Kết quả cuối | Nội dung kiểm tra / bằng chứng |
|---|---|---|
| Tạo Word, Excel, PDF | 3/3 lượt, mỗi lượt 3 định dạng | `create-run-1`; `verify-create.json`: tiêu đề, nội dung, số liệu, công thức, mã giữ nguyên. Tiêu đề Word được kiểm thêm trong `verify-word-format.json`. |
| Sửa Word, Excel, PDF | 3/3 | `edit-run-5`; `retest-4/verify-edit.json`: thêm đoạn, sửa B2 từ 12 thành 15, giữ công thức SUM và định dạng, PDF thêm trang cuối và giữ trang cũ. 24/24 kiểm tra nội dung đạt. |
| Xóa bản sao Word, Excel, PDF, TXT | 3/3 | `delete-run-1`; `retest-3/verify-delete.json`; `verify-delete-original-hashes.json`: 12 bản sao bị xóa, thư mục thử trống; SHA-256 của 12 tệp gốc sau sửa giữ nguyên. |
| Word đang mở | 3/3 | `office-run-2/word-live-*`; `verify-office-word.json`: sửa đoạn thứ hai, in đậm, giữ tiêu đề và đoạn KEEP, lưu bản sao và đọc lại bằng thư viện độc lập. |
| Excel đang mở | **Chưa đạt** | `office-run-1..3`: Excel từ chối COM. Đọc giao diện xác nhận hộp thoại “Sorry, Excel can't open two workbooks with the same name at the same time.” Công cụ bấm/phím trả `GetCursorPos failed: Access is denied (0x80070005)`, chưa đóng được hộp thoại. Không coi các bài kiểm tra Office giả lập là Excel thực tế đã đạt. |
| AutoCAD DWG/DXF | 3/3 | `cad-run-2`; `verify-cad.json`: AutoCAD Core Console thật tạo LINE/TEXT/CIRCLE, sửa tọa độ LINE và chữ TEXT, xóa CIRCLE, mở lại DWG kiểm tra, xuất DXF; sao chép/xóa hai tệp dùng một lần. Bộ đọc DXF độc lập xác nhận hình học và nội dung. |
| OCR Docling | 3/3 mẫu | `ocr-run-3`: PDF quét tiếng Anh, PDF quét tiếng Việt, PNG tiếng Việt. Chạy model offline được cài trên máy. |
| OCR MinerU | 3/3 mẫu | `ocr-run-3`: cùng ba mẫu; đối chiếu tiêu đề, dòng tiếng Việt, Alpha 12, Beta 30. |
| OCR GOT-OCR | 3/3 mẫu | `ocr-got-run-4`: sau sửa lỗi bộ nhớ, ba mẫu đạt; khoảng 101–108 giây/mẫu trên máy này. |
| Đính kèm → OCR → Agent | 3/3 | `ocr-agent-run-2`; `verify-ocr-agent.json`: ba engine, PDF/PNG, Agent tự đọc attachment và lưu JSON với tiêu đề đúng dấu, Alpha 12, Beta 30, tổng 42. Byte của tệp gốc được kiểm tra giữ nguyên. |
| Truy cập mạng, lọc và lưu tin | 3/3 | `news-run-7`; `verify-news.json`: VnExpress/BBC RSS thật, tối đa 5 tin/lượt, cửa sổ 72 giờ, loại trùng, tóm tắt tiếng Việt, JSON/Markdown. Cả 15 tiêu đề/URL/ngày khớp dữ liệu nguồn đã ghi nhận; hash bằng chứng được kiểm tra. |

Các kết quả thực tế trên dùng mẫu nhỏ, hữu hạn. Tệp XLSX đóng được kiểm công thức và dữ liệu; chưa dùng kết quả này để khẳng định Excel đang mở đã tính lại thành công. Tóm tắt tin do model soạn, còn tiêu đề/URL/ngày được giữ trực tiếp từ nguồn.

## Lỗi tìm thấy và đã sửa

1. **Kết quả dài không có đường đọc tiếp:** runtime cất kết quả web/Office vào bằng chứng nhưng thiếu công cụ đọc. Agent đoán `read_run` hoặc tải nguồn lặp lại. Đã thêm `read_tool_output`, đọc từng phần có kiểm hash và giới hạn độ dài; hướng dẫn chứa đúng tên công cụ.
2. **Assistant thiếu mã đính kèm:** chỉ một số bề mặt UI đưa attachment ID vào context. Đã đưa danh sách ID/tên/nguồn OCR vào adapter chung; Assistant xử lý PDF/ảnh bằng OCR đã chọn trước khi gửi model, giữ bản nháp khi lỗi/hủy.
3. **Theo dõi sửa lỗi đọc nhầm bản trình bày:** output có dòng bằng chứng hoặc bị rút gọn khiến runtime không đọc được `failureId`, mã lỗi và kết quả phục hồi. Đã đọc output nguyên bản, theo dõi chuỗi thử lại có cùng hash đầu vào, cho phép hash mới của cùng phép ghi; thành công ở tệp khác vẫn không xóa lỗi cũ. Chặn lặp cùng lỗi/đầu vào ba lần.
4. **Sửa PDF:** thiếu hash hiện tại cho tệp nhị phân và hướng dẫn dễ tạo PDF mới làm mất trang cũ. Đã trả metadata/hash rõ ràng, yêu cầu ghép trang bằng pypdf và kiểm nội dung cũ/mới trước khi công bố. Không giả nhận metadata là nội dung scan.
5. **Mở ứng dụng không cần thiết làm khóa tệp:** tác vụ giới hạn thư mục dùng công cụ tệp/Python trên bản sao; không tự mở Word/Excel để đọc tệp. Bổ sung tên thư viện Python có sẵn và đường phục hồi lỗi.
6. **Office:** tránh hủy RCW COM đang được chia sẻ; discovery chỉ lấy metadata; đóng đúng bản sao Word trước khi tính hash. Lỗi Office đang bận được báo rõ để xử lý hộp thoại.
7. **AutoCAD:** model tự ghép lệnh xuất DXF sai và không xử lý được output console. Đã bổ sung công cụ tạo/đọc/sửa/xuất tệp bằng AutoCAD Core Console, host tự dựng lệnh, sửa trên bản sao, kiểm state token và mở lại đọc kết quả trước khi lưu.
8. **OCR:** GOT gặp access violation khi cấp phát attention lớn. Đã chia theo khối truy vấn, giữ toán học tương đương và có kiểm so sánh số học. Bổ sung đối chiếu dòng tiếng Việt bằng OCR vi/en cục bộ, giữ số, bảng và khối mã; không dùng LLM để đoán chữ.
9. **Tin tức:** model chép sai dấu tiêu đề. Đã thêm `save_news_digest`: model chọn URL và viết tóm tắt; chương trình lấy nguyên tiêu đề/URL/ngày từ feed đã đọc, từ chối URL lạ, tin quá hạn và trùng. JSON/Markdown được đọc lại sau lưu.

## Kiểm tra tự động và giới hạn

- `tests-4.log`: **584/584**; bao gồm phạm vi quyền, từ chối thao tác ngoài phạm vi, hash cũ, khôi phục lỗi, ID đính kèm, đọc bằng chứng dài, UI/composer, lưu trữ và lịch sử.
- OCR bridge: **15/15** (`ocr-contract` và lần chạy sau bổ sung kiểm giữ số/bảng/code). GOT attention: so sánh kết quả/tensor attention với hàm gốc ở nhiều kích thước, có nhiều khối và có/không relative position.
- Các báo cáo `contracts-*`, `final-mb-*` kiểm runtime, quyền, completion gate, đọc bằng chứng, repair, discovery, bounded context, extension bus, MCP và Office/Desktop protocol. Đây là các bài kiểm tra hợp đồng có fixture, được phân biệt với ứng dụng thật trong bảng trên.
- AutoCAD mới hỗ trợ **tệp đóng** với LINE/CIRCLE/TEXT và DXF. Chưa kết nối plugin xử lý block/attribute đang chọn trong AutoCAD GUI; bài hợp đồng selected-block không phải bằng chứng plugin native đã triển khai.
- Chưa thử tài khoản Gmail/Drive/Slack hoặc dịch vụ ngoài có đăng nhập; không tự tạo kết nối hay gửi dữ liệu cho người khác để kiểm thử. Trình duyệt/chuột/phím native chưa xác nhận do Windows chặn đầu vào ở phiên này.
- OCR mới kiểm mẫu một trang Anh/Việt. Chưa chứng nhận mọi tài liệu nhiều trang, chữ viết tay, bản vẽ scan hoặc bố cục phức tạp. Thời gian xử lý phụ thuộc máy.
- Giao diện mới có bằng chứng ở [biên bản giao diện](H2_AGENT_DOCUMENTS_IMPLEMENTATION.md). Lượt sửa này kiểm tương tác OCR bằng bộ kiểm tra; chưa xác nhận lại chuột thật trong phiên desktop đang chặn input. Không chứng nhận khớp 100% từng pixel.

## Cần kiểm tiếp khi Excel hết chặn

Đóng thông báo trùng tên trong Excel, rồi chạy lại ba tình huống `excel-fixed.json`: sửa B2, giữ công thức B4, tính lại ra 45, giữ D1/định dạng, lưu bản sao và đọc lại. Bộ mẫu và log lỗi đã được giữ để tiếp tục đúng các trường hợp này.

Bản Release Windows x64 tự kèm .NET đã build. Xem [thông tin gói portable](H2_PORTABLE_BUILD_2026-09-22.md) cho bản cập nhật và hash cuối. Không chứa API key, thiết lập máy hay tài liệu cá nhân; Office/AutoCAD và kết nối model là phụ thuộc bên ngoài.

## Bổ sung sau phản hồi lỗi Word/CV

Đã sửa thêm đường sửa nhiều đoạn, ghi nhận phục hồi lỗi và thời gian đọc Word dài. **588/588** kiểm tra ứng dụng, **3/3** lượt phục hồi trên Word thật, **3/3** lượt CV với Gemma 4 Cloud và **54/54** điều kiện đọc lại DOCX đạt. Xem [biên bản Word/CV](H2_WORD_CV_REPAIR_2026-09-22.md). Các số liệu và giới hạn phía trên là của vòng kiểm tra trước bổ sung này.
