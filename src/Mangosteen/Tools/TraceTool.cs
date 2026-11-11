using System.Diagnostics;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mangosteen.Core;
using Mangosteen.Core.Imaging;
using Mangosteen.Platform;
using Path = System.Windows.Shapes.Path;

namespace Mangosteen.Tools;

/// <summary>
/// vtracer (github.com/visioncortex/vtracer, MIT), carried inside Mangosteen.exe when build/fetch-vtracer.ps1 ran:
/// colour tracing with smoother curves. Unpacked into %LOCALAPPDATA%\Mangosteen\vtracer on first use.
/// </summary>
internal static class VTracer
{
    private const string Resource = "vtracer.exe";

    public static bool IsBundled { get; } = typeof(VTracer).Assembly.GetManifestResourceInfo(Resource) is not null;

    private static readonly Lock Gate = new();
    private static string? _path;

    /// <summary>The unpacked vtracer.exe; checked by its hash so a damaged copy is replaced.</summary>
    public static string ExePath()
    {
        lock (Gate)
        {
            if (_path is not null)
            {
                return _path;
            }
            using var stream = typeof(VTracer).Assembly.GetManifestResourceStream(Resource)
                ?? throw new InvalidOperationException("This build has no vtracer.");
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            var bytes = memory.ToArray();
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes))[..16];
            var folder = System.IO.Path.Combine(Log.Directory, "vtracer", hash);
            var path = System.IO.Path.Combine(folder, "vtracer.exe");
            if (!File.Exists(path) || new FileInfo(path).Length != bytes.Length)
            {
                Directory.CreateDirectory(folder);
                var temp = path + ".part";
                File.WriteAllBytes(temp, bytes);
                File.Move(temp, path, overwrite: true);
            }
            return _path = path;
        }
    }

    /// <summary>
    /// Colour tracing: the palette comes from our k-means (vtracer's own merged red into black), the picture goes to
    /// vtracer as PNG at the working size, and its SVG is read back into layers scaled to the original.
    /// </summary>
    public static VectorTracer.Result Trace(VectorTracer.Pixels pixels, byte[] png, int width, int height, VectorTracer.Options options,
        CancellationToken token)
    {
        var palette = VectorTracer.Palette(Downsampled(pixels, 800), options.Colors);
        if (palette.Count == 0)
        {
            return new VectorTracer.Result(width, height, []);
        }
        var workspace = Directory.CreateTempSubdirectory("mangosteen-vtracer-").FullName;
        try
        {
            var input = System.IO.Path.Combine(workspace, "in.png");
            var output = System.IO.Path.Combine(workspace, "out.svg");
            File.WriteAllBytes(input, png);
            var start = new ProcessStartInfo(ExePath()) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var argument in VectorTracer.VTracerArguments(input, output, options, palette))
            {
                start.ArgumentList.Add(argument);
            }
            using var process = Process.Start(start) ?? throw new InvalidOperationException("vtracer did not start.");
            var errors = process.StandardError.ReadToEndAsync(token);
            _ = process.StandardOutput.ReadToEndAsync(token);
            using (token.Register(() =>
            {
                try
                {
                    process.Kill();
                }
                catch (InvalidOperationException)
                {
                }
            }))
            {
                process.WaitForExit();
            }
            token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0 || !File.Exists(output))
            {
                throw new InvalidOperationException($"vtracer: {errors.Result.Trim()}");
            }
            var layers = VectorTracer.ParseVTracer(File.ReadAllText(output), (double)width / pixels.Width);
            if (options.DropBackground && VectorTracer.EdgeColor(Downsampled(pixels, 400), layers.Select(l => l.Color).Distinct().ToList()) is { } background)
            {
                layers.RemoveAll(l => l.Color == background);
            }
            return new VectorTracer.Result(width, height, layers);
        }
        finally
        {
            try
            {
                Directory.Delete(workspace, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>Every n-th pixel, enough for choosing colours.</summary>
    private static VectorTracer.Pixels Downsampled(VectorTracer.Pixels pixels, int maxSide)
    {
        var step = Math.Max(1, (int)Math.Ceiling(Math.Max(pixels.Width, pixels.Height) / (double)maxSide));
        if (step == 1)
        {
            return pixels;
        }
        var (w, h) = ((pixels.Width + step - 1) / step, (pixels.Height + step - 1) / step);
        var bytes = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var source = Math.Min(y * step, pixels.Height - 1) * pixels.Width + Math.Min(x * step, pixels.Width - 1);
                var color = pixels.Rgb(source);
                var d = (y * w + x) * 4;
                bytes[d] = (byte)Math.Round(color.B * 255);
                bytes[d + 1] = (byte)Math.Round(color.G * 255);
                bytes[d + 2] = (byte)Math.Round(color.R * 255);
                bytes[d + 3] = (byte)Math.Round(pixels.Alpha(source) * 255);
            }
        }
        return new VectorTracer.Pixels(bytes, w, h);
    }
}

