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
            window = Create(id, files, null!) ?? throw new ArgumentException($"Unknown tool {id}");
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
        // The whole window is rendered, so effects such as shadows keep their place.
        var width = (int)Math.Ceiling(window.ActualWidth);
        var height = (int)Math.Ceiling(window.ActualHeight);
        var background = new DrawingVisual();
        using (var context = background.RenderOpen())
        {
            var brush = window.Background is SolidColorBrush { Color.A: > 0 } solid ? solid : SystemColors.WindowBrush;
            context.DrawRectangle(brush, null, new Rect(0, 0, width, height));
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(background);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        using var stream = File.Create(output);
        encoder.Save(stream);
    }
}
