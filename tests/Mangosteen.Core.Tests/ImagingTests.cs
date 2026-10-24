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

public class TransformAndTilesTests
{
    [Fact]
    public void RotateSwapsTheFlipAxes()
    {
        var transform = new ImageTransform().Flip(horizontal: true).Rotate(clockwise: true);
        Assert.Equal(new ImageTransform(1, FlipHorizontal: false, FlipVertical: true), transform);
        Assert.Equal((200, 400), transform.Size(400, 200));
        Assert.True(new ImageTransform().Rotate(true).Rotate(false).IsIdentity);
        Assert.Equal(3, new ImageTransform().Rotate(clockwise: false).QuarterTurns);
    }

    [Fact]
    public void TilesAreCutAtWholePixelsRowByRow()
    {
        var tiles = CropMath.Tiles(CropMath.EvenCuts(3), CropMath.EvenCuts(2), 100, 51);
        Assert.Equal(6, tiles.Count);
        Assert.Equal(new PixelRect(0, 0, 33, 26), tiles[0]);
        Assert.Equal(new PixelRect(33, 0, 34, 26), tiles[1]);
        Assert.Equal(new PixelRect(67, 26, 33, 25), tiles[5]);
        Assert.Equal(100 * 51, tiles.Sum(t => t.Width * t.Height));
    }

    [Fact]
    public void CutsKeepAGap()
    {
        var cuts = new List<double> { 0.3, 0.6 };
        Assert.Equal(0.58, CropMath.ClampCut(cuts, 0, 0.9), 6);
        Assert.Equal(0.02, CropMath.ClampCut(cuts, 0, -1), 6);
        Assert.Equal(0.98, CropMath.ClampCut(cuts, 1, 1.5), 6);
    }
}

public class PdfSplitTests
{
    [Fact]
    public void CutsAndExcludedPages()
    {
        var documents = PdfSplit.Documents(6, [1, 3], [2]);
        Assert.Equal([[0, 1], [3], [4, 5]], documents);
        Assert.Equal("1–2, 4, 6", PdfSplit.Ranges([0, 1, 3, 5]));
    }

    [Fact]
    public void EmptyPartsAreDropped() => Assert.Equal([[2]], PdfSplit.Documents(3, [0, 1], [0, 1]));

    [Fact]
    public void Presets()
    {
        Assert.Equal([0, 1, 2], PdfSplit.EveryPage(4).Order());
        Assert.Equal([2], PdfSplit.InHalf(5));
        Assert.Equal([1, 3], PdfSplit.EveryN(5, 2).Order());
        Assert.Equal(3, PdfSplit.Documents(5, PdfSplit.EveryN(5, 2), []).Count);
    }
}

public class SubjectMaskTests
{
    [Fact]
    public void NormalizesBoundsAndCuts()
    {
        var mask = SubjectMask.Normalize([0.1f, 0.1f, 0.9f, 0.5f, 0.1f, 0.9f]);
        Assert.Equal([0, 0, 255, 128, 0, 255], mask);
        Assert.Equal(new PixelRect(0, 0, 3, 2), SubjectMask.Bounds(mask, 3, 2));
        Assert.Equal(new PixelRect(2, 0, 1, 2), SubjectMask.Bounds([0, 0, 255, 0, 0, 255], 3, 2));
        Assert.Null(SubjectMask.Bounds(new byte[6], 3, 2));
        Assert.False(SubjectMask.HasSubject(new byte[1000]));
        Assert.False(SubjectMask.HasSubject(SubjectMask.Normalize([0f, 0.02f, 0.045f, 0.01f])));

        var image = new Bgra(3, 2);
        Array.Fill(image.Pixels, (byte)200);
        for (var i = 3; i < image.Pixels.Length; i += 4)
        {
            image.Pixels[i] = 255;
        }
        var cut = SubjectMask.Cut(image, mask);
        Assert.Equal(0, cut.Pixels[3]);
        Assert.Equal(255, cut.Pixels[2 * 4 + 3]);
        var flat = SubjectMask.OnColor(cut, 0xFF0000FF);
        Assert.Equal((255, 0, 0, 255), (flat.Pixels[0], flat.Pixels[1], flat.Pixels[2], flat.Pixels[3]));
        Assert.Equal(200, flat.Pixels[2 * 4 + 2]);
    }

    [Fact]
    public void InputIsPlanarRgbMinusTheMeans()
    {
        var image = new Bgra(SubjectMask.Side, SubjectMask.Side);
        image.Pixels[0] = 255; // blue of the first pixel
        var input = SubjectMask.Input(image);
        Assert.Equal(-0.485f, input[0], 3);
        Assert.Equal(1 - 0.406f, input[2 * SubjectMask.Side * SubjectMask.Side], 3);
    }
}

public class ResizeRuleTests
{
    [Fact]
    public void RulesGiveTheNewSize()
    {
        Assert.Equal((2016, 1512), ResizeRule.Percent(50).Apply(4032, 3024));
        Assert.Equal((1920, 1440), ResizeRule.LongSide(1920).Apply(4032, 3024));
        Assert.Equal((1440, 1920), ResizeRule.LongSide(1920).Apply(3024, 4032));
        Assert.Equal((800, 600), ResizeRule.LongSide(1920).Apply(800, 600)); // never enlarged
        Assert.Equal((1000, 750), ResizeRule.Width(1000).Apply(4032, 3024));
        Assert.Equal((1333, 1000), ResizeRule.HeightOnly(1000).Apply(4032, 3024));
        Assert.Equal((500, 500), ResizeRule.Exact(500, 500).Apply(4032, 3024));
    }

    [Fact]
    public void SidesStayBetweenOneAndTheLimit()
    {
        Assert.Equal((1, 1), ResizeRule.Percent(1).Apply(10, 20));
        Assert.Equal((20000, 50), ResizeRule.Width(30000).Apply(4000, 10));
        Assert.Equal((20000, 20000), ResizeRule.Exact(30000, 25000).Apply(100, 100));
        Assert.Equal((1, 1), ResizeRule.Exact(0, -5).Apply(100, 100));
    }

    [Fact]
    public void ResultNameCarriesTheSize() => Assert.Equal("photo (1920×1440)", ResizeRule.ResultName("photo", 1920, 1440));
}
