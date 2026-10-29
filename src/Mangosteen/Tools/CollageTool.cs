using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Mangosteen.Core;
using Mangosteen.Core.Imaging;

namespace Mangosteen.Tools;

/// <summary>
/// Collage: several photos on one canvas as a grid, a row, a column or around a featured photo, on a colour or a
/// gradient, with an optional shadow. A photo dragged in the preview onto another one takes its place.
/// </summary>
internal sealed class CollageTool : ToolWindow
{
    private static readonly int[] Widths = [1080, 2000, 3000, 4000];
    private static readonly FileFormat[] Formats = [FileFormat.Jpg, FileFormat.Png, FileFormat.Webp];
    private readonly List<string> _images;
    private readonly Jobs _jobs;
    private readonly Image _preview;
    private readonly DispatcherTimer _debounce;
    private readonly RadioButton[] _layouts;
    private readonly ComboBox _columns = new();
    private readonly ComboBox _aspect = new();
    private readonly ComboBox _width = new();
    private readonly ComboBox _format = new();
    private readonly Slider _spacing;
    private readonly Slider _corners;
    private readonly Slider _shadow;
    private readonly Slider _angle;
    private readonly RadioButton _colorFill = new() { Content = L("Colour"), GroupName = "fill", IsChecked = true, Margin = new Thickness(0, 0, 14, 0) };
    private readonly RadioButton _gradientFill = new() { Content = L("Gradient"), GroupName = "fill" };
    private readonly Swatches _background = new(0xFFFFFFFF);
    private readonly Swatches _start = new(new CollageSettings().GradientStart);
    private readonly Swatches _end = new(new CollageSettings().GradientEnd);
    private readonly FrameworkElement _columnsRow, _colorRow, _gradientRows;
    private readonly Canvas _overlay = new() { IsHitTestVisible = false };
    private List<BitmapSource> _thumbnails = [];
    /// <summary>Canvas width and cells of the shown preview, to find the photo under the pointer.</summary>
    private (double Width, List<Box> Cells)? _layout;
    private int? _dragSource;
    private Point _dragStart;
    private bool _dragging;
    private bool _settled;

    /// <summary>Snapshots: open on a gradient with shadows, a photo half-way through being dragged onto the next one.</summary>
    public static bool SnapshotDemo { get; set; }

    public CollageTool(IReadOnlyList<string> files, Jobs jobs) : base(L("Collage"), files, L("Create"))
    {
        _images = ToolOutput.Images(files);
        _jobs = jobs;
        StatusText = WheelCatalog.FileCount(_images.Count);
        var (frame, image) = PreviewPane(440, 360);
        _preview = image;
        var stack = new Grid();
        frame.Child = stack;
        stack.Children.Add(image);
        stack.Children.Add(_overlay);
        image.Cursor = Cursors.Hand;
        image.MouseLeftButtonDown += PressPhoto;
        image.MouseMove += DragPhoto;
        image.MouseLeftButtonUp += DropPhoto;
        image.LostMouseCapture += (_, _) => EndDrag();
        _debounce = new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Background, (_, _) => Draw(), Dispatcher);
        _debounce.Stop();

        _layouts = [Radio(L("Grid"), true), Radio(L("Row"), false), Radio(L("Column"), false), Radio(L("Featured photo"), false)];
        var layouts = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        foreach (var layout in _layouts)
        {
            layout.Margin = new Thickness(0, 0, 14, 4);
            layout.Checked += (_, _) => Changed();
            layouts.Children.Add(layout);
        }
        _columns.Items.Add(L("Auto"));
        for (var c = 1; c <= 6; c++)
        {
            _columns.Items.Add(c.ToString());
        }
        _columns.SelectedIndex = 0;
        foreach (var aspect in Enum.GetValues<CanvasAspect>())
        {
            _aspect.Items.Add(aspect.Title());
        }
        _aspect.SelectedIndex = 0;
        foreach (var width in Widths)
        {
            _width.Items.Add($"{width} px");
        }
        _width.SelectedIndex = 2;
        foreach (var format in Formats)
        {
            _format.Items.Add(format.DisplayName());
        }
        _format.SelectedIndex = 0;
        (var spacingView, _spacing) = LabeledSlider(0, 0.05, 0.015, 0.015, v => $"{Math.Round(v * 1000) / 10}%");
        (var cornersView, _corners) = LabeledSlider(0, 0.2, 0, 0, v => $"{Math.Round(v * 100)}%");
        var defaults = new CollageSettings();
        (var angleView, _angle) = LabeledSlider(0, 360, defaults.GradientAngle, defaults.GradientAngle, v => $"{Math.Round(v)}°");
        (var shadowView, _shadow) = LabeledSlider(0, 1, defaults.Shadow, defaults.Shadow, v => $"{Math.Round(v * 100)}%");
        foreach (var combo in new[] { _columns, _aspect, _width, _format })
        {
            combo.SelectionChanged += (_, _) => Changed();
        }
        foreach (var slider in new[] { _spacing, _corners, _angle, _shadow })
        {
            slider.ValueChanged += (_, _) => Changed();
        }
        foreach (var swatches in new[] { _background, _start, _end })
        {
            swatches.Changed += _ => Changed();
        }
        _colorFill.Checked += (_, _) => Changed();
        _gradientFill.Checked += (_, _) => Changed();
        var fills = new WrapPanel();
        fills.Children.Add(_colorFill);
        fills.Children.Add(_gradientFill);

