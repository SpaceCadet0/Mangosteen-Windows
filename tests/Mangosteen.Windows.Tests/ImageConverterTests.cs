using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mangosteen.Conversion;
using Mangosteen.Core;

namespace Mangosteen.Windows.Tests;

public sealed class ImageConverterTests : IDisposable
{
    private const int Width = 7;
    private const int Height = 5;
    private readonly string _dir = Directory.CreateTempSubdirectory("mangosteen-wic").FullName;

    public void Dispose() => TempCleanup.Delete(_dir);

    public static TheoryData<FileFormat> Targets => new() { FileFormat.Jpg, FileFormat.Tiff, FileFormat.Gif };

    [Theory]
    [MemberData(nameof(Targets))]
    public void ConvertsPngNextToTheOriginal(FileFormat target)
    {
        var source = WritePng("photo.png");
        var before = File.ReadAllBytes(source);

        var output = ImageConverter.Convert(source, target);

        Assert.Equal(Path.Combine(_dir, $"photo.{target.Extension()}"), output);
        var decoded = ImageConverter.Decode(output);
        Assert.Equal(Width, decoded.PixelWidth);
        Assert.Equal(Height, decoded.PixelHeight);
        Assert.Equal(before, File.ReadAllBytes(source));
        Assert.DoesNotContain(Directory.GetFiles(_dir), f => Path.GetFileName(f).StartsWith(".mangosteen-"));
    }

    [Theory]
    [InlineData(FileFormat.Png)]
    [InlineData(FileFormat.Tiff)]
    [InlineData(FileFormat.Gif)]
    public void ConvertsJpgToEveryFormat(FileFormat target)
    {
        var jpg = ImageConverter.Convert(WritePng("photo.png"), FileFormat.Jpg);
        var output = ImageConverter.Convert(jpg, target);
        Assert.Equal(Width, ImageConverter.Decode(output).PixelWidth);
    }

    [Fact]
    public void NeverOverwritesAnExistingResult()
    {
        var source = WritePng("photo.png");
        File.WriteAllText(Path.Combine(_dir, "photo.jpg"), "keep me");

        var first = ImageConverter.Convert(source, FileFormat.Jpg);
        var second = ImageConverter.Convert(source, FileFormat.Jpg);

        Assert.Equal(Path.Combine(_dir, "photo 2.jpg"), first);
        Assert.Equal(Path.Combine(_dir, "photo 3.jpg"), second);
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(_dir, "photo.jpg")));
    }

    [Fact]
    public void TransparencyBecomesWhiteWithoutAlphaChannel()
    {
        var gif = ImageConverter.Convert(WritePng("clear.png"), FileFormat.Gif);
        var pixels = Pixels(ImageConverter.Decode(gif));
        // Pixel (0,0) was fully transparent.
        Assert.Equal([255, 255, 255], pixels[..3]);
    }

    [Fact]
    public void PngKeepsTransparency()
    {
        var tiff = ImageConverter.Convert(WritePng("clear.png"), FileFormat.Tiff);
        var png = ImageConverter.Convert(tiff, FileFormat.Png);
        Assert.Equal(0, Pixels(ImageConverter.Decode(png))[3]);
    }

    [Fact]
    public void AppliesExifOrientation()
    {
        var source = Path.Combine(_dir, "rotated.jpg");
        var metadata = new BitmapMetadata("jpg");
        metadata.SetQuery("System.Photo.Orientation", (ushort)6);
        var encoder = new JpegBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Image(), null, metadata, null));
        using (var stream = File.Create(source))
        {
            encoder.Save(stream);
        }

        var png = ImageConverter.Convert(source, FileFormat.Png);
        var decoded = ImageConverter.Decode(png);
        Assert.Equal(Height, decoded.PixelWidth);
        Assert.Equal(Width, decoded.PixelHeight);
    }

    [Fact]
    public void UnsupportedTargetThrowsAndWritesNothing()
    {
        var source = WritePng("photo.png");
        Assert.Throws<NotSupportedException>(() => ImageConverter.Convert(source, FileFormat.Heic));
        Assert.Single(Directory.GetFiles(_dir));
    }

    /// <summary>A small BGRA image: the first pixel fully transparent, the rest opaque colours.</summary>
    private static BitmapSource Image()
    {
        var pixels = new byte[Width * Height * 4];
        for (var i = 0; i < Width * Height; i++)
        {
            pixels[i * 4 + 0] = (byte)(i * 9);
            pixels[i * 4 + 1] = 0x1E;
            pixels[i * 4 + 2] = 0x67;
            pixels[i * 4 + 3] = (byte)(i == 0 ? 0 : 255);
        }
        var image = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgra32, null, pixels, Width * 4);
        image.Freeze();
        return image;
    }

    private string WritePng(string name)
    {
        var path = Path.Combine(_dir, name);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Image()));
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    private static byte[] Pixels(BitmapSource image)
    {
        var bgra = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[bgra.PixelWidth * bgra.PixelHeight * 4];
        bgra.CopyPixels(pixels, bgra.PixelWidth * 4, 0);
        return pixels;
    }
}
