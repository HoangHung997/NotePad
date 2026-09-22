# H2 Agent — Tổng hợp rà soát và mục tiêu cần làm

> Đã hợp nhất vào [báo cáo hiện trạng và giải pháp đầy đủ](H2_AGENT_CONSOLIDATED_REVIEW_AND_SOLUTION_2026-09-22.md) ngày 22/09/2026. Giữ bản này để truy vết; dùng bản hợp nhất để đọc kết luận và giải pháp hiện hành của đợt rà soát.

Ngày: 22/09/2026. Mã nguồn đối chiếu: `9265c75c4be60e4019d655ef049f66ffc6f779bc`.

**Giai đoạn hiện tại: rà soát và tổng hợp yêu cầu; chưa triển khai.** Người dùng yêu cầu không sửa code trong giai đoạn này. Chưa cài Agent bên ngoài, thay backend, build bản mới hoặc đẩy GitHub. Các thay đổi của đợt nghiên cứu hiện tại chỉ thuộc tài liệu.

## 1. Mục tiêu sản phẩm đã được làm rõ

H2 cần một nền tảng Agent tổng quát, độc lập với model, có thể phối hợp tệp, mã/chương trình, web, trình duyệt, ứng dụng desktop và dịch vụ. Thành phần công cụ, bộ kết nối, bộ nén ngữ cảnh và bộ máy thực thi phải có ranh giới rõ để bổ sung hoặc thay thế về sau.

Agent phải làm việc dài, tiếp nhận lời nhắn bổ sung, kiểm chứng kết quả, lưu việc đang làm và giảm ngữ cảnh đầu vào mà vẫn truy hồi được chính xác những việc đã thực hiện. Không xem một bản tóm tắt bằng văn xuôi là nguồn sự thật duy nhất.

Tương đồng với Codex được đánh giá bằng hành vi thực tế và khả năng mở rộng. Không đặt tiêu chí mơ hồ “làm mọi việc 100%” hoặc chỉ đếm số công cụ có tên trong danh sách.

Đặc tả chuẩn: [Agent Master](H2_AGENT_MASTER_SPEC.md), [Chat Surface](H2_AGENT_CHAT_SURFACE_SPEC.md). Đối chiếu mã nguồn bên ngoài: [báo cáo Open Interpreter/Cua/UFO](H2_DESKTOP_AGENT_COMPARISON_2026-09-22.md). Các dự án đó là nguồn tham khảo hoặc ứng viên thay thế từng thành phần, chưa phải lựa chọn tích hợp đã nghiệm thu.

## 2. Phát hiện từ đường chạy hiện tại

“Xác nhận trong mã” không có nghĩa đã tái hiện lỗi trên ứng dụng. Báo cáo này không thay cho kiểm thử chức năng trực tiếp.

