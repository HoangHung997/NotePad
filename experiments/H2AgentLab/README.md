# H2 Agent Lab: skill-driven agent

> **Active implementation plan (17/09/2026): H2 Agent Lab 2.0.**  
> Architecture/specification: [AGENTLAB_V2_SPEC.md](AGENTLAB_V2_SPEC.md)  
> Authoritative sequential task tracker: [AGENTLAB_V2_TASKS.md](AGENTLAB_V2_TASKS.md)  
> Normative MCP/Web/Office/Plugin requirements: [../../docs/H2_AGENT_MCP_WEB_OFFICE_REQUIREMENTS.md](../../docs/H2_AGENT_MCP_WEB_OFFICE_REQUIREMENTS.md)  
> The v1 material below is retained as baseline/history. Do not treat it as the final v2 architecture. V2 is developed inside Agent Lab first; integration into H2 Notes is blocked until the acceptance gate passes and the user explicitly approves it.

Ứng dụng AI thử nghiệm **độc lập với Codex**, dùng Ollama hoặc API tương thích OpenAI Chat Completions. Người dùng chốt hướng này ngày 16/09/2026. Không dùng tài khoản Codex, không đọc khóa hay lịch sử H2 Notes, chưa tích hợp vào H2 Notes.

## Hướng mới

Yêu cầu → khám phá skill → đọc hướng dẫn cần thiết → tự viết mã theo tác vụ → chạy trên bản sao → đọc lỗi/kết quả → sửa và kiểm lại → duyệt xuất tệp.

Không tạo thêm công cụ C# cho từng yêu cầu đổi font, màu, range hay đoạn Word. `run_python` là năng lực thực thi dùng chung; model tự soạn mã cho yêu cầu cụ thể. Skill là hướng dẫn phương pháp và tiêu chuẩn kiểm tra, không phải quyền chạy vô hạn và không thay thế chất lượng model.

Năm skill: `spreadsheets`, `documents`, `pdf`, `coding`, `computer-use`. Word/Excel/PDF chuyển thể từ skill Codex thực đã cài; bản gốc và nguồn giữ trong [skill-sources/NOTICE.md](skill-sources/NOTICE.md). Không sao chép công cụ riêng chỉ Codex có. Chỉ nạp tên/mô tả ban đầu, đọc SKILL.md và tài liệu tham chiếu khi cần. Nút **Kỹ năng & môi trường** cho xem hướng dẫn hiện có.

## Tự kiểm và phục hồi

Lỗi trả về có nhóm nguyên nhân, khả năng sửa và hướng kiểm tra tiếp. Nếu thiếu tệp, công cụ cung cấp tên gần giống **thực sự quan sát được**; `find_files` tìm tiếp trong phạm vi đã chọn và báo rõ giới hạn quét. Tên giống không chứng minh đúng tài liệu: nhiều ứng viên thì phải đối chiếu hoặc hỏi, không tự đổi tên/tạo tệp thay thế.

Vòng agent không nhận ngay kết luận sau lỗi còn bỏ ngỏ: nhắc model kiểm tra bằng công cụ và đổi cách làm, tối đa hai lần nhắc trong ngân sách 24 vòng. Lệnh giống hệt đã lỗi hai lần không thực thi tiếp trong lượt. Thao tác có thể đã ghi/bấm phải đọc lại đúng tệp/cửa sổ trước khi lặp; lời từ chối không bị hỏi lại qua công cụ khác. JSON tham số hỏng được yêu cầu sửa, không chạy một phần nhóm lệnh.

Mã chạy lỗi và kiểm cấu trúc không đạt là lỗi cần chẩn đoán, không phải hoàn thành. Khi còn lỗi chưa giải quyết, app ghi rõ chưa xác minh và giữ nhật ký, không đưa lời tự nhận thành công chưa có bằng chứng làm câu trả lời cuối. Đây là kiểm soát quy trình theo dấu vết công cụ, **không phải bộ chấm ngữ nghĩa toàn năng**: model vẫn phải chọn đúng phương pháp và kiểm yêu cầu. Không tự thêm năng lực, bỏ sandbox, đổi API hay thử lại giao dịch mạng không rõ kết quả.

## Mở và thử

