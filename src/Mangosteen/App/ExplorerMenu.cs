using System.IO.Pipes;
using System.Text;
using Mangosteen.Core;
using Mangosteen.Platform;
using Microsoft.Win32;

namespace Mangosteen;

/// <summary>
/// "Mangosteen ▸ Convert… / Tools… / Actions…" in File Explorer's right-click menu, for files and folders: a second
/// way to open a wheel, without the keys. Registered for the current user only (HKCU\Software\Classes), so no
/// administrator rights are needed. On Windows 11 the items are under "Show more options" (Shift+F10): the new
/// top-level menu only takes packaged, signed apps.
/// </summary>
/// <remarks>
/// Explorer starts one process per selected item ("Mangosteen.exe --wheel tools C:\a.jpg"). Each one hands its
/// path to the running Mangosteen over a named pipe and exits; Mangosteen gathers the paths that arrive together and
/// opens one wheel for all of them at the pointer.
/// </remarks>
internal static class ExplorerMenu
{
    private const string Key = "Mangosteen";
    private static readonly string[] Classes = [@"Software\Classes\*\shell", @"Software\Classes\Directory\shell"];
    private static readonly string PipeName = $"Mangosteen.Explorer.{Environment.UserName}";

    public static void Register()
    {
        if (Environment.ProcessPath is not { } exe)
        {
            return;
        }
        try
        {
            foreach (var root in Classes)
            {
                using var menu = Registry.CurrentUser.CreateSubKey($@"{root}\{Key}");
                menu.SetValue("MUIVerb", "Mangosteen");
                menu.SetValue("Icon", $"\"{exe}\",0");
                menu.SetValue("SubCommands", "");
                // One command for any number of selected items; each item still gets its own process.
                menu.SetValue("MultiSelectModel", "Player");
                var order = 1;
                foreach (var (kind, title) in new[] { ("convert", L("Convert…")), ("tools", L("Tools…")), ("actions", L("Actions…")) })
                {
                    using var verb = menu.CreateSubKey($@"shell\{order++}{kind}");
                    verb.SetValue("MUIVerb", title);
                    using var command = verb.CreateSubKey("command");
                    command.SetValue("", $"\"{exe}\" --wheel {kind} \"%1\"");
                }
            }
            Log.Write("explorer menu registered");
        }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Log.Error("explorer menu", error);
        }
    }

    public static void Unregister()
    {
        try
        {
            foreach (var root in Classes)
            {
                Registry.CurrentUser.DeleteSubKeyTree($@"{root}\{Key}", throwOnMissingSubKey: false);
            }
        }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Log.Error("explorer menu", error);
        }
    }

    /// <summary>The wheel and path from "--wheel kind path", or null.</summary>
    public static (WheelKind Kind, string Path)? Parse(IReadOnlyList<string> args)
    {
        if (args.Count < 3 || args[0] != "--wheel")
        {
            return null;
        }
        WheelKind? kind = args[1] switch { "convert" => WheelKind.Convert, "tools" => WheelKind.Tools, "actions" => WheelKind.Actions, _ => null };
        return kind is { } k ? (k, args[2]) : null;
    }

    /// <summary>Hands the request to the running Mangosteen. False when none is listening.</summary>
    public static bool Send(WheelKind kind, string path)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            pipe.Connect(3000);
            var line = Encoding.UTF8.GetBytes($"{(int)kind}\t{path}\n");
            pipe.Write(line);
            return true;
        }
        catch (Exception error) when (error is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Listens for requests from Explorer's menu; <paramref name="received"/> runs on a background thread.</summary>
    public static void Listen(Action<WheelKind, string> received)
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.In, NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly);
                    pipe.WaitForConnection();
                    using var reader = new StreamReader(pipe, Encoding.UTF8);
                    while (reader.ReadLine() is { } line)
                    {
                        var parts = line.Split('\t', 2);
                        if (parts.Length == 2 && int.TryParse(parts[0], out var kind) && kind is >= 0 and <= 2)
                        {
                            received((WheelKind)kind, parts[1]);
                        }
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    Log.Error("explorer pipe", error);
                    Thread.Sleep(500);
                }
            }
        }) { IsBackground = true, Name = "Explorer menu" };
        thread.Start();
    }
}
