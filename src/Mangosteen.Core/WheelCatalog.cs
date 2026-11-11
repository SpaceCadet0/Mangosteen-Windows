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
    Book, RemoveBars, Hdr, Vector, PagesPerSheet, LivePhoto, Tags,
}

/// <summary>One segment of a wheel. <see cref="Available"/> is false for features this build lacks.</summary>
public sealed record WheelItem(string Id, string Title, WheelIcon Icon, bool Available, FileFormat? Target = null);

/// <summary>The two sides of a wheel for a selection.</summary>
public sealed record WheelSides(IReadOnlyList<WheelItem> Front, IReadOnlyList<WheelItem> Back);

public static class WheelCatalog
{
    public static string Title(WheelKind kind) => kind switch
    {
        WheelKind.Convert => Loc.L("Convert"),
        WheelKind.Tools => Loc.L("Tools"),
        _ => Loc.L("Actions"),
    };

    public static string FileCount(int count) => Loc.Count(count, "file");

    /// <summary>Every segment for the selection, in wheel order, before it is split into sides.</summary>
    public static IReadOnlyList<WheelItem> Items(WheelKind kind, IReadOnlyList<string> paths) => kind switch
    {
        WheelKind.Convert => FormatCatalog.Targets(paths).Select(ConvertItem).ToList(),
        WheelKind.Tools => ToolItems(paths),
        _ => ActionItems.Select(i => i with { Title = Loc.L(i.Title) }).ToList(),
    };

    /// <summary>The selection's segments on the face and on the back of the wheel.</summary>
    public static WheelSides Sides(WheelKind kind, IReadOnlyList<string> paths, IReadOnlyDictionary<string, SlicePlacement>? choices)
    {
        var (front, back) = WheelLayout.Split(Items(kind, paths), i => i.Id, choices);
        return new WheelSides(front, back);
    }

    private static WheelItem ConvertItem(FileFormat f) => new($"convert.{f.Extension()}", f.DisplayName(), IconFor(f), true, f);

