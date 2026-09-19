# H2 Notes: đặc tả sản phẩm đã chốt

> **HISTORICAL / CURRENT-IMPLEMENTATION BASELINE**
>
> Tài liệu này được giữ để bảo toàn yêu cầu, hành vi và bằng chứng UI của bản H2 Notes hiện tại. Nó không còn là kiến trúc tương lai chuẩn. Kiến trúc tương lai hiện hành: `docs/H2_PRODUCT_MASTER_SPEC.md`; tracker thực hiện: `docs/H2_PRODUCT_MASTER_TASKS.md`; lỗi dữ liệu/NAS độc lập: `docs/H2_NOTES_NON_AI_BUG_LEDGER.md`.

Bổ sung 16/09/2026 (Word giữ bố cục và chờ máy yếu): thêm thao tác **Word giữ bố cục từ PDF/ảnh** trong +/@ và bản nháp AI tham chiếu ID tệp nguồn, không dựng bố cục từ Markdown hoặc tự tạo tệp rỗng. Dựng trên máy bằng MinerU, dùng bộ nhận chữ vi/en đã cài nếu có; không tự tải model, không gửi tài liệu lên dịch vụ khác. Bản thử tối đa 10 trang, chữ nằm trong các khung chỉnh sửa được; bảng, dấu/chữ ký và vùng chưa nhận dạng giữ ảnh, không hứa giống 100% hay bảng đã sửa được. Cho đối chiếu/sửa chữ, font/cỡ/đậm/nghiêng trước khi người dùng chọn nơi lưu; nguồn gốc không bị sửa. Phông scan chỉ ước lượng (hiện Times New Roman/Arial). Bản Markdown lịch sử thiếu nguồn phải chọn lại tệp gốc và kiểm hash. AI chat mặc định chờ hoàn tất, không tự ngắt vì model nạp/suy nghĩ lâu; Dừng và mất kết nối vẫn kết thúc phiên. Khi tắt tùy chọn chờ, chỉ dùng giới hạn im lặng, có đặt lại khi nhận suy nghĩ, câu trả lời hoặc heartbeat. Không thay bộ ảnh chuẩn và không thay chính sách quyền/ghi đè.

Bổ sung 15/09/2026 (sửa OCR ảnh và cuộn suy nghĩ): người dùng chọn bật OCR cho ảnh tài liệu. Thêm tùy chọn máy-local **OCR cả ảnh đính kèm** bên cạnh engine PDF; không âm thầm áp dụng cho cấu hình khác. Khi bật, ảnh mới được trích thành Markdown trước khi gửi LLM, giữ nguyên ảnh và chữ OCR trong lịch sử; ảnh cũ chuyển cho lượt gửi, không sửa lịch sử. Chặn gửi thiếu nội dung khi OCR lỗi/hủy. Cần ghi rõ OCR chỉ trích chữ/bảng, không thay phân tích ảnh và không bảo đảm đúng tiếng Việt. Khung suy nghĩ phải theo dòng mới khi dài, mở lại hoặc đổi kích thước; tiếp tục xóa tiến trình khi trả lời/dừng/lỗi. Không đổi bố cục chuẩn hoặc các quy tắc dữ liệu/quyền trước đó.

Bổ sung mới nhất 15/09/2026 (thanh soạn một hàng): luôn giữ +, quyền, model, mic và gửi/dừng trên một hàng dưới ô nhập. Khi thiếu chỗ, quyền chỉ còn biểu tượng khiên có tooltip; không chia thành hai hàng hay thu nhỏ chữ. Model và mức suy luận dùng chung một nút, mở thẻ gọn gồm model đã lưu, thanh chọn các mức được hỗ trợ và đặt lại mặc định. Tên dài rút gọn có tooltip đầy đủ. “Chỉ lưu mốc, không hỏi AI” và “Xem dữ liệu gửi” nằm trong Ngữ cảnh của + và @, không còn nút/checkbox riêng ngoài khung chat. Quy tắc này thay cách chia hàng trong chặng trước bên dưới.

