using System.Globalization;
using System.Text.Json;

namespace Mangosteen.Core;

/// <summary>
/// Every user-visible string goes through <see cref="L"/>. The keys are the English texts; Russian and Spanish
/// tables are embedded JSON files (Strings/ru.json, Strings/es.json), built from the macOS translations plus the
/// strings only Windows has.
/// </summary>
public static class Loc
{
    public static readonly IReadOnlyList<(string Code, string Name)> Languages = [("en", "English"), ("es", "Español"), ("ru", "Русский")];

    private static IReadOnlyDictionary<string, string>? _table;

    public static string Language { get; private set; } = "en";

    /// <summary>Russian or Spanish when Windows is set to one of them, otherwise English.</summary>
    public static string SystemLanguage() => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName switch
    {
        "ru" or "uk" or "be" or "kk" => "ru",
        "es" => "es",
        _ => "en",
    };

    /// <summary>Switches to "en", "es" or "ru"; anything else means English.</summary>
    public static void SetLanguage(string code)
    {
        Language = code is "ru" or "es" ? code : "en";
        _table = Language == "en" ? null : Load(Language);
    }

    /// <summary>Replaces the current translation table; null restores English. For tests.</summary>
    public static void Use(IReadOnlyDictionary<string, string>? table) => _table = table;

    public static IReadOnlyDictionary<string, string> Load(string code)
    {
        using var stream = typeof(Loc).Assembly.GetManifestResourceStream($"Strings.{code}.json");
        return stream is null ? new Dictionary<string, string>() : JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? [];
    }

    public static string L(string english) => _table is not null && _table.TryGetValue(english, out var text) && text.Length > 0 ? text : english;

    private static readonly Dictionary<string, (string[] En, string[] Ru, string[] Es)> Nouns = new()
    {
        ["file"] = (["file", "files"], ["файл", "файла", "файлов"], ["archivo", "archivos"]),
        ["item"] = (["item", "items"], ["объект", "объекта", "объектов"], ["elemento", "elementos"]),
        ["path"] = (["path", "paths"], ["путь", "пути", "путей"], ["ruta", "rutas"]),
        ["part"] = (["part", "parts"], ["часть", "части", "частей"], ["parte", "partes"]),
        ["page"] = (["page", "pages"], ["страница", "страницы", "страниц"], ["página", "páginas"]),
        ["frame"] = (["frame", "frames"], ["кадр", "кадра", "кадров"], ["fotograma", "fotogramas"]),
        ["document"] = (["document", "documents"], ["документ", "документа", "документов"], ["documento", "documentos"]),
        ["bleep"] = (["bleep", "bleeps"], ["писк", "писка", "писков"], ["pitido", "pitidos"]),
    };

    /// <summary>"1 file", "5 files"; "1 файл", "2 файла", "5 файлов"; "1 archivo", "5 archivos".</summary>
    public static string Count(int count, string noun)
    {
        var forms = Nouns[noun];
        string word;
        if (Language == "ru")
        {
            var mod10 = count % 10;
            var mod100 = count % 100;
            word = mod10 == 1 && mod100 != 11 ? forms.Ru[0] : mod10 is >= 2 and <= 4 && mod100 is < 12 or > 14 ? forms.Ru[1] : forms.Ru[2];
        }
        else
        {
            word = (Language == "es" ? forms.Es : forms.En)[count == 1 ? 0 : 1];
        }
        return $"{count} {word}";
    }
}
