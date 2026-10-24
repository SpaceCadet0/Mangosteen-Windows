namespace Mangosteen.Core;

/// <summary>Which output formats are offered for which inputs on Windows.</summary>
/// <remarks>
/// Only formats Mangosteen for Windows can already write are listed; the catalog grows phase by phase. HEIC and
/// AVIF output have no dependable encoder on Windows yet, and video and audio arrive with ffmpeg.
/// </remarks>
public static class FormatCatalog
{
    /// <summary>True when the app carries FFmpeg: video, audio and AVIF are offered only then.</summary>
    public static bool MediaAvailable { get; set; }

    /// <summary>Image formats written without FFmpeg (WIC, SkiaSharp, PDFsharp). BMP is not offered as a target.</summary>
    public static readonly IReadOnlyList<FileFormat> WicImageOutputs =
        [FileFormat.Png, FileFormat.Jpg, FileFormat.Webp, FileFormat.Tiff, FileFormat.Gif, FileFormat.Pdf];

    /// <summary>
    /// Formats the editors (crop, editor, annotate, background, hide…) save an edited image in: the image targets
    /// without PDF, plus BMP, so an edited BMP stays a BMP. Anything else is saved as PNG.
    /// </summary>
    public static readonly IReadOnlySet<FileFormat> EditableImageFormats = new HashSet<FileFormat>
    {
        FileFormat.Png, FileFormat.Jpg, FileFormat.Webp, FileFormat.Tiff, FileFormat.Gif, FileFormat.Bmp,
    };

    private static readonly IReadOnlyList<FileFormat> ImageOutputsWithAvif =
        [FileFormat.Png, FileFormat.Jpg, FileFormat.Webp, FileFormat.Tiff, FileFormat.Avif, FileFormat.Gif, FileFormat.Pdf];

    public static IReadOnlyList<FileFormat> ImageOutputs => MediaAvailable ? ImageOutputsWithAvif : WicImageOutputs;

    public static readonly IReadOnlyList<FileFormat> VideoOutputs =
        [FileFormat.Mp4, FileFormat.Mov, FileFormat.Mkv, FileFormat.Webm, FileFormat.Avi, FileFormat.Wmv, FileFormat.Gif, FileFormat.Mp3];
    public static readonly IReadOnlyList<FileFormat> AudioOutputs =
        [FileFormat.Mp3, FileFormat.M4a, FileFormat.Wav, FileFormat.Flac, FileFormat.Ogg, FileFormat.Opus, FileFormat.Aiff, FileFormat.Wma];
    public static readonly IReadOnlyList<FileFormat> PdfOutputs =
        [FileFormat.Png, FileFormat.Jpg, FileFormat.Txt, FileFormat.Md, FileFormat.Rtf, FileFormat.Docx, FileFormat.Html];
    public static readonly IReadOnlyList<FileFormat> DocumentOutputs =
        [FileFormat.Pdf, FileFormat.Docx, FileFormat.Odt, FileFormat.Rtf, FileFormat.Txt, FileFormat.Md, FileFormat.Html];
    public static readonly IReadOnlyList<FileFormat> SubtitleOutputs = [FileFormat.Srt, FileFormat.Vtt, FileFormat.Txt];
    public static readonly IReadOnlyList<FileFormat> TableOutputs = [FileFormat.Csv, FileFormat.Tsv, FileFormat.Xlsx];
    public static readonly IReadOnlyList<FileFormat> ArchiveOutputs = [FileFormat.Zip, FileFormat.Tar, FileFormat.Tgz];

    /// <summary>Image inputs WIC can decode. WebP, HEIC and AVIF need the codecs from the Microsoft Store.</summary>
    public static readonly IReadOnlySet<FileFormat> ImageInputs = new HashSet<FileFormat>
    {
        FileFormat.Png, FileFormat.Jpg, FileFormat.Tiff, FileFormat.Bmp, FileFormat.Gif,
        FileFormat.Ico, FileFormat.Webp, FileFormat.Heic, FileFormat.Avif,
    };

    /// <summary>An image that becomes one page when several are converted to a single PDF (raster, readable).</summary>
    public static bool IsPageImage(FileFormat format) => ImageInputs.Contains(format);

