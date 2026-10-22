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

    private readonly AppSettings _settings;

    public AppController(Dispatcher dispatcher, AppSettings settings)
    {
        _dispatcher = dispatcher;
        _settings = settings;
    }

    public void Start()
    {
        FormatCatalog.MediaAvailable = Media.FFmpeg.IsBundled;
        WheelCatalog.BackgroundRemovalAvailable = Tools.BackgroundRemoval.IsBundled;
        Log.Write($"ffmpeg bundled: {Media.FFmpeg.IsBundled}, background model bundled: {Tools.BackgroundRemoval.IsBundled}");
        _tray = new TrayIcon(ChooseFiles, _shelf.Toggle, () => new SettingsWindow(_settings).Present(), () => new WelcomeWindow().Present(), Quit);
        _jobs = new Jobs(_dispatcher, _tray);
        // The hook thread only posts; everything below runs on the UI thread.
        _hooks.Armed += click => _dispatcher.BeginInvoke(() => OnArmed(click));
        _hooks.Triggered += (click, up) => _dispatcher.BeginInvoke(() => OnTriggered(click, up));
        _hooks.DismissWheel += reason => _dispatcher.BeginInvoke(() => CloseWheel(reason));
        _hooks.SwitchWheel += kind => _dispatcher.BeginInvoke(() => SwitchWheel(kind));
        _hooks.Start();
        if (_settings.ExplorerMenu)
        {
            ExplorerMenu.Register();
        }
        ExplorerMenu.Listen((kind, path) => _dispatcher.BeginInvoke(() => OpenFromExplorer(kind, path)));
        _dispatcher.BeginInvoke(ShellHitTest.WarmUp, DispatcherPriority.ApplicationIdle);
        if (!_settings.WelcomeShown)
        {
            _settings.WelcomeShown = true;
            _settings.Save();
            new WelcomeWindow().Present();
        }
        else if (_hooks.Installed)
        {
            _tray.Notify(L("Mangosteen is running"), L("Hold Shift, Ctrl or Alt and right-click a file in Explorer or on the desktop."));
        }
        else
        {
            _tray.Notify("Mangosteen", L("Could not install the mouse hook. See the log in the tray menu."), error: true);
        }
    }

    private readonly List<string> _explorerPaths = [];
    private WheelKind _explorerKind;
    private DispatcherTimer? _explorerTimer;

    /// <summary>
    /// A request from Explorer's right-click menu. Explorer starts one process per selected item, so the paths that
    /// arrive within a moment of each other open one wheel together.
    /// </summary>
    public void OpenFromExplorer(WheelKind kind, string path)
    {
        if (_explorerPaths.Count > 0 && kind != _explorerKind)
        {
            _explorerPaths.Clear();
        }
        _explorerKind = kind;
        if (!_explorerPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            _explorerPaths.Add(path);
        }
        _explorerTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(350), DispatcherPriority.Normal, (_, _) =>
        {
            _explorerTimer!.Stop();
            var paths = _explorerPaths.ToList();
            _explorerPaths.Clear();
            GetCursorPos(out var cursor);
            Log.Write($"explorer menu: {_explorerKind} for {paths.Count} items");
            ShowWheel(_explorerKind, paths, cursor);
        }, _dispatcher);
        _explorerTimer.Stop();
        _explorerTimer.Start();
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
        // A click on the centre goes to the next wheel: Convert, Tools, Actions, Convert…
        wheel.HubClicked += () => SwitchWheel((WheelKind)(((int)_wheelKind + 1) % 3));
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
            jobs.RunEach(string.Format(L("Converting to {0}"), target.DisplayName()), string.Format(L("Converted to {0}"), target.DisplayName()), inputs,
                (input, token, progress) => Converter.Convert(input, target, token, progress));
            return;
        }
        try
        {
            switch (item.Id)
            {
                case "tool.extract":
                    var archives = paths.Where(p => FileFormats.Detect(p)?.Category() == FormatCategory.Archive).ToList();
                    jobs.RunEach(L("Extracting"), L("Extracted"), archives, (input, _) => Archives.ExtractNextToArchive(input));
                    break;
                case "tool.createPDF":
                    jobs.RunOnce(L("Creating PDF"), L("PDF created"), WheelCatalog.FileCount(paths.Count), () => Converter.CreatePdf(paths));
                    break;
                case "tool.compressAll":
                    // A mixed selection: every file compressed its own default way, without a window.
                    jobs.RunEach(L("Compressing"), L("Compressed"),
                        paths.Where(p => FileFormats.Detect(p) is { } f && WheelCatalog.CanCompress(f)).ToList(),
                        (input, token, progress) => FileFormats.Detect(input)!.Value.Category().IsMedia()
                            ? Media.MediaTools.Compress(input, new Core.Media.MediaMath.MediaCompression(), progress, token)
                            : Tools.Compression.CompressFile(input, new Core.Imaging.CompressionSettings(), token));
                    break;
                case "tool.archive":
                    jobs.RunOnce(L("Archiving"), L("Archived"), Core.Loc.Count(paths.Count, "item"), () => Archives.AddToArchive(paths));
                    break;
                case "tool.recognizeText":
                    jobs.RunEach(L("Recognising text"), L("Text recognised"), paths.Where(Tools.TextRecognition.Accepts).ToList(),
                        (input, token) => Tools.TextRecognition.RecognizeFile(input, token));
                    break;
                case "tool.mergePDF":
                    jobs.RunOnce(L("Merging PDFs"), L("PDFs merged"), WheelCatalog.FileCount(paths.Count), () => Converter.MergePdfs(paths));
                    break;
                case "action.float":
                    _shelf.Add(paths);
                    break;
                case "action.copy":
                    FileActions.Copy(paths);
                    tray.Notify(L("Copied"), string.Format(L("{0}: paste it in a folder or a message."), paths.Count == 1 ? Path.GetFileName(paths[0]) : Core.Loc.Count(paths.Count, "item")));
                    break;
                case "action.copyPath":
                    FileActions.CopyPaths(paths);
                    tray.Notify(L("Path copied"), paths.Count == 1 ? paths[0] : Core.Loc.Count(paths.Count, "path"));
                    break;
                case "action.move":
                    if (FileActions.Move(paths) is var (moved, failed))
                    {
                        if (failed.Count > 0)
                        {
                            tray.Notify(string.Format(L("{0} of {1} not moved"), failed.Count, moved.Count + failed.Count), string.Join("\n", failed.Take(3)), error: true);
                        }
                        else if (moved.Count > 0)
                        {
                            tray.Notify(L("Moved"), moved.Count == 1 ? Path.GetFileName(moved[0]) : Core.Loc.Count(moved.Count, "item"), reveal: moved[^1]);
                        }
                    }
                    break;
                case "action.terminal":
                    FileActions.OpenTerminal(paths);
                    break;
                default:
                    if (Tools.ToolRegistry.Create(item.Id, paths, jobs) is { } tool)
                    {
                        tool.Present();
                    }
                    else
                    {
                        tray.Notify(item.Title, L("Coming in a later version of Mangosteen for Windows."));
                    }
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
