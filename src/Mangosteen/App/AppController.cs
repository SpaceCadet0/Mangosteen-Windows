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
    private readonly FloatShelf _shelf = new();
    private TrayIcon? _tray;
    private Jobs? _jobs;
    private WheelWindow? _wheel;
    private WheelKind _wheelKind;
    private IReadOnlyList<string> _wheelPaths = [];

    public AppController(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public void Start()
    {
        _tray = new TrayIcon(ChooseFiles, _shelf.Toggle, Quit);
        _jobs = new Jobs(_dispatcher, _tray);
        // The hook thread only posts; everything below runs on the UI thread.
        _hooks.Armed += click => _dispatcher.BeginInvoke(() => OnArmed(click));
        _hooks.Triggered += (click, up) => _dispatcher.BeginInvoke(() => OnTriggered(click, up));
        _hooks.DismissWheel += reason => _dispatcher.BeginInvoke(() => CloseWheel(reason));
        _hooks.SwitchWheel += kind => _dispatcher.BeginInvoke(() => SwitchWheel(kind));
        _hooks.Start();
        _dispatcher.BeginInvoke(ShellHitTest.WarmUp, DispatcherPriority.ApplicationIdle);
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

        // Explorer never saw this click, so select the item under the cursor the way it would have.
        var hit = ShellHitTest.SelectItemAt(click.DownPoint);
        var hitTime = clock.ElapsedMilliseconds;
        Log.Write($"triggered {click.Kind}: down-to-up {latency} ms, hit {hit.Outcome} {hit.Detail} in {hitTime} ms");
        if (hit.Outcome is ShellHitTest.Outcome.NoItem or ShellHitTest.Outcome.Failed)
        {
            // Empty space, a header, or something UI Automation cannot select: give the click back so the usual
            // context menu opens.
            Log.Write($"replaying the right click because hit={hit.Outcome}");
            SendRightClick();
            return;
        }

        // A selection made through UI Automation may reach the shell view a moment later; after Select() exactly
        // one item is expected.
        ShellSelection.Result selection;
        var attempts = 0;
        do
        {
            if (attempts > 0)
            {
                Thread.Sleep(30);
            }
            attempts++;
            try
            {
                selection = ShellSelection.Read(click.Target, click.RootClass);
            }
            catch (Exception error)
            {
                Log.Error("read selection", error);
                selection = new ShellSelection.Result([], "error");
            }
        }
        while (hit.Outcome == ShellHitTest.Outcome.Selected && selection.Paths.Count != 1 && attempts < 4);

        Log.Write($"selection {selection.Paths.Count} via {selection.Source} in {clock.ElapsedMilliseconds - hitTime} ms " +
            $"({attempts} {(attempts == 1 ? "read" : "reads")}), rbutton down={IsDown(VK_RBUTTON)}");
        foreach (var path in selection.Paths.Take(20))
        {
            Log.Write($"  {path}");
        }

        if (selection.Paths.Count == 0)
        {
            // Not on a file (empty space, or a non-file item): give the right click back so the usual menu opens.
            Log.Write("replaying the right click because the selection has no files");
            SendRightClick();
            return;
        }

        ShowWheel(click.Kind, selection.Paths, up);
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

    private WheelWindow ShowWheel(WheelKind kind, IReadOnlyList<string> paths, POINT at)
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
        _wheelKind = kind;
        _wheelPaths = paths;
        wheel.Show();
        return wheel;
    }

    /// <summary>Shift, Ctrl or Alt pressed while a wheel is open: show that wheel for the same files.</summary>
    private void SwitchWheel(WheelKind kind)
    {
        if (_wheel is not { } wheel || kind == _wheelKind)
        {
            return;
        }
        var items = WheelCatalog.Items(kind, _wheelPaths);
        Log.Write($"wheel switched {_wheelKind} -> {kind}: {string.Join(", ", items.Select(i => i.Title))}");
        _wheelKind = kind;
        wheel.ShowItems(kind, items, _wheelPaths.Count);
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
        if (_tray is not { } tray || _jobs is not { } jobs)
        {
            return;
        }
        if (item.Target is { } target)
        {
            // Items that cannot become the target (or already are it) are skipped, as the wheel promised.
            var inputs = paths.Where(p => FormatCatalog.Conversion(p) is var c && c.Source != target && c.Targets.Contains(target)).ToList();
            jobs.RunEach($"Converting to {target.DisplayName()}", $"Converted to {target.DisplayName()}", inputs,
                (input, token) => Converter.Convert(input, target, token));
            return;
        }
        try
        {
            switch (item.Id)
            {
                case "tool.extract":
                    var archives = paths.Where(p => FileFormats.Detect(p)?.Category() == FormatCategory.Archive).ToList();
                    jobs.RunEach("Extracting", "Extracted", archives, (input, _) => Archives.ExtractNextToArchive(input));
                    break;
                case "tool.createPDF":
                    jobs.RunOnce("Creating PDF", "PDF created", () => Converter.CreatePdf(paths));
                    break;
                case "tool.mergePDF":
                    jobs.RunOnce("Merging PDFs", "PDFs merged", () => Converter.MergePdfs(paths));
                    break;
                case "action.float":
                    _shelf.Add(paths);
                    break;
                case "action.copy":
                    FileActions.Copy(paths);
                    tray.Notify("Copied", paths.Count == 1 ? $"{Path.GetFileName(paths[0])}: paste it in a folder or a message." : $"{paths.Count} items: paste them in a folder or a message.");
                    break;
                case "action.copyPath":
                    FileActions.CopyPaths(paths);
                    tray.Notify("Path copied", paths.Count == 1 ? paths[0] : $"{paths.Count} paths");
                    break;
                case "action.move":
                    if (FileActions.Move(paths) is var (moved, failed))
                    {
                        if (failed.Count > 0)
                        {
                            tray.Notify($"{failed.Count} of {moved.Count + failed.Count} not moved", string.Join("\n", failed.Take(3)), error: true);
                        }
                        else if (moved.Count > 0)
                        {
                            tray.Notify("Moved", moved.Count == 1 ? Path.GetFileName(moved[0]) : $"{moved.Count} items", reveal: moved[^1]);
                        }
                    }
                    break;
                case "action.terminal":
                    FileActions.OpenTerminal(paths);
                    break;
                default:
                    tray.Notify(item.Title, "Coming in a later version of Mangosteen for Windows.");
                    break;
            }
        }
        catch (Exception error)
        {
            Log.Error(item.Id, error);
            tray.Notify(item.Title, error.Message, error: true);
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
