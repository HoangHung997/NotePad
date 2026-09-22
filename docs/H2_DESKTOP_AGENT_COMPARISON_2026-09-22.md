# Đối chiếu Agent mã nguồn mở và khả năng làm việc với ứng dụng của H2

> Đã hợp nhất vào [báo cáo hiện trạng và giải pháp đầy đủ](H2_AGENT_CONSOLIDATED_REVIEW_AND_SOLUTION_2026-09-22.md) ngày 22/09/2026. Giữ bản này để truy vết; dùng bản hợp nhất để đọc kết luận và giải pháp hiện hành của đợt rà soát.

Ngày nghiên cứu: 22/09/2026. H2 được đọc tại commit `9265c75c4be60e4019d655ef049f66ffc6f779bc`.

## 1. Kết luận và lựa chọn

**Mục tiêu kiến trúc là một nền tảng Agent tổng quát, mở và thay thế được từng thành phần.** Open Interpreter Rust là nguồn tham khảo bộ máy Agent; Cua Driver là nguồn tham khảo điều khiển desktop; Microsoft UFO là nguồn tham khảo Office. Không coi việc ghép cố định ba dự án này là kiến trúc đích. Đây là đề xuất dựa trên mã nguồn, chưa phải kết quả chạy so sánh các ứng dụng trên máy này.

Open Interpreter Rust có nguồn gốc từ Codex và cung cấp các giao diện tích hợp cho ứng dụng khác. Đáng chú ý, skill QA trong chính bản được kiểm tra hướng dẫn dùng `agent-browser` cho trình duyệt và `cua-driver` cho ứng dụng desktop. Hai công cụ này được cài thêm khi cần, không thể suy ra rằng chỉ cài Open Interpreter là mọi ứng dụng đã kết nối được. [S1–S3]

Với lỗi Word hiện tại, ưu tiên nâng cấp lớp nhận diện và kết nối ứng dụng trước khi thay toàn bộ bộ máy Agent. Việc thay bộ máy có thể cải thiện điều phối công cụ nhưng không tự sửa cách H2 gắn nhầm hoặc không tìm thấy tài liệu.

Không có bằng chứng từ các nguồn đã kiểm tra rằng một ứng dụng bảo đảm mọi chức năng của Codex hoặc đọc/sửa đúng mọi phiên bản Word. Báo cáo không đánh giá chất lượng model; quyền hạn, công cụ, hệ điều hành và cách kiểm chứng vẫn cần được thiết kế độc lập với model.

### 1.1. Làm rõ theo yêu cầu tổng quát và mở rộng/thay thế

Người dùng làm rõ ngày 22/09/2026: công cụ phải tổng quát, giải quyết vấn đề ở nền tảng và cho phép bổ sung/thay thế về sau. Phần Word trong báo cáo là ca đối chiếu cụ thể; không phải giới hạn sản phẩm vào Office.

Định hướng này đã có trong [H2_AGENT_MASTER_SPEC](H2_AGENT_MASTER_SPEC.md): lõi độc lập với ứng dụng; FileSystem, Process/Shell, Office, Web, Desktop, AutoCAD và MCP là phần mở rộng. Cần thực hiện và nghiệm thu đầy đủ đặc tả này, không tạo thêm một kiến trúc cạnh tranh với nó.

Hình dạng đề nghị:

```text
Giao diện H2 / phiên và lịch sử công việc
                 |
Lõi Agent: hiểu nhiệm vụ, điều phối, ngữ cảnh, kiểm chứng
                 |
Danh mục công cụ + cổng thực thi chung + quản lý tài nguyên/phiên
                 |
Các phần mở rộng: file | tiến trình | browser | desktop | dữ liệu/dịch vụ
                 |
Các bộ kết nối có thể thay thế: local, MCP, CLI, SDK, API, UIA, ...
```

Skills cung cấp quy trình và kiến thức thao tác. Plugin đóng gói công cụ, skills, cấu hình và phụ thuộc. MCP là một giao thức tích hợp; chỉ bổ sung MCP chưa tạo ra quản lý tiến trình, đọc tài liệu đầy đủ, phục hồi hay kiểm chứng kết quả. Tài liệu OpenAI phân biệt rõ vai trò plugin, skill và MCP server. [S18]

