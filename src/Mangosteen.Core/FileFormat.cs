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

/// <summary>A file format Mangosteen recognises. Detection is by extension only.</summary>
public enum FileFormat
{
    // Images
    Jpg, Png, Webp, Heic, Tiff, Avif, Bmp, Gif,
    // Images (read only)
    Svg, Ico, Psd, Jp2, Tga, Exr,
    Pdf,
    // Documents
    Docx, Doc, Odt, Rtf, Txt, Md, Html,
    // Subtitles
    Srt, Vtt,
    // Tables
    Csv, Tsv, Xlsx,
    // Archives
    Zip, Tar, Tgz,
    // Video (m4v is read only)
    Mp4, Mov, Mkv, Webm, Avi, Wmv, M4v,
    // Audio (aac is read only)
    Mp3, M4a, Wav, Flac, Ogg, Opus, Aiff, Wma, Aac,
}

public static class FileFormats
{
    private static readonly Dictionary<string, FileFormat> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        ["jpg"] = FileFormat.Jpg, ["jpeg"] = FileFormat.Jpg, ["jpe"] = FileFormat.Jpg, ["jfif"] = FileFormat.Jpg,
        ["png"] = FileFormat.Png,
        ["webp"] = FileFormat.Webp,
        ["heic"] = FileFormat.Heic, ["heif"] = FileFormat.Heic,
        ["tif"] = FileFormat.Tiff, ["tiff"] = FileFormat.Tiff,
        ["avif"] = FileFormat.Avif,
        ["bmp"] = FileFormat.Bmp, ["dib"] = FileFormat.Bmp,
        ["gif"] = FileFormat.Gif,
        ["svg"] = FileFormat.Svg,
        ["ico"] = FileFormat.Ico,
        ["psd"] = FileFormat.Psd,
        ["jp2"] = FileFormat.Jp2,
        ["tga"] = FileFormat.Tga,
        ["exr"] = FileFormat.Exr,
        ["pdf"] = FileFormat.Pdf,
        ["docx"] = FileFormat.Docx, ["doc"] = FileFormat.Doc, ["odt"] = FileFormat.Odt, ["rtf"] = FileFormat.Rtf,
        ["txt"] = FileFormat.Txt, ["md"] = FileFormat.Md, ["markdown"] = FileFormat.Md,
        ["html"] = FileFormat.Html, ["htm"] = FileFormat.Html,
        ["srt"] = FileFormat.Srt, ["vtt"] = FileFormat.Vtt,
        ["csv"] = FileFormat.Csv, ["tsv"] = FileFormat.Tsv, ["xlsx"] = FileFormat.Xlsx,
        ["zip"] = FileFormat.Zip, ["tar"] = FileFormat.Tar, ["tgz"] = FileFormat.Tgz,
        ["mp4"] = FileFormat.Mp4, ["mov"] = FileFormat.Mov, ["mkv"] = FileFormat.Mkv, ["webm"] = FileFormat.Webm,
        ["avi"] = FileFormat.Avi, ["wmv"] = FileFormat.Wmv, ["m4v"] = FileFormat.M4v,
        ["mp3"] = FileFormat.Mp3, ["m4a"] = FileFormat.M4a, ["wav"] = FileFormat.Wav, ["flac"] = FileFormat.Flac,
        ["ogg"] = FileFormat.Ogg, ["opus"] = FileFormat.Opus, ["aiff"] = FileFormat.Aiff, ["aif"] = FileFormat.Aiff,
        ["wma"] = FileFormat.Wma, ["aac"] = FileFormat.Aac,
    };

    /// <summary>The format of a path, or null when the extension is unknown (folders included).</summary>
    public static FileFormat? Detect(string path)
    {
        var name = Path.GetFileName(path);
        if (name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
        {
            return FileFormat.Tgz;
        }
        var extension = Path.GetExtension(name);
        if (extension.Length < 2)
        {
            return null;
        }
        return ByExtension.TryGetValue(extension[1..], out var format) ? format : null;
    }

    public static string Extension(this FileFormat format) => format switch
    {
        FileFormat.Tgz => "tar.gz",
        _ => format.ToString().ToLowerInvariant(),
    };

    public static string DisplayName(this FileFormat format) => format switch
    {
        FileFormat.Jpg => "JPG",
        FileFormat.Webp => "WebP",
        FileFormat.Jp2 => "JPEG 2000",
        FileFormat.Md => "Markdown",
        FileFormat.Tgz => "TAR.GZ",
        FileFormat.Webm => "WebM",
        FileFormat.Opus => "Opus",
        _ => format.ToString().ToUpperInvariant(),
    };

    public static FormatCategory Category(this FileFormat format) => format switch
    {
        FileFormat.Pdf => FormatCategory.Pdf,
        FileFormat.Docx or FileFormat.Doc or FileFormat.Odt or FileFormat.Rtf or FileFormat.Txt
            or FileFormat.Md or FileFormat.Html => FormatCategory.Document,
        FileFormat.Srt or FileFormat.Vtt => FormatCategory.Subtitle,
        FileFormat.Csv or FileFormat.Tsv or FileFormat.Xlsx => FormatCategory.Table,
        FileFormat.Zip or FileFormat.Tar or FileFormat.Tgz => FormatCategory.Archive,
        FileFormat.Mp4 or FileFormat.Mov or FileFormat.Mkv or FileFormat.Webm or FileFormat.Avi
            or FileFormat.Wmv or FileFormat.M4v => FormatCategory.Video,
        FileFormat.Mp3 or FileFormat.M4a or FileFormat.Wav or FileFormat.Flac or FileFormat.Ogg
            or FileFormat.Opus or FileFormat.Aiff or FileFormat.Wma or FileFormat.Aac => FormatCategory.Audio,
        _ => FormatCategory.Image,
    };

    public static bool IsMedia(this FormatCategory category) =>
        category is FormatCategory.Video or FormatCategory.Audio;
}
