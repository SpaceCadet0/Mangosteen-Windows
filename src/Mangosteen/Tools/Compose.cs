using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Mangosteen.Core.Imaging;

namespace Mangosteen.Tools;

/// <summary>Draws Background and Collage results with WPF. Must run on an STA thread (the UI thread or <see cref="Sta"/>).</summary>
internal static class Compose
{
    /// <summary>The largest square from the middle of the image.</summary>
    private static BitmapSource CentredSquare(BitmapSource image)
    {
        var side = Math.Min(image.PixelWidth, image.PixelHeight);
        if (side == image.PixelWidth && side == image.PixelHeight)
        {
            return image;
        }
        var square = new CroppedBitmap(image, new Int32Rect((image.PixelWidth - side) / 2, (image.PixelHeight - side) / 2, side, side));
        square.Freeze();
        return square;
    }

    public static BitmapSource Frame(BitmapSource photo, FrameSettings settings, BitmapSource? backgroundImage)
    {
        if (settings.Circle)
        {
            photo = CentredSquare(photo);
        }
        double pw = photo.PixelWidth, ph = photo.PixelHeight;
        var (width, height) = Core.Imaging.Frame.CanvasSize(pw, ph, settings);
        var longest = Math.Max(width, height);
        var canvas = new Canvas { Width = width, Height = height, ClipToBounds = true };

        switch (settings.Background)
        {
            case FrameBackground.Color:
                canvas.Children.Add(Fill(new SolidColorBrush(Swatches.ToColor(settings.Color)), 0, 0, width, height));
                break;
            case FrameBackground.Gradient:
                canvas.Children.Add(Fill(Gradient(settings.GradientStart, settings.GradientEnd, settings.GradientAngle), 0, 0, width, height));
                break;
            default:
                var backdrop = settings.Background == FrameBackground.Blur ? photo : backgroundImage ?? photo;
                var radius = settings.Blur * longest * 0.04;
                // Drawn larger than the canvas so the blur does not fade at the edges.
                var layer = Fill(new ImageBrush(backdrop) { Stretch = Stretch.UniformToFill }, -radius, -radius, width + radius * 2, height + radius * 2);
                if (radius > 0.5)
                {
                    layer.Effect = new BlurEffect { Radius = radius, KernelType = KernelType.Gaussian };
                }
                canvas.Children.Add(layer);
                break;
        }

        var corner = (settings.Circle ? 0.5 : settings.CornerRadius) * Math.Min(pw, ph);
        var picture = new Rectangle { Width = pw, Height = ph, RadiusX = corner, RadiusY = corner, Fill = new ImageBrush(photo) };
        Canvas.SetLeft(picture, (width - pw) / 2);
        Canvas.SetTop(picture, (height - ph) / 2);
        if (settings.Shadow > 0)
        {
            var blur = settings.Shadow * longest * 0.035;
            picture.Effect = new DropShadowEffect
            {
                BlurRadius = blur, ShadowDepth = blur * 0.35, Direction = 270, Color = Colors.Black, Opacity = 0.25 + settings.Shadow * 0.3,
            };
        }
        canvas.Children.Add(picture);
        return Render(canvas, (int)width, (int)height);
    }

    public static BitmapSource Collage(IReadOnlyList<BitmapSource> photos, CollageSettings settings, uint background)
    {
        var aspects = photos.Select(p => (double)p.PixelWidth / p.PixelHeight).ToList();
        var (width, height, cells) = Core.Imaging.Collage.Layout(aspects, settings);
        var canvas = new Canvas { Width = width, Height = height, ClipToBounds = true };
        Brush fill = settings.Fill == CollageFill.Gradient
            ? Gradient(settings.GradientStart, settings.GradientEnd, settings.GradientAngle)
            : new SolidColorBrush(Swatches.ToColor(background));
        canvas.Children.Add(Fill(fill, 0, 0, width, height));
        Rectangle Cell(Box cell, Brush brush)
        {
            var corner = settings.CornerRadius * Math.Min(cell.Width, cell.Height);
            var shape = new Rectangle { Width = cell.Width, Height = cell.Height, RadiusX = corner, RadiusY = corner, Fill = brush };
            Canvas.SetLeft(shape, cell.X);
            Canvas.SetTop(shape, cell.Y);
            return shape;
        }
        // All shadows first, so no shadow falls on a neighbouring photo; the photos then cover the shapes.
        if (settings.Shadow > 0)
        {
            foreach (var cell in cells.Take(photos.Count))
            {
                var (blur, offset, opacity) = Core.Imaging.Collage.ShadowOf(cell, settings.Shadow);
                var shape = Cell(cell, Brushes.Black);
                shape.Effect = new DropShadowEffect { BlurRadius = blur, ShadowDepth = offset, Direction = 270, Color = Colors.Black, Opacity = opacity };
                canvas.Children.Add(shape);
            }
        }
        for (var i = 0; i < photos.Count; i++)
        {
            canvas.Children.Add(Cell(cells[i], new ImageBrush(photos[i]) { Stretch = Stretch.UniformToFill }));
        }
        return Render(canvas, (int)Math.Round(width), (int)Math.Round(height));
    }

    /// <summary>A linear gradient across the whole area; 0° runs left → right, 90° bottom → top.</summary>
    private static LinearGradientBrush Gradient(uint start, uint end, double angle)
    {
        var radians = angle * Math.PI / 180;
        double dx = Math.Cos(radians) / 2, dy = -Math.Sin(radians) / 2;
        return new LinearGradientBrush(Swatches.ToColor(start), Swatches.ToColor(end), new Point(0.5 - dx, 0.5 - dy), new Point(0.5 + dx, 0.5 + dy));
    }

    private static Rectangle Fill(Brush brush, double x, double y, double width, double height)
    {
        var rectangle = new Rectangle { Width = width, Height = height, Fill = brush };
        Canvas.SetLeft(rectangle, x);
        Canvas.SetTop(rectangle, y);
        return rectangle;
    }

    public static BitmapSource Render(FrameworkElement element, int width, int height)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap(Math.Max(1, width), Math.Max(1, height), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var result = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        result.Freeze();
        return result;
    }
}
