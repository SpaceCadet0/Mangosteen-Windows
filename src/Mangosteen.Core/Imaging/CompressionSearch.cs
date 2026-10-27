namespace Mangosteen.Core.Imaging;

public enum CompressionMode
{
    Preset, Quality, TargetSize,
}

public enum CompressionPreset
{
    High, Balanced, Small,
}

public sealed record CompressionSettings
{
    public CompressionMode Mode { get; init; } = CompressionMode.Preset;
    public CompressionPreset Preset { get; init; } = CompressionPreset.Balanced;
    /// <summary>0.05…1</summary>
    public double Quality { get; init; } = 0.7;
    /// <summary>Upper bound for each output file in target-size mode.</summary>
    public long TargetBytes { get; init; } = 1_000_000;
    /// <summary>Longer side limit; null keeps the original size.</summary>
    public int? MaxPixelSize { get; init; }
    /// <summary>Null keeps the original format when it is lossy, otherwise picks JPG (WebP when there is transparency).</summary>
    public FileFormat? Format { get; init; }
    public bool PreserveMetadata { get; init; } = true;

    public static readonly IReadOnlyList<int> SizeLimits = [4096, 3840, 2560, 1920, 1600, 1280, 1024, 800];
    public static readonly IReadOnlyList<FileFormat> SelectableFormats = [FileFormat.Jpg, FileFormat.Webp];

    public double PresetQuality => Preset switch
    {
        CompressionPreset.High => 0.85,
        CompressionPreset.Balanced => 0.7,
        _ => 0.45,
    };

    /// <summary>
    /// Resolution and JPEG quality for PDF pages and pictures, as in the macOS version: 200/0.8, 150/0.7, 110/0.5 for
    /// the presets; the chosen quality at 150 dpi in quality mode.
    /// </summary>
    public PdfStep PdfStep => Mode == CompressionMode.Quality
        ? new PdfStep(150, Quality)
        : Preset switch
        {
            CompressionPreset.High => new PdfStep(200, 0.8),
            CompressionPreset.Balanced => new PdfStep(150, 0.7),
            _ => new PdfStep(110, 0.5),
        };

    /// <summary>Target-size mode tries these until the PDF fits.</summary>
    public static readonly IReadOnlyList<PdfStep> PdfTargetSteps =
        [new(200, 0.8), new(150, 0.7), new(120, 0.55), new(100, 0.4), new(72, 0.3)];

    public int? EffectiveMaxPixelSize => Mode == CompressionMode.Preset && Preset == CompressionPreset.Small ? MaxPixelSize ?? 2560 : MaxPixelSize;

    public static string Title(CompressionPreset preset) => preset switch
    {
        CompressionPreset.High => Loc.L("High quality"),
        CompressionPreset.Balanced => Loc.L("Balanced"),
        _ => Loc.L("Smallest size"),
    };

    public static string Detail(CompressionPreset preset) => preset switch
    {
        CompressionPreset.High => Loc.L("Best quality with moderate savings"),
        CompressionPreset.Balanced => Loc.L("Noticeably smaller file, almost no visible loss"),
        _ => Loc.L("The smallest files, quality loss is more noticeable"),
    };

    public static bool IsLossy(FileFormat format) => format is FileFormat.Jpg or FileFormat.Webp or FileFormat.Heic or FileFormat.Avif;
}

/// <summary>How a PDF is compressed: pages and pictures at <see cref="Dpi"/>, JPEG at <see cref="Quality"/> (0…1).</summary>
public readonly record struct PdfStep(double Dpi, double Quality);

/// <summary>Finds the highest quality whose encoded size fits a limit.</summary>
public static class CompressionSearch
{
    /// <summary>Binary search over quality 0.05…0.95; null when even the lowest quality is too big.</summary>
    public static byte[]? BestFit(Func<double, byte[]> encode, long limit, int steps = 7)
    {
        double low = 0.05, high = 0.95;
        byte[]? best = null;
        for (var i = 0; i < steps; i++)
        {
            var quality = (low + high) / 2;
            var data = encode(quality);
            if (data.Length <= limit)
            {
                best = data;
                low = quality;
            }
            else
            {
                high = quality;
            }
        }
        if (best is null)
        {
            var smallest = encode(0.05);
            if (smallest.Length <= limit)
            {
                best = smallest;
            }
        }
        return best;
    }

    /// <summary>The size to try next when even the lowest quality is too big: shrink by the square root of the ratio.</summary>
    public static (int Width, int Height) ShrinkToFit(int width, int height, long smallestBytes, long limit)
    {
        var ratio = Math.Min(Math.Sqrt((double)limit / Math.Max(smallestBytes, 1)) * 0.9, 0.9);
        return (Math.Max(1, (int)(width * ratio)), Math.Max(1, (int)(height * ratio)));
    }

    public static (int Width, int Height) FitInside(int width, int height, int? maxSide)
    {
        if (maxSide is not { } limit || Math.Max(width, height) <= limit)
        {
            return (width, height);
        }
        var scale = (double)limit / Math.Max(width, height);
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }
}
