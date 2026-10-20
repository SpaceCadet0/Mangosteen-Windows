using Mangosteen.Core;
using Mangosteen.Core.Imaging;

namespace Mangosteen.Core.Tests;

public class CropMathTests
{
    [Fact]
    public void CenteredSquareInLandscape()
    {
        var rect = CropMath.CenteredRect(1, 400, 200);
        Assert.Equal(0.25, rect.X, 6);
        Assert.Equal(0, rect.Y, 6);
        Assert.Equal(0.5, rect.Width, 6);
        Assert.Equal(1, rect.Height, 6);
    }

    [Fact]
    public void ResizeKeepsAspect()
    {
        var start = new UnitRect(0.25, 0.25, 0.5, 0.5);
        var result = CropMath.Resize(start, CropMath.Edges.BottomRight, 0.1, 0.0, unitAspect: 1)!.Value;
        Assert.Equal(0.6, result.Width, 6);
        Assert.Equal(0.6, result.Height, 6);
        Assert.Equal(0.25, result.X, 6);
    }

    [Fact]
    public void ResizeRefusesToLeaveTheImage() =>
        Assert.Null(CropMath.Resize(new UnitRect(0.5, 0.5, 0.4, 0.4), CropMath.Edges.Right, 0.2, 0, null));

    [Fact]
    public void MoveStaysInside()
    {
        var moved = CropMath.Move(new UnitRect(0.5, 0.5, 0.4, 0.4), 0.5, -1);
        Assert.Equal(new UnitRect(0.6, 0, 0.4, 0.4), moved with { X = Math.Round(moved.X, 6) });
    }

    [Fact]
    public void PixelRectIsClampedAndNeverEmpty()
    {
        Assert.Equal(new PixelRect(0, 0, 100, 50), UnitRect.Full.ToPixels(100, 50));
        Assert.Equal(1, new UnitRect(0.999, 0.999, 0, 0).ToPixels(100, 50).Width);
    }

    [Fact]
    public void CropCopiesPixels()
    {
        var image = new Bgra(3, 2);
        image.Pixels[(1 * 3 + 2) * 4] = 7; // pixel (2,1), blue
        var cropped = CropMath.Crop(image, new PixelRect(1, 1, 2, 1));
        Assert.Equal(7, cropped.Pixels[4]);
    }
}

public class LayoutTests
{
    [Fact]
    public void GridCentresAShortLastRow()
    {
        var (width, height, cells) = Collage.Layout([1, 1, 1], new CollageSettings { Width = 1000, Spacing = 0 });
        Assert.Equal(1000, width);
        Assert.Equal(1000, height);
        Assert.Equal(250, cells[2].X, 3); // centred under the first row
    }

    [Fact]
    public void RowKeepsPhotosWhole()
    {
        var (_, height, cells) = Collage.Layout([2, 1], new CollageSettings { Layout = CollageLayout.Row, Width = 1000, Spacing = 0 });
        Assert.Equal(2, cells[0].Width / cells[0].Height, 6);
        Assert.Equal(1, cells[1].Width / cells[1].Height, 6);
        Assert.Equal(1000, cells[0].Width + cells[1].Width, 6);
        Assert.Equal(Math.Round(1000 / 3.0), height);
    }

    [Fact]
    public void FeaturedPutsTheFirstPhotoLarge()
    {
        var (_, _, cells) = Collage.Layout([1, 1, 1], new CollageSettings { Layout = CollageLayout.Featured, Width = 1000 });
        Assert.True(cells[0].Width > cells[1].Width * 1.5);
        Assert.Equal(cells[1].X, cells[2].X);
    }

    [Fact]
    public void FrameAddsMarginAndAspect()
    {
        Assert.Equal((1160, 760), Frame.CanvasSize(1000, 600, new FrameSettings { Padding = 0.08 }));
        var (w, h) = Frame.CanvasSize(1000, 600, new FrameSettings { Padding = 0, Aspect = CanvasAspect.Square });
        Assert.Equal(w, h);
    }
}

public class PhotoEditorTests
{
    private static Bgra Sample()
    {
        var image = new Bgra(16, 12);
        var random = new Random(1);
        random.NextBytes(image.Pixels);
        return image;
    }

