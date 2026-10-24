namespace Mangosteen.Core.Imaging;

public enum ResizeKind
{
    Percent, LongSide, Width, Height, Exact,
}

/// <summary>
/// One rule for every file of a resize: a share of the size, a long side (smaller images are not enlarged), a width or
/// a height with the other side to scale, or an exact size. Each side ends up between 1 and <see cref="MaxSide"/>.
/// </summary>
public readonly record struct ResizeRule(ResizeKind Kind, int Value, int Height = 0)
{
    public const int MaxSide = 20000;

    public static ResizeRule Percent(int percent) => new(ResizeKind.Percent, percent);
    public static ResizeRule LongSide(int pixels) => new(ResizeKind.LongSide, pixels);
    public static ResizeRule Width(int pixels) => new(ResizeKind.Width, pixels);
    public static ResizeRule HeightOnly(int pixels) => new(ResizeKind.Height, pixels);
    public static ResizeRule Exact(int width, int height) => new(ResizeKind.Exact, width, height);

    public static readonly IReadOnlyList<int> Percents = [25, 50, 75];
    public static readonly IReadOnlyList<int> LongSides = [3840, 2560, 1920, 1280, 1080, 800];

    /// <summary>The new size of an image of <paramref name="width"/> × <paramref name="height"/> pixels.</summary>
    public (int Width, int Height) Apply(int width, int height)
    {
        double w = width, h = height;
        switch (Kind)
        {
            case ResizeKind.Percent:
                (w, h) = (width * Value / 100.0, height * Value / 100.0);
                break;
            case ResizeKind.LongSide:
                var scale = Math.Min(1, (double)Value / Math.Max(width, height));
                (w, h) = (width * scale, height * scale);
                break;
            case ResizeKind.Width:
                (w, h) = (Value, (double)height * Value / width);
                break;
            case ResizeKind.Height:
                (w, h) = ((double)width * Value / height, Value);
                break;
            case ResizeKind.Exact:
                (w, h) = (Value, Height);
                break;
        }
        // Beyond the limit both sides shrink together, so the proportions hold (an exact size is taken as it is).
        if (Kind != ResizeKind.Exact && Math.Max(w, h) > MaxSide)
        {
            var fit = MaxSide / Math.Max(w, h);
            (w, h) = (w * fit, h * fit);
        }
        static int Side(double value) => Math.Clamp((int)Math.Round(value, MidpointRounding.AwayFromZero), 1, MaxSide);
        return (Side(w), Side(h));
    }

    /// <summary>"photo (1920×1440)": the name of a resized result, next to the original.</summary>
    public static string ResultName(string baseName, int width, int height) => $"{baseName} ({width}×{height})";
}
