using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using Mangosteen.Core;
using Mangosteen.Platform;

namespace Mangosteen;

/// <summary>Actions that work on any file or folder (the Actions wheel).</summary>
internal static class FileActions
{
    /// <summary>Puts the files themselves on the clipboard, ready to paste in Explorer or a mail.</summary>
    public static void Copy(IReadOnlyList<string> paths)
    {
        var list = new StringCollection();
        list.AddRange(paths.ToArray());
        Clipboard.SetFileDropList(list);
    }

    /// <summary>One full path per line.</summary>
    public static void CopyPaths(IReadOnlyList<string> paths) => Clipboard.SetText(string.Join(Environment.NewLine, paths));

    /// <summary>Asks for a folder and moves the items there; a name that is taken gets a number.</summary>
    public static (List<string> Moved, List<string> Failed)? Move(IReadOnlyList<string> paths)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = paths.Count == 1 ? $"Move “{Path.GetFileName(paths[0])}” to" : $"Move {paths.Count} items to",
            InitialDirectory = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(paths[0])),
        };
        if (dialog.ShowDialog() != true)
        {
            return null;
        }
        var destination = dialog.FolderName;
        var moved = new List<string>();
        var failed = new List<string>();
        foreach (var path in paths)
        {
            var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (string.Equals(Path.GetDirectoryName(source), Path.TrimEndingDirectorySeparator(destination), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            try
            {
                var isFolder = Directory.Exists(source);
                if (isFolder && (destination + Path.DirectorySeparatorChar).StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("A folder cannot be moved into itself.");
                }
                var name = isFolder ? Path.GetFileName(source) : Path.GetFileNameWithoutExtension(source);
                var extension = isFolder ? "" : Path.GetExtension(source).TrimStart('.');
                var target = OutputNaming.UniquePath(destination, name, extension);
                if (isFolder)
                {
                    MoveFolder(source, target);
                }
                else
                {
                    File.Move(source, target);
                }
                moved.Add(target);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                failed.Add($"{Path.GetFileName(source)}: {error.Message}");
                Log.Error($"move {source}", error);
            }
        }
        return (moved, failed);
    }

    /// <summary>Directory.Move cannot cross drives; then the folder is copied and the original removed.</summary>
    private static void MoveFolder(string source, string target)
    {
        if (string.Equals(Path.GetPathRoot(source), Path.GetPathRoot(target), StringComparison.OrdinalIgnoreCase))
        {
            Directory.Move(source, target);
            return;
        }
        CopyFolder(source, target);
        Directory.Delete(source, recursive: true);
    }

    private static void CopyFolder(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }
        foreach (var folder in Directory.GetDirectories(source))
        {
            CopyFolder(folder, Path.Combine(target, Path.GetFileName(folder)));
        }
    }

    /// <summary>Opens each folder (or a file's folder) in Windows Terminal, or in PowerShell when it is not installed.</summary>
    public static void OpenTerminal(IReadOnlyList<string> paths)
    {
        var folders = paths
            .Select(p => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p)))
            .Select(p => Directory.Exists(p) ? p : Path.GetDirectoryName(p)!)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders)
        {
            try
            {
                var terminal = new ProcessStartInfo("wt.exe") { UseShellExecute = false };
                terminal.ArgumentList.Add("-d");
                terminal.ArgumentList.Add(folder);
                Process.Start(terminal);
            }
            catch (Win32Exception)
            {
                Process.Start(new ProcessStartInfo("powershell.exe") { UseShellExecute = true, WorkingDirectory = folder });
            }
        }
    }

    /// <summary>Opens Explorer with the item selected.</summary>
    public static void Reveal(string path)
    {
        // Explorer wants exactly /select,"path"; ArgumentList would quote the whole argument.
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
    }
}
