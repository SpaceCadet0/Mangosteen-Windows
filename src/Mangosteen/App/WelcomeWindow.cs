using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mangosteen.Tools;
using Mangosteen.Wheel;

namespace Mangosteen;

/// <summary>
/// Shown on the first launch (and from the tray menu): how to open a wheel, what the three wheels do, and whether
/// Mangosteen starts with Windows. The layout follows the macOS welcome window: the header and the steps on the left,
/// a looping demo video on the right, the three wheels below.
/// </summary>
internal sealed class WelcomeWindow : ToolWindow
{
    private const double ContentWidth = 780;
    private const double VideoWidth = 300, VideoHeight = 300;
    private static readonly Brush StepBrush = new SolidColorBrush(Color.FromRgb(0xC0, 0x5A, 0x80));
    private static readonly Brush CardBrush = new SolidColorBrush(Color.FromArgb(0x1C, 0x80, 0x80, 0x80));
    private static readonly Brush TileBrush = new SolidColorBrush(Color.FromArgb(0x30, 0x80, 0x80, 0x80));

    public WelcomeWindow() : base(L("How to use Mangosteen"), [], L("Got it"))
    {
        HideCancel();
        var stack = new StackPanel { Width = ContentWidth, Margin = new Thickness(4, 4, 4, 0) };
        var left = new StackPanel();
        left.Children.Add(Header());

        var steps = new StackPanel { Margin = new Thickness(0, 22, 0, 0) };
        var number = 1;
        foreach (var text in new[]
        {
            L("Hold Shift, Ctrl or Alt: each key opens its own wheel."),
            L("Right-click a file in Explorer or on the desktop: the wheel opens at the cursor."),
            L("Click a segment. The result appears next to the original file; the original itself is never changed."),
        })
        {
            steps.Children.Add(Step(number++, text));
        }
        left.Children.Add(steps);
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.Children.Add(left);
        if (DemoVideo() is { } video)
        {
            Grid.SetColumn(video, 1);
            top.Children.Add(video);
        }
        stack.Children.Add(top);

        var cards = new UniformGrid { Columns = 3, Margin = new Thickness(-6, 18, -6, 0) };
        cards.Children.Add(Card("", L("Convert"), "Shift",
            L("Turns files into another format: images, documents, PDF, tables, archives, video and audio.")));
        cards.Children.Add(Card("", L("Tools"), "Ctrl",
            L("Opens an editor with a live preview: crop, compress, annotate, trim a video and more.")));
        cards.Children.Add(Card("", L("Actions"), "Alt",
            L("Float shelf, copy, move, copy the path or open a terminal.")));
        stack.Children.Add(cards);

        stack.Children.Add(Tip(L("Picked the wrong wheel? While it is open, press Shift, Ctrl or Alt to switch. Without the keys: click the Mangosteen icon by the clock and choose files.")));
        Body = stack;

        var autostart = new CheckBox
        {
            Content = L("Start Mangosteen when I sign in to Windows"), VerticalAlignment = VerticalAlignment.Center,
            IsChecked = AppSettings.StartsWithWindows,
        };
        autostart.Click += (_, _) => AppSettings.StartsWithWindows = autostart.IsChecked == true;
        FooterStart = autostart;
        ActionEnabled = true;
    }

    protected override void Run() => Close();

