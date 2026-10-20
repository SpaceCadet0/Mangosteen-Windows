using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mangosteen.Core;
using SkiaSharp;

namespace Mangosteen.Conversion;

/// <summary>
/// Converts images with WIC (System.Windows.Media.Imaging): PNG, JPG, TIFF, BMP and GIF; WebP through SkiaSharp
/// (Windows has a WebP decoder but no encoder); PDF through PDFsharp.
/// </summary>
/// <remarks>Multi-frame GIF and TIFF inputs are converted from their first frame.</remarks>
internal static class ImageConverter
{
    public const int WebpQuality = 80;

    public static bool CanWrite(FileFormat format) => FormatCatalog.WicImageOutputs.Contains(format);

    /// <summary>Converts <paramref name="source"/> next to itself and returns the path of the new file.</summary>
    public static string Convert(string source, FileFormat target)
    {
        if (!CanWrite(target))
        {
            throw new NotSupportedException($"{target.DisplayName()} is not supported yet.");
        }
        var directory = Path.GetDirectoryName(Path.GetFullPath(source))!;
        return OutputNaming.WriteAtomically(directory, OutputNaming.BaseName(source), target.Extension(),
            output => Write(source, target, output));
    }

    /// <summary>Converts <paramref name="input"/> into a new file at <paramref name="output"/>.</summary>
    public static void Write(string input, FileFormat target, string output)
    {
        if (!CanWrite(target))
        {
            throw new NotSupportedException($"{target.DisplayName()} is not supported yet.");
        }
        var image = Decode(input);
        if (target == FileFormat.Pdf)
        {
            PdfWriter.ImagesToPdf([(image, FileFormats.Detect(input) == FileFormat.Jpg)], output);
            return;
        }
        WriteImage(image, target, output);
    }

    public static void WriteImage(BitmapSource image, FileFormat target, string output)
    {
        using var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        Encode(image, target, stream);
    }

    internal static BitmapSource Decode(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        BitmapSource image = frame;
        image = ApplyOrientation(image, Orientation(frame));
        image.Freeze();
        return image;
    }

    internal static void Encode(BitmapSource image, FileFormat target, Stream output)
    {
        if (target == FileFormat.Webp)
        {
            EncodeWebp(image, output);
            return;
        }
        BitmapEncoder encoder = target switch
        {
            FileFormat.Png => new PngBitmapEncoder(),
            FileFormat.Jpg => new JpegBitmapEncoder { QualityLevel = 92 },
            FileFormat.Tiff => new TiffBitmapEncoder { Compression = TiffCompressOption.Lzw },
            FileFormat.Bmp => new BmpBitmapEncoder(),
            FileFormat.Gif => new GifBitmapEncoder(),
            _ => throw new NotSupportedException($"{target.DisplayName()} is not supported yet."),
        };
        // JPG and BMP have no alpha, and GIF's one-bit transparency turns soft edges black, so these get a
        // white background like a browser would show.
        var frame = target is FileFormat.Jpg or FileFormat.Bmp or FileFormat.Gif ? FlattenOnWhite(image) : image;
        encoder.Frames.Add(BitmapFrame.Create(frame));
        encoder.Save(output);
    }

    /// <summary>Lossy WebP at quality 80 with the alpha channel kept, like cwebp on the Mac.</summary>
    private static void EncodeWebp(BitmapSource image, Stream output)
    {
        var (pixels, width, height) = Bgra(image);
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            using var skImage = SKImage.FromPixelCopy(info, handle.AddrOfPinnedObject(), width * 4)
                ?? throw new InvalidOperationException("Could not prepare the image for WebP.");
            using var data = skImage.Encode(SKEncodedImageFormat.Webp, WebpQuality)
                ?? throw new InvalidOperationException("Could not encode WebP.");
            data.SaveTo(output);
        }
        finally
        {
            handle.Free();
        }
    }

    private static (byte[] Pixels, int Width, int Height) Bgra(BitmapSource image)
    {
        var bgra = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[bgra.PixelWidth * bgra.PixelHeight * 4];
        bgra.CopyPixels(pixels, bgra.PixelWidth * 4, 0);
        return (pixels, bgra.PixelWidth, bgra.PixelHeight);
    }

    /// <summary>EXIF orientation (1-8), or 1 when the file has none.</summary>
    private static int Orientation(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is BitmapMetadata metadata &&
                metadata.GetQuery("System.Photo.Orientation") is ushort value and >= 1 and <= 8)
            {
                return value;
            }
        }
        catch (Exception error) when (error is NotSupportedException or InvalidOperationException or ArgumentException)
        {
        }
        return 1;
    }

    private static BitmapSource ApplyOrientation(BitmapSource image, int orientation)
    {
        if (orientation == 1)
        {
            return image;
        }
        Transform transform = orientation switch
        {
            2 => new ScaleTransform(-1, 1),
            3 => new RotateTransform(180),
            4 => new ScaleTransform(1, -1),
            5 => new TransformGroup { Children = { new ScaleTransform(-1, 1), new RotateTransform(270) } },
            6 => new RotateTransform(90),
            7 => new TransformGroup { Children = { new ScaleTransform(-1, 1), new RotateTransform(90) } },
            8 => new RotateTransform(270),
            _ => Transform.Identity,
        };
        return new TransformedBitmap(image, transform);
    }

    internal static BitmapSource FlattenOnWhite(BitmapSource image)
    {
        var (source, width, height) = Bgra(image);
        var stride = (width * 3 + 3) & ~3;
        var output = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var s = (y * width + x) * 4;
                var d = y * stride + x * 3;
                var alpha = source[s + 3];
                for (var c = 0; c < 3; c++)
                {
                    output[d + c] = (byte)((source[s + c] * alpha + 255 * (255 - alpha) + 127) / 255);
                }
            }
        }
        var dpiX = image.DpiX > 0 ? image.DpiX : 96;
        var dpiY = image.DpiY > 0 ? image.DpiY : 96;
        var result = BitmapSource.Create(width, height, dpiX, dpiY, PixelFormats.Bgr24, null, output, stride);
        result.Freeze();
        return result;
    }
}
