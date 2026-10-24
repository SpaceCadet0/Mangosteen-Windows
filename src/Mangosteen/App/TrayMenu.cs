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
/// Submenus open beside their item when the mouse rests on it; they never take the focus, so the menu stays open.
/// </summary>
internal sealed class TrayMenu : Window
{
    private static readonly Brush Highlight = Frozen(new SolidColorBrush(WheelWindow.Brand));
    private readonly StackPanel _items = new() { Margin = new Thickness(4) };
    private readonly List<FrameworkElement> _rows = [];
    private readonly bool _light;
    private readonly Brush _text;
    private readonly Brush _secondary;
    private readonly Brush _line;
    private bool _closing;
    private TrayMenu? _parent;
    private TrayMenu? _child;
    private Rect? _anchor; // the item a submenu belongs to, in screen DIPs

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
            if (Trace)
            {
                Platform.Log.Write($"menu: deactivated (submenu: {_parent is not null})");
            }
            if (!KeepOpen && _parent is null)
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
        Loaded += (_, _) => Place();
        // SizeToContent may settle on the final size after the first layout pass.
        SizeChanged += (_, _) =>
        {
            if (IsLoaded)
            {
                Place();
            }
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (_parent is not null)
        {
            // A submenu must not take the focus from the menu, or the menu would close.
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_NOACTIVATE);
        }
    }

    private TrayMenu Root => _parent?.Root ?? this;

    private void Place()
    {
        if (_anchor is { } anchor)
        {
            PlaceBeside(anchor);
        }
        else
        {
            PlaceAtCursor();
        }
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

    public void AddItem(string text, string glyph, Action action, bool indent = false, string? tooltip = null) =>
        AddRow(text, glyph, indent, tooltip, submenu: null, action);

    /// <summary>An item that opens a submenu, filled by <paramref name="fill"/> each time it opens.</summary>
    public void AddSubmenu(string text, string glyph, Action<TrayMenu> fill, string? tooltip = null) =>
        AddRow(text, glyph, false, tooltip, fill, null);

    /// <summary>A line of grey text that does nothing (an empty submenu).</summary>
    public void AddNote(string text) =>
        _items.Children.Add(new TextBlock { Text = text, Foreground = _secondary, FontSize = 13, Margin = new Thickness(12, 7, 16, 7) });

    private void AddRow(string text, string glyph, bool indent, string? tooltip, Action<TrayMenu>? submenu, Action? action)
    {
        var icon = new TextBlock
        {
            Text = glyph, FontFamily = new FontFamily(WheelGlyphs.FontFamily), FontSize = 15, Width = 22, Foreground = _text,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0),
        };
        var label = new TextBlock
        {
            Text = text, Foreground = _text, FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 320, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var chevron = new TextBlock
        {
            Text = submenu is null ? "" : "\uE76C", FontFamily = new FontFamily(WheelGlyphs.FontFamily), FontSize = 10,
            Foreground = _secondary, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0),
        };
        var row = new DockPanel();
        DockPanel.SetDock(chevron, Dock.Right);
        row.Children.Add(chevron);
        row.Children.Add(icon);
        row.Children.Add(label);
        var item = new Border
        {
            Child = row, CornerRadius = new CornerRadius(6), Padding = new Thickness(indent ? 30 : 10, 7, submenu is null ? 16 : 10, 7),
            Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = tooltip,
        };
        void Lit(bool on)
        {
            item.Background = on ? Highlight : Brushes.Transparent;
            icon.Foreground = label.Foreground = on ? Brushes.White : _text;
            chevron.Foreground = on ? Brushes.White : _secondary;
        }
        item.MouseEnter += (_, _) =>
        {
            Lit(true);
            if (submenu is not null)
            {
                OpenChild(item, submenu, () => Lit(false));
            }
            else
            {
                CloseChild();
            }
        };
        item.MouseLeave += (_, _) =>
        {
            if (submenu is null || _child is null)
            {
                Lit(false);
            }
        };
        if (Trace)
        {
            item.PreviewMouseLeftButtonDown += (_, _) => Platform.Log.Write($"menu: down on \"{text}\"");
            Loaded += (_, _) => Platform.Log.Write($"menu: window 0x{new System.Windows.Interop.WindowInteropHelper(this).Handle:X} shown with \"{text}\"");
            Closed += (_, _) => Platform.Log.Write($"menu: closed ({text} row)");
        }
        item.MouseLeftButtonUp += (_, _) =>
        {
            if (Trace)
            {
                Platform.Log.Write($"menu: up on \"{text}\"");
            }
            if (submenu is not null)
            {
                OpenChild(item, submenu, () => Lit(false));
                return;
            }
            Root.CloseMenu();
            if (action is not null)
            {
                Dispatcher.BeginInvoke(action);
            }
        };
        _items.Children.Add(item);
        _rows.Add(item);
    }

    /// <summary>For the menu self-test: write what the menus do to the log.</summary>
    internal static bool Trace { get; set; }

    /// <summary>For the menu self-test: the submenu now open, if any.</summary>
    internal TrayMenu? OpenSubmenu => _child;

    /// <summary>For the menu self-test: the centre of an item (items and submenus only), in screen pixels.</summary>
    internal Point ItemCenter(int index)
    {
        var row = _rows[index];
        return row.PointToScreen(new Point(row.ActualWidth / 2, row.ActualHeight / 2));
    }

    private void OpenChild(FrameworkElement item, Action<TrayMenu> fill, Action closed)
    {
        CloseChild();
        var child = new TrayMenu(_light) { _parent = this, KeepOpen = KeepOpen, Topmost = true };
        fill(child);
        var toDip = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var topLeft = toDip.Transform(item.PointToScreen(new Point(0, 0)));
        var bottomRight = toDip.Transform(item.PointToScreen(new Point(item.ActualWidth, item.ActualHeight)));
        child._anchor = new Rect(topLeft, bottomRight);
        child.Closed += (_, _) =>
        {
            if (ReferenceEquals(_child, child))
            {
                _child = null;
            }
            closed();
        };
        _child = child;
        child.ShowActivated = false;
        child.Show();
    }

    private void CloseChild()
    {
        if (_child is { } child)
        {
            _child = null;
            child.CloseMenu();
        }
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
            CloseChild();
            Close();
        }
    }

    /// <summary>A submenu: to the right of its item, or to the left of the menu when there is no room.</summary>
    private void PlaceBeside(Rect anchor)
    {
        var center = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.Transform(anchor.TopLeft) ?? anchor.TopLeft;
        var work = WorkArea(new POINT { X = (int)center.X, Y = (int)center.Y });
        var toDip = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var area = new Rect(toDip.Transform(new Point(work.Left, work.Top)), toDip.Transform(new Point(work.Right, work.Bottom)));
        const double shadow = 14, inset = 5; // the border's margin holds the shadow; the item sits inside the menu's padding
        var left = anchor.Right + inset - shadow;
        if (left + ActualWidth - shadow > area.Right)
        {
            left = anchor.Left - inset - ActualWidth + shadow;
        }
        var top = anchor.Top - inset - shadow;
        Left = Math.Clamp(left, area.Left - shadow, Math.Max(area.Left, area.Right - ActualWidth + shadow));
        Top = Math.Clamp(top, area.Top - shadow, Math.Max(area.Top, area.Bottom - ActualHeight + shadow));
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