| Nhóm công cụ tổng quát | Trách nhiệm cần có |
|---|---|
| Tệp và tài nguyên | Tìm, đọc từng phần, tạo/sửa/xóa trong phạm vi quyền; nhận diện phiên bản và xung đột; tệp nhị phân dùng bộ xử lý phù hợp |
| Mã và tiến trình | Chạy PowerShell/Python/chương trình được phép; phiên dài, đọc tiến độ, gửi đầu vào, chờ, hủy; quản lý cây tiến trình |
| Web và trình duyệt | Tìm kiếm, tải/đọc nội dung, mở trang, đọc cấu trúc trang, thao tác và quản lý phiên/tab |
| Ứng dụng desktop | Tìm/chọn cửa sổ, đọc cây giao diện/ảnh, thao tác, xác thực đúng ứng dụng và đọc lại kết quả |
| Kết nối mở rộng | Tìm khả năng có sẵn, nạp công cụ khi cần, kết nối dịch vụ qua MCP/API/CLI và báo tình trạng kết nối |
| Kết quả công việc | Tạo và xem sản phẩm, lưu bằng chứng, kiểm tra nội dung/định dạng, phân biệt thành công một lệnh với hoàn thành nhiệm vụ |

Tên thao tác cụ thể do hợp đồng công cụ quy định; bảng trên không tuyên bố mọi thao tác đã có trong H2. Công cụ nên có thao tác và dữ liệu vào/ra rõ ràng; một lệnh mơ hồ như “làm mọi thứ” không giải quyết được khả năng kiểm chứng hay thay thế.

Các điều kiện để thật sự thay được thành phần:

1. **Hợp đồng có phiên bản:** công cụ khai báo schema vào/ra, khả năng, yêu cầu quyền, nền tảng/phụ thuộc và cách kiểm chứng. Bộ kết nối được dò khả năng thực tế trước khi gọi; không suy khả năng chỉ từ tên ứng dụng.
2. **Kết quả thống nhất:** có trạng thái, dữ liệu, sản phẩm, tiến độ, lỗi có mã, phần nội dung bị cắt và vị trí đọc tiếp. Tác vụ dài có run ID và đường theo dõi/hủy.
3. **Danh tính tài nguyên thống nhất:** công cụ nhận đích đã giải quyết rõ ràng, không đoán lại “file đó/cửa sổ đó” mỗi lần. Handle do bộ kết nối sở hữu cần được kiểm chứng hoặc kết nối lại khi thay backend; không chuyển nguyên handle cũ sang backend mới.
4. **Quyền thực thi chung:** phạm vi cấp cho phiên được áp dụng ở nơi thực thi. Bộ kết nối không tự mở rộng quyền, không bắt người dùng cấp lại từng thao tác khi quyền đã đủ. Đổi đường truy cập không được lách phạm vi đã cấp.
5. **Vòng đời phần mở rộng:** cài, kiểm tra phụ thuộc, bật/tắt, nâng cấp, quay lại phiên bản trước và thay nhà cung cấp. Phiên đang chạy giữ phiên bản tương thích hoặc chuyển tại điểm an toàn; không thay âm thầm giữa thao tác sửa dữ liệu.
6. **Kiểm tra khả năng thay thế:** thay provider A bằng B, thêm một ứng dụng mới, tắt hoặc làm lỗi một plugin mà lõi và các công cụ còn lại vẫn chạy. Kiểm thử phải đi qua giao diện H2 và backend thực ngoài các ca giả lập.

### 1.2. Nền tảng hiện có và khoảng trống đã đọc

H2 không bắt đầu từ số không:

