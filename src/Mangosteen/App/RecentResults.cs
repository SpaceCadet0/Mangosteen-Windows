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

    /// <summary>Windows' "Open with" dialog for the result: any installed app, once or as the default.</summary>
    public static void OpenWith(ResultEntry entry)
    {
        var info = new OpenAsInfo { File = entry.Outputs[0], Flags = OAIF_ALLOW_REGISTRATION | OAIF_EXEC };
        var result = SHOpenWithDialog(IntPtr.Zero, ref info);
        if (result != 0 && result != unchecked((int)0x800704C7)) // ERROR_CANCELLED when the dialog is closed
        {
            Log.Write($"open with: 0x{result:X8}");
        }
    }

    private const int OAIF_ALLOW_REGISTRATION = 0x1, OAIF_EXEC = 0x4;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct OpenAsInfo
    {
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] public string File;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] public string? Class;
        public int Flags;
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SHOpenWithDialog(IntPtr parent, ref OpenAsInfo info);

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
