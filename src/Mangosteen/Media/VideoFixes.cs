using System.Globalization;
using System.Text.Json;
using Mangosteen.Core;
using Mangosteen.Core.Media;

namespace Mangosteen.Media;

/// <summary>One-click video fixes from the Tools wheel: black bars, HDR to SDR, subtitles out of a video, Live Photos.</summary>
internal static class VideoFixes
{
    private static string Dir(string input) => Path.GetDirectoryName(Path.GetFullPath(input))!;

    private static MediaInfo VideoInfo(string input, CancellationToken token)
    {
        var info = MediaConverter.Probe(input, token);
        if (!info.HasVideo)
        {
            throw new Tools.ShownException(string.Format(L("“{0}” has no video."), Path.GetFileName(input)));
        }
        return info;
    }

    // ---------------------------------------------------------------- Black bars

    /// <summary>
    /// The picture area without the black bars, in upright frame pixels, or null when there are none. Three moments of
    /// the video are checked so a dark opening scene does not eat into the picture.
    /// </summary>
    public static CropDetect.Box? BlackBarsCrop(string input, CancellationToken token)
    {
        var info = VideoInfo(input, token);
        var frame = MediaTools.Frame(input, 0, null, token);
        var duration = info.Duration ?? 0;
        var found = new List<CropDetect.Box>();
        foreach (var fraction in new[] { 0.2, 0.5, 0.8 })
        {
            var start = duration > 4 ? duration * fraction : 0;
            var log = new System.Text.StringBuilder();
            FFmpeg.Run("ffmpeg.exe", ["-hide_banner", "-nostdin", "-ss", start.ToString("0.00", CultureInfo.InvariantCulture), "-i", input,
                "-t", "2", "-vf", "cropdetect=limit=24:round=2:reset=0", "-an", "-f", "null", "-"], token,
                onErrorLine: line =>
                {
                    lock (log)
                    {
                        log.AppendLine(line);
                    }
                });
            if (CropDetect.LastCrop(log.ToString()) is { } box)
            {
                found.Add(box);
            }
        }
        return CropDetect.PictureArea(found, frame.PixelWidth, frame.PixelHeight);
    }

    /// <summary>Writes "name (no bars).ext" with the black bars cut away.</summary>
    public static string RemoveBlackBars(string input, Action<double> progress, CancellationToken token)
    {
        var source = FileFormats.Detect(input) ?? FileFormat.Mp4;
        var crop = BlackBarsCrop(input, token)
            ?? throw new Tools.ShownException(string.Format(L("“{0}” has no black bars."), Path.GetFileName(input)));
        var info = MediaConverter.Probe(input, token);
        var format = MediaMath.OutputFormat(source);
        var arguments = MediaConverter.EncodeArguments(format, info, [$"crop={crop.Width}:{crop.Height}:{crop.X}:{crop.Y}"]);
        return OutputNaming.WriteAtomically(Dir(input), $"{OutputNaming.BaseName(input)} ({L("no bars")})", format.Extension(),
            output => MediaConverter.Run(input, output, arguments, info.Duration, progress, token));
    }

    // ---------------------------------------------------------------- HDR to SDR

