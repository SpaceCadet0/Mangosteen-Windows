using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mangosteen.Conversion;
using Mangosteen.Core;
using Mangosteen.Core.Imaging;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace Mangosteen.Tools;

/// <summary>Image and PDF compression for the Compress tool. Never writes a file that is not smaller.</summary>
internal static class Compression
{
    /// <summary>The format results are written in.</summary>
    public static FileFormat OutputFormat(string input, CompressionSettings settings) =>
        settings.Format ?? (FileFormats.Detect(input) is { } source && CompressionSettings.IsLossy(source) && FormatCatalog.ImageOutputs.Contains(source)
            ? source : FileFormat.Webp);

    /// <summary>Compresses one image in memory.</summary>
    public static (byte[] Data, FileFormat Format) CompressImage(string input, CompressionSettings settings, CancellationToken token = default)
    {
        var format = OutputFormat(input, settings);
        var image = ImageIO.Load(input, settings.EffectiveMaxPixelSize);
        var metadata = settings.PreserveMetadata && format == FileFormat.Jpg ? ImageIO.ReadMetadata(input) : null;
        switch (settings.Mode)
        {
            case CompressionMode.Preset:
                return (ImageIO.Encode(image, format, settings.PresetQuality, metadata), format);
            case CompressionMode.Quality:
                return (ImageIO.Encode(image, format, settings.Quality, metadata), format);
        }
        // Target size: the highest quality that fits; if even the lowest is too big, shrink the pixels and retry.
        for (var attempt = 0; attempt < 6; attempt++)
        {
            token.ThrowIfCancellationRequested();
            var current = image;
            var lossy = CompressionSettings.IsLossy(format);
            var fit = lossy
                ? CompressionSearch.BestFit(q => ImageIO.Encode(current, format, q, metadata), settings.TargetBytes)
                : ImageIO.Encode(current, format, 1, metadata) is var whole && whole.Length <= settings.TargetBytes ? whole : null;
            if (fit is not null)
            {
                return (fit, format);
            }
            var smallest = ImageIO.Encode(current, format, 0.05, metadata);
            var (width, height) = CompressionSearch.ShrinkToFit(current.PixelWidth, current.PixelHeight, smallest.Length, settings.TargetBytes);
            image = ImageIO.Resize(current, width, height);
        }
        return (ImageIO.Encode(image, format, 0.05, metadata), format);
    }

    public static string CompressFile(string input, CompressionSettings settings, CancellationToken token)
    {
        var original = new FileInfo(input).Length;
        if (FileFormats.Detect(input) == FileFormat.Pdf)
        {
            var pdf = CompressPdf(input, settings, token);
            if (pdf.Length >= original)
            {
                throw new InvalidOperationException(NotSmaller(original, pdf.Length));
            }
            return ToolOutput.Write(input, L("compressed"), FileFormat.Pdf, pdf);
        }
        var (data, format) = CompressImage(input, settings, token);
        if (data.Length >= original)
        {
            throw new InvalidOperationException(NotSmaller(original, data.Length));
        }
        return ToolOutput.Write(input, L("compressed"), format, data);
    }

    private static string NotSmaller(long original, long result) =>
        string.Format(L("The compressed file ({0}) is not smaller than the original ({1}). Choose another format or a smaller size."),
            ImageIO.Size(result), ImageIO.Size(original));


    /// <summary>
    /// Compresses a PDF the way the macOS version does. A page without text (a scan, a photo) becomes one JPEG of the
    /// whole page at the step's resolution. On a page with text the pictures are re-encoded as JPEG, no larger than
    /// the page at that resolution, and the text, fonts, drawings and links stay as they are. Target-size mode tries
    /// ever smaller steps until the file fits, and returns the smallest result when none does.
    /// </summary>
    public static byte[] CompressPdf(string input, CompressionSettings settings, CancellationToken token)
    {
        if (settings.Mode != CompressionMode.TargetSize)
        {
            return CompressPdf(input, settings.PdfStep, token);
        }
        byte[]? smallest = null;
        foreach (var step in CompressionSettings.PdfTargetSteps)
        {
            var data = CompressPdf(input, step, token);
            if (data.Length <= settings.TargetBytes)
            {
                return data;
            }
            if (smallest is null || data.Length < smallest.Length)
            {
                smallest = data;
            }
        }
        return smallest!;
    }