Bổ sung 15/09/2026 (thanh soạn và PDF): người dùng yêu cầu riêng thanh soạn theo ảnh Codex mới, không thay bộ 10 ảnh bố cục tổng thể. Vùng nhập trên, nút +/quyền/model/mức suy luận/mic/gửi dưới; cửa sổ hẹp chia hàng và đưa tùy chọn phụ vào +. Ba quyền có thực thi: Chỉ đọc, Xác nhận thay đổi, Toàn quyền dự án (chỉ nối ghi chú/thêm công việc, không phải quyền hệ điều hành). Quyền tự áp dụng cần cấp tại máy này, không lấy từ JSON dự án nhập về. Mức suy luận dựa trên model/endpoint đã xác nhận; model chưa biết chỉ hiện Tối đa và không tự gửi tham số chưa hỗ trợ. Mic mở Windows voice typing, không tự gửi tin, không phải bộ ghi âm/chép lời riêng của H2 Notes. PDF mặc định gửi bản gốc đến model nhận PDF; GOT-OCR 2.0/MinerU/Docling là lựa chọn xử lý cục bộ thành Markdown. Không tự bỏ PDF hay chuyển dịch vụ khi lỗi; không tự tải model lúc gửi. Xem [báo cáo chặng này](D:/VSstudio/Nodepad/docs/ui-verification/2026-09-15-composer-pdf/REPORT.md).

Bổ sung theo yêu cầu 15/09/2026 (chat/input/LAN): hỗ trợ HTTP cho Ollama ở IP mạng riêng do người dùng nhập, ghi rõ nơi xử lý; bấm Gửi là gửi ngay theo phạm vi đang chọn, bỏ hộp xác nhận dữ liệu từng lượt nhưng vẫn có Xem dữ liệu gửi tùy chọn. Giao diện soạn có nút +, dán ảnh clipboard và thả tệp/liên kết file cục bộ; @ gợi ý thao tác/ngữ cảnh đã hỗ trợ, không tự gửi hoặc tự thực thi thay đổi. Tiến trình/suy nghĩ do provider cung cấp được hiển thị tạm, mở/thu gọn, xóa khỏi giao diện khi có câu trả lời/dừng/lỗi; không lưu/replay vào lịch sử. Với API đóng, chỉ yêu cầu bản tóm tắt công khai khi model hỗ trợ, không đòi nội dung suy nghĩ ẩn. Các quy tắc xác nhận ghi đè/xóa/áp dụng kết quả vào dự án vẫn giữ nguyên.

Bổ sung theo yêu cầu 15/09/2026: AI nhận ảnh/Word/Excel/tệp văn bản, tạo bản nháp tệp để người dùng lưu và đọc đầy đủ dữ liệu dự án đang chọn theo phạm vi xác nhận. Đã triển khai bước đầu, kho schema 4 có migration từ 2/3. Phạm vi, giới hạn và hướng dẫn tại [AI tài liệu](D:/VSstudio/Nodepad/docs/AI_DOCUMENTS.md); chưa đồng nghĩa với trợ lý toàn quyền đọc mọi tệp hay quan sát công việc ngoài app.

Ngày chốt: 15/09/2026. Trạng thái: **đã có bản triển khai đầu tiên responsive/AI/kho dữ liệu mới; chưa nghiệm thu đầy đủ**. Xem [tiến độ và giới hạn thực tế](D:/VSstudio/Nodepad/docs/RESPONSIVE_IMPLEMENTATION.md). Các yêu cầu bên dưới vẫn giữ nguyên, không tự bỏ yêu cầu chưa hoàn thành.

Tài liệu này là nguồn yêu cầu lịch sử/current-baseline của bản cũ; không ghi đè Product Master. [Mô tả bản thử đang có](D:/VSstudio/Nodepad/docs/PROJECT_SHEET_IMPLEMENTATION.md) là lịch sử triển khai, không phải bằng chứng đã hoàn thành các yêu cầu dưới đây. Giữ Avalonia/.NET và chỉ tận dụng chọn lọc Nera; không chuyển lại WPF/WinForms hay nhúng nguyên engine bảng tính.