    /// <summary>
    /// The demo: looping, muted, without controls, playing while the window is open. Hidden when the video cannot be
    /// written out or played (for example on Windows N without the Media Feature Pack).
    /// </summary>
    private FrameworkElement? DemoVideo()
    {
        string path;
        try
        {
            path = System.IO.Path.Combine(Platform.Log.Directory, "welcome-demo.mp4");
            using var resource = typeof(WelcomeWindow).Assembly.GetManifestResourceStream("welcome-demo.mp4");
            if (resource is null)
            {
                return null;
            }
            if (!File.Exists(path) || new FileInfo(path).Length != resource.Length)
            {
                Directory.CreateDirectory(Platform.Log.Directory);
                using var file = File.Create(path);
                resource.CopyTo(file);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Platform.Log.Error("welcome video", error);
            return null;
        }
        var media = new MediaElement
        {
            Source = new Uri(path), LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close, IsMuted = true,
            Stretch = Stretch.UniformToFill, Width = VideoWidth, Height = VideoHeight,
        };
        var frame = new Border
        {
            Width = VideoWidth, Height = VideoHeight, Margin = new Thickness(24, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(Color.FromRgb(0x16, 0x14, 0x16)), Child = media,
            Clip = new RectangleGeometry(new Rect(0, 0, VideoWidth, VideoHeight), 10, 10),
        };
        media.MediaEnded += (_, _) =>
        {
            media.Position = TimeSpan.Zero;
            media.Play();
        };
        media.MediaFailed += (_, e) =>
        {
            Platform.Log.Error("welcome video", e.ErrorException);
            media.Close();
            frame.Visibility = Visibility.Collapsed;
        };
        Loaded += (_, _) => media.Play();
        Closed += (_, _) =>
        {
            media.Stop();
            media.Close();
        };
        return frame;
    }

    private static FrameworkElement Header()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var icon = new Image { Width = 64, Height = 64, Margin = new Thickness(0, 0, 18, 0), VerticalAlignment = VerticalAlignment.Center };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        try
        {
            var decoder = BitmapDecoder.Create(new Uri("pack://application:,,,/Assets/Mangosteen.ico"), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            icon.Source = decoder.Frames.MaxBy(frame => frame.PixelWidth);
        }
        catch (Exception error) when (error is IOException or UriFormatException or NotSupportedException)
        {
        }
        grid.Children.Add(icon);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = L("Welcome to Mangosteen"), FontSize = 22, FontWeight = FontWeights.SemiBold });
        titles.Children.Add(new TextBlock
        {
            Text = L("Converters and tools for your files, at a right-click. Everything runs locally, with no third-party services and no limits."),
            TextWrapping = TextWrapping.Wrap, Opacity = 0.75, FontSize = 14, Margin = new Thickness(0, 4, 0, 0),
        });
        Grid.SetColumn(titles, 1);
        grid.Children.Add(titles);
        return grid;
    }

    private static FrameworkElement Step(int number, string text)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var circle = new Border
        {
            Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Background = StepBrush, Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = number.ToString(), Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        row.Children.Add(circle);
        row.Children.Add(new TextBlock { Text = text, FontSize = 14, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    private static FrameworkElement Card(string glyph, string title, string key, string text)
    {
        var heading = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        heading.Children.Add(new Border
        {
            Width = 40, Height = 40, CornerRadius = new CornerRadius(8), Background = TileBrush, Margin = new Thickness(0, 0, 12, 0),
            Child = new TextBlock
            {
                Text = glyph, FontFamily = new FontFamily(WheelGlyphs.FontFamily), FontSize = 18,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        });
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold });
        titles.Children.Add(new TextBlock { Text = key + " + " + L("right-click"), FontSize = 12, Opacity = 0.7, Margin = new Thickness(0, 1, 0, 0) });
        heading.Children.Add(titles);

        var content = new StackPanel();
        content.Children.Add(heading);
        content.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.8, LineHeight = 19 });
        return new Border
        {
            CornerRadius = new CornerRadius(8), Background = CardBrush, Padding = new Thickness(16, 14, 16, 16),
            Margin = new Thickness(6, 0, 6, 0), Child = content,
        };
    }

    private static FrameworkElement Tip(string text)
    {
        var row = new DockPanel { Margin = new Thickness(0, 18, 0, 0) };
        row.Children.Add(new TextBlock
        {
            Text = "", FontFamily = new FontFamily(WheelGlyphs.FontFamily), FontSize = 16, Opacity = 0.7,
            Margin = new Thickness(0, 1, 10, 0), VerticalAlignment = VerticalAlignment.Top,
        });
        row.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.75, FontSize = 13 });
        return row;
    }
}
