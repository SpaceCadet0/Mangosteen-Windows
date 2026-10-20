using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Mangosteen.Core.Documents;

/// <summary>Reads the text layer of a PDF (scans without text give nothing; OCR comes with the Tools phase).</summary>
public static class PdfText
{
    /// <summary>The text of every page in reading order, pages separated by a blank line.</summary>
    public static string PlainText(string path)
    {
        using var pdf = PdfDocument.Open(path);
        return string.Join("\n\n", pdf.GetPages().Select(page => ContentOrderTextExtractor.GetText(page).Trim()));
    }

    /// <summary>
    /// Paragraphs from the text layer: blank lines separate paragraphs, and lines inside a paragraph are joined
    /// (a word split by a hyphen at the end of a line is put back together).
    /// </summary>
    public static RichDocument Read(string path)
    {
        var document = new RichDocument();
        foreach (var chunk in PlainText(path).Replace("\r\n", "\n").Split("\n\n"))
        {
            var lines = chunk.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            if (lines.Count == 0)
            {
                continue;
            }
            var text = new StringBuilder(lines[0]);
            foreach (var line in lines.Skip(1))
            {
                if (text.Length > 1 && text[^1] == '-' && char.IsLetter(text[^2]) && char.IsLower(line[0]))
                {
                    text.Length--;
                }
                else
                {
                    text.Append(' ');
                }
                text.Append(line);
            }
            var block = new Block(BlockType.Paragraph);
            block.Add(new Run(text.ToString()));
            document.Blocks.Add(block);
        }
        return document;
    }
}