    private static byte[] CompressPdf(string input, PdfStep step, CancellationToken token)
    {
        using var document = PdfReader.Open(input, PdfDocumentOpenMode.Modify);
        Func<int, BitmapSource>? render = null;
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        for (var index = 0; index < document.PageCount; index++)
        {
            token.ThrowIfCancellationRequested();
            var page = document.Pages[index];
            var resources = page.Elements.GetDictionary("/Resources");
            var (text, imageBytes) = Inspect(ContentStreams(page), resources, 0);
            if (imageBytes > 0 && !text)
            {
                // A scan: one JPEG of the page, unless that is larger than its pictures (a low-resolution scan).
                render ??= PdfRenderer.Pages(input, step.Dpi);
                var picture = render(index);
                var jpeg = ImageIO.Encode(picture, FileFormat.Jpg, step.Quality);
                if (jpeg.Length < imageBytes)
                {
                    Rasterize(page, jpeg, picture.PixelWidth * 72.0 / picture.DpiX, picture.PixelHeight * 72.0 / picture.DpiY);
                    continue;
                }
            }
            var side = (int)Math.Ceiling(Math.Max(page.Width.Point, page.Height.Point) / 72 * step.Dpi);
            RecompressImages(resources, step, side, seen, 0);
        }
        document.Options.CompressContentStreams = true;
        using var stream = new MemoryStream();
        document.Save(stream, false);
        return stream.ToArray();
    }

    /// <summary>A text-showing operator; inline image bytes may match too, which only keeps such a page as it is.</summary>
    private static readonly Regex TextOperator = new(@"\b(BT|Tj|TJ)\b", RegexOptions.Compiled);

    private static PdfItem? Deref(PdfItem? item) => item is PdfReference reference ? reference.Value : item;

    private static List<byte[]> ContentStreams(PdfPage page)
    {
        var result = new List<byte[]>();
        switch (Deref(page.Elements["/Contents"]))
        {
            case PdfDictionary { Stream: { } stream }:
                result.Add(stream.UnfilteredValue);
                break;
            case PdfArray array:
                foreach (var item in array.Elements)
                {
                    if (Deref(item) is PdfDictionary { Stream: { } part })
                    {
                        result.Add(part.UnfilteredValue);
                    }
                }
                break;
        }
        return result;
    }

    /// <summary>Whether a page (with the forms it draws) shows text, and how many bytes its pictures take.</summary>
    private static (bool Text, long ImageBytes) Inspect(IEnumerable<byte[]> contents, PdfDictionary? resources, int depth)
    {
        var text = contents.Any(content => TextOperator.IsMatch(Encoding.Latin1.GetString(content)));
        var images = 0L;
        if (depth < 8 && resources?.Elements.GetDictionary("/XObject") is { } objects)
        {
            foreach (var key in objects.Elements.Keys)
            {
                if (objects.Elements.GetDictionary(key) is not { } item)
                {
                    continue;
                }
                var subtype = item.Elements.GetName("/Subtype");
                if (subtype == "/Image")
                {
                    images += item.Stream?.Value.Length ?? 0;
                }
                else if (subtype == "/Form" && item.Stream is { } stream)
                {
                    var (formText, formImages) = Inspect([stream.UnfilteredValue], item.Elements.GetDictionary("/Resources"), depth + 1);
                    text |= formText;
                    images += formImages;
                }
            }
        }
        return (text, images);
    }

