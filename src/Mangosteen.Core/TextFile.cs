using System.Text;

namespace Mangosteen.Core;

/// <summary>Reads text files whatever their encoding, so Russian files saved by old Windows apps open too.</summary>
public static class TextFile
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    static TextFile() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>The encoding results are written in: UTF-8 without a byte order mark.</summary>
    public static Encoding Utf8 { get; } = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static string Read(string path) => Decode(File.ReadAllBytes(path));

    /// <summary>UTF-8 (with or without BOM), UTF-16 with BOM, then Windows-1251 as the fallback.</summary>
    public static string Decode(byte[] data)
    {
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(data, 3, data.Length - 3);
        }
        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(data, 2, data.Length - 2);
        }
        if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(data, 2, data.Length - 2);
        }
        try
        {
            return StrictUtf8.GetString(data);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(1251).GetString(data);
        }
    }

    public static void Write(string path, string text) => File.WriteAllText(path, text, Utf8);
}
