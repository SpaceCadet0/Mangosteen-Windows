using System.Globalization;
using System.Text.Json;
using System.Windows.Media.Imaging;
using Mangosteen.Core;
using Mangosteen.Core.Imaging;
using Mangosteen.Core.Media;
using static Mangosteen.Core.Media.MediaMath;

namespace Mangosteen.Media;

/// <summary>The video and audio tools' engine, with the same FFmpeg arguments as the Mac version.</summary>
internal static class MediaTools
{
    private static string Dir(string input) => Path.GetDirectoryName(Path.GetFullPath(input))!;

    private static FileFormat Source(string input) =>
        FileFormats.Detect(input) is { } f && f.Category().IsMedia() ? f : throw new NotSupportedException(L("This is not a video or audio file."));

    private static string Write(string input, string suffix, FileFormat format, Action<string> produce) =>
        OutputNaming.WriteAtomically(Dir(input), $"{OutputNaming.BaseName(input)} ({suffix})", format.Extension(), produce);

    // MARK: Frames and waveforms

    /// <summary>One frame as PNG bytes; <paramref name="maxHeight"/> scales it down.</summary>
    public static byte[] FrameData(string input, double time, int? maxHeight = null, CancellationToken token = default)
    {
        var arguments = new List<string> { "-ss", Seconds(time), "-i", input, "-frames:v", "1", "-an" };
        if (maxHeight is { } height)
        {
            arguments.AddRange(["-vf", $"scale=-2:'min({height},ih)'"]);
        }
        arguments.AddRange(["-f", "image2pipe", "-c:v", "png", "-"]);
        return FFmpeg.RunBytes(arguments, token);
    }

    public static BitmapSource Frame(string input, double time, int? maxHeight = null, CancellationToken token = default) =>
        Decode(FrameData(input, time, maxHeight, token));

    public static BitmapSource Decode(byte[] png)
    {
        var frame = BitmapDecoder.Create(new MemoryStream(png), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        frame.Freeze();
        return frame;
    }

    public static List<BitmapSource> Thumbnails(string input, double duration, int count, int height, CancellationToken token)
    {
        var images = new List<BitmapSource>();
        for (var i = 0; i < count && !token.IsCancellationRequested; i++)
        {
            try
            {
                images.Add(Frame(input, duration * (i + 0.5) / count, height, token));
            }
            catch (InvalidOperationException)
            {
            }
        }
        return images;
    }

    public static float[] Waveform(string input, double duration, int buckets, CancellationToken token)
    {
        var rate = duration > 600 ? 500 : 4000;
        var bytes = FFmpeg.RunBytes(["-i", input, "-map", "0:a:0", "-ac", "1", "-ar", rate.ToString(CultureInfo.InvariantCulture), "-f", "f32le", "-"], token);
        var samples = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, samples, 0, samples.Length * 4);
        return Peaks(samples, buckets);
    }

    // MARK: Trim, split, frames

    private static List<string> CutArguments(FileFormat format, MediaInfo info, bool fast) =>
        fast || format is FileFormat.Wav or FileFormat.Aiff or FileFormat.Flac
            ? ["-map", "0:v?", "-map", "0:a?", "-c", "copy", "-avoid_negative_ts", "make_zero"]
            : MediaConverter.EncodeArguments(format, info);

    private static void Cut(string input, MediaInfo info, FileFormat format, double start, double end, bool fast, string output,
        Action<double>? progress, CancellationToken token)
    {
        var length = Math.Max(end - start, 0.01);
        MediaConverter.Run(input, output, ["-t", Seconds(length), .. CutArguments(format, info, fast)], length, progress, token,
            ["-ss", Seconds(start)]);
    }

    public static string Trim(string input, double start, double end, bool fast, Action<double> progress, CancellationToken token)
    {
        var format = OutputFormat(Source(input));
        var info = MediaConverter.Probe(input, token);
        return Write(input, L("trimmed"), format, output => Cut(input, info, format, start, end, fast, output, progress, token));
    }

