using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Mangosteen.Tools;

namespace Mangosteen;

/// <summary>
/// What the current version brought, shown once after an update (a new installation gets the welcome window instead)
/// and from Settings. A release that brings something new replaces the list.
/// </summary>
internal sealed class WhatsNewWindow : ToolWindow
{
    private static readonly (string Glyph, string Title, string Text)[] Items =
    [
        ("", "Two sides to every wheel", "Up to ten segments on the face, the rest on the back. Turn the wheel over with the mouse wheel or Tab; choose the side of each segment in Settings."),
        ("", "Your colours", "An accent colour of your own, the wheel's opacity and a blur of what is behind it."),
        ("", "Where results go", "Next to the original, in one folder or a folder you pick each time; a name template with the date, time and format; presets for quality and size."),
        ("", "E-books", "EPUB and FB2 (zipped and Windows-1251 too) into each other, PDF, Word, text, Markdown and HTML; documents into books."),
        ("", "To vector", "Pictures traced to SVG, black-and-white or in colour, with a preview you can zoom and move."),
        ("", "Video", "H.264, HEVC or AV1, on the graphics card if you like; remove black bars, HDR to SDR, subtitles out of a video, Live Photos to video."),
        ("", "PDF", "Several pages on one sheet, and splitting by ranges such as 1-3, 8, 12-."),
        ("", "Metadata", "Title, artist and more for video, audio and PDF."),
        ("", "More formats", "7Z, RAR, ISO, CAB, TAR.XZ; ICO; PSD, TGA, OpenEXR, camera RAW, JPEG XL, DDS; ASS subtitles; JSON tables."),
        ("", "Recent Results", "Thumbnails, and Open with any app."),
        ("", "Languages", "German, French, Portuguese (Brazil), Chinese (Simplified) and Japanese."),
    ];

    public WhatsNewWindow() : base(string.Format(L("What's New in Mangosteen {0}"), Program.Version), [], L("Continue"))
    {
        HideCancel();
        StatusText = "";
        var stack = new StackPanel { Width = 520 };
        stack.Children.Add(new TextBlock
        {
            Text = string.Format(L("What's New in Mangosteen {0}"), Program.Version), FontSize = 20, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 14),
        });
        foreach (var (glyph, title, text) in Items)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
            var icon = new TextBlock
            {
                Text = glyph, FontFamily = new FontFamily(Wheel.WheelGlyphs.FontFamily), FontSize = 20, Width = 36,
                Foreground = Brand.AccentBrush, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0),
            };
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);
            var words = new StackPanel();
            words.Children.Add(new TextBlock { Text = L(title), FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            words.Children.Add(new TextBlock { Text = L(text), TextWrapping = TextWrapping.Wrap, Opacity = 0.8, Margin = new Thickness(0, 2, 0, 0) });
            row.Children.Add(words);
            stack.Children.Add(row);
        }
        Body = stack;
        ActionEnabled = true;
    }

    protected override void Run() => Close();
}
