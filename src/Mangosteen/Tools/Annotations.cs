using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;

namespace Mangosteen.Tools;

internal enum AnnotationKind
{
    Arrow, Rectangle, Ellipse, Text, Number, Pen, Highlight, Magnifier, Picture,
}

/// <summary>One mark on a photo, in pixels of the full-size upright image (origin top-left).</summary>
internal sealed record Annotation(AnnotationKind Kind, IReadOnlyList<Point> Points, uint Color, double LineWidth, double FontSize)
{
    public string Text { get; init; } = "";
    public int Number { get; init; }
    public BitmapSource? Picture { get; init; }

    public Rect Bounds => Points.Count == 0 ? Rect.Empty : Points.Skip(1).Aggregate(new Rect(Points[0], Points[0]), (r, p) => Rect.Union(r, p));

    public Annotation Translated(Vector delta) => this with { Points = Points.Select(p => p + delta).ToList() };
}

/// <summary>Draws annotations as WPF elements; the same code draws the preview and the full-size result.</summary>
internal static class AnnotationDrawing
{
    public static UIElement Build(Annotation a, BitmapSource photo)
    {
        var color = Swatches.ToColor(a.Color);
        var brush = new SolidColorBrush(color);
        switch (a.Kind)
        {
            case AnnotationKind.Arrow:
            {
                var (start, end) = (a.Points[0], a.Points[^1]);
                var direction = end - start;
                var length = Math.Max(direction.Length, 0.001);
                direction /= length;
                var head = Math.Min(a.LineWidth * 4.5, length * 0.6);
                var normal = new Vector(-direction.Y, direction.X);
                var baseCenter = end - direction * head;
                var shaftEnd = end - direction * head * 0.8;
                var figure = new PathGeometry();
                figure.Figures.Add(new PathFigure(start, [new LineSegment(shaftEnd, true)], false));
                var arrowHead = new PathFigure(end, [new LineSegment(baseCenter + normal * head * 0.55, true), new LineSegment(baseCenter - normal * head * 0.55, true)], true)
                {
                    IsFilled = true,
                };
                figure.Figures.Add(arrowHead);
                return new Path
                {
                    Data = figure, Stroke = brush, Fill = brush, StrokeThickness = a.LineWidth, StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Effect = Shadow(a.LineWidth),
                };
            }
            case AnnotationKind.Rectangle:
            case AnnotationKind.Ellipse:
            {
                var rect = a.Bounds;
                Shape shape = a.Kind == AnnotationKind.Rectangle
                    ? new Rectangle { RadiusX = a.LineWidth, RadiusY = a.LineWidth }
                    : new Ellipse();
                shape.Width = Math.Max(rect.Width, 1);
                shape.Height = Math.Max(rect.Height, 1);
                shape.Stroke = brush;
                shape.StrokeThickness = a.LineWidth;
                shape.Effect = Shadow(a.LineWidth);
                return Place(shape, rect.X, rect.Y);
            }
            case AnnotationKind.Text:
                return Place(new TextBlock
                {
                    Text = a.Text, FontSize = a.FontSize, Foreground = brush, FontWeight = FontWeights.SemiBold,
                    FontFamily = new FontFamily("Segoe UI"), Effect = Shadow(a.FontSize / 10),
                }, a.Points[0].X, a.Points[0].Y);
            case AnnotationKind.Number:
            {
                var diameter = a.FontSize * 1.6;
                var badge = new Grid { Width = diameter, Height = diameter, Effect = Shadow(a.FontSize / 10) };
                badge.Children.Add(new Ellipse { Fill = brush, Stroke = Brushes.White, StrokeThickness = Math.Max(1, a.FontSize / 12) });
                badge.Children.Add(new TextBlock
                {
                    Text = a.Number.ToString(), Foreground = Brushes.White, FontSize = a.FontSize, FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                });
                return Place(badge, a.Points[0].X - diameter / 2, a.Points[0].Y - diameter / 2);
            }
            case AnnotationKind.Pen:
            case AnnotationKind.Highlight:
            {
                var line = new Polyline
                {
                    Points = new PointCollection(a.Points), Stroke = brush, StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                };
                if (a.Kind == AnnotationKind.Highlight)
                {
                    line.StrokeThickness = a.LineWidth * 5;
                    line.Opacity = 0.4;
                    line.StrokeStartLineCap = line.StrokeEndLineCap = PenLineCap.Square;
                }
                else
                {
                    line.StrokeThickness = a.LineWidth;
                }
                return line;
            }
            case AnnotationKind.Magnifier:
            {
                // Shows the area around the centre at 2× zoom.
                var center = a.Points[0];
                var radius = Math.Max(4, (a.Points[^1] - center).Length);
                var lens = new Ellipse
                {
                    Width = radius * 2, Height = radius * 2, Stroke = Brushes.White, StrokeThickness = Math.Max(2, radius / 25),
                    Fill = new ImageBrush(photo)
                    {
                        ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(center.X - radius / 2, center.Y - radius / 2, radius, radius),
                    },
                    Effect = new DropShadowEffect { BlurRadius = radius / 6, ShadowDepth = radius / 20, Opacity = 0.5, Direction = 270 },
                };
                return Place(lens, center.X - radius, center.Y - radius);
            }
            default:
            {
                var rect = a.Bounds;
                return Place(new Image { Source = a.Picture, Width = Math.Max(rect.Width, 1), Height = Math.Max(rect.Height, 1), Stretch = Stretch.Fill }, rect.X, rect.Y);
            }
        }
    }

    private static Effect Shadow(double size) => new DropShadowEffect { BlurRadius = Math.Max(2, size * 1.5), ShadowDepth = Math.Max(1, size / 3), Opacity = 0.35, Direction = 270 };

    private static UIElement Place(FrameworkElement element, double x, double y)
    {
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
        return element;
    }

    /// <summary>The photo with its annotations, at full size. Runs on an STA thread.</summary>
    public static BitmapSource Render(BitmapSource photo, IReadOnlyList<Annotation> annotations)
    {
        var canvas = new Canvas { Width = photo.PixelWidth, Height = photo.PixelHeight, ClipToBounds = true };
        canvas.Children.Add(new Image { Source = photo, Width = photo.PixelWidth, Height = photo.PixelHeight, Stretch = Stretch.Fill });
        foreach (var annotation in annotations)
        {
            canvas.Children.Add(Build(annotation, photo));
        }
        return Compose.Render(canvas, photo.PixelWidth, photo.PixelHeight);
    }
}