    /// <summary>The parts go into a folder "name (parts)".</summary>
    public static string Split(string input, IReadOnlyList<double> points, bool fast, Action<double> progress, CancellationToken token)
    {
        var format = OutputFormat(Source(input));
        var info = MediaConverter.Probe(input, token);
        var duration = info.Duration ?? throw new InvalidOperationException(L("The length of the file is unknown."));
        var bounds = SplitBounds(points, duration);
        var name = OutputNaming.BaseName(input);
        var digits = (bounds.Count - 1).ToString(CultureInfo.InvariantCulture).Length;
        return OutputNaming.WriteAtomically(Dir(input), $"{name} ({L("parts")})", "", folder =>
        {
            Directory.CreateDirectory(folder);
            for (var i = 0; i < bounds.Count - 1; i++)
            {
                var index = i;
                var output = Path.Combine(folder, $"{name} {(i + 1).ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0')}.{format.Extension()}");
                Cut(input, info, format, bounds[i], bounds[i + 1], fast, output, p => progress((index + p) / (bounds.Count - 1)), token);
            }
        });
    }

    public static string SaveFrames(string input, IReadOnlyList<double> times, CancellationToken token)
    {
        var sorted = times.Order().ToList();
        var name = OutputNaming.BaseName(input);
        string FileName(double time) => $"{name} {L("frame")} {Timecode(time).Replace(':', '-')}";
        if (sorted.Count == 1)
        {
            return OutputNaming.WriteAtomically(Dir(input), FileName(sorted[0]), "png", output => File.WriteAllBytes(output, FrameData(input, sorted[0], null, token)));
        }
        return OutputNaming.WriteAtomically(Dir(input), $"{name} ({L("frames")})", "", folder =>
        {
            Directory.CreateDirectory(folder);
            foreach (var time in sorted)
            {
                File.WriteAllBytes(OutputNaming.UniquePath(folder, FileName(time), "png"), FrameData(input, time, null, token));
            }
        });
    }

    // MARK: Speed, join, crop, compress

