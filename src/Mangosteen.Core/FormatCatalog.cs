namespace Mangosteen.Core;

/// <summary>Which output formats are offered for which inputs on Windows.</summary>
/// <remarks>
/// Only formats Mangosteen for Windows can write are listed. HEIC output has no dependable encoder on Windows; video,
/// audio and AVIF need the bundled FFmpeg; PSD, TGA, OpenEXR and the read-only camera formats need Magick.NET.
/// </remarks>
public static class FormatCatalog
{
    /// <summary>True when the app carries FFmpeg: video, audio and AVIF are offered only then.</summary>
    public static bool MediaAvailable { get; set; }

    /// <summary>True when Magick.NET is there: PSD, TGA, OpenEXR, camera RAW, JPEG XL, DDS, DICOM, JPEG 2000.</summary>
    public static bool MagickAvailable { get; set; }

    /// <summary>True when Windows' tar.exe writes 7z (libarchive 3.6 and later); checked once at start.</summary>
    public static bool SevenZipWritable { get; set; }

    /// <summary>True when Windows' tar.exe writes ISO 9660 images.</summary>
    public static bool IsoWritable { get; set; }

    /// <summary>Image formats written without FFmpeg (WIC, SkiaSharp, PDFsharp). BMP is not offered as a target.</summary>
    public static readonly IReadOnlyList<FileFormat> WicImageOutputs =
        [FileFormat.Png, FileFormat.Jpg, FileFormat.Webp, FileFormat.Tiff, FileFormat.Gif, FileFormat.Pdf, FileFormat.Ico];

    /// <summary>Written through Magick.NET; their segments start on the back of the wheel.</summary>
    public static readonly IReadOnlyList<FileFormat> MagickImageOutputs = [FileFormat.Psd, FileFormat.Tga, FileFormat.Exr];

    /// <summary>
    /// Formats the editors (crop, editor, annotate, background, hide…) save an edited image in: the image targets
    /// without PDF and ICO, plus BMP, so an edited BMP stays a BMP. Anything else is saved as PNG (see <see cref="EditedFormat"/>).
    /// </summary>
    public static readonly IReadOnlySet<FileFormat> EditableImageFormats = new HashSet<FileFormat>
    {
        FileFormat.Png, FileFormat.Jpg, FileFormat.Webp, FileFormat.Tiff, FileFormat.Gif, FileFormat.Bmp,
    };

    /// <summary>
    /// The format a tool saves an edited picture in: the original's when it can be written, JPG for camera RAW and
    /// JPEG XL (a PNG of a 50-megapixel photo runs to 100 MB and more; Windows has no HEIC encoder), PNG otherwise.
    /// </summary>
    public static FileFormat EditedFormat(FileFormat source) =>
        EditableImageFormats.Contains(source) ? source : source is FileFormat.Raw or FileFormat.Jxl ? FileFormat.Jpg : FileFormat.Png;

    public static IReadOnlyList<FileFormat> ImageOutputs
    {
        get
        {
            var list = new List<FileFormat> { FileFormat.Png, FileFormat.Jpg, FileFormat.Webp, FileFormat.Tiff };
            if (MediaAvailable)
            {
                list.Add(FileFormat.Avif);
            }
            list.AddRange([FileFormat.Gif, FileFormat.Pdf, FileFormat.Ico]);
            if (MagickAvailable)
            {
                list.AddRange(MagickImageOutputs);
            }
            return list;
        }
    }

    public static readonly IReadOnlyList<FileFormat> VideoOutputs =
        [FileFormat.Mp4, FileFormat.Mov, FileFormat.Mkv, FileFormat.Webm, FileFormat.Avi, FileFormat.Wmv, FileFormat.Gif, FileFormat.Mp3];
    public static readonly IReadOnlyList<FileFormat> AudioOutputs =
        [FileFormat.Mp3, FileFormat.M4a, FileFormat.Wav, FileFormat.Flac, FileFormat.Ogg, FileFormat.Opus, FileFormat.Aiff, FileFormat.Wma];
    public static readonly IReadOnlyList<FileFormat> PdfOutputs =
        [FileFormat.Png, FileFormat.Jpg, FileFormat.Txt, FileFormat.Md, FileFormat.Rtf, FileFormat.Docx, FileFormat.Html];
    /// <summary>Books (EPUB, FB2) convert like documents, and documents become one-chapter books.</summary>
    public static readonly IReadOnlyList<FileFormat> DocumentOutputs =
        [FileFormat.Pdf, FileFormat.Docx, FileFormat.Odt, FileFormat.Rtf, FileFormat.Txt, FileFormat.Md, FileFormat.Html, FileFormat.Epub, FileFormat.Fb2];
    public static readonly IReadOnlyList<FileFormat> SubtitleOutputs = [FileFormat.Srt, FileFormat.Vtt, FileFormat.Ass, FileFormat.Txt];
    public static readonly IReadOnlyList<FileFormat> TableOutputs = [FileFormat.Csv, FileFormat.Tsv, FileFormat.Xlsx, FileFormat.Json];

    public static IReadOnlyList<FileFormat> ArchiveOutputs
    {
        get
        {
            var list = new List<FileFormat> { FileFormat.Zip, FileFormat.Tar, FileFormat.Tgz };
            if (SevenZipWritable)
            {
                list.Add(FileFormat.SevenZip);
            }
            if (IsoWritable)
            {
                list.Add(FileFormat.Iso);
            }
            return list;
        }
    }

    /// <summary>Image inputs WIC can decode. WebP, HEIC and AVIF need the codecs from the Microsoft Store.</summary>
    public static readonly IReadOnlySet<FileFormat> WicImageInputs = new HashSet<FileFormat>
    {
        FileFormat.Png, FileFormat.Jpg, FileFormat.Tiff, FileFormat.Bmp, FileFormat.Gif,
        FileFormat.Ico, FileFormat.Webp, FileFormat.Heic, FileFormat.Avif,
    };

    /// <summary>Image inputs only Magick.NET decodes.</summary>
    public static readonly IReadOnlySet<FileFormat> MagickImageInputs = new HashSet<FileFormat>
    {
        FileFormat.Psd, FileFormat.Tga, FileFormat.Exr, FileFormat.Raw, FileFormat.Jxl, FileFormat.Dds, FileFormat.Dicom, FileFormat.Jp2,
    };

    /// <summary>Image inputs this build can decode.</summary>
    public static IReadOnlySet<FileFormat> ImageInputs =>
        MagickAvailable ? WicImageInputs.Concat(MagickImageInputs).ToHashSet() : WicImageInputs;

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
        FileFormat.Ico, FileFormat.Psd, FileFormat.Tga, FileFormat.Exr,
        FileFormat.Docx, FileFormat.Odt, FileFormat.Rtf, FileFormat.Txt, FileFormat.Md, FileFormat.Html, FileFormat.Epub, FileFormat.Fb2,
        FileFormat.Srt, FileFormat.Vtt, FileFormat.Ass, FileFormat.Csv, FileFormat.Tsv, FileFormat.Xlsx, FileFormat.Json,
        FileFormat.Mp4, FileFormat.Mov, FileFormat.Mkv, FileFormat.Webm, FileFormat.Avi, FileFormat.Wmv,
        FileFormat.Mp3, FileFormat.M4a, FileFormat.Wav, FileFormat.Flac, FileFormat.Ogg, FileFormat.Opus, FileFormat.Aiff, FileFormat.Wma,
        FileFormat.Zip, FileFormat.Tar, FileFormat.Tgz, FileFormat.SevenZip, FileFormat.Iso,
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
