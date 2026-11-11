namespace Mangosteen.Core;

/// <summary>
/// Splitting a PDF into several documents. A cut <c>i</c> falls after page <c>i</c> (0-based); excluded pages are left
/// out; a part left with no pages is dropped.
/// </summary>
public static class PdfSplit
{
    public static List<List<int>> Documents(int pageCount, IEnumerable<int> cuts, IEnumerable<int> excluded)
    {
        var cutSet = cuts.ToHashSet();
        var excludedSet = excluded.ToHashSet();
        var documents = new List<List<int>>();
        var current = new List<int>();
        for (var page = 0; page < pageCount; page++)
        {
            if (!excludedSet.Contains(page))
            {
                current.Add(page);
            }
            if (cutSet.Contains(page) || page == pageCount - 1)
            {
                if (current.Count > 0)
                {
                    documents.Add(current);
                }
                current = [];
            }
        }
        return documents;
    }

    /// <summary>A cut after every page.</summary>
    public static HashSet<int> EveryPage(int pageCount) => Enumerable.Range(0, Math.Max(pageCount - 1, 0)).ToHashSet();

    /// <summary>One cut in the middle; an odd page goes to the first half.</summary>
    public static HashSet<int> InHalf(int pageCount) => pageCount >= 2 ? [(pageCount + 1) / 2 - 1] : [];

    /// <summary>A cut after every <paramref name="n"/> pages.</summary>
    public static HashSet<int> EveryN(int pageCount, int n) =>
        n < 1 ? [] : Enumerable.Range(1, Math.Max(pageCount - 1, 0)).Where(p => p % n == 0).Select(p => p - 1).ToHashSet();

    /// <summary>
    /// Pages typed as ranges — "1-3, 8, 12-" (a dash or an en dash; "12-" runs to the end, "-3" from the start) — become
    /// one document per range, the pages in between left out. Ranges must go up and not overlap; null when the text
    /// cannot be read that way.
    /// </summary>
    public static (HashSet<int> Cuts, HashSet<int> Excluded)? FromRanges(string text, int pageCount)
    {
        var items = text.Replace('–', '-').Replace('—', '-').Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (items.Length == 0 || pageCount <= 0)
        {
            return null;
        }
        var ranges = new List<(int First, int Last)>();
        foreach (var item in items)
        {
            var parts = item.Split('-', 2).Select(p => p.Trim()).ToArray();
            int? Number(string s) => int.TryParse(s, out var n) ? n : null;
            var first = parts.Length == 1 ? Number(parts[0]) : parts[0].Length == 0 ? 1 : Number(parts[0]);
            var last = parts.Length == 1 ? first : parts[1].Length == 0 ? pageCount : Number(parts[1]);
            if (first is not { } f || last is not { } l || f < 1 || l > pageCount || f > l
                || ranges.Count > 0 && f <= ranges[^1].Last)
            {
                return null;
            }
            ranges.Add((f, l));
        }
        var kept = ranges.SelectMany(r => Enumerable.Range(r.First - 1, r.Last - r.First + 1)).ToHashSet();
        var cuts = ranges.Take(ranges.Count - 1).Select(r => r.Last - 1).ToHashSet();
        return (cuts, Enumerable.Range(0, pageCount).Where(p => !kept.Contains(p)).ToHashSet());
    }

    /// <summary>Page numbers as ranges for people, 1-based: "1–3, 5, 7–8".</summary>
    public static string Ranges(IReadOnlyList<int> pages)
    {
        var parts = new List<string>();
        for (var i = 0; i < pages.Count;)
        {
            var j = i;
            while (j + 1 < pages.Count && pages[j + 1] == pages[j] + 1)
            {
                j++;
            }
            parts.Add(i == j ? $"{pages[i] + 1}" : $"{pages[i] + 1}–{pages[j] + 1}");
            i = j + 1;
        }
        return string.Join(", ", parts);
    }
}