## 1. Chuẩn giao diện bắt buộc

- Bộ ảnh chuẩn: [10 ảnh responsive hybrid](D:/VSstudio/Nodepad/docs/ui-concepts/2026-09-15-responsive-hybrid/README.md), không dùng các hướng 1/3/4 cũ để thay thiết kế.
- Giữ màu ivory/terracotta, thương hiệu H2 Notes, thanh biểu tượng, vị trí danh sách, vùng công việc/ghi chú, AI nổi/ghim và menu như ảnh tương ứng.
- Giữ ảnh gốc. [BASELINE.json](D:/VSstudio/Nodepad/docs/ui-concepts/2026-09-15-responsive-hybrid/BASELINE.json) ghi tên, kích thước tệp và SHA-256 để phát hiện thay đổi chuẩn ngoài ý muốn. Không cập nhật chuẩn để che lỗi giao diện.
- Sau từng phần UI, chạy app, chụp trạng thái tương ứng, so sánh cạnh nhau, ghi kết quả theo [bảng nghiệm thu](D:/VSstudio/Nodepad/docs/UI_ACCEPTANCE.md). Build thành công không thay thế kiểm tra trực quan.
- Chữ trong dữ liệu mẫu và nội dung hội thoại chỉ là ví dụ. Ảnh sinh có một số khác biệt nhỏ về nhãn/icon/khoảng cách; chuẩn hóa theo bộ thành phần chung, nhưng không tự thay bố cục, màu, mật độ hay cách dùng đã chốt. Thay đổi đáng kể phải xin chốt lại và tạo bản ảnh mới, không ghi đè ảnh chuẩn.
- Kích thước khởi điểm: tối thiểu 560 x 600 DIP; hẹp dưới 900; vừa từ 900; đủ ba vùng khoảng 1360 trở lên. Đây là mốc triển khai ban đầu phải đo ở DPI 100/125/150/200%, không phải kích thước pixel của ảnh PNG.
- Hẹp: danh sách qua bộ chọn; cửa sổ thấp chuyển Công việc/Ghi chú bằng tab. Đủ rộng hiện danh sách chi tiết. Toàn màn hình ghim AI bên phải, vẫn cho ẩn/tách/kéo xuống dưới. Thiếu chỗ thì thu danh sách trước, không ép vùng soạn quá nhỏ.
- Ghi nhớ bản nháp, lựa chọn, undo, vị trí cuộn, dự án hiện tại và bố cục khi resize. AI bị người dùng ẩn không bật lại liên tục khi kéo cửa sổ.
- Giữ toàn bộ hành vi cũ đã yêu cầu: định dạng vùng chọn/toolbar/menu đồng bộ, checkbox ở vùng STT, Next từ việc chưa xong đầu tiên, auto-height theo cột, tooltip đủ chữ, kéo thả chỉ commit lúc thả, ưu tiên dự án, note thường riêng, tray, X chỉ ẩn, không taskbar, opacity, snap, startup/khôi phục phiên.

## 2. Cài đặt thư mục lưu

Yêu cầu người dùng: chọn thư mục ngoài mặc định; thư mục chưa có dữ liệu hỏi có đồng bộ sang không; thư mục có dữ liệu hỏi Đồng bộ / Ghi đè / Không làm gì.

UI nằm trong **Cài đặt > Dữ liệu và lưu trữ**: đường dẫn đang dùng, Chọn thư mục, Mở thư mục, Trở về mặc định, Nhập từ app cũ. Không bật chuyển thư mục chỉ vì người dùng duyệt qua một đường dẫn.

### Luồng và ý nghĩa thao tác

Bảng này làm rõ cách xử lý để triển khai, chưa thực hiện bất kỳ chuyển/ghi đè nào:

