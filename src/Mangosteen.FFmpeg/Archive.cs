namespace Mangosteen.FFmpegArchive;

/// <summary>The FFmpeg archive embedded in this assembly, or null in builds without it.</summary>
public static class Archive
{
    public static Stream? Open() => typeof(Archive).Assembly.GetManifestResourceStream("ffmpeg.zip");
}
