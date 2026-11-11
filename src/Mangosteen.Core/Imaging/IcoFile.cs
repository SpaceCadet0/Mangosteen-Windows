namespace Mangosteen.Core.Imaging;

/// <summary>
/// Windows icons: up to six square sizes (16–256 px) in one .ico, each stored as PNG (Windows Vista and later read
/// PNG frames of every size).
/// </summary>
public static class IcoFile
{
    public static readonly IReadOnlyList<int> StandardSizes = [16, 32, 48, 64, 128, 256];

    /// <summary>The sizes for a picture whose longer side is <paramref name="longSide"/>: none larger than it, 16 always.</summary>
    public static IReadOnlyList<int> Sizes(int longSide)
    {
        var sizes = StandardSizes.Where(s => s <= longSide).ToList();
        return sizes.Count == 0 ? [16] : sizes;
    }

    /// <summary>The .ico file of square PNG frames, smallest first.</summary>
    public static byte[] Write(IReadOnlyList<(int Size, byte[] Png)> frames)
    {
        if (frames.Count is 0 or > 255)
        {
            throw new ArgumentException("An icon needs 1 to 255 frames.", nameof(frames));
        }
        var ordered = frames.OrderBy(f => f.Size).ToList();
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0);          // reserved
        writer.Write((ushort)1);          // type: icon
        writer.Write((ushort)ordered.Count);
        var offset = 6 + 16 * ordered.Count;
        foreach (var (size, png) in ordered)
        {
            if (size is < 1 or > 256)
            {
                throw new ArgumentOutOfRangeException(nameof(frames), size, "Icon frames are 1 to 256 pixels.");
            }
            writer.Write((byte)(size == 256 ? 0 : size));  // width (0 means 256)
            writer.Write((byte)(size == 256 ? 0 : size));  // height
            writer.Write((byte)0);                         // palette colours
            writer.Write((byte)0);                         // reserved
            writer.Write((ushort)1);                       // colour planes
            writer.Write((ushort)32);                      // bits per pixel
            writer.Write((uint)png.Length);
            writer.Write((uint)offset);
            offset += png.Length;
        }
        foreach (var (_, png) in ordered)
        {
            writer.Write(png);
        }
        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>Where a picture of <paramref name="width"/>×<paramref name="height"/> sits on a square of <paramref name="size"/>: centred, aspect kept.</summary>
    public static (int X, int Y, int Width, int Height) Fit(int width, int height, int size)
    {
        var scale = (double)size / Math.Max(width, height);
        var w = Math.Max(1, (int)Math.Round(width * scale));
        var h = Math.Max(1, (int)Math.Round(height * scale));
        return ((size - w) / 2, (size - h) / 2, w, h);
    }
}
