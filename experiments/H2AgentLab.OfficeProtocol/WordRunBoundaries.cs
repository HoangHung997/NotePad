using System.Xml;
using System.Xml.Linq;

namespace H2AgentLab.OfficeProtocol;

/// <summary>Use Word's own OOXML only to locate run boundaries; formatting is still read
/// from native ranges. Fall back when the text cannot be mapped exactly.</summary>
public static class WordRunBoundaries
{
    public static int[]? TryRead(string xml, string expectedText)
    {
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
            { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 10_000_000 });
            var doc = XDocument.Load(reader);
            XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
            var body = doc.Descendants(w + "body").SingleOrDefault();
            var paragraphs = body?.Descendants(w + "p").ToArray();
            if (paragraphs?.Length != 1) return null;
            var lengths = new List<int>();
            var text = new System.Text.StringBuilder();
            foreach (var run in paragraphs[0].Descendants(w + "r"))
            {
                var start = text.Length;
                foreach (var item in run.Elements())
                {
                    if (item.Name == w + "rPr") continue;
                    if (item.Name == w + "t") text.Append(item.Value);
                    else if (item.Name == w + "tab") text.Append('\t');
                    else if (item.Name == w + "br" && (string?)item.Attribute(w + "type") is null or "textWrapping") text.Append('\v');
                    else if (item.Name == w + "cr") text.Append('\v');
                    else if (item.Name == w + "noBreakHyphen") text.Append('\u2011');
                    else if (item.Name == w + "softHyphen") text.Append('\u00ad');
                    else return null;
                }
                if (text.Length > start) lengths.Add(text.Length - start);
            }
            return text.ToString() == expectedText ? lengths.ToArray() : null;
        }
        catch (Exception ex) when (ex is XmlException or InvalidOperationException or ArgumentException) { return null; }
    }
}
