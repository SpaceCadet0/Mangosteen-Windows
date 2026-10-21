using System.Diagnostics;
using Mangosteen.Core;
using Mangosteen.Platform;
using Microsoft.VisualBasic.FileIO;

namespace Mangosteen;

/// <summary>
/// The results of finished jobs, for the tray menu's "Recent Results": show them in Explorer, open them, or move
/// them to the Recycle Bin. Kept in %LOCALAPPDATA%\Mangosteen\history.json.
/// </summary>
internal static class RecentResults
{
    private static readonly Lazy<ResultHistory> History = new(() => ResultHistory.Load(Path.Combine(Log.Directory, "history.json")));

    /// <summary>Called by <see cref="Jobs"/> when a job has made files.</summary>
    public static void Record(string title, IReadOnlyList<string> outputs) => History.Value.Add(title, outputs, DateTime.Now);

    public static List<ResultEntry> Live() => History.Value.Live();

    public static void Clear() => History.Value.Clear();

    /// <summary>"photo.png", or "photo.png and 2 more".</summary>
    public static string Label(ResultEntry entry)
    {
        var first = Path.GetFileName(Path.TrimEndingDirectorySeparator(entry.Outputs[0]));
        return entry.Outputs.Count == 1 ? first : $"{first} {string.Format(L("and {0} more"), entry.Outputs.Count - 1)}";
    }

    public static void Reveal(ResultEntry entry) => FileActions.Reveal(entry.Outputs[0]);

    public static void Open(ResultEntry entry)
    {
        foreach (var path in entry.Outputs)
        {
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                Log.Error("open result", error);
            }
        }
    }

    /// <summary>Moves the entry's files and folders to the Recycle Bin, never deleting them for good.</summary>
    public static void Delete(ResultEntry entry)
    {
        foreach (var path in entry.Outputs)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                }
                else if (File.Exists(path))
                {
                    FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                }
                Log.Write($"result moved to the Recycle Bin: {path}");
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or UnauthorizedAccessException)
            {
                Log.Error("delete result", error);
            }
        }
        History.Value.Remove(entry);
    }
}