    /// <summary>
    /// Converting to PDF: two or more photos go into one PDF (a page each, in the selection's order); everything else
    /// in the selection, and a single photo, is converted on its own.
    /// </summary>
    public static (List<string> Pages, List<string> Separate) SplitForPdf(IReadOnlyList<string> paths)
    {
        var pages = paths.Where(p => FileFormats.Detect(p) is { } f && IsPageImage(f)).ToList();
        if (pages.Count < 2)
        {
            return ([], paths.ToList());
        }
        return (pages, paths.Where(p => !pages.Contains(p)).ToList());
    }

    /// <summary>Formats Mangosteen can read as a conversion source.</summary>
    public static bool CanRead(FileFormat format) => format.Category() switch
    {
        FormatCategory.Image => ImageInputs.Contains(format),
        FormatCategory.Document => format != FileFormat.Doc,
        FormatCategory.Video or FormatCategory.Audio => MediaAvailable,
        _ => true,
    };

    /// <summary>Order of segments on the wheel when the selection mixes kinds of files.</summary>
    private static readonly IReadOnlyList<FileFormat> DisplayOrder =
    [
        FileFormat.Png, FileFormat.Jpg, FileFormat.Webp, FileFormat.Tiff, FileFormat.Avif, FileFormat.Gif, FileFormat.Pdf,
        FileFormat.Docx, FileFormat.Odt, FileFormat.Rtf, FileFormat.Txt, FileFormat.Md, FileFormat.Html,
        FileFormat.Srt, FileFormat.Vtt, FileFormat.Csv, FileFormat.Tsv, FileFormat.Xlsx,
        FileFormat.Mp4, FileFormat.Mov, FileFormat.Mkv, FileFormat.Webm, FileFormat.Avi, FileFormat.Wmv,
        FileFormat.Mp3, FileFormat.M4a, FileFormat.Wav, FileFormat.Flac, FileFormat.Ogg, FileFormat.Opus, FileFormat.Aiff, FileFormat.Wma,
        FileFormat.Zip, FileFormat.Tar, FileFormat.Tgz,
    ];

    public static IReadOnlyList<FileFormat> Outputs(FormatCategory category) => category switch
    {
        FormatCategory.Image => ImageOutputs,
        FormatCategory.Pdf => PdfOutputs,
        FormatCategory.Document => DocumentOutputs,
        FormatCategory.Subtitle => SubtitleOutputs,
        FormatCategory.Table => TableOutputs,
        FormatCategory.Archive => ArchiveOutputs,
        FormatCategory.Video => MediaAvailable ? VideoOutputs : [],
        FormatCategory.Audio => MediaAvailable ? AudioOutputs : [],
        _ => [],
    };

    public static IReadOnlyList<FileFormat> Targets(FileFormat source) =>
        CanRead(source) ? Outputs(source.Category()).Where(f => f != source).ToList() : [];

    /// <summary>The detected format of an item (null for folders) and what it can become.</summary>
    public static (FileFormat? Source, IReadOnlyList<FileFormat> Targets) Conversion(string path)
    {
        if (FileFormats.Detect(path) is { } source)
        {
            return (source, Targets(source));
        }
        return (null, Directory.Exists(path) ? ArchiveOutputs : []);
    }

    /// <summary>
    /// Formats offered for a selection. A format is offered when every convertible item can either be
    /// converted to it or already is it (those items are skipped), and at least one item needs converting.
    /// Items that cannot be converted at all are ignored.
    /// </summary>
    public static IReadOnlyList<FileFormat> Targets(IEnumerable<string> paths)
    {
        var items = paths.Select(Conversion).Where(i => i.Targets.Count > 0).ToList();
        if (items.Count == 0)
        {
            return [];
        }
        // A single kind of file keeps that kind's own order.
        var categories = items.Select(i => i.Source?.Category() ?? FormatCategory.Archive).Distinct().ToList();
        var order = categories.Count == 1 ? Outputs(categories[0]) : DisplayOrder;
        return order
            .Where(target => items.All(i => i.Source == target || i.Targets.Contains(target)) && items.Any(i => i.Source != target))
            .ToList();
    }
}
