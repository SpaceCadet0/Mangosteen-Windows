namespace Mangosteen.Core.Imaging;

public readonly record struct Box(double X, double Y, double Width, double Height);

/// <summary>Canvas proportions shared by Background and Collage.</summary>
public enum CanvasAspect
{
    Auto, Square, R4x5, R5x4, R4x3, R3x4, R3x2, R2x3, R16x9, R9x16,
}

public static class CanvasAspects
{
    public static string Title(this CanvasAspect aspect) => aspect switch
    {
        CanvasAspect.Auto => Loc.L("Auto"),
        CanvasAspect.Square => "1:1",
        _ => aspect.ToString()[1..].Replace('x', ':'),
    };

    public static double? Ratio(this CanvasAspect aspect) => aspect switch
    {
        CanvasAspect.Square => 1,
        CanvasAspect.R4x5 => 4.0 / 5,
        CanvasAspect.R5x4 => 5.0 / 4,
        CanvasAspect.R4x3 => 4.0 / 3,
        CanvasAspect.R3x4 => 3.0 / 4,
        CanvasAspect.R3x2 => 3.0 / 2,
        CanvasAspect.R2x3 => 2.0 / 3,
        CanvasAspect.R16x9 => 16.0 / 9,
        CanvasAspect.R9x16 => 9.0 / 16,
        _ => null,
    };
}

public enum CollageLayout
{
    Grid, Row, Column, Featured,
}

public sealed record CollageSettings
{
    public CollageLayout Layout { get; init; } = CollageLayout.Grid;
    /// <summary>Grid columns; 0 picks a near-square grid.</summary>
    public int Columns { get; init; }
    public CanvasAspect Aspect { get; init; } = CanvasAspect.Auto;
    /// <summary>Output width in pixels.</summary>
    public int Width { get; init; } = 3000;
    /// <summary>Gap between and around photos, as a fraction of the width.</summary>
    public double Spacing { get; init; } = 0.015;
    /// <summary>Corner radius as a fraction of each cell's shorter side.</summary>
    public double CornerRadius { get; init; }
}

