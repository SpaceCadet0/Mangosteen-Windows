namespace Mangosteen.Core.Imaging;

/// <summary>
/// The subject mask from the background-removal model: one opacity byte per pixel (255 = subject), row by row.
/// </summary>
public static class SubjectMask
{
    /// <summary>The model's input side and the per-channel means it was trained with (inputs are 0…1, std 1).</summary>
    public const int Side = 1024;
    private static readonly float[] Mean = [0.485f, 0.456f, 0.406f];

    /// <summary>
    /// The model input from a <see cref="Side"/> × <see cref="Side"/> BGRA image: planar RGB, 0…1, minus the means.
    /// </summary>
    public static float[] Input(Bgra image)
    {
        if (image.Width != Side || image.Height != Side)
        {
            throw new ArgumentException($"The model takes {Side} × {Side} pixels.", nameof(image));
        }
        var plane = Side * Side;
        var input = new float[3 * plane];
        for (var i = 0; i < plane; i++)
        {
            var p = i * 4;
            input[i] = image.Pixels[p + 2] / 255f - Mean[0];
            input[plane + i] = image.Pixels[p + 1] / 255f - Mean[1];
            input[2 * plane + i] = image.Pixels[p] / 255f - Mean[2];
        }
        return input;
    }

    /// <summary>
    /// The model output (0…1) as 0…255, stretched so its lowest value becomes 0 and its highest 255. A photo without a
    /// subject stays below one half everywhere; it is not stretched, or noise would become a subject.
    /// </summary>
    public static byte[] Normalize(ReadOnlySpan<float> output)
    {
        float low = float.MaxValue, high = float.MinValue;
        foreach (var value in output)
        {
            low = Math.Min(low, value);
            high = Math.Max(high, value);
        }
        if (high < 0.5f)
        {
            (low, high) = (0, 1);
        }
        var range = Math.Max(high - low, 1e-6f);
        var mask = new byte[output.Length];
        for (var i = 0; i < output.Length; i++)
        {
            mask[i] = (byte)Math.Clamp((int)MathF.Round((output[i] - low) / range * 255), 0, 255);
        }
        return mask;
    }

    /// <summary>The share of pixels that are mostly subject.</summary>
    public static double Coverage(byte[] mask) => mask.Length == 0 ? 0 : (double)mask.Count(a => a >= 128) / mask.Length;

    /// <summary>True when the model found something to keep: at least a few pixels of solid subject.</summary>
    public static bool HasSubject(byte[] mask) => Coverage(mask) >= 0.002;

    /// <summary>The smallest rectangle around the subject (pixels at least half opaque), or null when there is none.</summary>
    public static PixelRect? Bounds(byte[] mask, int width, int height)
    {
        int left = width, top = height, right = -1, bottom = -1;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (mask[y * width + x] >= 128)
                {
                    left = Math.Min(left, x);
                    right = Math.Max(right, x);
                    top = Math.Min(top, y);
                    bottom = Math.Max(bottom, y);
                }
            }
        }
        return right < 0 ? null : new PixelRect(left, top, right - left + 1, bottom - top + 1);
    }

    /// <summary>The image with the mask as its opacity (the mask has the image's size).</summary>
    public static Bgra Cut(Bgra image, byte[] mask)
    {
        var result = new Bgra(image.Width, image.Height);
        Array.Copy(image.Pixels, result.Pixels, image.Pixels.Length);
        for (var i = 0; i < mask.Length; i++)
        {
            result.Pixels[i * 4 + 3] = (byte)(image.Pixels[i * 4 + 3] * mask[i] / 255);
        }
        return result;
    }

    /// <summary>The cut-out over a solid colour (0xAARRGGBB), fully opaque.</summary>
    public static Bgra OnColor(Bgra cut, uint argb)
    {
        var (r, g, b) = ((argb >> 16) & 0xFF, (argb >> 8) & 0xFF, argb & 0xFF);
        var result = new Bgra(cut.Width, cut.Height);
        for (var i = 0; i < cut.Pixels.Length; i += 4)
        {
            var a = cut.Pixels[i + 3];
            result.Pixels[i] = (byte)((cut.Pixels[i] * a + b * (255 - a) + 127) / 255);
            result.Pixels[i + 1] = (byte)((cut.Pixels[i + 1] * a + g * (255 - a) + 127) / 255);
            result.Pixels[i + 2] = (byte)((cut.Pixels[i + 2] * a + r * (255 - a) + 127) / 255);
            result.Pixels[i + 3] = 255;
        }
        return result;
    }
}
