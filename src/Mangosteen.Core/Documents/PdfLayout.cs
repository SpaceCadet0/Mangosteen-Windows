namespace Mangosteen.Core.Documents;

/// <summary>Several PDF pages on one sheet: where each page goes. Sizes are in points.</summary>
public static class PdfLayout
{
    public static readonly IReadOnlyList<int> Choices = [2, 4, 6, 9];

    public readonly record struct Box(double X, double Y, double Width, double Height);

    /// <summary>Columns and rows for <paramref name="perSheet"/> pages; 2 and 6 lie on a landscape sheet.</summary>
    public static (int Columns, int Rows, bool Landscape) Grid(int perSheet) => perSheet switch
    {
        2 => (2, 1, true),
        4 => (2, 2, false),
        6 => (3, 2, true),
        9 => (3, 3, false),
        _ => throw new ArgumentOutOfRangeException(nameof(perSheet), perSheet, "2, 4, 6 or 9 pages per sheet."),
    };

    /// <summary>The sheet: the size of the first page, turned to the grid's orientation.</summary>
    public static (double Width, double Height) Sheet(double pageWidth, double pageHeight, int perSheet)
    {
        var (shortSide, longSide) = (Math.Min(pageWidth, pageHeight), Math.Max(pageWidth, pageHeight));
        return Grid(perSheet).Landscape ? (longSide, shortSide) : (shortSide, longSide);
    }

    /// <summary>The cells, left to right and top to bottom, inside a margin and with a gap between them.</summary>
    public static List<Box> Cells(double sheetWidth, double sheetHeight, int perSheet, double margin = 18, double gap = 12)
    {
        var (columns, rows, _) = Grid(perSheet);
        var width = (sheetWidth - 2 * margin - (columns - 1) * gap) / columns;
        var height = (sheetHeight - 2 * margin - (rows - 1) * gap) / rows;
        var cells = new List<Box>();
        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                cells.Add(new Box(margin + column * (width + gap), margin + row * (height + gap), width, height));
            }
        }
        return cells;
    }

    /// <summary>A page of <paramref name="width"/>×<paramref name="height"/> scaled to fit <paramref name="cell"/>, centred.</summary>
    public static Box Fit(Box cell, double width, double height)
    {
        var scale = Math.Min(cell.Width / width, cell.Height / height);
        var (w, h) = (width * scale, height * scale);
        return new Box(cell.X + (cell.Width - w) / 2, cell.Y + (cell.Height - h) / 2, w, h);
    }

    /// <summary>How many sheets <paramref name="pageCount"/> pages make.</summary>
    public static int SheetCount(int pageCount, int perSheet) => (pageCount + perSheet - 1) / perSheet;
}