/// <summary>
/// "To vector": a picture traced into SVG shapes, black and white or in a few colours, with the original and the
/// result side by side. The two previews move together: the mouse wheel zooms, dragging moves. The vector side is drawn
/// from its shapes, so it stays sharp at any zoom. Saved as "name.svg".
/// </summary>
internal sealed class TraceTool : ToolWindow
{
    private const double PaneSize = 380;
    private readonly Jobs _jobs;
    private readonly List<string> _images;
    private readonly RadioButton _mono = new() { Content = L("Black and white"), GroupName = "trace", IsChecked = true };
    private readonly RadioButton _color = new() { Content = L("Colours"), GroupName = "trace", Margin = new Thickness(0, 4, 0, 0) };
    private readonly Slider _colors;
    private readonly FrameworkElement _colorsView;
    private readonly Slider _detail;
    private readonly FrameworkElement _detailView;
    private readonly CheckBox _smooth = new() { Content = L("Smooth curves"), IsChecked = true };
    private readonly CheckBox _noBackground = new() { Content = L("No background") };
    private readonly TextBlock _status = new() { Opacity = 0.7, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Canvas _originalLayer = new();
    private readonly Canvas _vectorLayer = new();
    private readonly MatrixTransform _view = new();
    private BitmapSource? _source;
    private VectorTracer.Pixels? _pixels;
    private byte[]? _png;
    private double _scale = 1;
    private CancellationTokenSource? _tracing;
    private readonly System.Windows.Threading.DispatcherTimer _debounce;
    private bool _settled;
    private Point? _dragFrom;

    public TraceTool(IReadOnlyList<string> files, Jobs jobs) : base(L("To vector"), files, L("Save SVG"))
    {
        _jobs = jobs;
        _images = ToolOutput.Images(files);
        (_colorsView, _colors) = LabeledSlider(2, 16, 6, 6, v => $"{v:0}");
        (_detailView, _detail) = LabeledSlider(0, 100, 60, 60, v => $"{v:0}%");
        _colors.IsSnapToTickEnabled = true;
        _colors.TickFrequency = 1;
        _debounce = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Retrace();
        };
        foreach (var control in new Control[] { _mono, _color, _smooth, _noBackground })
        {
            if (control is ToggleButton toggle)
            {
                toggle.Checked += (_, _) => Changed();
                toggle.Unchecked += (_, _) => Changed();
            }
        }
        _colors.ValueChanged += (_, _) => Changed();
        _detail.ValueChanged += (_, _) => Changed();

        var form = new StackPanel { Width = 250, Margin = new Thickness(0, 0, 20, 0) };
        form.Children.Add(_mono);
        form.Children.Add(_color);
        form.Children.Add(Row(L("Colours"), _colorsView));
        form.Children.Add(Row(L("Detail"), _detailView));
        form.Children.Add(_smooth);
        form.Children.Add(_noBackground);
        form.Children.Add(_status);
        if (!VTracer.IsBundled)
        {
            form.Children.Add(Note(L("Colours are traced by Mangosteen itself in this build.")));
        }
        form.Children.Add(Note(L("Mouse wheel: zoom. Drag: move. Both previews move together.")));
        var zoom = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        Button ZoomButton(string text, Action click)
        {
            var button = new Button { Content = text, MinWidth = 40, Margin = new Thickness(0, 0, 6, 0) };
            button.Click += (_, _) => click();
            return button;
        }
        zoom.Children.Add(ZoomButton("−", () => Zoom(1 / 1.5, new Point(PaneSize / 2, PaneSize / 2))));
        zoom.Children.Add(ZoomButton("+", () => Zoom(1.5, new Point(PaneSize / 2, PaneSize / 2))));
        zoom.Children.Add(ZoomButton("1:1", ActualSize));
        zoom.Children.Add(ZoomButton(L("Fit"), Fit));
        form.Children.Add(zoom);

        var panes = new StackPanel { Orientation = Orientation.Horizontal };
        panes.Children.Add(Pane(L("Original"), _originalLayer));
        panes.Children.Add(new Border { Width = 12 });
        panes.Children.Add(Pane(L("Vector"), _vectorLayer));
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(form);
        row.Children.Add(panes);
        Body = row;
        ActionEnabled = false;
        _color.Checked += (_, _) => _colorsView.IsEnabled = true;
        _mono.Checked += (_, _) => _colorsView.IsEnabled = false;
        _colorsView.IsEnabled = false;
        Load();
    }

