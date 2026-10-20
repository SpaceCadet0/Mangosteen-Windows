using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Mangosteen.Platform;

namespace Mangosteen.Media;

/// <summary>
/// The FFmpeg bundled inside Mangosteen.exe (GPLv3, a separate program). It is unpacked once into
/// %LOCALAPPDATA%\Mangosteen\ffmpeg\&lt;hash&gt; and always run as its own process, below normal priority, inside a
/// job object so it never outlives Mangosteen.
/// </summary>
internal static class FFmpeg
{
    private const string Resource = "ffmpeg.zip";
    private static readonly object Gate = new();
    private static string? _folder;

    /// <summary>True when this build carries FFmpeg (CI builds do; local builds without the fetch step do not).</summary>
    public static bool IsBundled { get; } = typeof(FFmpeg).Assembly.GetManifestResourceInfo(Resource) is not null;

    public static string FFmpegPath => Path.Combine(Folder(), "ffmpeg.exe");
    public static string FFprobePath => Path.Combine(Folder(), "ffprobe.exe");

    /// <summary>Unpacks the bundle on first use: into a temporary folder that is then renamed, so a crash never leaves half a copy.</summary>
    public static string Folder()
    {
        lock (Gate)
        {
            if (_folder is not null)
            {
                return _folder;
            }
            if (!IsBundled)
            {
                throw new InvalidOperationException(L("This build of Mangosteen has no FFmpeg, so video and audio are not available."));
            }
            using var stream = typeof(FFmpeg).Assembly.GetManifestResourceStream(Resource)!;
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            var hash = Convert.ToHexString(SHA256.HashData(bytes))[..16].ToLowerInvariant();
            var root = Path.Combine(Log.Directory, "ffmpeg");
            var folder = Path.Combine(root, hash);
            using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            if (!IsComplete(folder, archive))
            {
                Directory.CreateDirectory(root);
                var temp = Path.Combine(root, $".unpacking-{Guid.NewGuid():N}");
                archive.ExtractToDirectory(temp);
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, recursive: true);
                }
                try
                {
                    Directory.Move(temp, folder);
                }
                catch (IOException) when (IsComplete(folder, archive))
                {
                    // Another Mangosteen finished first.
                    Directory.Delete(temp, recursive: true);
                }
                Log.Write($"ffmpeg unpacked to {folder}");
                foreach (var old in Directory.GetDirectories(root).Where(d => !string.Equals(d, folder, StringComparison.OrdinalIgnoreCase)))
                {
                    try
                    {
                        Directory.Delete(old, recursive: true);
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    {
                    }
                }
            }
            _folder = folder;
            return folder;
        }
    }

    private static bool IsComplete(string folder, ZipArchive archive) =>
        Directory.Exists(folder) && archive.Entries.All(e => e.FullName.EndsWith('/')
            || new FileInfo(Path.Combine(folder, e.FullName)) is { Exists: true } file && file.Length == e.Length);

    /// <summary>Runs a program from the bundle and returns its standard output; throws with the last error lines on failure.</summary>
    public static string Run(string program, IEnumerable<string> arguments, CancellationToken token = default, Action<string>? onOutputLine = null)
    {
        var start = new ProcessStartInfo(Path.Combine(Folder(), program))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using var process = new Process { StartInfo = start };
        var output = new StringBuilder();
        var errors = new Queue<string>();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }
            lock (output)
            {
                output.AppendLine(e.Data);
            }
            onOutputLine?.Invoke(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }
            lock (errors)
            {
                errors.Enqueue(e.Data);
                if (errors.Count > 20)
                {
                    errors.Dequeue();
                }
            }
        };
        process.Start();
        Jobs.Add(process);
        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception)
        {
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using (token.Register(() =>
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception error) when (error is InvalidOperationException or Win32Exception)
            {
            }
        }))
        {
            process.WaitForExit();
        }
        token.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
        {
            string message;
            lock (errors)
            {
                message = string.Join("\n", errors.Where(l => l.Trim().Length > 0).TakeLast(4));
            }
            throw new InvalidOperationException(message.Length > 0 ? message : $"{program} exited with code {process.ExitCode}.");
        }
        lock (output)
        {
            return output.ToString();
        }
    }

    /// <summary>Runs ffmpeg and returns its binary standard output (a PNG frame, raw samples).</summary>
    public static byte[] RunBytes(IEnumerable<string> arguments, CancellationToken token = default)
    {
        var start = new ProcessStartInfo(FFmpegPath)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in new[] { "-hide_banner", "-nostdin", "-loglevel", "error" }.Concat(arguments))
        {
            start.ArgumentList.Add(argument);
        }
        using var process = new Process { StartInfo = start };
        var errors = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (errors)
                {
                    errors.AppendLine(e.Data);
                }
            }
        };
        process.Start();
        Jobs.Add(process);
        process.BeginErrorReadLine();
        using var output = new MemoryStream();
        using (token.Register(() =>
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception error) when (error is InvalidOperationException or Win32Exception)
            {
            }
        }))
        {
            process.StandardOutput.BaseStream.CopyTo(output);
            process.WaitForExit();
        }
        token.ThrowIfCancellationRequested();
        if (process.ExitCode != 0 || output.Length == 0)
        {
            lock (errors)
            {
                throw new InvalidOperationException(errors.Length > 0 ? errors.ToString().Trim() : "ffmpeg produced nothing.");
            }
        }
        return output.ToArray();
    }

    /// <summary>
    /// Runs ffmpeg with the given inputs, filters and codecs, writing <paramref name="output"/> and reporting progress
    /// (0…1) against <paramref name="duration"/> seconds.
    /// </summary>
    public static void Encode(IEnumerable<string> arguments, string output, double? duration, Action<double>? progress, CancellationToken token)
    {
        var all = new List<string> { "-hide_banner", "-nostdin", "-y", "-loglevel", "error" };
        all.AddRange(arguments);
        all.AddRange(["-progress", "pipe:1", "-nostats", output]);
        Run("ffmpeg.exe", all, token, line =>
        {
            if (progress is null || duration is not > 0 || !line.StartsWith("out_time_us=", StringComparison.Ordinal)
                || !double.TryParse(line.AsSpan("out_time_us=".Length), NumberStyles.Float, CultureInfo.InvariantCulture, out var microseconds))
            {
                return;
            }
            progress(Math.Clamp(microseconds / 1_000_000 / duration.Value, 0, 1));
        });
        progress?.Invoke(1);
    }

    /// <summary>
    /// <c>Mangosteen.exe --media-self-test</c>: unpacks FFmpeg, checks that it runs and has the encoders Mangosteen uses.
    /// Exit code 0 when everything is there.
    /// </summary>
    public static int SelfTest()
    {
        try
        {
            var version = Run("ffmpeg.exe", ["-hide_banner", "-version"]).Split('\n')[0];
            var encoders = Run("ffmpeg.exe", ["-hide_banner", "-encoders"]);
            string[] required = ["libx264", "libvpx-vp9", "libopus", "libmp3lame", "aac", "libwebp", "mpeg4", "wmv2", "wmav2", "flac", "gif"];
            var missing = required.Where(r => !encoders.Contains($" {r} ", StringComparison.Ordinal)).ToList();
            if (!encoders.Contains(" libaom-av1 ", StringComparison.Ordinal) && !encoders.Contains(" libsvtav1 ", StringComparison.Ordinal))
            {
                missing.Add("AV1");
            }
            Run("ffprobe.exe", ["-hide_banner", "-version"]);
            Log.Write($"media self-test: {version}; missing encoders: {(missing.Count == 0 ? "none" : string.Join(", ", missing))}");
            return missing.Count == 0 ? 0 : 2;
        }
        catch (Exception error)
        {
            Log.Error("media self-test", error);
            return 1;
        }
    }

    /// <summary>A job object that kills every FFmpeg process when Mangosteen exits or crashes.</summary>
    private static class Jobs
    {
        private static readonly IntPtr Handle = Create();

        public static void Add(Process process)
        {
            if (Handle != IntPtr.Zero)
            {
                AssignProcessToJobObject(Handle, process.Handle);
            }
        }

        private static IntPtr Create()
        {
            var handle = CreateJobObject(IntPtr.Zero, null);
            if (handle == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            var pointer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, pointer, false);
                SetInformationJobObject(handle, 9, pointer, (uint)size); // JobObjectExtendedLimitInformation
            }
            finally
            {
                Marshal.FreeHGlobal(pointer);
            }
            return handle;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

        [DllImport("kernel32.dll")]
        private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

        [DllImport("kernel32.dll")]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    }
}
