namespace Mangosteen.Core;

/// <summary>Which output formats are offered for which inputs on Windows.</summary>
/// <remarks>Only formats that Mangosteen for Windows can already write are listed; the catalog grows phase by phase.</remarks>
public static class FormatCatalog
{
    /// <summary>Written through WIC (System.Windows.Media.Imaging).</summary>
    public static readonly IReadOnlyList<FileFormat> ImageOutputs =
        [FileFormat.Png, FileFormat.Jpg, FileFormat.Tiff, FileFormat.Bmp, FileFormat.Gif];

    /// <summary>Image inputs WIC can decode. WebP, HEIC and AVIF need the codecs from the Microsoft Store.</summary>
    public static readonly IReadOnlySet<FileFormat> ImageInputs = new HashSet<FileFormat>
    {
        FileFormat.Png, FileFormat.Jpg, FileFormat.Tiff, FileFormat.Bmp, FileFormat.Gif,
        FileFormat.Ico, FileFormat.Webp, FileFormat.Heic, FileFormat.Avif,
    };

    public static IReadOnlyList<FileFormat> Targets(FileFormat source) =>
        ImageInputs.Contains(source) ? ImageOutputs.Where(f => f != source).ToList() : [];

    /// <summary>
    /// Formats offered for a selection. A format is offered when every convertible item can either be
    /// converted to it or already is it (those items are skipped), and at least one item needs converting.
    /// Items that cannot be converted at all are ignored.
    /// </summary>
    public static IReadOnlyList<FileFormat> Targets(IEnumerable<string> paths)
    {
        var items = paths
            .Select(FileFormats.Detect)
            .Where(f => f is not null)
            .Select(f => (Source: f!.Value, Targets: Targets(f.Value)))
            .Where(i => i.Targets.Count > 0)
            .ToList();
        if (items.Count == 0)
        {
            return [];
        }
        return ImageOutputs
            .Where(target => items.All(i => i.Source == target || i.Targets.Contains(target))
                && items.Any(i => i.Source != target))
            .ToList();
    }
}
