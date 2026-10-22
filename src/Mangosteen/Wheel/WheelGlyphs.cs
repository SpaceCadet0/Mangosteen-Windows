using Mangosteen.Core;

namespace Mangosteen.Wheel;

/// <summary>Segoe Fluent Icons (Windows 11) / Segoe MDL2 Assets (Windows 10) glyphs for the wheel.</summary>
internal static class WheelGlyphs
{
    public const string FontFamily = "Segoe Fluent Icons, Segoe MDL2 Assets";

    public static string For(WheelIcon icon) => icon switch
    {
        WheelIcon.Image => "",
        WheelIcon.Document => "",
        WheelIcon.Film => "",
        WheelIcon.Music => "",
        WheelIcon.Archive => "",
        WheelIcon.Table => "",
        WheelIcon.Subtitle => "",
        WheelIcon.Trim => "",
        WheelIcon.Split => "",
        WheelIcon.Speed => "",
        WheelIcon.Volume => "",
        WheelIcon.Bleep => "",
        WheelIcon.Channels => "",
        WheelIcon.Visualizer => "",
        WheelIcon.Hide => "",
        WheelIcon.Crop => "",
        WheelIcon.Join => "",
        WheelIcon.Compress => "",
        WheelIcon.Frames => "",
        WheelIcon.Edit => "",
        WheelIcon.Annotate => "",
        WheelIcon.Pages => "",
        WheelIcon.Background => "",
        WheelIcon.Collage => "",
        WheelIcon.Info => "",
        WheelIcon.CreatePdf => "",
        WheelIcon.Extract => "",
        WheelIcon.Merge => "",
        WheelIcon.Text => "\uE8D2",
        WheelIcon.RemoveBackground => "\uE771",
        WheelIcon.Float => "",
        WheelIcon.Copy => "",
        WheelIcon.Move => "",
        WheelIcon.Path => "",
        WheelIcon.Terminal => "",
        _ => "",
    };
}
