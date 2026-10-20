using System.Text;
using Mangosteen.Core;
using Mangosteen.Core.Documents;

namespace Mangosteen.Core.Tests;

public class DocumentTests : TempFolderTest
{
    private const string Markdown = """
        # Заголовок

        Обычный **жирный** и *курсив* текст со `кодом` и [ссылкой](https://example.com/путь).

        - первый
        - второй
          - вложенный

        1. раз
        2. два

        ```
        var x = 1;
        ```
        """;

    private static void AssertSample(RichDocument document, bool code = true)
    {
        var blocks = document.Blocks.Where(b => b.Text.Length > 0).ToList();
        Assert.Equal(BlockType.Heading, blocks[0].Type);
        Assert.Equal(1, blocks[0].Level);
        Assert.Equal("Заголовок", blocks[0].Text);
        Assert.Equal("Обычный жирный и курсив текст со кодом и ссылкой.", blocks[1].Text);
        Assert.Contains(blocks[1].Runs, r => r.Bold && r.Text == "жирный");
        Assert.Contains(blocks[1].Runs, r => r.Italic && r.Text == "курсив");
        Assert.Contains(blocks[1].Runs, r => r.Link == "https://example.com/путь" && r.Text == "ссылкой");
        if (code)
        {
            Assert.Contains(blocks[1].Runs, r => r.Code && r.Text == "кодом");
        }
        Assert.Equal([(BlockType.ListItem, 0, false), (BlockType.ListItem, 0, false), (BlockType.ListItem, 1, false),
                (BlockType.ListItem, 0, true), (BlockType.ListItem, 0, true)],
            blocks.Skip(2).Take(5).Select(b => (b.Type, b.Level, b.Ordered)));
        Assert.Equal("вложенный", blocks[4].Text);
        Assert.Equal("var x = 1;", blocks[7].Text);
        if (code)
        {
            Assert.Equal(BlockType.Code, blocks[7].Type);
        }
    }

    [Fact]
    public void MarkdownParses() => AssertSample(MarkdownFormat.Parse(Markdown));

    [Fact]
    public void MarkdownRoundTrips() => AssertSample(MarkdownFormat.Parse(MarkdownFormat.Write(MarkdownFormat.Parse(Markdown))));

    [Fact]
    public void HtmlRoundTrips() => AssertSample(HtmlFormat.Parse(HtmlFormat.Write(MarkdownFormat.Parse(Markdown), "t")));

    [Theory]
    [InlineData(FileFormat.Docx)]
    [InlineData(FileFormat.Odt)]
    [InlineData(FileFormat.Rtf)]
    public void FileFormatsRoundTrip(FileFormat format)
    {
        var path = PathOf("документ." + format.Extension());
        DocumentConverter.Write(MarkdownFormat.Parse(Markdown), format, path, "документ");
        AssertSample(DocumentConverter.Read(path, format));
    }

    [Fact]
    public void ConvertWritesEveryTextFormat()
    {
        File.WriteAllText(PathOf("заметки.md"), Markdown);
        foreach (var target in new[] { FileFormat.Txt, FileFormat.Html, FileFormat.Docx, FileFormat.Odt, FileFormat.Rtf })
        {
            var output = PathOf("out." + target.Extension());
            DocumentConverter.Convert(PathOf("заметки.md"), FileFormat.Md, target, output);
            Assert.True(new FileInfo(output).Length > 0, target.ToString());
        }
        var text = File.ReadAllText(PathOf("out.txt"));
        Assert.Contains("Заголовок\n", text);
        Assert.Contains("• первый\n", text);
        Assert.Contains("    • вложенный\n", text);
        Assert.Contains("2. два\n", text);
    }

    [Fact]
    public void ReadsLegacyCyrillicRtf()
    {
        // As written by WordPad on Russian Windows: code page 1251 bytes and a \fcharset204 font.
        var rtf = @"{\rtf1\ansi\ansicpg1251\deff0{\fonttbl{\f0\fnil\fcharset204 Calibri;}}" +
                  @"\pard\f0 \'cf\'f0\'e8\'e2\'e5\'f2 {\b \'ec\'e8\'f0}\par" +
                  @"{\field{\*\fldinst{HYPERLINK ""https://ya.ru""}}{\fldrslt{\ul link}}}\par}";
        var document = RtfFormat.Parse(Encoding.Latin1.GetBytes(rtf));
        Assert.Equal("Привет мир", document.Blocks[0].Text);
        Assert.Contains(document.Blocks[0].Runs, r => r.Bold && r.Text == "мир");
        Assert.Equal("https://ya.ru", Assert.Single(document.Blocks[1].Runs).Link);
    }

    [Fact]
    public void ReadsUnicodeEscapesInRtf()
    {
        // Built at run time: each letter as backslash, "u", its decimal code and the "?" fallback.
        var escapes = string.Concat("Привет".Select(c => "\\" + "u" + (int)c + "?"));
        var document = RtfFormat.Parse(@"{\rtf1\ansi\uc1 " + escapes + @"\par}");
        Assert.Equal("Привет", Assert.Single(document.Blocks).Text);
    }

    [Fact]
    public void HtmlSkipsScriptsAndKeepsLineBreaks()
    {
        var document = HtmlFormat.Parse("<html><head><style>p{}</style></head><body><script>x()</script><p>a<br>b</p><div>c <b>d</b></div></body></html>");
        Assert.Equal(["a\nb", "c d"], document.Blocks.Select(b => b.Text));
    }

    [Fact]
    public void PlainTextKeepsLines()
    {
        var document = RichDocument.FromPlainText("один\r\n\r\nдва\n");
        Assert.Equal(["один", "", "два"], document.Blocks.Select(b => b.Text));
        Assert.Equal("один\n\nдва\n", document.ToPlainText());
    }
}