        _columnsRow = Row(L("Columns"), _columns);
        _colorRow = Row(L("Colour"), _background);
        var gradient = new StackPanel();
        gradient.Children.Add(Row(L("From"), _start));
        gradient.Children.Add(Row(L("To"), _end));
        gradient.Children.Add(Row(L("Angle"), angleView));
        _gradientRows = gradient;
        var settings = new StackPanel { Width = 440, Margin = new Thickness(20, 0, 0, 0) };
        settings.Children.Add(layouts);
        settings.Children.Add(_columnsRow);
        settings.Children.Add(Row(L("Proportions"), _aspect));
        settings.Children.Add(Row(L("Width"), _width));
        settings.Children.Add(Row(L("Spacing"), spacingView));
        settings.Children.Add(Row(L("Corners"), cornersView));
        settings.Children.Add(Row(L("Background"), fills));
        settings.Children.Add(_colorRow);
        settings.Children.Add(_gradientRows);
        settings.Children.Add(Row(L("Shadow"), shadowView));
        settings.Children.Add(Row(L("Format"), _format));
        settings.Children.Add(Note(L("Order — drag the photos in the preview. Featured photo puts the first one large.")));

        var body = new StackPanel { Orientation = Orientation.Horizontal };
        body.Children.Add(frame);
        body.Children.Add(settings);
        Body = body;
        ActionEnabled = _images.Count >= 2;
        if (SnapshotDemo)
        {
            _gradientFill.IsChecked = true;
            _shadow.Value = 0.6;
            _corners.Value = 0.06;
        }
        Changed();
        Load();
    }

    public override bool IsSettled => _settled;

    private static RadioButton Radio(string text, bool isChecked) => new() { Content = text, GroupName = "layout", IsChecked = isChecked };

    private CollageSettings Settings(int? width = null) => new()
    {
        Layout = (CollageLayout)Math.Max(0, Array.FindIndex(_layouts, l => l.IsChecked == true)),
        Columns = Math.Max(0, _columns.SelectedIndex),
        Aspect = (CanvasAspect)Math.Max(0, _aspect.SelectedIndex),
        Width = width ?? Widths[Math.Max(0, _width.SelectedIndex)],
        Spacing = _spacing.Value,
        CornerRadius = _corners.Value,
        Fill = _gradientFill.IsChecked == true ? CollageFill.Gradient : CollageFill.Color,
        GradientStart = _start.Value,
        GradientEnd = _end.Value,
        GradientAngle = _angle.Value,
        Shadow = _shadow.Value,
    };

    private void Load()
    {
        var images = _images.ToList();
        Task.Run(() => images.Select(p => ImageIO.Load(p, 700)).ToList()).ContinueWith(task =>
        {
            if (task.Exception is null)
            {
                _thumbnails = task.Result;
                Draw();
                if (SnapshotDemo && _layout is { Cells.Count: > 1 } layout)
                {
                    _dragSource = 0;
                    _dragging = true;
                    var target = layout.Cells[1];
                    UpdateLayout();
                    ShowDrag(ToPreview(new Point(target.X + target.Width / 2, target.Y + target.Height / 2)));
                }
            }
            else
            {
                Platform.Log.Error("collage preview", task.Exception);
            }
            _settled = true;
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void Changed()
    {
        if (_shadow is null || _columnsRow is null || _gradientRows is null)
        {
            return;
        }
        var settings = Settings();
        _columnsRow.Visibility = settings.Layout == CollageLayout.Grid ? Visibility.Visible : Visibility.Collapsed;
        _colorRow.Visibility = settings.Fill == CollageFill.Color ? Visibility.Visible : Visibility.Collapsed;
        _gradientRows.Visibility = settings.Fill == CollageFill.Gradient ? Visibility.Visible : Visibility.Collapsed;
        _debounce.Stop();
        _debounce.Start();
    }

    private void Draw()
    {
        _debounce.Stop();
        if (_thumbnails.Count > 0)
        {
            var settings = Settings(1200);
            _preview.Source = Compose.Collage(_thumbnails, settings, _background.Value);
            var (width, _, cells) = Collage.Layout(_thumbnails.Select(p => (double)p.PixelWidth / p.PixelHeight).ToList(), settings);
            _layout = (width, cells);
        }
    }

    // Reordering in the preview: press on a photo, drag it onto another one and let go.

    /// <summary>Preview pixels per canvas pixel.</summary>
    private double Scale => _layout is { Width: > 0 } layout ? _preview.ActualWidth / layout.Width : 0;

    private int? CellAt(Point point)
    {
        if (_layout is not { } layout || Scale <= 0)
        {
            return null;
        }
        var (x, y) = (point.X / Scale, point.Y / Scale);
        var index = layout.Cells.FindIndex(c => x >= c.X && x < c.X + c.Width && y >= c.Y && y < c.Y + c.Height);
        return index >= 0 && index < _thumbnails.Count ? index : null;
    }

    /// <summary>A point on the canvas in the preview image's coordinates.</summary>
    private Point ToPreview(Point canvas) => new(canvas.X * Scale, canvas.Y * Scale);

    private void PressPhoto(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(_preview);
        _dragSource = CellAt(_dragStart);
        if (_dragSource is not null)
        {
            _preview.CaptureMouse();
            e.Handled = true;
        }
    }

    private void DragPhoto(object sender, MouseEventArgs e)
    {
        if (_dragSource is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }
        var point = e.GetPosition(_preview);
        _dragging |= (point - _dragStart).Length > 4;
        if (_dragging)
        {
            ShowDrag(point);
        }
    }

    private void DropPhoto(object sender, MouseButtonEventArgs e)
    {
        if (_dragSource is { } source && _dragging && CellAt(e.GetPosition(_preview)) is { } target && target != source)
        {
            Collage.Move(_images, source, target);
            Collage.Move(_thumbnails, source, target);
            Draw();
        }
        _preview.ReleaseMouseCapture();
        EndDrag();
    }

    private void EndDrag()
    {
        _dragSource = null;
        _dragging = false;
        _overlay.Children.Clear();
    }

    /// <summary>The dragged photo's cell darkened, the cell under the pointer outlined, a thumbnail at the pointer.</summary>
    private void ShowDrag(Point point)
    {
        _overlay.Children.Clear();
        if (_dragSource is not { } source || _layout is not { } layout)
        {
            return;
        }
        var origin = _preview.TranslatePoint(new Point(0, 0), _overlay);
        void Add(UIElement element, double x, double y)
        {
            Canvas.SetLeft(element, origin.X + x);
            Canvas.SetTop(element, origin.Y + y);
            _overlay.Children.Add(element);
        }
        Rect Cell(int index)
        {
            var cell = layout.Cells[index];
            return new Rect(cell.X * Scale, cell.Y * Scale, cell.Width * Scale, cell.Height * Scale);
        }
        var from = Cell(source);
        Add(new System.Windows.Shapes.Rectangle { Width = from.Width, Height = from.Height, Fill = new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0)) }, from.X, from.Y);
        if (CellAt(point) is { } target && target != source)
        {
            var to = Cell(target);
            Add(new System.Windows.Shapes.Rectangle { Width = to.Width, Height = to.Height, Stroke = new SolidColorBrush(Wheel.WheelWindow.Brand), StrokeThickness = 3 }, to.X, to.Y);
        }
        var thumbnail = new Border
        {
            Width = 72, Height = 72, CornerRadius = new CornerRadius(6), Opacity = 0.9, ClipToBounds = true,
            Background = new ImageBrush(_thumbnails[source]) { Stretch = Stretch.UniformToFill },
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Opacity = 0.4 },
        };
        Add(thumbnail, point.X - 36, point.Y - 36);
    }

    protected override void Run()
    {
        var settings = Settings();
        var background = _background.Value;
        var format = Formats[Math.Max(0, _format.SelectedIndex)];
        var images = _images.ToList();
        Close();
        _jobs.RunOnce(L("Making a collage"), L("Collage created"), WheelCatalog.FileCount(images.Count), () =>
        {
            // Each photo is loaded only as large as its cell can show.
            var limit = Math.Max(settings.Width, 1000);
            var photos = images.Select(p => ImageIO.Load(p, limit)).ToList();
            var result = Sta.Run(() => Compose.Collage(photos, settings, background));
            var first = Path.GetFullPath(images[0]);
            return OutputNaming.WriteAtomically(Path.GetDirectoryName(first)!, L("Collage"), format.Extension(),
                temp => File.WriteAllBytes(temp, ImageIO.Encode(result, format, 0.92)));
        });
    }
}
