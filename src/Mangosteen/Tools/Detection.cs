using System.Runtime.InteropServices.WindowsRuntime;
using Mangosteen.Core.Imaging;
using Mangosteen.Platform;
using Windows.Graphics.Imaging;
using Windows.Media.FaceAnalysis;
using Windows.Media.Ocr;

namespace Mangosteen.Tools;

/// <summary>Finds faces and text on the device with Windows' own engines (no network).</summary>
internal static class Detection
{
    public static bool FacesSupported => FaceDetector.IsSupported;

    /// <summary>Faces, slightly enlarged so the edges are covered too.</summary>
    public static List<UnitRect> Faces(Bgra image)
    {
        if (!FaceDetector.IsSupported)
        {
            return [];
        }
        var detector = FaceDetector.CreateAsync().AsTask().GetAwaiter().GetResult();
        using var color = ToSoftwareBitmap(image);
        using var gray = SoftwareBitmap.Convert(color, BitmapPixelFormat.Gray8);
        var faces = detector.DetectFacesAsync(gray).AsTask().GetAwaiter().GetResult();
        return faces.Select(f => new UnitRect((double)f.FaceBox.X / image.Width, (double)f.FaceBox.Y / image.Height,
            (double)f.FaceBox.Width / image.Width, (double)f.FaceBox.Height / image.Height).Inflate(0.15)).ToList();
    }

    /// <summary>Lines of text, in the languages of the user's Windows profile.</summary>
    public static List<UnitRect> Text(Bgra image)
    {
        var engine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is null)
        {
            Log.Write("ocr: no recognition language installed");
            return [];
        }
        Log.Write($"ocr: language {engine.RecognizerLanguage.LanguageTag}");
        var source = image;
        var max = (int)OcrEngine.MaxImageDimension;
        if (Math.Max(image.Width, image.Height) > max)
        {
            source = ImageIO.ToBgra(ImageIO.Scale(ImageIO.ToBitmap(image), max));
        }
        using var bitmap = ToSoftwareBitmap(source);
        var result = engine.RecognizeAsync(bitmap).AsTask().GetAwaiter().GetResult();
        var regions = new List<UnitRect>();
        foreach (var line in result.Lines)
        {
            if (line.Words.Count == 0)
            {
                continue;
            }
            var left = line.Words.Min(w => w.BoundingRect.X);
            var top = line.Words.Min(w => w.BoundingRect.Y);
            var right = line.Words.Max(w => w.BoundingRect.X + w.BoundingRect.Width);
            var bottom = line.Words.Max(w => w.BoundingRect.Y + w.BoundingRect.Height);
            // Grow by a share of the line height on every side.
            var margin = (bottom - top) * 0.25;
            regions.Add(new UnitRect(Math.Max(0, (left - margin) / source.Width), Math.Max(0, (top - margin) / source.Height),
                Math.Min(source.Width, right - left + margin * 2) / source.Width, Math.Min(source.Height, bottom - top + margin * 2) / source.Height));
        }
        return regions;
    }

    private static SoftwareBitmap ToSoftwareBitmap(Bgra image) =>
        SoftwareBitmap.CreateCopyFromBuffer(image.Pixels.AsBuffer(), BitmapPixelFormat.Bgra8, image.Width, image.Height, BitmapAlphaMode.Ignore);
}
