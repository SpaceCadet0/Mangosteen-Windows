using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Mangosteen.Core.Imaging;
using Path = System.Windows.Shapes.Path;
using Point = System.Windows.Point;

namespace Mangosteen.Tools;

/// <summary>
/// Crop: drag the frame or its handles, pick proportions, or enter an exact output size. With several images the
/// frame drawn on the first one is used as is; the others get a centred crop of the same proportions.
/// </summary>
internal sealed class CropTool : ToolWindow
{
    private const int PreviewSide = 1600;
    private readonly List<string> _images;
    private readonly Jobs _jobs;
    private readonly Canvas _overlay = new();
    private readonly Image _image = new() { Stretch = Stretch.Fill };
    private readonly Grid _surface = new();
    private readonly Path _shade = new() { Fill = new SolidColorBrush(Color.FromArgb(0x90, 0, 0, 0)), IsHitTestVisible = false };
    private readonly Rectangle _border = new() { Stroke = Brushes.White, StrokeThickness = 2, IsHitTestVisible = false };
    private readonly List<(Rectangle Handle, CropMath.Edges Edges)> _handles = [];
    private readonly ComboBox _aspect = new() { MinWidth = 180 };
    private readonly TextBox _exactWidth = new() { Text = "1080", Width = 70 };
    private readonly TextBox _exactHeight = new() { Text = "1080", Width = 70 };
    private readonly StackPanel _exactPanel = new() { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed };
    private readonly TextBlock _pixels = new() { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7, Margin = new Thickness(12, 0, 12, 0) };
    private int _width = 1, _height = 1;
    private UnitRect _rect = UnitRect.Full;
    private (Point Start, UnitRect Rect, CropMath.Edges? Edges)? _drag;
    private bool _loaded;

