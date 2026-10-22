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
/// segment under the mouse, and the wheel name with the file count in the centre. Clicking the centre switches to
/// the next wheel; under the mouse the centre and the outer ring take the brand colour.
/// </summary>
/// <remarks>
/// The window never takes focus (WS_EX_NOACTIVATE), so Explorer stays active and no foreground rules
/// get in the way. Esc and clicks outside are caught by the global hooks and close it.
/// </remarks>
internal sealed class WheelWindow : Window
{
    private const double WindowSize = 420;      // window, DIP: room for the largest wheel and its shadow
    private const double MinOuterRadius = 150;  // segments' outer edge; dense wheels grow up to the maximum
    private const double MaxOuterRadius = 190;  //   so that every label fits inside its segment
    private const double DiscMargin = 6;        // dark disc beyond the segments
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

    private static readonly Point Center = new(WindowSize / 2, WindowSize / 2);

    private readonly POINT _anchor;
    private readonly Canvas _layer = new() { Width = WindowSize, Height = WindowSize };
    private Ellipse? _disc;
    private double _outerRadius = MinOuterRadius;
    private bool _closing;

    /// <summary>Raised when a segment is clicked. The window closes itself first.</summary>
    public event Action<WheelItem>? Chosen;

    /// <summary>Raised when the centre is clicked: the controller shows the next wheel.</summary>
    public event Action? HubClicked;

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

        // The disc stays; the segments and the centre live in a layer that is replaced when the wheel switches.
        var canvas = new Canvas { Width = WindowSize, Height = WindowSize };
        _disc = Circle(Center, MinOuterRadius + DiscMargin, DiscBrush, DiscBorderBrush, effect: new DropShadowEffect
        {
            BlurRadius = 14, ShadowDepth = 3, Direction = 270, Opacity = 0.45, Color = Colors.Black,
        });
        canvas.Children.Add(_disc);
        canvas.Children.Add(_layer);
        _layer.Children.Add(BuildLayer(kind, items, fileCount));

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

    /// <summary>Shows another wheel for the same files in place: same window, same position.</summary>
    public void ShowItems(WheelKind kind, IReadOnlyList<WheelItem> items, int fileCount)
    {
        var layer = BuildLayer(kind, items, fileCount);
        _layer.Children.Clear();
        _layer.Children.Add(layer);
        layer.BeginAnimation(OpacityProperty, new DoubleAnimation(0.3, 1, TimeSpan.FromMilliseconds(90)));
        // The new wheel may be larger or smaller; the hooks need its new outline.
        Place();
    }

    private Canvas BuildLayer(WheelKind kind, IReadOnlyList<WheelItem> items, int fileCount)
    {
        var layer = new Canvas { Width = WindowSize, Height = WindowSize };
        // The smallest wheel on which every label fits its segment; the largest one when even that is not enough.
        var fits = new List<LabelFit?>();
        for (var outer = MinOuterRadius; ; outer += 10)
        {
            fits = items.Select((item, i) => FitLabel(item.Title, -90 + i * 360.0 / items.Count, 360.0 / items.Count, outer)).ToList();
            _outerRadius = outer;
            if (fits.All(f => f is not null) || outer >= MaxOuterRadius)
            {
                break;
            }
        }
        if (fits.Any(f => f is null))
        {
            Log.Write($"wheel: titles that do not fit their segment: {string.Join(", ", items.Where((_, i) => fits[i] is null).Select(i => i.Title))}");
        }
        SetDisc(_outerRadius + DiscMargin);
        for (var i = 0; i < items.Count; i++)
        {
            AddSegment(layer, Center, items[i], i, items.Count, fits[i]);
        }

        var hub = Circle(Center, CenterRadius, CenterBrush, null);
        hub.Cursor = System.Windows.Input.Cursors.Hand;
        hub.MouseEnter += (_, _) => HighlightHub(hub, true);
        hub.MouseLeave += (_, _) => HighlightHub(hub, false);
        hub.MouseLeftButtonUp += (_, args) =>
        {
            args.Handled = true;
            HubClicked?.Invoke();
        };
        layer.Children.Add(hub);
        var centerText = new StackPanel { Width = CenterRadius * 2 - 12, IsHitTestVisible = false };
        centerText.Children.Add(new TextBlock
        {
            Text = WheelCatalog.Title(kind), Foreground = Brushes.White, FontSize = 15, FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center,
        });
        centerText.Children.Add(new TextBlock
        {
            Text = items.Count == 0 ? (kind == WheelKind.Convert ? L("Nothing to convert") : L("Nothing here yet"))
                : WheelCatalog.FileCount(fileCount),
            Foreground = SecondaryBrush, FontSize = 12, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
        });
        PlaceCentered(layer, centerText, Center, CenterRadius * 2 - 12, 44);
        return layer;
    }

