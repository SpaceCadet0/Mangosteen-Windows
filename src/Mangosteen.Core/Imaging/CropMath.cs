namespace Mangosteen.Core.Imaging;

/// <summary>Crop presets and the geometry of the crop frame, all in normalised coordinates.</summary>
public static class CropMath
{
    public sealed record AspectChoice(string Title, double? Ratio, bool Original = false);

    /// <summary>Free, the image's own proportions, and the usual ratios (width / height).</summary>
    public static readonly IReadOnlyList<AspectChoice> Aspects =
    [
        new("Free", null), new("Original", null, Original: true),
        new("1:1", 1), new("4:5", 4.0 / 5), new("5:4", 5.0 / 4), new("4:3", 4.0 / 3), new("3:4", 3.0 / 4),
        new("3:2", 3.0 / 2), new("2:3", 2.0 / 3), new("16:9", 16.0 / 9), new("9:16", 9.0 / 16),
    ];

    public const double MinimumSize = 0.02;

    /// <summary>The largest rectangle of <paramref name="aspect"/> (pixel width / height) centred in the image.</summary>
    public static UnitRect CenteredRect(double aspect, int imageWidth, int imageHeight)
    {
        var imageAspect = (double)imageWidth / imageHeight;
        if (aspect > imageAspect)
        {
            var height = imageAspect / aspect;
            return new UnitRect(0, (1 - height) / 2, 1, height);
        }
        var width = aspect / imageAspect;
        return new UnitRect((1 - width) / 2, 0, width, 1);
    }

    [Flags]
    public enum Edges
    {
        None = 0,
        Left = 1,
        Top = 2,
        Right = 4,
        Bottom = 8,
        TopLeft = Top | Left,
        TopRight = Top | Right,
        BottomLeft = Bottom | Left,
        BottomRight = Bottom | Right,
    }

    /// <summary>Moves the whole frame by (dx, dy), keeping it inside the image.</summary>
    public static UnitRect Move(UnitRect rect, double dx, double dy) => rect with
    {
        X = Math.Clamp(rect.X + dx, 0, 1 - rect.Width),
        Y = Math.Clamp(rect.Y + dy, 0, 1 - rect.Height),
    };

    /// <summary>
    /// Drags the given edges by (dx, dy). With <paramref name="unitAspect"/> (normalised width / height) the frame
    /// keeps its proportions. Returns null when the result would leave the image or become too small.
    /// </summary>
    public static UnitRect? Resize(UnitRect rect, Edges edges, double dx, double dy, double? unitAspect)
    {
        double left = rect.X, top = rect.Y, right = rect.Right, bottom = rect.Bottom;
        if (edges.HasFlag(Edges.Left)) left += dx;
        if (edges.HasFlag(Edges.Right)) right += dx;
        if (edges.HasFlag(Edges.Top)) top += dy;
        if (edges.HasFlag(Edges.Bottom)) bottom += dy;

        if (unitAspect is { } aspect)
        {
            var horizontal = edges.HasFlag(Edges.Left) || edges.HasFlag(Edges.Right);
            var vertical = edges.HasFlag(Edges.Top) || edges.HasFlag(Edges.Bottom);
            var width = right - left;
            var height = bottom - top;
            if (horizontal && (!vertical || Math.Abs(dx) >= Math.Abs(dy)))
            {
                height = width / aspect;
            }
            else
            {
                width = height * aspect;
            }
            // Grow from the opposite edge (or from the centre when only one axis is dragged).
            if (edges.HasFlag(Edges.Left)) left = right - width;
            else if (edges.HasFlag(Edges.Right)) right = left + width;
            else
            {
                var center = (rect.X + rect.Right) / 2;
                left = center - width / 2;
                right = center + width / 2;
            }
            if (edges.HasFlag(Edges.Top)) top = bottom - height;
            else if (edges.HasFlag(Edges.Bottom)) bottom = top + height;
            else
            {
                var middle = (rect.Y + rect.Bottom) / 2;
                top = middle - height / 2;
                bottom = middle + height / 2;
            }
        }

        const double tolerance = 0.0001;
        if (left < -tolerance || top < -tolerance || right > 1 + tolerance || bottom > 1 + tolerance
            || right - left < MinimumSize || bottom - top < MinimumSize)
        {
            return null;
        }
        left = Math.Max(0, left);
        top = Math.Max(0, top);
        return new UnitRect(left, top, Math.Min(1, right) - left, Math.Min(1, bottom) - top);
    }

    /// <summary>Copies a pixel rectangle out of an image.</summary>
    public static Bgra Crop(Bgra image, PixelRect rect)
    {
        var result = new Bgra(rect.Width, rect.Height);
        for (var y = 0; y < rect.Height; y++)
        {
            Buffer.BlockCopy(image.Pixels, ((rect.Y + y) * image.Width + rect.X) * 4, result.Pixels, y * result.Stride, result.Stride);
        }
        return result;
    }
}