| Đích được chọn | Lựa chọn | Hành vi |
| --- | --- | --- |
| Chính kho đang dùng | Không cần chuyển | Báo đang dùng thư mục này, không copy hoặc sinh dữ liệu trùng. |
| Chưa có dữ liệu H2 Notes | Đồng bộ sang | Sao chép dữ liệu hiện tại sang đích; kiểm chứng xong mới dùng đích. Kho cũ còn nguyên. |
| Chưa có dữ liệu H2 Notes | Không đồng bộ | Dùng kho mới trống; xác nhận rõ dữ liệu cũ vẫn ở đường dẫn cũ, không bị xóa. |
| Có dữ liệu H2 Notes hợp lệ | Đồng bộ | Hợp nhất kho hiện tại và kho đích vào đích. Giữ dữ liệu không xung đột của cả hai, không âm thầm chọn một bản thắng. |
| Có dữ liệu H2 Notes hợp lệ | Ghi đè | Dùng bản chụp kho hiện tại thay phạm vi dữ liệu H2 Notes ở đích sau preview, sao lưu và xác nhận riêng. Không động tới tệp không thuộc H2 Notes. |
| Có dữ liệu H2 Notes hợp lệ | Không làm gì | Không sao chép/hợp nhất/ghi đè; chuyển sang đọc và dùng dữ liệu đã có tại đích. Nút phải kèm chú thích này. Kho cũ không đổi. |
| Bất kỳ bước nào trước commit | Hủy | Giữ thư mục đang dùng và dữ liệu hiện tại; không đổi cấu hình. |
| Có tệp H2 Notes hỏng hoặc schema mới hơn | Dừng chuyển | Báo tệp lỗi, không coi thư mục là rỗng, không khởi tạo đè. |

Ý nghĩa “Không làm gì” trên là không tác động dữ liệu trong lúc chuyển, khác với Hủy chuyển thư mục. Sau khi xác nhận dùng kho đích, các chỉnh sửa mới của người dùng mới lưu vào kho đó. Nếu chỉ có tệp không thuộc H2 Notes, nêu rõ phát hiện này và coi là chưa có kho H2 Notes; không sửa các tệp đó.

“Đồng bộ” trong hộp thoại chuyển kho là **hợp nhất một lần rồi dùng một thư mục chính**; không ngầm tạo dịch vụ đồng bộ hai thư mục hoặc nhiều máy chạy mãi về sau.

### An toàn và xung đột

- Trước chuyển: hoàn tất hoặc hủy rõ bản nháp, dừng phát sinh ghi nền mới; tác vụ AI đang chạy phải dừng/chờ và giữ kết quả dang dở, không cho kết quả muộn ghi nhầm kho.
- Preview hiện đầy đủ đường dẫn nguồn/đích, số dự án/note, số thêm mới/trùng/xung đột/thay thế và vị trí backup. Ghi đè cần xác nhận riêng, không chọn sẵn.
- Ghép theo ID ổn định, không theo tên hay thời gian sửa file. Hai dự án trùng tên nhưng khác ID vẫn là hai dự án.
- Dự án cùng ID/nội dung cùng hash chỉ giữ một. Có revision chung thì hợp nhất các mục độc lập; xung đột cùng trường/rich text/thứ tự/task/trạng thái xóa/hội thoại phải cho chọn Giữ bản hiện tại / Giữ bản ở đích / Giữ cả hai.
- Không nối cơ học hai tài liệu rich text hoặc hai thứ tự checklist. Giữ cả hai dự án xung đột phải cấp ID bản sao và ánh xạ lại các liên kết nội bộ; không sinh hai dự án hoạt động cùng ID.
- Tin nhắn có ID, conversation ID, thứ tự và nhánh hội thoại; không nhân đôi lần nhập/merge tiếp theo. Không hợp nhất nhánh chat chỉ bằng timestamp.
- Lưu bản sao an toàn trước khi đổi cả dữ liệu nguồn đang mở và phần đích sẽ thay; không xóa nguồn sau chuyển. Không copy backup/cache/khóa API như dữ liệu dự án.
- Kiểm tra quyền ghi, dung lượng, file lock, đường dẫn chuẩn hóa, thư mục nguồn/đích lồng nhau hoặc trỏ cùng nơi qua liên kết. Chỉ scan dữ liệu H2 Notes đúng phạm vi, không duyệt rồi sao chép mọi tệp trong ổ đĩa.
- Staging + nhật ký chuyển kho + commit có phục hồi. Chỉ đổi đường dẫn hoạt động sau khi mọi tệp/manifest được kiểm chứng. Mất điện, bị hủy, lỗi I/O hoặc đầy đĩa không để kho nửa cũ nửa mới; khôi phục được từ checkpoint.
- Khóa chống hai process ghi cùng kho. Nếu phát hiện file thay đổi ngoài app thì kiểm revision, báo xung đột; không ghi đè dựa trên bản trong RAM đã cũ.
- Thư mục ngoài bị ngắt kết nối: báo không lưu được, giữ bản nháp phục hồi cục bộ và cho thử lại. Không tự đổi sang kho trống hoặc báo Đã lưu giả.
- Không thay thư mục lưu/cài đặt nhạy cảm chỉ vì nhập một file dự án do bên ngoài cung cấp.

