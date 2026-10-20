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
        "tool.crop" => new CropTool(files, jobs),
        "tool.compress" => new CompressTool(files, jobs),
        "tool.metadata" => new MetadataTool(files, jobs),
        "tool.frame" => new BackgroundTool(files, jobs),
        "tool.collage" => new CollageTool(files, jobs),
        "tool.organizePDF" => new PdfPagesTool(files, jobs),
        "tool.edit" => new EditorTool(files, jobs),
        "tool.redact" => new HideTool(files, jobs),
        _ => null,
    };

    /// <summary>
    /// <c>Mangosteen.exe --snapshot &lt;tool id&gt; &lt;output.png&gt; &lt;files…&gt;</c>: opens the tool, waits for its preview,
    /// saves the window content as a PNG and quits. Used in CI to look at every tool window.
    /// </summary>
    public static void Snapshot(string id, string output, IReadOnlyList<string> files)
    {
        var window = Create(id, files, null!) ?? throw new ArgumentException($"Unknown tool {id}");
        window.Show();
        var started = DateTime.UtcNow;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) =>
        {
            if (!window.IsSettled && DateTime.UtcNow - started < TimeSpan.FromSeconds(20))
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
        var content = (FrameworkElement)window.Content;
        var width = (int)Math.Ceiling(content.ActualWidth + content.Margin.Left + content.Margin.Right);
        var height = (int)Math.Ceiling(content.ActualHeight + content.Margin.Top + content.Margin.Bottom);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            var background = window.Background is SolidColorBrush { Color.A: > 0 } brush ? brush : SystemColors.WindowBrush;
            context.DrawRectangle(background, null, new Rect(0, 0, width, height));
            context.DrawRectangle(new VisualBrush(content) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                null, new Rect(content.Margin.Left, content.Margin.Top, content.ActualWidth, content.ActualHeight));
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        using var stream = File.Create(output);
        encoder.Save(stream);
    }
}
