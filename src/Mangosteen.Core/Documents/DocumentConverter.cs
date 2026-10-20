namespace Mangosteen.Core.Documents;

/// <summary>Reads any supported document into the common model and writes it out in another format.</summary>
/// <remarks>Writing PDF needs fonts and layout, so it lives in the Windows app (MigraDoc); see <c>PdfWriter</c>.</remarks>
public static class DocumentConverter
{
    public static RichDocument Read(string path, FileFormat format) => format switch
    {
        FileFormat.Txt => RichDocument.FromPlainText(TextFile.Read(path)),
        FileFormat.Md => MarkdownFormat.Parse(TextFile.Read(path)),
        FileFormat.Html => HtmlFormat.Parse(TextFile.Read(path)),
        FileFormat.Docx => DocxFormat.Read(path),
        FileFormat.Odt => OdtFormat.Read(path),
        FileFormat.Rtf => RtfFormat.Parse(File.ReadAllBytes(path)),
        FileFormat.Pdf => PdfText.Read(path),
        _ => throw new NotSupportedException($"{format.DisplayName()} documents cannot be read."),
    };

    /// <summary>Writes every text format except PDF.</summary>
    public static void Write(RichDocument document, FileFormat format, string output, string title)
    {
        switch (format)
        {
            case FileFormat.Txt:
                TextFile.Write(output, document.ToPlainText());
                break;
            case FileFormat.Md:
                TextFile.Write(output, MarkdownFormat.Write(document));
                break;
            case FileFormat.Html:
                TextFile.Write(output, HtmlFormat.Write(document, title));
                break;
            case FileFormat.Docx:
                DocxFormat.Write(document, output);
                break;
            case FileFormat.Odt:
                OdtFormat.Write(document, output);
                break;
            case FileFormat.Rtf:
                File.WriteAllText(output, RtfFormat.Write(document), System.Text.Encoding.ASCII);
                break;
            default:
                throw new NotSupportedException($"Documents cannot be written as {format.DisplayName()} here.");
        }
    }

    /// <summary>
    /// Converts between text formats. PDF to TXT keeps the page text as it is laid out; everything else goes
    /// through the common model.
    /// </summary>
    public static void Convert(string input, FileFormat source, FileFormat target, string output)
    {
        if (source == FileFormat.Pdf && target == FileFormat.Txt)
        {
            TextFile.Write(output, PdfText.PlainText(input) + "\n");
            return;
        }
        Write(Read(input, source), target, output, OutputNaming.BaseName(input));
    }
}