## 3. Mỗi dự án một tệp

Yêu cầu: toàn bộ dữ liệu thuộc dự án ở một tệp mang tên dự án, kể cả công việc, ghi chú có định dạng, hội thoại AI, đường dẫn và thông tin liên quan.

### Định dạng triển khai dự kiến

Một tệp UTF-8 JSON có schema version, đuôi **.h2project.json**. Ví dụ:

`Điện Hạt Nhân I - Ninh Thuận--a17c92de.h2project.json`

Tên dễ đọc theo tên dự án, thêm hậu tố ID để tránh trùng. STT không thuộc tên file vì thứ tự có thể đổi. Lọc ký tự tên file không hợp lệ, tên dành riêng, dấu chấm/khoảng trắng cuối và chiều dài đường dẫn; ID thực trong nội dung là khóa chính. Kiểm collision kể cả khi hậu tố rút gọn trùng. Đổi tên file giao dịch an toàn, không làm đứt ID/liên kết/lịch sử; nếu lỗi giữ tên cũ và báo rõ.

| Trong tệp dự án | Nội dung cần giữ |
| --- | --- |
| Nhận dạng | schema version, project ID, tên, rich name, board/group ID, revision, timestamps, nguồn import. |
| Công việc | ID, thứ tự, nội dung/ghi chú rich text, trạng thái tích, thông tin bổ sung và dữ liệu xóa cần cho merge. |
| Ghi chú | Toàn bộ rich document, liên kết, phần raw từ app cũ chưa chuyển đủ, trường chưa nhận diện để round-trip. |
| AI | Nhiều cuộc hội thoại, ID tin nhắn/parent/nhánh, role, nội dung, model/provider identifier, ngữ cảnh đã chọn/gửi, bản nháp, trạng thái dừng/lỗi và kết quả đã được người dùng áp dụng. |
| Tham chiếu | Đường dẫn file/thư mục, URL, nhãn và metadata liên quan; không tự mở/chạy khi đọc tệp. |
| Trạng thái riêng | Thu gọn, ưu tiên, phần bố cục riêng dự án, vị trí đang đọc nếu phù hợp. |

Chỉ lưu nội dung hội thoại/kết quả API thực nhận cần cho tính liên tục; không yêu cầu hay bịa suy nghĩ nội bộ không được API cung cấp. Dữ liệu xác thực không thuộc tệp dự án.

Đường dẫn tới CAD/PDF/ảnh ngoài được lưu dưới dạng tham chiếu; không mặc nhiên nhúng/sao chép bản thân các tệp ngoài vào JSON. Khi đổi máy, đường dẫn không tồn tại phải có thao tác Liên kết lại, không giả rằng file vẫn mở được. Nếu sau này cần gói cả file đính kèm thành một tệp mang đi, chốt định dạng gói riêng trước.

### Phần lưu chung và hiệu năng

