namespace Mangosteen.Core;

public enum FormatCategory
{
    Image,
    Pdf,
    Document,
    Subtitle,
    Table,
    Archive,
    Video,
    Audio,
}

/// <summary>A file format Mangosteen recognises. Detection is by extension (and a signature for .ts).</summary>
public enum FileFormat
{
    // Images
    Jpg, Png, Webp, Heic, Tiff, Avif, Bmp, Gif,
    // Images: ICO is written by Mangosteen itself; PSD, TGA and OpenEXR are read and written through Magick.NET
    Svg, Ico, Psd, Jp2, Tga, Exr,
    // Images (read only, through Magick.NET). Raw stands for every camera RAW extension.
    Raw, Jxl, Dds, Dicom,
    Pdf,
    // Documents
    Docx, Doc, Odt, Rtf, Txt, Md, Html,
    // Electronic books
    Epub, Fb2,
    // Subtitles
    Srt, Vtt, Ass,
    // Tables (JSON as an array of objects)
    Csv, Tsv, Xlsx, Json,
    // Archives: 7z and ISO are written through Windows' tar.exe; the second line is read only
    Zip, Tar, Tgz, SevenZip, Iso,
    Rar, Txz, Tbz, Gz, Cab,
    // Video (m4v and the second line are read only)
    Mp4, Mov, Mkv, Webm, Avi, Wmv, M4v,
    Flv, Mpg, Ts, M2ts, Vob, Ogv, ThreeGP,
    // Audio (aac and the second line are read only)
    Mp3, M4a, Wav, Flac, Ogg, Opus, Aiff, Wma, Aac,
    Ape, Wv, Amr, Ac3, Dts,
}

