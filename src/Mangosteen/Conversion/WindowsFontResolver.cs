using Microsoft.Win32;
using PdfSharp.Fonts;

namespace Mangosteen.Conversion;

/// <summary>
/// Lets PDFsharp and MigraDoc use the fonts installed in Windows. The registry lists every installed font file
/// with its family and style ("Segoe UI Bold (TrueType)" = segoeuib.ttf); a missing bold or italic face is
/// simulated, and an unknown family falls back to Segoe UI, then Arial.
/// </summary>
internal sealed class WindowsFontResolver : IFontResolver
{
    private readonly Dictionary<(string Family, bool Bold, bool Italic), string> _faces = [];

    public WindowsFontResolver()
    {
        var fonts = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");
        Read(Registry.LocalMachine, fonts);
        Read(Registry.CurrentUser, fonts);
    }

    public int Count => _faces.Count;

    private void Read(RegistryKey root, string fontsFolder)
    {
        using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts");
        if (key is null)
        {
            return;
        }
        foreach (var name in key.GetValueNames())
        {
            if (key.GetValue(name) is not string file
                || !(file.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".otf", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            var path = Path.IsPathRooted(file) ? file : Path.Combine(fontsFolder, file);
            var label = name.Replace("(TrueType)", "", StringComparison.OrdinalIgnoreCase)
                .Replace("(OpenType)", "", StringComparison.OrdinalIgnoreCase).Trim();
            var bold = false;
            var italic = false;
            var words = label.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            while (words.Count > 1)
            {
                var last = words[^1];
                if (last.Equals("Bold", StringComparison.OrdinalIgnoreCase))
                {
                    bold = true;
                }
                else if (last.Equals("Italic", StringComparison.OrdinalIgnoreCase) || last.Equals("Oblique", StringComparison.OrdinalIgnoreCase))
                {
                    italic = true;
                }
                else if (!last.Equals("Regular", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
                words.RemoveAt(words.Count - 1);
            }
            _faces.TryAdd((string.Join(' ', words).ToLowerInvariant(), bold, italic), path);
        }
    }

    public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic)
    {
        foreach (var family in new[] { familyName, "Segoe UI", "Arial" })
        {
            var key = family.ToLowerInvariant();
            if (_faces.TryGetValue((key, bold, italic), out var exact))
            {
                return new FontResolverInfo(exact);
            }
            if (_faces.TryGetValue((key, bold, false), out var noItalic))
            {
                return new FontResolverInfo(noItalic, false, italic);
            }
            if (_faces.TryGetValue((key, false, italic), out var noBold))
            {
                return new FontResolverInfo(noBold, bold, false);
            }
            if (_faces.TryGetValue((key, false, false), out var regular))
            {
                return new FontResolverInfo(regular, bold, italic);
            }
        }
        return null;
    }

    /// <summary>Face names are the font file paths handed out by <see cref="ResolveTypeface"/>.</summary>
    public byte[]? GetFont(string faceName) => File.Exists(faceName) ? File.ReadAllBytes(faceName) : null;
}