- Thư mục `projects` chứa tệp dự án. `notes` chứa note thường theo tệp riêng, không ép vào một dự án giả.
- Chỉ mục kho lưu workspace/board, thứ tự project ID và ánh xạ ID -> tên file; không nhân đôi toàn bộ nội dung các dự án. Có quy tắc phục hồi chỉ mục bằng ID trong tệp khi chỉ mục lỗi.
- Cấu hình máy, đường dẫn kho đang dùng, vị trí các cửa sổ theo màn hình và tham chiếu khóa bí mật ở vùng ứng dụng cục bộ; cấu hình bố cục chung có thể di chuyển nhưng không ghi đè mù vị trí của máy khác.
- Mỗi lần chỉnh chỉ ghi dự án/note đã thay đổi. Không serialize toàn kho khi gõ, chọn chữ, hover, resize hoặc nhận từng token AI.
- Dùng snapshot ổn định, debounce, ghi nền theo revision, file tạm/flush/atomic replace và backup. Lưu chat stream theo checkpoint rồi flush khi xong/dừng/thoát; lỗi lưu phải hiển thị.
- Với chat dài, chỉ render/nạp phần cần xem, kiểm tra thời gian lưu tệp lớn. Không âm thầm cắt/xóa lịch sử để giảm dung lượng.
- Chuyển từ `project-sheet-v1.json` và import WPF là luồng migration có preview/backup; giữ ID, thứ tự, rich text, note thường và phiên cửa sổ. Nguồn cũ không bị sửa hoặc xóa.
- Có schema version mới và regression test; không dùng lại schema v1 để chứa cấu trúc khác rồi bắt bản cũ ghi đè.

## 4. Thiết lập AI riêng

### Bổ sung đã yêu cầu: lịch sử và cửa sổ AI dự án (15/09)

- Nút Gửi/Dừng ở cạnh ô soạn, cùng một hàng. Tin người dùng bên phải, AI bên trái.
- Mỗi tin có giờ nhỏ, hover xem ngày/giờ đầy đủ; nhãn ngăn nhóm theo ngày và khoảng nghỉ. Tin cũ thiếu giờ không được gán giờ mới.
- Có chế độ chỉ lưu mốc công việc tại thời điểm hiện tại, không gọi AI và không tự đưa các mốc riêng tư này vào lịch sử gửi API sau đó.
- Đính chính theo người dùng: **độc lập là cửa sổ, không phải hội thoại ngoài dự án**. Cửa sổ AI desktop có thể ghim khi làm tài liệu khác nhưng luôn dùng dự án đang chọn, chung lịch sử/bản nháp/ngữ cảnh với khung AI trong bảng. Đổi dự án ở bảng hoặc bộ chọn trong cửa sổ AI đều cập nhật cùng dự án; tên dự án luôn hiển thị rõ.
- Tách/ghép di chuyển cùng một editor và request, không tạo bản sao hội thoại hay gửi lại. X của bảng không đóng cửa sổ AI đang tách; X của AI chỉ ẩn. Khôi phục phiên giữ dự án đang chọn và vị trí/ghim cửa sổ AI.
- Bản triển khai trước đã hiểu nhầm thành chat riêng. Những hội thoại đó được giữ trong mục tray “Chat ngoài dự án đã lưu ở bản trước”, không tự xóa hoặc gộp vào dự án. Các điểm mở AI mới đều mở cửa sổ dự án, không tạo thêm notebook ngoài dự án.
- X chỉ ẩn, mở lại giữ lịch sử; lưu vị trí/kích thước/ghim và khôi phục cùng phiên desktop. Đổi model không xóa lịch sử.
- Đã triển khai UI/lưu trữ bước này, chưa phải trợ lý có quyền đọc tệp hoặc quan sát phần mềm khác. Kho nâng từ schema 2 lên 3 có backup/rollback để chặn bản cũ hiểu sai mốc chỉ lưu cục bộ.

