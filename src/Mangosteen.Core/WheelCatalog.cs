namespace Mangosteen.Core;

/// <summary>The three wheels. Shift opens Convert, Ctrl opens Tools, Alt opens Actions.</summary>
public enum WheelKind
{
    Convert,
    Tools,
    Actions,
}

/// <summary>An icon name the app maps to a glyph; keeps the catalog free of UI types.</summary>
public enum WheelIcon
{
    Image, Document, Film, Music, Archive, Table, Subtitle,
    Trim, Split, Speed, Volume, Bleep, Channels, Visualizer, Hide, Crop, Join, Compress, Frames,
    Edit, Annotate, Pages, Background, Collage, Info, CreatePdf, Extract, Merge, Text, RemoveBackground,
    Float, Copy, Move, Path, Terminal,
}

/// <summary>One segment of a wheel. <see cref="Available"/> is false for features of later phases.</summary>
public sealed record WheelItem(string Id, string Title, WheelIcon Icon, bool Available, FileFormat? Target = null);

public static class WheelCatalog
{
    public static string Title(WheelKind kind) => kind switch
    {
        WheelKind.Convert => Loc.L("Convert"),
        WheelKind.Tools => Loc.L("Tools"),
        _ => Loc.L("Actions"),
    };

    public static string FileCount(int count) => Loc.Count(count, "file");

    public static IReadOnlyList<WheelItem> Items(WheelKind kind, IReadOnlyList<string> paths) => kind switch
    {
        WheelKind.Convert => FormatCatalog.Targets(paths)
            .Select(f => new WheelItem($"convert.{f.Extension()}", f.DisplayName(), IconFor(f), true, f))
            .ToList(),
        WheelKind.Tools => ToolItems(paths),
        _ => ActionItems.Select(i => i with { Title = Loc.L(i.Title) }).ToList(),
    };

    public static WheelIcon IconFor(FileFormat format) => format.Category() switch
    {
        FormatCategory.Image => WheelIcon.Image,
        FormatCategory.Video => WheelIcon.Film,
        FormatCategory.Audio => WheelIcon.Music,
        FormatCategory.Archive => WheelIcon.Archive,
        FormatCategory.Table => WheelIcon.Table,
        FormatCategory.Subtitle => WheelIcon.Subtitle,
        _ => WheelIcon.Document,
    };

    private static readonly IReadOnlyList<WheelItem> ActionItems =
    [
        new("action.float", "Float", WheelIcon.Float, true),
        new("action.copy", "Copy", WheelIcon.Copy, true),
        new("action.move", "Move", WheelIcon.Move, true),
        new("action.copyPath", "Path", WheelIcon.Path, true),
        new("action.terminal", "Terminal", WheelIcon.Terminal, true),
    ];

    /// <summary>Tools that already work on Windows; the others are shown greyed out until their phase.</summary>
    private static readonly HashSet<string> AvailableTools = ["tool.extract", "tool.createPDF", "tool.mergePDF", "tool.crop", "tool.compress", "tool.metadata", "tool.frame", "tool.collage", "tool.organizePDF", "tool.splitPDF", "tool.recognizeText", "tool.compressAll", "tool.archive", "tool.edit", "tool.redact", "tool.annotate"];

    /// <summary>Video and audio tools; they need the bundled FFmpeg.</summary>
    private static readonly HashSet<string> MediaTools =
    [
        "tool.trimMedia", "tool.splitMedia", "tool.speed", "tool.normalize", "tool.bleep", "tool.channels", "tool.visualizer",
        "tool.redactVideo", "tool.cropVideo", "tool.join", "tool.compressMedia", "tool.frames",
    ];

    /// <summary>True when the app carries the background-removal model.</summary>
    public static bool BackgroundRemovalAvailable { get; set; }

    private static bool IsAvailable(string id) => AvailableTools.Contains(id) || FormatCatalog.MediaAvailable && MediaTools.Contains(id)
        || id == "tool.removeBackground" && BackgroundRemovalAvailable;

    /// <summary>
    /// The kinds of items in a selection: format categories, plus folders. More than one kind makes a mixed selection.
    /// </summary>
    private static HashSet<string> Kinds(IReadOnlyList<string> paths) => paths
        .Select(p => FileFormats.Detect(p) is { } format ? format.Category().ToString() : Directory.Exists(p) ? "folder" : null)
        .OfType<string>().ToHashSet();

