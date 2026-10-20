namespace Mangosteen.Core.Imaging;

public enum PhotoEffect
{
    None, Mono, Noir, Tonal, Chrome, Fade, Instant, Process, Transfer,
}

/// <summary>Photo adjustments, the same set as on the Mac. Every value at its default leaves the image unchanged.</summary>
public sealed record PhotoAdjustments
{
    public double Exposure { get; init; }       // EV, -2…2
    public double Brightness { get; init; }     // -1…1
    public double Contrast { get; init; }       // -1…1
    public double Highlights { get; init; }     // -1…1 (negative recovers highlights)
    public double Shadows { get; init; }        // -1…1 (positive lifts shadows)
    public double Temperature { get; init; }    // -1 cooler … 1 warmer
    public double Tint { get; init; }           // -1 green … 1 magenta
    public double Saturation { get; init; }     // -1…1
    public double Vibrance { get; init; }       // -1…1
    public double Sharpness { get; init; }      // 0…1
    public double Clarity { get; init; }        // 0…1
    public double NoiseReduction { get; init; } // 0…1
    public double Dehaze { get; init; }         // 0…1
    public PhotoEffect Effect { get; init; }
    public double Vignette { get; init; }       // 0…1
    public double Grain { get; init; }          // 0…1

    public static readonly PhotoAdjustments Identity = new();

    public bool IsIdentity => this == Identity;
}

