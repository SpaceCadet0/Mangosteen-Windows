using Mangosteen.Core;

namespace Mangosteen.Tools;

/// <summary>Writes tool results next to the original as "name (suffix).ext", never overwriting anything.</summary>
internal static class ToolOutput
{
    public static string Write(string input, string suffix, FileFormat format, byte[] data)
    {
        var full = Path.GetFullPath(input);
        return OutputNaming.WriteAtomically(Path.GetDirectoryName(full)!, $"{OutputNaming.BaseName(full)} ({suffix})", format.Extension(),
            temp => File.WriteAllBytes(temp, data));
    }

    /// <summary>The format results keep: the original's when Mangosteen can write it, otherwise PNG.</summary>
    public static FileFormat KeepFormat(string input) =>
        FileFormats.Detect(input) is { } format && format != FileFormat.Pdf && FormatCatalog.WicImageOutputs.Contains(format) ? format : FileFormat.Png;

    public static List<string> Images(IEnumerable<string> paths) =>
        paths.Where(p => FileFormats.Detect(p) is { } f && FormatCatalog.ImageInputs.Contains(f)).ToList();
}
