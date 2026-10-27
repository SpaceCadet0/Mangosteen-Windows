using System.Text.Json;

namespace Mangosteen.Core;

/// <summary>"Check for Updates…": the latest release on GitHub, and whether it is newer than this one.</summary>
public static class Updates
{
    public const string LatestReleaseApi = "https://api.github.com/repos/SpaceCadet0/Mangosteen-Windows/releases/latest";

    /// <summary>
    /// Compares versions part by part as numbers, ignoring a leading "v": 1.10.0 is newer than 1.9.9, and 1.0 equals
    /// 1.0.0. Parts that are not numbers count as 0.
    /// </summary>
    public static int Compare(string a, string b)
    {
        var left = Parts(a);
        var right = Parts(b);
        for (var i = 0; i < Math.Max(left.Length, right.Length); i++)
        {
            var difference = (i < left.Length ? left[i] : 0).CompareTo(i < right.Length ? right[i] : 0);
            if (difference != 0)
            {
                return difference;
            }
        }
        return 0;
    }

    private static long[] Parts(string version) => version.Trim().TrimStart('v', 'V').Split('.')
        .Select(part => long.TryParse(new string(part.TakeWhile(char.IsDigit).ToArray()), out var number) ? number : 0)
        .ToArray();

    /// <summary>The tag ("v1.3.2") and web page of a release, from the GitHub API's JSON.</summary>
    public static (string Tag, string Url) ParseRelease(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? throw new InvalidDataException("The release has no tag.");
        var url = root.TryGetProperty("html_url", out var page) ? page.GetString() : null;
        return (tag, url ?? "https://github.com/SpaceCadet0/Mangosteen-Windows/releases/latest");
    }
}