    /// <summary>The centre and the outer ring in the brand colour while the mouse is over the centre.</summary>
    private void HighlightHub(Ellipse hub, bool on)
    {
        hub.Fill = on ? HighlightBrush : CenterBrush;
        if (_disc is { } disc)
        {
            disc.Stroke = on ? HighlightBrush : DiscBorderBrush;
            disc.StrokeThickness = on ? 6 : 1;
        }
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
            width * (_outerRadius + DiscMargin) / WindowSize));
    }

    private void SetDisc(double radius)
    {
        if (_disc is { } disc)
        {
            disc.Width = disc.Height = radius * 2;
            Canvas.SetLeft(disc, Center.X - radius);
            Canvas.SetTop(disc, Center.Y - radius);
        }
    }

    /// <summary>How a segment's icon and title are drawn so that they stay inside the segment.</summary>
    private sealed record LabelFit(double GlyphSize, double FontSize, double Radius, string[] Lines, double Width, double Height);

    private static readonly Typeface LabelFace = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private const double GlyphGap = 4;

    private static readonly Dictionary<(string, double), double> Widths = [];

    /// <summary>The width of a title line in the label font (cached: the fitting tries many sizes and radii).</summary>
    private static double TextWidth(string text, double size)
    {
        if (!Widths.TryGetValue((text, size), out var width))
        {
            // Measured as the wheel draws it (Display text formatting, which rounds glyphs to whole pixels and can be
            // wider than the ideal layout), with a little room so the last letter is never clipped.
            width = new FormattedText(text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, LabelFace,
                size, Brushes.White, null, TextFormattingMode.Display, 1).WidthIncludingTrailingWhitespace + 4;
            Widths[(text, size)] = width;
        }
        return width;
    }

    /// <summary>
    /// The largest icon and title, on one or two lines, whose boxes lie inside the segment (between the rings, clear of
    /// the gaps) at some distance from the centre; null when nothing fits down to 8 pt with a 14 pt icon.
    /// </summary>
    private LabelFit? FitLabel(string title, double angle, double span, double outer)
    {
        var words = title.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var candidates = new List<string[]> { new[] { title } };
        for (var split = 1; split < words.Length; split++)
        {
            candidates.Add([string.Join(' ', words[..split]), string.Join(' ', words[split..])]);
        }
        // Every segment keeps its icon: dense wheels get a smaller one first.
        foreach (var glyph in span <= 36 ? new[] { 14.0 } : [20.0, 18, 16, 14])
        {
            for (var size = 12.5; size >= 8; size -= 0.5)
            {
                var lineHeight = size * LabelFace.FontFamily.LineSpacing;
                // One line first, then the most even split.
                foreach (var lines in candidates.OrderBy(c => c.Length).ThenBy(c => c.Max(l => TextWidth(l, size))))
                {
                    var width = lines.Max(l => TextWidth(l, size));
                    var textHeight = lines.Length * lineHeight;
                    var glyphHeight = glyph * 1.3;
                    var height = glyphHeight + (glyph > 0 ? GlyphGap : 0) + textHeight;
                    for (var radius = InnerRadius + 16; radius <= outer - 10; radius += 2)
                    {
                        var radians = angle * Math.PI / 180;
                        var (x, y) = (radius * Math.Cos(radians), radius * Math.Sin(radians));
                        var top = y - height / 2;
                        var boxes = new List<Rect> { new(x - width / 2, top + height - textHeight, width, textHeight) };
                        if (glyph > 0)
                        {
                            boxes.Add(new Rect(x - glyph / 2, top, glyph, glyphHeight));
                        }
                        if (boxes.All(box => InsideSegment(box, angle, span, outer)))
                        {
                            return new LabelFit(glyph, size, radius, lines, Math.Max(width, glyph), height);
                        }
                    }
                }
            }
        }
        return null;
    }

    /// <summary>True when the box (relative to the centre) is inside the ring sector, with a small margin.</summary>
    private static bool InsideSegment(Rect box, double angle, double span, double outer)
    {
        const double margin = 2;
        foreach (var px in new[] { box.Left, box.Left + box.Width / 2, box.Right })
        {
            foreach (var py in new[] { box.Top, box.Top + box.Height / 2, box.Bottom })
            {
                var distance = Math.Sqrt(px * px + py * py);
                if (distance < InnerRadius + margin || distance > outer - margin)
                {
                    return false;
                }
                if (span < 360)
                {
                    var off = Math.Abs(((Math.Atan2(py, px) * 180 / Math.PI - angle) % 360 + 540) % 360 - 180);
                    var gap = Math.Asin(Math.Min(1, (Gap / 2 + margin) / distance)) * 180 / Math.PI;
                    if (off > span / 2 - gap)
                    {
                        return false;
                    }
                }
            }
        }
        return true;
    }

    private void AddSegment(Canvas canvas, Point center, WheelItem item, int index, int count, LabelFit? fit)
    {
        var span = 360.0 / count;
        var angle = -90 + index * span;
        // Nothing fitted even on the largest wheel: the smallest icon and title, in the middle of the ring.
        fit ??= new LabelFit(14, 8, (InnerRadius + _outerRadius) / 2, [item.Title], Math.Max(TextWidth(item.Title, 8), 14),
            14 * 1.3 + GlyphGap + 8 * LabelFace.FontFamily.LineSpacing);
        var path = new Path
        {
            Data = SegmentGeometry(center, angle, span, count, _outerRadius),
            Fill = SegmentBrush,
            Stroke = SegmentBrush,
            StrokeThickness = count == 1 ? 0 : Corner * 2,
            StrokeLineJoin = PenLineJoin.Round,
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = item.Available ? null : L("Coming in a later version"),
        };

        var foreground = item.Available ? TextBrush : SecondaryBrush;
        var glyph = new TextBlock
        {
            Text = WheelGlyphs.For(item.Icon), FontFamily = new FontFamily(WheelGlyphs.FontFamily), FontSize = Math.Max(fit.GlyphSize, 1),
            Foreground = foreground, HorizontalAlignment = HorizontalAlignment.Center, Height = fit.GlyphSize * 1.3,
            Visibility = fit.GlyphSize > 0 ? Visibility.Visible : Visibility.Collapsed,
        };
        var label = new TextBlock
        {
            FontFamily = LabelFace.FontFamily, FontSize = fit.FontSize, FontWeight = FontWeights.SemiBold, Foreground = foreground,
            TextAlignment = TextAlignment.Center, Margin = new Thickness(0, fit.GlyphSize > 0 ? GlyphGap : 0, 0, 0),
            LineHeight = fit.FontSize * LabelFace.FontFamily.LineSpacing, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        };
        for (var i = 0; i < fit.Lines.Length; i++)
        {
            if (i > 0)
            {
                label.Inlines.Add(new System.Windows.Documents.LineBreak());
            }
            label.Inlines.Add(new System.Windows.Documents.Run(fit.Lines[i]));
        }
        var stack = new StackPanel { IsHitTestVisible = false };
        stack.Children.Add(glyph);
        stack.Children.Add(label);
        if (!item.Available)
        {
            stack.Opacity = 0.6;
        }

        var radians = angle * Math.PI / 180;
        var labelCenter = new Point(center.X + fit.Radius * Math.Cos(radians), center.Y + fit.Radius * Math.Sin(radians));

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
        PlaceCentered(canvas, stack, labelCenter, fit.Width + 2, fit.Height + 2);
    }

    /// <summary>
    /// A ring sector whose straight edges stay <see cref="Gap"/> apart from the neighbours. The geometry is
    /// shrunk by <see cref="Corner"/> and drawn with a round-joined stroke of twice that, which rounds the corners.
    /// </summary>
    internal static Geometry SegmentGeometry(Point center, double angle, double span, int count, double outerRadius)
    {
        if (count == 1)
        {
            return new CombinedGeometry(GeometryCombineMode.Exclude,
                new EllipseGeometry(center, outerRadius, outerRadius),
                new EllipseGeometry(center, InnerRadius, InnerRadius));
        }
        var outer = outerRadius - Corner;
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
