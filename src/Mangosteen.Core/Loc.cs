namespace Mangosteen.Core;

/// <summary>
/// Every user-visible string goes through <see cref="L"/>. The text is English; the localisation phase adds
/// Russian and Spanish tables (built from the macOS translations, whose English values match these strings).
/// </summary>
public static class Loc
{
    private static IReadOnlyDictionary<string, string>? _table;

    /// <summary>Replaces the current translation table; null restores English.</summary>
    public static void Use(IReadOnlyDictionary<string, string>? table) => _table = table;

    public static string L(string english) => _table is not null && _table.TryGetValue(english, out var text) ? text : english;
}
