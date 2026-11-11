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

    /// <summary>Formats this converter writes: the image targets, and BMP for edited BMP files.</summary>
    public static bool CanWrite(FileFormat format) => FormatCatalog.WicImageOutputs.Contains(format) || FormatCatalog.EditableImageFormats.Contains(format);

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
        // The settings in use (the standard ones or a preset): quality, the longest side and camera data.
        var options = OutputLocation.Options;
        var image = Downscale(Decode(input), options.MaxPixelSize);
        if (target == FileFormat.Pdf)
        {
            PdfWriter.ImagesToPdf([(image, FileFormats.Detect(input) == FileFormat.Jpg)], output);
            return;
        }
        if (target == FileFormat.Ico)
        {
            File.WriteAllBytes(output, EncodeIco(image));
            return;
        }
        using var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        Encode(image, target, stream, options.Quality, options.KeepMetadata ? input : null);
    }

    public static void WriteImage(BitmapSource image, FileFormat target, string output)
    {
        if (target == FileFormat.Ico)
        {
            File.WriteAllBytes(output, EncodeIco(image));
            return;
        }
        using var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        Encode(image, target, stream);
    }

    /// <summary>The picture with its longer side at most <paramref name="maxSide"/> pixels (0 keeps it).</summary>
    internal static BitmapSource Downscale(BitmapSource image, int maxSide)
    {
        var longSide = Math.Max(image.PixelWidth, image.PixelHeight);
        if (maxSide <= 0 || longSide <= maxSide)
        {
            return image;
        }
        var scale = (double)maxSide / longSide;
        using var source = ToSkia(image);
        var size = new SKImageInfo(Math.Max(1, (int)Math.Round(source.Width * scale)), Math.Max(1, (int)Math.Round(source.Height * scale)),
            SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var scaled = source.Resize(size, new SKSamplingOptions(SKCubicResampler.Mitchell)) ?? throw new InvalidOperationException("Could not resize the image.");
        var result = BitmapSource.Create(size.Width, size.Height, 96, 96, PixelFormats.Bgra32, null, scaled.Bytes, scaled.RowBytes);
        result.Freeze();
        return result;
    }

    private static SKBitmap ToSkia(BitmapSource image)
    {
        var (pixels, width, height) = Bgra(image);
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
        return bitmap;
    }

    /// <summary>
    /// A Windows icon: the picture centred on a transparent square at each size up to its own (16–256 px, at most six),
    /// every size a PNG.
    /// </summary>
    internal static byte[] EncodeIco(BitmapSource image)
    {
        using var source = ToSkia(image);
        using var sourceImage = SKImage.FromBitmap(source);
        var frames = new List<(int, byte[])>();
        foreach (var size in Core.Imaging.IcoFile.Sizes(Math.Max(source.Width, source.Height)))
        {
            var (x, y, w, h) = Core.Imaging.IcoFile.Fit(source.Width, source.Height, size);
            using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul));
            surface.Canvas.Clear(SKColors.Transparent);
            surface.Canvas.DrawImage(sourceImage, SKRect.Create(x, y, w, h), new SKSamplingOptions(SKCubicResampler.Mitchell));
            using var snapshot = surface.Snapshot();
            using var png = snapshot.Encode(SKEncodedImageFormat.Png, 100);
            frames.Add((size, png.ToArray()));
        }
        return Core.Imaging.IcoFile.Write(frames);
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

    /// <param name="quality">1…100 for JPG and WebP; null keeps their usual quality.</param>
    /// <param name="metadataFrom">A JPEG or TIFF whose camera data (EXIF, GPS) goes into a JPEG or TIFF result.</param>
    internal static void Encode(BitmapSource image, FileFormat target, Stream output, int? quality = null, string? metadataFrom = null)
    {
        if (target == FileFormat.Webp)
        {
            EncodeWebp(image, output, quality ?? WebpQuality);
            return;
        }
        BitmapEncoder encoder = target switch
        {
            FileFormat.Png => new PngBitmapEncoder(),
            FileFormat.Jpg => new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality ?? 92, 1, 100) },
            FileFormat.Tiff => new TiffBitmapEncoder { Compression = TiffCompressOption.Lzw },
            FileFormat.Bmp => new BmpBitmapEncoder(),
            FileFormat.Gif => new GifBitmapEncoder(),
            _ => throw new NotSupportedException($"{target.DisplayName()} is not supported yet."),
        };
        // JPG and BMP have no alpha, and GIF's one-bit transparency turns soft edges black, so these get a
        // white background like a browser would show.
        var frame = target is FileFormat.Jpg or FileFormat.Bmp or FileFormat.Gif ? FlattenOnWhite(image) : image;
        if (metadataFrom is not null && target is FileFormat.Jpg or FileFormat.Tiff && CameraData(metadataFrom, target) is { } metadata)
        {
            try
            {
                encoder.Frames.Add(BitmapFrame.Create(frame, null, metadata, null));
                encoder.Save(output);
                return;
            }
            catch (Exception error) when (error is InvalidOperationException or NotSupportedException or ArgumentException or System.Runtime.InteropServices.COMException)
            {
                // The data does not fit this file; save without it.
                output.SetLength(0);
                encoder = target == FileFormat.Jpg ? new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality ?? 92, 1, 100) } : new TiffBitmapEncoder { Compression = TiffCompressOption.Lzw };
            }
        }
        encoder.Frames.Add(BitmapFrame.Create(frame));
        encoder.Save(output);
    }

    /// <summary>
    /// The camera data of a JPEG or TIFF, ready for a file of <paramref name="target"/>; null when there is none or it
    /// cannot move. The orientation is reset, since the pixels are already upright.
    /// </summary>
    private static BitmapMetadata? CameraData(string path, FileFormat target)
    {
        var source = FileFormats.Detect(path);
        if (source is not (FileFormat.Jpg or FileFormat.Tiff) || source != target && target != FileFormat.Jpg)
        {
            return null;
        }
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames[0].Metadata is not BitmapMetadata metadata || source != target)
            {
                return null;
            }
            var copy = metadata.Clone();
            if (copy.ContainsQuery("System.Photo.Orientation"))
            {
                copy.SetQuery("System.Photo.Orientation", (ushort)1);
            }
            return copy;
        }
        catch (Exception error) when (error is IOException or NotSupportedException or InvalidOperationException or ArgumentException
            or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    /// <summary>Lossy WebP (quality 80 by default) with the alpha channel kept, like cwebp on the Mac.</summary>
    private static void EncodeWebp(BitmapSource image, Stream output, int quality = WebpQuality)
    {
        var (pixels, width, height) = Bgra(image);
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            using var skImage = SKImage.FromPixelCopy(info, handle.AddrOfPinnedObject(), width * 4)
                ?? throw new InvalidOperationException("Could not prepare the image for WebP.");
            using var data = skImage.Encode(SKEncodedImageFormat.Webp, Math.Clamp(quality, 1, 100))
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
