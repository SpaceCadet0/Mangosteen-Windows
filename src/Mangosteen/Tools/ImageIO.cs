using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mangosteen.Conversion;
using Mangosteen.Core;
using Mangosteen.Core.Imaging;
using Mangosteen.Platform;
using SkiaSharp;

namespace Mangosteen.Tools;

/// <summary>Loading, converting and encoding images for the tools.</summary>
internal static class ImageIO
{
    /// <summary>The upright image (EXIF orientation applied), scaled down so the longer side is at most <paramref name="maxSide"/>.</summary>
    public static BitmapSource Load(string path, int? maxSide = null)
    {
        var image = ImageConverter.Decode(path);
        return maxSide is { } limit ? Scale(image, limit) : image;
    }

    public static BitmapSource Scale(BitmapSource image, int maxSide)
    {
        var (width, height) = CompressionSearch.FitInside(image.PixelWidth, image.PixelHeight, maxSide);
        if (width == image.PixelWidth && height == image.PixelHeight)
        {
            return image;
        }
        return Resize(image, width, height);
    }

    /// <summary>High-quality resize (Fant scaling in WIC).</summary>
    public static BitmapSource Resize(BitmapSource image, int width, int height)
    {
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.Fant);
        using (var context = visual.RenderOpen())
        {
            context.DrawImage(image, new System.Windows.Rect(0, 0, width, height));
        }
        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        var result = new FormatConvertedBitmap(target, PixelFormats.Bgra32, null, 0);
        result.Freeze();
        return result;
    }

    public static Bgra ToBgra(BitmapSource image)
    {
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var result = new Bgra(converted.PixelWidth, converted.PixelHeight);
        converted.CopyPixels(result.Pixels, result.Stride, 0);
        return result;
    }

    public static BitmapSource ToBitmap(Bgra image, double dpiX = 96, double dpiY = 96)
    {
        var bitmap = BitmapSource.Create(image.Width, image.Height, dpiX, dpiY, PixelFormats.Bgra32, null, image.Pixels, image.Stride);
        bitmap.Freeze();
        return bitmap;
    }

    public static bool HasTransparency(BitmapSource image)
    {
        if (!image.Format.Masks.Any() && image.Format != PixelFormats.Bgra32 && image.Format != PixelFormats.Pbgra32
            && image.Format != PixelFormats.Rgba64 && image.Format != PixelFormats.Prgba64)
        {
            return false;
        }
        var pixels = ToBgra(image).Pixels;
        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 255)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Pixel formats JPEG stores as they are; anything else (alpha, 16 bits) is flattened on white.</summary>
    private static readonly HashSet<PixelFormat> JpegFormats = [PixelFormats.Gray8, PixelFormats.Bgr24, PixelFormats.Bgr32, PixelFormats.Cmyk32];

    /// <summary>
    /// Encodes in memory. <paramref name="quality"/> (0…1) applies to JPG and WebP. JPG, PNG and TIFF keep the pixel
    /// format (grey, CMYK, 16 bits where the format allows) and the colour profiles in <paramref name="colors"/>.
    /// </summary>
    public static byte[] Encode(BitmapSource image, FileFormat format, double quality = 0.9, BitmapMetadata? metadata = null,
        IReadOnlyList<ColorContext>? colors = null)
    {
        using var stream = new MemoryStream();
        switch (format)
        {
            case FileFormat.Webp:
            {
                var bgra = ToBgra(image);
                var info = new SKImageInfo(bgra.Width, bgra.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
                var handle = System.Runtime.InteropServices.GCHandle.Alloc(bgra.Pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
                try
                {
                    using var sk = SKImage.FromPixelCopy(info, handle.AddrOfPinnedObject(), bgra.Stride)!;
                    using var data = sk.Encode(SKEncodedImageFormat.Webp, (int)Math.Round(Math.Clamp(quality, 0.01, 1) * 100))!;
                    data.SaveTo(stream);
                }
                finally
                {
                    handle.Free();
                }
                break;
            }
            case FileFormat.Jpg:
            {
                var encoder = new JpegBitmapEncoder { QualityLevel = Math.Clamp((int)Math.Round(quality * 100), 1, 100) };
                var pixels = JpegFormats.Contains(image.Format) ? image : ImageConverter.FlattenOnWhite(image);
                encoder.Frames.Add(Frame(pixels, metadata, colors));
                encoder.Save(stream);
                break;
            }
            case FileFormat.Png or FileFormat.Tiff when colors is { Count: > 0 }:
            {
                BitmapEncoder encoder = format == FileFormat.Png ? new PngBitmapEncoder() : new TiffBitmapEncoder { Compression = TiffCompressOption.Lzw };
                encoder.Frames.Add(Frame(image, null, colors));
                encoder.Save(stream);
                break;
            }
            default:
                ImageConverter.Encode(image, format, stream);
                break;
        }
        return stream.ToArray();
    }

    /// <summary>A frame with the original metadata when the format accepts it; otherwise without.</summary>
    private static BitmapFrame Frame(BitmapSource image, BitmapMetadata? metadata, IReadOnlyList<ColorContext>? colors = null)
    {
        var profiles = colors is { Count: > 0 } ? new System.Collections.ObjectModel.ReadOnlyCollection<ColorContext>(colors.ToList()) : null;
        try
        {
            return BitmapFrame.Create(image, null, (BitmapMetadata?)metadata?.Clone(), profiles);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return BitmapFrame.Create(image);
        }
    }

    /// <summary>The colour profiles of the first frame (embedded ICC), or none.</summary>
    public static IReadOnlyList<ColorContext> ReadColorContexts(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            return decoder.Frames[0].ColorContexts?.ToList() ?? [];
        }
        catch (Exception error) when (error is IOException or NotSupportedException or FileFormatException or InvalidOperationException)
        {
            return [];
        }
    }

    /// <summary>The metadata of the first frame, or null.</summary>
    public static BitmapMetadata? ReadMetadata(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames[0].Metadata is BitmapMetadata metadata)
            {
                var copy = (BitmapMetadata)metadata.Clone();
                // The orientation is applied to the pixels, so it must not be applied again.
                try
                {
                    copy.SetQuery("System.Photo.Orientation", (ushort)1);
                }
                catch (Exception error) when (error is NotSupportedException or InvalidOperationException or ArgumentException)
                {
                }
                return copy;
            }
        }
        catch (Exception error) when (error is IOException or NotSupportedException or FileFormatException or InvalidOperationException)
        {
        }
        return null;
    }

    public static string Size(long bytes) => bytes switch
    {
        < 1000 => $"{bytes} {L("B")}",
        < 1_000_000 => $"{bytes / 1000.0:0.#} {L("KB")}",
        _ => $"{bytes / 1_000_000.0:0.##} {L("MB")}",
    };
}

/// <summary>
/// Runs preview renders off the UI thread. A new request cancels the previous one and waits a moment, so a
/// slider being dragged does not queue up work.
/// </summary>
internal sealed class PreviewScheduler<TResult>(System.Windows.Threading.Dispatcher dispatcher, Action<TResult> show, Action<Exception>? fail = null)
{
    private CancellationTokenSource? _current;

    public bool IsBusy { get; private set; }

    public void Request(Func<CancellationToken, TResult> render, int delayMilliseconds = 200)
    {
        _current?.Cancel();
        var cancel = new CancellationTokenSource();
        _current = cancel;
        IsBusy = true;
        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delayMilliseconds, cancel.Token);
                var result = render(cancel.Token);
                cancel.Token.ThrowIfCancellationRequested();
                await dispatcher.InvokeAsync(() =>
                {
                    if (ReferenceEquals(_current, cancel))
                    {
                        IsBusy = false;
                        show(result);
                    }
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception error)
            {
                Log.Error("preview", error);
                await dispatcher.InvokeAsync(() =>
                {
                    IsBusy = false;
                    fail?.Invoke(error);
                });
            }
        });
    }
}
