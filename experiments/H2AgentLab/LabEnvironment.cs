using System.Text.Json;

namespace H2AgentLab;

public static class LabEnvironment
{
    public static string[] Problems(SkillCatalog catalog)
    {
        var issues = new List<string>();
        foreach (var name in new[] { "documents", "spreadsheets", "pdf", "coding", "computer-use" })
            if (!catalog.Skills.Any(s => s.Name == name)) issues.Add("Thiếu skill: " + name);
        foreach (var file in new[] { "runtime/worker.py", "runtime-guide.md" })
            if (!File.Exists(Path.Combine(AppContext.BaseDirectory, file))) issues.Add("Thiếu tệp đi kèm: " + file);
        if (WindowsPythonSandbox.RuntimeProblem() is { } problem) issues.Add(problem);
        return issues.ToArray();
    }

    public static string Summary(SkillCatalog catalog)
    {
        var issues = Problems(catalog);
        return issues.Length == 0
            ? $"Đã tìm thấy {catalog.Skills.Count} skill và bộ Python riêng. Thực thi vẫn cần Windows AppContainer; chưa chứng nhận chất lượng model."
            : "Bản chương trình chưa đủ thành phần:\n" + string.Join("\n", issues) + "\nDùng gói Portable đầy đủ hoặc xem PORTABLE.md. Không tải ngầm hoặc bỏ sandbox.";
    }

    // Offline installation check: known fixture code only, no LLM or user documents.
    public static async Task<int> Verify(string output)
    {
        output = Path.GetFullPath(output);
        if (Directory.Exists(output)) throw new IOException("Use a new verification directory.");
        Directory.CreateDirectory(output);
        var log = new List<string> { "Installation verification, synthetic fixtures only; no AI calls.",
            "App: " + AppContext.BaseDirectory, "Python: " + WindowsPythonSandbox.RuntimeRoot,
            "Portable Python selected: " + (Path.GetFullPath(WindowsPythonSandbox.RuntimeRoot) == Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "python"))) };
        try
        {
            var catalog = new SkillCatalog();
            var problems = Problems(catalog);
            if (problems.Length != 0) throw new IOException(string.Join("\n", problems));
            foreach (var skill in catalog.Skills)
            {
                _ = catalog.Read(skill.Name, "SKILL.md");
                _ = catalog.Read(skill.Name, "references/runtime.md");
            }
            log.Add("PASS 5 bundled skills and runtime guidance readable");
            var workspace = Path.Combine(output, "workspace"); Directory.CreateDirectory(workspace);
            var scripts = new ScriptWorkspace(new(workspace), Path.Combine(output, "state"), (_, _) => Task.FromResult(true));
            var result = JsonSerializer.SerializeToElement(await scripts.Run("""
                from docx import Document
                from openpyxl import Workbook, load_workbook
                from reportlab.pdfgen import canvas
                from pypdf import PdfReader
                import pypdfium2 as pdfium
                from PIL import Image
                import lxml.etree
                d=Document(); d.add_paragraph('PORTABLE_WORD_OK'); d.save('output/check.docx')
                assert Document('output/check.docx').paragraphs[0].text=='PORTABLE_WORD_OK'
                w=Workbook(); w.active['A1']='PORTABLE_EXCEL_OK'; w.save('output/check.xlsx')
                assert load_workbook('output/check.xlsx').active['A1'].value=='PORTABLE_EXCEL_OK'
                c=canvas.Canvas('output/check.pdf'); c.drawString(50,750,'PORTABLE_PDF_OK'); c.save()
                assert 'PORTABLE_PDF_OK' in PdfReader('output/check.pdf').pages[0].extract_text()
                pdf=pdfium.PdfDocument('output/check.pdf'); page=pdf[0]; bitmap=page.render(scale=1)
                bitmap.to_pil().save('output/check.png')
                with Image.open('output/check.png') as image: assert image.width>100
                bitmap.close(); page.close(); pdf.close()
                print('WORD_EXCEL_PDF_RENDER_READBACK_PASS')
                """, "", "", default));
            log.Add(result.ToString());
            if (result.GetProperty("exitCode").GetInt32() != 0 || !result.GetProperty("stdout").GetString()!.Contains("WORD_EXCEL_PDF_RENDER_READBACK_PASS"))
                throw new IOException("Sandbox/library check failed. See output above; no unsandboxed fallback was used.");
            log.Add("INSTALLATION_PASS=True; model quality and Microsoft Word installation are not tested.");
            return 0;
        }
        catch (Exception ex) { log.Add("INSTALLATION_PASS=False\n" + ex); return 1; }
        finally { File.WriteAllLines(Path.Combine(output, "installation-check.txt"), log); }
    }
}
