using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Mangosteen.Core.Imaging;

/// <summary>
/// Turns a picture into vector shapes (SVG): a black-and-white trace for drawings, text and scans, or a few flat colours
/// for logos and icons. Ported from the macOS app.
/// </summary>
/// <remarks>
/// The picture is reduced to its main colours (k-means), each colour becomes a mask, the mask's boundaries are followed
/// along the pixel edges (every outline and hole is one loop; even-odd filling tells them apart), and the loops are
/// simplified and smoothed. Layers are stacked from the largest colour up, each covering its own area and everything
/// painted after it, so no hairline gaps show between neighbouring colours. Colour tracing goes to vtracer when the app
/// carries it (smoother curves); its palette still comes from our k-means, since vtracer's own merged red into black.
/// </remarks>
public static partial class VectorTracer
{
    public sealed record Options(int Colors = 1, double Detail = 0.6, bool Smooth = true, bool DropBackground = false);

    public readonly record struct Color(double R, double G, double B)
    {
        public string Hex => $"#{To255(R):x2}{To255(G):x2}{To255(B):x2}";
        private static int To255(double v) => (int)Math.Round(Math.Clamp(v, 0, 1) * 255);
    }

    /// <summary>One filled shape. <paramref name="Transform"/> is an SVG transform for vtracer's paths, empty for ours.</summary>
    public sealed record Layer(Color Color, string PathData, bool EvenOdd = true, string Transform = "");

    public sealed record Result(int Width, int Height, IReadOnlyList<Layer> Layers);

    /// <summary>The picture is traced at most this size across; the shapes are scaled back to the original.</summary>
    public const int WorkingSize = 2000;

    /// <summary>Pixels as 8-bit BGRA, not premultiplied.</summary>
    public sealed class Pixels(byte[] bgra, int width, int height)
    {
        public int Width { get; } = width;
        public int Height { get; } = height;
        public int Count => Width * Height;

        public double Alpha(int i) => bgra[i * 4 + 3] / 255.0;

        public Color Rgb(int i) => new(bgra[i * 4 + 2] / 255.0, bgra[i * 4 + 1] / 255.0, bgra[i * 4] / 255.0);

        public double Luminance(int i)
        {
            var c = Rgb(i);
            return 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B;
        }

        /// <summary>The luminance as seen on white paper: transparent pixels are white, half-transparent ones lighter.</summary>
        public double LuminanceOnWhite(int i)
        {
            var a = Alpha(i);
            return Luminance(i) * a + (1 - a);
        }
    }

    // ---------------------------------------------------------------- Tracing

    /// <param name="scale">Original size divided by the working size, to give the shapes the original's coordinates.</param>
    public static Result Trace(Pixels pixels, Options options, double scale = 1, Func<bool>? isCancelled = null)
    {
        isCancelled ??= () => false;
        var width = (int)Math.Round(pixels.Width * scale);
        var height = (int)Math.Round(pixels.Height * scale);
        List<Color> palette;
        int[] labels;
        if (options.Colors <= 1)
        {
            // A transparent background is paper: a logo saved with no background is dark on nothing.
            var threshold = OtsuThreshold(pixels);
            labels = new int[pixels.Count];
            for (var i = 0; i < pixels.Count; i++)
            {
                labels[i] = pixels.LuminanceOnWhite(i) < threshold ? 1 : 0;
            }
            palette = [new Color(1, 1, 1), new Color(0, 0, 0)];
        }
        else
        {
            (palette, labels) = Quantize(pixels, Math.Clamp(options.Colors, 2, 16));
        }

        var minimumArea = Math.Pow(1 - options.Detail, 2) * pixels.Count * 0.0004 + 2;
        var layers = new List<Layer>();
        var areas = new int[palette.Count];
        foreach (var label in labels)
        {
            if (label >= 0)
            {
                areas[label]++;
            }
        }
        var order = Enumerable.Range(0, palette.Count).Where(i => areas[i] > 0).OrderByDescending(i => areas[i]).ToList();
        if (options.Colors <= 1)
        {
            order = order.Where(i => i == 1).ToList();
            if (!options.DropBackground)
            {
                layers.Add(new Layer(palette[0], $"M0 0L{width} 0L{width} {height}L0 {height}Z"));
            }
        }
        else if (options.DropBackground && EdgeLabel(labels, pixels.Width, pixels.Height) is { } background)
        {
            order.Remove(background);
        }

        for (var position = 0; position < order.Count; position++)
        {
            if (isCancelled())
            {
                throw new OperationCanceledException();
            }
            // Black-and-white draws exactly the dark pixels; colour layers also cover the colours drawn after them.
            var covered = new bool[palette.Count];
            foreach (var included in options.Colors <= 1 ? [order[position]] : order.Skip(position))
            {
                covered[included] = true;
            }
            var mask = new bool[labels.Length];
            for (var i = 0; i < labels.Length; i++)
            {
                mask[i] = labels[i] >= 0 && covered[labels[i]];
            }
            var path = Contours(mask, pixels.Width, pixels.Height, options.Detail, options.Smooth, minimumArea, scale, isCancelled);
            if (path.Length > 0)
            {
                layers.Add(new Layer(palette[order[position]], path));
            }
        }
        return new Result(width, height, layers);
    }

