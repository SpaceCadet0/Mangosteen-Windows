using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows.Media.Imaging;
using Mangosteen.Core;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Mangosteen.Conversion;

/// <summary>Renders PDF pages to images with the PDF engine built into Windows (Windows.Data.Pdf).</summary>
internal static class PdfRenderer
{
    /// <summary>Pages are rendered at this resolution, like the macOS version.</summary>
    public const double Dpi = 300;

    /// <summary>Very large pages (posters, drawings) are scaled down so a page stays a reasonable image.</summary>
    private const double MaxSide = 10000;

    public static int PageCount(string path) => (int)Load(path).PageCount;

    public static BitmapSource RenderPage(string path, int index) => Render(Load(path), index);

    /// <summary>Writes <c>name 1.png</c>, <c>name 2.png</c>... (numbers padded to the page count) into a new folder.</summary>
    public static void RenderPages(string path, FileFormat target, string folder, string baseName, CancellationToken token)
    {
        var document = Load(path);
        Directory.CreateDirectory(folder);
        var digits = document.PageCount.ToString().Length;
        for (var index = 0; index < document.PageCount; index++)
        {
            token.ThrowIfCancellationRequested();
            var image = Render(document, index);
            var file = Path.Combine(folder, $"{baseName} {(index + 1).ToString().PadLeft(digits, '0')}.{target.Extension()}");
            ImageConverter.WriteImage(image, target, file);
        }
    }

    private static PdfDocument Load(string path)
    {
        try
        {
            var file = StorageFile.GetFileFromPathAsync(Path.GetFullPath(path)).AsTask().GetAwaiter().GetResult();
            return PdfDocument.LoadFromFileAsync(file).AsTask().GetAwaiter().GetResult();
        }
        catch (Exception error) when (error.HResult == unchecked((int)0x8007052B)) // ERROR_WRONG_PASSWORD
        {
            throw new InvalidDataException($"{Path.GetFileName(path)} is protected with a password.", error);
        }
    }

    private static BitmapSource Render(PdfDocument document, int index)
    {
        using var page = document.GetPage((uint)index);
        // Page size is in device-independent pixels (1/96 inch), already rotated.
        var scale = Dpi / 96;
        var width = page.Size.Width * scale;
        var height = page.Size.Height * scale;
        var fit = Math.Min(1, MaxSide / Math.Max(width, height));
        var options = new PdfPageRenderOptions
        {
            DestinationWidth = (uint)Math.Max(1, Math.Round(width * fit)),
            DestinationHeight = (uint)Math.Max(1, Math.Round(height * fit)),
            BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255),
        };
        using var stream = new InMemoryRandomAccessStream();
        page.RenderToStreamAsync(stream, options).AsTask().GetAwaiter().GetResult();
        stream.Seek(0);
        using var managed = stream.AsStreamForRead();
        var frame = BitmapDecoder.Create(managed, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        // Keep the physical size: a 300 dpi render of an A4 page is still A4.
        var dpi = Dpi * fit;
        var pixels = new FormatConvertedBitmap(frame, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        var stride = pixels.PixelWidth * 4;
        var buffer = new byte[stride * pixels.PixelHeight];
        pixels.CopyPixels(buffer, stride, 0);
        var result = BitmapSource.Create(pixels.PixelWidth, pixels.PixelHeight, dpi, dpi, System.Windows.Media.PixelFormats.Bgra32, null, buffer, stride);
        result.Freeze();
        return result;
    }
}