    [Fact]
    public void NeutralSettingsChangeNothing()
    {
        var image = Sample();
        Assert.Equal(image.Pixels, PhotoEditor.Apply(image, PhotoAdjustments.Identity).Pixels);
    }

    [Fact]
    public void TinyAdjustmentsKeepPixelsClose()
    {
        // Every slider is a smooth function: a small change makes a small difference.
        var image = Sample();
        var result = PhotoEditor.Apply(image, new PhotoAdjustments { Contrast = 0.001 }).Pixels;
        for (var i = 0; i < result.Length; i++)
        {
            Assert.InRange(Math.Abs(result[i] - image.Pixels[i]), 0, 1);
        }
    }

    [Fact]
    public void ExposureBrightens()
    {
        var image = Bgra.Filled(4, 4, 60, 60, 60);
        Assert.True(PhotoEditor.Apply(image, new PhotoAdjustments { Exposure = 1 }).Pixels[0] > 100);
    }

    [Fact]
    public void MonoRemovesColour()
    {
        var image = Bgra.Filled(2, 2, 10, 120, 240);
        var p = PhotoEditor.Apply(image, new PhotoAdjustments { Effect = PhotoEffect.Mono }).Pixels;
        Assert.Equal(p[0], p[1]);
        Assert.Equal(p[1], p[2]);
    }

    [Fact]
    public void AlphaIsKept()
    {
        var image = Bgra.Filled(3, 3, 100, 100, 100, 77);
        Assert.All(PhotoEditor.Apply(image, new PhotoAdjustments { Sharpness = 1, Vignette = 1, Grain = 1 }).Pixels.Where((_, i) => i % 4 == 3),
            a => Assert.Equal(77, a));
    }
}

public class RedactionTests
{
    [Theory]
    [InlineData(RedactionStyle.Solid)]
    [InlineData(RedactionStyle.Pixelate)]
    [InlineData(RedactionStyle.Blur)]
    public void OnlyTheRegionChanges(RedactionStyle style)
    {
        var image = new Bgra(40, 40);
        new Random(2).NextBytes(image.Pixels);
        var before = image.Clone();
        Redaction.Apply(image, [new UnitRect(0.5, 0.5, 0.5, 0.5)], style);
        // Top-left quarter untouched, bottom-right changed.
        Assert.Equal(before.Pixels[..(40 * 4 * 10)], image.Pixels[..(40 * 4 * 10)]);
        var regionStart = (20 * 40 + 20) * 4;
        Assert.NotEqual(before.Pixels[regionStart..], image.Pixels[regionStart..]);
    }

    [Fact]
    public void PixelateMakesUniformBlocks()
    {
        var image = new Bgra(16, 16);
        new Random(3).NextBytes(image.Pixels);
        Redaction.Pixelate(image, new PixelRect(0, 0, 16, 16), 8);
        Assert.Equal(image.Pixels[0], image.Pixels[7 * 4]);
        Assert.Equal(image.Pixels[0], image.Pixels[(7 * 16) * 4]);
    }
}

public class CompressionSearchTests
{
    [Fact]
    public void FindsTheHighestQualityThatFits()
    {
        // A fake encoder: size grows with quality.
        var result = CompressionSearch.BestFit(q => new byte[(int)(q * 1000)], limit: 600);
        Assert.NotNull(result);
        Assert.InRange(result!.Length, 550, 600);
    }

    [Fact]
    public void NullWhenNothingFits() => Assert.Null(CompressionSearch.BestFit(_ => new byte[1000], limit: 10));

    [Fact]
    public void FitInsideKeepsProportions() => Assert.Equal((1280, 720), CompressionSearch.FitInside(3840, 2160, 1280));

    [Fact]
    public void SmallPresetLimitsSize() =>
        Assert.Equal(2560, new CompressionSettings { Preset = CompressionPreset.Small }.EffectiveMaxPixelSize);
}

public class LocTests
{
    [Fact]
    public void FallsBackToEnglish()
    {
        Assert.Equal("Crop", Loc.L("Crop"));
        Loc.Use(new Dictionary<string, string> { ["Crop"] = "Обрезка" });
        try
        {
            Assert.Equal("Обрезка", Loc.L("Crop"));
            Assert.Equal("Other", Loc.L("Other"));
        }
        finally
        {
            Loc.Use(null);
        }
    }
}
