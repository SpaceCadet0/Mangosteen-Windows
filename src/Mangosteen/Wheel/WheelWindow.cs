using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;
using Mangosteen.Core;
using Mangosteen.Platform;
using static Mangosteen.Platform.Native;

namespace Mangosteen.Wheel;

/// <summary>
/// The round wheel shown at the cursor: dark ring segments with small gaps, the brand colour on the
/// segment under the mouse, and the wheel name with the file count in the centre.
/// </summary>
/// <remarks>
/// The window never takes focus (WS_EX_NOACTIVATE), so Explorer stays active and no foreground rules
/// get in the way. Esc and clicks outside are caught by the global hooks and close it.
/// </remarks>
internal sealed class WheelWindow : Window
{
    private const double WindowSize = 340;            // window, DIP
    private const double DiscRadius = 156;      // dark disc
    private const double OuterRadius = 150;     // segments' outer edge
    private const double InnerRadius = 64;      // segments' inner edge
    private const double CenterRadius = 58;     // centre circle
    private const double Gap = 6;               // between segments
    private const double Corner = 7;            // segment corner rounding

    public static readonly Color Brand = Color.FromRgb(0x67, 0x1E, 0x38);
    private static readonly Brush DiscBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1E)));
    private static readonly Brush DiscBorderBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3D)));
    private static readonly Brush SegmentBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x2F)));
    private static readonly Brush HighlightBrush = Frozen(new SolidColorBrush(Brand));
    private static readonly Brush CenterBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x24)));
    private static readonly Brush TextBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xEA)));
    private static readonly Brush SecondaryBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xA0)));

    private readonly POINT _anchor;
    private bool _closing;

    /// <summary>Raised when a segment is clicked. The window closes itself first.</summary>
    public event Action<WheelItem>? Chosen;

    /// <summary>Raised once the window is placed: the disc in physical screen pixels.</summary>
    public event Action<ScreenCircle>? Placed;

    public WheelWindow(WheelKind kind, IReadOnlyList<WheelItem> items, int fileCount, POINT anchor)
    {
        _anchor = anchor;
        Title = "Mangosteen";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        Width = WindowSize;
        Height = WindowSize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -10000;
        Top = -10000;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        var canvas = new Canvas { Width = WindowSize, Height = WindowSize };
        var center = new Point(WindowSize / 2, WindowSize / 2);

        canvas.Children.Add(Circle(center, DiscRadius, DiscBrush, DiscBorderBrush, effect: new DropShadowEffect
        {
            BlurRadius = 14, ShadowDepth = 3, Direction = 270, Opacity = 0.45, Color = Colors.Black,
        }));

        for (var i = 0; i < items.Count; i++)
        {
            AddSegment(canvas, center, items[i], i, items.Count);
        }

        canvas.Children.Add(Circle(center, CenterRadius, CenterBrush, null));
        var centerText = new StackPanel { Width = CenterRadius * 2 - 12, IsHitTestVisible = false };
        centerText.Children.Add(new TextBlock
        {
            Text = WheelCatalog.Title(kind), Foreground = Brushes.White, FontSize = 15, FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center,
        });
        centerText.Children.Add(new TextBlock
        {
            Text = items.Count == 0 ? (kind == WheelKind.Convert ? "Nothing to convert" : "Nothing here yet")
                : WheelCatalog.FileCount(fileCount),
            Foreground = SecondaryBrush, FontSize = 12, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
        });
        PlaceCentered(canvas, centerText, center, CenterRadius * 2 - 12, 44);

        var scale = new ScaleTransform(0.92, 0.92, WindowSize / 2, WindowSize / 2);
        canvas.RenderTransform = scale;
        canvas.Opacity = 0;
        Content = canvas;

        Loaded += (_, _) =>
        {
            var duration = TimeSpan.FromMilliseconds(120);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            canvas.BeginAnimation(OpacityProperty, new DoubleAnimation(1, duration));
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, duration) { EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, duration) { EasingFunction = ease });
        };
    }

    public void CloseWheel()
    {
        if (_closing)
        {
            return;
        }
        _closing = true;
        Close();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        var style = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST);
        Place();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        // Moving onto a monitor with another scale resizes the window; centre it on the anchor again.
        Dispatcher.BeginInvoke(Place);
    }

    /// <summary>Centres the window on the anchor in physical pixels, kept inside the monitor's work area.</summary>
    private void Place()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || _closing)
        {
            return;
        }
        var dpi = MonitorDpi(_anchor);
        var size = (int)Math.Round(WindowSize * dpi / 96.0);
        var work = WorkArea(_anchor);
        var x = Math.Clamp(_anchor.X - size / 2, work.Left, Math.Max(work.Left, work.Right - size));
        var y = Math.Clamp(_anchor.Y - size / 2, work.Top, Math.Max(work.Top, work.Bottom - size));
        SetWindowPos(hwnd, HWND_TOPMOST, x, y, size, size, SWP_NOACTIVATE);
        GetWindowRect(hwnd, out var rect);
        var width = rect.Right - rect.Left;
        Placed?.Invoke(new ScreenCircle(rect.Left + width / 2.0, rect.Top + (rect.Bottom - rect.Top) / 2.0,
            width * DiscRadius / WindowSize));
    }

    private void AddSegment(Canvas canvas, Point center, WheelItem item, int index, int count)
    {
        var span = 360.0 / count;
        var angle = -90 + index * span;
        var path = new Path
        {
            Data = SegmentGeometry(center, angle, span, count),
            Fill = SegmentBrush,
            Stroke = SegmentBrush,
            StrokeThickness = count == 1 ? 0 : Corner * 2,
            StrokeLineJoin = PenLineJoin.Round,
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = item.Available ? null : "Coming in a later version",
        };

        var foreground = item.Available ? TextBrush : SecondaryBrush;
        var glyph = new TextBlock
        {
            Text = WheelGlyphs.For(item.Icon), FontFamily = new FontFamily(WheelGlyphs.FontFamily), FontSize = 20,
            Foreground = foreground, HorizontalAlignment = HorizontalAlignment.Center,
        };
        var label = new TextBlock
        {
            Text = item.Title, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = foreground,
            TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 5, 0, 0),
        };
        var stack = new StackPanel { IsHitTestVisible = false };
        stack.Children.Add(glyph);
        stack.Children.Add(label);
        if (!item.Available)
        {
            stack.Opacity = 0.6;
        }

        var radius = (InnerRadius + OuterRadius) / 2;
        var radians = angle * Math.PI / 180;
        var labelCenter = new Point(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
        var labelWidth = Math.Min(84, count <= 2 ? 120 : 2 * radius * Math.Sin(Math.PI / count) - 8);

        path.MouseEnter += (_, _) =>
        {
            path.Fill = path.Stroke = HighlightBrush;
            glyph.Foreground = label.Foreground = Brushes.White;
        };
        path.MouseLeave += (_, _) =>
        {
            path.Fill = path.Stroke = SegmentBrush;
            glyph.Foreground = label.Foreground = foreground;
        };
        path.MouseLeftButtonUp += (_, args) =>
        {
            args.Handled = true;
            CloseWheel();
            Chosen?.Invoke(item);
        };

        canvas.Children.Add(path);
        PlaceCentered(canvas, stack, labelCenter, labelWidth, 46);
    }

    /// <summary>
    /// A ring sector whose straight edges stay <see cref="Gap"/> apart from the neighbours. The geometry is
    /// shrunk by <see cref="Corner"/> and drawn with a round-joined stroke of twice that, which rounds the corners.
    /// </summary>
    internal static Geometry SegmentGeometry(Point center, double angle, double span, int count)
    {
        if (count == 1)
        {
            return new CombinedGeometry(GeometryCombineMode.Exclude,
                new EllipseGeometry(center, OuterRadius, OuterRadius),
                new EllipseGeometry(center, InnerRadius, InnerRadius));
        }
        var outer = OuterRadius - Corner;
        var inner = InnerRadius + Corner;
        var half = Gap / 2 + Corner;
        double Offset(double r) => Math.Asin(Math.Min(1, half / r)) * 180 / Math.PI;
        Point At(double r, double degrees)
        {
            var radians = degrees * Math.PI / 180;
            return new Point(center.X + r * Math.Cos(radians), center.Y + r * Math.Sin(radians));
        }

        var start = angle - span / 2;
        var end = angle + span / 2;
        var figure = new PathFigure { StartPoint = At(inner, start + Offset(inner)), IsClosed = true, IsFilled = true };
        figure.Segments.Add(new LineSegment(At(outer, start + Offset(outer)), true));
        figure.Segments.Add(new ArcSegment(At(outer, end - Offset(outer)), new Size(outer, outer), 0,
            span - 2 * Offset(outer) > 180, SweepDirection.Clockwise, true));
        figure.Segments.Add(new LineSegment(At(inner, end - Offset(inner)), true));
        figure.Segments.Add(new ArcSegment(At(inner, start + Offset(inner)), new Size(inner, inner), 0,
            span - 2 * Offset(inner) > 180, SweepDirection.Counterclockwise, true));
        var geometry = new PathGeometry([figure]);
        geometry.Freeze();
        return geometry;
    }

    private static Ellipse Circle(Point center, double radius, Brush fill, Brush? stroke, Effect? effect = null)
    {
        var ellipse = new Ellipse
        {
            Width = radius * 2, Height = radius * 2, Fill = fill, Stroke = stroke, StrokeThickness = stroke is null ? 0 : 1,
            Effect = effect,
        };
        Canvas.SetLeft(ellipse, center.X - radius);
        Canvas.SetTop(ellipse, center.Y - radius);
        return ellipse;
    }

    private static void PlaceCentered(Canvas canvas, FrameworkElement element, Point at, double width, double height)
    {
        var host = new Grid { Width = width, Height = height, IsHitTestVisible = false };
        element.VerticalAlignment = VerticalAlignment.Center;
        element.HorizontalAlignment = HorizontalAlignment.Center;
        host.Children.Add(element);
        Canvas.SetLeft(host, at.X - width / 2);
        Canvas.SetTop(host, at.Y - height / 2);
        canvas.Children.Add(host);
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