    public override bool IsSettled => _settled;

    private VectorTracer.Options Options => new(_color.IsChecked == true ? (int)_colors.Value : 1, _detail.Value / 100,
        _smooth.IsChecked == true, _noBackground.IsChecked == true);

    /// <summary>A preview pane: a clipped area whose content follows the shared zoom and pan.</summary>
    private FrameworkElement Pane(string title, Canvas layer)
    {
        layer.RenderTransform = _view;
        var host = new Border
        {
            Width = PaneSize, Height = PaneSize, ClipToBounds = true, CornerRadius = new CornerRadius(10), Child = layer, Cursor = Cursors.SizeAll,
            Background = new DrawingBrush(new GeometryDrawing(Brushes.LightGray, null,
                new GeometryGroup { Children = { new RectangleGeometry(new Rect(0, 0, 8, 8)), new RectangleGeometry(new Rect(8, 8, 8, 8)) } }))
            {
                TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 16, 16), ViewportUnits = BrushMappingMode.Absolute,
            },
        };
        host.MouseWheel += (_, e) => Zoom(e.Delta > 0 ? 1.25 : 0.8, e.GetPosition(host));
        host.MouseLeftButtonDown += (_, e) =>
        {
            _dragFrom = e.GetPosition(host);
            host.CaptureMouse();
        };
        host.MouseMove += (_, e) =>
        {
            if (_dragFrom is { } from && e.LeftButton == MouseButtonState.Pressed)
            {
                var to = e.GetPosition(host);
                var matrix = _view.Matrix;
                matrix.Translate(to.X - from.X, to.Y - from.Y);
                _view.Matrix = matrix;
                _dragFrom = to;
            }
        };
        host.MouseLeftButtonUp += (_, _) =>
        {
            _dragFrom = null;
            host.ReleaseMouseCapture();
        };
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = title, Opacity = 0.7, Margin = new Thickness(0, 0, 0, 4) });
        stack.Children.Add(host);
        return stack;
    }

    private void Zoom(double factor, Point at)
    {
        var matrix = _view.Matrix;
        var current = matrix.M11;
        factor = Math.Clamp(current * factor, 0.02, 64) / current;
        matrix.ScaleAt(factor, factor, at.X, at.Y);
        _view.Matrix = matrix;
    }

    private void Fit()
    {
        if (_source is null)
        {
            return;
        }
        var scale = Math.Min(PaneSize / _source.PixelWidth, PaneSize / _source.PixelHeight);
        _view.Matrix = new Matrix(scale, 0, 0, scale, (PaneSize - _source.PixelWidth * scale) / 2, (PaneSize - _source.PixelHeight * scale) / 2);
    }

    private void ActualSize()
    {
        var center = new Point(PaneSize / 2, PaneSize / 2);
        var inverse = _view.Matrix;
        inverse.Invert();
        var picture = inverse.Transform(center);
        _view.Matrix = new Matrix(1, 0, 0, 1, center.X - picture.X, center.Y - picture.Y);
    }

    private void Load()
    {
        if (_images.Count == 0)
        {
            _settled = true;
            return;
        }
        var path = _images[0];
        Task.Run(() =>
        {
            var image = ImageIO.Load(path, 4096);
            var working = ImageIO.Scale(image, VectorTracer.WorkingSize);
            var bgra = ImageIO.ToBgra(working);
            var pixels = new VectorTracer.Pixels(Unpremultiplied(bgra.Pixels), bgra.Width, bgra.Height);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(working));
            using var png = new MemoryStream();
            encoder.Save(png);
            return (image, pixels, png.ToArray(), (double)image.PixelWidth / working.PixelWidth);
        }).ContinueWith(task =>
        {
            if (task.Exception is { } error)
            {
                _status.Text = error.InnerException?.Message ?? error.Message;
                _settled = true;
                return;
            }
            (_source, _pixels, _png, _scale) = task.Result;
            _originalLayer.Children.Add(new Image { Source = _source, Width = _source.PixelWidth, Height = _source.PixelHeight });
            RenderOptions.SetBitmapScalingMode(_originalLayer, BitmapScalingMode.HighQuality);
            Fit();
            Retrace();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>WPF's Bgra32 is straight alpha already; kept as a copy so the tracer owns its buffer.</summary>
    private static byte[] Unpremultiplied(byte[] pixels) => (byte[])pixels.Clone();

    private void Changed()
    {
        _debounce.Stop();
        _debounce.Start();
    }

    private void Retrace()
    {
        if (_pixels is not { } pixels || _source is not { } source)
        {
            return;
        }
        _tracing?.Cancel();
        var cancel = _tracing = new CancellationTokenSource();
        var options = Options;
        var png = _png!;
        var scale = _scale;
        _status.Text = L("Tracing…");
        var clock = Stopwatch.StartNew();
        Task.Run(() => TraceWith(pixels, png, source.PixelWidth, source.PixelHeight, scale, options, cancel.Token), cancel.Token).ContinueWith(task =>
        {
            if (cancel.IsCancellationRequested)
            {
                return;
            }
            if (task.Exception is { } error)
            {
                _status.Text = error.InnerException?.Message ?? error.Message;
                Log.Error("trace", error);
            }
            else
            {
                Show(task.Result);
                _status.Text = string.Format(L("{0} shapes in {1} colours"), task.Result.Layers.Count, task.Result.Layers.Select(l => l.Color).Distinct().Count())
                    + $" · {clock.ElapsedMilliseconds} ms";
                ActionEnabled = true;
            }
            _settled = true;
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    internal static VectorTracer.Result TraceWith(VectorTracer.Pixels pixels, byte[] png, int width, int height, double scale,
        VectorTracer.Options options, CancellationToken token)
    {
        if (options.Colors > 1 && VTracer.IsBundled)
        {
            return VTracer.Trace(pixels, png, width, height, options, token);
        }
        return VectorTracer.Trace(pixels, options, scale, () => token.IsCancellationRequested);
    }

    /// <summary>The traced shapes as WPF paths over the same coordinates as the original.</summary>
    private void Show(VectorTracer.Result result)
    {
        _vectorLayer.Children.Clear();
        foreach (var layer in result.Layers)
        {
            try
            {
                _vectorLayer.Children.Add(Shape(layer));
            }
            catch (FormatException error)
            {
                Log.Write($"trace: a path could not be drawn ({error.Message})");
            }
        }
    }

    internal static Path Shape(VectorTracer.Layer layer)
    {
        var geometry = Geometry.Parse((layer.EvenOdd ? "F0 " : "F1 ") + layer.PathData);
        if (layer.Transform.Length > 0)
        {
            geometry = geometry.Clone();
            geometry.Transform = SvgTransform(layer.Transform);
        }
        geometry.Freeze();
        var c = layer.Color;
        return new Path
        {
            Data = geometry, IsHitTestVisible = false,
            Fill = new SolidColorBrush(Color.FromRgb((byte)Math.Round(c.R * 255), (byte)Math.Round(c.G * 255), (byte)Math.Round(c.B * 255))),
        };
    }

    /// <summary>"scale(s) translate(x,y)" as one transform, applied right to left as SVG does.</summary>
    private static Transform SvgTransform(string text)
    {
        var matrix = Matrix.Identity;
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(text, @"(\w+)\(([^)]*)\)").Reverse())
        {
            var numbers = match.Groups[2].Value.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries)
                .Select(n => double.Parse(n, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            var step = Matrix.Identity;
            switch (match.Groups[1].Value)
            {
                case "translate" when numbers.Length >= 1:
                    step.Translate(numbers[0], numbers.Length > 1 ? numbers[1] : 0);
                    break;
                case "scale" when numbers.Length >= 1:
                    step.Scale(numbers[0], numbers.Length > 1 ? numbers[1] : numbers[0]);
                    break;
            }
            matrix.Append(step);
        }
        return new MatrixTransform(matrix);
    }

    protected override void Run()
    {
        var options = Options;
        Close();
        _jobs.RunEach(L("Tracing"), L("Traced to SVG"), _images, (input, token) =>
        {
            var image = ImageIO.Load(input, 4096);
            var working = ImageIO.Scale(image, VectorTracer.WorkingSize);
            var bgra = ImageIO.ToBgra(working);
            var pixels = new VectorTracer.Pixels(bgra.Pixels, bgra.Width, bgra.Height);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(working));
            using var png = new MemoryStream();
            encoder.Save(png);
            var result = TraceWith(pixels, png.ToArray(), image.PixelWidth, image.PixelHeight, (double)image.PixelWidth / working.PixelWidth, options, token);
            var svg = VectorTracer.Svg(result);
            return OutputNaming.WriteAtomically(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(input))!, OutputNaming.BaseName(input), "svg",
                output => File.WriteAllText(output, svg));
        });
    }
}