    /// <summary>Replaces the page with one JPEG of it, <paramref name="width"/> × <paramref name="height"/> points as seen.</summary>
    private static void Rasterize(PdfPage page, byte[] jpeg, double width, double height)
    {
        // The render is already turned and cropped; old pictures and fonts go with the old resources.
        foreach (var key in new[] { "/Rotate", "/CropBox", "/BleedBox", "/TrimBox", "/ArtBox", "/Resources" })
        {
            page.Elements.Remove(key);
        }
        page.Orientation = PdfSharp.PageOrientation.Portrait;
        page.MediaBox = new PdfRectangle(new XPoint(0, 0), new XPoint(width, height));
        using var picture = XImage.FromStream(new MemoryStream(jpeg));
        using var graphics = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Replace);
        graphics.DrawImage(picture, 0, 0, width, height);
    }

    private static void RecompressImages(PdfDictionary? resources, PdfStep step, int maxSide, HashSet<PdfDictionary> seen, int depth)
    {
        if (depth >= 8 || resources?.Elements.GetDictionary("/XObject") is not { } objects)
        {
            return;
        }
        foreach (var key in objects.Elements.Keys)
        {
            if (objects.Elements.GetDictionary(key) is not { } item || !seen.Add(item))
            {
                continue;
            }
            var subtype = item.Elements.GetName("/Subtype");
            if (subtype == "/Image")
            {
                RecompressImage(item, step, maxSide);
            }
            else if (subtype == "/Form")
            {
                RecompressImages(item.Elements.GetDictionary("/Resources"), step, maxSide, seen, depth + 1);
            }
        }
    }

    /// <summary>
    /// Re-encodes one RGB or grey picture (JPEG, Flate or uncompressed) as JPEG, scaled down to
    /// <paramref name="maxSide"/>. Masks, colour keys, decode arrays and other colour spaces are left alone, as is any
    /// picture that would not get smaller.
    /// </summary>
    private static void RecompressImage(PdfDictionary image, PdfStep step, int maxSide)
    {
        var elements = image.Elements;
        var filter = elements["/Filter"]?.ToString();
        var width = elements.GetInteger("/Width");
        var height = elements.GetInteger("/Height");
        var components = Components(image);
        if (image.Stream?.Value is not { Length: > 16_000 } bytes || components == 0 || width <= 0 || height <= 0
            || (long)width * height > 60_000_000 || elements.GetBoolean("/ImageMask")
            || elements.ContainsKey("/Mask") || elements.ContainsKey("/Decode"))
        {
            return;
        }
        try
        {
            BitmapSource? picture = filter switch
            {
                "/DCTDecode" or "[/DCTDecode]" =>
                    BitmapDecoder.Create(new MemoryStream(bytes), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0],
                "/FlateDecode" or "[/FlateDecode]" or null when elements.GetInteger("/BitsPerComponent") == 8 =>
                    Raw(Inflate(bytes, elements.GetDictionary("/DecodeParms"), width, height, components), width, height, components),
                _ => null,
            };
            if (picture is null)
            {
                return;
            }
            picture = ImageIO.Scale(picture, maxSide);
            picture = new FormatConvertedBitmap(picture, components == 1 ? PixelFormats.Gray8 : PixelFormats.Bgr24, null, 0);
            var jpeg = ImageIO.Encode(picture, FileFormat.Jpg, step.Quality);
            if (jpeg.Length >= bytes.Length)
            {
                return;
            }
            image.Stream!.Value = jpeg;
            elements.SetName("/Filter", "/DCTDecode");
            elements.Remove("/DecodeParms");
            elements.SetInteger("/Width", picture.PixelWidth);
            elements.SetInteger("/Height", picture.PixelHeight);
            elements.SetInteger("/BitsPerComponent", 8);
        }
        catch (Exception error) when (error is NotSupportedException or FileFormatException or InvalidOperationException
            or ArgumentException or InvalidDataException or IOException)
        {
            // A picture that cannot be read stays as it is.
        }
    }

    /// <summary>3 for RGB, 1 for grey (device or ICC-based), 0 for colour spaces left alone.</summary>
    private static int Components(PdfDictionary image)
    {
        switch (Deref(image.Elements["/ColorSpace"]))
        {
            case PdfName name:
                return name.Value switch { "/DeviceRGB" => 3, "/DeviceGray" => 1, _ => 0 };
            case PdfArray { Elements.Count: 2 } array when Deref(array.Elements[0]) is PdfName { Value: "/ICCBased" }
                && Deref(array.Elements[1]) is PdfDictionary profile:
                return profile.Elements.GetInteger("/N") switch { 3 => 3, 1 => 1, _ => 0 };
            default:
                return 0;
        }
    }

    /// <summary>The samples of a Flate (or uncompressed) picture, with PNG predictors undone.</summary>
    private static byte[]? Inflate(byte[] data, PdfDictionary? parameters, int width, int height, int components)
    {
        var row = width * components;
        byte[] samples;
        if (data.Length == row * height && parameters is null)
        {
            samples = data; // uncompressed
        }
        else
        {
            using var inflater = new System.IO.Compression.ZLibStream(new MemoryStream(data), System.IO.Compression.CompressionMode.Decompress);
            using var output = new MemoryStream();
            inflater.CopyTo(output);
            samples = output.ToArray();
        }
        var predictor = parameters?.Elements.GetInteger("/Predictor") ?? 1;
        if (predictor <= 1)
        {
            return samples.Length >= row * height ? samples : null;
        }
        if (predictor < 10 || samples.Length < (row + 1) * height)
        {
            return null; // TIFF predictors are rare in pictures
        }
        var result = new byte[row * height];
        for (var y = 0; y < height; y++)
        {
            var type = samples[y * (row + 1)];
            var source = y * (row + 1) + 1;
            var target = y * row;
            for (var x = 0; x < row; x++)
            {
                int left = x >= components ? result[target + x - components] : 0;
                int up = y > 0 ? result[target - row + x] : 0;
                int upLeft = x >= components && y > 0 ? result[target - row + x - components] : 0;
                var raw = samples[source + x];
                result[target + x] = type switch
                {
                    0 => raw,
                    1 => (byte)(raw + left),
                    2 => (byte)(raw + up),
                    3 => (byte)(raw + (left + up) / 2),
                    4 => (byte)(raw + Paeth(left, up, upLeft)),
                    _ => throw new InvalidDataException("Unknown PNG filter."),
                };
            }
        }
        return result;
    }

    private static int Paeth(int left, int up, int upLeft)
    {
        var estimate = left + up - upLeft;
        var (toLeft, toUp, toUpLeft) = (Math.Abs(estimate - left), Math.Abs(estimate - up), Math.Abs(estimate - upLeft));
        return toLeft <= toUp && toLeft <= toUpLeft ? left : toUp <= toUpLeft ? up : upLeft;
    }

    private static BitmapSource? Raw(byte[]? samples, int width, int height, int components) => samples is null ? null
        : BitmapSource.Create(width, height, 96, 96, components == 1 ? PixelFormats.Gray8 : PixelFormats.Rgb24, null, samples,
            width * components);
}
