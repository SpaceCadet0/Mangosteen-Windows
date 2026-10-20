using System.Windows.Media.Imaging;
using Mangosteen.Core;
using Mangosteen.Core.Imaging;
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
            var pdf = CompressPdf(input, token);
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
    /// Re-encodes the JPEG pictures inside a PDF at screen resolution (at most 1600 px on the longer side, quality 60).
    /// Pictures with masks, unusual colour spaces or decode arrays are left alone, as is everything else in the file.
    /// </summary>
    public static byte[] CompressPdf(string input, CancellationToken token)
    {
        using var document = PdfReader.Open(input, PdfDocumentOpenMode.Modify);
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        foreach (var page in document.Pages)
        {
            token.ThrowIfCancellationRequested();
            if (page.Resources?.Elements.GetDictionary("/XObject") is not { } objects)
            {
                continue;
            }
            foreach (var key in objects.Elements.Keys)
            {
                if (objects.Elements.GetDictionary(key) is { } image && seen.Add(image))
                {
                    RecompressJpeg(image);
                }
            }
        }
        document.Options.CompressContentStreams = true;
        using var stream = new MemoryStream();
        document.Save(stream, false);
        return stream.ToArray();
    }

    private static void RecompressJpeg(PdfDictionary image)
    {
        var elements = image.Elements;
        if (elements.GetName("/Subtype") != "/Image" || image.Stream?.Value is not { Length: > 20_000 } bytes
            || elements["/Filter"]?.ToString() is not ("/DCTDecode" or "[/DCTDecode]")
            || elements.ContainsKey("/SMask") || elements.ContainsKey("/Mask") || elements.ContainsKey("/Decode")
            || elements.GetName("/ColorSpace") is not ("/DeviceRGB" or "/DeviceGray"))
        {
            return;
        }
        try
        {
            var decoder = BitmapDecoder.Create(new MemoryStream(bytes), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            BitmapSource picture = decoder.Frames[0];
            picture = ImageIO.Scale(picture, 1600);
            var gray = elements.GetName("/ColorSpace") == "/DeviceGray";
            if (gray)
            {
                picture = new FormatConvertedBitmap(picture, System.Windows.Media.PixelFormats.Gray8, null, 0);
            }
            var encoder = new JpegBitmapEncoder { QualityLevel = 60 };
            encoder.Frames.Add(BitmapFrame.Create(picture));
            using var output = new MemoryStream();
            encoder.Save(output);
            if (output.Length >= bytes.Length)
            {
                return;
            }
            image.Stream!.Value = output.ToArray();
            elements.SetInteger("/Width", picture.PixelWidth);
            elements.SetInteger("/Height", picture.PixelHeight);
            elements.SetInteger("/BitsPerComponent", 8);
        }
        catch (Exception error) when (error is NotSupportedException or FileFormatException or InvalidOperationException or ArgumentException)
        {
            // An image WIC cannot read stays as it is.
        }
    }
}
