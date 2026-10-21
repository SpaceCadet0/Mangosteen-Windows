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
