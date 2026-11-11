using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mangosteen.Core;

/// <summary>
/// JSON ↔ table rows. Reading keeps the order of keys as they appear in the file, so columns come out in the author's
/// order. Nested objects become <c>a.b</c> columns; arrays inside a record are kept as JSON text. Writing makes one
/// flat object per row, keyed by the first row.
/// </summary>
public static partial class JsonTable
{
    /// <summary>The header row and one row per record of an array of objects (or the rows of an array of arrays).</summary>
    public static List<List<string>> Rows(string text)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("The file is not valid JSON.", error);
        }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
            {
                throw new InvalidDataException("The JSON is not a table: it needs an array of objects.");
            }
            var items = root.EnumerateArray().ToList();
            if (items.All(i => i.ValueKind == JsonValueKind.Array))
            {
                return items.Select(i => i.EnumerateArray().Select(CellText).ToList()).ToList();
            }
            if (!items.All(i => i.ValueKind == JsonValueKind.Object))
            {
                throw new InvalidDataException("The JSON is not a table: it needs an array of objects.");
            }
            var columns = new List<string>();
            var seen = new HashSet<string>();
            var records = items.Select(item =>
            {
                var record = new Dictionary<string, string>();
                Flatten(item, null, record, key =>
                {
                    if (seen.Add(key))
                    {
                        columns.Add(key);
                    }
                });
                return record;
            }).ToList();
            var rows = new List<List<string>> { columns.ToList() };
            rows.AddRange(records.Select(r => columns.Select(c => r.GetValueOrDefault(c, "")).ToList()));
            return rows;
        }
    }

    private static void Flatten(JsonElement value, string? prefix, Dictionary<string, string> record, Action<string> newColumn)
    {
        foreach (var property in value.EnumerateObject())
        {
            var name = prefix is null ? property.Name : $"{prefix}.{property.Name}";
            if (property.Value.ValueKind == JsonValueKind.Object && property.Value.EnumerateObject().Any())
            {
                Flatten(property.Value, name, record, newColumn);
            }
            else
            {
                newColumn(name);
                record[name] = CellText(property.Value);
            }
        }
    }

    /// <summary>The text of one cell: strings as they are, numbers as written, true/false, empty for null, JSON for the rest.</summary>
    public static string CellText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null or JsonValueKind.Undefined => "",
        _ => value.GetRawText(),
    };

    /// <summary>
    /// The first row names the keys. Plain decimal numbers and true/false become JSON numbers and booleans; text such
    /// as "007" stays a string, as does everything else.
    /// </summary>
    public static string Serialize(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        if (rows.Count == 0)
        {
            return "[]\n";
        }
        var keys = rows[0].Select((name, i) => name.Length == 0 ? $"column{i + 1}" : name).ToList();
        var objects = rows.Skip(1).Where(r => r.Any(c => c.Length > 0)).Select(row =>
        {
            var fields = keys.Select((key, i) =>
            {
                var cell = i < row.Count ? row[i] : "";
                var value = PlainNumber().IsMatch(cell) ? cell : cell is "true" or "false" ? cell : Quote(cell);
                return $"    {Quote(key)}: {value}";
            });
            return "  {\n" + string.Join(",\n", fields) + "\n  }";
        }).ToList();
        return objects.Count == 0 ? "[]\n" : "[\n" + string.Join(",\n", objects) + "\n]\n";
    }

    private static string Quote(string text)
    {
        var builder = new StringBuilder("\"");
        foreach (var c in text)
        {
            builder.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                < ' ' => $"\\u{(int)c:x4}",
                _ => c.ToString(),
            });
        }
        return builder.Append('"').ToString();
    }

    /// <summary>Numbers that read back the same: no leading zeros (so "007" stays text), not too long.</summary>
    [GeneratedRegex(@"^-?(0|[1-9][0-9]{0,14})(\.[0-9]+)?$")]
    private static partial Regex PlainNumber();
}
