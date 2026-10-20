using System.Diagnostics;
using System.Text;

namespace Mangosteen.Platform;

/// <summary>
/// A plain text log at %LOCALAPPDATA%\Mangosteen\mangosteen.log. It is how a test on a real PC tells us
/// what the hook saw, so every right click Mangosteen handles is written here.
/// </summary>
internal static class Log
{
    private static readonly object Gate = new();
    private const long MaxBytes = 1024 * 1024;

    public static string Directory { get; } =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mangosteen");

    public static string Path { get; } = System.IO.Path.Combine(Directory, "mangosteen.log");

    public static void Start(string version)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            if (File.Exists(Path) && new FileInfo(Path).Length > MaxBytes)
            {
                File.Move(Path, Path + ".old", overwrite: true);
            }
        }
        catch (IOException)
        {
        }
        Write($"---- Mangosteen {version} started, Windows {Environment.OSVersion.Version}, pid {Environment.ProcessId}");
    }

    public static void Write(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Environment.CurrentManagedThreadId}] {message}";
        Debug.WriteLine(line);
        lock (Gate)
        {
            try
            {
                File.AppendAllText(Path, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    public static void Error(string context, Exception error) => Write($"ERROR {context}: {error}");
}
