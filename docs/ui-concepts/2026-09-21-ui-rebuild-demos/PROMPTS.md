# Prompt tạo ảnh demo H2 Notes — 21/09/2026

Chế độ: imagegen tích hợp. Mỗi ảnh dùng một lần tạo riêng, không sửa ảnh baseline và không dùng ảnh tham chiếu.

## 01 · Tổng quan dự án

Tệp: `01-command-center.png`

```text
Use case: ui-mockup.
Asset type: high-fidelity proposed H2 Notes desktop application screenshot, concept 01, overview screen. This is a design proposal with synthetic demo data, not a screenshot of an implemented app.
Create ONE large landscape image, approximately 16:10, sharp enough to inspect typography. Single complete app window almost filling the image with a tiny neutral outer margin, straight-on front view. No device mockup or perspective.
Visual direction: warm editorial project command center, practical professional Vietnamese engineering project software. Ivory #FCFAF7 canvas, subtly warmer #F3EEE7 navigation, white cards, terracotta #A4573D accents, dark charcoal legible text, warm gray rules. Beautiful restrained serif ONLY for the main page heading, clean modern Vietnamese sans-serif elsewhere. Crisp small outline icons; comfortable density, 8px rhythm, 8-10px corner radii, barely visible shadows, no texture.
Structure: slim top title bar bearing small terracotta H2 square and 'H2 Notes'; top-right only pin, ellipsis and X. Narrow left icon rail with 'Dự án' selected, 'Ghi chú', 'Trợ lý' and 'Cài đặt' at the bottom. A calm useful overview, no permanent empty chat.
Main area: page heading 'Tổng quan dự án', short subtitle 'Nhìn nhanh tiến độ, bắt đầu việc cần làm.' Search field 'Tìm dự án…', terracotta '+ Dự án mới'. Filter pills 'Tất cả', 'Cần xử lý', 'Đang làm', 'Đang chờ', 'Hoàn thành'. One compact attention banner 'Cần bạn xử lý · 2' containing exactly two actionable rows: 'Thạch Bích · Xem lại 2 công thức' with 'Mở kết quả', and 'Cầu Vạn · Xác nhận bản vẽ' with 'Xem chi tiết'.
Below, six compact informative project cards in a 3-column by 2-row grid, generous readable card widths:
1 'RPBMVN Thạch Bích', '3/5 công việc', 'Tiếp theo: Hoàn thiện dự toán', terracotta status 'Cần bạn xem', latest activity 'Đã đối chiếu khối lượng'.
2 'Đường gom CT.04', '2/6 công việc', 'Tiếp theo: Kiểm tra bản vẽ', 'Agent đang rà hồ sơ'.
3 'Điện hạt nhân Ninh Thuận', '2/3 công việc', 'Tiếp theo: Gửi hồ sơ bổ sung', 'Chờ phản hồi'.
4 'Cầu Vạn — Kinh Môn', '1/4 công việc', 'Tiếp theo: Xác nhận bản vẽ'.
5 'Quốc lộ 18', '4/7 công việc', 'Tiếp theo: Tổng hợp ý kiến'.
6 'Hồ sơ nghiệm thu', '5/5 công việc', muted green tiny status 'Hoàn thành'.
Thin progress bars must match completed/total counts (60%,33%,67%,25%,57%,100%), but print fractions only, no invented AI percentage.
Bottom small strip 'Đã đồng bộ' and 'Dữ liệu minh họa'. Compact unobtrusive H2 assistant bubble bottom right labeled 'Hỏi H2', not an open overlay hiding projects.
Constraints: exact Vietnamese diacritics and clean readable text; restrained green only for tiny success indicators; no green table theme, no charts, no illustrations, no marketing slogans, no giant dashboard metrics, no second AI inbox database, no raw IDs/JSON, no browser chrome, no minimize/maximize buttons, no overlapping controls. The page must feel like a polished buildable desktop app, not a landing page. Render all described content with coherent alignment and clear hierarchy.
```

## 02 · Agent và tài liệu

Tệp: `02-agent-workspace.png`

