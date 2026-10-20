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
    Edit, Annotate, Pages, Background, Collage, Info, CreatePdf, Extract, Merge,
    Float, Copy, Move, Path, Terminal,
}

/// <summary>One segment of a wheel. <see cref="Available"/> is false for features of later phases.</summary>
public sealed record WheelItem(string Id, string Title, WheelIcon Icon, bool Available, FileFormat? Target = null);

public static class WheelCatalog
{
    public static string Title(WheelKind kind) => kind switch
    {
        WheelKind.Convert => "Convert",
        WheelKind.Tools => "Tools",
        _ => "Actions",
    };

    public static string FileCount(int count) => count == 1 ? "1 file" : $"{count} files";

    public static IReadOnlyList<WheelItem> Items(WheelKind kind, IReadOnlyList<string> paths) => kind switch
    {
        WheelKind.Convert => FormatCatalog.Targets(paths)
            .Select(f => new WheelItem($"convert.{f.Extension()}", f.DisplayName(), IconFor(f), true, f))
            .ToList(),
        WheelKind.Tools => ToolItems(paths),
        _ => ActionItems,
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
    private static readonly HashSet<string> AvailableTools = ["tool.extract", "tool.createPDF", "tool.mergePDF"];

    /// <summary>Same rules and order as the macOS Tools wheel.</summary>
    private static List<WheelItem> ToolItems(IReadOnlyList<string> paths)
    {
        var formats = paths.Select(FileFormats.Detect).OfType<FileFormat>().ToList();
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
                items.Add(new WheelItem(id, title, icon, AvailableTools.Contains(id)));
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
        Add(video, "tool.cropVideo", "Crop", WheelIcon.Crop);
        Add(Count(f => f.Category() == FormatCategory.Video) >= 2, "tool.join", "Join", WheelIcon.Join);
        Add(media && !images && !pdf, "tool.compressMedia", "Compress", WheelIcon.Compress);
        Add(video, "tool.frames", "Frames", WheelIcon.Frames);
        Add(images || pdf, "tool.compress", "Compress", WheelIcon.Compress);
        Add(images, "tool.crop", "Crop", WheelIcon.Crop);
        Add(images, "tool.edit", "Editor", WheelIcon.Edit);
        Add(images, "tool.annotate", "Annotate", WheelIcon.Annotate);
        Add(images, "tool.redact", "Hide", WheelIcon.Hide);
        Add(pdf, "tool.organizePDF", "Pages", WheelIcon.Pages);
        Add(images, "tool.frame", "Background", WheelIcon.Background);
        Add(Count(f => f.Category() == FormatCategory.Image) >= 2, "tool.collage", "Collage", WheelIcon.Collage);
        var rasterImages = Any(f => f.Category() == FormatCategory.Image && f != FileFormat.Svg);
        Add(rasterImages, "tool.metadata", "Metadata", WheelIcon.Info);
        Add(rasterImages, "tool.createPDF", "PDF from images", WheelIcon.CreatePdf);
        Add(Any(f => f.Category() == FormatCategory.Archive), "tool.extract", "Extract", WheelIcon.Extract);
        Add(Count(f => f == FileFormat.Pdf) >= 2, "tool.mergePDF", "Merge", WheelIcon.Merge);
        return items;
    }
}