/// <summary>
/// Arranges photos on one canvas, exactly like the macOS version: cells are filled edge to edge (cropping when the
/// proportions differ), except Row and Column with automatic proportions, which keep every photo whole.
/// </summary>
public static class Collage
{
    /// <summary>Canvas size and cell rectangles (origin top-left) for photos with the given aspect ratios (width / height).</summary>
    public static (double Width, double Height, List<Box> Cells) Layout(IReadOnlyList<double> aspects, CollageSettings settings)
    {
        var count = aspects.Count;
        if (count == 0)
        {
            return (0, 0, []);
        }
        double width = Math.Max(settings.Width, 100);
        var gap = Math.Round(settings.Spacing * width);
        double? fixedHeight = settings.Aspect.Ratio() is { } ratio ? Math.Round(width / ratio) : null;

        switch (settings.Layout)
        {
            case CollageLayout.Row:
                if (fixedHeight is { } rowFixed)
                {
                    var cellWidth = (width - gap * (count + 1)) / count;
                    return (width, rowFixed, Enumerable.Range(0, count)
                        .Select(i => new Box(gap + i * (cellWidth + gap), gap, cellWidth, rowFixed - gap * 2)).ToList());
                }
                var rowHeight = (width - gap * (count + 1)) / aspects.Sum();
                var x = gap;
                var rowCells = new List<Box>();
                foreach (var aspect in aspects)
                {
                    rowCells.Add(new Box(x, gap, aspect * rowHeight, rowHeight));
                    x += aspect * rowHeight + gap;
                }
                return (width, Math.Round(rowHeight + gap * 2), rowCells);

            case CollageLayout.Column:
                var columnWidth = width - gap * 2;
                if (fixedHeight is { } columnFixed)
                {
                    var cellHeight = (columnFixed - gap * (count + 1)) / count;
                    return (width, columnFixed, Enumerable.Range(0, count)
                        .Select(i => new Box(gap, gap + i * (cellHeight + gap), columnWidth, cellHeight)).ToList());
                }
                var y = gap;
                var columnCells = new List<Box>();
                foreach (var aspect in aspects)
                {
                    var height = columnWidth / aspect;
                    columnCells.Add(new Box(gap, y, columnWidth, height));
                    y += height + gap;
                }
                return (width, Math.Round(y), columnCells);

            case CollageLayout.Featured when count > 1:
                var featuredHeight = fixedHeight ?? Math.Round(width * 3 / 4);
                var mainWidth = Math.Round((width - gap * 3) * 2 / 3);
                var sideWidth = width - gap * 3 - mainWidth;
                var others = count - 1;
                var sideHeight = (featuredHeight - gap * (others + 1)) / others;
                var featured = new List<Box> { new(gap, gap, mainWidth, featuredHeight - gap * 2) };
                for (var i = 0; i < others; i++)
                {
                    featured.Add(new Box(gap * 2 + mainWidth, gap + i * (sideHeight + gap), sideWidth, sideHeight));
                }
                return (width, featuredHeight, featured);

            default:
                var columns = settings.Columns > 0 ? Math.Min(settings.Columns, count) : (int)Math.Ceiling(Math.Sqrt(count));
                var rows = (int)Math.Ceiling((double)count / columns);
                var gridCellWidth = (width - gap * (columns + 1)) / columns;
                double gridHeight, gridCellHeight;
                if (fixedHeight is { } gridFixed)
                {
                    gridHeight = gridFixed;
                    gridCellHeight = (gridHeight - gap * (rows + 1)) / rows;
                }
                else
                {
                    gridCellHeight = gridCellWidth / (aspects.Sum() / count);
                    gridHeight = Math.Round(gridCellHeight * rows + gap * (rows + 1));
                }
                var grid = new List<Box>();
                for (var index = 0; index < count; index++)
                {
                    var row = index / columns;
                    var inRow = row == rows - 1 ? count - row * columns : columns;
                    // Centre a shorter last row.
                    var rowInset = (columns - inRow) * (gridCellWidth + gap) / 2;
                    grid.Add(new Box(rowInset + gap + index % columns * (gridCellWidth + gap), gap + row * (gridCellHeight + gap),
                        gridCellWidth, gridCellHeight));
                }
                return (width, gridHeight, grid);
        }
    }
}

public enum FrameBackground
{
    Color, Gradient, Image, Blur,
}

public sealed record FrameSettings
{
    public FrameBackground Background { get; init; } = FrameBackground.Gradient;
    public uint Color { get; init; } = 0xFFF5F2ED;
    public uint GradientStart { get; init; } = 0xFFFA9E5C;
    public uint GradientEnd { get; init; } = 0xFF8C45AD;
    /// <summary>Degrees, 0 = left to right, 90 = bottom to top.</summary>
    public double GradientAngle { get; init; } = 135;
    public string? BackgroundImage { get; init; }
    /// <summary>0…1, for the image and blur backgrounds.</summary>
    public double Blur { get; init; } = 0.5;
    public CanvasAspect Aspect { get; init; } = CanvasAspect.Auto;
    /// <summary>Margin around the photo as a fraction of its longer side.</summary>
    public double Padding { get; init; } = 0.08;
    /// <summary>Corner radius as a fraction of the photo's shorter side.</summary>
    public double CornerRadius { get; init; } = 0.03;
    /// <summary>0…1</summary>
    public double Shadow { get; init; } = 0.5;
}

/// <summary>Background: puts a photo on a new canvas with a margin, rounded corners and a shadow.</summary>
public static class Frame
{
    public static (double Width, double Height) CanvasSize(double photoWidth, double photoHeight, FrameSettings settings)
    {
        var margin = settings.Padding * Math.Max(photoWidth, photoHeight);
        var width = photoWidth + margin * 2;
        var height = photoHeight + margin * 2;
        if (settings.Aspect.Ratio() is { } ratio)
        {
            if (width / height < ratio)
            {
                width = height * ratio;
            }
            else
            {
                height = width / ratio;
            }
        }
        return (Math.Round(width), Math.Round(height));
    }
}
