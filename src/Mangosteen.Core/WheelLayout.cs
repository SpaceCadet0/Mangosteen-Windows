namespace Mangosteen.Core;

/// <summary>Where a segment sits: on the face of the wheel, on its back (shown by turning it over), or nowhere.</summary>
public enum SlicePlacement
{
    Front,
    Back,
    Hidden,
}

/// <summary>
/// Splits a wheel's segments into its two sides. The user's choice for a segment id wins; without one a segment is on
/// the face, except the few newer segments listed in <see cref="DefaultBack"/>. A face with more than
/// <see cref="MaxFront"/> segments moves the rest to the back, and a wheel whose face would be empty shows its back as
/// the face.
/// </summary>
public static class WheelLayout
{
    public const int MaxFront = 10;

    /// <summary>Segments added after the wheels were settled; they start on the back so the familiar faces stay as they were.</summary>
    public static readonly IReadOnlySet<string> DefaultBack = new HashSet<string>
    {
        "tool.extractSubtitles", "tool.trace", "tool.removeBars", "tool.hdrToSDR", "tool.pagesPerSheet", "tool.livePhoto",
        "convert.ico", "convert.psd", "convert.tga", "convert.exr", "convert.7z", "convert.iso",
    };

    public static SlicePlacement Placement(string id, IReadOnlyDictionary<string, SlicePlacement>? choices) =>
        choices is not null && choices.TryGetValue(id, out var choice) ? choice
            : DefaultBack.Contains(id) ? SlicePlacement.Back : SlicePlacement.Front;

    public static (List<T> Front, List<T> Back) Split<T>(IEnumerable<T> items, Func<T, string> id,
        IReadOnlyDictionary<string, SlicePlacement>? choices)
    {
        var front = new List<T>();
        var back = new List<T>();
        foreach (var item in items)
        {
            switch (Placement(id(item), choices))
            {
                case SlicePlacement.Front:
                    front.Add(item);
                    break;
                case SlicePlacement.Back:
                    back.Add(item);
                    break;
            }
        }
        // An empty face shows the back first; then any face holds at most MaxFront, the rest going behind.
        if (front.Count == 0)
        {
            (front, back) = (back, []);
        }
        if (front.Count > MaxFront)
        {
            back.InsertRange(0, front.Skip(MaxFront));
            front.RemoveRange(MaxFront, front.Count - MaxFront);
        }
        return (front, back);
    }
}
