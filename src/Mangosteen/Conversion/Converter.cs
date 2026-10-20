using Mangosteen.Core;
using Mangosteen.Core.Documents;

namespace Mangosteen.Conversion;

/// <summary>Entry point for format conversion: picks the converter and writes the result next to the input.</summary>
internal static class Converter
{
    /// <summary>Converts <paramref name="input"/> and returns the new file or folder, or null when it already has that format.</summary>
    public static string? Convert(string input, FileFormat target, CancellationToken token = default, Action<double>? progress = null)
    {
        var (source, targets) = FormatCatalog.Conversion(input);
        if (source == target)
        {
            return null;
        }
        if (!targets.Contains(target))
        {
            throw new NotSupportedException(source is { } s
                ? $"{s.DisplayName()} cannot be converted to {target.DisplayName()}."
                : $"{Path.GetFileName(input)} cannot be converted.");
        }
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(input));
        var directory = Path.GetDirectoryName(path)!;
        var baseName = source is null ? Path.GetFileName(path) : OutputNaming.BaseName(path);

        // Several PDF pages become a folder of images.
        if (source == FileFormat.Pdf && target.Category() == FormatCategory.Image && PdfRenderer.PageCount(path) > 1)
        {
            return OutputNaming.WriteAtomically(directory, $"{baseName} ({target.DisplayName()})", "",
                folder => PdfRenderer.RenderPages(path, target, folder, baseName, token));
        }
        // A workbook with several sheets becomes a folder with one table per sheet.
        if (source == FileFormat.Xlsx)
        {
            var sheets = Tables.ReadXlsx(path).Where(s => s.Rows.Count > 0).ToList();
            if (sheets.Count > 1)
            {
                return OutputNaming.WriteAtomically(directory, $"{baseName} ({target.DisplayName()})", "",
                    folder => Tables.WriteSheets(sheets, target, folder));
            }
        }

        return OutputNaming.WriteAtomically(directory, baseName, target.Extension(), output =>
        {
            switch (source?.Category())
            {
                case null:
                    Archives.Pack([path], target, output);
                    break;
                case FormatCategory.Image when target == FileFormat.Avif:
                    Media.MediaConverter.ImageToAvif(path, output, token);
                    break;
                case FormatCategory.Image:
                    ImageConverter.Write(path, target, output);
                    break;
                case FormatCategory.Video or FormatCategory.Audio:
                    Media.MediaConverter.Convert(path, target, output, progress, token);
                    break;
                case FormatCategory.Pdf when target.Category() == FormatCategory.Image:
                    ImageConverter.WriteImage(PdfRenderer.RenderPage(path, 0), target, output);
                    break;
                case FormatCategory.Pdf:
                    DocumentConverter.Convert(path, FileFormat.Pdf, target, output);
                    break;
                case FormatCategory.Document when target == FileFormat.Pdf:
                    PdfWriter.WriteDocument(DocumentConverter.Read(path, source.Value), baseName, output);
                    break;
                case FormatCategory.Document:
                    DocumentConverter.Convert(path, source.Value, target, output);
                    break;
                case FormatCategory.Subtitle:
                    Subtitles.Convert(path, target, output);
                    break;
                case FormatCategory.Table:
                    Tables.Convert(path, source.Value, target, output);
                    break;
                case FormatCategory.Archive:
                    Archives.Repack(path, source.Value, target, output);
                    break;
                default:
                    throw new NotSupportedException($"{source.Value.DisplayName()} cannot be converted yet.");
            }
        });
    }

    /// <summary>One PDF with a page per image, next to the first image: its name for one image, "Images" for several.</summary>
    public static string CreatePdf(IReadOnlyList<string> paths)
    {
        var images = paths.Where(p => FileFormats.Detect(p) is { } f && FormatCatalog.ImageInputs.Contains(f)).ToList();
        if (images.Count == 0)
        {
            throw new InvalidOperationException("There are no images to put into a PDF.");
        }
        var first = Path.GetFullPath(images[0]);
        var name = images.Count == 1 ? OutputNaming.BaseName(first) : "Images";
        return OutputNaming.WriteAtomically(Path.GetDirectoryName(first)!, name, "pdf", output =>
            PdfWriter.ImagesToPdf(images.Select(p => (ImageConverter.Decode(p), FileFormats.Detect(p) == FileFormat.Jpg)), output));
    }

    /// <summary>The PDFs in the given order as one "Merged.pdf" next to the first.</summary>
    public static string MergePdfs(IReadOnlyList<string> paths)
    {
        var pdfs = paths.Where(p => FileFormats.Detect(p) == FileFormat.Pdf).ToList();
        if (pdfs.Count < 2)
        {
            throw new InvalidOperationException("Merging needs at least two PDFs.");
        }
        var first = Path.GetFullPath(pdfs[0]);
        return OutputNaming.WriteAtomically(Path.GetDirectoryName(first)!, "Merged", "pdf", output => PdfWriter.Merge(pdfs, output));
    }
}
