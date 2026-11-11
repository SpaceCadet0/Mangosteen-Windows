using Mangosteen.Core.Documents;
using Mangosteen.Core.Imaging;

namespace Mangosteen.Core.Tests;

public class TracerTests
{
    /// <summary>A white picture with filled rectangles of the given colours (BGRA).</summary>
    private static VectorTracer.Pixels Picture(int width, int height, params (int X, int Y, int W, int H, byte R, byte G, byte B, byte A)[] boxes)
    {
        var bytes = new byte[width * height * 4];
        for (var i = 0; i < width * height; i++)
        {
            bytes[i * 4] = bytes[i * 4 + 1] = bytes[i * 4 + 2] = bytes[i * 4 + 3] = 255;
        }
        foreach (var (x0, y0, w, h, r, g, b, a) in boxes)
        {
            for (var y = y0; y < y0 + h; y++)
            {
                for (var x = x0; x < x0 + w; x++)
                {
                    var i = (y * width + x) * 4;
                    (bytes[i], bytes[i + 1], bytes[i + 2], bytes[i + 3]) = (b, g, r, a);
                }
            }
        }
        return new VectorTracer.Pixels(bytes, width, height);
    }

    [Fact]
    public void BlackSquareBecomesPaperAndOneSquare()
    {
        var result = VectorTracer.Trace(Picture(100, 100, (20, 20, 60, 60, 0, 0, 0, 255)), new VectorTracer.Options(Smooth: false));
        Assert.Equal(2, result.Layers.Count);
        Assert.Equal("#ffffff", result.Layers[0].Color.Hex);
        Assert.Equal("#000000", result.Layers[1].Color.Hex);
        // The square's corners come back square: no more than its four corners after simplifying and sharpening.
        Assert.InRange(result.Layers[1].PathData.Count(c => c is 'M' or 'L'), 4, 6);
        var svg = VectorTracer.Svg(result);
        Assert.Contains("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100\" height=\"100\"", svg);
        Assert.Contains("fill-rule=\"evenodd\"", svg);
    }

    [Fact]
    public void NoBackgroundDrawsOnlyTheInk()
    {
        var result = VectorTracer.Trace(Picture(80, 80, (10, 10, 30, 30, 0, 0, 0, 255)), new VectorTracer.Options(DropBackground: true));
        Assert.Single(result.Layers);
        Assert.Equal("#000000", result.Layers[0].Color.Hex);
    }

    [Fact]
    public void TransparentBackgroundIsPaper()
    {
        // A logo saved without a background: dark ink on fully transparent pixels.
        var picture = Picture(60, 60, (0, 0, 60, 60, 0, 0, 0, 0), (15, 15, 30, 30, 20, 20, 20, 255));
        var result = VectorTracer.Trace(picture, new VectorTracer.Options(DropBackground: true));
        Assert.Single(result.Layers);
    }

    [Fact]
    public void ColoursKeepRedApartFromBlack()
    {
        var picture = Picture(120, 60, (0, 0, 40, 60, 0, 0, 0, 255), (40, 0, 40, 60, 220, 20, 30, 255));
        var palette = VectorTracer.Palette(picture, 3);
        Assert.Equal(3, palette.Count);
        Assert.Contains(palette, c => c.R > 0.7 && c.G < 0.2);
        var result = VectorTracer.Trace(picture, new VectorTracer.Options(Colors: 3));
        Assert.Equal(3, result.Layers.Count);
    }

    [Fact]
    public void ScaleGivesTheOriginalSize()
    {
        var result = VectorTracer.Trace(Picture(50, 40, (10, 10, 20, 20, 0, 0, 0, 255)), new VectorTracer.Options(), scale: 4);
        Assert.Equal((200, 160), (result.Width, result.Height));
    }

    [Fact]
    public void SimplifyKeepsCornersOfAStaircaseSquare()
    {
        var points = new List<(double X, double Y)>();
        for (var i = 0; i < 10; i++) points.Add((i, 0));
        for (var i = 0; i < 10; i++) points.Add((10, i));
        for (var i = 10; i > 0; i--) points.Add((i, 10));
        for (var i = 10; i > 0; i--) points.Add((0, i));
        Assert.Equal(4, VectorTracer.Simplify(points, 0.5).Count);
    }

