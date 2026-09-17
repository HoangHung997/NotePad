using H2Notes.Core;
using System.Text.Json;

namespace H2AgentLab;

public static class SkillLiveEvaluation
{
    public static async Task<int> Run(string[] args)
    {
        var i = Array.IndexOf(args, "--skills-live");
        if (args.Length < i + 4) throw new IOException("--skills-live endpoint model NEW-output-directory");
        var root = Path.GetFullPath(args[i + 3]); if (Directory.Exists(root)) throw new IOException("Use a new test directory.");
        var workspace = Path.Combine(root, "workspace"); Directory.CreateDirectory(workspace);
        var state = Path.Combine(root, "state"); var log = new List<string>();
        var profile = new AiProfile { BaseUrl = args[i + 1], Model = args[i + 2], OllamaThinking = args.Contains("--no-thinking") ? false : null };
        if (profile.IsOllamaCloud || !Uri.TryCreate(profile.BaseUrl, UriKind.Absolute, out var endpoint) || !endpoint.IsLoopback) throw new IOException("Live fixture evaluation only accepts explicitly selected loopback Ollama.");
        var scripts = new ScriptWorkspace(new(workspace), state, (_, _) => Task.FromResult(true));
        var seed = JsonSerializer.SerializeToElement(await scripts.Run("""
            from openpyxl import Workbook
            from openpyxl.styles import Font
            w=Workbook(); s=w.active; s.title='Tasks'; s.append(['Task','State','Amount','Double','Keep'])
            for row in [('A','done',10),('B','todo',20),('C','done',30)]: s.append(row)
            for r in range(2,5):
                s.cell(r,4,f'=C{r}*2'); s.cell(r,5,'untouched'); s.cell(r,1).font=Font(name='Calibri',size=11,italic=True)
            w.create_sheet('Archive')['A1']='preserve'; w.save('output/input.xlsx')
            """, "", "", default));
        if (seed.GetProperty("exitCode").GetInt32() != 0) throw new IOException(seed.ToString());
        var original = scripts.Read(seed.GetProperty("runId").GetString()!, "input.xlsx");
        File.WriteAllBytes(Path.Combine(workspace, "input.xlsx"), original);
        var session = new LabSession { Workspace = workspace }; var usedSkill = false; var ranCode = false;
        var tools = new AgentTools(new(workspace), state,
            (a, _) => Task.FromResult(a.Title == "Cho AI tự chạy mã trên bản sao trong lượt này" || a.Title == "Lưu kết quả: result.xlsx"),
            (kind, text) => { session.Add(kind, text); if (kind == "tool-result" && text.StartsWith("read_skill")) usedSkill = true; if (kind == "tool-result" && text.StartsWith("run_python")) ranCode = true; }) { ReadOnly = false };
        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(8)); using var runner = new AgentRunner();
        try
        {
            await runner.Run(profile, "", session, tools,
                "Sửa input.xlsx, sheet Tasks: chỉ vùng A2:B4 đổi font thành Arial 14, giữ trạng thái nghiêng đang có. Những hàng có cột B bằng done thì tô nền A:B màu C6EFD5. Không đổi công thức, dữ liệu khác hoặc sheet Archive. Lưu bản mới result.xlsx. Dùng skill phù hợp, tự viết Python, kiểm kết quả rồi xuất tệp; không chỉ đưa hướng dẫn. Không tạo ngày hay dữ liệu mới.",
                (kind, text) => { if (kind is "status" or "tool" or "final" or "metrics") { log.Add($"{DateTimeOffset.Now:O} {kind} {text}"); File.WriteAllLines(Path.Combine(root, "live-eval.txt"), log); } }, () => session.Save(state), stop.Token);
            if (!File.Exists(Path.Combine(workspace, "result.xlsx"))) throw new IOException("No published result.xlsx.");
            var verify = JsonSerializer.SerializeToElement(await scripts.Run("""
                from openpyxl import load_workbook
                from copy import copy
                a=load_workbook('input/input.xlsx'); b=load_workbook('input/result.xlsx')
                assert a.sheetnames==b.sheetnames
                for sa,sb in zip(a,b):
                    assert sa.max_row==sb.max_row and sa.max_column==sb.max_column
                    for row in sa:
                        for ca in row:
                            cb=sb[ca.coordinate]; assert ca.value==cb.value, ca.coordinate
                            changed=sa.title=='Tasks' and 2<=ca.row<=4 and ca.column<=2
                            if changed:
                                assert cb.font.name=='Arial' and cb.font.sz==14
                                assert ca.font.italic==cb.font.italic
                                if sa.cell(ca.row,2).value=='done': assert cb.fill.fgColor.rgb.endswith('C6EFD5')
                                else: assert copy(ca.fill)==copy(cb.fill)
                            else:
                                assert copy(ca.font)==copy(cb.font) and copy(ca.fill)==copy(cb.fill) and ca.number_format==cb.number_format, ca.coordinate
                print('INDEPENDENT_ORACLE_PASS')
                """, "input.xlsx\nresult.xlsx", "", default));
            var pass = usedSkill && ranCode && verify.GetProperty("exitCode").GetInt32() == 0 && original.SequenceEqual(File.ReadAllBytes(Path.Combine(workspace, "input.xlsx")));
            log.Add("ORACLE " + verify); log.Add("LIVE_SKILL_AGENT_PASS=" + pass); File.WriteAllLines(Path.Combine(root, "live-eval.txt"), log); return pass ? 0 : 1;
        }
        catch (Exception ex) { log.Add("NOT_COMPLETE " + ex.GetType().Name + ": " + ex.Message); File.WriteAllLines(Path.Combine(root, "live-eval.txt"), log); return 1; }
    }
}