- **Mang sang máy khác:** dùng gói Portable đầy đủ, không chỉ chép exe. Đã đóng kèm .NET, Python và 5 skill. Xem [hướng dẫn Portable](PORTABLE.md). Lịch sử/cấu hình riêng và model Ollama không nằm trong gói.
- Chạy `bin/Release/net10.0-windows/H2AgentLab.exe` sau khi build. Cần .NET Desktop Runtime 10 trên Windows.
- Mặc định chỉ làm việc trong thư mục mẫu `%LOCALAPPDATA%/H2AgentLab/workspace`. Lịch sử, kết nối không chứa khóa và backup nằm riêng trong `%LOCALAPPDATA%/H2AgentLab`.
- **Kết nối AI**: nhập địa chỉ Ollama local/LAN, lấy danh sách và chọn model. API khác cần đúng giao thức Chat Completions và endpoint HTTPS. Không tự tải model. API key chỉ giữ trong RAM; phải nhập lại khi mở app, không lấy khóa của app khác.
- **Tạo dữ liệu mẫu** tạo `brief.md` mới, không ghi đè. **Chỉ đọc** vẫn cho phép tính toán được duyệt trên bản sao nhưng chặn ghi/xuất vào kho gốc. Lần chạy mã đầu hỏi quyền thực thi trong lượt hiện tại; xuất tệp luôn hỏi riêng. Thay tệp cần hash hiện tại và có backup.
- Thử: “Trong Tasks, đổi A2:B10 sang Arial 12 nhưng giữ nghiêng, tô xanh các dòng đã xong, kiểm dữ liệu khác không đổi rồi xuất bản mới.” Không có hàm viết sẵn riêng cho yêu cầu này.
- **Chọn cửa sổ** chỉ gắn một cửa sổ đã chọn, chưa đọc nội dung. Mỗi lần đọc/bấm/thay text có xác nhận. **Mở vùng thử điều khiển** là form không lưu hoặc gửi mạng để thử trước. Lab chặn một số terminal/IDE, Codex, cửa sổ của chính Lab (trừ form thử), ứng dụng bảo mật và quản lý mật khẩu. Điều khiển UI Automation không hoạt động trên mọi app/canvas và chưa được nghiệm thu end-to-end.
- **Nhật ký & bằng chứng** chứa thời gian và kết quả công cụ. Phiên mới không trộn ngữ cảnh cũ; tệp lịch sử theo ID vẫn còn trong thư mục dữ liệu. Chưa có bộ tìm kiếm/mở lại mọi phiên cũ trong UI.

## Có gì và chưa có gì

Có native tool-calling tối đa 24 vòng/lượt, khám phá/đọc skill, lập kế hoạch, đọc/tìm tệp, tự viết và chạy Python, đọc kết quả/lỗi để sửa lại, xem ảnh đầu ra, xuất tệp có duyệt/hash/backup. Mã, stdout/stderr, SHA-256 đầu vào/đầu ra và nhật ký thời gian được giữ theo run ID; có thể đọc lại để tiếp tục sau lỗi/khởi động lại.

Thư viện: openpyxl, python-docx, lxml, pypdf, pypdfium2, reportlab, Pillow. Có đọc/sửa/tạo Excel, Word, PDF bằng mã và render PDF thành ảnh. `view_artifact` gửi ảnh thật cho model có vision, không suy nội dung từ tên tệp. Bản Portable ưu tiên Python đi kèm cạnh exe; bản phát triển dùng bộ riêng trong LocalAppData. Bộ đi kèm bị thiếu/hỏng sẽ báo rõ, không âm thầm dùng bộ cài của máy lập trình để che lỗi. `Setup-Runtime.ps1 -SourcePython <thư mục CPython 3.12 có các thư viện cần thiết>` dành cho chuẩn bị môi trường phát triển/đóng gói.

Python chạy trong Windows AppContainer: runtime riêng chỉ đọc; chỉ thư mục bản sao được cấp ghi; không cấp quyền mạng; một tiến trình; 768 MB; 120 giây/đoạn mã. Không thừa hưởng API key từ môi trường. Thiếu runtime thì báo lỗi, không tự chạy mã ngoài sandbox. Không còn công cụ hẹp `create_word`, `word_replace` hay build .NET ngoài sandbox.

Đây là AppContainer thông thường, không phải LPAC. Những tài nguyên hệ thống công khai cho AppContainer vẫn có thể truy cập. Kiểm tăng trưởng tệp không phải quota ổ đĩa; chưa có kiểm toán bảo mật độc lập. Công cụ mở/điều khiển ứng dụng ngoài không nằm trong sandbox Python và vẫn cần duyệt riêng. Không thử dữ liệu quan trọng trước khi nghiệm thu.

**Chưa ngang Codex**: chưa tích hợp OCR, renderer bố cục Word, engine tính lại Excel, C#/Node build sandbox, browser agent, quản lý plugin/MCP động, tìm mọi phiên cũ hoặc điều khiển mọi app/canvas. API hiện chỉ có hai giao thức trên, không đồng nghĩa mọi API native đều tương thích.

