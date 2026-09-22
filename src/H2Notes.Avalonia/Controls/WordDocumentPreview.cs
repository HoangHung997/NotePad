using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using DocumentFormat.OpenXml.Packaging;
using W=DocumentFormat.OpenXml.Wordprocessing;

namespace H2Notes.Avalonia.Controls;

internal static class WordDocumentPreview
{
    public static Control Read(string path)
    {
        // Reuse the bounded reader before opening package parts for presentation.
        _=H2Notes.Core.AiDocuments.Read(path);
        using var doc=WordprocessingDocument.Open(path,false);
        var body=doc.MainDocumentPart?.Document.Body;
        var page=new StackPanel { Spacing=12,Width=620 };
        Control Paragraph(W.Paragraph p)
        {
            var text=new SelectableTextBlock { TextWrapping=TextWrapping.Wrap,FontFamily=new FontFamily("Times New Roman"),FontSize=15 };
            var style=p.ParagraphProperties?.ParagraphStyleId?.Val?.Value??"";
            if(style.StartsWith("Heading",StringComparison.OrdinalIgnoreCase)) { text.FontSize=20;text.FontWeight=FontWeight.Bold; }
            var justification=p.ParagraphProperties?.Justification?.Val?.Value;
            text.TextAlignment=justification==W.JustificationValues.Center ? TextAlignment.Center
                : justification==W.JustificationValues.Right ? TextAlignment.Right : TextAlignment.Left;
            foreach(var run in p.Descendants<W.Run>())
            {
                var value=string.Concat(run.ChildElements.Select(e=>e is W.Break ? "\n" : e is W.TabChar ? "    " : e is W.Text t ? t.Text : ""));
                if(value.Length==0)continue;
                var inline=new Run(value);var props=run.RunProperties;
                if(props?.Bold is not null && props.Bold.Val?.Value!=false)inline.FontWeight=FontWeight.Bold;
                if(props?.Italic is not null && props.Italic.Val?.Value!=false)inline.FontStyle=FontStyle.Italic;
                if(props?.Underline is not null)inline.TextDecorations=TextDecorations.Underline;
                if(double.TryParse(props?.FontSize?.Val?.Value,out var size))inline.FontSize=Math.Clamp(size/2*96/72,10,36);
                text.Inlines!.Add(inline);
            }
            return text;
        }
        foreach(var element in body?.ChildElements.Take(1000)??[])
        {
            if(element is W.Paragraph paragraph)page.Children.Add(Paragraph(paragraph));
            else if(element is W.Table table)
            {
                var rows=table.Elements<W.TableRow>().Take(200).ToArray();var count=rows.Select(r=>r.Elements<W.TableCell>().Count()).DefaultIfEmpty(1).Max();
                var grid=new Grid();for(var col=0;col<count;col++)grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1,GridUnitType.Star)));
                for(var row=0;row<rows.Length;row++)
                {
                    grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));var col=0;
                    foreach(var cell in rows[row].Elements<W.TableCell>())
                    {
                        var content=new StackPanel { Spacing=5 };foreach(var p in cell.Elements<W.Paragraph>())content.Children.Add(Paragraph(p));
                        var border=new Border { BorderBrush=Brush.Parse("#CED0D4"),BorderThickness=new Thickness(1),Padding=new Thickness(8),Child=content };
                        Grid.SetRow(border,row);Grid.SetColumn(border,col++);grid.Children.Add(border);
                    }
                }
                page.Children.Add(grid);
            }
        }
        return new Border { Name="WordPagePreview",Padding=new Thickness(36),Background=Brushes.White,BorderBrush=Brush.Parse("#D8D9DC"),BorderThickness=new Thickness(1),
            Child=page,HorizontalAlignment=HorizontalAlignment.Left };
    }
}
