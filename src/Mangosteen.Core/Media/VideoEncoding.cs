using System.Globalization;
using System.Text.RegularExpressions;

namespace Mangosteen.Core.Media;

public enum VideoCodec
{
    H264,
    Hevc,
    Av1,
}

/// <summary>The graphics card's video encoder family, as FFmpeg names it.</summary>
public enum HardwareFamily
{
    None,
    /// <summary>NVIDIA NVENC.</summary>
    Nvenc,
    /// <summary>Intel Quick Sync.</summary>
    Qsv,
    /// <summary>AMD AMF.</summary>
    Amf,
}

/// <summary>
/// Which encoder makes the picture when video is re-encoded into MP4, MOV or MKV. Every tool that re-encodes video asks
/// here, so the choice in Settings applies everywhere. Compression keeps its own settings, and a source the container
/// already accepts is still copied without re-encoding.
/// </summary>
public sealed record VideoEncoding(VideoCodec Codec = VideoCodec.H264, bool Hardware = false)
{
    private static readonly Lock Gate = new();
    private static VideoEncoding _current = new();

    /// <summary>The setting in effect; the app updates it when Settings change.</summary>
    public static VideoEncoding Current
    {
        get { lock (Gate) { return _current; } }
        set { lock (Gate) { _current = value; } }
    }

    /// <summary>
    /// The hardware encoders that worked in a test encode on this PC (h264_nvenc, hevc_qsv…); the app fills it in
    /// once, the first time hardware encoding is on.
    /// </summary>
    public static IReadOnlySet<string> WorkingHardwareEncoders { get; set; } = new HashSet<string>();

    public static string Title(VideoCodec codec) => codec switch
    {
        VideoCodec.H264 => "H.264",
        VideoCodec.Hevc => "HEVC (H.265)",
        _ => "AV1",
    };

    /// <summary>The hardware encoder for a codec on this PC, NVIDIA first, then Intel, then AMD; null for none.</summary>
    public static string? HardwareEncoder(VideoCodec codec)
    {
        var prefix = codec switch { VideoCodec.H264 => "h264", VideoCodec.Hevc => "hevc", _ => "av1" };
        foreach (var family in new[] { "nvenc", "qsv", "amf" })
        {
            var name = $"{prefix}_{family}";
            if (WorkingHardwareEncoders.Contains(name))
            {
                return name;
            }
        }
        return null;
    }

    /// <summary>
    /// Encoder arguments for <paramref name="container"/> (MP4, MOV, MKV). <paramref name="crf"/> is the quality in
    /// libx264 terms (lower is better); the other encoders get the matching value. MOV cannot hold AV1, so AV1 for MOV
    /// is saved as H.264. Without a working hardware encoder the software one is used.
    /// </summary>
    public IReadOnlyList<string> Arguments(FileFormat container, int crf = 20)
    {
        var codec = Codec == VideoCodec.Av1 && container == FileFormat.Mov ? VideoCodec.H264 : Codec;
        string[] hvc1 = container == FileFormat.Mkv ? [] : ["-tag:v", "hvc1"];
        if (Hardware && HardwareEncoder(codec) is { } encoder)
        {
            var q = (crf + (codec == VideoCodec.H264 ? 3 : 5)).ToString(CultureInfo.InvariantCulture);
            string[] tag = codec == VideoCodec.Hevc ? hvc1 : [];
            if (encoder.EndsWith("_nvenc", StringComparison.Ordinal))
            {
                return ["-c:v", encoder, "-preset", "p5", "-rc", "vbr", "-cq", q, "-b:v", "0", "-pix_fmt", "yuv420p", .. tag];
            }
            if (encoder.EndsWith("_qsv", StringComparison.Ordinal))
            {
                return ["-c:v", encoder, "-preset", "medium", "-global_quality", q, "-pix_fmt", "nv12", .. tag];
            }
            return ["-c:v", encoder, "-quality", "balanced", "-rc", "cqp", "-qp_i", q, "-qp_p", q, "-qp_b", q, "-pix_fmt", "nv12", .. tag];
        }
        return codec switch
        {
            VideoCodec.Hevc => ["-c:v", "libx265", "-preset", "medium", "-crf", (crf + 4).ToString(CultureInfo.InvariantCulture), "-pix_fmt", "yuv420p", .. hvc1],
            VideoCodec.Av1 => ["-c:v", "libsvtav1", "-preset", "8", "-crf", (crf + 12).ToString(CultureInfo.InvariantCulture), "-pix_fmt", "yuv420p"],
            _ => ["-c:v", "libx264", "-preset", "medium", "-crf", crf.ToString(CultureInfo.InvariantCulture), "-pix_fmt", "yuv420p"],
        };
    }

    /// <summary>Every hardware encoder name worth a test encode.</summary>
    public static readonly IReadOnlyList<string> HardwareCandidates =
    [
        "h264_nvenc", "hevc_nvenc", "av1_nvenc", "h264_qsv", "hevc_qsv", "av1_qsv", "h264_amf", "hevc_amf", "av1_amf",
    ];
}

/// <summary>Reading FFmpeg's cropdetect: where the black bars of a video end.</summary>
public static partial class CropDetect
{
    public readonly record struct Box(int X, int Y, int Width, int Height)
    {
        public int Right => X + Width;
        public int Bottom => Y + Height;
    }

    /// <summary>The last "crop=w:h:x:y" cropdetect printed, or null.</summary>
    public static Box? LastCrop(string log)
    {
        var matches = CropPattern().Matches(log);
        if (matches.Count == 0)
        {
            return null;
        }
        var g = matches[^1].Groups;
        var box = new Box(int.Parse(g[3].Value, CultureInfo.InvariantCulture), int.Parse(g[4].Value, CultureInfo.InvariantCulture),
            int.Parse(g[1].Value, CultureInfo.InvariantCulture), int.Parse(g[2].Value, CultureInfo.InvariantCulture));
        return box.Width > 0 && box.Height > 0 ? box : null;
    }

    /// <summary>
    /// The picture area that holds every moment checked (so a dark scene does not eat into the picture), within the
    /// frame; null when the bars are thinner than 1% of the frame, which is noise or a few dark pixels at the edge.
    /// Sizes are even, as the 4:2:0 encoders need.
    /// </summary>
    public static Box? PictureArea(IReadOnlyList<Box> found, int frameWidth, int frameHeight)
    {
        if (found.Count == 0 || frameWidth <= 0 || frameHeight <= 0)
        {
            return null;
        }
        var left = Math.Max(0, found.Min(b => b.X));
        var top = Math.Max(0, found.Min(b => b.Y));
        var right = Math.Min(frameWidth, found.Max(b => b.Right));
        var bottom = Math.Min(frameHeight, found.Max(b => b.Bottom));
        var width = (right - left) / 2 * 2;
        var height = (bottom - top) / 2 * 2;
        var trimmed = frameWidth - width + frameHeight - height;
        if (trimmed < Math.Min(frameWidth, frameHeight) * 0.02 || width < 16 || height < 16)
        {
            return null;
        }
        return new Box(left, top, width, height);
    }

    [GeneratedRegex(@"crop=(\d+):(\d+):(\d+):(\d+)")]
    private static partial Regex CropPattern();
}