    public static string ChangeSpeed(string input, double factor, bool keepPitch, Action<double> progress, CancellationToken token)
    {
        var format = OutputFormat(Source(input));
        var info = MediaConverter.Probe(input, token);
        List<string> videoFilters = info.HasVideo ? [$"setpts=PTS/{factor.ToString("0.######", CultureInfo.InvariantCulture)}"] : [];
        var rate = info.SampleRate ?? 48000;
        List<string> audioFilters = keepPitch ? AtempoChain(factor) : [$"asetrate={(int)(rate * factor)}", $"aresample={rate}"];
        var arguments = MediaConverter.EncodeArguments(format, info, videoFilters, audioFilters);
        return Write(input, $"{L("speed")} {SpeedLabel(factor)}", format,
            output => MediaConverter.Run(input, output, arguments, info.Duration / factor, progress, token));
    }

    /// <summary>Videos one after another in one MP4 ("Joined.mp4"), scaled and padded to the first one's size.</summary>
    public static string Join(IReadOnlyList<string> inputs, Action<double> progress, CancellationToken token)
    {
        var infos = inputs.Select(i => MediaConverter.Probe(i, token)).ToList();
        for (var i = 0; i < inputs.Count; i++)
        {
            if (!infos[i].HasVideo)
            {
                throw new InvalidOperationException(string.Format(L("“{0}” has no video."), Path.GetFileName(inputs[i])));
            }
        }
        var first = infos[0];
        int width = (first.Width ?? 1280) / 2 * 2, height = (first.Height ?? 720) / 2 * 2;
        var fps = (first.FrameRate ?? 30).ToString("0.000", CultureInfo.InvariantCulture);
        var graph = new List<string>();
        var concat = "";
        for (var i = 0; i < infos.Count; i++)
        {
            graph.Add($"[{i}:v:0]scale={width}:{height}:force_original_aspect_ratio=decrease,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2,setsar=1,fps={fps},format=yuv420p[v{i}]");
            graph.Add(infos[i].HasAudio
                ? $"[{i}:a:0]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo[a{i}]"
                : $"anullsrc=channel_layout=stereo:sample_rate=48000,atrim=0:{Seconds(infos[i].Duration ?? 0)},aformat=sample_fmts=fltp[a{i}]");
            concat += $"[v{i}][a{i}]";
        }
        graph.Add($"{concat}concat=n={inputs.Count}:v=1:a=1[v][a]");
        var arguments = inputs.SelectMany(i => new[] { "-i", i }).Concat([
            "-filter_complex", string.Join(";", graph), "-map", "[v]", "-map", "[a]",
            "-c:v", "libx264", "-preset", "medium", "-crf", "20", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart",
        ]).ToList();
        var total = infos.Sum(i => i.Duration ?? 0);
        return OutputNaming.WriteAtomically(Dir(inputs[0]), L("Joined"), "mp4", output => FFmpeg.Encode(arguments, output, total, progress, token));
    }

    public static string CropVideo(string input, UnitRect rect, (int Width, int Height)? size, Action<double> progress, CancellationToken token)
    {
        var source = Source(input);
        var info = MediaConverter.Probe(input, token);
        if (!info.HasVideo)
        {
            throw new InvalidOperationException(string.Format(L("“{0}” has no video."), Path.GetFileName(input)));
        }
        var frame = Frame(input, 0, null, token);
        var pixels = rect.ToPixels(frame.PixelWidth, frame.PixelHeight);
        var filters = new List<string> { $"crop={Math.Max(pixels.Width / 2 * 2, 2)}:{Math.Max(pixels.Height / 2 * 2, 2)}:{pixels.X}:{pixels.Y}" };
        if (size is { Width: >= 2, Height: >= 2 } exact)
        {
            filters.Add($"scale={exact.Width / 2 * 2}:{exact.Height / 2 * 2}");
        }
        var format = OutputFormat(source);
        var arguments = MediaConverter.EncodeArguments(format, info, filters);
        return Write(input, L("cropped"), format, output => MediaConverter.Run(input, output, arguments, info.Duration, progress, token));
    }

    public static string Compress(string input, MediaCompression settings, Action<double> progress, CancellationToken token)
    {
        var source = Source(input);
        var info = MediaConverter.Probe(input, token);
        if (source.Category() == FormatCategory.Video && !info.HasVideo)
        {
            throw new InvalidOperationException(string.Format(L("“{0}” has no video."), Path.GetFileName(input)));
        }
        var format = CompressedFormat(source);
        var output = Write(input, L("compressed"), format,
            o => FFmpeg.Encode(["-i", input, .. CompressionArguments(source, info.Duration, settings), "-map_metadata", "0"], o, info.Duration, progress, token));
        var original = new FileInfo(input).Length;
        var result = new FileInfo(output).Length;
        if (result >= original)
        {
            File.Delete(output);
            throw new InvalidOperationException(string.Format(L("The compressed file ({0}) is not smaller than the original ({1}). Choose another format or a smaller size."),
                Tools.ImageIO.Size(result), Tools.ImageIO.Size(original)));
        }
        return output;
    }

    /// <summary>The size after compression, from a 6-second sample in the middle (or the target, in target-size mode).</summary>
    public static long EstimateCompressedSize(string input, MediaCompression settings, CancellationToken token)
    {
        var source = Source(input);
        var info = MediaConverter.Probe(input, token);
        var duration = info.Duration ?? 0;
        if (settings.UseTargetSize)
        {
            return settings.TargetBytes;
        }
        if (source.Category() == FormatCategory.Audio)
        {
            return (long)(Math.Clamp(settings.AudioKbps, 24, 320) * 1000 / 8.0 * duration);
        }
        var sample = Math.Min(duration, 6);
        if (sample <= 0)
        {
            throw new InvalidOperationException(L("The length of the file is unknown."));
        }
        var temp = Path.Combine(Path.GetTempPath(), $"mangosteen-estimate-{Guid.NewGuid():N}.{CompressedFormat(source).Extension()}");
        try
        {
            FFmpeg.Encode(["-ss", Seconds(Math.Max(0, duration / 2 - sample / 2)), "-t", Seconds(sample), "-i", input,
                .. CompressionArguments(source, info.Duration, settings)], temp, sample, null, token);
            return (long)(new FileInfo(temp).Length * duration / sample);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    // MARK: Audio

    private static List<string> AudioCodecForVideo(FileFormat format) => format switch
    {
        FileFormat.Webm => ["-c:a", "libopus", "-b:a", "160k"],
        FileFormat.Avi => ["-c:a", "libmp3lame", "-q:a", "2"],
        FileFormat.Wmv => ["-c:a", "wmav2", "-b:a", "192k"],
        _ => ["-c:a", "aac", "-b:a", "192k"],
    };

    /// <summary>Applies an audio filter (or a graph ending in [a]); the picture of a video is copied untouched.</summary>
    public static string ProcessAudio(string input, string suffix, string? filter, string? graph, Action<double> progress, CancellationToken token)
    {
        var source = Source(input);
        var info = MediaConverter.Probe(input, token);
        if (!info.HasAudio)
        {
            throw new InvalidOperationException(string.Format(L("“{0}” has no sound track."), Path.GetFileName(input)));
        }
        var format = OutputFormat(source);
        var arguments = AudioArguments(input, filter, graph);
        if (source.Category() == FormatCategory.Video && info.HasVideo)
        {
            arguments.AddRange(["-map", "0:v:0", "-c:v", "copy", .. AudioCodecForVideo(format)]);
        }
        else
        {
            arguments.AddRange(MediaConverter.EncodeArguments(format, info).Where(a => a != "-vn"));
        }
        arguments.AddRange(["-map_metadata", "0"]);
        return Write(input, suffix, format, output => FFmpeg.Encode(arguments, output, info.Duration, progress, token));
    }

    private static List<string> AudioArguments(string input, string? filter, string? graph)
    {
        var arguments = new List<string> { "-i", input };
        if (graph is not null)
        {
            arguments.AddRange(["-filter_complex", graph, "-map", "[a]"]);
        }
        else
        {
            arguments.AddRange(["-map", "0:a:0"]);
            if (filter is not null)
            {
                arguments.AddRange(["-af", filter]);
            }
        }
        return arguments;
    }

    /// <summary>Renders the processed sound to a temporary WAV for listening; the caller deletes it.</summary>
    public static string RenderPreview(string input, string? filter, string? graph, double? start, double? length, CancellationToken token)
    {
        var output = Path.Combine(Path.GetTempPath(), $"mangosteen-preview-{Guid.NewGuid():N}.wav");
        var arguments = new List<string>();
        if (start is { } s)
        {
            arguments.AddRange(["-ss", Seconds(s)]);
        }
        if (length is { } l)
        {
            arguments.AddRange(["-t", Seconds(l)]);
        }
        arguments.AddRange(AudioArguments(input, filter, graph));
        arguments.AddRange(["-vn", "-c:a", "pcm_s16le"]);
        FFmpeg.Encode(arguments, output, null, null, token);
        return output;
    }

    public sealed record Loudness(double Integrated, double TruePeak, double Range, double Threshold, double Offset);

    public static Loudness MeasureLoudness(string input, LoudnessTarget target, CancellationToken token)
    {
        // loudnorm prints its JSON report on stderr, which Run discards; capture it through a log file instead.
        var report = Path.Combine(Path.GetTempPath(), $"mangosteen-loudness-{Guid.NewGuid():N}.txt");
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo(FFmpeg.FFmpegPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            foreach (var argument in new[] { "-hide_banner", "-nostdin", "-i", input, "-map", "0:a:0", "-vn", "-af", LoudnormFilter(target) + ":print_format=json", "-f", "null", "-" })
            {
                start.ArgumentList.Add(argument);
            }
            using var process = System.Diagnostics.Process.Start(start)!;
            using var cancel = token.Register(() =>
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }
            });
            var log = process.StandardError.ReadToEnd();
            process.WaitForExit();
            token.ThrowIfCancellationRequested();
            var open = log.LastIndexOf('{');
            var close = log.LastIndexOf('}');
            if (open < 0 || close < open)
            {
                throw new InvalidOperationException(L("The loudness could not be measured."));
            }
            using var json = JsonDocument.Parse(log[open..(close + 1)]);
            double Value(string key) => json.RootElement.TryGetProperty(key, out var v)
                && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) ? d : -70;
            return new Loudness(Value("input_i"), Value("input_tp"), Value("input_lra"), Value("input_thresh"), Value("target_offset"));
        }
        finally
        {
            File.Delete(report);
        }
    }

