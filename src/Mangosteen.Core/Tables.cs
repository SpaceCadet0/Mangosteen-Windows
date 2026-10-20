using System.Globalization;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace Mangosteen.Core;

/// <summary>
/// CSV, TSV (RFC 4180 quoting) and XLSX. An XLSX source contributes its first non-empty sheet; a workbook with
/// several sheets becomes a folder with one table per sheet (see <see cref="Converter"/>).
/// </summary>
public static class Tables
{
    public sealed record Sheet(string Name, List<List<string>> Rows);

    public static void Convert(string input, FileFormat source, FileFormat target, string output)
    {
        var rows = source == FileFormat.Xlsx
            ? ReadXlsx(input).FirstOrDefault(s => s.Rows.Count > 0)?.Rows ?? []
            : Parse(TextFile.Read(input), Separator(source));
        Write(rows, target, OutputNaming.BaseName(input), output);
    }

    public static void Write(List<List<string>> rows, FileFormat target, string sheetName, string output)
    {
        if (target == FileFormat.Xlsx)
        {
            WriteXlsx(rows, sheetName, output);
        }
        else
        {
            // Excel opens a CSV without a byte order mark in the ANSI code page, which garbles Cyrillic.
            File.WriteAllText(output, Serialize(rows, Separator(target)), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }
    }

    /// <summary>One file per sheet inside <paramref name="folder"/>.</summary>
    public static void WriteSheets(IEnumerable<Sheet> sheets, FileFormat target, string folder)
    {
        Directory.CreateDirectory(folder);
        foreach (var sheet in sheets)
        {
            var name = string.Concat(sheet.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is ':' or '/' or '\\' ? '-' : c)).Trim();
            if (name.Length == 0 || name.StartsWith('.'))
            {
                name = "_" + name;
            }
            Write(sheet.Rows, target, sheet.Name, OutputNaming.UniquePath(folder, name, target.Extension()));
        }
    }

    public static char Separator(FileFormat format) => format == FileFormat.Tsv ? '\t' : ',';

