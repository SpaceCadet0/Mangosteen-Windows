using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Mangosteen.Tools;
using Mangosteen.Wheel;

namespace Mangosteen;

/// <summary>Shown on the first launch (and from the tray menu): what the three wheels do.</summary>
internal sealed class WelcomeWindow : ToolWindow
{
    public WelcomeWindow() : base(L("Welcome to Mangosteen"), [], L("Got it"))
    {
        StatusText = "";
        HideCancel();
        var columns = new UniformGrid();
        columns.Columns = 3;
        foreach (var (key, title, text, glyph) in new[]
        {
            ("Shift", L("Convert"), L("Turns files into another format: images, documents, PDF, tables, archives, video and audio."), ""),
            ("Ctrl", L("Tools"), L("Opens an editor with a live preview: crop, compress, annotate, trim a video and more."), ""),
            ("Alt", L("Actions"), L("Float shelf, copy, move, copy the path or open a terminal."), ""),
        })
        {
            var card = new StackPanel { Margin = new Thickness(0, 0, 16, 0), Width = 200 };
            card.Children.Add(new TextBlock { Text = glyph, FontFamily = new FontFamily(WheelGlyphs.FontFamily), FontSize = 28, Foreground = new SolidColorBrush(WheelWindow.Brand) });
            card.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 2) });
            card.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1, 6, 1), HorizontalAlignment = HorizontalAlignment.Left,
                Background = new SolidColorBrush(Color.FromArgb(0x30, 0x80, 0x80, 0x80)), Margin = new Thickness(0, 0, 0, 8),
                Child = new TextBlock { Text = key + " + " + L("right-click"), FontSize = 12 },
            });
            card.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 });
            columns.Children.Add(card);
        }
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = L("Hold a key and right-click a file in Explorer or on the desktop: a wheel opens at the cursor. Results are saved next to the original; nothing is uploaded."),
            TextWrapping = TextWrapping.Wrap, Width = 640, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 20), FontSize = 14,
        });
        stack.Children.Add(columns);
        stack.Children.Add(Note(L("Picked the wrong wheel? While it is open, press Shift, Ctrl or Alt to switch. Mangosteen lives in the notification area by the clock.")));
        Body = stack;
        ActionEnabled = true;
    }

    protected override void Run() => Close();
}
