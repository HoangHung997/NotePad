"""Small bridge contract tests; no model imports or network are needed."""
import contextlib
import importlib.util
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("bridge", HERE / "convert.py")
bridge = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bridge)


class BridgeTests(unittest.TestCase):
    def test_grounded_prose_correction_preserves_numbers_tables_and_code(self):
        original = "## Bao cao tien do du an\nNgay 15 thang 9 hoan thanh\n| Bao cao tien do | 42 |\n```\nBao cao tien do du an\n```"
        result = bridge.repair_prose_lines(original, ["Báo cáo tiến độ dự án", "Ngày 16 tháng 9 hoàn thành", "Báo cáo tiến độ 45"])
        self.assertIn("## Báo cáo tiến độ dự án", result)
        self.assertIn("Ngay 15 thang 9 hoan thanh", result)
        self.assertIn("| Bao cao tien do | 42 |", result)
        self.assertIn("```\nBao cao tien do du an\n```", result)

    def test_mineru_relocation_uses_new_root_without_rewriting_original(self):
        with tempfile.TemporaryDirectory(prefix="H2 moved bundle ") as directory:
            root = Path(directory)
            config = root / "mineru.json"
            original = json.dumps({"config_version": "1.3.2", "models-dir": {"pipeline": "Z:/old-machine/models"}})
            config.write_text(original, encoding="utf-8")
            previous = os.environ.get("MINERU_TOOLS_CONFIG_JSON")
            with self.assertRaisesRegex(RuntimeError, "test failure"):
                with bridge.mineru_configuration(root):
                    temporary = Path(os.environ["MINERU_TOOLS_CONFIG_JSON"])
                    effective = json.loads(temporary.read_text(encoding="utf-8"))
                    self.assertEqual(str(root.resolve()), effective["models-dir"]["pipeline"])
                    self.assertEqual("local", effective["model-source"])
                    self.assertEqual(original, config.read_text(encoding="utf-8"))
                    raise RuntimeError("test failure")
            self.assertEqual(previous, os.environ.get("MINERU_TOOLS_CONFIG_JSON"))
            self.assertFalse(temporary.exists())
            self.assertEqual(original, config.read_text(encoding="utf-8"))

    def test_document_image_is_lossless_pdf_and_source_is_preserved(self):
        from PIL import Image
        import pypdfium2 as pdfium
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "image.png"
            with Image.new("RGB", (120, 60), "white") as image:
                image.paste("red", (0, 0, 20, 20))
                image.save(source)
            before = source.read_bytes()
            with bridge.document_pdf(source, root) as converted:
                self.assertEqual(1, bridge.pdf_pages(converted, 1))
                with pdfium.PdfDocument(converted) as pdf, contextlib.closing(pdf[0]) as page:
                    with contextlib.closing(page.render(scale=2)) as bitmap:
                        with bitmap.to_pil().convert("RGB") as raster:
                            self.assertEqual((120, 60), raster.size)
                            self.assertEqual((255, 0, 0), raster.getpixel((10, 10)))
                            self.assertEqual((255, 255, 255), raster.getpixel((100, 50)))
            self.assertFalse(converted.exists())
            self.assertEqual(before, source.read_bytes())

    def test_corrupt_animated_and_oversized_images_are_rejected(self):
        from PIL import Image
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "image.png"
            source.write_bytes(b"not an image")
            with self.assertRaises(bridge.BridgeError):
                with bridge.document_pdf(source, root):
                    self.fail("Corrupt image accepted")
            with Image.new("RGB", (14500, 1), "white") as image:
                image.save(source)
            with self.assertRaises(bridge.BridgeError):
                with bridge.document_pdf(source, root):
                    self.fail("Oversized image accepted")
            with Image.new("RGB", (10, 10), "red") as a, Image.new("RGB", (10, 10), "blue") as b:
                a.save(source, save_all=True, append_images=[b])
            with self.assertRaises(bridge.BridgeError):
                with bridge.document_pdf(source, root):
                    self.fail("Animated image silently truncated")
            self.assertEqual([source], list(root.iterdir()))

    def test_empty_output_rejected(self):
        with self.assertRaises(bridge.BridgeError):
            bridge.validate_text(" \n ", 100)

    def test_character_limit_never_truncates(self):
        with self.assertRaises(bridge.BridgeError):
            bridge.validate_text("abcd", 4)
        self.assertEqual("abc\n", bridge.validate_text("abc", 4))
        with self.assertRaises(bridge.BridgeError):
            bridge.validate_text("\U0001f600", 2)

    def test_got_latex_table_becomes_markdown(self):
        text = r"\begin{tabular}{|l|l|}\hline Item & Count \\\hline Alpha & 12 \\\hline\end{tabular}"
        result = bridge.got_markdown(text)
        self.assertIn("| Item | Count |", result)
        self.assertIn("| Alpha | 12 |", result)
        self.assertNotIn("begin{tabular}", result)

    def test_complex_got_table_preserved_not_executed(self):
        text = r"\begin{tabular}{cc}\multicolumn{2}{c}{Example}\end{tabular}"
        self.assertIn("```latex", bridge.got_markdown(text))

    def test_atomic_output_refuses_overwrite(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "result.md"
            bridge.atomic_output(path, "first\n")
            with self.assertRaises(FileExistsError):
                bridge.atomic_output(path, "second\n")
            self.assertEqual("first\n", path.read_text())
            self.assertEqual([path], list(Path(directory).iterdir()))

    def test_receipt_rejects_missing_and_traversal(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            with self.assertRaises(bridge.BridgeError):
                bridge.verified_models(root, "got-ocr")
            (root / "models.json").write_text(json.dumps({"schemaVersion": 1, "engine": "got-ocr",
                "files": [{"path": "../escape", "size": 1, "sha256": "a"}]}))
            with self.assertRaises(bridge.BridgeError):
                bridge.verified_models(root, "got-ocr")

    def test_pdf_page_count_enforced_before_models(self):
        from reportlab.pdfgen.canvas import Canvas
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / "101-pages.pdf"
            canvas = Canvas(str(source))
            for page in range(101):
                canvas.drawString(20, 20, str(page + 1))
                canvas.showPage()
            canvas.save()
            with self.assertRaises(bridge.BridgeError) as error:
                bridge.pdf_pages(source, 100)
            self.assertEqual(2, error.exception.code)

    def test_network_and_subprocess_blocked(self):
        code = f'''import runpy,socket,subprocess
from pathlib import Path
b=runpy.run_path({str(HERE / 'convert.py')!r})
b['offline_policy'](Path.cwd())
for action in [lambda:socket.create_connection(('127.0.0.1',9)),lambda:subprocess.run(['never-run'])]:
 try: action()
 except b['BridgeError'] as e: assert e.code == 5
 else: raise AssertionError('offline guard failed')
'''
        result = subprocess.run([sys.executable, "-I", "-c", code], capture_output=True, text=True)
        self.assertEqual(0, result.returncode, result.stderr)

    def test_parser_limits_before_loading(self):
        for value in ("0", "101"):
            with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
                status = bridge.main(["--engine", "got-ocr", "--input", str(HERE / "absent.pdf"),
                                      "--output", str(HERE / "absent.md"), "--models", str(HERE),
                                      "--max-pages", value])
            self.assertEqual(2, status)

    def test_layout_rejects_empty_pages_and_image_only_document(self):
        import runpy
        from reportlab.pdfgen.canvas import Canvas
        build = runpy.run_path(str(HERE / "layout.py"))["reconstruct"]
        with self.assertRaises(ValueError):
            build(Path("not-read.pdf"), [])
        with self.assertRaises(ValueError):
            build(Path("not-read.pdf"), [{}] * 11)
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / "blank.pdf"
            canvas = Canvas(str(source), pagesize=(595, 842)); canvas.showPage(); canvas.save()
            with self.assertRaises(ValueError):
                build(source, [{"page_idx": 0, "page_size": [595, 842], "para_blocks": []}])

    def test_layout_preserves_graphics_and_exports_text_without_modifying_pdf(self):
        import runpy
        import hashlib
        import base64
        from reportlab.pdfgen.canvas import Canvas
        from PIL import Image
        build = runpy.run_path(str(HERE / "layout.py"))["reconstruct"]
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / "fixture.pdf"
            canvas = Canvas(str(source), pagesize=(595, 842))
            canvas.setFont("Times-Roman", 14); canvas.drawString(70, 700, "Editable text")
            canvas.setFillColorRGB(1, 0, 0); canvas.rect(70, 600, 50, 20, fill=1, stroke=0)
            canvas.showPage(); canvas.save()
            before = hashlib.sha256(source.read_bytes()).digest()
            result = build(source, [{"page_idx": 0, "page_size": [595, 842], "para_blocks": [
                {"index": 0, "type": "text", "lines": [{"bbox": [68, 125, 150, 145], "spans": [{"type": "text", "content": "Editable text", "score": .9}]}]},
                {"index": 1, "type": "image", "bbox": [70, 222, 120, 242]}]}])
            self.assertEqual("Editable text", result["pages"][0]["items"][0]["text"])
            self.assertEqual(before, hashlib.sha256(source.read_bytes()).digest())
            image = Image.open(io.BytesIO(base64.b64decode(result["pages"][0]["backgroundPng"])))
            self.assertEqual((255, 0, 0), image.getpixel((150, 455)))


if __name__ == "__main__":
    unittest.main()
