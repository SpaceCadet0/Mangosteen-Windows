using Mangosteen.Conversion;
using Mangosteen.Core;
using Mangosteen.Platform;
using Windows.Media.Ocr;

namespace Mangosteen.Tools;

/// <summary>
/// Text from photos and scanned PDFs, recognised on the device by Windows (Windows.Media.Ocr) in the languages of the
/// user's profile. PDF pages are rendered at 300 dpi and separated by a form feed. Writes "name (text).txt".
/// </summary>
internal static class TextRecognition
{
    public static bool Accepts(string path) =>
        FileFormats.Detect(path) is { } format && (format == FileFormat.Pdf || format.Category() == FormatCategory.Image && format != FileFormat.Svg);

    public static string RecognizeFile(string path, CancellationToken token)
    {
        var engine = OcrEngine.TryCreateFromUserProfileLanguages()
            ?? throw new ShownException(L("Windows has no text recognition language installed. Add one in Settings → Time & language → Language."));
        Log.Write($"ocr: {Path.GetFileName(path)} in {engine.RecognizerLanguage.LanguageTag}");
        var pages = new List<string>();
        if (FileFormats.Detect(path) == FileFormat.Pdf)
        {
            var count = PdfRenderer.PageCount(path);
            for (var index = 0; index < count; index++)
            {
                token.ThrowIfCancellationRequested();
                pages.Add(Recognize(engine, PdfRenderer.RenderPage(path, index)));
            }
        }
        else
        {
            pages.Add(Recognize(engine, ImageIO.Load(path)));
        }
        if (pages.All(string.IsNullOrWhiteSpace))
        {
            throw new ShownException(string.Format(L("No text was found in “{0}”."), Path.GetFileName(path)));
        }
        var text = string.Join("\n\f", pages.Select(p => p.TrimEnd())) + "\n";
        return ToolOutput.Write(path, L("text"), FileFormat.Txt, new System.Text.UTF8Encoding(false).GetBytes(text));
    }

    private static string Recognize(OcrEngine engine, System.Windows.Media.Imaging.BitmapSource image)
    {
        var max = (int)OcrEngine.MaxImageDimension;
        if (Math.Max(image.PixelWidth, image.PixelHeight) > max)
        {
            image = ImageIO.Scale(image, max);
        }
        var bgra = ImageIO.ToBgra(image);
        using var bitmap = Windows.Graphics.Imaging.SoftwareBitmap.CreateCopyFromBuffer(
            System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(bgra.Pixels),
            Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, bgra.Width, bgra.Height, Windows.Graphics.Imaging.BitmapAlphaMode.Ignore);
        var result = engine.RecognizeAsync(bitmap).AsTask().GetAwaiter().GetResult();
        return string.Join("\n", result.Lines.Select(line => line.Text));
    }
}

/// <summary>A failure whose message is meant for people as it is: the notification shows it without the file name.</summary>
internal sealed class ShownException(string message) : Exception(message);