```text
Use case: ui-mockup.
Asset type: high-fidelity H2 Notes desktop UI design proposal, concept 02, project Agent workspace with artifact inspector. Synthetic demo data.
Create ONE landscape image approximately 16:10, high resolution, flat straight-on screenshot of a single complete desktop app filling nearly all the canvas. No devices, no perspective, no collage.
Visual direction: precise and calm modern agent workbench, H2 warm ivory #FCFAF7, pale warm-gray sidebar #F3EEE7, white message/document surfaces, restrained terracotta #A4573D accents, charcoal text, very fine warm borders. Clean Vietnamese sans-serif throughout, tiny refined serif touch for project title only, 8px spacing rhythm, subtle 8px corners. Less rounded-card decoration than a dashboard. Strong readability and practical professional density.
Top app titlebar: terracotta H2 mark + 'H2 Notes', pin / ellipsis / X only. Slim icon rail left: project folder selected, notes, assistant, settings bottom.
Three content columns below titlebar: LEFT about 220px project and chat navigation, CENTER about 700px agent conversation, RIGHT about 360px document inspector. Keep center the largest.
Left: 'Dự án', search; project 'RPBMVN Thạch Bích' selected with nested chats 'Rà soát hồ sơ' selected, 'Đối chiếu khối lượng', 'Ghi nhớ cuộc họp'; two other projects below 'Đường gom CT.04' and 'Cầu Vạn — Kinh Môn'. Bottom button '+ Trao đổi mới'.
Center header: 'RPBMVN Thạch Bích', subtitle '3/5 công việc · Tiếp theo: Hoàn thiện dự toán'. Secondary navigation 'Agent' selected, 'Công việc', 'Ghi chú', 'Tệp', 'Lịch sử'. Context chips '[Dự án này]' and '[DuToan.xlsx]'. A continuous rich agent thread, NOT all content inside giant bubbles.
User compact right-aligned pale peach message: 'Rà soát dự toán và chỉ ra những công thức cần sửa.'
Agent left-aligned prose label 'H2 Agent', paragraph 'Đã kiểm tra 12 trang tính. Có 2 công thức cần bạn xem lại.' A collapsed activity strip with chevron 'Đã kiểm tra · 6 thao tác'. One compact tool card 'Excel · Đọc công thức' / 'DuToan.xlsx · BAOCAOGS' with small check. A small real table, headers 'Ô', 'Hiện tại', 'Đề xuất', rows 'D51' | '=B5*C7' | '=$B$5*$C$7' and 'D52' | '=B6*C8' | '=$B$6*$C$8'. Formula cells in readable monospace. Inline approval card title 'Cho phép sửa 2 ô?', body 'DuToan.xlsx · Trang BAOCAOGS', buttons terracotta 'Cho phép một lần' and outline 'Từ chối'. State near card: 'Đang chờ bạn xác nhận'. Do NOT show edits as already applied or completed. Small artifact chip 'Báo cáo kiểm tra'.
Pinned composer bottom CENTER only: white softly rounded border, upper text input placeholder 'Giao việc tiếp theo…'. A SINGLE bottom row with plus, shield 'Hỏi trước khi sửa', flexible space, 'Ollama · Local' dropdown, microphone, dark circular up-arrow send. No giant model setup panel.
Right inspector header 'DuToan.xlsx', subtitle 'Bản xem trước · BAOCAOGS', tabs 'Xem trước' selected / 'Thay đổi' / 'Bằng chứng'. Faithful small spreadsheet preview with row numbers, column headings, highlighted D51 and D52 in pale orange, modest data rows. Bottom short note 'Đề xuất chưa được áp dụng'. Small 'Mở tệp' action.
Constraints: Vietnamese must be correctly spelled and readable; content must agree across conversation and inspector; UI shows agent work, approval and artifact together. No opaque raw tool JSON, no made-up percent, no green theme, no gradients, no purple neon, no redundant floating assistant overlay, no web browser chrome, no minimize/maximize. Subtle footer 'Dữ liệu minh họa'.
```

## 03 · Công việc, ghi chú và Agent

Tệp: `03-hybrid-workspace.png`

