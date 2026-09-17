# H2 Agent Lab: chuyen sang may Windows khac

## Cach mo

1. Dung goi `H2-Agent-Lab-Portable-*.zip`, giai nen TOAN BO vao thu muc rieng tren o NTFS co quyen ghi (vi du Documents/H2-Agent-Lab). Khong chay tu trong ZIP, khong chi chep exe, khong de trong Program Files/thu muc chi doc.
2. Mo `H2AgentLab.exe`. Goi da kem .NET Desktop Runtime va Python 3.12 x64 cung thu vien Word/Excel/PDF; khong can cai Python hay .NET rieng. Can Windows 10/11 x64 ho tro AppContainer.
3. Mo **Ky nang & moi truong**: phai thay 5 skill va Python trong thu muc `python` ngay canh app. Neu bao thieu, giai nen lai goi day du. Khong dung Python ngoai sandbox de thay the.
4. Chon thu muc tai lieu tren MAY MOI. Thu muc rong thi list_files tra rong la dung; tai lieu may cu khong tu duoc chuyen sang.
5. Vao **Ket noi AI**: chon Ollama/model tren may moi, dia chi Ollama LAN, hoac API. Goi khong kem Ollama, trong so model, tai khoan hay API key. `localhost` luon chi may dang chay app, khong chi may cu.
6. Thu **Tao du lieu mau** truoc khi dung tai lieu that. AI chay ma tren ban sao sau khi duyet; xuat tep can duyet rieng. Mo DOCX can Word hoac ung dung mac dinh doc DOCX tren may moi.

## Thanh phan can giu cung nhau

- H2AgentLab.exe, cac DLL va tep .NET di kem.
- skills/: documents, spreadsheets, pdf, coding, computer-use.
- runtime/worker.py, runtime-guide.md, skill-sources/.
- python/: Python va cac thu vien, giay phep di kem.
- package-manifest.json: danh sach tep/hash cua goi phat hanh, khong phai chu ky so.

Lich su/cau hinh tren may moi van luu rieng tai `%LOCALAPPDATA%/H2AgentLab`; goi KHONG kem lich su, ket noi, du lieu thu nghiem, API key hay tai lieu ca nhan cua nguoi dong goi. Day la portable ve thanh phan chuong trinh, khong phai dong bo du lieu hai may.

## Chan doan

- `Private Python runtime not prepared`: ban cu thieu runtime rieng. Thay bang goi Portable day du.
- `list_skills` thieu query: da sua; co the goi `{}` de liet ke tat ca. Tim kiem khong khop se tra danh sach ten skill thuc.
- `discover_available_skills`: khong phai cong cu cua Lab. AI phai chon `list_skills`; app khong tu thuc thi ten cong cu bia ra.
- `list_files` rong: kiem tra dung thu muc va co tep duoc ho tro. Khong tu suy rang file da bi xoa.
- Khong co cong cu khoi dong Word rong. `open_file` mo tai lieu co san sau khi duyet; khong dong nghia dieu khien Word tuy y.
- Quy trinh AI van dang thu nghiem; du thanh phan khong bao dam model se viet ma dung.

Kiem tra cai dat khong dung AI (du lieu gia, thu muc ket qua phai moi):

```powershell
H2AgentLab.exe --verify-install "C:\Users\YOUR_NAME\Documents\Lab-install-check"
```

Doc `installation-check.txt` trong thu muc ket qua. Bai nay tao/doc lai Word, Excel, PDF va render PDF trong AppContainer; khong xac nhan chat/model hay dieu khien giao dien da dat.