AI chờ đến khi xong hoặc người dùng Dừng/lỗi mạng; không có timeout tổng lượt trong chat. Bộ benchmark riêng có giới hạn thời gian để ghi rõ trường hợp chưa hoàn thành. Dừng không hoàn tác hành động đã xảy ra; nhật ký ghi lại kết quả, backup tệp giúp phục hồi thủ công. Không tự retry thao tác ghi/bấm.

## Mã và kiểm thử

- `LabWindow.cs`, `App.axaml`: giao diện.
- `AgentRunner.cs`: vòng model → tool → kết quả → model; Ollama native và Chat Completions.
- `AgentTools.cs`, `ComputerTools.cs`: công cụ và lớp duyệt/phạm vi.
- `LabSession.cs`: lịch sử và kiểm đường dẫn.
- `SkillCatalog.cs`, `skills/`: khám phá và tải kỹ năng theo nhu cầu.
- `ScriptWorkspace.cs`, `WindowsPythonSandbox.cs`: bản sao, thực thi OS sandbox, bằng chứng và xuất tệp.
- `LabTests.cs`, `SkillTests.cs`, `SkillLiveEvaluation.cs`: kiểm thử và benchmark trên dữ liệu giả.

Build: `dotnet build experiments/H2AgentLab/H2AgentLab.csproj -c Release`.

Tự kiểm: `H2AgentLab.exe --self-test <thư mục bằng chứng mới>` và `--skills-test <thư mục mới>`.

Kiểm lỗi mang sang máy khác: `--portability-test <thư mục mới>`. Kiểm bộ cài thực, tạo/đọc Word/Excel/PDF trong sandbox, không dùng AI: `--verify-install <thư mục mới>`.

Đóng gói Windows x64: `./Publish-Portable.ps1 -Destination <thư mục phát hành mới> -Zip`. Script publish self-contained, chỉ chép runtime/thư viện được chọn và skill, kiểm thực thi sandbox trước khi tạo ZIP. Không đóng gói dữ liệu LocalAppData, hồ sơ kết nối, lịch sử, API key hay model. Giữ sandbox và xác nhận thao tác như cũ.

Kiểm phục hồi: `--recovery-test <thư mục bằng chứng mới>`; các bước model được giả lập có chủ đích, thao tác tệp/Python là thật. `--recovery-live http://localhost:11434 <model> <thư mục mới>` kiểm model thật xử lý một lỗi sai tên tệp được tạo trước, chỉ dùng tài liệu giả, không sửa tệp nguồn, giới hạn benchmark 8 phút. Giới hạn này không áp vào thời gian chờ chat thường.

Benchmark skill: `H2AgentLab.exe --skills-live http://localhost:11434 <model> <thư mục mới>`: model thật sửa workbook tổng hợp, oracle độc lập; chỉ cho phép xuất result.xlsx, tối đa 8 phút cho benchmark. `--live-eval http://localhost:11434 <model> <thư mục mới>` kiểm đọc dữ liệu; thêm `--word` để thử skill Word. Không dùng model cloud cho các lệnh này.

Kiểm sửa Word có sẵn: `--word-edit-live http://localhost:11434 <model> <thư mục mới>` dùng tài liệu giả có định dạng/bảng/header, yêu cầu nối một đoạn rồi kiểm độc lập phần cũ và bản nguồn. Các lệnh live có thể thêm `--no-thinking` để thử riêng chế độ không suy luận; không đổi cấu hình app đã lưu. Đây là chế độ kiểm khác và phải báo rõ khi so kết quả. Số đo từng lượt không chứa nội dung suy luận. Xem [đánh giá Qwen3.5](QWEN35_EVALUATION.md).

Chụp control app: `H2AgentLab.exe --evidence <thư mục bằng chứng>`; kho demo riêng, không gọi model, tự đóng sau chụp. `--data <thư mục riêng>` để kiểm UI thủ công. Không dùng thư mục H2 Notes cho các chế độ này.

Xem [nghiên cứu và điều kiện tích hợp](RESEARCH.md), [kết quả đánh giá](EVALUATION.md). Chỉ chia sẻ adapter đã đạt về H2 Notes sau một đợt nghiệm thu riêng; không có kết nối tích hợp tự bật.

Có thể thêm hướng dẫn bằng thư mục `skills/<name>/SKILL.md` với frontmatter một dòng `name`, `description`; không cần thêm công cụ cho mỗi thao tác mới. Đây là định dạng con đơn giản, không hỗ trợ toàn bộ YAML hay tự cài plugin bên thứ ba.
