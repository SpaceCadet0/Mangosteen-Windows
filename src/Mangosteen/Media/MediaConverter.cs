using System.Globalization;
using System.Text.Json;
using System.Windows.Media.Imaging;
using Mangosteen.Conversion;
using Mangosteen.Core;

namespace Mangosteen.Media;

/// <summary>What ffprobe reports about a media file.</summary>
internal sealed record MediaInfo(double? Duration, string? VideoCodec, string? AudioCodec, int? Width, int? Height,
    int? AudioChannels, double? FrameRate, int? SampleRate)
{
    public bool HasVideo => VideoCodec is not null;
    public bool HasAudio => AudioCodec is not null;
}

/// <summary>
/// Video and audio conversion through FFmpeg, the same as on the Mac: when the target container accepts the source
/// codecs the streams are copied (instant and lossless), otherwise they are re-encoded.
/// </summary>
internal static class MediaConverter
{
    public static MediaInfo Probe(string path, CancellationToken token = default)
    {
        var json = FFmpeg.Run("ffprobe.exe", ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", path], token);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        JsonElement? video = null, audio = null;
        if (root.TryGetProperty("streams", out var streams))
        {
            foreach (var stream in streams.EnumerateArray())
            {
                var type = Text(stream, "codec_type");
                // Cover art is reported as a video stream; it is not a picture track.
                var coverArt = stream.TryGetProperty("disposition", out var disposition)
                    && disposition.TryGetProperty("attached_pic", out var attached) && attached.GetInt32() == 1;
                if (type == "video" && !coverArt && video is null)
                {
                    video = stream;
                }
                else if (type == "audio" && audio is null)
                {
                    audio = stream;
                }
            }
        }
        double? duration = root.TryGetProperty("format", out var format) && Text(format, "duration") is { } d
            && double.TryParse(d, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ? seconds : null;
        // MP3 without a VBR header has its duration guessed from the bitrate; counting the frames is exact.
        if (video is null && audio is { } a && Text(a, "codec_name") == "mp3" && CountedDuration(path, token) is { } counted)
        {
            duration = counted;
        }
        return new MediaInfo(
            duration,
            video is { } v ? Text(v, "codec_name") : null,
            audio is { } au ? Text(au, "codec_name") : null,
            video is { } vw && vw.TryGetProperty("width", out var width) ? width.GetInt32() : null,
            video is { } vh && vh.TryGetProperty("height", out var height) ? height.GetInt32() : null,
            audio is { } ac && ac.TryGetProperty("channels", out var channels) ? channels.GetInt32() : null,
            video is { } vr ? Rate(Text(vr, "avg_frame_rate")) ?? Rate(Text(vr, "r_frame_rate")) : null,
            audio is { } ar && Text(ar, "sample_rate") is { } rate && int.TryParse(rate, out var sampleRate) ? sampleRate : null);
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>"30000/1001" is 29.97; null for "0/0".</summary>
    public static double? Rate(string? fraction)
    {
        var parts = fraction?.Split('/');
        return parts is { Length: 2 } && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var m) && n > 0 && m > 0 ? n / m : null;
    }

    private static double? CountedDuration(string path, CancellationToken token)
    {
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo(FFmpeg.FFmpegPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            foreach (var argument in new[] { "-hide_banner", "-nostdin", "-v", "error", "-stats", "-i", path, "-map", "0:a:0", "-c", "copy", "-f", "null", "-" })
            {
                start.ArgumentList.Add(argument);
            }
            using var process = System.Diagnostics.Process.Start(start)!;
            var log = process.StandardError.ReadToEnd();
            process.WaitForExit();
            var matches = System.Text.RegularExpressions.Regex.Matches(log, @"time=(\d+):(\d+):(\d+(?:\.\d+)?)");
            if (matches.Count == 0)
            {
                return null;
            }
            var last = matches[^1].Groups;
            var total = double.Parse(last[1].Value, CultureInfo.InvariantCulture) * 3600 + double.Parse(last[2].Value, CultureInfo.InvariantCulture) * 60
                + double.Parse(last[3].Value, CultureInfo.InvariantCulture);
            return total > 0 ? total : null;
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or FormatException)
        {
            return null;
        }
    }

    public static void Convert(string input, FileFormat target, string output, Action<double>? progress, CancellationToken token)
    {
        var info = Probe(input, token);
        if (target.Category() == FormatCategory.Audio && !info.HasAudio || target == FileFormat.Mp3 && !info.HasAudio)
        {
            throw new InvalidOperationException(string.Format(L("“{0}” has no sound track."), Path.GetFileName(input)));
        }
        if (target.Category() == FormatCategory.Video && target != FileFormat.Mp3 && !info.HasVideo)
        {
            throw new InvalidOperationException(string.Format(L("“{0}” has no video."), Path.GetFileName(input)));
        }
        if (RemuxArguments(target, info) is { } copy)
        {
            try
            {
                Run(input, output, copy, info.Duration, progress, token);
                return;
            }
            catch (InvalidOperationException)
            {
                // Some streams cannot be copied into this container after all; re-encode instead.
                File.Delete(output);
            }
        }
        Run(input, output, EncodeArguments(target, info), info.Duration, progress, token);
    }

    public static void Run(string input, string output, IEnumerable<string> codecArguments, double? duration, Action<double>? progress,
        CancellationToken token, IEnumerable<string>? inputArguments = null)
    {
        var arguments = new List<string>(inputArguments ?? []) { "-i", input };
        arguments.AddRange(codecArguments);
        arguments.AddRange(["-map_metadata", "0"]);
        FFmpeg.Encode(arguments, output, duration, progress, token);
    }

    /// <summary>Stream-copy arguments when the target container accepts the source codecs, otherwise null.</summary>
    public static List<string>? RemuxArguments(FileFormat target, MediaInfo info)
    {
        var video = info.VideoCodec;
        var audio = info.AudioCodec;
        bool AudioFits(params string[] codecs) => audio is null || codecs.Contains(audio);
        switch (target)
        {
            case FileFormat.Mp4 or FileFormat.Mov:
                if (video is not ("h264" or "hevc") || !AudioFits("aac", "mp3", "alac"))
                {
                    return null;
                }
                var arguments = new List<string> { "-map", "0:v:0", "-map", "0:a?", "-c", "copy" };
                if (video == "hevc")
                {
                    arguments.AddRange(["-tag:v", "hvc1"]);
                }
                if (target == FileFormat.Mp4)
                {
                    arguments.AddRange(["-movflags", "+faststart"]);
                }
                return arguments;
            case FileFormat.Mkv:
                return info.HasVideo ? ["-map", "0:v:0", "-map", "0:a?", "-map", "0:s?", "-c", "copy"] : null;
            case FileFormat.Webm:
                return video is "vp8" or "vp9" or "av1" && AudioFits("opus", "vorbis") ? ["-map", "0:v:0", "-map", "0:a?", "-c", "copy"] : null;
            case FileFormat.M4a:
                return audio is "aac" or "alac" ? ["-vn", "-c:a", "copy"] : null;
            case FileFormat.Opus:
                return audio == "opus" ? ["-vn", "-c:a", "copy"] : null;
            case FileFormat.Ogg:
                return audio is "vorbis" or "opus" ? ["-vn", "-c:a", "copy"] : null;
            case FileFormat.Flac:
                return audio == "flac" ? ["-vn", "-c:a", "copy"] : null;
            case FileFormat.Mp3:
                return audio == "mp3" ? ["-vn", "-c:a", "copy"] : null;
            default:
                return null;
        }
    }

    /// <summary>Re-encoding arguments for each target. <paramref name="videoFilters"/> run before the even-size scaling.</summary>
    public static List<string> EncodeArguments(FileFormat target, MediaInfo info, IReadOnlyList<string>? videoFilters = null,
        IReadOnlyList<string>? audioFilters = null)
    {
        var arguments = VideoCodecArguments(target, videoFilters ?? []);
        if (info.HasAudio && audioFilters is { Count: > 0 })
        {
            arguments.AddRange(["-af", string.Join(",", audioFilters)]);
        }
        return arguments;
    }

    private static List<string> VideoCodecArguments(FileFormat target, IReadOnlyList<string> videoFilters)
    {
        // H.264 and the other 4:2:0 encoders need even dimensions.
        string[] evenSize = ["-vf", string.Join(",", videoFilters.Append("scale=trunc(iw/2)*2:trunc(ih/2)*2"))];
        string[] h264 = ["-c:v", "libx264", "-preset", "medium", "-crf", "20", "-pix_fmt", "yuv420p", .. evenSize];
        string[] aac = ["-c:a", "aac", "-b:a", "192k"];
        string[] maps = ["-map", "0:v:0", "-map", "0:a?"];
        return target switch
        {
            FileFormat.Mp4 => [.. maps, .. h264, .. aac, "-movflags", "+faststart"],
            FileFormat.Mov or FileFormat.Mkv => [.. maps, .. h264, .. aac],
            FileFormat.Webm => [.. maps, "-c:v", "libvpx-vp9", "-crf", "32", "-b:v", "0", "-row-mt", "1", "-deadline", "good", "-cpu-used", "4",
                "-pix_fmt", "yuv420p", "-c:a", "libopus", "-b:a", "128k", .. evenSize],
            FileFormat.Avi => [.. maps, "-c:v", "mpeg4", "-q:v", "3", "-c:a", "libmp3lame", "-q:a", "3", .. evenSize],
            FileFormat.Wmv => [.. maps, "-c:v", "wmv2", "-q:v", "3", "-c:a", "wmav2", "-b:a", "192k", .. evenSize],
            FileFormat.Gif => ["-an", "-vf", string.Concat(videoFilters.Select(f => f + ","))
                + "fps=15,scale='min(720,iw)':-2:flags=lanczos,split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=4",
                "-loop", "0"],
            FileFormat.Mp3 => ["-vn", "-c:a", "libmp3lame", "-q:a", "2"],
            FileFormat.M4a => ["-vn", "-c:a", "aac", "-b:a", "256k"],
            FileFormat.Wav => ["-vn", "-c:a", "pcm_s16le"],
            FileFormat.Aiff => ["-vn", "-c:a", "pcm_s16be"],
            FileFormat.Flac => ["-vn", "-c:a", "flac"],
            // The built-in Vorbis encoder is experimental and stereo only.
            FileFormat.Ogg => ["-vn", "-c:a", "vorbis", "-strict", "-2", "-ac", "2", "-b:a", "192k"],
            FileFormat.Opus => ["-vn", "-c:a", "libopus", "-b:a", "160k"],
            FileFormat.Wma => ["-vn", "-c:a", "wmav2", "-b:a", "192k"],
            _ => [],
        };
    }

    /// <summary>
    /// An image as AVIF (AV1 still picture). Transparency is flattened onto white, as for JPG.
    /// </summary>
    public static void ImageToAvif(string input, string output, CancellationToken token)
    {
        var image = ImageConverter.FlattenOnWhite(ImageConverter.Decode(input));
        var temp = Path.Combine(Path.GetTempPath(), $"mangosteen-{Guid.NewGuid():N}.png");
        try
        {
            using (var stream = File.Create(temp))
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                encoder.Save(stream);
            }
            FFmpeg.Encode(["-i", temp, "-c:v", "libaom-av1", "-still-picture", "1", "-crf", "28", "-cpu-used", "6",
                "-pix_fmt", "yuv420p", "-vf", "scale=trunc(iw/2)*2:trunc(ih/2)*2"], output, null, null, token);
        }
        finally
        {
            File.Delete(temp);
        }
    }
}
