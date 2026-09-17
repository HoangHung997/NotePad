# Prompts: H2 Notes responsive hybrid

Generated using the built-in image generation tool. Ten selected PNGs, one generation per state, with a targeted revision of state01. No app code changed. Original generation files remain in the tool's generated_images directory.

## Style references

- D:\VSstudio\Nodepad\docs\ui-concepts\2026-09-15-direction-2-ai\24-ai-bat-khi-can.png
- D:\VSstudio\Nodepad\docs\ui-concepts\2026-09-15-direction-2-ai\25-khong-gian-tu-sap-xep.png
- Narrow states use state01/state03 as additional layout references; medium/wide states use state05. The user-provided Zalo screenshot informed window proportions and collapsed navigation only, not branding.

## Shared prompt

Use case: ui-mockup. Create a polished, realistic high-fidelity Vietnamese Windows desktop UI mockup for H2 Notes, one single complete app window, not a web page, not a photo, no phone/device, no montage. This is a NEW RESPONSIVE STATE of the SAME app as the supplied style reference. Reference image is STYLE ONLY: preserve warm ivory #F7F3EC, charcoal #302E2B, restrained terracotta #A4573D, light peach selections, fine stone-grey dividers, small terracotta square H2 mark, clear readable humanist sans interface text and modest serif project/note headings. Do NOT copy reference's exact pane positions or big typography. Compact, lightweight, functional density. No purple. Flat interiors, minimal shadow, 4px corner radii. Small app title bar H2 Notes with pin, overflow, and X only; absolutely no minimize/maximize buttons. Never show taskbar, browser chrome, giant marketing headings, decorative illustrations, charts, or gradients.
Use consistent content: selected project number 3, 'Điện Hạt Nhân I - Ninh Thuận', progress '2/3'. Three tasks: checked 'Chia lại khối lượng', checked 'Dựng HSHC', unchecked 'Gửi hồ sơ bổ sung'. Next summary 'Tiếp: Gửi hồ sơ bổ sung'. Notes 'Tạm hoàn thành HSHC, chờ chủ đầu tư chốt.' and 'Cập nhật khối lượng khi có xác nhận mới.' and 'Kiểm tra phụ lục trước khi gửi.' Other projects numbered1 'Đường Gom CT_TA_171' 1/3;2 'Đường tỉnh 156 - Lào Cai' 0/2;4 'Đường gom QL18' 1/4;5 'Cầu Vạn - Kinh Môn' 2/5.
Tiny vertical app navigation rail, width ~52 logical units: project/folder icon selected, note/document icon, AI/spark icon; gear at bottom. Collapsed rail never becomes a detailed project list; project switching is clearly accessible via current project title with chevron and folder rail icon.
No giant headers. At narrow width title wraps to two lines rather than ellipsis. Real text should be legible, not gibberish. Task checkboxes share the leading position/number area, never create a separate column called 'Xong'. No spellcheck squiggles. A short neutral outside caption identifies state number/name, not large decorative text. Preserve complete window with small plain offwhite margin.
AI content when applicable is an illustrative local conversation, with 'Trên máy' / 'API online' choices, explicitly scoped context, composer, and actions requiring user click; never silently edits notes or sends entire data. Use only the UI asked for in each state. No extra popups unless specified.
IMPORTANT: Project title is modest 20 logical pixels, NOT large display typography. Do not show any character limit/counter such as 94/1000. No fictional storage or length limits. Persistent top navigation must be compact to leave room for content.

The final IMPORTANT paragraph was added after the initial generation of state01. State01 was subsequently refined with the targeted prompt below.

## 01. Cửa sổ hẹp: một dự án

Final workspace image: D:/VSstudio/Nodepad/docs/ui-concepts/2026-09-15-responsive-hybrid/01-hep-cong-viec-va-ghi-chu.png

Original generated file: C:\Users\hoang\.codex\generated_images\019db2f7-6081-77a2-98e3-07a34040b090\exec-71ad673a-f26c-4311-b27c-cb697d8a9c13.png