UI **Cài đặt > AI** và biểu tượng bánh răng ở khung chat mở cùng phần cấu hình. Dùng phong cách ảnh chuẩn; trang cài đặt mới chưa có ảnh chuẩn riêng, cần mô phỏng/đối chiếu trước khi nghiệm thu UI phần này.

### Ollama trên máy

- Loại kết nối: Ollama. Có địa chỉ máy chủ, Kiểm tra kết nối, Làm mới danh sách, chọn model, Nạp model và Giải phóng model. Mặc định ưu tiên localhost, không tự chuyển sang dịch vụ cloud.
- Hiện danh sách model đã cài, phân biệt đã cài trên đĩa và đang nạp bộ nhớ; không hardcode vài tên model vào UI.
- Nạp model khi người dùng yêu cầu, có trạng thái đang nạp/sẵn sàng/lỗi và thời gian giữ trong bộ nhớ. Hiển thị khung AI hoặc startup app không tự nạp model nặng.
- Tải model chưa có là thao tác riêng, báo tên/kích thước nếu biết và xin xác nhận trước khi tải. Không nhầm Load với download, không tự tải hoặc xóa model.
- Nếu Ollama chưa chạy, không có model hoặc thiếu bộ nhớ, báo rõ và hướng dẫn; không làm đơ giao diện và không âm thầm gửi sang API trả phí.
- Nếu người dùng nhập máy chủ LAN/từ xa hoặc chọn model cloud, nhãn phải phản ánh đúng nơi xử lý; không gọi dữ liệu là “chỉ trên máy” khi thực tế gửi đi nơi khác.

