using System.Diagnostics;
using System.Text.Json;
using H2Notes.Core;

namespace H2AgentLab;

// A synthetic, isolated acceptance test. Never opens or edits a user's document.
public static class WordEditLiveEvaluation
{
    public static async Task<int> Run(string[] args)
    {
        var index = Array.IndexOf(args, "--word-edit-live");
        if (args.Length < index + 4) throw new IOException("--word-edit-live loopback-endpoint model NEW-directory");
        var endpoint = new Uri(args[index + 1]);
        var profile = new AiProfile { BaseUrl = endpoint.ToString(), Model = args[index + 2], OllamaThinking = args.Contains("--no-thinking") ? false : null };
        if (!endpoint.IsLoopback || profile.IsOllamaCloud) throw new IOException("Synthetic test only accepts loopback Ollama.");
        var root = Path.GetFullPath(args[index + 3]);
        if (Directory.Exists(root)) throw new IOException("Use a new test directory.");
        var workspace = Path.Combine(root, "workspace"); Directory.CreateDirectory(workspace);
        var state = Path.Combine(root, "state"); var log = new List<string>();
        var clock = Stopwatch.StartNew();
        void Log(string text)
        {
            log.Add($"{DateTimeOffset.Now:O} +{clock.Elapsed.TotalSeconds:F1}s {text}");
            File.WriteAllLines(Path.Combine(root, "live-eval.txt"), log);
        }
        var scripts = new ScriptWorkspace(new(workspace), state, (_, _) => Task.FromResult(true));
        var seed = JsonSerializer.SerializeToElement(await scripts.Run("""
            from docx import Document
            from docx.shared import Pt, Cm
            d=Document(); s=d.sections[0]; s.top_margin=Cm(2.2); s.left_margin=Cm(2.7)
            r=d.add_paragraph().add_run('DE NGHI THANH TOAN - DU LIEU GIA'); r.bold=True; r.font.name='Arial'; r.font.size=Pt(16)
            p=d.add_paragraph(); p.add_run('Ma doi chieu: '); r=p.add_run('H2-WORD-418'); r.italic=True
            t=d.add_table(rows=2, cols=2); t.cell(0,0).text='Noi dung'; t.cell(0,1).text='Gia tri'
            t.cell(1,0).text='Hang muc mau'; t.cell(1,1).text='1250000'
            d.add_paragraph('Ket thuc noi dung goc.'); s.header.paragraphs[0].text='H2 LAB FIXTURE'
            d.save('output/DNTT.docx')
            """, "", "", default));
        if (seed.GetProperty("exitCode").GetInt32() != 0) throw new IOException(seed.ToString());
        var original = scripts.Read(seed.GetProperty("runId").GetString()!, "DNTT.docx");
        File.WriteAllBytes(Path.Combine(workspace, "DNTT.docx"), original);
        var session = new LabSession { Workspace = workspace }; var usedSkill = false; var ranCode = false;
        var tools = new AgentTools(new(workspace), state, (approval, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            var allow = approval.Title is "Cho AI tự chạy mã trên bản sao trong lượt này" or "Lưu kết quả: DNTT-test.docx";
            Log("APPROVAL " + approval.Title + " allowed=" + allow); return Task.FromResult(allow);
        }, (kind, text) =>
        {
            session.Add(kind, text);
            if (kind == "tool-result" && text.StartsWith("read_skill")) usedSkill = true;
            if (kind == "tool-result" && text.StartsWith("run_python")) ranCode = true;
        }) { ReadOnly = false };
        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(8)); using var runner = new AgentRunner();
        try
        {
            Log("START real model; thinking=" + (profile.OllamaThinking?.ToString() ?? "default") + "; default context, fresh history; no UI-open test");
            await runner.Run(profile, "", session, tools,
                "Mở DNTT.docx trong thư mục đã chọn. Thêm đúng một đoạn ở cuối nội dung tài liệu: đã test thành công. Giữ nguyên toàn bộ nội dung, bảng, font, đậm/nghiêng, lề và header cũ. Lưu bản mới DNTT-test.docx, tuyệt đối không thay DNTT.docx. Đọc skill phù hợp, thực hiện bằng công cụ, đọc lại kiểm tra rồi xuất tệp. Không chỉ đưa mã hoặc hướng dẫn. Đây là dữ liệu mẫu; không mở ứng dụng ngoài trong bài kiểm này.",
                (kind, text) => { if (kind is "status" or "tool" or "recovery" or "final" or "metrics") Log(kind + " " + text); },
                () => session.Save(state), stop.Token);
            if (!File.Exists(Path.Combine(workspace, "DNTT-test.docx"))) throw new IOException("No published DNTT-test.docx.");
            // Independent oracle: the model does not receive this verification code.
            var verify = JsonSerializer.SerializeToElement(await scripts.Run("""
                from docx import Document
                from lxml import etree
                from copy import deepcopy
                a=Document('input/DNTT.docx'); b=Document('input/DNTT-test.docx')
                ns='{http://schemas.openxmlformats.org/wordprocessingml/2006/main}'
                aa=[e for e in a.element.body if e.tag!=ns+'sectPr']
                bb=[e for e in b.element.body if e.tag!=ns+'sectPr']
                assert len(bb)==len(aa)+1, 'Exactly one body paragraph must be appended'
                def semantic(e):
                    return (e.tag, tuple(sorted(e.attrib.items())), e.text or '', tuple(semantic(c) for c in e))
                assert all(semantic(x)==semantic(y) for x,y in zip(aa,bb[:-1])), 'Original content/formatting changed'
                assert bb[-1].tag==ns+'p'
                assert ''.join(e.text or '' for e in bb[-1].iter(ns+'t'))=='đã test thành công', 'Wrong appended text'
                assert semantic(a.element.body.sectPr)==semantic(b.element.body.sectPr), 'Section layout changed'
                assert semantic(a.styles.element)==semantic(b.styles.element), 'Styles changed'
                assert len(a.sections)==len(b.sections)
                for sa,sb in zip(a.sections,b.sections):
                    assert semantic(sa.header._element)==semantic(sb.header._element), 'Header changed'
                    assert semantic(sa.footer._element)==semantic(sb.footer._element), 'Footer changed'
                print('INDEPENDENT_WORD_EDIT_ORACLE_PASS')
                """, "DNTT.docx\nDNTT-test.docx", "", default));
            var structure = await new AgentTools(new(workspace), state, (_, _) => Task.FromResult(false), (_, _) => { })
                .Execute(new("oracle", "check_word", JsonSerializer.SerializeToElement(new { path = "DNTT-test.docx" })), default);
            using var structuralResult = JsonDocument.Parse(structure);
            var pass = usedSkill && ranCode && verify.GetProperty("exitCode").GetInt32() == 0
                && structuralResult.RootElement.TryGetProperty("structureValid", out var valid) && valid.GetBoolean()
                && original.SequenceEqual(File.ReadAllBytes(Path.Combine(workspace, "DNTT.docx")));
            Log("ORACLE " + verify); Log("STRUCTURE " + structure); Log("LIVE_WORD_EDIT_PASS=" + pass);
            return pass ? 0 : 1;
        }
        catch (Exception ex) { Log("NOT_COMPLETE " + ex.GetType().Name + ": " + ex.Message); return 1; }
    }
}
