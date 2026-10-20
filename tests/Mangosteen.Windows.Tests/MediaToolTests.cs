using Mangosteen.Core;
using Mangosteen.Core.Imaging;
using Mangosteen.Core.Media;
using Mangosteen.Media;

namespace Mangosteen.Windows.Tests;

/// <summary>The video and audio tools, end to end with the bundled FFmpeg; skipped without it.</summary>
public sealed class MediaToolTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mangosteen-mediatools").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string PathOf(string name) => Path.Combine(_dir, name);

    private string Clip(string name, double seconds = 4, string source = "testsrc2")
    {
        var path = PathOf(name);
        FFmpeg.Encode(["-f", "lavfi", "-i", $"{source}=size=320x240:rate=25:duration={seconds}", "-f", "lavfi", "-i", $"sine=frequency=440:duration={seconds}",
            "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac"], path, null, null, CancellationToken.None);
        return path;
    }

    private string Song(string name, double seconds = 4)
    {
        var path = PathOf(name);
        FFmpeg.Encode(["-f", "lavfi", "-i", $"sine=frequency=330:duration={seconds}", "-ac", "2", "-c:a", "libmp3lame"], path, null, null, CancellationToken.None);
        return path;
    }

    private static double Duration(string path) => MediaConverter.Probe(path).Duration ?? 0;

    [SkippableFact]
    public void TrimKeepsTheSelection()
    {
        Skip.IfNot(FFmpeg.IsBundled);
        var output = MediaTools.Trim(Clip("клип.mp4"), 1, 3, fast: false, _ => { }, CancellationToken.None);
        Assert.Equal(PathOf("клип (trimmed).mp4"), output);
        Assert.InRange(Duration(output), 1.8, 2.2);
    }

    [SkippableFact]
    public void SplitMakesAFolderOfParts()
    {
        Skip.IfNot(FFmpeg.IsBundled);
        var folder = MediaTools.Split(Clip("clip.mp4"), [1.5, 3], fast: false, _ => { }, CancellationToken.None);
        Assert.Equal(["clip 1.mp4", "clip 2.mp4", "clip 3.mp4"], Directory.GetFiles(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [SkippableFact]
    public void FramesArePng()
    {
        Skip.IfNot(FFmpeg.IsBundled);
        var single = MediaTools.SaveFrames(Clip("clip.mp4"), [1.0], CancellationToken.None);
        Assert.EndsWith(".png", single);
        var folder = MediaTools.SaveFrames(PathOf("clip.mp4"), [0.5, 2.5], CancellationToken.None);
        Assert.Equal(2, Directory.GetFiles(folder, "*.png").Length);
    }

    [SkippableFact]
    public void SpeedHalvesTheLength()
    {
        Skip.IfNot(FFmpeg.IsBundled);
        var output = MediaTools.ChangeSpeed(Clip("clip.mp4"), 2, keepPitch: true, _ => { }, CancellationToken.None);
        Assert.InRange(Duration(output), 1.8, 2.3);
    }

    [SkippableFact]
    public void JoinAddsLengths()
    {
        Skip.IfNot(FFmpeg.IsBundled);
        var output = MediaTools.Join([Clip("a.mp4", 2), Clip("b.mp4", 3, "smptebars")], _ => { }, CancellationToken.None);
        Assert.Equal(PathOf("Joined.mp4"), output);
        Assert.InRange(Duration(output), 4.7, 5.4);
    }

    [SkippableFact]
    public void CropVideoChangesTheSize()
    {
        Skip.IfNot(FFmpeg.IsBundled);
        var output = MediaTools.CropVideo(Clip("clip.mp4"), new UnitRect(0, 0, 0.5, 0.5), null, _ => { }, CancellationToken.None);
        var info = MediaConverter.Probe(output);
        Assert.Equal(160, info.Width);
        Assert.Equal(120, info.Height);
    }

    [SkippableFact]
    public void CompressAudio()
    {
        Skip.IfNot(FFmpeg.IsBundled);
        var wav = PathOf("long.wav");
        FFmpeg.Encode(["-f", "lavfi", "-i", "sine=frequency=330:duration=10", "-c:a", "pcm_s16le"], wav, null, null, CancellationToken.None);
        var output = MediaTools.Compress(wav, new MediaMath.MediaCompression(), _ => { }, CancellationToken.None);
        Assert.EndsWith("(compressed).m4a", output);
        Assert.True(MediaTools.EstimateCompressedSize(wav, new MediaMath.MediaCompression(), CancellationToken.None) > 0);
    }

    [SkippableFact]
    public void VolumeChannelsAndBleep()
    {
        Skip.IfNot(FFmpeg.IsBundled);
        var song = Song("песня.mp3");
        var loud = MediaTools.MeasureLoudness(song, new MediaMath.LoudnessTarget(), CancellationToken.None);
        Assert.InRange(loud.Integrated, -60, 0);
        Assert.EndsWith("(volume).mp3", MediaTools.Normalize(song, new MediaMath.LoudnessTarget(), _ => { }, CancellationToken.None));
        var mono = MediaTools.ProcessAudio(song, "channels", MediaMath.ChannelFilter(MediaMath.ChannelLayout.Mono, 1, 1, false, 2), null, _ => { }, CancellationToken.None);
        Assert.Equal(1, MediaConverter.Probe(mono).AudioChannels);
        var bleeped = MediaTools.ProcessAudio(song, "bleeped", null, MediaMath.BleepGraph([(1, 2)], 1000, 0.3, 4), _ => { }, CancellationToken.None);
        Assert.InRange(Duration(bleeped), 3.8, 4.2);
        var preview = MediaTools.RenderPreview(song, null, MediaMath.BleepGraph([(0.5, 1)], 1000, 0.3, 2), 0.5, 2, CancellationToken.None);
        Assert.True(new FileInfo(preview).Length > 1000);
        File.Delete(preview);
    }

    [SkippableFact]
    public void VideoKeepsItsPictureWhenTheSoundChanges()
    {
        Skip.IfNot(FFmpeg.IsBundled);
        var output = MediaTools.ProcessAudio(Clip("clip.mp4"), "volume", "volume=0.5", null, _ => { }, CancellationToken.None);
        var info = MediaConverter.Probe(output);
        Assert.Equal("h264", info.VideoCodec);
        Assert.True(info.HasAudio);
    }

    [SkippableFact]
    public void VisualizerMakesAVideo()
    {
        Skip.IfNot(FFmpeg.IsBundled);
        var song = Song("song.mp3", 2);
        var output = MediaTools.Visualize(song, MediaMath.VisualizerStyle.Waveform, MediaMath.VisualizerShape.Square, 0xFFFF9E33, 0xFF141419, null, _ => { }, CancellationToken.None);
        var info = MediaConverter.Probe(output);
        Assert.Equal(1080, info.Width);
        Assert.True(info.HasAudio);
        Assert.Equal(1080, MediaTools.VisualizerFrame(song, MediaMath.VisualizerStyle.Spectrum, MediaMath.VisualizerShape.Square, 0xFFFFFFFF, 0, null, 2, 1, CancellationToken.None).PixelWidth);
    }

    [SkippableFact]
    public void RedactVideoAndPreview()
    {
        Skip.IfNot(FFmpeg.IsBundled);
        var clip = Clip("clip.mp4");
        List<MediaMath.VideoRegion> regions = [new(new UnitRect(0.1, 0.1, 0.3, 0.3), 0, 2)];
        foreach (var style in Enum.GetValues<RedactionStyle>())
        {
            Assert.Equal(320, MediaTools.RedactedFrame(clip, regions, style, 0xFF000000, 320, 240, 1, CancellationToken.None).PixelWidth);
        }
        var output = MediaTools.RedactVideo(clip, regions, RedactionStyle.Pixelate, 0, _ => { }, CancellationToken.None);
        Assert.InRange(Duration(output), 3.8, 4.2);
    }

    [SkippableFact]
    public void TimelineInputs()
    {
        Skip.IfNot(FFmpeg.IsBundled);
        var clip = Clip("clip.mp4");
        Assert.Equal(5, MediaTools.Thumbnails(clip, 4, 5, 48, CancellationToken.None).Count);
        Assert.Equal(100, MediaTools.Waveform(clip, 4, 100, CancellationToken.None).Length);
    }
}
