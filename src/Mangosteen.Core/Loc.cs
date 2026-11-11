using System.Globalization;
using System.Text.Json;

namespace Mangosteen.Core;

/// <summary>
/// Every user-visible string goes through <see cref="L"/>. The keys are the English texts; the other languages are
/// embedded JSON files (Strings/ru.json, es.json, de.json, fr.json, pt-BR.json, zh-Hans.json, ja.json), built from the
/// macOS translations plus the strings only Windows has (build/build-translations.py).
/// </summary>
public static class Loc
{
    public static readonly IReadOnlyList<(string Code, string Name)> Languages =
    [
        ("en", "English"), ("es", "Español"), ("ru", "Русский"), ("de", "Deutsch"), ("fr", "Français"),
        ("pt-BR", "Português (Brasil)"), ("zh-Hans", "简体中文"), ("ja", "日本語"),
    ];

    private static IReadOnlyDictionary<string, string>? _table;

    public static string Language { get; private set; } = "en";

    /// <summary>The language of Windows when Mangosteen has it, otherwise English.</summary>
    public static string SystemLanguage() => FromCulture(CultureInfo.CurrentUICulture);

    public static string FromCulture(CultureInfo culture) => culture.TwoLetterISOLanguageName switch
    {
        "ru" or "uk" or "be" or "kk" => "ru",
        "es" => "es",
        "de" => "de",
        "fr" => "fr",
        "pt" => "pt-BR",
        "zh" => "zh-Hans",
        "ja" => "ja",
        _ => "en",
    };

    /// <summary>Switches to one of <see cref="Languages"/>; anything else means English.</summary>
    public static void SetLanguage(string code)
    {
        Language = Languages.Any(l => l.Code == code) ? code : "en";
        _table = Language == "en" ? null : Load(Language);
    }

    /// <summary>Replaces the current translation table; null restores English. For tests.</summary>
    public static void Use(IReadOnlyDictionary<string, string>? table) => _table = table;

    public static IReadOnlyDictionary<string, string> Load(string code)
    {
        using var stream = typeof(Loc).Assembly.GetManifestResourceStream($"Strings.{code}.json");
        return stream is null ? new Dictionary<string, string>() : JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? [];
    }

    /// <remarks>A key may carry a context after "|" ("Crop|tab") when one English word needs two translations; the
    /// English text is the part before it.</remarks>
    public static string L(string english) => _table is not null && _table.TryGetValue(english, out var text) && text.Length > 0 ? text
        : english.IndexOf('|') is var bar and > 0 ? english[..bar] : english;

    /// <summary>Singular and plural (Russian: one, few, many) for the nouns counted in the interface.</summary>
    private static readonly Dictionary<string, Dictionary<string, string[]>> Nouns = new()
    {
        ["file"] = new() { ["en"] = ["file", "files"], ["ru"] = ["файл", "файла", "файлов"], ["es"] = ["archivo", "archivos"],
            ["de"] = ["Datei", "Dateien"], ["fr"] = ["fichier", "fichiers"], ["pt-BR"] = ["arquivo", "arquivos"], ["zh-Hans"] = ["个文件"], ["ja"] = ["個のファイル"] },
        ["item"] = new() { ["en"] = ["item", "items"], ["ru"] = ["объект", "объекта", "объектов"], ["es"] = ["elemento", "elementos"],
            ["de"] = ["Objekt", "Objekte"], ["fr"] = ["élément", "éléments"], ["pt-BR"] = ["item", "itens"], ["zh-Hans"] = ["个项目"], ["ja"] = ["個の項目"] },
        ["path"] = new() { ["en"] = ["path", "paths"], ["ru"] = ["путь", "пути", "путей"], ["es"] = ["ruta", "rutas"],
            ["de"] = ["Pfad", "Pfade"], ["fr"] = ["chemin", "chemins"], ["pt-BR"] = ["caminho", "caminhos"], ["zh-Hans"] = ["个路径"], ["ja"] = ["件のパス"] },
        ["part"] = new() { ["en"] = ["part", "parts"], ["ru"] = ["часть", "части", "частей"], ["es"] = ["parte", "partes"],
            ["de"] = ["Teil", "Teile"], ["fr"] = ["partie", "parties"], ["pt-BR"] = ["parte", "partes"], ["zh-Hans"] = ["个部分"], ["ja"] = ["個のパーツ"] },
        ["page"] = new() { ["en"] = ["page", "pages"], ["ru"] = ["страница", "страницы", "страниц"], ["es"] = ["página", "páginas"],
            ["de"] = ["Seite", "Seiten"], ["fr"] = ["page", "pages"], ["pt-BR"] = ["página", "páginas"], ["zh-Hans"] = ["页"], ["ja"] = ["ページ"] },
        ["frame"] = new() { ["en"] = ["frame", "frames"], ["ru"] = ["кадр", "кадра", "кадров"], ["es"] = ["fotograma", "fotogramas"],
            ["de"] = ["Bild", "Bilder"], ["fr"] = ["image", "images"], ["pt-BR"] = ["quadro", "quadros"], ["zh-Hans"] = ["帧"], ["ja"] = ["フレーム"] },
        ["document"] = new() { ["en"] = ["document", "documents"], ["ru"] = ["документ", "документа", "документов"], ["es"] = ["documento", "documentos"],
            ["de"] = ["Dokument", "Dokumente"], ["fr"] = ["document", "documents"], ["pt-BR"] = ["documento", "documentos"], ["zh-Hans"] = ["个文档"], ["ja"] = ["個の文書"] },
        ["bleep"] = new() { ["en"] = ["bleep", "bleeps"], ["ru"] = ["писк", "писка", "писков"], ["es"] = ["pitido", "pitidos"],
            ["de"] = ["Piepton", "Pieptöne"], ["fr"] = ["bip", "bips"], ["pt-BR"] = ["bipe", "bipes"], ["zh-Hans"] = ["个哔声"], ["ja"] = ["個のピー音"] },
    };

    /// <summary>"1 file", "5 files"; "1 файл", "2 файла", "5 файлов"; "5 个文件", "5 個のファイル".</summary>
    public static string Count(int count, string noun)
    {
        var forms = Nouns[noun][Language];
        switch (Language)
        {
            case "ru":
                var mod10 = count % 10;
                var mod100 = count % 100;
                return $"{count} {(mod10 == 1 && mod100 != 11 ? forms[0] : mod10 is >= 2 and <= 4 && mod100 is < 12 or > 14 ? forms[1] : forms[2])}";
            case "zh-Hans":
                return $"{count} {forms[0]}";
            case "ja":
                return $"{count} {forms[0]}";
            case "fr":
                // French counts 0 and 1 as singular.
                return $"{count} {forms[count <= 1 ? 0 : 1]}";
            default:
                return $"{count} {forms[count == 1 ? 0 : 1]}";
        }
    }
}
