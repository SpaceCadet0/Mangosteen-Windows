using System.Globalization;
using System.Text.RegularExpressions;

namespace Mangosteen.Core;

/// <summary>SRT, WebVTT and ASS/SSA into each other, and any of them into plain text.</summary>
public static partial class Subtitles
{
    public sealed record Cue(double Start, double End, string Text);

    public static void Convert(string input, FileFormat target, string output)
    {
        var source = TextFile.Read(input);
        var cues = FileFormats.Detect(input) == FileFormat.Ass ? ParseAss(source) : Parse(source);
        if (cues.Count == 0)
        {
            throw new InvalidDataException($"No subtitles found in {Path.GetFileName(input)}.");
        }
        var text = target switch
        {
            FileFormat.Srt => Srt(cues),
            FileFormat.Vtt => Vtt(cues),
            FileFormat.Ass => Ass(cues),
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

    /// <summary>
    /// The <c>Dialogue:</c> lines of the <c>[Events]</c> section. Their fields are named by the section's <c>Format:</c>
    /// line; the text is the last field and may itself contain commas. Override blocks are dropped except italics and bold.
    /// </summary>
    public static List<Cue> ParseAss(string text)
    {
        var inEvents = false;
        string[] fields = [];
        var cues = new List<Cue>();
        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = raw.Trim().TrimStart('﻿');
            if (line.StartsWith('['))
            {
                inEvents = line.Equals("[events]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            var colon = line.IndexOf(':');
            if (!inEvents || colon < 0)
            {
                continue;
            }
            var kind = line[..colon].Trim().ToLowerInvariant();
            var value = line[(colon + 1)..];
            if (kind == "format")
            {
                fields = value.Split(',').Select(f => f.Trim().ToLowerInvariant()).ToArray();
                continue;
            }
            if (kind != "dialogue" || fields.Length == 0)
            {
                continue;
            }
            var values = value.Split(',', fields.Length).Select(v => v.Trim()).ToArray();
            var start = Array.IndexOf(fields, "start");
            var end = Array.IndexOf(fields, "end");
            var body = Array.IndexOf(fields, "text");
            if (values.Length != fields.Length || start < 0 || end < 0 || body < 0
                || Timestamp(values[start]) is not { } from || Timestamp(values[end]) is not { } to)
            {
                continue;
            }
            var cueText = AssToSrtText(values[body]);
            if (cueText.Trim().Length > 0)
            {
                cues.Add(new Cue(from, to, cueText));
            }
        }
        return cues.OrderBy(c => c.Start).ToList();
    }

    /// <summary><c>{\i1}Hi{\i0}\Nthere</c> → <c>&lt;i&gt;Hi&lt;/i&gt;</c> and a new line; other override blocks are dropped.</summary>
    public static string AssToSrtText(string text)
    {
        var result = text.Replace("\\N", "\n").Replace("\\n", "\n").Replace("\\h", " ");
        var tags = new[] { ("\\i1", "<i>"), ("\\i0", "</i>"), ("\\b1", "<b>"), ("\\b0", "</b>") };
        var output = new System.Text.StringBuilder();
        for (var i = 0; i < result.Length; i++)
        {
            var close = result[i] == '{' ? result.IndexOf('}', i) : -1;
            if (close > i)
            {
                var block = result[(i + 1)..close];
                foreach (var (tag, html) in tags)
                {
                    if (block.Contains(tag, StringComparison.Ordinal))
                    {
                        output.Append(html);
                    }
                }
                i = close;
            }
            else
            {
                output.Append(result[i]);
            }
        }
        return output.ToString();
    }

    /// <summary>One "Default" style and a <c>Dialogue:</c> line per cue.</summary>
    public static string Ass(IReadOnlyList<Cue> cues)
    {
        const string header = """
            [Script Info]
            ScriptType: v4.00+
            PlayResX: 384
            PlayResY: 288
            WrapStyle: 0

            [V4+ Styles]
            Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
            Style: Default,Arial,20,&H00FFFFFF,&H000000FF,&H00000000,&H80000000,0,0,0,0,100,100,0,0,1,2,1,2,10,10,10,1

            [Events]
            Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
            """;
        var lines = cues.Select(cue =>
        {
            var text = cue.Text.Replace("<i>", "{\\i1}").Replace("</i>", "{\\i0}").Replace("<b>", "{\\b1}").Replace("</b>", "{\\b0}");
            text = HtmlTag().Replace(text, "").Replace("\r\n", "\n").Replace("\n", "\\N");
            return $"Dialogue: 0,{AssTime(cue.Start)},{AssTime(cue.End)},Default,,0,0,0,,{text}";
        });
        return string.Join("\n", lines.Prepend(header));
    }

    /// <summary><c>H:MM:SS.cc</c>, as ASS writes it.</summary>
    public static string AssTime(double time)
    {
        var cs = (long)Math.Round(Math.Max(time, 0) * 100);
        return $"{cs / 360_000}:{cs / 6000 % 60:00}:{cs / 100 % 60:00}.{cs % 100:00}";
    }

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex HtmlTag();

    public static string StripTags(string text) => TagPattern().Replace(text, "");

    [GeneratedRegex(@"<[^>]+>|\{\\[^}]*\}")]
    private static partial Regex TagPattern();
}
