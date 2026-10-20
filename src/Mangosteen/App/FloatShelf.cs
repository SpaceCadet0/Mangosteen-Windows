using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Mangosteen.Platform;
using Mangosteen.Wheel;

namespace Mangosteen;

/// <summary>
/// A floating shelf that holds references to files so they can be dragged into another app later. Files are
/// never copied onto the shelf; it only remembers where they are. Dragging out copies unless Move is on, so
/// originals are never taken by accident.
/// </summary>
internal sealed class FloatShelf : Window
{
    private static readonly Brush Panel = Frozen(new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1E)));
    private static readonly Brush Line = Frozen(new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3D)));
    private static readonly Brush Hover = Frozen(new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x2F)));
    private static readonly Brush TextBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xEA)));
    private static readonly Brush Secondary = Frozen(new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xA0)));
    private static readonly Brush BrandBrush = Frozen(new SolidColorBrush(WheelWindow.Brand));

    /// <summary>Thumbnails sit in a square of this many DIPs, keeping their proportions.</summary>
    private const int ThumbnailSide = 48;

    private readonly List<string> _items = [];
    private readonly StackPanel _rows = new();
    private readonly ToggleButton _moveMode;
    private readonly TextBlock _empty;

    public FloatShelf()
    {
        Title = "Float";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Width = 300;
        SizeToContent = SizeToContent.Height;
        AllowDrop = true;

        _moveMode = new ToggleButton
        {
            Content = "Copy", ToolTip = "Dragging out copies the files. Click to move them instead.",
            Foreground = TextBrush, Background = Line, BorderThickness = new Thickness(0), Padding = new Thickness(10, 2, 10, 2),
            Margin = new Thickness(0, 0, 6, 0), Cursor = Cursors.Hand,
        };
        _moveMode.Checked += (_, _) =>
        {
            _moveMode.Content = "Move";
            _moveMode.Background = BrandBrush;
            _moveMode.ToolTip = "Dragging out moves the files. Click to copy them instead.";
        };
        _moveMode.Unchecked += (_, _) =>
        {
            _moveMode.Content = "Copy";
            _moveMode.Background = Line;
            _moveMode.ToolTip = "Dragging out copies the files. Click to move them instead.";
        };

        var clear = HeaderButton("", "Remove all");
        clear.Click += (_, _) => Clear();
        var close = HeaderButton("", "Hide the shelf (it keeps its files)");
        close.Click += (_, _) => Hide();

        var title = new TextBlock
        {
            Text = "Float", Foreground = Brushes.White, FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
        };
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8), Background = Brushes.Transparent };
        DockPanel.SetDock(close, Dock.Right);
        DockPanel.SetDock(clear, Dock.Right);
        DockPanel.SetDock(_moveMode, Dock.Right);
        header.Children.Add(close);
        header.Children.Add(clear);
        header.Children.Add(_moveMode);
        header.Children.Add(title);
        header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        };

        _empty = new TextBlock
        {
            Text = "Drop files here, or use Float on the Actions wheel.", Foreground = Secondary, FontSize = 12,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 16, 4, 16), TextAlignment = TextAlignment.Center,
        };
        var scroll = new ScrollViewer { Content = _rows, MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(_empty);
        stack.Children.Add(scroll);
        Content = new Border
        {
            Background = Panel, BorderBrush = Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 10, 12, 12), Margin = new Thickness(10), Child = stack,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Opacity = 0.4, Direction = 270 },
        };

        DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None;
            e.Handled = true;
        };
        Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] dropped)
            {
                Add(dropped);
            }
        };
        MouseEnter += (_, _) => Reload();
        Loaded += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - ActualWidth - 8;
            Top = area.Top + (area.Height - ActualHeight) / 3;
        };
        Reload();
    }

    public void Add(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (!_items.Contains(full, StringComparer.OrdinalIgnoreCase))
            {
                _items.Add(full);
            }
        }
        Reload();
        if (!IsVisible)
        {
            Show();
        }
    }

    public void Toggle()
    {
        if (IsVisible)
        {
            Hide();
        }
        else
        {
            Reload();
            Show();
        }
    }

    private void Clear()
    {
        _items.Clear();
        Reload();
    }

    private void Reload()
    {
        _rows.Children.Clear();
        _empty.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var available = _items.Where(Exists).ToList();
        if (available.Count > 1)
        {
            _rows.Children.Add(Row(available, $"All {available.Count} items", "Drag them all at once", null));
        }
        foreach (var item in _items)
        {
            _rows.Children.Add(Row([item], Path.GetFileName(item), Path.GetDirectoryName(item) ?? "", item));
        }
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private FrameworkElement Row(IReadOnlyList<string> paths, string title, string subtitle, string? single)
    {
        var present = paths.All(Exists);
        var icon = new Image { Width = 32, Height = 32, Source = single is null ? null : ShellIcon(single), Stretch = Stretch.Uniform };
        // A real thumbnail (photo, video frame, PDF page) replaces the icon when the shell has one.
        var picture = new Border { Width = ThumbnailSide, Height = ThumbnailSide, Margin = new Thickness(0, 0, 10, 0), Child = icon };
        if (single is not null && present)
        {
            Thumbnails.Request(single, ThumbnailSide * 2, Dispatcher, thumbnail =>
            {
                if (thumbnail is null)
                {
                    return;
                }
                var scale = Math.Min((double)ThumbnailSide / thumbnail.PixelWidth, (double)ThumbnailSide / thumbnail.PixelHeight);
                icon.Width = Math.Max(1, thumbnail.PixelWidth * scale);
                icon.Height = Math.Max(1, thumbnail.PixelHeight * scale);
                icon.Source = thumbnail;
                icon.Clip = new RectangleGeometry(new Rect(0, 0, icon.Width, icon.Height), 4, 4);
            });
        }
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, Foreground = TextBrush, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock
        {
            Text = present ? subtitle : "Moved or deleted", Foreground = Secondary, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var remove = new Button
        {
            Content = "", FontFamily = new FontFamily(WheelGlyphs.FontFamily), FontSize = 10, Foreground = Secondary,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(6), Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Hidden, ToolTip = "Remove from the shelf",
        };
        remove.Click += (_, _) =>
        {
            if (single is null)
            {
                Clear();
            }
            else
            {
                _items.Remove(single);
                Reload();
            }
        };
        var dock = new DockPanel();
        DockPanel.SetDock(remove, Dock.Right);
        DockPanel.SetDock(picture, Dock.Left);
        dock.Children.Add(remove);
        if (single is not null)
        {
            dock.Children.Add(picture);
        }
        dock.Children.Add(text);
        var row = new Border
        {
            Child = dock, Padding = new Thickness(8, 6, 4, 6), CornerRadius = new CornerRadius(8), Background = Brushes.Transparent,
            Opacity = present ? 1 : 0.45, Cursor = present ? Cursors.Hand : Cursors.Arrow, Margin = new Thickness(0, 0, 0, 2),
        };
        row.MouseEnter += (_, _) =>
        {
            row.Background = Hover;
            remove.Visibility = Visibility.Visible;
        };
        row.MouseLeave += (_, _) =>
        {
            row.Background = Brushes.Transparent;
            remove.Visibility = Visibility.Hidden;
        };

        Point? pressed = null;
        row.PreviewMouseLeftButtonDown += (_, e) =>
        {
            pressed = e.GetPosition(row);
            if (e.ClickCount == 2 && single is not null && present)
            {
                Process.Start(new ProcessStartInfo(single) { UseShellExecute = true });
            }
        };
        row.PreviewMouseLeftButtonUp += (_, _) => pressed = null;
        row.MouseMove += (_, e) =>
        {
            if (pressed is not { } start || e.LeftButton != MouseButtonState.Pressed || !present)
            {
                return;
            }
            var delta = e.GetPosition(row) - start;
            if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }
            pressed = null;
            var allowed = _moveMode.IsChecked == true ? DragDropEffects.Move | DragDropEffects.Copy : DragDropEffects.Copy | DragDropEffects.Link;
            var data = new DataObject(DataFormats.FileDrop, paths.ToArray());
            var result = DragDrop.DoDragDrop(row, data, allowed);
            Log.Write($"float: dragged {paths.Count} items out, effect {result}");
            RefreshAfterDrag();
        };

        if (single is not null)
        {
            var menu = new ContextMenu();
            var open = new MenuItem { Header = "Open" };
            open.Click += (_, _) => Process.Start(new ProcessStartInfo(single) { UseShellExecute = true });
            var reveal = new MenuItem { Header = "Show in Explorer" };
            reveal.Click += (_, _) => FileActions.Reveal(single);
            var drop = new MenuItem { Header = "Remove from shelf" };
            drop.Click += (_, _) =>
            {
                _items.Remove(single);
                Reload();
            };
            menu.Items.Add(open);
            menu.Items.Add(reveal);
            menu.Items.Add(new Separator());
            menu.Items.Add(drop);
            open.IsEnabled = reveal.IsEnabled = present;
            row.ContextMenu = menu;
        }
        return row;
    }

    /// <summary>Explorer finishes moves a moment after the drop, so availability is checked again for a few seconds.</summary>
    private void RefreshAfterDrag()
    {
        Reload();
        var ticks = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) =>
        {
            Reload();
            if (++ticks >= 6)
            {
                timer.Stop();
            }
        };
        timer.Start();
    }

    private Button HeaderButton(string glyph, string tooltip) => new()
    {
        Content = glyph, FontFamily = new FontFamily(WheelGlyphs.FontFamily), FontSize = 12, Foreground = Secondary,
        Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(6, 4, 6, 4), ToolTip = tooltip,
        Cursor = Cursors.Hand,
    };

    // MARK: Shell icons

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private const uint SHGFI_ICON = 0x100;
    private const uint SHGFI_LARGEICON = 0x0;

    private static ImageSource? ShellIcon(string path)
    {
        var info = new SHFILEINFO();
        if (SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_LARGEICON) == IntPtr.Zero || info.hIcon == IntPtr.Zero)
        {
            return null;
        }
        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