Ollama có API liệt kê model và API phân biệt model đang chạy; FAQ mô tả cơ chế giữ/nạp/giải phóng model. Đây là cơ sở cho thiết kế, chưa gọi Ollama trên máy người dùng: [danh sách model](https://docs.ollama.com/api/tags), [model đang chạy](https://docs.ollama.com/api/ps), [quản lý bộ nhớ model](https://docs.ollama.com/faq).

### API online nhiều nhà cung cấp

- Preset ban đầu: OpenAI, Google Gemini, DeepSeek; thêm hồ sơ Tùy chỉnh thay vì giới hạn chỉ ba hãng.
- Mỗi hồ sơ có tên hiển thị, loại giao thức, Base URL/endpoint, API key, model, timeout và tham số được nhà cung cấp hỗ trợ. Có nhiều hồ sơ, chọn mặc định chung và chọn lại cho từng dự án/chat.
- Có Kiểm tra kết nối, lấy danh sách model khi API hỗ trợ, và nhập model thủ công nếu endpoint không hỗ trợ liệt kê. Không suy rằng model nhìn thấy có nghĩa tài khoản chắc chắn gọi được.
- Bộ kết nối tách khỏi UI: Ollama native, OpenAI Responses, OpenAI-compatible Chat Completions, Gemini native và adapter bổ sung cho API khác chuẩn. Preset phải chọn đúng giao thức đã kiểm, không đoán giao thức chỉ từ tên hãng.
- “Bất kỳ hãng” nghĩa kiến trúc mở rộng, không hardcode vendor vào chat. API có giao thức tương thích dùng hồ sơ tùy chỉnh; API có định dạng/xác thực khác phải thêm adapter và kiểm chứng. Không hứa mọi endpoint đều hoạt động chỉ bằng đổi URL/key.
- Giao diện chỉ hiện chức năng/tham số model hỗ trợ; không âm thầm bỏ qua thiết lập rồi báo thành công. Xử lý streaming, dừng, timeout, lỗi xác thực, quota/rate limit và kết quả dở dang.
- Thử kết nối/liệt kê không gửi nội dung dự án. Nếu cần câu thử có tính phí, dùng câu mẫu và người dùng chủ động chạy; không tự thử hàng loạt model/provider.
- Chuyển local/online/model không xóa lịch sử. Tin nhắn ghi model/provider đã dùng; dừng request cũ trước khi chuyển cấu hình, chặn callback muộn gắn sai dự án.
- Không tự retry vô hạn hoặc đổi nhà cung cấp sau lỗi; tránh gửi trùng nội dung và phí phát sinh.

OpenAI có giao thức Responses riêng; Gemini và DeepSeek có lớp tương thích nhưng không có nghĩa mọi chức năng giống nhau. Vì vậy chọn adapter và kiểm khả năng là quyết định kỹ thuật của H2 Notes, không cam kết từ nhà cung cấp. Nguồn đối chiếu ngày 15/09/2026: [OpenAI Responses](https://developers.openai.com/api/docs/guides/migrate-to-responses), [Gemini compatibility và giới hạn](https://ai.google.dev/gemini-api/docs/openai), [DeepSeek API](https://api-docs.deepseek.com/).

### Khóa và phạm vi dữ liệu

- API key là bí mật theo máy/người dùng, lưu qua kho bảo vệ của Windows; không lưu plaintext trong project JSON, chỉ mục kho, backup xuất ra hoặc log. Tệp dự án chỉ tham chiếu hồ sơ không chứa bí mật.
- Không hardcode khóa của nhà phát triển. Người dùng nhập khóa của họ trong app; không yêu cầu dán khóa vào cuộc trò chuyện này. Đổi máy phải nhập/cấp lại khóa phù hợp.
- Dùng HTTPS cho API online; ngoại lệ localhost cho Ollama. Thay endpoint phải xác nhận lại nơi nhận trước khi dùng khóa/ngữ cảnh; không chuyển tiếp Authorization sang host khác qua redirect không tin cậy.
- Ngữ cảnh mặc định chỉ dự án/đoạn đang chọn; có Xem dữ liệu gửi và bỏ chọn. Không tự quét tài liệu ngoài theo đường dẫn hoặc gửi tất cả dự án cho AI.
- Nội dung import/chat/tệp là dữ liệu, không được ghi đè cấu hình kết nối hay cấp quyền cho AI. Mọi thay đổi công việc/ghi chú từ AI cần preview và người dùng bấm áp dụng; có undo.
- Có thao tác quản lý/xóa lịch sử với xác nhận và phạm vi rõ. Không tuyên bố xóa trên server nhà cung cấp chỉ vì xóa bản lưu trong máy.

## 5. Thứ tự triển khai và cổng kiểm tra

Đây là thứ tự công việc, không phải lịch tự chạy hoặc automation.

| Chặng | Phạm vi | Điều kiện trước khi đi tiếp |
| --- | --- | --- |
| 0 | Khóa bộ ảnh, đặc tả, checklist | Đã ghi nhận trong đợt này; không đánh dấu app đã có tính năng. |
| 1 | Kho từng dự án, migration/import | Round-trip và backup/rollback trên bản sao dữ liệu; không mất rich text/chat/ID. |
| 2 | Chọn kho, merge/overwrite/no-op | Test đầy đủ các nhánh lựa chọn, hỏng file, khóa, hết dung lượng, mất kết nối. |
| 3 | Shell responsive, danh sách, công việc/notes | Ảnh đối chiếu 01/02/03/05/09/10; DPI/IME, định dạng và thao tác cũ không thoái lui. |
| 4 | Khung AI, cấu hình Ollama và API | Ảnh 04/06/07, UI cài đặt bổ sung, test adapter; smoke test thật chỉ với kết nối được người dùng cấu hình. |
| 5 | Kéo/ghim/khôi phục bố cục | Ảnh 08, preview/drop/cancel, giữ bản nháp/selection/lịch sử. |
| 6 | Hồi quy toàn app | Tray/opacity/startup/snap, restart, lưu tệp lớn, chat dài, đo độ trễ nhập; mở app cho người dùng kiểm. |

Không tự nhận một chặng hoàn thành bằng ảnh sinh. Sau các lần build triển khai thành công, mở đúng bản app mới cho người dùng như yêu cầu trước; tôn trọng dữ liệu và không chạy hai bản cùng ghi kho. Đợt ghi đặc tả này không build/restart vì không thay mã ứng dụng.