    /// <summary>
    /// A mixed selection (say photos and a video) gets only what applies to all of it, instead of every tool for
    /// every kind: compress each file its own way, metadata of the photos, text from photos and PDFs, one archive.
    /// </summary>
    private static List<WheelItem> MixedItems(IReadOnlyList<string> paths, List<FileFormat> formats)
    {
        var items = new List<WheelItem>();
        void Add(bool condition, string id, string title, WheelIcon icon)
        {
            if (condition)
            {
                items.Add(new WheelItem(id, Loc.L(title), icon, IsAvailable(id)));
            }
        }
        bool Raster(FileFormat f) => f.Category() == FormatCategory.Image && f != FileFormat.Svg;
        Add(formats.Any(f => CanCompress(f)), "tool.compressAll", "Compress", WheelIcon.Compress);
        Add(formats.Any(Raster), "tool.metadata", "Metadata", WheelIcon.Info);
        Add(formats.Count == paths.Count && formats.All(f => Raster(f) || f == FileFormat.Pdf), "tool.recognizeText", "Text", WheelIcon.Text);
        Add(true, "tool.archive", "Archive", WheelIcon.Archive);
        return items;
    }

    /// <summary>Formats "Compress" handles without a window: photos and PDF, and video and audio when FFmpeg is there.</summary>
    public static bool CanCompress(FileFormat format) =>
        format.Category() == FormatCategory.Image && format != FileFormat.Svg || format == FileFormat.Pdf
        || format.Category().IsMedia() && FormatCatalog.MediaAvailable;

    /// <summary>Same rules and order as the macOS Tools wheel.</summary>
    private static List<WheelItem> ToolItems(IReadOnlyList<string> paths)
    {
        var formats = paths.Select(FileFormats.Detect).OfType<FileFormat>().ToList();
        if (Kinds(paths).Count > 1)
        {
            return MixedItems(paths, formats);
        }
        bool Any(Func<FileFormat, bool> predicate) => formats.Any(predicate);
        int Count(Func<FileFormat, bool> predicate) => formats.Count(predicate);
        var media = Any(f => f.Category().IsMedia());
        var audio = Any(f => f.Category() == FormatCategory.Audio);
        var video = Any(f => f.Category() == FormatCategory.Video);
        var images = Any(f => f.Category() == FormatCategory.Image);
        var pdf = Any(f => f == FileFormat.Pdf);

        var items = new List<WheelItem>();
        void Add(bool condition, string id, string title, WheelIcon icon)
        {
            if (condition)
            {
                items.Add(new WheelItem(id, Loc.L(title), icon, IsAvailable(id)));
            }
        }
        Add(media, "tool.trimMedia", "Trim", WheelIcon.Trim);
        Add(media, "tool.splitMedia", "Split", WheelIcon.Split);
        Add(media, "tool.speed", "Speed", WheelIcon.Speed);
        Add(media, "tool.normalize", "Volume", WheelIcon.Volume);
        Add(media, "tool.bleep", "Bleep", WheelIcon.Bleep);
        Add(audio, "tool.channels", "Channels", WheelIcon.Channels);
        Add(audio, "tool.visualizer", "Visualizer", WheelIcon.Visualizer);
        Add(video, "tool.redactVideo", "Hide", WheelIcon.Hide);
        Add(video, "tool.cropVideo", "Reframe", WheelIcon.Crop);
        Add(Count(f => f.Category() == FormatCategory.Video) >= 2, "tool.join", "Join", WheelIcon.Join);
        Add(media && !images && !pdf, "tool.compressMedia", "Compress", WheelIcon.Compress);
        Add(video, "tool.frames", "Frames", WheelIcon.Frames);
        Add(images || pdf, "tool.compress", "Compress", WheelIcon.Compress);
        Add(images, "tool.crop", "Crop", WheelIcon.Crop);
        Add(images, "tool.edit", "Editor", WheelIcon.Edit);
        Add(images, "tool.annotate", "Annotate", WheelIcon.Annotate);
        Add(images, "tool.redact", "Hide", WheelIcon.Hide);
        // With media in the selection there is a "Split" for video too; say which one this is.
        Add(pdf, "tool.splitPDF", media ? "Split PDF" : "Split", WheelIcon.Split);
        Add(pdf, "tool.organizePDF", "Page order", WheelIcon.Pages);
        var rasterImages = Any(f => f.Category() == FormatCategory.Image && f != FileFormat.Svg);
        Add(rasterImages || pdf, "tool.recognizeText", "Text", WheelIcon.Text);
        Add(images, "tool.frame", "Background", WheelIcon.Background);
        Add(rasterImages, "tool.removeBackground", "Remove bg", WheelIcon.RemoveBackground);
        Add(Count(f => f.Category() == FormatCategory.Image) >= 2, "tool.collage", "Collage", WheelIcon.Collage);
        Add(rasterImages, "tool.metadata", "Metadata", WheelIcon.Info);
        Add(rasterImages, "tool.createPDF", "PDF from images", WheelIcon.CreatePdf);
        Add(Any(f => f.Category() == FormatCategory.Archive), "tool.extract", "Extract", WheelIcon.Extract);
        Add(Count(f => f == FileFormat.Pdf) >= 2, "tool.mergePDF", "Merge", WheelIcon.Merge);
        return items;
    }
}
