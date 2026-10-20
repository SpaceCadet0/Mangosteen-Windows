using System.Globalization;
using Mangosteen.Core.Imaging;

namespace Mangosteen.Core.Media;

/// <summary>The pure parts of the video and audio tools: time codes, filter chains and arguments (ported from the Mac).</summary>
public static class MediaMath
{
    private static string N(double value, string format = "0.###") => value.ToString(format, CultureInfo.InvariantCulture);

    public static string Seconds(double value) => N(Math.Max(value, 0), "0.000");

    /// <summary>"01:02:03.456"</summary>
    public static string Timecode(double seconds)
    {
        var ms = (long)Math.Round(Math.Max(seconds, 0) * 1000);
        return $"{ms / 3_600_000:00}:{ms / 60_000 % 60:00}:{ms / 1000 % 60:00}.{ms % 1000:000}";
    }

    /// <summary>"1:02:03.5", "2:03", "75" or "75,5" as seconds; null when not a time.</summary>
    public static double? ParseTimecode(string text)
    {
        var fields = text.Trim().Replace(',', '.').Split(':');
        if (fields.Length is < 1 or > 3)
        {
            return null;
        }
        double seconds = 0;
        foreach (var field in fields)
        {
            if (!double.TryParse(field, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value < 0)
            {
                return null;
            }
            seconds = seconds * 60 + value;
        }
        return seconds;
    }

    /// <summary>Tools keep the source format, except M4V (written as MP4) and AAC (written as M4A).</summary>
    public static FileFormat OutputFormat(FileFormat source) => source switch
    {
        FileFormat.M4v => FileFormat.Mp4,
        FileFormat.Aac => FileFormat.M4a,
        _ => source,
    };

    public static List<double> EqualSplitPoints(double duration, int parts) =>
        parts > 1 && duration > 0 ? Enumerable.Range(1, parts - 1).Select(i => duration * i / parts).ToList() : [];

    /// <summary>Split bounds: 0, the cuts that are not too close to the ends, the duration.</summary>
    public static List<double> SplitBounds(IEnumerable<double> points, double duration) =>
        [0, .. points.Where(p => p > 0.05 && p < duration - 0.05).Distinct().Order(), duration];

    /// <summary>atempo accepts 0.5…2, so larger factors are chained.</summary>
    public static List<string> AtempoChain(double factor)
    {
        var remaining = factor;
        var filters = new List<string>();
        while (remaining > 2)
        {
            filters.Add("atempo=2");
            remaining /= 2;
        }
        while (remaining < 0.5)
        {
            filters.Add("atempo=0.5");
            remaining /= 0.5;
        }
        filters.Add("atempo=" + N(remaining, "0.######"));
        return filters;
    }

    public static string SpeedLabel(double factor) => "×" + N(Math.Round(factor * 100) / 100, "0.##");

    /// <summary>The largest absolute sample in each of <paramref name="buckets"/> equal slices.</summary>
    public static float[] Peaks(ReadOnlySpan<float> samples, int buckets)
    {
        if (samples.Length == 0 || buckets <= 0)
        {
            return [];
        }
        var count = Math.Min(buckets, samples.Length);
        var result = new float[count];
        for (var i = 0; i < count; i++)
        {
            var lower = (long)i * samples.Length / count;
            var upper = Math.Max((long)(i + 1) * samples.Length / count, lower + 1);
            float peak = 0;
            for (var j = lower; j < upper; j++)
            {
                peak = Math.Max(peak, Math.Abs(samples[(int)j]));
            }
            result[i] = Math.Min(peak, 1);
        }
        return result;
    }

    public static string Hex(uint argb) => $"0x{argb & 0xFFFFFF:X6}";

    /// <summary>The source muted and a sine tone mixed in during the ranges.</summary>
    public static string BleepGraph(IReadOnlyList<(double Start, double End)> ranges, double frequency, double volume, double duration)
    {
        var condition = string.Join("+", ranges.Select(r => $"between(t,{Seconds(r.Start)},{Seconds(r.End)})"));
        return $"[0:a:0]aformat=sample_fmts=fltp:sample_rates=48000,volume=0:enable='{condition}'[muted];"
            + $"sine=frequency={(int)frequency}:sample_rate=48000:duration={Seconds(Math.Max(duration, 0.1))},aformat=sample_fmts=fltp:channel_layouts=mono,"
            + $"volume={N(volume, "0.00")},volume=0:enable='not({condition})'[tone];"
            + "[muted][tone]amix=inputs=2:duration=first:normalize=0[a]";
    }

    public enum ChannelLayout
    {
        Stereo, Mono,
    }

    public static string ChannelFilter(ChannelLayout layout, double left, double right, bool swap, int sourceChannels)
    {
        var leftSource = sourceChannels < 2 ? "c0" : swap ? "c1" : "c0";
        var rightSource = sourceChannels < 2 ? "c0" : swap ? "c0" : "c1";
        return layout == ChannelLayout.Stereo
            ? $"pan=stereo|c0={N(left, "0.00")}*{leftSource}|c1={N(right, "0.00")}*{rightSource}"
            : $"pan=mono|c0={N(left / 2, "0.00")}*{leftSource}+{N(right / 2, "0.00")}*{rightSource}";
    }

    public sealed record LoudnessTarget(double Integrated = -14, double TruePeak = -1, double Range = 11);

    public static string LoudnormFilter(LoudnessTarget target) =>
        $"loudnorm=I={N(target.Integrated, "0.00")}:TP={N(target.TruePeak, "0.00")}:LRA={N(target.Range, "0.00")}";

    public sealed record VideoRegion(UnitRect Rect, double Start, double End);

    /// <summary>Covers areas of a video during their time ranges: a filled box, or large pixels (blurred for Blur).</summary>
    public static string RedactionGraph(IReadOnlyList<VideoRegion> regions, RedactionStyle style, uint color, int frameWidth, int frameHeight)
    {
        var chains = new List<string>();
        var current = "[0:v]";
        for (var i = 0; i < regions.Count; i++)
        {
            var region = regions[i];
            var pixels = region.Rect.ToPixels(frameWidth, frameHeight);
            int x = pixels.X / 2 * 2, y = pixels.Y / 2 * 2;
            int width = Math.Max(pixels.Width / 2 * 2, 2), height = Math.Max(pixels.Height / 2 * 2, 2);
            var enable = $"between(t,{Seconds(region.Start)},{Seconds(region.End)})";
            var output = $"[v{i}]";
            if (style == RedactionStyle.Solid)
            {
                chains.Add($"{current}drawbox=x={x}:y={y}:w={width}:h={height}:color={Hex(color)}@1:t=fill:enable='{enable}'{output}");
            }
            else
            {
                var block = Math.Max(Math.Max(width, height) / 10, 4);
                var effect = $"crop={width}:{height}:{x}:{y},scale={Math.Max(width / block, 2)}:{Math.Max(height / block, 2)},scale={width}:{height}:flags=neighbor";
                if (style == RedactionStyle.Blur)
                {
                    effect += $",gblur=sigma={N(block * 0.9, "0.0")}";
                }
                chains.Add($"{current}split[main{i}][part{i}]");
                chains.Add($"[part{i}]{effect}[hidden{i}]");
                chains.Add($"[main{i}][hidden{i}]overlay={x}:{y}:enable='{enable}'{output}");
            }
            current = output;
        }
        chains.Add($"{current}format=yuv420p[v]");
        return string.Join(";", chains);
    }

    public enum MediaQuality
    {
        High, Balanced, Small,
    }

    public sealed record MediaCompression(MediaQuality Quality = MediaQuality.Balanced, int? MaxHeight = null, bool Hevc = false,
        bool UseTargetSize = false, long TargetBytes = 25_000_000)
    {
        public int H264Crf => Quality switch { MediaQuality.High => 23, MediaQuality.Balanced => 27, _ => 31 };
        public int HevcCrf => Quality switch { MediaQuality.High => 26, MediaQuality.Balanced => 30, _ => 34 };
        public int AudioKbps => Quality switch { MediaQuality.High => 160, MediaQuality.Balanced => 128, _ => 80 };
    }

    private static readonly HashSet<FileFormat> LossyAudio = [FileFormat.Mp3, FileFormat.M4a, FileFormat.Ogg, FileFormat.Opus, FileFormat.Wma];

    public static FileFormat CompressedFormat(FileFormat source) => source.Category() == FormatCategory.Video
        ? source is FileFormat.Mp4 or FileFormat.Mov or FileFormat.Mkv ? source : FileFormat.Mp4
        : LossyAudio.Contains(source) ? source : FileFormat.M4a;

    public static List<string> CompressionArguments(FileFormat source, double? duration, MediaCompression settings)
    {
        var seconds = Math.Max(duration ?? 1, 0.1);
        var audioKbps = settings.AudioKbps;
        if (source.Category() == FormatCategory.Audio)
        {
            if (settings.UseTargetSize)
            {
                audioKbps = (int)(settings.TargetBytes * 8 / seconds / 1000 * 0.95);
            }
            audioKbps = Math.Clamp(audioKbps, 24, 320);
            List<string> codec = CompressedFormat(source) switch
            {
                FileFormat.Mp3 => ["-c:a", "libmp3lame"],
                FileFormat.Ogg => ["-c:a", "vorbis", "-strict", "-2", "-ac", "2"],
                FileFormat.Opus => ["-c:a", "libopus"],
                FileFormat.Wma => ["-c:a", "wmav2"],
                _ => ["-c:a", "aac"],
            };
            return ["-vn", .. codec, "-b:a", $"{audioKbps}k"];
        }
        var filters = new List<string>();
        if (settings.MaxHeight is { } maxHeight)
        {
            filters.Add($"scale=-2:'min({maxHeight},ih)'");
        }
        filters.Add("scale=trunc(iw/2)*2:trunc(ih/2)*2");
        var arguments = new List<string> { "-map", "0:v:0", "-map", "0:a?", "-vf", string.Join(",", filters), "-pix_fmt", "yuv420p" };
        arguments.AddRange(settings.Hevc ? ["-c:v", "libx265", "-preset", "medium", "-tag:v", "hvc1"] : ["-c:v", "libx264", "-preset", "medium"]);
        if (settings.UseTargetSize)
        {
            audioKbps = 96;
            var videoKbps = Math.Max((int)(settings.TargetBytes * 8 / seconds / 1000 * 0.93) - audioKbps, 80);
            arguments.AddRange(["-b:v", $"{videoKbps}k", "-maxrate", $"{videoKbps * 3 / 2}k", "-bufsize", $"{videoKbps * 2}k"]);
        }
        else
        {
            arguments.AddRange(["-crf", (settings.Hevc ? settings.HevcCrf : settings.H264Crf).ToString(CultureInfo.InvariantCulture)]);
        }
        arguments.AddRange(["-c:a", "aac", "-b:a", $"{audioKbps}k"]);
        if (CompressedFormat(source) == FileFormat.Mp4)
        {
            arguments.AddRange(["-movflags", "+faststart"]);
        }
        return arguments;
    }

    public enum VisualizerStyle
    {
        Waveform, Spectrum, Still,
    }

    public enum VisualizerShape
    {
        Landscape, Portrait, Square,
    }

    public static (int Width, int Height) Size(VisualizerShape shape) => shape switch
    {
        VisualizerShape.Landscape => (1920, 1080),
        VisualizerShape.Portrait => (1080, 1920),
        _ => (1080, 1080),
    };

    /// <summary>Inputs and the filter graph for a music video; <paramref name="previewAt"/> renders 1.5 s around a moment.</summary>
    public static List<string> VisualizerArguments(string input, VisualizerStyle style, VisualizerShape shape, uint wave, uint background,
        string? image, double duration, double? previewAt = null)
    {
        var (width, height) = Size(shape);
        var length = Seconds(previewAt is null ? duration : 1.5);
        var arguments = new List<string>();
        if (previewAt is { } at)
        {
            arguments.AddRange(["-ss", Seconds(Math.Max(at - 0.75, 0)), "-t", length]);
        }
        arguments.AddRange(["-i", input]);
        string backdrop;
        if (image is not null)
        {
            arguments.AddRange(["-loop", "1", "-framerate", "30", "-t", length, "-i", image]);
            backdrop = $"[1:v]scale={width}:{height}:force_original_aspect_ratio=increase,crop={width}:{height},setsar=1,format=yuv420p[bg]";
        }
        else
        {
            backdrop = $"color=c={Hex(background)}:s={width}x{height}:r=30:d={length},format=yuv420p[bg]";
        }
        var waveHeight = (int)(height * (shape == VisualizerShape.Portrait ? 0.22 : 0.4)) / 2 * 2;
        const string audio = "[0:a:0]aformat=channel_layouts=mono,dynaudnorm=f=150:g=15,";
        var graph = style switch
        {
            VisualizerStyle.Waveform => $"{backdrop};{audio}showwaves=s={width}x{waveHeight}:mode=cline:scale=sqrt:draw=full:rate=30:colors={Hex(wave)},format=yuva420p[w];"
                + "[bg][w]overlay=(W-w)/2:(H-h)/2:shortest=1,format=yuv420p[v]",
            VisualizerStyle.Spectrum => $"{backdrop};{audio}showfreqs=s={width}x{waveHeight}:mode=bar:ascale=sqrt:fscale=log:win_size=2048:colors={Hex(wave)},"
                + "fps=30,format=yuva420p[w];[bg][w]overlay=(W-w)/2:(H-h)/2:shortest=1,format=yuv420p[v]",
            _ => $"{backdrop};[bg]null[v]",
        };
        arguments.AddRange(["-filter_complex", graph, "-map", "[v]"]);
        return arguments;
    }
}
