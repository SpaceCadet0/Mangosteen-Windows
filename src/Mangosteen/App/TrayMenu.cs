using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Mangosteen.Wheel;
using Microsoft.Win32;
using static Mangosteen.Platform.Native;

namespace Mangosteen;

/// <summary>
/// The tray icon's menu, drawn by Mangosteen: rounded corners, a light or dark background that follows the Windows
/// theme, and the brand colour on the item under the mouse. It closes when you click elsewhere or press Esc.
/// </summary>
internal sealed class TrayMenu : Window
{
    private static readonly Brush Highlight = Frozen(new SolidColorBrush(WheelWindow.Brand));
    private readonly StackPanel _items = new() { Margin = new Thickness(4) };
    private readonly bool _light;
    private readonly Brush _text;
    private readonly Brush _secondary;
    private readonly Brush _line;
    private bool _closing;

    /// <param name="light">Forces the light or dark look (snapshots); null follows Windows.</param>
    public TrayMenu(bool? light = null)
    {
        _light = light ?? IsLightTheme();
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -10000;
        Top = -10000;
        Title = "Mangosteen";
        _text = Frozen(new SolidColorBrush(_light ? Color.FromRgb(0x1A, 0x1A, 0x1A) : Color.FromRgb(0xF0, 0xF0, 0xF2)));
        _secondary = Frozen(new SolidColorBrush(_light ? Color.FromRgb(0x6B, 0x6B, 0x70) : Color.FromRgb(0x9A, 0x9A, 0xA0)));
        _line = Frozen(new SolidColorBrush(_light ? Color.FromRgb(0xE3, 0xE3, 0xE6) : Color.FromRgb(0x40, 0x40, 0x44)));
        Content = new Border
        {
            Background = new SolidColorBrush(_light ? Color.FromRgb(0xF9, 0xF9, 0xFA) : Color.FromRgb(0x2B, 0x2B, 0x2E)),
            BorderBrush = _line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
            Margin = new Thickness(14), MinWidth = 250, Child = _items,
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Direction = 270, Opacity = _light ? 0.18 : 0.45 },
        };
        Deactivated += (_, _) =>
        {
            if (!KeepOpen)
            {
                CloseMenu();
            }
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                CloseMenu();
            }
        };
        Loaded += (_, _) => PlaceAtCursor();
        // SizeToContent may settle on the final size after the first layout pass.
        SizeChanged += (_, _) =>
        {
            if (IsLoaded)
            {
                PlaceAtCursor();
            }
        };
    }

    /// <summary>Windows' "app mode" setting (Settings › Personalisation › Colours).</summary>
    private static bool IsLightTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
    }

    public void AddHeader(string title, string subtitle)
    {
        var stack = new StackPanel { Margin = new Thickness(12, 8, 12, 6) };
        stack.Children.Add(new TextBlock { Text = title, Foreground = _text, FontWeight = FontWeights.SemiBold, FontSize = 13 });
        stack.Children.Add(new TextBlock { Text = subtitle, Foreground = _secondary, FontSize = 12, Margin = new Thickness(0, 2, 0, 0) });
        _items.Children.Add(stack);
    }

    public void AddSeparator() => _items.Children.Add(new Border { Height = 1, Background = _line, Margin = new Thickness(8, 4, 8, 4) });

    /// <summary>A heading with indented items under it.</summary>
    public void AddCaption(string text) =>
        _items.Children.Add(new TextBlock { Text = text, Foreground = _secondary, FontSize = 12, Margin = new Thickness(12, 6, 12, 2) });

    public void AddItem(string text, string glyph, Action action, bool indent = false)
    {
        var icon = new TextBlock
        {
            Text = glyph, FontFamily = new FontFamily(WheelGlyphs.FontFamily), FontSize = 15, Width = 22, Foreground = _text,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0),
        };
        var label = new TextBlock { Text = text, Foreground = _text, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(icon);
        row.Children.Add(label);
        var item = new Border
        {
            Child = row, CornerRadius = new CornerRadius(6), Padding = new Thickness(indent ? 30 : 10, 7, 16, 7),
            Background = Brushes.Transparent, Cursor = Cursors.Hand,
        };
        item.MouseEnter += (_, _) =>
        {
            item.Background = Highlight;
            icon.Foreground = label.Foreground = Brushes.White;
        };
        item.MouseLeave += (_, _) =>
        {
            item.Background = Brushes.Transparent;
            icon.Foreground = label.Foreground = _text;
        };
        item.MouseLeftButtonUp += (_, _) =>
        {
            CloseMenu();
            Dispatcher.BeginInvoke(action);
        };
        _items.Children.Add(item);
    }

    /// <summary>For snapshots: stay open without focus.</summary>
    public bool KeepOpen { get; set; }

    public void ShowAtCursor()
    {
        Show();
        Activate();
    }

    private void CloseMenu()
    {
        if (!_closing)
        {
            _closing = true;
            Close();
        }
    }

    /// <summary>Opens towards the screen's inside from the cursor, like the system menu, kept within the work area.</summary>
    private void PlaceAtCursor()
    {
        GetCursorPos(out var cursor);
        var work = WorkArea(cursor);
        var toDip = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var point = toDip.Transform(new Point(cursor.X, cursor.Y));
        var area = new Rect(toDip.Transform(new Point(work.Left, work.Top)), toDip.Transform(new Point(work.Right, work.Bottom)));
        // The border's margin holds the shadow; line the visible menu up with the cursor.
        const double shadow = 14;
        var left = point.X - shadow;
        var top = point.Y - ActualHeight + shadow;
        if (left + ActualWidth > area.Right)
        {
            left = point.X - ActualWidth + shadow;
        }
        if (top < area.Top)
        {
            top = point.Y - shadow;
        }
        Left = Math.Clamp(left, area.Left - shadow, Math.Max(area.Left, area.Right - ActualWidth + shadow));
        Top = Math.Clamp(top, area.Top - shadow, Math.Max(area.Top, area.Bottom - ActualHeight + shadow));
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
