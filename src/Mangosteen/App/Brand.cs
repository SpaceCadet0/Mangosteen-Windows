using System.Windows.Media;
using Mangosteen.Core;

namespace Mangosteen;

/// <summary>
/// Mangosteen's colours. The accent fills the lit wheel segment, the menu highlight and selections; it is the brand's
/// #671E38 unless another colour is chosen in Settings › Appearance.
/// </summary>
internal static class Brand
{
    /// <summary>#671E38, the colour of the fruit's rind.</summary>
    public static readonly Color Wine = Color.FromRgb(0x67, 0x1E, 0x38);

    public static Color Accent { get; private set; } = Wine;
    public static Brush AccentBrush { get; private set; } = Frozen(Wine);
    /// <summary>Text and glyphs on an accent fill: white or black, whichever stands out more.</summary>
    public static Brush OnAccentBrush { get; private set; } = Brushes.White;
    /// <summary>How opaque the wheel is, 0.4…1.</summary>
    public static double WheelOpacity { get; private set; } = 0.96;
    /// <summary>The wheel blurs what is behind it.</summary>
    public static bool WheelBlur { get; private set; } = true;

    public static void Load(AppSettings settings) =>
        Set(settings.AccentHex, settings.WheelOpacity, settings.WheelBlur);

    public static void Set(string accentHex, double opacity, bool blur)
    {
        var rgb = Core.Accent.Resolve(accentHex);
        Accent = ToColor(rgb);
        AccentBrush = Frozen(Accent);
        OnAccentBrush = Core.Accent.TextIsWhite(rgb) ? Brushes.White : Brushes.Black;
        WheelOpacity = Math.Clamp(opacity, 0.4, 1);
        WheelBlur = blur;
    }

    public static Color ToColor(Rgb rgb) => Color.FromRgb(rgb.R, rgb.G, rgb.B);

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
