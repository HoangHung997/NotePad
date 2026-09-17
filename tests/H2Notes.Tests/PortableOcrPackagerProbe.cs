using System.Runtime.CompilerServices;
using System.Text.Json;
using H2Notes.Core;

internal static class PortableOcrPackagerProbe
{
    [ModuleInitializer]
    internal static void Run()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "H2Notes-portable-ocr-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "python"));
            Directory.CreateDirectory(Path.Combine(root, "venv", "Scripts"));
            File.WriteAllText(Path.Combine(root, "python", "python.exe"), "probe");
            File.WriteAllText(Path.Combine(root, "venv", "Scripts", "python.exe"), "probe");
            foreach (var engine in new[] { "got-ocr", "docling", "mineru" })
            {
                var models = Path.Combine(root, "models", engine);
                Directory.CreateDirectory(models); File.WriteAllText(Path.Combine(models, "probe.bin"), "probe");
            }
            var manifest = new
            {
                schemaVersion = 1,
                pythonBundled = true,
                engines = new Dictionary<string, object>
                {
                    ["got-ocr"] = new { ready = true, modelsPath = "models/got-ocr" },
                    ["docling"] = new { ready = true, modelsPath = "models/docling" },
                    ["mineru"] = new { ready = true, modelsPath = "models/mineru" }
                }
            };
            File.WriteAllText(Path.Combine(root, "runtime.json"), JsonSerializer.Serialize(manifest));
            var ok = PortableOcrPackager.Inspect(root);
            if (!ok.Ready || !ok.PythonBundled || ok.Engines.Count != 3) throw new Exception("Portable OCR inspection rejected a complete synthetic bundle.");
            File.Delete(Path.Combine(root, "models", "mineru", "probe.bin"));
            var missing = PortableOcrPackager.Inspect(root);
            if (missing.Ready || missing.Engines["mineru"]) throw new Exception("Portable OCR inspection accepted a missing model directory.");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