    public CropTool(IReadOnlyList<string> files, Jobs jobs) : base(L("Crop"), files, L("Crop"))
    {
        _images = ToolOutput.Images(files);
        _jobs = jobs;

        _surface.Children.Add(_image);
        _surface.Children.Add(_overlay);
        _overlay.Children.Add(_shade);
        _overlay.Children.Add(_border);
        foreach (var edges in new[] { CropMath.Edges.TopLeft, CropMath.Edges.Top, CropMath.Edges.TopRight, CropMath.Edges.Right,
                     CropMath.Edges.BottomRight, CropMath.Edges.Bottom, CropMath.Edges.BottomLeft, CropMath.Edges.Left })
        {
            var handle = new Rectangle { Fill = Brushes.White, Stroke = new SolidColorBrush(Color.FromRgb(0x67, 0x1E, 0x38)), StrokeThickness = 1 };
            handle.Cursor = edges switch
            {
                CropMath.Edges.TopLeft or CropMath.Edges.BottomRight => Cursors.SizeNWSE,
                CropMath.Edges.TopRight or CropMath.Edges.BottomLeft => Cursors.SizeNESW,
                CropMath.Edges.Left or CropMath.Edges.Right => Cursors.SizeWE,
                _ => Cursors.SizeNS,
            };
            var captured = edges;
            handle.MouseLeftButtonDown += (_, e) => StartDrag(e, captured);
            _handles.Add((handle, edges));
            _overlay.Children.Add(handle);
        }
        _overlay.Background = Brushes.Transparent;
        _overlay.Cursor = Cursors.SizeAll;
        _overlay.MouseLeftButtonDown += (_, e) => StartDrag(e, null);
        _overlay.MouseMove += (_, e) => Drag(e);
        _overlay.MouseLeftButtonUp += (_, _) =>
        {
            _drag = null;
            _overlay.ReleaseMouseCapture();
        };

        var canvasFrame = new Border
        {
            Width = 720, Height = 440, CornerRadius = new CornerRadius(10), Padding = new Thickness(8),
            Background = new SolidColorBrush(Color.FromArgb(0x18, 0x80, 0x80, 0x80)),
            Child = new Viewbox { Child = _surface, Stretch = Stretch.Uniform },
        };

        foreach (var choice in CropMath.Aspects)
        {
            _aspect.Items.Add(choice.Original ? L("Same as original") : L(choice.Title));
        }
        _aspect.Items.Add(L("Exact size…"));
        _aspect.SelectedIndex = 0;
        _aspect.SelectionChanged += (_, _) =>
        {
            _exactPanel.Visibility = _aspect.SelectedIndex == CropMath.Aspects.Count ? Visibility.Visible : Visibility.Collapsed;
            ApplyAspect();
        };
        _exactPanel.Children.Add(_exactWidth);
        _exactPanel.Children.Add(new TextBlock { Text = " × ", VerticalAlignment = VerticalAlignment.Center });
        _exactPanel.Children.Add(_exactHeight);
        _exactPanel.Children.Add(new TextBlock { Text = " px", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 });
        _exactWidth.TextChanged += (_, _) => ApplyAspect();
        _exactHeight.TextChanged += (_, _) => ApplyAspect();
        var reset = new Button { Content = L("Reset"), MinWidth = 80 };
        reset.Click += (_, _) =>
        {
            _aspect.SelectedIndex = 0;
            _rect = UnitRect.Full;
            Redraw();
        };

        var controls = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(new TextBlock { Text = L("Proportions"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        left.Children.Add(_aspect);
        left.Children.Add(new Border { Width = 12 });
        left.Children.Add(_exactPanel);
        DockPanel.SetDock(reset, Dock.Right);
        DockPanel.SetDock(_pixels, Dock.Right);
        controls.Children.Add(reset);
        controls.Children.Add(_pixels);
        controls.Children.Add(left);

        var stack = new StackPanel();
        stack.Children.Add(canvasFrame);
        stack.Children.Add(controls);
        if (_images.Count > 1)
        {
            stack.Children.Add(Note(L("The frame is drawn on the first image; the others get a centred crop of the same proportions.")));
        }
        Body = stack;
        ActionEnabled = false;
        Load();
    }

    public override bool IsSettled => _loaded;

    private void Load()
    {
        if (_images.Count == 0)
        {
            return;
        }
        var first = _images[0];
        Task.Run(() =>
        {
            var full = ImageIO.Load(first);
            return (Preview: ImageIO.Scale(full, PreviewSide), full.PixelWidth, full.PixelHeight);
        }).ContinueWith(task =>
        {
            if (task.Exception is { } error)
            {
                Platform.Log.Error("crop preview", error);
                return;
            }
            var (preview, width, height) = task.Result;
            _width = width;
            _height = height;
            _image.Source = preview;
            _surface.Width = _overlay.Width = preview.PixelWidth;
            _surface.Height = _overlay.Height = preview.PixelHeight;
            _loaded = true;
            ActionEnabled = true;
            Redraw();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>The locked proportions (pixel width / height), or null for a free frame.</summary>
    private double? PixelAspect(int width, int height)
    {
        var index = _aspect.SelectedIndex;
        if (index == CropMath.Aspects.Count)
        {
            return int.TryParse(_exactWidth.Text, out var w) && int.TryParse(_exactHeight.Text, out var h) && w > 0 && h > 0 ? (double)w / h : null;
        }
        var choice = CropMath.Aspects[Math.Max(index, 0)];
        return choice.Original ? (double)width / height : choice.Ratio;
    }

    private double? UnitAspect => PixelAspect(_width, _height) is { } aspect ? aspect * _height / _width : null;

    private void ApplyAspect()
    {
        if (_loaded && PixelAspect(_width, _height) is { } aspect)
        {
            _rect = CropMath.CenteredRect(aspect, _width, _height);
        }
        Redraw();
    }

    private void StartDrag(MouseButtonEventArgs e, CropMath.Edges? edges)
    {
        if (!_loaded)
        {
            return;
        }
        _drag = (e.GetPosition(_overlay), _rect, edges);
        _overlay.CaptureMouse();
        e.Handled = true;
    }

    private void Drag(MouseEventArgs e)
    {
        if (_drag is not { } drag || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }
        var point = e.GetPosition(_overlay);
        var dx = (point.X - drag.Start.X) / _overlay.Width;
        var dy = (point.Y - drag.Start.Y) / _overlay.Height;
        if (drag.Edges is { } edges)
        {
            if (CropMath.Resize(drag.Rect, edges, dx, dy, UnitAspect) is { } resized)
            {
                _rect = resized;
            }
        }
        else
        {
            _rect = CropMath.Move(drag.Rect, dx, dy);
        }
        Redraw();
    }

    private void Redraw()
    {
        if (!_loaded)
        {
            return;
        }
        double w = _overlay.Width, h = _overlay.Height;
        var frame = new Rect(_rect.X * w, _rect.Y * h, _rect.Width * w, _rect.Height * h);
        _shade.Data = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(0, 0, w, h)), new RectangleGeometry(frame));
        Canvas.SetLeft(_border, frame.X);
        Canvas.SetTop(_border, frame.Y);
        _border.Width = frame.Width;
        _border.Height = frame.Height;
        _border.StrokeThickness = Math.Max(1, w / 400);
        var size = Math.Max(8, w / 70);
        foreach (var (handle, edges) in _handles)
        {
            var x = edges.HasFlag(CropMath.Edges.Left) ? frame.Left : edges.HasFlag(CropMath.Edges.Right) ? frame.Right : frame.Left + frame.Width / 2;
            var y = edges.HasFlag(CropMath.Edges.Top) ? frame.Top : edges.HasFlag(CropMath.Edges.Bottom) ? frame.Bottom : frame.Top + frame.Height / 2;
            handle.Width = handle.Height = size;
            handle.StrokeThickness = size / 8;
            Canvas.SetLeft(handle, x - size / 2);
            Canvas.SetTop(handle, y - size / 2);
        }
        var pixels = _rect.ToPixels(_width, _height);
        _pixels.Text = $"{pixels.Width} × {pixels.Height} px";
    }

    protected override void Run()
    {
        var firstRect = _rect;
        var first = _images.FirstOrDefault();
        var exact = _aspect.SelectedIndex == CropMath.Aspects.Count
            && int.TryParse(_exactWidth.Text, out var ew) && int.TryParse(_exactHeight.Text, out var eh) && ew > 0 && eh > 0
            ? (ew, eh) : ((int, int)?)null;
        var frameAspect = (double)firstRect.ToPixels(_width, _height).Width / firstRect.ToPixels(_width, _height).Height;
        var lockedFirst = PixelAspect(_width, _height);
        Close();
        _jobs.RunEach(L("Cropping"), L("Cropped"), _images, (input, _) =>
        {
            var image = ImageIO.Load(input);
            var rect = input == first ? firstRect
                : CropMath.CenteredRect(lockedFirst ?? frameAspect, image.PixelWidth, image.PixelHeight);
            var pixels = rect.ToPixels(image.PixelWidth, image.PixelHeight);
            BitmapSource cropped = new CroppedBitmap(image, new Int32Rect(pixels.X, pixels.Y, pixels.Width, pixels.Height));
            if (exact is { } size)
            {
                cropped = ImageIO.Resize(cropped, size.Item1, size.Item2);
            }
            var format = ToolOutput.KeepFormat(input);
            return ToolOutput.Write(input, L("cropped"), format, ImageIO.Encode(cropped, format, 0.92, ImageIO.ReadMetadata(input)));
        });
    }
}