    public static WheelIcon IconFor(FileFormat format) => format.Category() switch
    {
        FormatCategory.Image => WheelIcon.Image,
        FormatCategory.Video => WheelIcon.Film,
        FormatCategory.Audio => WheelIcon.Music,
        FormatCategory.Archive => WheelIcon.Archive,
        FormatCategory.Table => WheelIcon.Table,
        FormatCategory.Subtitle => WheelIcon.Subtitle,
        _ when format is FileFormat.Epub or FileFormat.Fb2 => WheelIcon.Book,
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

    /// <summary>Every tool segment there is, for the settings that put each one on a side. Titles in English.</summary>
    public static readonly IReadOnlyList<(string Id, string Title, WheelIcon Icon)> ToolDefinitions =
    [
        ("tool.trimMedia", "Trim", WheelIcon.Trim),
        ("tool.splitMedia", "Split", WheelIcon.Split),
        ("tool.speed", "Speed", WheelIcon.Speed),
        ("tool.normalize", "Volume", WheelIcon.Volume),
        ("tool.bleep", "Bleep", WheelIcon.Bleep),
        ("tool.channels", "Channels", WheelIcon.Channels),
        ("tool.visualizer", "Visualizer", WheelIcon.Visualizer),
        ("tool.redactVideo", "Hide", WheelIcon.Hide),
        ("tool.cropVideo", "Reframe", WheelIcon.Crop),
        ("tool.join", "Join", WheelIcon.Join),
        ("tool.compressMedia", "Compress", WheelIcon.Compress),
        ("tool.frames", "Frames", WheelIcon.Frames),
        ("tool.extractSubtitles", "Subtitles", WheelIcon.Subtitle),
        ("tool.removeBars", "Remove bars", WheelIcon.RemoveBars),
        ("tool.hdrToSDR", "HDR → SDR", WheelIcon.Hdr),
        ("tool.compress", "Compress", WheelIcon.Compress),
        ("tool.crop", "Crop", WheelIcon.Crop),
        ("tool.edit", "Editor", WheelIcon.Edit),
        ("tool.trace", "To vector", WheelIcon.Vector),
        ("tool.annotate", "Annotate", WheelIcon.Annotate),
        ("tool.redact", "Hide", WheelIcon.Hide),
        ("tool.splitPDF", "Split", WheelIcon.Split),
        ("tool.organizePDF", "Page order", WheelIcon.Pages),
        ("tool.pagesPerSheet", "Per sheet", WheelIcon.PagesPerSheet),
        ("tool.recognizeText", "Text", WheelIcon.Text),
        ("tool.frame", "Background", WheelIcon.Background),
        ("tool.removeBackground", "Remove bg", WheelIcon.RemoveBackground),
        ("tool.livePhoto", "Live → video", WheelIcon.LivePhoto),
        ("tool.collage", "Collage", WheelIcon.Collage),
        ("tool.metadata", "Metadata", WheelIcon.Info),
        ("tool.tags", "Metadata", WheelIcon.Tags),
        ("tool.extract", "Extract", WheelIcon.Extract),
        ("tool.mergePDF", "Merge", WheelIcon.Merge),
        ("tool.compressAll", "Compress", WheelIcon.Compress),
        ("tool.archive", "Archive", WheelIcon.Archive),
    ];

    /// <summary>Every segment a wheel can show, for the settings that put each one on a side.</summary>
    public static IReadOnlyList<WheelItem> AllItems(WheelKind kind) => kind switch
    {
        WheelKind.Convert => Enum.GetValues<FileFormat>()
            .Where(f => FormatCatalog.Outputs(f.Category()).Contains(f) || FormatCatalog.VideoOutputs.Contains(f))
            .Select(ConvertItem).DistinctBy(i => i.Id).ToList(),
        WheelKind.Tools => ToolDefinitions.Select(d => new WheelItem(d.Id, Loc.L(d.Title), d.Icon, IsAvailable(d.Id))).ToList(),
        _ => Items(kind, []),
    };

    /// <summary>Tools that work without extra parts; the others need FFmpeg or the background model.</summary>
    private static readonly HashSet<string> AvailableTools =
    [
        "tool.extract", "tool.mergePDF", "tool.crop", "tool.compress", "tool.metadata", "tool.frame", "tool.collage",
        "tool.organizePDF", "tool.splitPDF", "tool.recognizeText", "tool.compressAll", "tool.archive", "tool.edit",
        "tool.redact", "tool.annotate", "tool.trace", "tool.pagesPerSheet", "tool.tags",
    ];

    /// <summary>Video and audio tools; they need the bundled FFmpeg.</summary>
    private static readonly HashSet<string> MediaTools =
    [
        "tool.trimMedia", "tool.splitMedia", "tool.speed", "tool.normalize", "tool.bleep", "tool.channels", "tool.visualizer",
        "tool.redactVideo", "tool.cropVideo", "tool.join", "tool.compressMedia", "tool.frames", "tool.extractSubtitles",
        "tool.removeBars", "tool.hdrToSDR", "tool.livePhoto",
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

    /// <summary>Raster pictures this build decodes (SVG is not one).</summary>
    private static bool Raster(FileFormat f) => f.Category() == FormatCategory.Image && FormatCatalog.ImageInputs.Contains(f);

    /// <summary>Pictures whose metadata Mangosteen rewrites in their own format (not RAW, JPEG XL or the Magick formats).</summary>
    private static bool MetadataImage(FileFormat f) => FormatCatalog.WicImageInputs.Contains(f);

    /// <summary>Video, audio and PDF carry their own tags (title, artist…), edited by "Metadata" for them.</summary>
    public static bool HasTags(FileFormat f) =>
        f == FileFormat.Pdf || f.Category().IsMedia() && !f.IsReadOnlyMedia() && FormatCatalog.MediaAvailable;

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
        Add(formats.Any(f => CanCompress(f)), "tool.compressAll", "Compress", WheelIcon.Compress);
        Add(formats.Any(MetadataImage), "tool.metadata", "Metadata", WheelIcon.Info);
        Add(formats.Count == paths.Count && formats.All(f => Raster(f) || f == FileFormat.Pdf), "tool.recognizeText", "Text", WheelIcon.Text);
        Add(true, "tool.archive", "Archive", WheelIcon.Archive);
        return items;
    }

    /// <summary>Formats "Compress" handles without a window: photos and PDF, and video and audio when FFmpeg is there.</summary>
    public static bool CanCompress(FileFormat format) =>
        Raster(format) || format == FileFormat.Pdf
        || format.Category().IsMedia() && FormatCatalog.MediaAvailable;

    /// <summary>The video next to a Live Photo: a .mov (or .mp4) with the picture's name, or null.</summary>
    public static string? LivePhotoMovie(string picture)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(picture)) ?? "";
        var name = Path.GetFileNameWithoutExtension(picture);
        foreach (var extension in new[] { ".mov", ".MOV", ".mp4", ".MP4" })
        {
            var candidate = Path.Combine(directory, name + extension);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

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
        var images = Any(Raster);
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
        Add(video, "tool.extractSubtitles", "Subtitles", WheelIcon.Subtitle);
        Add(video, "tool.removeBars", "Remove bars", WheelIcon.RemoveBars);
        Add(video, "tool.hdrToSDR", "HDR → SDR", WheelIcon.Hdr);
        Add(images || pdf, "tool.compress", "Compress", WheelIcon.Compress);
        Add(images, "tool.crop", "Crop", WheelIcon.Crop);
        Add(images, "tool.edit", "Editor", WheelIcon.Edit);
        Add(images, "tool.trace", "To vector", WheelIcon.Vector);
        Add(images, "tool.annotate", "Annotate", WheelIcon.Annotate);
        Add(images, "tool.redact", "Hide", WheelIcon.Hide);
        // With media in the selection there is a "Split" for video too; say which one this is.
        Add(pdf, "tool.splitPDF", media ? "Split PDF" : "Split", WheelIcon.Split);
        Add(pdf, "tool.organizePDF", "Page order", WheelIcon.Pages);
        Add(pdf, "tool.pagesPerSheet", "Per sheet", WheelIcon.PagesPerSheet);
        Add(images || pdf, "tool.recognizeText", "Text", WheelIcon.Text);
        Add(images, "tool.frame", "Background", WheelIcon.Background);
        Add(images, "tool.removeBackground", "Remove bg", WheelIcon.RemoveBackground);
        Add(paths.Any(p => FileFormats.Detect(p) is { } f && Raster(f) && LivePhotoMovie(p) is not null), "tool.livePhoto", "Live → video", WheelIcon.LivePhoto);
        Add(Count(Raster) >= 2, "tool.collage", "Collage", WheelIcon.Collage);
        Add(Any(MetadataImage), "tool.metadata", "Metadata", WheelIcon.Info);
        Add(!Any(MetadataImage) && Any(HasTags), "tool.tags", "Metadata", WheelIcon.Tags);
        Add(Any(f => f.Category() == FormatCategory.Archive), "tool.extract", "Extract", WheelIcon.Extract);
        Add(Count(f => f == FileFormat.Pdf) >= 2, "tool.mergePDF", "Merge", WheelIcon.Merge);
        return items;
    }
}