    // ---------------------------------------------------------------- SVG

    public static string Svg(Result result)
    {
        var text = new StringBuilder();
        text.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        text.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{result.Width}\" height=\"{result.Height}\" viewBox=\"0 0 {result.Width} {result.Height}\">\n");
        foreach (var layer in result.Layers)
        {
            text.Append($"<path fill=\"{layer.Color.Hex}\"");
            if (layer.EvenOdd)
            {
                text.Append(" fill-rule=\"evenodd\"");
            }
            if (layer.Transform.Length > 0)
            {
                text.Append($" transform=\"{layer.Transform}\"");
            }
            text.Append(" d=\"").Append(layer.PathData).Append("\"/>\n");
        }
        text.Append("</svg>\n");
        return text.ToString();
    }

    /// <summary>A number to a tenth of a pixel, written by hand: a detailed picture has hundreds of thousands of them.</summary>
    private static void Number(StringBuilder text, double value)
    {
        var tenths = (long)Math.Round(value * 10);
        if (tenths < 0)
        {
            text.Append('-');
        }
        var magnitude = Math.Abs(tenths);
        text.Append(magnitude / 10);
        if (magnitude % 10 != 0)
        {
            text.Append('.').Append(magnitude % 10);
        }
    }

    private static void Point(StringBuilder text, (double X, double Y) p)
    {
        Number(text, p.X);
        text.Append(' ');
        Number(text, p.Y);
    }

    // ---------------------------------------------------------------- Contours

    /// <summary>
    /// Follows every boundary between mask and non-mask pixels. Edges run clockwise around mask pixels (y down), so at
    /// each corner at most two leave; where two do (pixels touching only at a corner) the right turn is taken, keeping
    /// such pixels apart. Points are the middles of the pixel edges, which turns stairs into diagonals.
    /// </summary>
    private static string Contours(bool[] mask, int width, int height, double detail, bool smooth, double minimumArea, double scale, Func<bool> isCancelled)
    {
        var stride = width + 1;
        // Per corner, the directions of the edges leaving it: 1 right, 2 down, 4 left, 8 up.
        var outgoing = new byte[stride * (height + 1)];
        bool Inside(int x, int y) => x >= 0 && y >= 0 && x < width && y < height && mask[y * width + x];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (!mask[y * width + x])
                {
                    continue;
                }
                if (!Inside(x, y - 1)) outgoing[y * stride + x] |= 1;
                if (!Inside(x + 1, y)) outgoing[y * stride + x + 1] |= 2;
                if (!Inside(x, y + 1)) outgoing[(y + 1) * stride + x + 1] |= 4;
                if (!Inside(x - 1, y)) outgoing[(y + 1) * stride + x] |= 8;
            }
        }
        (int X, int Y)[] steps = [(1, 0), (0, 1), (-1, 0), (0, -1)];
        // Douglas–Peucker tolerance in working pixels: 0.25 at full detail, 1.2 at the lowest.
        var tolerance = 1.2 - 0.95 * detail;
        var path = new StringBuilder();
        for (var start = 0; start < outgoing.Length; start++)
        {
            if (outgoing[start] == 0)
            {
                continue;
            }
            if (isCancelled())
            {
                throw new OperationCanceledException();
            }
            var points = new List<(double X, double Y)>();
            var vertex = start;
            var direction = System.Numerics.BitOperations.TrailingZeroCount(outgoing[start]);
            while (true)
            {
                outgoing[vertex] &= (byte)~(1 << direction);
                var (x, y) = (vertex % stride, vertex / stride);
                var step = steps[direction];
                points.Add((x + 0.5 * step.X, y + 0.5 * step.Y));
                vertex = (y + step.Y) * stride + x + step.X;
                var available = outgoing[vertex];
                if (available == 0)
                {
                    break;
                }
                int right = (direction + 1) % 4, straight = direction, left = (direction + 3) % 4;
                direction = (available & (1 << right)) != 0 ? right
                    : (available & (1 << straight)) != 0 ? straight
                    : (available & (1 << left)) != 0 ? left
                    : System.Numerics.BitOperations.TrailingZeroCount(available);
            }
            if (points.Count < 4 || Math.Abs(Area(points)) < minimumArea)
            {
                continue;
            }
            var simple = SharpenCorners(Simplify(points, tolerance)).Select(p => (p.X * scale, p.Y * scale)).ToList();
            if (simple.Count < 3)
            {
                continue;
            }
            if (smooth)
            {
                AddSmooth(simple, 24 * scale, path);
            }
            else
            {
                AddPolygon(simple, path);
            }
        }
        return path.ToString();
    }

    private static double Length((double X, double Y) a, (double X, double Y) b) => Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));

    /// <summary>
    /// Pixels round off a sharp corner, and simplifying keeps that as a short bevel between two straight sides. Where two
    /// longer sides turn by 60° or more across a short segment, the segment is replaced by the point where the sides
    /// meet, so letters and logos get their square corners back.
    /// </summary>
    public static List<(double X, double Y)> SharpenCorners(List<(double X, double Y)> points)
    {
        var count = points.Count;
        if (count < 5)
        {
            return points;
        }
        (double X, double Y) P(int index) => points[(index % count + count) % count];
        var replaced = new Dictionary<int, (double X, double Y)>();
        var i = 0;
        while (i < count)
        {
            var (a, b, c, d) = (P(i - 1), P(i), P(i + 1), P(i + 2));
            var (bevel, before, after) = (Length(b, c), Length(a, b), Length(c, d));
            var wrapsIntoFirst = i == count - 1 && replaced.ContainsKey(0);
            if (bevel <= 3 && before > bevel * 1.5 && after > bevel * 1.5 && !replaced.ContainsKey(i - 1) && !wrapsIntoFirst)
            {
                var (ux, uy) = (b.X - a.X, b.Y - a.Y);
                var (vx, vy) = (d.X - c.X, d.Y - c.Y);
                var cosine = (ux * vx + uy * vy) / (before * after);
                var cross = ux * vy - uy * vx;
                if (cosine < 0.5 && Math.Abs(cross) > 1e-9)
                {
                    // Where line a→b meets line c→d.
                    var t = ((c.X - a.X) * vy - (c.Y - a.Y) * vx) / cross;
                    var corner = (a.X + ux * t, a.Y + uy * t);
                    var middle = ((b.X + c.X) / 2, (b.Y + c.Y) / 2);
                    if (Length(corner, middle) <= 3)
                    {
                        replaced[i] = corner;
                        i += 2;
                        continue;
                    }
                }
            }
            i++;
        }
        if (replaced.Count == 0)
        {
            return points;
        }
        var result = new List<(double X, double Y)>();
        var skip = false;
        for (var index = 0; index < count; index++)
        {
            if (skip)
            {
                skip = false;
                continue;
            }
            if (replaced.TryGetValue(index, out var corner))
            {
                result.Add(corner);
                skip = true;
            }
            else if (index == 0 && replaced.ContainsKey(count - 1))
            {
                // The bevel wrapped around the end of the loop; its corner is already the last point.
            }
            else
            {
                result.Add(points[index]);
            }
        }
        return result.Count >= 3 ? result : points;
    }

    /// <summary>Douglas–Peucker on a closed loop, without recursion: a boundary can have tens of thousands of points.</summary>
    public static List<(double X, double Y)> Simplify(List<(double X, double Y)> points, double tolerance)
    {
        if (points.Count <= 4)
        {
            return points;
        }
        // Split the loop at the point farthest from the first, then simplify both halves as open lines.
        var first = points[0];
        var far = 0;
        for (var i = 1; i < points.Count; i++)
        {
            if (Length(points[i], first) > Length(points[far], first))
            {
                far = i;
            }
        }
        var keep = new bool[points.Count];
        keep[0] = keep[far] = true;
        var stack = new Stack<(int From, int To)>();
        stack.Push((0, far));
        stack.Push((far, points.Count));
        while (stack.Count > 0)
        {
            var (from, to) = stack.Pop();
            if (to - from <= 1)
            {
                continue;
            }
            var (a, b) = (points[from], points[to % points.Count]);
            var (dx, dy) = (b.X - a.X, b.Y - a.Y);
            var length = Math.Max(Math.Sqrt(dx * dx + dy * dy), 1e-9);
            var (worst, worstIndex) = (-1.0, from);
            for (var i = from + 1; i < to; i++)
            {
                var p = points[i];
                var distance = Math.Abs(dy * (p.X - a.X) - dx * (p.Y - a.Y)) / length;
                if (distance > worst)
                {
                    (worst, worstIndex) = (distance, i);
                }
            }
            if (worst > tolerance)
            {
                keep[worstIndex] = true;
                stack.Push((from, worstIndex));
                stack.Push((worstIndex, to));
            }
        }
        return points.Where((_, i) => keep[i]).ToList();
    }

    private static double Area(List<(double X, double Y)> points)
    {
        var sum = 0.0;
        for (var i = 0; i < points.Count; i++)
        {
            var (a, b) = (points[i], points[(i + 1) % points.Count]);
            sum += a.X * b.Y - b.X * a.Y;
        }
        return sum / 2;
    }

    private static void AddPolygon(List<(double X, double Y)> points, StringBuilder path)
    {
        path.Append('M');
        Point(path, points[0]);
        for (var i = 1; i < points.Count; i++)
        {
            path.Append('L');
            Point(path, points[i]);
        }
        path.Append('Z');
    }

    /// <summary>
    /// Curves only where the outline really bends gently; everything else stays as drawn. A vertex is a corner when the
    /// outline turns there by more than 45°, or by more than 15° next to a long segment. Gentle vertices get a quadratic
    /// curve between the middles of their segments, which never leaves the polygon: no bulges and no loops at corners.
    /// </summary>
    private static void AddSmooth(List<(double X, double Y)> points, double longSegment, StringBuilder path)
    {
        var count = points.Count;
        (double X, double Y) P(int index) => points[(index % count + count) % count];
        (double X, double Y) Middle((double X, double Y) a, (double X, double Y) b) => ((a.X + b.X) / 2, (a.Y + b.Y) / 2);
        var corner = new bool[count];
        for (var i = 0; i < count; i++)
        {
            var (a, b, c) = (P(i - 1), P(i), P(i + 1));
            var (l1, l2) = (Length(a, b), Length(b, c));
            if (l1 <= 0 || l2 <= 0)
            {
                corner[i] = true;
                continue;
            }
            var cosine = ((b.X - a.X) * (c.X - b.X) + (b.Y - a.Y) * (c.Y - b.Y)) / (l1 * l2);
            var nearStraight = l1 > longSegment || l2 > longSegment;
            corner[i] = cosine < 0.707 || nearStraight && cosine < 0.966;
        }
        var current = corner[0] ? P(0) : Middle(P(-1), P(0));
        path.Append('M');
        Point(path, current);
        for (var i = 1; i <= count; i++)
        {
            var vertex = P(i);
            if (corner[i % count])
            {
                path.Append('L');
                Point(path, vertex);
                current = vertex;
            }
            else
            {
                var start = Middle(P(i - 1), vertex);
                if (Math.Abs(start.X - current.X) > 1e-9 || Math.Abs(start.Y - current.Y) > 1e-9)
                {
                    path.Append('L');
                    Point(path, start);
                }
                var end = Middle(vertex, P(i + 1));
                path.Append('Q');
                Point(path, vertex);
                path.Append(' ');
                Point(path, end);
                current = end;
            }
        }
        path.Append('Z');
    }

    /// <summary>The label most common along the picture's border (transparent pixels do not count).</summary>
    private static int? EdgeLabel(int[] labels, int width, int height)
    {
        var counts = new Dictionary<int, int>();
        void Count(int label)
        {
            if (label >= 0)
            {
                counts[label] = counts.GetValueOrDefault(label) + 1;
            }
        }
        for (var x = 0; x < width; x++)
        {
            Count(labels[x]);
            Count(labels[(height - 1) * width + x]);
        }
        for (var y = 0; y < height; y++)
        {
            Count(labels[y * width]);
            Count(labels[y * width + width - 1]);
        }
        return counts.Count == 0 ? null : counts.MaxBy(c => c.Value).Key;
    }

    // ---------------------------------------------------------------- Colours

    /// <summary>Otsu's threshold on luminance: the split that best separates dark ink from light paper.</summary>
    public static double OtsuThreshold(Pixels pixels)
    {
        var histogram = new double[256];
        for (var i = 0; i < pixels.Count; i++)
        {
            histogram[Math.Min(255, (int)(pixels.LuminanceOnWhite(i) * 255))]++;
        }
        double total = pixels.Count;
        if (total == 0)
        {
            return 0.5;
        }
        var sum = 0.0;
        for (var level = 0; level < 256; level++)
        {
            sum += level * histogram[level];
        }
        double backgroundWeight = 0, backgroundSum = 0, best = 0;
        var threshold = 127;
        for (var level = 0; level < 256; level++)
        {
            backgroundWeight += histogram[level];
            if (backgroundWeight == 0)
            {
                continue;
            }
            var foregroundWeight = total - backgroundWeight;
            if (foregroundWeight == 0)
            {
                break;
            }
            backgroundSum += level * histogram[level];
            var meanB = backgroundSum / backgroundWeight;
            var meanF = (sum - backgroundSum) / foregroundWeight;
            var between = backgroundWeight * foregroundWeight * (meanB - meanF) * (meanB - meanF);
            if (between > best)
            {
                (best, threshold) = (between, level);
            }
        }
        // Everything in the chosen level's bin and below is ink: the bin holds luminances up to (level + 1) / 255.
        return (threshold + 1.0) / 255;
    }

    private static double Distance(Color a, Color b)
    {
        var (dr, dg, db) = (a.R - b.R, a.G - b.G, a.B - b.B);
        return dr * dr * 0.3 + dg * dg * 0.59 + db * db * 0.11;
    }

    /// <summary>
    /// k-means on a sample of the opaque pixels, seeded deterministically with the farthest points; then every pixel takes
    /// its nearest colour (-1 for transparent ones).
    /// </summary>
    public static (List<Color> Palette, int[] Labels) Quantize(Pixels pixels, int colors)
    {
        var opaque = Enumerable.Range(0, pixels.Count).Where(i => pixels.Alpha(i) >= 0.5).ToList();
        if (opaque.Count == 0)
        {
            return ([], Enumerable.Repeat(-1, pixels.Count).ToArray());
        }
        var step = Math.Max(1, opaque.Count / 20_000);
        var sample = new List<Color>();
        for (var i = 0; i < opaque.Count; i += step)
        {
            sample.Add(pixels.Rgb(opaque[i]));
        }
        var centers = new List<Color> { sample[0] };
        var nearestDistance = sample.Select(c => Distance(c, sample[0])).ToArray();
        while (centers.Count < colors)
        {
            var farthest = 0;
            for (var i = 1; i < nearestDistance.Length; i++)
            {
                if (nearestDistance[i] > nearestDistance[farthest])
                {
                    farthest = i;
                }
            }
            if (nearestDistance[farthest] <= 1e-6)
            {
                break;
            }
            centers.Add(sample[farthest]);
            for (var i = 0; i < sample.Count; i++)
            {
                nearestDistance[i] = Math.Min(nearestDistance[i], Distance(sample[i], sample[farthest]));
            }
        }
        int Nearest(Color color)
        {
            var (best, bestDistance) = (0, double.PositiveInfinity);
            for (var i = 0; i < centers.Count; i++)
            {
                var d = Distance(centers[i], color);
                if (d < bestDistance)
                {
                    (best, bestDistance) = (i, d);
                }
            }
            return best;
        }
        for (var round = 0; round < 12; round++)
        {
            var sums = new (double R, double G, double B, double N)[centers.Count];
            foreach (var color in sample)
            {
                var i = Nearest(color);
                sums[i] = (sums[i].R + color.R, sums[i].G + color.G, sums[i].B + color.B, sums[i].N + 1);
            }
            var moved = false;
            for (var i = 0; i < centers.Count; i++)
            {
                if (sums[i].N == 0)
                {
                    continue;
                }
                var updated = new Color(sums[i].R / sums[i].N, sums[i].G / sums[i].N, sums[i].B / sums[i].N);
                moved |= Distance(updated, centers[i]) > 1e-7;
                centers[i] = updated;
            }
            if (!moved)
            {
                break;
            }
        }
        // Anti-aliased edges pull a colour towards its neighbours; the final colour is the mean of the half of its
        // pixels closest to the centre, so a red logo stays red rather than pink.
        var members = centers.Select(_ => new List<Color>()).ToList();
        foreach (var color in sample)
        {
            members[Nearest(color)].Add(color);
        }
        for (var i = 0; i < centers.Count; i++)
        {
            if (members[i].Count == 0)
            {
                continue;
            }
            var center = centers[i];
            var core = members[i].OrderBy(c => Distance(c, center)).Take(Math.Max(1, members[i].Count / 2)).ToList();
            centers[i] = new Color(core.Average(c => c.R), core.Average(c => c.G), core.Average(c => c.B));
        }
        // Colours too close to tell apart become one: a one-colour logo asked for two colours gave two blacks.
        var representative = Enumerable.Range(0, centers.Count).ToArray();
        for (var i = 0; i < centers.Count; i++)
        {
            for (var earlier = 0; earlier < i; earlier++)
            {
                if (representative[earlier] == earlier && Distance(centers[earlier], centers[i]) < 0.0015)
                {
                    representative[i] = earlier;
                    break;
                }
            }
        }
        var labels = new int[pixels.Count];
        for (var i = 0; i < pixels.Count; i++)
        {
            labels[i] = pixels.Alpha(i) >= 0.5 ? representative[Nearest(pixels.Rgb(i))] : -1;
        }
        return (centers, labels);
    }

    // ---------------------------------------------------------------- vtracer

    /// <summary>The picture's main colours for vtracer, as many as asked for and none twice.</summary>
    public static List<Color> Palette(Pixels pixels, int colors)
    {
        var (centers, labels) = Quantize(pixels, Math.Clamp(colors, 2, 16));
        var used = labels.Where(l => l >= 0).ToHashSet();
        var seen = new HashSet<string>();
        return centers.Where((_, i) => used.Contains(i)).Where(c => seen.Add(c.Hex)).ToList();
    }

    /// <summary>Detail 1 keeps patches down to 2 px and fits curves closely; detail 0 drops patches under 16 px.</summary>
    public static List<string> VTracerArguments(string input, string output, Options options, IReadOnlyList<Color> palette) =>
    [
        "--input", input, "--output", output,
        "--clustering", "color-cluster", "--hierarchical", "stacked",
        "--mode", options.Smooth ? "spline" : "polygon",
        "--palette", string.Join(",", palette.Select(c => c.Hex)),
        "--filter-speckle", ((int)Math.Round(16 - 14 * options.Detail)).ToString(CultureInfo.InvariantCulture),
        "--path-precision", "2",
    ];

    /// <summary>
    /// Every <c>&lt;path d="…" fill="#rrggbb" transform="translate(x,y)"&gt;</c> of vtracer's SVG becomes a layer, in
    /// drawing order, scaled by <paramref name="scale"/> back to the original picture. vtracer winds holes the other way,
    /// so its layers fill nonzero.
    /// </summary>
    public static List<Layer> ParseVTracer(string svg, double scale)
    {
        var layers = new List<Layer>();
        foreach (Match match in PathElement().Matches(svg))
        {
            var element = match.Value;
            if (Attribute("d", element) is not { Length: > 0 } data || Attribute("fill", element) is not { } fill || ParseColor(fill) is not { } color)
            {
                continue;
            }
            var transform = new StringBuilder();
            if (Math.Abs(scale - 1) > 1e-9)
            {
                transform.Append($"scale({scale.ToString("0.#####", CultureInfo.InvariantCulture)})");
            }
            if (Attribute("transform", element) is { Length: > 0 } own)
            {
                transform.Append(transform.Length > 0 ? " " : "").Append(own);
            }
            layers.Add(new Layer(color, data.Trim(), EvenOdd: false, Transform: transform.ToString()));
        }
        return layers;
    }

    private static string? Attribute(string name, string element)
    {
        var match = Regex.Match(element, $@"\s{name}=""([^""]*)""");
        return match.Success ? match.Groups[1].Value : null;
    }

    private static Color? ParseColor(string text)
    {
        var hex = text.Trim().TrimStart('#');
        if (hex.Length == 3)
        {
            hex = string.Concat(hex.Select(c => $"{c}{c}"));
        }
        return hex.Length == 6 && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)
            ? new Color((value >> 16 & 0xFF) / 255.0, (value >> 8 & 0xFF) / 255.0, (value & 0xFF) / 255.0) : null;
    }

    /// <summary>The colour among <paramref name="colors"/> nearest to most of the picture's border pixels.</summary>
    public static Color? EdgeColor(Pixels pixels, IReadOnlyCollection<Color> colors)
    {
        if (colors.Count == 0)
        {
            return null;
        }
        var counts = new Dictionary<Color, int>();
        void Count(int index)
        {
            if (pixels.Alpha(index) < 0.5)
            {
                return;
            }
            var pixel = pixels.Rgb(index);
            var nearest = colors.MinBy(c => Distance(c, pixel));
            counts[nearest] = counts.GetValueOrDefault(nearest) + 1;
        }
        for (var x = 0; x < pixels.Width; x++)
        {
            Count(x);
            Count((pixels.Height - 1) * pixels.Width + x);
        }
        for (var y = 0; y < pixels.Height; y++)
        {
            Count(y * pixels.Width);
            Count(y * pixels.Width + pixels.Width - 1);
        }
        return counts.Count == 0 ? null : counts.MaxBy(c => c.Value).Key;
    }

    [GeneratedRegex(@"<path\b[^>]*>")]
    private static partial Regex PathElement();
}
