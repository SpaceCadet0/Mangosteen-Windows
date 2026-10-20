namespace Mangosteen.Core.Imaging;

/// <summary>An image as 8-bit BGRA pixels with straight (not premultiplied) alpha, rows packed without padding.</summary>
public sealed class Bgra
{
    public Bgra(int width, int height, byte[]? pixels = null)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentException("An image needs a positive size.");
        }
        Width = width;
        Height = height;
        Pixels = pixels ?? new byte[width * height * 4];
        if (Pixels.Length != width * height * 4)
        {
            throw new ArgumentException("The pixel buffer does not match the size.");
        }
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }
    public int Stride => Width * 4;

    public Bgra Clone() => new(Width, Height, (byte[])Pixels.Clone());

    public static Bgra Filled(int width, int height, byte b, byte g, byte r, byte a = 255)
    {
        var image = new Bgra(width, height);
        for (var i = 0; i < image.Pixels.Length; i += 4)
        {
            image.Pixels[i] = b;
            image.Pixels[i + 1] = g;
            image.Pixels[i + 2] = r;
            image.Pixels[i + 3] = a;
        }
        return image;
    }
}

/// <summary>A rectangle in normalised coordinates: 0…1, origin at the top-left of the upright image.</summary>
public readonly record struct UnitRect(double X, double Y, double Width, double Height)
{
    public static readonly UnitRect Full = new(0, 0, 1, 1);

    public double Right => X + Width;
    public double Bottom => Y + Height;

    /// <summary>The rectangle in whole pixels, clamped to the image and never empty.</summary>
    public PixelRect ToPixels(int imageWidth, int imageHeight)
    {
        var left = Math.Clamp((int)Math.Round(X * imageWidth), 0, imageWidth - 1);
        var top = Math.Clamp((int)Math.Round(Y * imageHeight), 0, imageHeight - 1);
        var right = Math.Clamp((int)Math.Round(Right * imageWidth), left + 1, imageWidth);
        var bottom = Math.Clamp((int)Math.Round(Bottom * imageHeight), top + 1, imageHeight);
        return new PixelRect(left, top, right - left, bottom - top);
    }

    /// <summary>Grows the rectangle by a fraction of its size on every side, staying inside the image.</summary>
    public UnitRect Inflate(double fraction)
    {
        var dx = Width * fraction;
        var dy = Height * fraction;
        var x = Math.Max(0, X - dx);
        var y = Math.Max(0, Y - dy);
        return new UnitRect(x, y, Math.Min(1, Right + dx) - x, Math.Min(1, Bottom + dy) - y);
    }
}

public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
}