| Mã | Phát hiện | Ý nghĩa |
|---|---|---|
| A01 | H2 đã có ToolRegistry, tìm/nạp công cụ, ICapabilityProvider, IAgentExtension, model transport, kiểm chứng và quyền theo tác vụ | Có nền tảng để hoàn thiện; không cần mặc định viết lại toàn bộ |
| A02 | App khởi tạo H2ProductionAgentAdapter. Đường này tự tạo AgentContextInput và chạy runtime; chưa thấy gọi RuntimeCompactionCoordinator như AgentOrchestratedRun của Lab | Kết quả nén của đường Lab chưa chứng minh đường app H2 được nén tương tự |
| A03 | Work Assistant dựng lịch sử từ 6 tác vụ **Completed** gần nhất; bước snapshot còn giới hạn 32 tin | Các tác vụ lỗi/dang dở và quyết định cũ không được giữ đầy đủ chỉ bằng cửa sổ hội thoại gần đây; lưu lịch sử UI không đồng nghĩa model được truy hồi |
| A04 | BuildSummary của bộ nén hiện ghi số sự kiện user/assistant/tool cùng tham chiếu nguồn | Đã có mốc và nguồn bền vững, nhưng riêng bản tóm tắt này chưa ghi nội dung quyết định, việc đã làm hoặc việc còn thiếu |
| A05 | AgentContextBudget dùng giới hạn ký tự; AgentRuntime dựng ngữ cảnh đầu vào trước vòng chạy, trong khi Ollama/ChatCompletions tiếp tục thêm thông điệp/kết quả công cụ | Cần kiểm tra và quản lý kích thước **toàn bộ yêu cầu mỗi vòng**, không chỉ lịch sử ở đầu lượt; chưa có bằng chứng nén tự động trong một lượt dài ở đường đã đọc |
| A06 | Công cụ exec_command chạy PowerShell không tương tác, đóng stdin, đợi kết thúc; tối đa 300 giây. Lượt production đặt ngân sách 64 vòng công cụ | Có chạy mã tổng quát, nhưng chưa phải hợp đồng phiên tiến trình dài với đầu vào/đầu ra liên tục và tiếp nối tác vụ có kiểm soát |
| A07 | Khi nạp lại archive, tác vụ chưa kết thúc được chuyển sang Failed với thông báo process H2 trước đã ngừng | Có ghi nhận gián đoạn, chưa có khả năng tiếp tục công việc đã được chứng minh; không nên báo Agent vẫn chạy sau khi tiến trình đã dừng |
| A08 | Desktop gắn cửa sổ đã chọn; Office nhận diện qua một COM instance, có thời gian chờ nhận diện 3 giây; UIA đọc văn bản có giới hạn | Cần thống nhất nhận diện tài nguyên, dò khả năng và các đường đọc/thao tác. Chi tiết và giới hạn suy luận nằm ở báo cáo đối chiếu |
| A09 | Production đăng ký web.fetch/download/extract/get_metadata/read_feed. HttpWebResearchBackend được tạo không có hàm search; đường browser fallback trong backend này chỉ trả URL | Đã có tải/đọc web; chưa thể tính là tìm kiếm web và điều khiển trình duyệt hoàn chỉnh. Production hiện không quảng bá các công cụ giả này |
| A10 | Lab có phần mở rộng và các bài kiểm thử đăng ký/thay provider. Đường production được đọc tập trung cấu hình các nhóm công cụ cụ thể | Cần nghiệm thu cài/thay/tắt một phần mở rộng thật qua H2; không suy từ interface hoặc bài fixture rằng sản phẩm đã có trọn luồng |
| A11 | AgentRuntime đã có theo dõi lỗi chưa giải quyết, phản hồi yêu cầu sửa, chặn thao tác sửa thất bại lặp lại và ngân sách vòng sửa | Không thiếu hoàn toàn vòng phục hồi. Chưa có bằng chứng từ đợt rà soát rằng hệ thống tự chẩn đoán và tạo/thử bộ kết nối tương thích cho từng môi trường qua H2 thật |

### Bằng chứng nguồn nội bộ

