using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageMagick;
using Mangosteen.Core;

namespace Mangosteen.Conversion;

/// <summary>
/// Pictures Windows cannot read or write by itself, through Magick.NET (ImageMagick with LibRaw, libjxl, OpenEXR and
/// libheif): PSD, TGA, OpenEXR, camera RAW, JPEG XL, DDS, DICOM and JPEG 2000; HEIC and AVIF too when the Microsoft
/// Store codecs are missing. Nothing here is touched until such a file is opened, so ImageMagick's native library is
/// loaded only then and the idle app stays small.
/// </summary>
internal static class MagickImaging
{
    /// <summary>Always true in this build; a constant, so asking does not load ImageMagick.</summary>
    public const bool IsBundled = true;

    private static MagickFormat Format(FileFormat format) => format switch
    {
        FileFormat.Psd => MagickFormat.Psd,
        FileFormat.Tga => MagickFormat.Tga,
        FileFormat.Exr => MagickFormat.Exr,
        _ => throw new NotSupportedException($"{format.DisplayName()} is not written through ImageMagick."),
    };

    /// <summary>The upright picture as 8-bit BGRA. Only the first frame (or layer composite) of a file is read.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static BitmapSource Decode(string path)
    {
        var settings = new MagickReadSettings { FrameIndex = 0, FrameCount = 1 };
        // A PSD's first image is the flattened composite; for camera RAW LibRaw develops the picture.
        using var image = new MagickImage(path, settings);
        image.AutoOrient();
        var width = (int)image.Width;
        var height = (int)image.Height;
        var bytes = image.ToByteArray(MagickFormat.Bgra);
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bytes, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Write(BitmapSource source, FileFormat format, string output)
    {
        var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var width = bgra.PixelWidth;
        var height = bgra.PixelHeight;
        var bytes = new byte[width * height * 4];
        bgra.CopyPixels(bytes, width * 4, 0);
        using var image = new MagickImage(bytes, new PixelReadSettings((uint)width, (uint)height, StorageType.Char, PixelMapping.BGRA));
        if (format == FileFormat.Tga)
        {
            image.Settings.Compression = CompressionMethod.RLE;
        }
        image.Write(output, Format(format));
    }

    /// <summary>Whether ImageMagick in this build reads <paramref name="extension"/>; for the tests.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool Reads(string extension) =>
        MagickNET.SupportedFormats.Any(f => f.SupportsReading && string.Equals(f.Format.ToString(), extension, StringComparison.OrdinalIgnoreCase));
}
