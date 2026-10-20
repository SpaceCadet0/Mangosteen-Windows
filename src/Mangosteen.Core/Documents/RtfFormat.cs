using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Mangosteen.Core.Documents;

/// <summary>
/// Rich Text Format. The reader understands what text editors put in RTF: paragraphs, headings (outline levels),
/// bold, italic, underline, links, list markers, tables as tab-separated lines, Unicode and legacy code pages
/// (Cyrillic RTF from old Windows apps included). Pictures, headers and footnotes are skipped.
/// </summary>
public static partial class RtfFormat
{
    static RtfFormat() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    // MARK: Writing

    private static readonly int[] HeadingSizes = [36, 30, 26, 24, 22, 22]; // half-points

    public static string Write(RichDocument document)
    {
        var builder = new StringBuilder();
        builder.Append(@"{\rtf1\ansi\ansicpg1252\deff0\uc1")
            .Append(@"{\fonttbl{\f0\fswiss\fcharset0 Calibri;}{\f1\fmodern\fcharset0 Consolas;}}")
            .Append(@"{\colortbl;\red5\green99\blue193;}")
            .Append(@"\viewkind4\paperw11906\paperh16838\margl1134\margr1134\margt1134\margb1134")
            .Append('\n');
        var numbers = new ListNumbering();
        foreach (var block in document.Blocks)
        {
            builder.Append(@"\pard\plain\sa120\f0\fs22 ");
            switch (block.Type)
            {
                case BlockType.Heading:
                    numbers.Reset();
                    var level = Math.Clamp(block.Level, 1, 6);
                    builder.Append($@"\outlinelevel{level - 1}\sb240\keepn\b\fs{HeadingSizes[level - 1]} ");
                    break;
                case BlockType.ListItem:
                    var indent = 720 * (block.Level + 1);
                    builder.Append($@"\fi-360\li{indent}\sa60 ");
                    // The bullet is written as an RTF Unicode escape (backslash, "u8226", "?").
                    builder.Append(block.Ordered ? $@"{numbers.Next(block)}.\tab " : "\\" + "u8226?" + @"\tab ");
                    break;
                case BlockType.Code:
                    numbers.Reset();
                    builder.Append(@"\sa0\f1\fs20 ");
                    break;
                default:
                    numbers.Reset();
                    break;
            }
            foreach (var run in block.Runs)
            {
                var text = Escape(run.Text);
                var style = new StringBuilder();
                if (run.Bold)
                {
                    style.Append(@"\b");
                }
                if (run.Italic)
                {
                    style.Append(@"\i");
                }
                if (run.Underline)
                {
                    style.Append(@"\ul");
                }
                if (run.Code && block.Type != BlockType.Code)
                {
                    style.Append(@"\f1");
                }
                var styled = style.Length > 0 ? "{" + style + " " + text + "}" : text;
                if (run.Link is { } link)
                {
                    styled = @"{\field{\*\fldinst{HYPERLINK """ + Escape(link.Replace("\"", "%22")) + @"""}}{\fldrslt{\ul\cf1 " + styled + "}}}";
                }
                builder.Append(styled);
            }
            builder.Append(@"\par").Append('\n');
        }
        return builder.Append('}').ToString();
    }

    private static string Escape(string text)
    {
        var builder = new StringBuilder();
        foreach (var c in text)
        {
            switch (c)
            {
                case '\\' or '{' or '}':
                    builder.Append('\\').Append(c);
                    break;
                case '\n':
                    builder.Append(@"\line ");
                    break;
                case '\t':
                    builder.Append(@"\tab ");
                    break;
                case < (char)0x80:
                    builder.Append(c);
                    break;
                default:
                    builder.Append(@"\u").Append(((short)c).ToString(CultureInfo.InvariantCulture)).Append('?');
                    break;
            }
        }
        return builder.ToString();
    }

    // MARK: Reading

    private enum Destination
    {
        Text,
        Skip,
        FontTable,
        FontEntry,
        FieldInstruction,
        FieldResult,
        ListText,
    }

    private sealed class State
    {
        public bool Bold;
        public bool Italic;
        public bool Underline;
        public int Font = -1;
        public int UnicodeSkip = 1;
        public Destination Destination;
        public string? Link;
        public bool IsField;

        public State Clone() => (State)MemberwiseClone();
    }

    private sealed record FontInfo(int CodePage, bool Monospace);

    public static RichDocument Parse(byte[] data) => Parse(Encoding.Latin1.GetString(data));

    public static RichDocument Parse(string rtf)
    {
        if (!rtf.StartsWith(@"{\rtf", StringComparison.Ordinal))
        {
            throw new InvalidDataException("This is not an RTF document.");
        }
        var document = new RichDocument();
        var fonts = new Dictionary<int, FontInfo>();
        var defaultCodePage = 1252;
        var defaultFont = 0;
        var stack = new Stack<State>();
        var state = new State();
        var bytes = new List<byte>();
        var fieldInstruction = new StringBuilder();
        var listText = new StringBuilder();
        var fontName = new StringBuilder();
        var fontNumber = 0;
        var fontCharset = -1;

        // Paragraph properties
        var outline = -1;
        var leftIndent = 0;
        var listLevel = -1;
        var block = new Block(BlockType.Paragraph);
        var paragraphHasMonoOnly = true;
        var pendingSkip = 0;

        int CodePage(int font) => fonts.TryGetValue(font < 0 ? defaultFont : font, out var info) && info.CodePage > 0 ? info.CodePage : defaultCodePage;

        void Emit(string text)
        {
            if (text.Length == 0)
            {
                return;
            }
            switch (state.Destination)
            {
                case Destination.Text:
                case Destination.FieldResult:
                    var mono = fonts.TryGetValue(state.Font < 0 ? defaultFont : state.Font, out var f) && f.Monospace;
                    paragraphHasMonoOnly &= mono || text.Trim().Length == 0;
                    block.Add(new Run(text, state.Bold, state.Italic, state.Underline && state.Link is null, state.Link, mono));
                    break;
                case Destination.FieldInstruction:
                    fieldInstruction.Append(text);
                    break;
                case Destination.ListText:
                    listText.Append(text);
                    break;
                case Destination.FontEntry:
                    fontName.Append(text);
                    break;
            }
        }

        void FlushBytes()
        {
            if (bytes.Count == 0)
            {
                return;
            }
            Encoding encoding;
            try
            {
                encoding = Encoding.GetEncoding(CodePage(state.Font));
            }
            catch (Exception error) when (error is ArgumentException or NotSupportedException)
            {
                encoding = Encoding.Latin1;
            }
            Emit(encoding.GetString(bytes.ToArray()));
            bytes.Clear();
        }

        void EndParagraph()
        {
            FlushBytes();
            var text = block.Text;
            if (outline is >= 0 and < 6)
            {
                block.Type = BlockType.Heading;
                block.Level = outline + 1;
            }
            else if (listLevel >= 0 || listText.Length > 0 || ListMarker().IsMatch(text))
            {
                var marker = listText.Length > 0 ? listText.ToString().Trim() : ListMarker().Match(text).Value.Trim();
                block.Type = BlockType.ListItem;
                block.Ordered = marker.Length > 0 && char.IsDigit(marker[0]);
                block.Level = listLevel >= 0 ? listLevel : Math.Max(0, leftIndent / 720 - 1);
                if (listText.Length == 0)
                {
                    StripLeading(block, ListMarker().Match(text).Length);
                }
            }
            else if (paragraphHasMonoOnly && text.Trim().Length > 0)
            {
                block.Type = BlockType.Code;
                for (var i = 0; i < block.Runs.Count; i++)
                {
                    block.Runs[i] = block.Runs[i] with { Code = false };
                }
            }
            document.Blocks.Add(block);
            block = new Block(BlockType.Paragraph);
            listText.Clear();
            paragraphHasMonoOnly = true;
        }

        var i = 0;
        while (i < rtf.Length)
        {
            var c = rtf[i];
            if (c == '{')
            {
                FlushBytes();
                stack.Push(state);
                state = state.Clone();
                state.IsField = false;
                if (state.Destination == Destination.FontTable)
                {
                    state.Destination = Destination.FontEntry;
                    fontName.Clear();
                    fontNumber = 0;
                    fontCharset = -1;
                }
                i++;
                continue;
            }
            if (c == '}')
            {
                FlushBytes();
                if (state.Destination == Destination.FontEntry && stack.Count > 0 && stack.Peek().Destination == Destination.FontTable)
                {
                    var name = fontName.ToString().TrimEnd(';', ' ');
                    var mono = name.Contains("Courier", StringComparison.OrdinalIgnoreCase) || name.Contains("Consolas", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("Mono", StringComparison.OrdinalIgnoreCase) || name.Contains("Menlo", StringComparison.OrdinalIgnoreCase);
                    fonts[fontNumber] = new FontInfo(CharsetCodePage(fontCharset), mono);
                }
                if (state.Destination == Destination.FieldInstruction && stack.Count > 0
                    && stack.Peek().Destination != Destination.FieldInstruction)
                {
                    // The instruction belongs to the enclosing \field group; its result group takes the link.
                    var url = HyperlinkPattern().Match(fieldInstruction.ToString());
                    var field = stack.Peek();
                    field.Link = url.Success ? url.Groups[1].Value : null;
                    fieldInstruction.Clear();
                }
                state = stack.Count > 0 ? stack.Pop() : state;
                i++;
                if (stack.Count == 0)
                {
                    break;
                }
                continue;
            }
            if (c is '\r' or '\n')
            {
                i++;
                continue;
            }
            if (c != '\\')
            {
                if (pendingSkip > 0)
                {
                    pendingSkip--;
                    i++;
                    continue;
                }
                FlushBytes();
                if (state.Destination == Destination.FontEntry || state.Destination == Destination.Text
                    || state.Destination == Destination.FieldResult || state.Destination == Destination.FieldInstruction
                    || state.Destination == Destination.ListText)
                {
                    var start = i;
                    while (i < rtf.Length && rtf[i] is not ('\\' or '{' or '}' or '\r' or '\n'))
                    {
                        i++;
                    }
                    Emit(rtf[start..i]);
                    continue;
                }
                i++;
                continue;
            }

            // Control symbol or word
            if (i + 1 >= rtf.Length)
            {
                break;
            }
            var next = rtf[i + 1];
            if (next == '\'')
            {
                if (i + 3 < rtf.Length && byte.TryParse(rtf.AsSpan(i + 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
                {
                    if (pendingSkip > 0)
                    {
                        pendingSkip--;
                    }
                    else if (state.Destination != Destination.Skip && state.Destination != Destination.FontTable)
                    {
                        bytes.Add(b);
                    }
                }
                i += 4;
                continue;
            }
            if (!char.IsAsciiLetter(next))
            {
                FlushBytes();
                i += 2;
                if (pendingSkip > 0 && next is '\\' or '{' or '}')
                {
                    pendingSkip--;
                    continue;
                }
                switch (next)
                {
                    case '\\' or '{' or '}':
                        Emit(next.ToString());
                        break;
                    case '~':
                        Emit(" ");
                        break;
                    case '_':
                        Emit("-");
                        break;
                    case '*':
                        state.Destination = Destination.Skip;
                        // \*\fldinst is the one starred destination worth reading.
                        if (rtf.AsSpan(i).StartsWith(@"\fldinst", StringComparison.Ordinal))
                        {
                            state.Destination = Destination.FieldInstruction;
                        }
                        break;
                    case '\r' or '\n':
                        if (state.Destination == Destination.Text)
                        {
                            EndParagraph();
                        }
                        break;
                }
                continue;
            }
            var wordStart = i + 1;
            var j = wordStart;
            while (j < rtf.Length && char.IsAsciiLetter(rtf[j]))
            {
                j++;
            }
            var word = rtf[wordStart..j];
            int? parameter = null;
            var numberStart = j;
            if (j < rtf.Length && (rtf[j] == '-' || char.IsAsciiDigit(rtf[j])))
            {
                j++;
                while (j < rtf.Length && char.IsAsciiDigit(rtf[j]))
                {
                    j++;
                }
                if (int.TryParse(rtf.AsSpan(numberStart, j - numberStart), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
                {
                    parameter = value;
                }
            }
            if (j < rtf.Length && rtf[j] == ' ')
            {
                j++;
            }
            i = j;

            if (word != "u")
            {
                FlushBytes();
            }
            if (state.Destination == Destination.Skip)
            {
                if (word == "bin" && parameter is > 0)
                {
                    i += parameter.Value;
                }
                continue;
            }
            if (pendingSkip > 0 && word is not ("u"))
            {
                pendingSkip = 0;
            }

            switch (word)
            {
                case "ansicpg" when parameter is { } cp:
                    defaultCodePage = cp;
                    break;
                case "deff" when parameter is { } df:
                    defaultFont = df;
                    break;
                case "fonttbl":
                    state.Destination = Destination.FontTable;
                    break;
                case "f" when parameter is { } fn:
                    if (state.Destination == Destination.FontEntry)
                    {
                        fontNumber = fn;
                    }
                    else
                    {
                        state.Font = fn;
                    }
                    break;
                case "fcharset" when parameter is { } cs:
                    fontCharset = cs;
                    break;
                case "colortbl" or "stylesheet" or "info" or "pict" or "object" or "header" or "headerl" or "headerr" or "headerf"
                    or "footer" or "footerl" or "footerr" or "footerf" or "footnote" or "annotation" or "listtable"
                    or "listoverridetable" or "rsidtbl" or "generator" or "themedata" or "colorschememapping" or "datastore"
                    or "latentstyles" or "xmlnstbl" or "pgdsctbl" or "revtbl" or "filetbl" or "nonshppict" or "shppict"
                    or "bkmkstart" or "bkmkend" or "atnid" or "atnauthor":
                    state.Destination = Destination.Skip;
                    break;
                case "fldinst":
                    state.Destination = Destination.FieldInstruction;
                    break;
                case "field":
                    state.IsField = true;
                    break;
                case "fldrslt":
                    state.Destination = Destination.FieldResult;
                    break;
                case "listtext" or "pntext":
                    state.Destination = Destination.ListText;
                    break;
                case "par":
                    if (state.Destination is Destination.Text or Destination.FieldResult)
                    {
                        EndParagraph();
                    }
                    break;
                case "row":
                    if (state.Destination == Destination.Text)
                    {
                        EndParagraph();
                    }
                    break;
                case "line":
                    Emit("\n");
                    break;
                case "tab":
                    Emit("\t");
                    break;
                case "cell":
                    Emit("\t");
                    break;
                case "emdash":
                    Emit("—");
                    break;
                case "endash":
                    Emit("–");
                    break;
                case "bullet":
                    Emit("•");
                    break;
                case "lquote":
                    Emit("‘");
                    break;
                case "rquote":
                    Emit("’");
                    break;
                case "ldblquote":
                    Emit("“");
                    break;
                case "rdblquote":
                    Emit("”");
                    break;
                case "u" when parameter is { } code:
                    FlushBytes();
                    Emit(((char)(code < 0 ? code + 65536 : code)).ToString());
                    pendingSkip = state.UnicodeSkip;
                    break;
                case "uc" when parameter is { } uc:
                    state.UnicodeSkip = uc;
                    break;
                case "b":
                    state.Bold = parameter != 0;
                    break;
                case "i":
                    state.Italic = parameter != 0;
                    break;
                case "ul":
                    state.Underline = parameter != 0;
                    break;
                case "ulnone":
                    state.Underline = false;
                    break;
                case "plain":
                    state.Bold = state.Italic = state.Underline = false;
                    state.Font = -1;
                    break;
                case "pard":
                    outline = -1;
                    leftIndent = 0;
                    listLevel = -1;
                    break;
                case "outlinelevel" when parameter is { } ol:
                    outline = ol;
                    break;
                case "li" when parameter is { } li:
                    leftIndent = li;
                    break;
                case "ilvl" when parameter is { } lvl:
                    listLevel = lvl;
                    break;
                case "ls":
                    listLevel = Math.Max(listLevel, 0);
                    break;
            }
        }
        FlushBytes();
        if (block.Runs.Count > 0)
        {
            EndParagraph();
        }
        foreach (var b in document.Blocks)
        {
            b.Trim();
        }
        while (document.Blocks.Count > 0 && document.Blocks[^1].Runs.Count == 0)
        {
            document.Blocks.RemoveAt(document.Blocks.Count - 1);
        }
        return document;
    }

    private static void StripLeading(Block block, int count)
    {
        while (count > 0 && block.Runs.Count > 0)
        {
            var first = block.Runs[0];
            if (first.Text.Length <= count)
            {
                count -= first.Text.Length;
                block.Runs.RemoveAt(0);
            }
            else
            {
                block.Runs[0] = first with { Text = first.Text[count..] };
                count = 0;
            }
        }
    }

    private static int CharsetCodePage(int charset) => charset switch
    {
        0 => 1252,
        204 => 1251,
        238 => 1250,
        161 => 1253,
        162 => 1254,
        177 => 1255,
        178 => 1256,
        186 => 1257,
        163 => 1258,
        222 => 874,
        128 => 932,
        134 => 936,
        129 => 949,
        136 => 950,
        _ => 0,
    };

    [GeneratedRegex(@"^\s*(•|·|◦|▪|-|\d{1,3}[.)])\t")]
    private static partial Regex ListMarker();

    [GeneratedRegex(@"HYPERLINK\s+""([^""]+)""")]
    private static partial Regex HyperlinkPattern();
}