- [ToolRegistry](https://github.com/HoangHung997/NotePad/blob/9265c75c4be60e4019d655ef049f66ffc6f779bc/experiments/H2AgentLab/Tools/ToolRegistry.cs#L147) đã có schema, phiên bản, executor, provenance, phạm vi và metadata lựa chọn công cụ.
- [IAgentExtension](https://github.com/HoangHung997/NotePad/blob/9265c75c4be60e4019d655ef049f66ffc6f779bc/experiments/H2AgentLab/Extensions/AgentExtension.cs#L41) và registry phần mở rộng đã có đường đăng ký công cụ, skill, verifier và provider.
- [ICapabilityProvider](https://github.com/HoangHung997/NotePad/blob/9265c75c4be60e4019d655ef049f66ffc6f779bc/experiments/H2AgentLab/Providers/CapabilityProvider.cs#L54) và [adapter chung](https://github.com/HoangHung997/NotePad/blob/9265c75c4be60e4019d655ef049f66ffc6f779bc/experiments/H2AgentLab/Providers/CapabilityProviderToolRegistryAdapter.cs#L9) đã hỗ trợ nạp định nghĩa công cụ từ provider.
- [AgentRuntimeFactory](https://github.com/HoangHung997/NotePad/blob/9265c75c4be60e4019d655ef049f66ffc6f779bc/experiments/H2AgentLab/Runtime/AgentRuntimeFactory.cs#L37) dùng registry cho đường chạy chính. Không quy lỗi hiện tại cho switch lịch sử trong AgentTools khi chưa chứng minh đường đó được chạy.

Một khoảng trống cụ thể là [H2LocalCommandTool](https://github.com/HoangHung997/NotePad/blob/9265c75c4be60e4019d655ef049f66ffc6f779bc/experiments/H2AgentLab/Integration/H2LocalCommandTool.cs#L25): công cụ chạy PowerShell không tương tác, đóng stdin, đợi kết thúc và giới hạn tối đa 300 giây. Đây chưa phải phiên tiến trình dài để Agent đọc tiến độ, gửi thêm đầu vào và tiếp tục điều khiển. Cần thiết kế hợp đồng process session trong phần mở rộng Process/Shell.

Đường Desktop hiện gắn một cửa sổ đã chọn, và kết nối Office còn các vấn đề ở mục 4. Đây là các khoảng trống của bộ kết nối/phối hợp tài nguyên; thay vòng chạy Agent không tự khắc phục chúng.

Ưu tiên kiến trúc: chuẩn hóa hợp đồng và bộ kiểm tra tương thích → quản lý tài nguyên/phiên và trạng thái lỗi → hoàn thiện công cụ nền dùng chung → nghiệm thu thêm/thay provider thực tế. Word, Excel, AutoCAD, OCR là các phần mở rộng chuyên dụng giúp công việc chính xác hơn; Agent vẫn phải có công cụ nền để xử lý tình huống ngoài danh sách đó.

### 1.3. Làm việc dài và nhớ chính xác sau nén

Bổ sung yêu cầu làm việc dài và nén ngữ cảnh ngày 22/09/2026: kiến trúc chuẩn được mở rộng tại [Agent Master, mục 8.1–8.4](H2_AGENT_MASTER_SPEC.md); các hạng mục MB-124–MB-127 trong [tracker Agent](H2_AGENT_MASTER_TASKS.md) còn mở. Nền tảng phải lưu nhật ký đầy đủ, sổ trạng thái có bằng chứng và ngữ cảnh gửi model gọn; hỗ trợ truy hồi chính xác và tiếp tục sau gián đoạn. Bộ nén cũng phải thay thế được theo provider.

Qua đọc [RuntimeCompactionCoordinator.BuildSummary](https://github.com/HoangHung997/NotePad/blob/9265c75c4be60e4019d655ef049f66ffc6f779bc/experiments/H2AgentLab/Session/RuntimeCompactionCoordinator.cs#L186), bản tóm tắt lịch sử hiện ghi số sự kiện user/assistant/tool và tham chiếu nguồn; chưa tự tóm tắt đầy đủ quyết định và việc đã làm. [AgentContextBudget](https://github.com/HoangHung997/NotePad/blob/9265c75c4be60e4019d655ef049f66ffc6f779bc/experiments/H2AgentLab/Context/AgentContextManager.cs#L11) dùng giới hạn ký tự. Không thể lấy bằng chứng hội thoại được rút ngắn làm bằng chứng Agent nhớ đúng toàn bộ công việc. Lần bổ sung này cập nhật yêu cầu và tiêu chí nghiệm thu, chưa sửa bộ nén đang chạy.

## 2. Các dự án đã đối chiếu

| Dự án | Giá trị đối với H2 | Giới hạn cần biết |
|---|---|---|
| Open Interpreter Rust | Tham khảo vòng chạy Agent, phiên làm việc, gọi công cụ, MCP, tích hợp SDK/app-server | Computer use cần công cụ bổ sung; kiểm tra tương thích SDK hiện có không chứng minh tương đương toàn bộ Codex |
| Cua / Cua Driver | Lớp điều khiển desktop qua CLI/MCP; chọn cửa sổ, đọc cây giao diện, chụp ảnh, gửi thao tác | Là thành phần công cụ, không phải toàn bộ ứng dụng chat; bảng kiểm thử công khai vẫn có thao tác bị từ chối hoặc chưa hỗ trợ |
| Microsoft UFO | Tham khảo điều phối ứng dụng Windows; kết hợp UI Automation với API Word/Excel/PowerPoint | Một số đường chọn tài liệu dựa vào tên gần giống; không nên sao chép nguyên phần nhận diện |
| UI-TARS Desktop / Agent TARS | Tham khảo vòng quan sát–thao tác và phối hợp trình duyệt, tệp, shell, MCP | Cần phân biệt Agent TARS tổng quát với UI-TARS Desktop hướng tới các model thị giác cụ thể; chưa có bằng chứng giải quyết đầy đủ Word COM |

Nguồn: [S1–S4], [S7–S10], [S13].

Các bản nguồn được cố định để có thể kiểm tra lại:

| Kho | Phiên bản/commit đã đọc | Giấy phép kho công bố |
|---|---|---|
| openinterpreter/openinterpreter | `rust-v0.0.45`, `d9b49c828bf61d820c30e343a22033e3e7999583` | Apache-2.0 |
| trycua/cua | `9bbfa7dd3e27ca7f1861ede70aaca390174493f9` | MIT |
| microsoft/UFO | `be75a7ded2ad98d97819e15ff1b39d4202ac3ac5` | MIT |
| bytedance/UI-TARS-desktop | `c2ad42e3eb9b27830db41a3e6f51ca7179d9b168` | Apache-2.0 |

Nếu đưa mã vào sản phẩm, cần giữ thông báo bản quyền và kiểm tra giấy phép của các thành phần phụ thuộc được chọn.

## 3. Vì sao Codex đọc được Word không có nghĩa là có một kết nối Word vạn năng

Tài liệu OpenAI mô tả Computer Use là khả năng nhìn và thao tác giao diện; đồng thời ưu tiên tích hợp chuyên dụng nếu ứng dụng cung cấp. Trên Windows, tài liệu yêu cầu desktop đang hoạt động và nêu giới hạn sử dụng foreground. Đây là nhiều đường tiếp cận bổ sung nhau, không phải cam kết mọi phiên bản Word đều có cùng một API. [S14]

H2 cần phân biệt ba loại kết quả:

1. **Đọc giao diện:** văn bản hoặc ảnh mà ứng dụng đang hiển thị. Có thể thiếu trang ngoài màn hình, bảng, chú thích và nội dung chưa được cây giao diện cung cấp.
2. **Đọc tài liệu đang mở:** đọc đối tượng tài liệu trong Word, bao gồm thay đổi chưa lưu nếu API và trạng thái tài liệu cho phép.
3. **Đọc tệp đã lưu:** đọc DOCX/PDF trên ổ đĩa. Nội dung có thể cũ hơn bản đang soạn.

Đọc được loại 1 không chứng minh đã đọc đầy đủ loại 2. H2 không được tự chuyển sang loại 3 rồi báo đã đọc bản đang mở nếu chưa kiểm tra độ mới.

## 4. Các điểm cụ thể trong mã H2

Đây là các đặc tính được xác nhận trong mã. Các tình huống gây lỗi nêu bên cạnh là suy luận cần tái hiện, chưa xác định được lỗi trong lần thao tác cụ thể của người dùng.

### H2-01 — Nhận diện Office có thời gian chờ ngắn và làm mất nguyên nhân lỗi

`CaptureActiveWorkContext` tạo OfficeHostClient và cancellation đều ở mức **3 giây**. Các lỗi IO, hủy hoặc trạng thái không hợp lệ trả về `null`; không cung cấp nguyên nhân kết nối cho bước tiếp theo. Lớp ngoài vẫn giữ thông tin cửa sổ nhưng có thể thiếu liên kết tài liệu Office.

Điều này có thể tạo cảm giác lúc được lúc không khi Word khởi động chậm hoặc đang bận. Đây là thời gian chờ nhận diện ngữ cảnh, khác với thời gian chờ 60 giây của công cụ Office khi thực thi.

Nguồn H2: [nhận diện ngữ cảnh](https://github.com/HoangHung997/NotePad/blob/9265c75c4be60e4019d655ef049f66ffc6f779bc/experiments/H2AgentLab/Integration/H2ProductionAgentAdapter.Context.cs#L34).

Đề xuất: nhận diện bất đồng bộ, lưu kết quả kết nối có thời hạn, trả các trạng thái phân biệt như đang bận, hết thời gian, không tìm thấy, không khớp tài liệu; chỉ thử lại có giới hạn với thao tác đọc phù hợp.

### H2-02 — Liên kết Word phụ thuộc một COM instance và tên/đường dẫn

`WordDiscovery` và `FindWord` lấy ứng dụng bằng `GetActiveObject("Word.Application")`. Chúng không duyệt đầy đủ mọi Word instance. Session ID được tạo từ `Application.Hwnd`, tên và đường dẫn tài liệu; bước ghép với cửa sổ trước mặt còn yêu cầu trùng ActiveSessionId.

Các tình huống cần kiểm tra: hai Word instance, nhiều cửa sổ xem tài liệu, tài liệu chưa lưu, đổi tên hoặc Save As. Việc đổi đường dẫn làm thay đổi ID tính từ đường dẫn; cửa sổ đích và cửa sổ ứng dụng COM có thể không phải cùng một view.

Nguồn H2: [WordDiscovery](https://github.com/HoangHung997/NotePad/blob/9265c75c4be60e4019d655ef049f66ffc6f779bc/experiments/H2AgentLab.OfficeHost/ComOfficeBackend.cs#L158), [FindWord và tạo session ID](https://github.com/HoangHung997/NotePad/blob/9265c75c4be60e4019d655ef049f66ffc6f779bc/experiments/H2AgentLab.OfficeHost/ComOfficeBackend.cs#L970).

Đề xuất: gắn phiên với đúng process/cửa sổ/view/tài liệu; đường dẫn là thuộc tính có thể thay đổi. Thử cơ chế native object model theo cửa sổ Word thực tế, rồi kiểm chứng `Word.Window.Hwnd`. Microsoft có tài liệu `AccessibleObjectFromWindow` với `OBJID_NATIVEOM`, trong đó `_WwG` cung cấp Word Window. Đây là một đường cần dò khả năng và kiểm thử, không phải bảo đảm mọi phiên bản đều hoạt động. [S11–S12]

### H2-03 — Đọc cây giao diện là bản tóm tắt có giới hạn

DesktopHost đã có UI Automation, kiểm tra process, ảnh cửa sổ và token thao tác. Tuy nhiên, mỗi giá trị văn bản bị giới hạn 1.500 ký tự, toàn bộ cây giới hạn 200 phần tử. Cơ chế này phù hợp quan sát giao diện nhưng không nên được dùng như bằng chứng đã đọc đầy đủ tài liệu dài.

Nguồn H2: [đọc văn bản UIA](https://github.com/HoangHung997/NotePad/blob/9265c75c4be60e4019d655ef049f66ffc6f779bc/experiments/H2AgentLab.DesktopHost/Win32DesktopBackend.cs#L454), [giới hạn cây](https://github.com/HoangHung997/NotePad/blob/9265c75c4be60e4019d655ef049f66ffc6f779bc/experiments/H2AgentLab.DesktopProtocol/DesktopProtocol.cs#L104).

Đề xuất: đọc theo đoạn/phạm vi và trả rõ `complete`, `truncated`, vị trí tiếp tục cùng nguồn dữ liệu. Đọc đầy đủ Word cần ưu tiên API tài liệu; UIA/OCR phải ghi rõ phạm vi đã đọc.

### H2-04 — Kiểm tra trạng thái có thể quá nhạy với ảnh thay đổi

StateId bao gồm hash ảnh chụp. Trước khi thao tác, H2 chụp lại và yêu cầu toàn bộ StateId giữ nguyên. Thay đổi hình ảnh không liên quan đến đích thao tác có thể gây `stale_state`; cần kiểm thử con trỏ nhấp nháy, hoạt ảnh và thay đổi thanh trạng thái để xác nhận mức ảnh hưởng thực tế.

Nguồn H2: [kiểm tra trước thao tác](https://github.com/HoangHung997/NotePad/blob/9265c75c4be60e4019d655ef049f66ffc6f779bc/experiments/H2AgentLab.DesktopHost/Win32DesktopBackend.cs#L125), [dữ liệu tạo StateId](https://github.com/HoangHung997/NotePad/blob/9265c75c4be60e4019d655ef049f66ffc6f779bc/experiments/H2AgentLab.DesktopHost/Win32DesktopBackend.cs#L339).

Đề xuất: giữ kiểm tra danh tính process/cửa sổ; xác thực lại phần tử và điều kiện liên quan đến thao tác. Thao tác tọa độ vẫn cần kiểm tra hình học/ảnh phù hợp. Không bỏ toàn bộ chống trạng thái cũ.

### H2-05 — Ảnh dự phòng chưa cho biết bị che và công cụ desktop có thể bị bỏ qua

Nếu PrintWindow thất bại, H2 dùng CopyFromScreen theo vùng cửa sổ. Vùng đó có thể chứa cửa sổ khác đang che lên; đường này chưa đánh dấu tình trạng bị che. Ngoài ra, PrepareDesktopAsync chỉ chuẩn bị công cụ khi đã có WindowIdentity và có thể bỏ qua công cụ sau lỗi kết nối mà không đưa lý do ra ngoài.

Nguồn H2: [chụp cửa sổ](https://github.com/HoangHung997/NotePad/blob/9265c75c4be60e4019d655ef049f66ffc6f779bc/experiments/H2AgentLab.DesktopHost/Win32DesktopBackend.cs#L545), [chuẩn bị desktop](https://github.com/HoangHung997/NotePad/blob/9265c75c4be60e4019d655ef049f66ffc6f779bc/experiments/H2AgentLab/Integration/H2ProductionToolSession.Desktop.cs#L11).

Đề xuất: trả trạng thái khả dụng của từng công cụ và lý do thất bại; có đường chọn/chuyển ứng dụng trong phạm vi được cấp. Phân biệt ảnh thực của cửa sổ với ảnh vùng màn hình và ảnh bị che.

## 5. Những gì nên học và không nên sao chép nguyên

**Cua Driver:** mã Windows kiểm chứng cặp process ID/cửa sổ; có UIA và đường MSAA cho một số ứng dụng; phân biệt thao tác nền với thao tác cần foreground. Mã capture có Windows.Graphics.Capture, kiểm tra ảnh tối và thông tin bị che khi cần dùng ảnh màn hình. Đây là các điểm cụ thể để đối chiếu DesktopHost của H2. Tuy nhiên, bảng action-support vẫn công khai các trường hợp Refused/Gap; số hàng kiểm thử không đồng nghĩa số thao tác thành công. [S4–S8, S17]

**Microsoft UFO:** học cách phối hợp API Office với thao tác giao diện và tách phát hiện cửa sổ khỏi điều khiển nội dung. Inspector liệt kê Win32 rồi tạo UIA wrapper cho từng cửa sổ. Nhưng phần Word chọn tài liệu có bước so khớp tên theo chuỗi con chung dài nhất. Không nên dùng tên gần giống làm căn cứ ghi/sửa tài liệu. [S9–S10, S15]

**Open Interpreter:** học cách đưa bộ máy Agent ra sau một giao diện tích hợp và dùng các công cụ chuyên dụng. Có thể thử làm backend tùy chọn của H2; trước khi thay mặc định cần kiểm tra đầy đủ phiên, streaming, hủy, lỗi, quyền và công cụ hiện có. Bài smoke tương thích SDK đọc được chỉ kiểm tra một phần nhỏ, không chứng minh toàn bộ hệ thống thay thế được. [S2–S3, S16]

H2 đã có OfficeHost, DesktopHost, thực thi Office trên STA, xác thực trạng thái và các bước đọc lại sau ghi. Cần nâng cấp cách phối hợp và nhận diện; không có cơ sở để bỏ toàn bộ phần này.

## 6. Thiết kế nâng cấp đề nghị cho H2

Luồng cho yêu cầu “đọc tài liệu Word đang mở”:

1. Xác định đúng cửa sổ người dùng muốn làm việc; lưu process, thời điểm process khởi động, HWND và danh tính tài liệu.
2. Dò khả năng kết nối tài liệu Word đang mở, ưu tiên API native theo đúng cửa sổ; kiểm tra lại tài liệu trước mỗi thay đổi.
3. Nếu cần thao tác giao diện, lấy cây UIA/MSAA và ảnh cửa sổ; chọn công cụ theo khả năng thực tế.
4. Nếu chỉ đọc được hình ảnh/giao diện, trả kết quả là một phần và tiếp tục đọc có kiểm soát nếu tác vụ cần thêm. Không tự suy thành toàn văn.
5. Chỉ dùng tệp trên ổ đĩa khi đã làm rõ đó là bản lưu phù hợp. Không tự lưu đè thay đổi của người dùng để làm cho việc đọc dễ hơn.
6. Sau sửa, đọc lại vùng thay đổi và kiểm tra đúng tài liệu, nội dung, định dạng liên quan. Lệnh chạy không lỗi chưa đủ để báo hoàn thành.

Lớp công cụ nên trả thống nhất: đích thực tế, đường truy cập, phạm vi nội dung, độ đầy đủ, nguyên nhân lỗi và khả năng thử lại. Khi sửa thất bại giữa chừng, kiểm tra kết quả hiện tại trước khi chạy lại để tránh sửa lặp.

Lộ trình: sửa nhận diện và chẩn đoán Office → thêm đọc đầy đủ và đối chiếu sau ghi → cải thiện DesktopHost bằng các cơ chế tương ứng Cua → thử Open Interpreter qua adapter riêng. Giữ giao diện H2 đang được duyệt và cho phép so sánh hai backend trên cùng bộ ca kiểm thử.

## 7. Bộ kiểm thử chấp nhận cần có

Tạo tài liệu mẫu riêng, không dùng tài liệu làm việc thật. Mỗi ca chạy ít nhất ba lần; ghi riêng lần đầu khởi động và lần đã kết nối.

| Nhóm | Ca cần chạy | Điều kiện đạt |
|---|---|---|
| Danh tính | Hai tài liệu có tên giống nhau; hai Word instance; hai view của một tài liệu | Đọc/sửa đúng đích; tài liệu còn lại không đổi |
| Độ mới | Tài liệu chưa lưu; thêm nội dung chưa lưu; Save As giữa phiên | Đọc được đúng bản đang soạn hoặc nói rõ giới hạn; không dùng âm thầm bản cũ |
| Độ đầy đủ | Văn bản vượt 1.500 ký tự; nhiều trang; bảng; header/footer; chú thích | Có dấu kiểm tra ở đầu/giữa/cuối và từng phần; không báo đầy đủ nếu bỏ sót |
| Trạng thái | Word bận, hộp thoại đang mở, cửa sổ bị che, đổi focus | Lỗi có nguyên nhân; phục hồi hoặc dừng đúng; không chuyển nhầm tài liệu |
| Giao diện | Con trỏ nhấp nháy, thay đổi zoom/DPI, di chuyển cửa sổ | Quan sát lại phù hợp; không lặp vô hạn stale_state hoặc bấm sai tọa độ |
| Sửa và phục hồi | Chèn/thay thế nhiều đoạn, bảng; lỗi giữa thao tác; thử lại | Đọc lại xác nhận; không sửa hai lần; có bản phục hồi riêng khi phù hợp |
| Định dạng/app | DOCX/DOC/PDF; Word desktop, Word web và ứng dụng khác nếu cài | Báo đúng khả năng từng engine; không coi mọi ứng dụng mở DOCX là Word COM |

Dùng dấu kiểm tra khác nhau như DOC-A/DOC-B và UNSAVED-ONLY để bắt nhầm tài liệu/bản lưu. Ghi build Office, kiến trúc 32/64-bit, Windows, backend, thời gian, lỗi và bằng chứng sau thao tác. Chỉ công bố hỗ trợ những tổ hợp đã chạy thực tế; các phiên bản chưa có phải ghi chưa kiểm chứng.

## 8. Phạm vi đã thực hiện trong lần nghiên cứu này

Đã đọc mã H2 và tải các tệp nguồn liên quan từ những commit nêu trên; đọc tài liệu chính thức của OpenAI và Microsoft. Bản nguồn tham khảo nằm trong `.artifacts/desktop-agent-research-2026-09-22` và `.artifacts/openinterpreter-research-2026-09-22`.

Chưa cài/chạy các Agent bên ngoài, chưa chạy bộ thử Word nêu trên, chưa thay backend hoặc sửa mã ứng dụng. Do đó báo cáo xác nhận thiết kế và điểm yếu trong mã, chưa đo được tỷ lệ thành công thực tế hoặc xác nhận nguyên nhân của một lần lỗi cụ thể trên màn hình.

## Nguồn

- [S1 — Open Interpreter README tại bản được kiểm tra](https://github.com/openinterpreter/openinterpreter/blob/d9b49c828bf61d820c30e343a22033e3e7999583/README.md)
- [S2 — Open Interpreter SDK](https://github.com/openinterpreter/openinterpreter/blob/d9b49c828bf61d820c30e343a22033e3e7999583/docs/sdk.md)
- [S3 — Skill QA tham chiếu agent-browser và cua-driver](https://github.com/openinterpreter/openinterpreter/blob/d9b49c828bf61d820c30e343a22033e3e7999583/codex-rs/skills/src/assets/samples/qa-testing/SKILL.md)
- [S4 — Cua Driver: hợp đồng thao tác](https://github.com/trycua/cua/blob/9bbfa7dd3e27ca7f1861ede70aaca390174493f9/docs/content/docs/reference/cua-driver/contracts.mdx)
- [S5 — Cua Windows: danh tính cửa sổ](https://github.com/trycua/cua/blob/9bbfa7dd3e27ca7f1861ede70aaca390174493f9/libs/cua-driver/rust/crates/platform-windows/src/win32/windows.rs)
- [S6 — Cua Windows: chụp ảnh và tình trạng bị che](https://github.com/trycua/cua/blob/9bbfa7dd3e27ca7f1861ede70aaca390174493f9/libs/cua-driver/rust/crates/platform-windows/src/capture.rs)
- [S7 — Cua Driver: bảng thao tác hỗ trợ và giới hạn](https://github.com/trycua/cua/blob/9bbfa7dd3e27ca7f1861ede70aaca390174493f9/libs/cua-driver/docs/action-support.md)
- [S8 — Cua Windows: UIA và MSAA](https://github.com/trycua/cua/blob/9bbfa7dd3e27ca7f1861ede70aaca390174493f9/libs/cua-driver/rust/crates/platform-windows/src/uia/mod.rs)
- [S9 — UFO: cơ sở kết nối API và ghép tên](https://github.com/microsoft/UFO/blob/be75a7ded2ad98d97819e15ff1b39d4202ac3ac5/ufo/automator/app_apis/basic.py)
- [S10 — UFO: Word COM client](https://github.com/microsoft/UFO/blob/be75a7ded2ad98d97819e15ff1b39d4202ac3ac5/ufo/automator/app_apis/word/wordclient.py)
- [S11 — Microsoft: AccessibleObjectFromWindow và OBJID_NATIVEOM](https://learn.microsoft.com/en-us/windows/win32/api/oleacc/nf-oleacc-accessibleobjectfromwindow)
- [S12 — Microsoft: Word.Window.Hwnd](https://learn.microsoft.com/en-us/office/vba/api/word.window.hwnd)
- [S13 — UI-TARS Desktop / Agent TARS README](https://github.com/bytedance/UI-TARS-desktop/blob/c2ad42e3eb9b27830db41a3e6f51ca7179d9b168/README.md)
- [S14 — OpenAI: Computer Use](https://learn.chatgpt.com/docs/computer-use)
- [S15 — UFO: liệt kê cửa sổ và UIA](https://github.com/microsoft/UFO/blob/be75a7ded2ad98d97819e15ff1b39d4202ac3ac5/ufo/automator/ui_control/inspector.py)
- [S16 — Open Interpreter: smoke kiểm tra tương thích SDK](https://github.com/openinterpreter/openinterpreter/blob/d9b49c828bf61d820c30e343a22033e3e7999583/scripts/test-codex-sdk-compat.sh)
- [S17 — Cua Windows: background/foreground input](https://github.com/trycua/cua/blob/9bbfa7dd3e27ca7f1861ede70aaca390174493f9/libs/cua-driver/rust/crates/platform-windows/src/input/delivery.rs)
- [S18 — OpenAI: vai trò plugin, skill và MCP server](https://developers.openai.com/plugins/concepts/plugins)