    public static string NormalizeFilter(Loudness measured, LoudnessTarget target, int sampleRate)
    {
        string F(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);
        return LoudnormFilter(target) + $":measured_I={F(measured.Integrated)}:measured_TP={F(measured.TruePeak)}:measured_LRA={F(measured.Range)}"
            + $":measured_thresh={F(measured.Threshold)}:offset={F(measured.Offset)}:linear=true,aresample={sampleRate}";
    }

    public static string Normalize(string input, LoudnessTarget target, Action<double> progress, CancellationToken token)
    {
        var measured = MeasureLoudness(input, target, token);
        var rate = MediaConverter.Probe(input, token).SampleRate ?? 48000;
        return ProcessAudio(input, L("volume"), NormalizeFilter(measured, target, rate), null, progress, token);
    }

    // MARK: Visualizer and video redaction

    public static string Visualize(string input, VisualizerStyle style, VisualizerShape shape, uint wave, uint background, string? image,
        Action<double> progress, CancellationToken token)
    {
        var info = MediaConverter.Probe(input, token);
        if (!info.HasAudio || info.Duration is not { } duration)
        {
            throw new InvalidOperationException(string.Format(L("“{0}” has no sound track."), Path.GetFileName(input)));
        }
        var arguments = VisualizerArguments(input, style, shape, wave, background, image, duration);
        arguments.AddRange(["-map", "0:a:0", "-c:v", "libx264", "-preset", "medium", "-crf", "20", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-b:a", "192k", "-shortest", "-movflags", "+faststart"]);
        return OutputNaming.WriteAtomically(Dir(input), $"{OutputNaming.BaseName(input)} ({L("visualizer")})", "mp4",
            output => FFmpeg.Encode(arguments, output, duration, progress, token));
    }

    public static BitmapSource VisualizerFrame(string input, VisualizerStyle style, VisualizerShape shape, uint wave, uint background, string? image,
        double duration, double at, CancellationToken token) =>
        Decode(FFmpeg.RunBytes([.. VisualizerArguments(input, style, shape, wave, background, image, duration, at),
            "-ss", "0.75", "-frames:v", "1", "-f", "image2pipe", "-c:v", "png", "-"], token));

    private static List<string> VideoCodec(FileFormat format) => format switch
    {
        FileFormat.Webm => ["-c:v", "libvpx-vp9", "-crf", "30", "-b:v", "0", "-row-mt", "1", "-deadline", "good", "-cpu-used", "4"],
        FileFormat.Avi => ["-c:v", "mpeg4", "-q:v", "2"],
        FileFormat.Wmv => ["-c:v", "wmv2", "-q:v", "2"],
        _ => ["-c:v", "libx264", "-preset", "medium", "-crf", "18"],
    };

    public static string RedactVideo(string input, IReadOnlyList<VideoRegion> regions, RedactionStyle style, uint color,
        Action<double> progress, CancellationToken token)
    {
        var format = OutputFormat(Source(input));
        var info = MediaConverter.Probe(input, token);
        var frame = Frame(input, 0, null, token);
        var graph = RedactionGraph(regions, style, color, frame.PixelWidth, frame.PixelHeight);
        var arguments = new List<string> { "-i", input, "-filter_complex", graph, "-map", "[v]", "-map", "0:a?" };
        arguments.AddRange(VideoCodec(format));
        arguments.AddRange(["-c:a", "copy"]);
        if (format == FileFormat.Mp4)
        {
            arguments.AddRange(["-movflags", "+faststart"]);
        }
        return Write(input, L("redacted"), format, output => FFmpeg.Encode(arguments, output, info.Duration, progress, token));
    }

    public static BitmapSource RedactedFrame(string input, IReadOnlyList<VideoRegion> regions, RedactionStyle style, uint color,
        int width, int height, double at, CancellationToken token) =>
        Decode(FFmpeg.RunBytes(["-ss", Seconds(at), "-copyts", "-i", input, "-filter_complex", RedactionGraph(regions, style, color, width, height),
            "-map", "[v]", "-frames:v", "1", "-f", "image2pipe", "-c:v", "png", "-"], token));
}