PORTRAIT mockup, window logical proportions 560 wide x 820 tall (approximately 680 displayed pixels wide at Windows scaling as in user's Zalo reference). Outside caption '01 / CỬA SỔ HẸP'. Detailed project list fully hidden, just52-wide icon rail. Top content: small clickable outlined breadcrumb 'Dự án / 3' and a downward chevron; below current project name wraps into TWO comfortable lines, compact 20px-equivalent heading. Right of title use star and overflow, not huge icons. Tabs 'Công việc' (active), 'Ghi chú', and small outlined 'Hỏi AI'. Main area is ONE continuous workspace, NOT chat bubbles. Upper task panel header 'Công việc' with 2/3 and plus button, three comfortable rows, leading checkboxes, task title and tiny supporting note on a second line ('Đã gửi 09/06', 'Đã xong', 'Chờ duyệt'); no horizontally overflowing columns. Next line wraps. Below a modest horizontal resize handle then 'Ghi chú' heading with collapse chevron, compact B I U A toolbar and editable notes body taking the remaining height. Bottom quiet 'Đã lưu' indicator. AI hidden. No dead giant whitespace between title and content. Show pointer near project-title chevron with small tooltip 'Đổi dự án' to make switch affordance clear. This is a compact desktop window, not a phone app.

## 02. Đổi dự án khi danh sách ẩn

Final workspace image: D:/VSstudio/Nodepad/docs/ui-concepts/2026-09-15-responsive-hybrid/02-hep-doi-du-an.png

Original generated file: C:\Users\hoang\.codex\generated_images\019db2f7-6081-77a2-98e3-07a34040b090\exec-3526c566-0293-4913-abc5-66210e38d484.png

PORTRAIT same narrow 560x820 logical window. Caption '02 / ĐỔI DỰ ÁN'. Same rail, app topbar, project workspace as state01. Show the drawer opened INSIDE the window from the left immediately after the rail: wide enough (~360 of508 content units) to read names, leaving narrow lightly dimmed strip of current project workspace visible at right. Drawer title 'Đổi dự án', close X, search field 'Tìm dự án hoặc công việc...', quiet '+ Dự án'. Vertical list of exactly five numbered project rows, long names wrap, progress and next task second line, selected number3 highlighted terracotta leftbar and peach background with small check. Number1 title 'Đường Gom CT_TA_171', next 'Kiểm tra bản vẽ'; number2 'Đường tỉnh 156 - Lào Cai', next 'Chuẩn bị hồ sơ'; selected3 exact name/progress above;4 'Đường gom QL18';5 'Cầu Vạn - Kinh Môn'. Bottom drawer small hint 'Chọn dự án để quay lại làm việc' and 'Ctrl + K'. Cursor points to project2. This is an OVERLAY selector, not a squeezed permanent list; no project editor beside it at equal width. Keep unsaved content visible behind scrim, no fullscreen external dialog.

## 03. Cửa sổ hẹp: tập trung soạn ghi chú

Final workspace image: D:/VSstudio/Nodepad/docs/ui-concepts/2026-09-15-responsive-hybrid/03-hep-soan-ghi-chu.png

Original generated file: C:\Users\hoang\.codex\generated_images\019db2f7-6081-77a2-98e3-07a34040b090\exec-8d9b232c-5d07-4e9e-999b-9142180b509b.png

PORTRAIT same narrow 560x820 logical window. Caption '03 / TẬP TRUNG GHI CHÚ'. Iconrail52, full project list hidden. Same currentproject header two lines with visible downchevron for project switching. Tabs 'Công việc', 'Ghi chú' active, outlined Hỏi AI. Checklist is collapsed to ONE compact strip 'Công việc 2/3' with small chevron and 'Tiếp: Gửi hồ sơ bổ sung' wrapping if necessary. Below, note editor fills almost all remaining space. Toolbar two compact rows at most: 'Cambria', '14', B I U A (terracotta underline), bullet icon, overflow. Selected bold text 'Cần lưu ý' in editor and toolbar B visibly pressed to show formatting synchronization. Notes body: small serif heading 'Ghi chú dự án'; exact notes paragraphs; subheading 'Cần lưu ý' selected pale peach; 'Kiểm tra phụ lục trước khi gửi.'; short checklist-style reminder 'Đối chiếu bản vẽ mới nhất.' No open context menu to avoid clutter. Bottom 'Đã lưu' and tiny 'Mở thành note nổi' action. No AI popup; room dedicated to reading and typing. Do not invent a second permanent notes sidebar.

## 04. Cửa sổ hẹp: gọi AI khi cần

Final workspace image: D:/VSstudio/Nodepad/docs/ui-concepts/2026-09-15-responsive-hybrid/04-hep-mo-ai.png

Original generated file: C:\Users\hoang\.codex\generated_images\019db2f7-6081-77a2-98e3-07a34040b090\exec-114c04d3-2bde-4017-9913-658f0ae524fd.png

PORTRAIT same narrow 560x820 logical window. Caption '04 / AI KHI CỬA SỔ HẸP'. Keep rail and appbar. Instead of tiny overlay, AI temporarily takes the WHOLE content area after rail, with very clear top '← Quay lại dự án' control, then 'Hỏi AI' and small project chip '3. Điện Hạt Nhân I - Ninh Thuận' wrapping if needed. Top segmented choice Trên máy active / API online and gear. Context strip 'Ghi chú đang chọn' plus removable chip and 'Xem ngữ cảnh'. Tiny note 'Bản nháp được giữ nguyên'. Chat: user rightbubble 'Viết gọn đoạn này giúp tôi.' Assistant left with small H2 mark says 'Cập nhật khối lượng sau khi được xác nhận.' Beneath assistant answer show two explicit buttons 'Thay đoạn đã chọn' and 'Chèn bên dưới' stacked/wrapped. Below smaller assistant helper line 'Chỉ thay đổi ghi chú khi bạn chọn áp dụng.' Composer anchored atbottom with 'Hỏi tiếp...' and send arrow; footer 'Trên máy • Chỉ ngữ cảnh đã chọn'. Leave comfortable chat space, no task table cramped beside AI, no additional floatingwindow. Headerback is key to return to notes without losing draft/history.

## 05. Cửa sổ vừa: hiện danh sách dự án

Final workspace image: D:/VSstudio/Nodepad/docs/ui-concepts/2026-09-15-responsive-hybrid/05-vua-danh-sach-chi-tiet.png

Original generated file: C:\Users\hoang\.codex\generated_images\019db2f7-6081-77a2-98e3-07a34040b090\exec-1a33764a-c084-41b1-b7bf-b5279ef4092a.png

LANDSCAPE app mockup, logical window1040x760, caption '05 / KÉO RỘNG: HIỆN DANH SÁCH'. Rail52 + permanent detailed project list260 +mainworkspace remaining width. List header Dự án, collapsepanel icon, plus button, search; exactly five projectrows with numbered tiles, wrapped projectnames, progresscount, secondary next line and restrained tiny progressbar. Selected3. Main header compact projectname canwrap to2lines, star, 'Bố cục' overflow, outlined Hỏi AI. Tasks panel ABOVE notes, headers with subtle grip and collapsechevron. Tasktable columns leading checkbox, 'Công việc', short 'Ghi chú'. Three tasks and remarks. Nextsummarybelow. Horizontal splitter with small grip; below editor header 'Ghi chú', toolbar font,size,B,I,U,A, bullet andlink; two notesparagraphs and 'Cần lưu ý'. AI is absent and does not take space. Panel boundaries thin and aligned, mainworkspace uses allwidth. This illustrates automatic detail list expansion on resize, not a completely different app. Include a small quiet bottom hint 'Danh sách tự hiện khi cửa sổ đủ rộng' in caption area OUTSIDE app.

## 06. Cửa sổ vừa: AI dạng nổi

Final workspace image: D:/VSstudio/Nodepad/docs/ui-concepts/2026-09-15-responsive-hybrid/06-vua-ai-noi.png

Original generated file: C:\Users\hoang\.codex\generated_images\019db2f7-6081-77a2-98e3-07a34040b090\exec-6ec58001-a13f-4490-83b4-68d71aff7127.png

LANDSCAPE same1040x760 app and same detailed leftlist asstate05. Caption '06 / AI NỔI KHI CẦN'. Main noteeditor active, taskscollapsedtoonestrip. Floating movable AI panel about340logicalwide x450high at bottomright INSIDE mainwindow, overlaps part ofnoteeditor but never project list. Header has six-dot draghandle, Hỏi AI, dockbutton andX. Segmented Trên máy/APIonline with APIonline active. Context chip 'Chỉ đoạn đang chọn', small 'Xem dữ liệu gửi'. Note text selected behindpanel in palepeach. Userbubble 'Tóm tắt đoạn này giúp tôi.' Assistantanswer 'Chờ xác nhận khối lượng trước khi hoàn thiện hồ sơ.' Action 'Chèn vào ghi chú' and secondary 'Sao chép'; composer'Hỏi tiếp...' +sendarrow; footer 'API online • Chỉ gửi khi bấm Gửi'. Small griptooltip near header 'Kéo ra cạnh để ghim'. No right AIcolumn yet. Model source no concrete brandname/noapikey. Main window still one consistent design.

## 07. Toàn màn hình: AI ghim bên phải

Final workspace image: D:/VSstudio/Nodepad/docs/ui-concepts/2026-09-15-responsive-hybrid/07-toan-man-hinh-ai-ben-phai.png

Original generated file: C:\Users\hoang\.codex\generated_images\019db2f7-6081-77a2-98e3-07a34040b090\exec-4b69f53b-f15d-40de-9689-2e5f96c65ea0.png

WIDE LANDSCAPE app1440x860 logical proportions, highresolution readable. Caption '07 / TOÀN MÀN HÌNH'. UIuses complete widewindow, no desktop/taskbar. Four vertical regions:52iconrail,248detailed projectlist, CENTERworkspaceabout760, RIGHTAIpanelabout360; exact proportions visually balance, center remains dominant. Maincurrentprojecttitle modest22-equivalent,2/3star, Bố cụccontrol. Center tasks paneltop with three rows and leadingchecks, CôngviệcandGhichúcolumns; middle horizontal splitter; substantialnoteeditorbelowwith synced B I U A toolbar andnotesparagraphs. AIright permanentdocked with distinct thinverticaldivider andresizegrip, header 'Hỏi AI', undock icon andX. Segment Trên máyactive/APIonline andsettingsgear. Context 'Dự án này', '3 công việc', 'Ghi chú', expandable 'Xem ngữ cảnh'. User 'Việc nào cần làm tiếp?' Assistant 'Còn việc gửi hồ sơ bổ sung. Nên kiểm tra phụ lục trước khi gửi.' Proposed actioncard 'Kiểm tra phụ lục' + 'Thêm vào công việc', secondary 'Lưu vào ghi chú'. Chatcomposeranchoredbottom. Footer 'Xử lý trên máy'. Outside window small designcaption 'AI tự ghim khi mở toàn màn hình; có thể ẩn bất cứ lúc nào'. This is samecontent/layout expanded, not chat dominating thecenter. Showingpanelneverimpliesautomaticsending. Do not addmin/max titlebuttons.

## 08. Kéo đổi bố cục: xem trước vị trí ghim

Final workspace image: D:/VSstudio/Nodepad/docs/ui-concepts/2026-09-15-responsive-hybrid/08-keo-ghim-ai-xuong-duoi.png

Original generated file: C:\Users\hoang\.codex\generated_images\019db2f7-6081-77a2-98e3-07a34040b090\exec-8d0feb1d-e95b-456c-8f5d-e127e43ba2c9.png

LANDSCAPE app1160x820logical, caption '08 / KÉO ĐỂ ĐỔI BỐ CỤC'. Sameiconrail anddetailedprojectlistleft. Mainworkspace currentlytasksandnotes SIDE BY SIDE inupper~60%, eachwith smallpanelheader andsixdotgrip. Show drag-in-progress of AI: a semi-opaque SMALL CHAT GHOST around cursor inmainworkspace lower-middle (notcovering leftprojectlist), ghostheader 'Hỏi AI' withsixdotgrip, onefaintmessage. Bottom~35% ofMAINworkspace only is palepeach DOCKTARGET with2pxdashedterracottaborder, centerdockicon and cleartext 'Thả để ghim AI ở phía dưới'. Cursor attachedtogriponfloatingghost, simpledirectionalarrow optionalone. Vertical/horizontalsplitters indicateflexibility. Underprojecttitle small 'Bố cục: Tùy chỉnh' and reseticon. Exactly ONE AIghost andone emptydropzone, notmultiplefinishedAIpanels. Do not permanently reorder tasks duringpreview. Visualemphasisdrop-preview andgrabbedpane movingwithpointer. Keepclear enough tounderstandattachmentmechanism. Notes and tasks content remainreadableabove.

## 09. Ghi chú thường: cửa sổ nhỏ và menu định dạng

Final workspace image: D:/VSstudio/Nodepad/docs/ui-concepts/2026-09-15-responsive-hybrid/09-note-thuong-nho-gon.png

Original generated file: C:\Users\hoang\.codex\generated_images\019db2f7-6081-77a2-98e3-07a34040b090\exec-c2839b23-a611-4f32-bd4f-cbcebf63275a.png

Near-square/portrait mockup of ORDINARY FLOATING NOTE logical560x600, caption '09 / GHI CHÚ THƯỜNG NHỎ GỌN'. SAME warmH2brand but NO projectsidebar orAIpermanentpane, narrowappbarH2Notes,pin,overflow,X. Note title 'Họp giao ban', quiet breadcrumb 'Ghi chú thường' andsmall'Hỏi AI'button. Compactrichtexttoolbar Cambria14 B I U A andoverflow. Editorfillsremainingwindow: smallheading'Nội dung cần trao đổi'; 'Chốt khối lượng với đội thi công.'; 'Kiểm tra hồ sơ trước khi gửi chủ đầu tư.'; bold selected phrase'Nhắc nhanh' palepeach; 'Mang theo bản vẽ mới nhất.'; 'Gọi lại đội khảo sát.' Show neatly designed compact RIGHTCLICK formattingpopup (~245unitswide, not giant) nexttoselectedphrase withfontrow Cambria/14/color swatches, style B I U, options 'Cắt', 'Sao chép', 'Dán', separator, 'Hỏi AI về đoạn này', 'Xóa định dạng'. B bothontoolbarandinpopupvisiblyactivebecause selectionbold. Bottom 'Đã lưu' and quiet pinstatus'Note nổi trên màn hình'. AIhidden untilHỏiAI. Ordinarynote remains independentwindow androomyeditor, no tasklist appended. Minimalwindowframe,noheavyroundedinnerborders.

## 10. Cả chiều rộng và chiều cao tối thiểu

Final workspace image: D:/VSstudio/Nodepad/docs/ui-concepts/2026-09-15-responsive-hybrid/10-kich-thuoc-toi-thieu.png

Original generated file: C:\Users\hoang\.codex\generated_images\019db2f7-6081-77a2-98e3-07a34040b090\exec-e0b32fa4-8f3b-40cc-8022-4beb77091476.png

NEAR-SQUARE compact desktop app window with logical size560width x600height, exact approximate ratio0.93, caption '10 / KÍCH THƯỚC TỐI THIỂU' and small outsidecaption 'Đề xuất: 560 × 600'. This is minimum width AND minimum height, NOT tall version01. Same52wideiconrail. Appbar36logicalhigh H2 Notes,pin,ellipsis,X. Projectheader must be MUCH MORE COMPACT than reference: one dropdown icon then current project name at18logicalpixels wrappingmax2lines, star/overflowright; combine projectselectorwithtitle no separate large breadcrumbrow. Below tabbar 'Công việc' active / 'Ghi chú' inactive / small 'Hỏi AI' button. Because heightisshort, display ONLY taskspanel and do NOT include noteseditor or notespreviewbelow. Taskpanelhead 'Công việc' and2/3plusbutton. Three compact taskrows38-50logicalhigh with leadingcheckboxes, tasktitle andsmallremark. Nextsummarytwo linesifneeded then quietbottomsaveindicator. Remaining spaceforadditional tasks withslimscrollbar. Tinytooltip onGhi chútab 'Chuyển sang ghi chú' andcursor, showinghowtonotes. Detailedprojectlisthiddenbutchevrontitleclear. No giantheader, no duplicatedCôngviệcpanes, noheightcutoff, noAIoverlay, nofloatingmenu. At actualsize typography14logicalbody/18title, not enlargedlikephone. This image prioritizes usableminimumdesktopgeometry andtab-switching. Preservewarmbrand.

## Targeted revision: state01

Input: C:\Users\hoang\.codex\generated_images\019db2f7-6081-77a2-98e3-07a34040b090\exec-419b0334-2e9a-45bc-9495-d314f5b88151.png

Use case: precise-object-edit. Edit this H2 Notes mockup only in TWO precise places. 1) Remove the small '94/1000' text counter entirely from the bottom-right of the note editor, replace it with matching blank editor background; the app has no character limit. 2) Add a thin low-height pale-peach summary strip directly after the third task row and before the horizontal resize divider, with exact legible Vietnamese text 'Tiếp: Gửi hồ sơ bổ sung'. Make room by slightly reducing task row padding if needed; keep full notes visible. Preserve every other detail, full image framing, caption01, warm brand palette, text, selection, rail, app titlebar, all window geometry, project header and typography. Do not add other controls or counters.