- A01/A10: [ToolRegistry](https://github.com/HoangHung997/NotePad/blob/9265c75c4be60e4019d655ef049f66ffc6f779bc/experiments/H2AgentLab/Tools/ToolRegistry.cs), [phần mở rộng](https://github.com/HoangHung997/NotePad/blob/9265c75c4be60e4019d655ef049f66ffc6f779bc/experiments/H2AgentLab/Extensions/AgentExtension.cs), [provider](https://github.com/HoangHung997/NotePad/blob/9265c75c4be60e4019d655ef049f66ffc6f779bc/experiments/H2AgentLab/Providers/CapabilityProvider.cs).
- A02: [khởi tạo trong app](https://github.com/HoangHung997/NotePad/blob/9265c75c4be60e4019d655ef049f66ffc6f779bc/src/H2Notes.Avalonia/App.axaml.cs#L336), [ngữ cảnh production](https://github.com/HoangHung997/NotePad/blob/9265c75c4be60e4019d655ef049f66ffc6f779bc/experiments/H2AgentLab/Integration/H2ProductionAgentAdapter.cs#L344), [bộ nén trong đường Lab](https://github.com/HoangHung997/NotePad/blob/9265c75c4be60e4019d655ef049f66ffc6f779bc/experiments/H2AgentLab/Tasking/AgentOrchestratedRun.cs#L152).
- A03: [lịch sử Work Assistant](https://github.com/HoangHung997/NotePad/blob/9265c75c4be60e4019d655ef049f66ffc6f779bc/src/H2Notes.Avalonia/WorkAssistantCompactWindow.Conversation.cs#L128), [snapshot 32 tin](https://github.com/HoangHung997/NotePad/blob/9265c75c4be60e4019d655ef049f66ffc6f779bc/experiments/H2AgentLab/Integration/H2ProductionAgentAdapter.Context.cs#L25).
- A04/A05: [BuildSummary](https://github.com/HoangHung997/NotePad/blob/9265c75c4be60e4019d655ef049f66ffc6f779bc/experiments/H2AgentLab/Session/RuntimeCompactionCoordinator.cs#L186), [ngân sách ký tự](https://github.com/HoangHung997/NotePad/blob/9265c75c4be60e4019d655ef049f66ffc6f779bc/experiments/H2AgentLab/Context/AgentContextManager.cs#L11), [ngữ cảnh đầu lượt](https://github.com/HoangHung997/NotePad/blob/9265c75c4be60e4019d655ef049f66ffc6f779bc/experiments/H2AgentLab/Runtime/AgentRuntime.cs#L147), [Ollama](https://github.com/HoangHung997/NotePad/blob/9265c75c4be60e4019d655ef049f66ffc6f779bc/experiments/H2AgentLab/Transport/OllamaTransport.cs#L111), [Chat Completions](https://github.com/HoangHung997/NotePad/blob/9265c75c4be60e4019d655ef049f66ffc6f779bc/experiments/H2AgentLab/Transport/ChatCompletionsTransport.cs#L118).
- A06/A07: [tiến trình](https://github.com/HoangHung997/NotePad/blob/9265c75c4be60e4019d655ef049f66ffc6f779bc/experiments/H2AgentLab/Integration/H2LocalCommandTool.cs#L25), [ngân sách vòng](https://github.com/HoangHung997/NotePad/blob/9265c75c4be60e4019d655ef049f66ffc6f779bc/experiments/H2AgentLab/Integration/H2ProductionAgentAdapter.cs#L367), [tác vụ bị gián đoạn](https://github.com/HoangHung997/NotePad/blob/9265c75c4be60e4019d655ef049f66ffc6f779bc/experiments/H2AgentLab/Integration/H2ProductionAgentAdapter.cs#L880).
- A08: [đối chiếu kết nối và DesktopHost](H2_DESKTOP_AGENT_COMPARISON_2026-09-22.md).
- A09/A10: [cấu hình công cụ production](https://github.com/HoangHung997/NotePad/blob/9265c75c4be60e4019d655ef049f66ffc6f779bc/experiments/H2AgentLab/Integration/H2ProductionToolSession.Domains.cs#L10), [HTTP backend](https://github.com/HoangHung997/NotePad/blob/9265c75c4be60e4019d655ef049f66ffc6f779bc/experiments/H2AgentLab/Web/WebResearchHost.cs#L43).
- A11: [vòng xử lý lỗi và yêu cầu sửa](https://github.com/HoangHung997/NotePad/blob/9265c75c4be60e4019d655ef049f66ffc6f779bc/experiments/H2AgentLab/Runtime/AgentRuntime.cs#L225).

## 3. Danh sách mục tiêu cần làm

Đây là danh sách công việc đề xuất sau rà soát, chưa phải trạng thái đã triển khai. P0 là nền tảng hoặc chặn yêu cầu chính; P1 là hoàn thiện khả năng; P2 là nghiệm thu và bàn giao. Các yêu cầu quyền và kiểm chứng đi cùng từng chặng, không chờ đến cuối.

| Mục tiêu | Ưu tiên | Nội dung và điều kiện hoàn thành |
|---|---|---|
| G01 — Thống nhất đường chạy thực tế | P0 | Global Assistant và Project Agent dùng cùng hợp đồng task/context/tools/verification; giữ khác biệt về đích mặc định. Chứng minh từ thao tác UI tới provider thực, không chỉ facade của Lab |
| G02 — Hợp đồng công cụ mở | P0 | Hoàn thiện schema, phiên bản, capability, trạng thái kết nối, quyền, lỗi, tiến độ, hủy và kết quả có nguồn. Thêm/thay provider mà không sửa lõi; thực thi được một plugin thật và quay lại phiên bản trước |
| G03 — Sổ trạng thái và truy hồi công việc | P0 | Nhật ký đầy đủ + trạng thái có cấu trúc + ngữ cảnh gọn. Giữ quyết định mới nhất, dữ liệu chính xác, việc đã kiểm chứng/dang dở/thất bại và bước tiếp theo; đọc lại nguồn khi cần. Không bỏ việc lỗi chỉ vì nó không Completed |
| G04 — Nén ngữ cảnh xuyên suốt | P0 | Nén cả giữa các lượt và trong lượt dài; tính toàn bộ request gồm công cụ/ảnh và chừa ngân sách đầu ra. Thay bộ nén/provider được, checkpoint kích hoạt nguyên tử, có kiểm tra khả năng nhớ sau nhiều lần nén |
| G05 — Phiên tiến trình và tiếp tục sau gián đoạn | P0 | Job/run ID, đọc tiến độ, gửi đầu vào, chờ/hủy, lưu mốc. Khi khởi động lại, đối chiếu công việc và file thực trước khi chạy tiếp; tránh lặp thay đổi chưa rõ đã hoàn thành |
| G06 — Nhận diện và điều khiển ứng dụng tổng quát | P0 | Phân biệt process/cửa sổ/tài liệu/tệp, dò khả năng, chọn đúng đích, chuyển API/UIA/hình ảnh khi phù hợp. Giữ nguyên ý nghĩa dữ liệu: bản đang soạn khác bản lưu, đọc màn hình khác toàn văn |
| G07 — Web, trình duyệt và dịch vụ thật | P1 | Hoàn thiện search/browser session và MCP/API theo hợp đồng chung; quản lý tab/phiên, đọc nguồn và xác thực dữ liệu. Khả năng chưa cấu hình phải được báo rõ |
| G08 — Quyền và khả năng thay thế thống nhất | P0 | Cùng phạm vi quyền ở mọi bộ kết nối, tái sử dụng quyền còn hiệu lực. Đổi provider không mất task state và không mang nhầm opaque context/live handle; plugin lỗi không làm hỏng toàn bộ Agent |
| G09 — Công cụ chuyên dụng và kiểm chứng sản phẩm | P1 | Word/Excel/AutoCAD/PDF/OCR là phần mở rộng có thể thay; dùng công cụ nền khi phù hợp. Kiểm tra nội dung/định dạng và trạng thái sau sửa; trả lỗi có nguyên nhân, tránh thử lại mù |
| G10 — Hội thoại phản ánh trạng thái thật | P1 | Lịch sử, tiến độ, bằng chứng, sản phẩm, lỗi và phục hồi cùng một task. Hiển thị gọn lúc nén/tiếp tục, giữ thanh soạn và bong bóng theo thiết kế đã duyệt; không mở một đợt thiết kế UI khác |
| G11 — Bộ nghiệm thu tổng thể | P2 | Test hợp đồng + tích hợp + công việc thực qua H2. Mỗi tình huống biên chạy ít nhất ba lần; đo chất lượng kết quả và mức sử dụng ngữ cảnh. Liệt kê rõ ứng dụng/phiên bản chưa kiểm chứng |
| G12 — Đóng gói và chuyển máy | P2 | Sau khi triển khai và đạt nghiệm thu: đóng gói đầy đủ runtime/phụ thuộc được phép phân phối, kiểm tra trên máy sạch, liên kết lại tài nguyên theo máy; không mang API secret cùng dữ liệu dự án |
| G13 — Chẩn đoán và thích ứng bộ kết nối | P0 | Dò môi trường và khả năng thực tế, phân loại lỗi, thử cách phù hợp hoặc tạo bản adapter/script riêng theo nhiệm vụ khi quyền cho phép. Kiểm thử trước khi sử dụng lại; giữ bản gốc và phiên bản đã chạy ổn; không sửa mù hay lặp thao tác ghi |

G03–G05 liên quan MB-124–MB-127 mới ghi trong [tracker Agent](H2_AGENT_MASTER_TASKS.md). Các mục đó đang mở; chưa có kết quả chạy mới. Những mục G còn lại là tổng hợp định hướng, chưa tự coi là thay thế toàn bộ tracker sản phẩm hiện hữu.

### 3.1. Độ đầy đủ của từng bộ công cụ

Làm rõ từ người dùng: mỗi bộ công cụ cần bao phủ công việc thực tế, có khả năng chuyên biệt và thích ứng phiên bản. “Cấu trúc đi đúng hướng” chưa đồng nghĩa chỉ cần thêm lệnh: quản lý tài nguyên, chờ, trí nhớ, phục hồi và kiểm chứng trong lõi vẫn cần hoàn thiện cùng bộ kết nối.

Danh mục dưới đây là phạm vi cần lập bảng khả năng và nghiệm thu, không phải khẳng định tất cả đã được hỗ trợ:

| Bộ công cụ | Các nhóm thao tác cần đối chiếu với nhu cầu thực tế |
|---|---|
| Tệp | Duyệt/tìm; đọc từng phần và tìm trong nội dung; tạo/ghi/thêm/sửa; đổi tên/sao chép/di chuyển/xóa; thư mục; metadata/quyền/khóa; nén/giải nén; xung đột phiên bản; khôi phục khi có cơ chế hỗ trợ |
| Chương trình | Khởi chạy và kết nối phiên đang chạy; thư mục/môi trường; stdin/stdout/stderr; trạng thái/tiến độ; chờ/hủy/kết thúc; sản phẩm trung gian; tiếp tục hoặc đối chiếu sau gián đoạn |
| Trình duyệt | Tab/phiên; điều hướng/tìm/đọc; phần tử trang và ảnh; nhập/chọn/bấm/cuộn; tải lên/xuống; hộp thoại và iframe khi được hỗ trợ; kiểm tra kết quả website |
| Desktop | Tìm ứng dụng/cửa sổ; nhận diện đúng đích; đọc UIA/ảnh; nhập liệu/menu/hộp thoại; kéo/cuộn/phím tắt; focus/DPI/nhiều màn hình; kiểm tra trạng thái sau thao tác |
| Kết nối | Khám phá API/công cụ; kết nối/xác thực/ngắt/kết nối lại; phiên bản/kiến trúc/capability; dữ liệu vào/ra có kiểu; xử lý lỗi/rate limit; lựa chọn adapter phù hợp |
| Kiểm chứng | Kiểm tra đúng tài nguyên, nội dung, cấu trúc, định dạng/bố cục và hậu điều kiện; so sánh trước/sau; nhận diện kết quả chỉ một phần; bằng chứng và khả năng phục hồi |

Word/Excel/AutoCAD là ứng dụng có thể được truy cập qua bộ kết nối riêng, API/SDK/COM, tệp hoặc giao diện. MCP là giao thức đưa công cụ ra cho Agent; plugin là cách đóng gói khả năng. Chúng không tự tạo ra mọi chức năng của ứng dụng. Bảng khả năng phải ghi theo **thao tác × ứng dụng/phiên bản × đường truy cập × kết quả kiểm thử**; các khoảng trống có thể được bù bằng cách truy cập khác nhưng phải kiểm tra lại ý nghĩa kết quả.

### 3.2. Chờ đúng với tác vụ dài

Đọc tài liệu dài phải có tác vụ hoặc con trỏ đọc tiếp, định danh phiên bản tài liệu và tiến độ thực. Lời gọi điều khiển có thể trả “đang chạy” cùng job ID; Agent tiếp tục theo dõi, nhận từng phần và lưu mốc. Không chuyển một công việc còn tiến triển sang lỗi chỉ vì hết khoảng chờ của một lần gọi.

Phân biệt thời gian chờ phản hồi của lời gọi, thời gian không có tiến triển của công việc và ngân sách tổng tác vụ. Heartbeat chỉ chứng minh tiến trình còn sống, không chứng minh công việc đang tiến triển. Khi không có tiến triển, cần kiểm tra ứng dụng bận, hộp thoại, khóa file hoặc mất kết nối; không chờ vô hạn và cũng không chạy lại toàn bộ một cách mù quáng. Dừng của người dùng phải luôn có tác dụng.

Kết quả đọc chỉ được báo đầy đủ khi xác nhận đã đọc hết phạm vi cần thiết. Nếu đọc lại theo phần sau gián đoạn, kiểm tra tài liệu còn cùng phiên bản; không ghép nội dung từ hai bản sửa khác nhau.

### 3.3. Phục hồi và thích ứng phiên bản trong vùng riêng

Vòng mục tiêu: quan sát lỗi → kiểm tra môi trường và trạng thái hiện tại → phân loại nguyên nhân → chọn cách xử lý → chạy thử phù hợp → kiểm chứng → tiếp tục nhiệm vụ và cập nhật bộ nhớ. Retry chỉ là một nhánh; không lặp cùng đầu vào khi đã biết nó thất bại.

Các nhánh gồm sửa tham số, làm mới handle, kết nối lại, chờ ứng dụng hết bận, chọn adapter tương thích, đổi đường truy cập hoặc viết script/bản adapter thử nghiệm nếu nguồn/API và quyền cho phép. Lỗi thiếu quyền không được biến thành lý do dùng một đường khác để vượt quyền. Kết quả ghi chưa rõ thành công phải được đối chiếu trước khi thử lại.

**Đề xuất cho H2:** giữ công cụ chuẩn có phiên bản; tạo bản thử riêng theo nhiệm vụ và dấu nhận diện môi trường. Mỗi bản thử lưu mô tả thay đổi, phiên bản/phụ thuộc, log đã loại bí mật và bằng chứng kiểm thử. Sau khi đạt mới đăng ký dùng trong phạm vi phù hợp; tái sử dụng ở phiên sau cần dò lại môi trường. Nâng thành bản dùng chung phải đi qua cập nhật phiên bản và kiểm thử hồi quy, không âm thầm ghi đè công cụ gốc.

Một thư mục riêng chỉ giúp tách tệp, không phải sandbox thực thi. Script/adapter phát sinh vẫn phải đi qua cùng cổng quyền, quản lý tiến trình và kiểm chứng. Không mặc định cho phép Agent sửa chương trình đã cài, plugin toàn cục hoặc mã dịch vụ ở phía nhà cung cấp.

Đây là kiến trúc đề xuất cho H2, không phải khẳng định về thư mục hay cơ chế nội bộ cố định của Codex. OpenAI mô tả vòng thực thi–quan sát–sửa lỗi và trạng thái lưu ngoài hội thoại; tài liệu cũng mô tả việc dùng shell để chạy script và phối hợp tích hợp chuyên dụng với thao tác giao diện. Các nguồn này không bảo đảm tự sửa mọi kết nối/mọi phiên bản ứng dụng:

- [Codex: vòng làm việc dài và sửa lỗi](https://developers.openai.com/blog/run-long-horizon-tasks-with-codex)
- [Shell, Skills và Compaction](https://developers.openai.com/blog/skills-shell-tips)
- [Computer Use và tích hợp chuyên dụng](https://learn.chatgpt.com/docs/computer-use)

## 4. Thứ tự và cách nghiệm thu

1. **Đóng đường đi thực tế và hợp đồng:** G01/G02/G08; lập bản đồ công cụ nào thật sự khả dụng trong H2, quy định chung về tài nguyên, lỗi và phiên.
2. **Hoàn thiện trí nhớ và công việc dài:** G03/G04/G05/G13; nghiệm thu lời nhắn cũ, yêu cầu đã thay đổi, công cụ thất bại và tác vụ gián đoạn; định nghĩa vòng chẩn đoán và vùng adapter thử nghiệm.
3. **Hoàn thiện bộ kết nối:** G06/G07/G09; ưu tiên lỗi Word đang gặp nhưng giữ hợp đồng tổng quát cho ứng dụng khác.
4. **Nghiệm thu toàn bộ trải nghiệm và gói chạy:** G10/G11/G12. Kiểm tra UI thật khi có thay đổi UI, rồi mới đóng gói và kiểm tra máy khác.

Các ca bắt buộc trước khi nói “đáp ứng yêu cầu”:

- Thêm một công cụ mới và thay một provider mà không sửa lõi; tắt/làm lỗi provider đó và kiểm tra phần còn lại.
- Tác vụ có nhiều vòng công cụ, nhiều lần nén, thay yêu cầu giữa chừng và chuyển model có cửa sổ ngữ cảnh nhỏ hơn; truy hồi đúng quyết định và việc chưa làm.
- Hai tài liệu/cửa sổ giống tên; nội dung Word chưa lưu; Save As; tài liệu dài có bảng; kiểm tra đúng đích và đủ nội dung.
- Tạo/sửa/xóa các tệp mẫu Word, Excel, AutoCAD, PDF trong thư mục thử riêng; OCR có mẫu đối chiếu; không dùng tài liệu đang làm thật để thử xóa/ghi đè.
- Đọc/tìm/lọc tin tức với nguồn thật, làm việc trên trình duyệt và kết nối app; phân biệt công cụ thiếu cấu hình với lỗi model chọn công cụ.
- Mất mạng/đóng app ở ranh giới thao tác ghi; mở lại không lặp tác động, không báo hoàn thành khi chưa kiểm chứng.
- Một tác vụ đọc vẫn có tiến triển sau khoảng chờ của lời gọi; tiếp tục nhận kết quả đúng job. Một tác vụ chỉ còn heartbeat nhưng không tiến triển phải được chẩn đoán, không chờ mãi.
- Đổi phiên bản ứng dụng hoặc làm hỏng bộ kết nối thử nghiệm; dò lại khả năng, dùng bản phù hợp hoặc phục hồi bản ổn định. Bản công cụ gốc không bị thay đổi bởi sửa chữa theo nhiệm vụ.

Lưu input/output và bằng chứng cần thiết theo chính sách dữ liệu; báo token đầu vào thực hoặc ước tính có nhãn, chi phí nén, tỷ lệ hoàn thành, các dữ kiện quan trọng nhớ đúng và số lần thao tác bị lặp. Không dùng duy nhất số test xanh hoặc mức giảm token để kết luận.

## 5. Phạm vi còn chưa xác minh

Chưa chạy app để tái hiện các phát hiện mới, chưa chạy thử nhiều giờ, chưa thử crash/restart, chưa đo tỷ lệ nhớ sau nén với model người dùng, chưa thử thay provider thực qua UI và chưa thử toàn bộ phiên bản Office/AutoCAD. Mã nguồn cho thấy các khoảng trống nêu trên; mức tác động thực tế cần được đo trong giai đoạn kiểm thử riêng.

Các thống kê kiểm thử cũ trong master/tracker giữ nguyên phạm vi và thời điểm của chúng. Báo cáo này không thu hồi lịch sử đã kiểm chứng và không dùng các kết quả đó để tuyên bố hoàn thành mục tiêu mới.

## 6. Đánh giá mức độ hoàn thiện hiện tại

**Ước lượng tổng thể: 4/10 đối với mục tiêu Agent tổng quát người dùng đã nêu.** Đây là nhận định kỹ thuật từ mã nguồn và bằng chứng đã đọc tại commit trên, không phải benchmark đối đầu với Codex, tỷ lệ hoàn thành công việc hay tuyên bố H2 đạt 40% Codex. Không tính chất lượng model và không đánh giá lại thiết kế giao diện trong bảng này.

Mốc chấm: 2 = mới có một phần khả năng hoặc đường thử; 4 = làm được một số công việc thật nhưng còn các khoảng trống nền tảng; 6 = luồng sản phẩm tương đối đầy đủ trong phạm vi hỗ trợ; 8 = đã kiểm chứng độ ổn định, lỗi và nhiều môi trường; 10 = đạt trọn bộ mục tiêu và điều kiện nghiệm thu đã công bố, không mang nghĩa làm được mọi việc tuyệt đối. Điểm tổng thể là đánh giá mức trưởng thành, không phải phép đo có độ chính xác thống kê.

| Mặt đánh giá | Điểm /10 | Căn cứ và giới hạn |
|---|---|---|
| Khung kiến trúc mở | 6 | Có registry, provider, extension, transport, quyền và verifier. Việc nối đầy đủ các thành phần qua sản phẩm thật và thay provider thực tế còn cần nghiệm thu |
| Độ đầy đủ của công cụ | 4 | Có công cụ file/Python/Office, PowerShell, đọc web và desktop. Độ rộng thao tác, chọn đúng tài nguyên, trình duyệt/search và tương thích phiên bản chưa đạt mục tiêu |
| Tích hợp và điều phối trong H2 | 4 | Có đường chạy Agent thật, tiến độ và kiểm tra kết quả. Luồng context/compaction ở Lab và production chưa tương đương; có giới hạn và phụ thuộc vào bộ công cụ được cấu hình |
| Chẩn đoán, sửa lỗi, kiểm chứng | 4 | Có repair, theo dõi lỗi và chặn lặp thao tác thất bại. Chưa chứng minh thích ứng bộ kết nối theo môi trường hoặc phục hồi đầy đủ các lỗi thực tế qua UI |
| Ngữ cảnh và trí nhớ công việc | 2 | Work Assistant lấy 6 tác vụ hoàn thành; summary của checkpoint chủ yếu là số sự kiện; chưa chứng minh truy hồi quyết định cũ/việc lỗi/dang dở hay giữ đúng trạng thái sau nhiều lần nén trong production |
| Công việc dài và tiếp tục sau gián đoạn | 2 | Shell chưa có phiên tương tác dài; công việc chưa kết thúc chuyển Failed khi nạp lại archive. Chưa có bằng chứng nối lại job hoặc đối chiếu tác động dở dang sau restart |

Các hạn chế này giải thích vì sao trải nghiệm có thể cảm thấy sơ sài dù dự án có nhiều module và bài kiểm thử. Đánh giá trước đó “cấu trúc đi đúng hướng” chỉ nói về các ranh giới kiến trúc đã có; không có nghĩa lõi đã hoàn chỉnh hoặc chỉ còn bổ sung vài lệnh cho bộ công cụ.

Ưu tiên để nâng mức trưởng thành: nối đúng đường sản phẩm → task state/truy hồi/nén trong lượt dài → job và phục hồi → khả năng công cụ và nhận diện tài nguyên → kiểm thử tương thích/thay provider thật. Chưa có căn cứ để chấm điểm độ tin cậy từng tác vụ bằng phần trăm; cần corpus dùng chung, cùng điều kiện và chạy lặp để đo.
