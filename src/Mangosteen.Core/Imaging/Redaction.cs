namespace Mangosteen.Core.Imaging;

public enum RedactionStyle
{
    Solid, Blur, Pixelate,
}

/// <summary>Covers regions of an image irreversibly: a fill, large pixels, or pixels blurred on top.</summary>
public static class Redaction
{
    public static void Apply(Bgra image, IEnumerable<UnitRect> regions, RedactionStyle style, uint color = 0xFF000000)
    {
        foreach (var region in regions)
        {
            var rect = region.ToPixels(image.Width, image.Height);
            switch (style)
            {
                case RedactionStyle.Solid:
                    Fill(image, rect, color);
                    break;
                case RedactionStyle.Pixelate:
                    Pixelate(image, rect, Math.Max(6, Math.Min(rect.Width, rect.Height) / 8));
                    break;
                default:
                    // Pixelating first makes the blur impossible to undo; the blur then hides the blocks.
                    var block = Math.Max(4, Math.Min(rect.Width, rect.Height) / 12);
                    Pixelate(image, rect, block);
                    Blur(image, rect, Math.Max(3, block));
                    break;
            }
        }
    }

    public static void Fill(Bgra image, PixelRect rect, uint argb)
    {
        byte a = (byte)(argb >> 24), r = (byte)(argb >> 16), g = (byte)(argb >> 8), b = (byte)argb;
        for (var y = rect.Y; y < rect.Bottom; y++)
        {
            for (var x = rect.X; x < rect.Right; x++)
            {
                var i = (y * image.Width + x) * 4;
                image.Pixels[i] = b;
                image.Pixels[i + 1] = g;
                image.Pixels[i + 2] = r;
                image.Pixels[i + 3] = Math.Max(a, image.Pixels[i + 3]);
            }
        }
    }

    public static void Pixelate(Bgra image, PixelRect rect, int block)
    {
        for (var by = rect.Y; by < rect.Bottom; by += block)
        {
            for (var bx = rect.X; bx < rect.Right; bx += block)
            {
                int right = Math.Min(bx + block, rect.Right), bottom = Math.Min(by + block, rect.Bottom);
                long sb = 0, sg = 0, sr = 0, sa = 0, n = 0;
                for (var y = by; y < bottom; y++)
                {
                    for (var x = bx; x < right; x++)
                    {
                        var i = (y * image.Width + x) * 4;
                        sb += image.Pixels[i];
                        sg += image.Pixels[i + 1];
                        sr += image.Pixels[i + 2];
                        sa += image.Pixels[i + 3];
                        n++;
                    }
                }
                for (var y = by; y < bottom; y++)
                {
                    for (var x = bx; x < right; x++)
                    {
                        var i = (y * image.Width + x) * 4;
                        image.Pixels[i] = (byte)(sb / n);
                        image.Pixels[i + 1] = (byte)(sg / n);
                        image.Pixels[i + 2] = (byte)(sr / n);
                        image.Pixels[i + 3] = (byte)(sa / n);
                    }
                }
            }
        }
    }

    public static void Blur(Bgra image, PixelRect rect, int radius)
    {
        int w = rect.Width, h = rect.Height;
        for (var c = 0; c < 3; c++)
        {
            var plane = new float[w * h];
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    plane[y * w + x] = image.Pixels[((rect.Y + y) * image.Width + rect.X + x) * 4 + c];
                }
            }
            var blurred = PhotoEditor.Blurred(plane, w, h, radius);
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    image.Pixels[((rect.Y + y) * image.Width + rect.X + x) * 4 + c] = (byte)Math.Clamp((int)MathF.Round(blurred[y * w + x]), 0, 255);
                }
            }
        }
    }
}
