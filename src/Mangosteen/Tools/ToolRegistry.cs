using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Mangosteen.Platform;

namespace Mangosteen.Tools;

/// <summary>Creates the tool window for a wheel segment, and renders tool windows to images for layout checks.</summary>
internal static class ToolRegistry
{
    public static ToolWindow? Create(string id, IReadOnlyList<string> files, Jobs jobs) => id switch
    {
        "welcome" => new WelcomeWindow(),
        "settings" => new SettingsWindow(new AppSettings()),
        "tool.crop" => new CropTool(files, jobs),
        "tool.compress" => new CompressTool(files, jobs),
        "tool.metadata" => new MetadataTool(files, jobs),
        "tool.frame" => new BackgroundTool(files, jobs),
        "tool.collage" => new CollageTool(files, jobs),
        "tool.organizePDF" => new PdfPagesTool(files, jobs),
        "tool.edit" => new EditorTool(files, jobs),
        "tool.redact" => new HideTool(files, jobs),
        "tool.annotate" => new AnnotateTool(files, jobs),
        "tool.trimMedia" => new TrimTool(files, jobs),
        "tool.splitMedia" => new SplitTool(files, jobs),
        "tool.frames" => new FramesTool(files, jobs),
        "tool.speed" => new SpeedTool(files, jobs),
        "tool.normalize" => new VolumeTool(files, jobs),
        "tool.channels" => new ChannelsTool(files, jobs),
        "tool.compressMedia" => new MediaCompressTool(files, jobs),
        "tool.join" => new JoinTool(files, jobs),
        "tool.cropVideo" => new CropTool(files, jobs),
        "tool.bleep" => new BleepTool(files, jobs),
        "tool.visualizer" => new VisualizerTool(files, jobs),
        "tool.redactVideo" => new VideoHideTool(files, jobs),
        _ => null,
    };

    /// <summary>
    /// <c>Mangosteen.exe --snapshot &lt;tool id&gt; &lt;output.png&gt; &lt;files…&gt;</c>: opens the tool, waits for its preview,
    /// saves the window content as a PNG and quits. Used in CI to look at every tool window.
    /// </summary>
    public static void Snapshot(string id, string output, IReadOnlyList<string> files)
    {
        Window window;
        if (id.StartsWith("traymenu", StringComparison.Ordinal))
        {
            var menu = new TrayMenu(light: !id.EndsWith("dark", StringComparison.Ordinal)) { KeepOpen = true };
            menu.AddHeader($"Mangosteen {Program.Version}", L("Shift / Ctrl / Alt + right-click a file"));
            menu.AddSeparator();
            menu.AddCaption(L("Choose files…"));
            menu.AddItem(L("Convert…"), "\uE8AB", () => { }, indent: true);
            menu.AddItem(L("Tools…"), "\uE90F", () => { }, indent: true);
            menu.AddSeparator();
            menu.AddItem(L("Settings…"), "\uE713", () => { });
            menu.AddItem(L("Quit Mangosteen"), "\uE7E8", () => { });
            window = menu;
        }
        else
        {
            // README screenshots: the dark theme and a selection in the middle of the timeline.
            MediaToolWindow.SnapshotDemo = Environment.GetEnvironmentVariable("MANGOSTEEN_SNAPSHOT_DEMO") == "1";
            var tool = Create(id, files, null!) ?? throw new ArgumentException($"Unknown tool {id}");
            if (Environment.GetEnvironmentVariable("MANGOSTEEN_THEME") == "dark")
            {
#pragma warning disable WPF0001 // Fluent theme is marked experimental in .NET 10.
                tool.ThemeMode = ThemeMode.Dark;
#pragma warning restore WPF0001
            }
            window = tool;
        }
        window.Show();
        var started = DateTime.UtcNow;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) =>
        {
            var waited = DateTime.UtcNow - started;
            if ((window is ToolWindow { IsSettled: false } || waited < TimeSpan.FromSeconds(1.5)) && waited < TimeSpan.FromSeconds(20))
            {
                return;
            }
            timer.Stop();
            window.Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    Save(window, output);
                    if (Environment.GetEnvironmentVariable("MANGOSTEEN_SCREEN") == "1")
                    {
                        SaveScreen(window, Path.ChangeExtension(output, ".screen.png"));
                    }
                    Log.Write($"snapshot {id} -> {output}");
                    Application.Current.Shutdown(0);
                }
                catch (Exception error)
                {
                    Log.Error("snapshot", error);
                    Application.Current.Shutdown(1);
                }
            }, DispatcherPriority.ContextIdle);
        };
        timer.Start();
    }

    private static void Save(Window window, string output)
    {
        window.UpdateLayout();
        // The whole window is rendered, so effects such as shadows keep their place. Twice the size, for sharp
        // screenshots on high-density screens.
        const double scale = 2;
        var width = Math.Ceiling(window.ActualWidth);
        var height = Math.Ceiling(window.ActualHeight);
        var background = new DrawingVisual();
        using (var context = background.RenderOpen())
        {
            context.DrawRectangle(WindowBackground(window), null, new Rect(0, 0, width, height));
        }
        var bitmap = new RenderTargetBitmap((int)(width * scale), (int)(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(background);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        using var stream = File.Create(output);
        encoder.Save(stream);
    }

    /// <summary>
    /// The colour behind the window's content. Fluent windows leave their background to the system backdrop (Mica),
    /// which a rendered bitmap does not have, so the theme's own background colour stands in for it.
    /// </summary>
    private static Brush WindowBackground(Window window)
    {
        if (window.Background is SolidColorBrush { Color.A: 0xFF } solid)
        {
            return solid;
        }
        foreach (var key in new[] { "ApplicationBackgroundBrush", "SolidBackgroundFillColorBaseBrush", "WindowBackground" })
        {
            if (window.TryFindResource(key) is SolidColorBrush { Color.A: 0xFF } brush)
            {
                Log.Write($"snapshot background: {key} {brush.Color}");
                return brush;
            }
        }
#pragma warning disable WPF0001 // Fluent theme is marked experimental in .NET 10.
        return window.ThemeMode == ThemeMode.Dark ? new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)) : SystemColors.WindowBrush;
#pragma warning restore WPF0001
    }

    /// <summary>
    /// With MANGOSTEEN_SCREEN=1 (the README screenshots), also copies the window from the screen, title bar included;
    /// CI runners have a visible desktop. Best effort: nothing is written when the capture fails.
    /// </summary>
    private static void SaveScreen(Window window, string output)
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out var bounds, Marshal.SizeOf<Native.RECT>()) != 0)
            {
                return;
            }
            var (width, height) = (bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
            using var bitmap = new System.Drawing.Bitmap(width, height);
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, new System.Drawing.Size(width, height));
            }
            bitmap.Save(output, System.Drawing.Imaging.ImageFormat.Png);
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or ExternalException or ArgumentException)
        {
            Log.Error("snapshot screen", error);
        }
    }
}
