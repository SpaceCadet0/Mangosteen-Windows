using Mangosteen.Conversion;
using Mangosteen.Core;
using Mangosteen.Media;

namespace Mangosteen.Windows.Tests;

/// <summary>Needs the bundled FFmpeg (CI fetches it before building); skipped in builds without it.</summary>
public sealed class MediaTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mangosteen-media").FullName;

    public MediaTests() => FormatCatalog.MediaAvailable = FFmpeg.IsBundled;

    public void Dispose()
    {
        FormatCatalog.MediaAvailable = false;
        Directory.Delete(_dir, recursive: true);
    }

    private string PathOf(string name) => Path.Combine(_dir, name);

    /// <summary>A test video (H.264 + AAC) or audio made by FFmpeg itself.</summary>
    private string Make(string name, double seconds = 2, bool video = true, bool audio = true)
    {
        var path = PathOf(name);
        var arguments = new List<string>();
        if (video)
        {
            arguments.AddRange(["-f", "lavfi", "-i", $"testsrc2=size=320x240:rate=25:duration={seconds}"]);
        }
        if (audio)
        {
            arguments.AddRange(["-f", "lavfi", "-i", $"sine=frequency=440:duration={seconds}"]);
        }
        if (video)
        {
            arguments.AddRange(["-c:v", "libx264", "-pix_fmt", "yuv420p"]);
        }
        if (audio)
        {
            arguments.AddRange([name.EndsWith(".wav") ? "-c:a" : "-c:a", name.EndsWith(".wav") ? "pcm_s16le" : "aac"]);
        }
        FFmpeg.Encode(arguments, path, null, null, CancellationToken.None);
        return path;
    }

    [SkippableFact]
    public void Mp4RemuxesToMkvAndReencodesToWebm()
    {
        Skip.IfNot(FFmpeg.IsBundled);
        var mp4 = Make("клип.mp4");
        var progress = new List<double>();
        var mkv = Converter.Convert(mp4, FileFormat.Mkv, default, progress.Add)!;
        var info = MediaConverter.Probe(mkv);
        Assert.Equal("h264", info.VideoCodec);
        Assert.Equal("aac", info.AudioCodec);
        Assert.InRange(info.Duration ?? 0, 1.8, 2.3);
        Assert.Equal(1, progress[^1]);

        var webm = MediaConverter.Probe(Converter.Convert(mp4, FileFormat.Webm)!);
        Assert.Equal("vp9", webm.VideoCodec);
        Assert.Equal("opus", webm.AudioCodec);
    }

    [SkippableFact]
    public void VideoToGifAndMp3()
    {
        Skip.IfNot(FFmpeg.IsBundled);
        var mp4 = Make("clip.mp4");
        Assert.Equal("gif", MediaConverter.Probe(Converter.Convert(mp4, FileFormat.Gif)!).VideoCodec);
        var mp3 = MediaConverter.Probe(Converter.Convert(mp4, FileFormat.Mp3)!);
        Assert.Equal("mp3", mp3.AudioCodec);
        Assert.False(mp3.HasVideo);
    }

    [SkippableFact]
    public void SilentVideoHasNoMp3()
    {
        Skip.IfNot(FFmpeg.IsBundled);
        var mp4 = Make("silent.mp4", audio: false);
        var error = Assert.Throws<InvalidOperationException>(() => Converter.Convert(mp4, FileFormat.Mp3));
        Assert.Contains("no sound", error.Message);
        Assert.Single(Directory.GetFiles(_dir));
    }

    [SkippableFact]
    public void AudioFormats()
    {
        Skip.IfNot(FFmpeg.IsBundled);
        var wav = Make("песня.wav", video: false);
        foreach (var target in new[] { FileFormat.Flac, FileFormat.Mp3, FileFormat.M4a, FileFormat.Ogg, FileFormat.Opus, FileFormat.Wma, FileFormat.Aiff })
        {
            var output = Converter.Convert(wav, target)!;
            Assert.Equal(PathOf($"песня.{target.Extension()}"), output);
            Assert.InRange(MediaConverter.Probe(output).Duration ?? 0, 1.8, 2.3);
        }
    }

    [SkippableFact]
    public void ImageToAvif()
    {
        Skip.IfNot(FFmpeg.IsBundled);
        var png = PathOf("фото.png");
        FFmpeg.Encode(["-f", "lavfi", "-i", "testsrc2=size=301x201", "-frames:v", "1"], png, null, null, CancellationToken.None);
        var avif = Converter.Convert(png, FileFormat.Avif)!;
        Assert.Equal("av1", MediaConverter.Probe(avif).VideoCodec);
    }

    [SkippableFact]
    public void CancellingLeavesNothingBehind()
    {
        Skip.IfNot(FFmpeg.IsBundled);
        var mp4 = Make("long.mp4", seconds: 30);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(700));
        Assert.ThrowsAny<OperationCanceledException>(() => Converter.Convert(mp4, FileFormat.Webm, cancel.Token));
        Assert.Equal([mp4], Directory.GetFiles(_dir));
    }
}