```text
Use case: ui-mockup.
Asset type: high-fidelity H2 Notes desktop application proposed UI, concept 03, optional hybrid project detail mode with docked Agent. Synthetic demo data.
Generate ONE landscape image approximately 16:10, crisp high resolution front-facing desktop app screenshot, one window with tiny neutral outer margin. This is a buildable productivity interface, no laptop or phone, no perspective, no collage.
Visual direction: warm hybrid workspace closely honoring H2 Notes ivory/terracotta identity. Ivory #FCFAF7 app, white paper-like editor, terracotta #A4573D primary buttons, warm charcoal, pale peach selected backgrounds, hairline warm gray borders. Compact human-centered design with refined serif headings for project and notes, clean Vietnamese sans-serif for controls. Slightly denser and more document-oriented than a chat-first app, subtle 6px corners and no heavy shadows.
Top title bar H2 terracotta square, 'H2 Notes', pin, ellipsis, X. Narrow 60px navigation rail with 'Dự án' selected, 'Ghi chú', 'Trợ lý', settings at bottom.
Layout: left project navigator about 235px, center working document about 650px, right Agent dock about 400px. Readable widths, no clipping.
Left 'Dự án' and compact plus, search 'Tìm dự án…'. Five numbered project rows with meaningful line wrapping and progress fractions: 'Đường gom CT.04' 2/6, 'Quốc lộ 18' 4/7, selected 'RPBMVN Thạch Bích' 3/5, 'Cầu Vạn — Kinh Môn' 1/4, 'Hồ sơ nghiệm thu' 5/5. Active item pale peach with slim terracotta left stripe.
Center header 'RPBMVN Thạch Bích', small star, subtitle '3/5 công việc', navigation tabs 'Agent', 'Công việc' selected, 'Ghi chú', 'Tệp', 'Lịch sử'; small 'Bố cục' menu.
Upper central work pane titled 'Công việc' with '+ Thêm việc', compact 2-column checklist: three checked items 'Tổng hợp khối lượng', 'Đối chiếu bản vẽ', 'Rà hồ sơ đầu vào'; two unchecked items 'Hoàn thiện dự toán', 'Gửi hồ sơ nghiệm thu'. Second column 'Ghi chú' with short useful notes. Checkbox belongs in the same narrow leading column as row number. Peach Next strip 'Tiếp theo: Hoàn thiện dự toán'.
Subtle horizontal splitter.
Lower central notes pane headed 'Ghi chú dự án', restrained single-line toolbar with font dropdown, size 14, bold italic underline, bullet and link. Visible comfortable reading text:
'Cuộc họp ngày 21/09'
'Chốt khối lượng theo bản vẽ đã duyệt.'
'Đối chiếu phụ lục trước khi gửi chủ đầu tư.'
A short highlighted sentence 'Cần xác nhận 2 công thức trong dự toán.' Text is not tiny; notes use natural paragraph spacing.
Right dock header 'H2 Agent' with detach, pin, close. Context chips 'Dự án này' and 'DuToan.xlsx'. Compact user message 'Những việc nào cần hoàn thành trước khi gửi hồ sơ?'. Agent document-like reply 'Còn 2 việc cần hoàn tất:' then numbered 'Hoàn thiện dự toán.' and 'Gửi hồ sơ nghiệm thu.' A single activity summary 'Đã đọc công việc và ghi chú'. Short result card 'Cần bạn xem' / 'Xác nhận 2 công thức trong DuToan.xlsx' with 'Mở kết quả'. Do not imply unapproved edits already happened.
Bottom right composer in dock: white rounded rectangle, input 'Hỏi tiếp…', SINGLE lower action row plus, shield icon (label hidden for narrow space), 'Ollama · Local' dropdown, microphone, dark circular up-arrow send. Footer inside app small 'Đã đồng bộ' and 'Dữ liệu minh họa'.
Constraints: exact Vietnamese diacritics, coherent typography, practical resize handles, all panes aligned and independently scrollable, warm hybrid identity, no earlier green table, no decorative charts, no fake technical JSON, no full screen overlay, no browser bar or minimize/maximize buttons. This optional detail+Agent layout coexists with an Agent-first project home.
```

