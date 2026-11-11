using System.Globalization;

namespace Mangosteen.Core;

/// <summary>An sRGB colour, 0…255 per channel.</summary>
public readonly record struct Rgb(byte R, byte G, byte B);

/// <summary>
/// The accent colour: the brand's #671E38 unless another one is chosen in Settings › Appearance. Text on the accent is
/// white or black, whichever stands out more.
/// </summary>
public static class Accent
{
    public static readonly Rgb Brand = new(0x67, 0x1E, 0x38);

    /// <summary>Ready-made accents (hex, English name); all dark enough for white text. Empty hex is the brand colour.</summary>
    public static readonly IReadOnlyList<(string Hex, string Name)> Presets =
    [
        ("", "Mangosteen"),
        ("#1F5FBF", "Blue"),
        ("#0E7C7B", "Teal"),
        ("#2E7D32", "Green"),
        ("#C2410C", "Orange"),
        ("#5B3FA8", "Purple"),
        ("#B0306A", "Pink"),
        ("#4A4A4F", "Graphite"),
    ];

    /// <summary>"#RRGGBB" → colour; null for an empty or malformed string.</summary>
    public static Rgb? Parse(string? hex)
    {
        var digits = (hex ?? "").Trim().TrimStart('#');
        return digits.Length == 6 && uint.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)
            ? new Rgb((byte)(value >> 16), (byte)(value >> 8), (byte)value) : null;
    }

    public static Rgb Resolve(string? hex) => Parse(hex) ?? Brand;

    public static string Hex(Rgb color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>Relative luminance (WCAG).</summary>
    public static double Luminance(Rgb color)
    {
        static double Linear(byte channel)
        {
            var c = channel / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
    }

    public static double Contrast(Rgb a, Rgb b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static readonly Rgb White = new(255, 255, 255);
    private static readonly Rgb Black = new(0, 0, 0);

    /// <summary>True when white text stands out on the accent at least as well as black.</summary>
    public static bool TextIsWhite(Rgb accent) => Contrast(White, accent) >= Contrast(Black, accent);

    public static Rgb Mix(Rgb color, Rgb with, double fraction) => new(
        (byte)Math.Round(color.R + (with.R - color.R) * fraction),
        (byte)Math.Round(color.G + (with.G - color.G) * fraction),
        (byte)Math.Round(color.B + (with.B - color.B) * fraction));

    /// <summary><paramref name="color"/> lightened towards white just enough to reach 4.5:1 against <paramref name="background"/>.</summary>
    public static Rgb Readable(Rgb color, Rgb background)
    {
        var result = color;
        for (var fraction = 0.05; Contrast(result, background) < 4.5 && fraction <= 1; fraction += 0.05)
        {
            result = Mix(color, White, fraction);
        }
        return result;
    }
}