    [Fact]
    public void VTracerOutputIsReadBack()
    {
        const string svg = """
            <svg><path d="M0 0 L10 0 L10 10 Z " fill="#ff0000" transform="translate(5,6)"/>
            <path d="M1 1 L2 2 Z" fill="#00f"/></svg>
            """;
        var layers = VectorTracer.ParseVTracer(svg, 2);
        Assert.Equal(2, layers.Count);
        Assert.Equal("#ff0000", layers[0].Color.Hex);
        Assert.Equal("scale(2) translate(5,6)", layers[0].Transform);
        Assert.False(layers[0].EvenOdd);
        Assert.Equal("#0000ff", layers[1].Color.Hex);
    }

    [Fact]
    public void VTracerGetsOurPalette()
    {
        var arguments = VectorTracer.VTracerArguments("in.png", "out.svg", new VectorTracer.Options(Colors: 2, Detail: 1),
            [new(1, 0, 0), new(0, 0, 0)]);
        Assert.Equal("#ff0000,#000000", arguments[arguments.IndexOf("--palette") + 1]);
        Assert.Equal("2", arguments[arguments.IndexOf("--filter-speckle") + 1]);
    }
}

public class PdfLayoutAndIconTests
{
    [Theory]
    [InlineData(2, 2, 1, true)]
    [InlineData(4, 2, 2, false)]
    [InlineData(6, 3, 2, true)]
    [InlineData(9, 3, 3, false)]
    public void GridsAndOrientation(int perSheet, int columns, int rows, bool landscape) =>
        Assert.Equal((columns, rows, landscape), PdfLayout.Grid(perSheet));

    [Fact]
    public void CellsFillTheSheetInsideTheMargin()
    {
        var (w, h) = PdfLayout.Sheet(595, 842, 2);
        Assert.Equal((842.0, 595.0), (w, h));
        var cells = PdfLayout.Cells(w, h, 2);
        Assert.Equal(2, cells.Count);
        Assert.Equal(18, cells[0].X, 3);
        Assert.Equal(w - 18, cells[1].X + cells[1].Width, 3);
        var page = PdfLayout.Fit(cells[0], 595, 842);
        Assert.True(page.Height <= cells[0].Height + 1e-9 && page.Width <= cells[0].Width + 1e-9);
        Assert.Equal(3, PdfLayout.SheetCount(9, 4));
    }

    [Fact]
    public void IconSizesStopAtThePictureSize()
    {
        Assert.Equal([16, 32, 48, 64, 128, 256], IcoFile.Sizes(1024));
        Assert.Equal([16, 32, 48], IcoFile.Sizes(50));
        Assert.Equal([16], IcoFile.Sizes(10));
        Assert.Equal((0, 4, 16, 8), IcoFile.Fit(200, 100, 16));
    }

    [Fact]
    public void IconHeaderPointsAtEachFrame()
    {
        var data = IcoFile.Write([(256, [1, 2, 3]), (16, [9, 9])]);
        Assert.Equal(1, BitConverter.ToUInt16(data, 2));
        Assert.Equal(2, BitConverter.ToUInt16(data, 4));
        Assert.Equal(16, data[6]);                       // smallest first
        Assert.Equal(0, data[6 + 16]);                   // 256 is written as 0
        Assert.Equal(6 + 32, (int)BitConverter.ToUInt32(data, 6 + 12));
        Assert.Equal(new byte[] { 9, 9 }, data[(6 + 32)..(6 + 34)]);
    }

    [Fact]
    public void TextOnTheAccentIsReadable()
    {
        Assert.True(Accent.TextIsWhite(Accent.Brand));
        Assert.False(Accent.TextIsWhite(new Rgb(0xF5, 0xE0, 0x60)));
        Assert.Null(Accent.Parse("#12345"));
        Assert.Equal("#1F5FBF", Accent.Hex(Accent.Parse("1f5fbf")!.Value));
        Assert.True(Accent.Contrast(Accent.Readable(Accent.Brand, new Rgb(0x2B, 0x2B, 0x2B)), new Rgb(0x2B, 0x2B, 0x2B)) >= 4.5);
    }
}
