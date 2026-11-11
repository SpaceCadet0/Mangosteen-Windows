using System.Diagnostics;
using Mangosteen.Core;
using Mangosteen.Core.Media;
using Mangosteen.Platform;

namespace Mangosteen;

/// <summary>What this PC can do beyond what Mangosteen carries: tar.exe's archive formats, the graphics card's encoders.</summary>
internal static class Capabilities
{
    /// <summary>Once at start, in the background: which archive formats Windows' tar.exe writes (Windows 11 writes 7z and ISO).</summary>
    public static void Check()
    {
        try
        {
            FormatCatalog.SevenZipWritable = Archives.TarWrites("7zip");
            FormatCatalog.IsoWritable = Archives.TarWrites("iso9660");
            Log.Write($"tar.exe writes 7z: {FormatCatalog.SevenZipWritable}, ISO: {FormatCatalog.IsoWritable}");
        }
        catch (Exception error)
        {
            Log.Error("capabilities", error);
        }
    }

    private static readonly Lock Gate = new();
    private static bool _probed;

    /// <summary>
    /// The first time a video is encoded with hardware encoding on: a one-frame test encode with each graphics-card
    /// encoder FFmpeg has (NVIDIA, Intel, AMD) tells which ones this PC really has. Encoders that fail are left out,
    /// and the software encoder is used instead.
    /// </summary>
    public static void EnsureHardwareProbed()
    {
        if (!VideoEncoding.Current.Hardware || !Media.FFmpeg.IsBundled)
        {
            return;
        }
        lock (Gate)
        {
            if (_probed)
            {
                return;
            }
            _probed = true;
            var working = new HashSet<string>();
            foreach (var encoder in VideoEncoding.HardwareCandidates)
            {
                if (TestEncode(encoder))
                {
                    working.Add(encoder);
                }
            }
            VideoEncoding.WorkingHardwareEncoders = working;
            Log.Write($"hardware video encoders: {(working.Count == 0 ? "none" : string.Join(", ", working))}");
        }
    }

    private static bool TestEncode(string encoder)
    {
        try
        {
            var start = new ProcessStartInfo(Media.FFmpeg.FFmpegPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            foreach (var argument in new[] { "-hide_banner", "-nostdin", "-v", "error", "-f", "lavfi", "-i", "color=black:s=256x256:d=0.2",
                         "-frames:v", "2", "-pix_fmt", encoder.EndsWith("_nvenc", StringComparison.Ordinal) ? "yuv420p" : "nv12",
                         "-c:v", encoder, "-f", "null", "-" })
            {
                start.ArgumentList.Add(argument);
            }
            using var process = Process.Start(start)!;
            var errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(15000))
            {
                process.Kill();
                return false;
            }
            return process.ExitCode == 0;
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
