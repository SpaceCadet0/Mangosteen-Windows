using System.Globalization;
using System.Text.RegularExpressions;

namespace Mangosteen.Core;

/// <summary>SRT and WebVTT into each other, and either into plain text.</summary>
public static partial class Subtitles
{
    public sealed record Cue(double Start, double End, string Text);

    public static void Convert(string input, FileFormat target, string output)
    {
        var cues = Parse(TextFile.Read(input));
        if (cues.Count == 0)
        {
            throw new InvalidDataException($"No subtitles found in {Path.GetFileName(input)}.");
        }
        var text = target switch
        {
            FileFormat.Srt => Srt(cues),
            FileFormat.Vtt => Vtt(cues),
            FileFormat.Txt => string.Join("\n", cues.Select(c => StripTags(c.Text))),
            _ => throw new NotSupportedException($"Subtitles cannot be converted to {target.DisplayName()}."),
        };
        TextFile.Write(output, text + "\n");
    }

    /// <summary>Parses both SRT and VTT: blocks separated by blank lines, each with a <c>start --&gt; end</c> line.</summary>
    public static List<Cue> Parse(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var cues = new List<Cue>();
        foreach (var block in normalized.Split("\n\n"))
        {
            var lines = block.Split('\n');
            var timing = Array.FindIndex(lines, l => l.Contains("-->"));
            if (timing < 0)
            {
                continue;
            }
            var parts = lines[timing].Split("-->");
            if (parts.Length != 2 || Timestamp(parts[0]) is not { } start
                || Timestamp(parts[1].Trim().Split(' ')[0]) is not { } end)
            {
                continue;
            }
            var body = string.Join("\n", lines.Skip(timing + 1).Where(l => !string.IsNullOrWhiteSpace(l)));
            if (body.Length > 0)
            {
                cues.Add(new Cue(start, end, body));
            }
        }
        return cues;
    }

    /// <summary>Accepts <c>HH:MM:SS,mmm</c>, <c>HH:MM:SS.mmm</c> and <c>MM:SS.mmm</c>.</summary>
    public static double? Timestamp(string raw)
    {
        var fields = raw.Trim().Replace(',', '.').Split(':');
        if (fields.Length is < 2 or > 3)
        {
            return null;
        }
        double seconds = 0;
        foreach (var field in fields)
        {
            if (!double.TryParse(field, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            {
                return null;
            }
            seconds = seconds * 60 + number;
        }
        return seconds;
    }

    public static string Format(double time, char decimalSeparator)
    {
        var ms = (long)Math.Round(time * 1000);
        return $"{ms / 3_600_000:00}:{ms / 60_000 % 60:00}:{ms / 1000 % 60:00}{decimalSeparator}{ms % 1000:000}";
    }

    public static string Srt(IReadOnlyList<Cue> cues) => string.Join("\n\n",
        cues.Select((c, i) => $"{i + 1}\n{Format(c.Start, ',')} --> {Format(c.End, ',')}\n{c.Text}"));

    public static string Vtt(IReadOnlyList<Cue> cues) => "WEBVTT\n\n" + string.Join("\n\n",
        cues.Select(c => $"{Format(c.Start, '.')} --> {Format(c.End, '.')}\n{c.Text}"));

    public static string StripTags(string text) => TagPattern().Replace(text, "");

    [GeneratedRegex(@"<[^>]+>|\{\\[^}]*\}")]
    private static partial Regex TagPattern();
}
