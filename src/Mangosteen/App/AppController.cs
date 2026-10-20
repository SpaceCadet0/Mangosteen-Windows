using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Mangosteen.Conversion;
using Mangosteen.Core;
using Mangosteen.Platform;
using Mangosteen.Wheel;
using static Mangosteen.Platform.Native;

namespace Mangosteen;

/// <summary>Connects the hooks, the shell selection, the wheel and the actions. Lives on the UI thread.</summary>
internal sealed class AppController : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly InputHooks _hooks = new();
    private TrayIcon? _tray;
    private WheelWindow? _wheel;

    public AppController(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public void Start()
    {
        _tray = new TrayIcon(ChooseFiles, Quit);
        // The hook thread only posts; everything below runs on the UI thread.
        _hooks.Armed += click => _dispatcher.BeginInvoke(() => OnArmed(click));
        _hooks.Triggered += (click, up) => _dispatcher.BeginInvoke(() => OnTriggered(click, up));
        _hooks.DismissWheel += reason => _dispatcher.BeginInvoke(() => CloseWheel(reason));
        _hooks.Start();
        if (_hooks.Installed)
        {
            _tray.Notify("Mangosteen is running", "Hold Shift, Ctrl or Alt and right-click a file in Explorer or on the desktop.");
        }
        else
        {
            _tray.Notify("Mangosteen", "Could not install the mouse hook. See the log in the tray menu.", error: true);
        }
    }

    public void Dispose()
    {
        _hooks.Dispose();
        _tray?.Dispose();
    }

    private void Quit()
    {
        Log.Write("quit");
        CloseWheel("quit");
        Application.Current.Shutdown();
    }

    private void OnArmed(ShellClick click)
    {
        Log.Write($"armed {click.Kind} at {click.DownPoint.X},{click.DownPoint.Y}: {click.ClassChain}");
        if (click.Kind == WheelKind.Actions)
        {
            // A lone Alt press-and-release activates the window's menu bar; a key in between cancels that.
            SendKey(VK_MASK);
        }
    }

    private void OnTriggered(ShellClick click, POINT up)
    {
        var clock = Stopwatch.StartNew();
        var latency = Environment.TickCount64 - click.DownTimestamp;

        // Explorer saw the button-down but not the button-up; take it out of any capture or drag-detect mode
        // before talking to it over COM.
        var threadId = GetWindowThreadProcessId(click.Root, out _);
        var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        var capture = GetGUIThreadInfo(threadId, ref info) ? info.hwndCapture : IntPtr.Zero;
        if (capture != IntPtr.Zero)
        {
            PostMessage(capture, WM_CANCELMODE, IntPtr.Zero, IntPtr.Zero);
        }

        ShellSelection.Result selection;
        try
        {
            selection = ShellSelection.Read(click.Target, click.RootClass);
        }
        catch (Exception error)
        {
            Log.Error("read selection", error);
            selection = new ShellSelection.Result([], "error");
        }

        Log.Write($"triggered {click.Kind}: down-to-up {latency} ms, capture={(capture == IntPtr.Zero ? "none" : ClassName(capture))}, " +
            $"selection {selection.Paths.Count} via {selection.Source} in {clock.ElapsedMilliseconds} ms, " +
            $"rbutton down={IsDown(VK_RBUTTON)}");
        foreach (var path in selection.Paths.Take(20))
        {
            Log.Write($"  {path}");
        }

        if (selection.Paths.Count == 0)
        {
            // Not on a file (empty space, or a non-file item): give the right click back so the usual menu opens.
            Log.Write("nothing selected: replaying the right click");
            SendRightClick();
            return;
        }

        ShowWheel(click.Kind, selection.Paths, up);

        // A swallowed button-up may leave Windows thinking the right button is still down. Release it now that
        // the wheel is under the cursor, so the extra button-up lands on the wheel, which ignores it.
        info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        var stillCaptured = GetGUIThreadInfo(threadId, ref info) && info.hwndCapture != IntPtr.Zero;
        if (IsDown(VK_RBUTTON) && !stillCaptured)
        {
            Log.Write("right button still reads as down: releasing it on the wheel");
            SendRightButton(MOUSEEVENTF_RIGHTUP);
        }
    }

    private void ChooseFiles(WheelKind kind)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = $"Mangosteen: {WheelCatalog.Title(kind)}" };
        if (dialog.ShowDialog() != true || dialog.FileNames.Length == 0)
        {
            return;
        }
        GetCursorPos(out var cursor);
        Log.Write($"chosen {dialog.FileNames.Length} files for {kind}");
        ShowWheel(kind, dialog.FileNames, cursor);
    }

    private void ShowWheel(WheelKind kind, IReadOnlyList<string> paths, POINT at)
    {
        CloseWheel("new wheel");
        var items = WheelCatalog.Items(kind, paths);
        Log.Write($"wheel {kind}: {string.Join(", ", items.Select(i => i.Title))}");
        var wheel = new WheelWindow(kind, items, paths.Count, at);
        wheel.Placed += circle => _hooks.Wheel = circle;
        wheel.Chosen += item => Perform(item, paths);
        wheel.Closed += (_, _) =>
        {
            if (ReferenceEquals(_wheel, wheel))
            {
                _wheel = null;
                _hooks.Wheel = null;
            }
        };
        _wheel = wheel;
        wheel.Show();
    }

    private void CloseWheel(string reason)
    {
        if (_wheel is { } wheel)
        {
            Log.Write($"wheel closed: {reason}");
            _wheel = null;
            _hooks.Wheel = null;
            wheel.CloseWheel();
        }
    }

    private void Perform(WheelItem item, IReadOnlyList<string> paths)
    {
        Log.Write($"chose {item.Id} for {paths.Count} items");
        if (item.Target is { } target)
        {
            Convert(paths, target);
            return;
        }
        switch (item.Id)
        {
            case "action.copyPath":
                try
                {
                    Clipboard.SetText(string.Join(Environment.NewLine, paths));
                    _tray?.Notify("Path copied", paths.Count == 1 ? paths[0] : $"{paths.Count} paths");
                }
                catch (Exception error)
                {
                    Log.Error("copy path", error);
                    _tray?.Notify("Could not copy the path", error.Message, error: true);
                }
                break;
            default:
                _tray?.Notify(item.Title, "Coming in a later version of Mangosteen for Windows.");
                break;
        }
    }

    private void Convert(IReadOnlyList<string> paths, FileFormat target)
    {
        var inputs = paths
            .Where(p => FileFormats.Detect(p) is { } f && f != target && FormatCatalog.Targets(f).Contains(target))
            .ToList();
        Task.Run(() =>
        {
            var done = new List<string>();
            var failed = new List<string>();
            foreach (var input in inputs)
            {
                var clock = Stopwatch.StartNew();
                try
                {
                    var output = ImageConverter.Convert(input, target);
                    done.Add(output);
                    Log.Write($"converted {input} -> {output} in {clock.ElapsedMilliseconds} ms");
                }
                catch (Exception error)
                {
                    failed.Add($"{Path.GetFileName(input)}: {error.Message}");
                    Log.Error($"convert {input} to {target}", error);
                }
            }
            _dispatcher.BeginInvoke(() => ReportConversion(target, done, failed));
        });
    }

    private void ReportConversion(FileFormat target, List<string> done, List<string> failed)
    {
        if (failed.Count == 0)
        {
            _tray?.Notify($"Converted to {target.DisplayName()}",
                done.Count == 1 ? Path.GetFileName(done[0]) : $"{done.Count} files");
        }
        else
        {
            _tray?.Notify($"{failed.Count} of {failed.Count + done.Count} not converted", string.Join("\n", failed.Take(3)), error: true);
        }
    }

    private static void SendKey(ushort vk)
    {
        var inputs = new INPUT[2];
        for (var i = 0; i < 2; i++)
        {
            inputs[i].type = INPUT_KEYBOARD;
            inputs[i].u.ki = new KEYBDINPUT { wVk = vk, dwFlags = i == 0 ? 0 : KEYEVENTF_KEYUP, dwExtraInfo = InputHooks.InjectedTag };
        }
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static void SendRightButton(uint flags)
    {
        var inputs = new[] { new INPUT { type = INPUT_MOUSE, u = { mi = new MOUSEINPUT { dwFlags = flags, dwExtraInfo = InputHooks.InjectedTag } } } };
        SendInput(1, inputs, Marshal.SizeOf<INPUT>());
    }

    /// <summary>A full right click where the cursor is, tagged so the hook lets it through.</summary>
    private static void SendRightClick()
    {
        SendRightButton(MOUSEEVENTF_RIGHTDOWN);
        SendRightButton(MOUSEEVENTF_RIGHTUP);
    }
}