/// <summary>Applies <see cref="PhotoAdjustments"/> to pixels. Runs on any thread.</summary>
public static class PhotoEditor
{
    /// <param name="scale">The image size relative to the full photo, so radius-based effects look the same in the preview.</param>
    public static Bgra Apply(Bgra source, PhotoAdjustments a, double scale = 1, CancellationToken token = default)
    {
        if (a.IsIdentity)
        {
            return source.Clone();
        }
        int width = source.Width, height = source.Height, count = width * height;
        var r = new float[count];
        var g = new float[count];
        var b = new float[count];
        var px = source.Pixels;
        for (var i = 0; i < count; i++)
        {
            b[i] = px[i * 4] / 255f;
            g[i] = px[i * 4 + 1] / 255f;
            r[i] = px[i * 4 + 2] / 255f;
        }

        var exposure = (float)Math.Pow(2, a.Exposure);
        var haze = (float)(a.Dehaze * 0.12);
        for (var i = 0; i < count; i++)
        {
            float cr = r[i] * exposure, cg = g[i] * exposure, cb = b[i] * exposure;
            var brightness = (float)(a.Brightness * 0.25);
            cr += brightness;
            cg += brightness;
            cb += brightness;

            // Shadows and highlights move the tones by luminance, keeping the colour.
            var luma = Luma(cr, cg, cb);
            if (a.Shadows != 0 || a.Highlights != 0)
            {
                var l = Math.Clamp(luma, 0, 1);
                var shadowWeight = (1 - l) * (1 - l);
                var highlightWeight = l * l;
                var target = l + (float)(a.Shadows * 0.45) * shadowWeight * l * 2 + (float)(a.Highlights * 0.35) * highlightWeight;
                var factor = l > 0.0001f ? target / l : 1;
                cr *= factor;
                cg *= factor;
                cb *= factor;
                if (l <= 0.0001f)
                {
                    cr = cg = cb = target;
                }
            }
            if (a.Contrast != 0)
            {
                var c = (float)(1 + a.Contrast * (a.Contrast > 0 ? 0.8 : 0.6));
                cr = (cr - 0.5f) * c + 0.5f;
                cg = (cg - 0.5f) * c + 0.5f;
                cb = (cb - 0.5f) * c + 0.5f;
            }
            if (haze > 0)
            {
                cr = (cr - haze) / (1 - haze);
                cg = (cg - haze) / (1 - haze);
                cb = (cb - haze) / (1 - haze);
            }
            if (a.Temperature != 0 || a.Tint != 0)
            {
                var t = (float)a.Temperature * 0.12f;
                var tint = (float)a.Tint * 0.08f;
                cr *= 1 + t;
                cb *= 1 - t;
                cg *= 1 - tint;
                cr *= 1 + tint * 0.5f;
                cb *= 1 + tint * 0.5f;
            }
            var saturation = (float)(1 + a.Saturation + a.Dehaze * 0.25);
            if (a.Vibrance != 0)
            {
                var spread = Math.Clamp(Math.Max(cr, Math.Max(cg, cb)) - Math.Min(cr, Math.Min(cg, cb)), 0, 1);
                saturation *= 1 + (float)a.Vibrance * (1 - spread);
            }
            if (saturation != 1)
            {
                luma = Luma(cr, cg, cb);
                cr = luma + (cr - luma) * saturation;
                cg = luma + (cg - luma) * saturation;
                cb = luma + (cb - luma) * saturation;
            }
            ApplyEffect(a.Effect, ref cr, ref cg, ref cb);
            r[i] = cr;
            g[i] = cg;
            b[i] = cb;
        }
        token.ThrowIfCancellationRequested();

        if (a.NoiseReduction > 0)
        {
            var radius = Math.Max(1, (int)Math.Round(2 * scale));
            Blend(r, Blurred(r, width, height, radius), (float)(a.NoiseReduction * 0.8));
            Blend(g, Blurred(g, width, height, radius), (float)(a.NoiseReduction * 0.8));
            Blend(b, Blurred(b, width, height, radius), (float)(a.NoiseReduction * 0.8));
        }
        if (a.Clarity > 0)
        {
            UnsharpMask(r, g, b, width, height, Math.Max(2, (int)Math.Round(24 * scale)), (float)(a.Clarity * 0.7));
        }
        token.ThrowIfCancellationRequested();
        if (a.Sharpness > 0)
        {
            UnsharpMask(r, g, b, width, height, Math.Max(1, (int)Math.Round(1.5 * scale)), (float)(a.Sharpness * 1.6));
        }

        var output = new Bgra(width, height);
        var o = output.Pixels;
        var vignette = (float)a.Vignette;
        var grain = (float)(a.Grain * 0.12);
        var grainCell = Math.Max(1, (int)Math.Round(1.5 * scale));
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = y * width + x;
                float cr = r[i], cg = g[i], cb = b[i];
                if (vignette > 0)
                {
                    var dx = (x + 0.5f) / width - 0.5f;
                    var dy = (y + 0.5f) / height - 0.5f;
                    var d = MathF.Sqrt(dx * dx + dy * dy) / 0.7071f;
                    var t = Math.Clamp((d - 0.35f) / 0.65f, 0, 1);
                    var factor = 1 - vignette * 0.85f * t * t * (3 - 2 * t);
                    cr *= factor;
                    cg *= factor;
                    cb *= factor;
                }
                if (grain > 0)
                {
                    var noise = (Hash(x / grainCell, y / grainCell) - 0.5f) * 2 * grain;
                    cr += noise;
                    cg += noise;
                    cb += noise;
                }
                o[i * 4] = ToByte(cb);
                o[i * 4 + 1] = ToByte(cg);
                o[i * 4 + 2] = ToByte(cr);
                o[i * 4 + 3] = px[i * 4 + 3];
            }
        }
        return output;
    }

    private static float Luma(float r, float g, float b) => 0.2126f * r + 0.7152f * g + 0.0722f * b;

    private static byte ToByte(float value) => (byte)Math.Clamp((int)MathF.Round(value * 255), 0, 255);

    /// <summary>Approximations of the Core Image photo effects used on the Mac.</summary>
    private static void ApplyEffect(PhotoEffect effect, ref float r, ref float g, ref float b)
    {
        if (effect == PhotoEffect.None)
        {
            return;
        }
        var l = Luma(r, g, b);
        switch (effect)
        {
            case PhotoEffect.Mono:
                r = g = b = l;
                break;
            case PhotoEffect.Noir:
                l = Math.Clamp((l - 0.5f) * 1.45f + 0.47f, 0, 1);
                r = g = b = l;
                break;
            case PhotoEffect.Tonal:
                l = l * 0.9f + 0.05f;
                r = g = b = l;
                break;
            case PhotoEffect.Chrome:
                r = (l + (r - l) * 1.25f - 0.5f) * 1.1f + 0.5f;
                g = (l + (g - l) * 1.25f - 0.5f) * 1.1f + 0.5f;
                b = (l + (b - l) * 1.25f - 0.5f) * 1.1f + 0.5f;
                break;
            case PhotoEffect.Fade:
                r = (l + (r - l) * 0.7f) * 0.85f + 0.1f;
                g = (l + (g - l) * 0.7f) * 0.85f + 0.1f;
                b = (l + (b - l) * 0.7f) * 0.85f + 0.12f;
                break;
            case PhotoEffect.Instant:
                r = (l + (r - l) * 0.85f) * 0.9f + 0.09f;
                g = (l + (g - l) * 0.85f) * 0.88f + 0.07f;
                b = (l + (b - l) * 0.85f) * 0.8f + 0.06f;
                break;
            case PhotoEffect.Process:
                r = r * 0.92f + 0.04f;
                g = g * 1.0f + 0.02f * (1 - l);
                b = b * 0.88f + 0.1f * (1 - l);
                break;
            case PhotoEffect.Transfer:
                r = r * 1.05f + 0.04f;
                g = g * 0.97f + 0.02f;
                b = b * 0.82f + 0.03f;
                break;
        }
    }

    private static float Hash(int x, int y)
    {
        unchecked
        {
            var h = (uint)(x * 374761393 + y * 668265263);
            h = (h ^ (h >> 13)) * 1274126177;
            return (h ^ (h >> 16)) / (float)uint.MaxValue;
        }
    }

    private static void Blend(float[] target, float[] other, float amount)
    {
        for (var i = 0; i < target.Length; i++)
        {
            target[i] += (other[i] - target[i]) * amount;
        }
    }

    private static void UnsharpMask(float[] r, float[] g, float[] b, int width, int height, int radius, float amount)
    {
        foreach (var channel in new[] { r, g, b })
        {
            var blurred = Blurred(channel, width, height, radius);
            for (var i = 0; i < channel.Length; i++)
            {
                channel[i] += (channel[i] - blurred[i]) * amount;
            }
        }
    }

    /// <summary>Three box blurs, which approximate a Gaussian of about that radius.</summary>
    public static float[] Blurred(float[] source, int width, int height, int radius)
    {
        var a = (float[])source.Clone();
        var temp = new float[a.Length];
        for (var pass = 0; pass < 3; pass++)
        {
            BoxHorizontal(a, temp, width, height, radius);
            BoxVertical(temp, a, width, height, radius);
        }
        return a;
    }

    private static void BoxHorizontal(float[] source, float[] target, int width, int height, int radius)
    {
        var size = radius * 2 + 1;
        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            float sum = 0;
            for (var x = -radius; x <= radius; x++)
            {
                sum += source[row + Math.Clamp(x, 0, width - 1)];
            }
            for (var x = 0; x < width; x++)
            {
                target[row + x] = sum / size;
                sum += source[row + Math.Min(x + radius + 1, width - 1)] - source[row + Math.Max(x - radius, 0)];
            }
        }
    }

    private static void BoxVertical(float[] source, float[] target, int width, int height, int radius)
    {
        var size = radius * 2 + 1;
        for (var x = 0; x < width; x++)
        {
            float sum = 0;
            for (var y = -radius; y <= radius; y++)
            {
                sum += source[Math.Clamp(y, 0, height - 1) * width + x];
            }
            for (var y = 0; y < height; y++)
            {
                target[y * width + x] = sum / size;
                sum += source[Math.Min(y + radius + 1, height - 1) * width + x] - source[Math.Max(y - radius, 0) * width + x];
            }
        }
    }
}