    public static List<List<string>> Parse(string text, char separator)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var fieldStarted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(c);
                }
                continue;
            }
            if (c == '"' && field.Length == 0)
            {
                inQuotes = true;
                fieldStarted = true;
            }
            else if (c == separator)
            {
                row.Add(field.ToString());
                field.Clear();
                fieldStarted = true;
            }
            else if (c is '\n' or '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }
                row.Add(field.ToString());
                rows.Add(row);
                row = [];
                field.Clear();
                fieldStarted = false;
            }
            else
            {
                field.Append(c);
                fieldStarted = true;
            }
        }
        if (fieldStarted || field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }
        return rows;
    }

    public static string Serialize(IEnumerable<IReadOnlyList<string>> rows, char separator)
    {
        var builder = new StringBuilder();
        foreach (var row in rows)
        {
            builder.AppendJoin(separator, row.Select(field =>
                field.Contains(separator) || field.Contains('"') || field.Contains('\n') || field.Contains('\r')
                    ? "\"" + field.Replace("\"", "\"\"") + "\""
                    : field));
            builder.Append('\n');
        }
        return builder.ToString();
    }

    // MARK: XLSX

    public static List<Sheet> ReadXlsx(string path)
    {
        using var document = SpreadsheetDocument.Open(path, isEditable: false);
        var workbook = document.WorkbookPart ?? throw new InvalidDataException("The workbook has no sheets.");
        var shared = workbook.SharedStringTablePart?.SharedStringTable?.Elements<SharedStringItem>().Select(s => s.InnerText).ToList() ?? [];
        var dateStyles = DateStyleIndexes(workbook);
        var sheets = new List<Sheet>();
        foreach (var sheet in workbook.Workbook?.Sheets?.Elements<DocumentFormat.OpenXml.Spreadsheet.Sheet>() ?? [])
        {
            if (sheet.Id?.Value is not { } id || workbook.GetPartById(id) is not WorksheetPart { Worksheet: { } worksheet })
            {
                continue;
            }
            var rows = new List<List<string>>();
            foreach (var row in worksheet.Descendants<Row>())
            {
                var rowIndex = (int)(row.RowIndex?.Value ?? (uint)(rows.Count + 1)) - 1;
                while (rows.Count < rowIndex)
                {
                    rows.Add([]);
                }
                var values = new List<string>();
                foreach (var cell in row.Elements<Cell>())
                {
                    var column = cell.CellReference?.Value is { } reference ? ColumnIndex(reference) : values.Count;
                    while (values.Count < column)
                    {
                        values.Add("");
                    }
                    values.Add(CellText(cell, shared, dateStyles));
                }
                while (values.Count > 0 && values[^1].Length == 0)
                {
                    values.RemoveAt(values.Count - 1);
                }
                rows.Add(values);
            }
            while (rows.Count > 0 && rows[^1].Count == 0)
            {
                rows.RemoveAt(rows.Count - 1);
            }
            // Every row gets the same number of columns, so CSV readers line them up.
            var width = rows.Count == 0 ? 0 : rows.Max(r => r.Count);
            foreach (var r in rows)
            {
                while (r.Count < width)
                {
                    r.Add("");
                }
            }
            sheets.Add(new Sheet(sheet.Name?.Value ?? $"Sheet{sheets.Count + 1}", rows));
        }
        return sheets;
    }

    public static void WriteXlsx(IReadOnlyList<IReadOnlyList<string>> rows, string sheetName, string path)
    {
        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbook = document.AddWorkbookPart();
        workbook.Workbook = new Workbook();
        var worksheet = workbook.AddNewPart<WorksheetPart>();
        var data = new SheetData();
        worksheet.Worksheet = new Worksheet(data);
        for (var r = 0; r < rows.Count; r++)
        {
            var row = new Row { RowIndex = (uint)(r + 1) };
            for (var c = 0; c < rows[r].Count; c++)
            {
                var value = rows[r][c];
                if (value.Length == 0)
                {
                    continue;
                }
                var cell = new Cell { CellReference = ColumnName(c) + (r + 1) };
                if (IsPlainNumber(value))
                {
                    cell.DataType = CellValues.Number;
                    cell.CellValue = new CellValue(value);
                }
                else
                {
                    cell.DataType = CellValues.InlineString;
                    cell.InlineString = new InlineString(new Text(value) { Space = SpaceProcessingModeValues.Preserve });
                }
                row.Append(cell);
            }
            data.Append(row);
        }
        var sheets = workbook.Workbook.AppendChild(new Sheets());
        sheets.Append(new DocumentFormat.OpenXml.Spreadsheet.Sheet
        {
            Id = workbook.GetIdOfPart(worksheet), SheetId = 1, Name = SheetName(sheetName),
        });
        workbook.Workbook.Save();
    }

    /// <summary>Numbers stay numbers in Excel; anything that would change when stored as one stays text.</summary>
    private static bool IsPlainNumber(string value) =>
        value.Length < 16 && System.Text.RegularExpressions.Regex.IsMatch(value, @"^-?(0|[1-9][0-9]*)(\.[0-9]+)?$");

    private static string SheetName(string name)
    {
        var clean = string.Concat(name.Where(c => c is not ('[' or ']' or ':' or '*' or '?' or '/' or '\\'))).Trim('\'').Trim();
        if (clean.Length == 0)
        {
            clean = "Sheet1";
        }
        return clean.Length > 31 ? clean[..31] : clean;
    }

    private static string CellText(Cell cell, List<string> shared, HashSet<uint> dateStyles)
    {
        var raw = cell.CellValue?.Text ?? "";
        var type = cell.DataType?.Value;
        if (type == CellValues.SharedString)
        {
            return int.TryParse(raw, out var index) && index >= 0 && index < shared.Count ? shared[index] : "";
        }
        if (type == CellValues.InlineString)
        {
            return cell.InlineString?.InnerText ?? "";
        }
        if (type == CellValues.Boolean)
        {
            return raw == "1" ? "TRUE" : "FALSE";
        }
        if (type != CellValues.String && type != CellValues.Error
            && cell.StyleIndex?.Value is { } style && dateStyles.Contains(style)
            && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial)
            && serial is > 0 and < 2958466)
        {
            var date = DateTime.FromOADate(serial);
            return date.TimeOfDay == TimeSpan.Zero ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : date.ToString(serial < 1 ? "HH:mm:ss" : "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }
        return raw;
    }

    /// <summary>Cell styles whose number format shows a date or time.</summary>
    private static HashSet<uint> DateStyleIndexes(WorkbookPart workbook)
    {
        var result = new HashSet<uint>();
        var stylesheet = workbook.WorkbookStylesPart?.Stylesheet;
        if (stylesheet?.CellFormats is null)
        {
            return result;
        }
        var custom = stylesheet.NumberingFormats?.Elements<NumberingFormat>()
            .Where(f => f.NumberFormatId?.Value is not null)
            .ToDictionary(f => f.NumberFormatId!.Value, f => f.FormatCode?.Value ?? "") ?? [];
        uint index = 0;
        foreach (var format in stylesheet.CellFormats.Elements<CellFormat>())
        {
            var id = format.NumberFormatId?.Value ?? 0;
            var isDate = id is >= 14 and <= 22 or >= 45 and <= 47
                || (custom.TryGetValue(id, out var code) && IsDateFormat(code));
            if (isDate)
            {
                result.Add(index);
            }
            index++;
        }
        return result;
    }

    private static bool IsDateFormat(string code)
    {
        // Drop quoted text and [colour]/[locale] sections, then look for date or time letters.
        var plain = System.Text.RegularExpressions.Regex.Replace(code, "\"[^\"]*\"|\\[[^\\]]*\\]|\\\\.", "");
        return plain.IndexOfAny(['y', 'Y', 'd', 'D', 'h', 'H', 's', 'S']) >= 0
            || (plain.Contains('m', StringComparison.OrdinalIgnoreCase) && !plain.Contains('0') && !plain.Contains('#'));
    }

    public static int ColumnIndex(string reference)
    {
        var index = 0;
        foreach (var c in reference)
        {
            if (!char.IsLetter(c))
            {
                break;
            }
            index = index * 26 + (char.ToUpperInvariant(c) - 'A' + 1);
        }
        return index - 1;
    }

    public static string ColumnName(int index)
    {
        var name = "";
        for (index++; index > 0; index = (index - 1) / 26)
        {
            name = (char)('A' + (index - 1) % 26) + name;
        }
        return name;
    }
}