public static class FileFormats
{
    private static readonly Dictionary<string, FileFormat> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        ["jpg"] = FileFormat.Jpg, ["jpeg"] = FileFormat.Jpg, ["jpe"] = FileFormat.Jpg, ["jfif"] = FileFormat.Jpg,
        ["png"] = FileFormat.Png,
        ["webp"] = FileFormat.Webp,
        ["heic"] = FileFormat.Heic, ["heif"] = FileFormat.Heic, ["hif"] = FileFormat.Heic,
        ["tif"] = FileFormat.Tiff, ["tiff"] = FileFormat.Tiff,
        ["avif"] = FileFormat.Avif,
        ["bmp"] = FileFormat.Bmp, ["dib"] = FileFormat.Bmp,
        ["gif"] = FileFormat.Gif,
        ["svg"] = FileFormat.Svg,
        ["ico"] = FileFormat.Ico,
        ["psd"] = FileFormat.Psd,
        ["jp2"] = FileFormat.Jp2, ["j2k"] = FileFormat.Jp2, ["jpf"] = FileFormat.Jp2, ["jpx"] = FileFormat.Jp2,
        ["tga"] = FileFormat.Tga,
        ["exr"] = FileFormat.Exr,
        ["jxl"] = FileFormat.Jxl,
        ["dds"] = FileFormat.Dds,
        ["dcm"] = FileFormat.Dicom, ["dicom"] = FileFormat.Dicom,
        ["pdf"] = FileFormat.Pdf,
        ["docx"] = FileFormat.Docx, ["doc"] = FileFormat.Doc, ["odt"] = FileFormat.Odt, ["rtf"] = FileFormat.Rtf,
        ["txt"] = FileFormat.Txt, ["text"] = FileFormat.Txt, ["md"] = FileFormat.Md, ["markdown"] = FileFormat.Md,
        ["html"] = FileFormat.Html, ["htm"] = FileFormat.Html, ["xhtml"] = FileFormat.Html,
        ["epub"] = FileFormat.Epub, ["fb2"] = FileFormat.Fb2,
        ["srt"] = FileFormat.Srt, ["vtt"] = FileFormat.Vtt, ["ass"] = FileFormat.Ass, ["ssa"] = FileFormat.Ass,
        ["csv"] = FileFormat.Csv, ["tsv"] = FileFormat.Tsv, ["tab"] = FileFormat.Tsv, ["xlsx"] = FileFormat.Xlsx, ["json"] = FileFormat.Json,
        ["zip"] = FileFormat.Zip, ["tar"] = FileFormat.Tar, ["tgz"] = FileFormat.Tgz, ["7z"] = FileFormat.SevenZip,
        ["iso"] = FileFormat.Iso, ["rar"] = FileFormat.Rar, ["txz"] = FileFormat.Txz, ["tbz"] = FileFormat.Tbz,
        ["tbz2"] = FileFormat.Tbz, ["gz"] = FileFormat.Gz, ["cab"] = FileFormat.Cab,
        ["mp4"] = FileFormat.Mp4, ["mov"] = FileFormat.Mov, ["qt"] = FileFormat.Mov, ["mkv"] = FileFormat.Mkv, ["webm"] = FileFormat.Webm,
        ["avi"] = FileFormat.Avi, ["wmv"] = FileFormat.Wmv, ["m4v"] = FileFormat.M4v,
        ["flv"] = FileFormat.Flv, ["mpg"] = FileFormat.Mpg, ["mpeg"] = FileFormat.Mpg, ["mpe"] = FileFormat.Mpg,
        ["m1v"] = FileFormat.Mpg, ["m2v"] = FileFormat.Mpg, ["vob"] = FileFormat.Vob, ["ogv"] = FileFormat.Ogv,
        ["3gp"] = FileFormat.ThreeGP, ["3g2"] = FileFormat.ThreeGP,
        ["mp3"] = FileFormat.Mp3, ["m4a"] = FileFormat.M4a, ["wav"] = FileFormat.Wav, ["wave"] = FileFormat.Wav, ["flac"] = FileFormat.Flac,
        ["ogg"] = FileFormat.Ogg, ["oga"] = FileFormat.Ogg, ["opus"] = FileFormat.Opus, ["aiff"] = FileFormat.Aiff, ["aif"] = FileFormat.Aiff,
        ["aifc"] = FileFormat.Aiff, ["wma"] = FileFormat.Wma, ["aac"] = FileFormat.Aac,
        ["ape"] = FileFormat.Ape, ["wv"] = FileFormat.Wv, ["amr"] = FileFormat.Amr, ["awb"] = FileFormat.Amr,
        ["ac3"] = FileFormat.Ac3, ["dts"] = FileFormat.Dts,
    };

    /// <summary>Camera RAW extensions; Magick.NET (LibRaw) decodes them.</summary>
    public static readonly IReadOnlySet<string> RawExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "dng", "cr2", "cr3", "crw", "nef", "nrw", "arw", "srf", "sr2", "raf", "orf", "rw2", "rwl", "pef", "srw",
        "3fr", "fff", "iiq", "erf", "mos", "mrw", "dcr", "kdc",
    };

    /// <summary>The format of a path, or null when the extension is unknown (folders included).</summary>
    public static FileFormat? Detect(string path)
    {
        var name = Path.GetFileName(path);
        bool Ends(string suffix) => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
        if (Ends(".tar.gz"))
        {
            return FileFormat.Tgz;
        }
        // FictionBook is often passed around zipped.
        if (Ends(".fb2.zip"))
        {
            return FileFormat.Fb2;
        }
        if (Ends(".tar.xz"))
        {
            return FileFormat.Txz;
        }
        if (Ends(".tar.bz2"))
        {
            return FileFormat.Tbz;
        }
        var extension = Path.GetExtension(name);
        if (extension.Length < 2)
        {
            return null;
        }
        var ext = extension[1..];
        if (RawExtensions.Contains(ext))
        {
            return FileFormat.Raw;
        }
        if (ext.Equals("ts", StringComparison.OrdinalIgnoreCase) || ext.Equals("mts", StringComparison.OrdinalIgnoreCase)
            || ext.Equals("m2ts", StringComparison.OrdinalIgnoreCase))
        {
            // .ts is also TypeScript: only a real MPEG transport stream counts as video.
            return IsTransportStream(path) ? ext.Equals("ts", StringComparison.OrdinalIgnoreCase) ? FileFormat.Ts : FileFormat.M2ts : null;
        }
        return ByExtension.TryGetValue(ext, out var format) ? format : null;
    }

    /// <summary>TS packets are 188 bytes starting with 0x47; M2TS (AVCHD) adds a 4-byte time code before each.</summary>
    public static bool IsTransportStream(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var head = new byte[200];
            var read = stream.ReadAtLeast(head, 197, throwOnEndOfStream: false);
            return read >= 197 && (head[0] == 0x47 && head[188] == 0x47 || head[4] == 0x47 && head[196] == 0x47);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static string Extension(this FileFormat format) => format switch
    {
        FileFormat.Tgz => "tar.gz",
        FileFormat.Txz => "tar.xz",
        FileFormat.Tbz => "tar.bz2",
        FileFormat.SevenZip => "7z",
        FileFormat.ThreeGP => "3gp",
        FileFormat.Dicom => "dcm",
        _ => format.ToString().ToLowerInvariant(),
    };

    public static string DisplayName(this FileFormat format) => format switch
    {
        FileFormat.Jpg => "JPG",
        FileFormat.Webp => "WebP",
        FileFormat.Jp2 => "JPEG 2000",
        FileFormat.Jxl => "JPEG XL",
        FileFormat.Dicom => "DICOM",
        FileFormat.Md => "Markdown",
        FileFormat.Tgz => "TAR.GZ",
        FileFormat.Txz => "TAR.XZ",
        FileFormat.Tbz => "TAR.BZ2",
        FileFormat.SevenZip => "7Z",
        FileFormat.Webm => "WebM",
        FileFormat.Opus => "Opus",
        FileFormat.Mpg => "MPEG",
        FileFormat.ThreeGP => "3GP",
        FileFormat.Wv => "WavPack",
        _ => format.ToString().ToUpperInvariant(),
    };

    public static FormatCategory Category(this FileFormat format) => format switch
    {
        FileFormat.Pdf => FormatCategory.Pdf,
        FileFormat.Docx or FileFormat.Doc or FileFormat.Odt or FileFormat.Rtf or FileFormat.Txt
            or FileFormat.Md or FileFormat.Html or FileFormat.Epub or FileFormat.Fb2 => FormatCategory.Document,
        FileFormat.Srt or FileFormat.Vtt or FileFormat.Ass => FormatCategory.Subtitle,
        FileFormat.Csv or FileFormat.Tsv or FileFormat.Xlsx or FileFormat.Json => FormatCategory.Table,
        FileFormat.Zip or FileFormat.Tar or FileFormat.Tgz or FileFormat.SevenZip or FileFormat.Iso or FileFormat.Rar
            or FileFormat.Txz or FileFormat.Tbz or FileFormat.Gz or FileFormat.Cab => FormatCategory.Archive,
        FileFormat.Mp4 or FileFormat.Mov or FileFormat.Mkv or FileFormat.Webm or FileFormat.Avi or FileFormat.Wmv
            or FileFormat.M4v or FileFormat.Flv or FileFormat.Mpg or FileFormat.Ts or FileFormat.M2ts or FileFormat.Vob
            or FileFormat.Ogv or FileFormat.ThreeGP => FormatCategory.Video,
        FileFormat.Mp3 or FileFormat.M4a or FileFormat.Wav or FileFormat.Flac or FileFormat.Ogg or FileFormat.Opus
            or FileFormat.Aiff or FileFormat.Wma or FileFormat.Aac or FileFormat.Ape or FileFormat.Wv or FileFormat.Amr
            or FileFormat.Ac3 or FileFormat.Dts => FormatCategory.Audio,
        FileFormat.Jpg or FileFormat.Png or FileFormat.Webp or FileFormat.Heic or FileFormat.Tiff or FileFormat.Avif
            or FileFormat.Bmp or FileFormat.Gif or FileFormat.Svg or FileFormat.Ico or FileFormat.Psd or FileFormat.Jp2
            or FileFormat.Tga or FileFormat.Exr or FileFormat.Raw or FileFormat.Jxl or FileFormat.Dds
            or FileFormat.Dicom => FormatCategory.Image,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Every format needs a category."),
    };

    public static bool IsMedia(this FormatCategory category) =>
        category is FormatCategory.Video or FormatCategory.Audio;

    /// <summary>Video and audio that FFmpeg reads but Mangosteen never writes.</summary>
    public static bool IsReadOnlyMedia(this FileFormat format) => format is FileFormat.M4v or FileFormat.Flv or FileFormat.Mpg
        or FileFormat.Ts or FileFormat.M2ts or FileFormat.Vob or FileFormat.Ogv or FileFormat.ThreeGP or FileFormat.Aac
        or FileFormat.Ape or FileFormat.Wv or FileFormat.Amr or FileFormat.Ac3 or FileFormat.Dts;
}