    /// <summary>The transfer function of the first video stream ("smpte2084" for HDR10, "arib-std-b67" for HLG).</summary>
    private static string? Transfer(string input, CancellationToken token)
    {
        var json = FFmpeg.Run("ffprobe.exe", ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=color_transfer", "-of", "json", input], token);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("streams", out var streams) && streams.GetArrayLength() > 0
            && streams[0].TryGetProperty("color_transfer", out var transfer) ? transfer.GetString() : null;
    }

    /// <summary>
    /// Writes "name (SDR).mp4": HDR video (HDR10, HLG) brought to ordinary brightness and Rec. 709 colour by FFmpeg's
    /// tone mapping (zscale and the Hable curve), with the video codec from Settings.
    /// </summary>
    public static string HdrToSdr(string input, Action<double> progress, CancellationToken token)
    {
        var info = VideoInfo(input, token);
        var transfer = Transfer(input, token);
        if (transfer is not ("smpte2084" or "arib-std-b67"))
        {
            throw new Tools.ShownException(string.Format(L("“{0}” is not HDR video."), Path.GetFileName(input)));
        }
        string[] filters =
        [
            $"zscale=t=linear:npl=100{(transfer == "arib-std-b67" ? ":tin=arib-std-b67" : "")}",
            "format=gbrpf32le", "zscale=p=bt709", "tonemap=tonemap=hable:desat=0", "zscale=t=bt709:m=bt709:r=tv", "format=yuv420p",
        ];
        var arguments = MediaConverter.EncodeArguments(FileFormat.Mp4, info, filters);
        arguments.AddRange(["-color_primaries", "bt709", "-color_trc", "bt709", "-colorspace", "bt709"]);
        return OutputNaming.WriteAtomically(Dir(input), $"{OutputNaming.BaseName(input)} (SDR)", "mp4",
            output => MediaConverter.Run(input, output, arguments, info.Duration, progress, token));
    }

    // ---------------------------------------------------------------- Subtitles

    private sealed record SubtitleStream(int Index, string Codec, string? Language, string? Title);

    private static List<SubtitleStream> SubtitleStreams(string input, CancellationToken token)
    {
        var json = FFmpeg.Run("ffprobe.exe", ["-v", "error", "-select_streams", "s", "-show_entries", "stream=index,codec_name:stream_tags=language,title",
            "-of", "json", input], token);
        using var document = JsonDocument.Parse(json);
        var list = new List<SubtitleStream>();
        if (document.RootElement.TryGetProperty("streams", out var streams))
        {
            var number = 0;
            foreach (var stream in streams.EnumerateArray())
            {
                string? Tag(string name) => stream.TryGetProperty("tags", out var tags) && tags.TryGetProperty(name, out var v) ? v.GetString() : null;
                list.Add(new SubtitleStream(number++, stream.TryGetProperty("codec_name", out var codec) ? codec.GetString() ?? "" : "",
                    Tag("language"), Tag("title")));
            }
        }
        return list;
    }

    /// <summary>
    /// The text subtitles inside a video (SRT, ASS, MP4 text, WebVTT) as files next to it: one as "name (subtitles).srt",
    /// several in a folder, one file per language. Picture subtitles (DVD, Blu-ray) cannot become text.
    /// </summary>
    public static string ExtractSubtitles(string input, Action<double> progress, CancellationToken token)
    {
        var streams = SubtitleStreams(input, token);
        if (streams.Count == 0)
        {
            throw new Tools.ShownException(string.Format(L("“{0}” has no subtitles."), Path.GetFileName(input)));
        }
        var text = streams.Where(s => s.Codec is "subrip" or "srt" or "ass" or "ssa" or "mov_text" or "webvtt" or "text").ToList();
        if (text.Count == 0)
        {
            throw new Tools.ShownException(string.Format(L("The subtitles in “{0}” are pictures (DVD or Blu-ray), not text."), Path.GetFileName(input)));
        }
        string Extension(SubtitleStream s) => s.Codec is "ass" or "ssa" ? "ass" : "srt";
        void Extract(SubtitleStream s, string output) =>
            FFmpeg.Run("ffmpeg.exe", ["-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-i", input, "-map", $"0:s:{s.Index}",
                "-c:s", Extension(s) == "ass" ? "ass" : "srt", output], token);
        var name = OutputNaming.BaseName(input);
        if (text.Count == 1)
        {
            var only = text[0];
            return OutputNaming.WriteAtomically(Dir(input), $"{name} ({L("subtitles")})", Extension(only), output => Extract(only, output));
        }
        return OutputNaming.WriteAtomically(Dir(input), $"{name} ({L("subtitles")})", "", folder =>
        {
            Directory.CreateDirectory(folder);
            for (var i = 0; i < text.Count; i++)
            {
                var label = text[i].Language is { Length: > 0 } language && language != "und" ? language : (i + 1).ToString(CultureInfo.InvariantCulture);
                Extract(text[i], OutputNaming.UniquePath(folder, $"{name} ({label})", Extension(text[i])));
                progress((i + 1.0) / text.Count);
            }
        });
    }

    // ---------------------------------------------------------------- Live Photo

    /// <summary>
    /// Writes "name (Live).mp4" from the video half of a Live Photo: the .mov of the same name next to the picture. It is
    /// copied as it is when MP4 can hold it.
    /// </summary>
    public static string LivePhotoVideo(string picture, Action<double> progress, CancellationToken token)
    {
        var movie = WheelCatalog.LivePhotoMovie(picture)
            ?? throw new Tools.ShownException(string.Format(L("“{0}” has no video beside it."), Path.GetFileName(picture)));
        var info = VideoInfo(movie, token);
        return OutputNaming.WriteAtomically(Dir(picture), $"{OutputNaming.BaseName(picture)} (Live)", "mp4", output =>
        {
            if (MediaConverter.RemuxArguments(FileFormat.Mp4, info) is { } copy)
            {
                try
                {
                    MediaConverter.Run(movie, output, copy, info.Duration, progress, token);
                    return;
                }
                catch (InvalidOperationException)
                {
                    File.Delete(output);
                }
            }
            MediaConverter.Run(movie, output, MediaConverter.EncodeArguments(FileFormat.Mp4, info), info.Duration, progress, token);
        });
    }
}
