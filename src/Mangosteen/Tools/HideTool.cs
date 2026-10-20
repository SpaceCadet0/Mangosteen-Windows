using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Mangosteen.Core;
using Mangosteen.Core.Imaging;
using Point = System.Windows.Point;

namespace Mangosteen.Tools;

/// <summary>
/// Hide: finds faces and text and covers them with a fill, a blur or large pixels; areas can also be drawn by hand.
/// The result has no metadata, so location and camera details do not leak.
/// </summary>
internal sealed class HideTool : ToolWindow
{
    private const int PreviewSide = 1400;
    private readonly List<string> _images;
    private readonly Jobs _jobs;
    private readonly Image _image = new() { Stretch = Stretch.Fill };
    private readonly Canvas _overlay = new() { Background = Brushes.Transparent, Cursor = Cursors.Cross };
    private readonly Grid _surface = new();
    private readonly List<UnitRect> _regions = [];
    private readonly RadioButton[] _styles;
    private readonly Swatches _color = new(0xFF000000);
    private readonly TextBlock _status = new() { Opacity = 0.7, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly CheckBox _autoFaces = new() { Content = L("Faces"), Margin = new Thickness(0, 0, 14, 0) };
    private readonly CheckBox _autoText = new() { Content = L("Text") };
    private Bgra? _original;
    private Point? _drawStart;
    private Rectangle? _drawing;
    private bool _settled;

    public HideTool(IReadOnlyList<string> files, Jobs jobs) : base(L("Hide faces and text"), files, L("Save"))
    {
        _images = ToolOutput.Images(files);
        _jobs = jobs;
        StatusText = WheelCatalog.FileCount(_images.Count);

        _surface.Children.Add(_image);
        _surface.Children.Add(_overlay);
        _overlay.MouseLeftButtonDown += (_, e) => StartDrawing(e);
        _overlay.MouseMove += (_, e) => ContinueDrawing(e);
        _overlay.MouseLeftButtonUp += (_, e) => FinishDrawing(e);
        var canvasFrame = new Border
        {
            Width = 540, Height = 420, CornerRadius = new CornerRadius(10), Padding = new Thickness(8),
            Background = new SolidColorBrush(Color.FromArgb(0x18, 0x80, 0x80, 0x80)),
            Child = new Viewbox { Child = _surface, Stretch = Stretch.Uniform },
        };

        var faces = new Button { Content = L("Find faces"), MinWidth = 110, Margin = new Thickness(0, 0, 8, 0), IsEnabled = Detection.FacesSupported };
        faces.Click += (_, _) => Detect(faces: true);
        var text = new Button { Content = L("Find text"), MinWidth = 110, Margin = new Thickness(0, 0, 8, 0) };
        text.Click += (_, _) => Detect(faces: false);
        var clear = new Button { Content = L("Clear"), MinWidth = 80 };
        clear.Click += (_, _) =>
        {
            _regions.Clear();
            Redraw();
        };
        var buttons = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        buttons.Children.Add(faces);
        buttons.Children.Add(text);
        buttons.Children.Add(clear);

        _styles = [Radio(L("Fill"), true), Radio(L("Blur"), false), Radio(L("Pixels"), false)];
        var styles = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var style in _styles)
        {
            style.Margin = new Thickness(0, 0, 14, 0);
            style.Checked += (_, _) => Redraw();
            styles.Children.Add(style);
        }
        _color.Changed += _ => Redraw();

        var settings = new StackPanel { Width = 380, Margin = new Thickness(20, 0, 0, 0) };
        settings.Children.Add(buttons);
        settings.Children.Add(Row(L("Style"), styles));
        settings.Children.Add(Row(L("Colour"), _color));
        settings.Children.Add(Note(L("Drag on the picture to add an area; click an area to remove it.")));
        if (_images.Count > 1)
        {
            var auto = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            auto.Children.Add(_autoFaces);
            auto.Children.Add(_autoText);
            settings.Children.Add(Row(L("Other images"), auto));
            settings.Children.Add(Note(L("The areas above belong to the first image; on the others Mangosteen finds what is ticked here.")));
        }
        settings.Children.Add(_status);

        var body = new StackPanel { Orientation = Orientation.Horizontal };
        body.Children.Add(canvasFrame);
        body.Children.Add(settings);
        Body = body;
        ActionEnabled = _images.Count > 0;
        Load();
    }

    public override bool IsSettled => _settled;

    private static RadioButton Radio(string text, bool isChecked) => new() { Content = text, GroupName = "style", IsChecked = isChecked };

    private RedactionStyle ChosenStyle => (RedactionStyle)Math.Max(0, Array.FindIndex(_styles, s => s.IsChecked == true));

    private void Load()
    {
        if (_images.Count == 0)
        {
            _settled = true;
            return;
        }
        var first = _images[0];
        Task.Run(() => ImageIO.ToBgra(ImageIO.Load(first, PreviewSide))).ContinueWith(task =>
        {
            if (task.Exception is null)
            {
                _original = task.Result;
                _surface.Width = _overlay.Width = _original.Width;
                _surface.Height = _overlay.Height = _original.Height;
                Redraw();
            }
            _settled = true;
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void Detect(bool faces)
    {
        if (_original is not { } image)
        {
            return;
        }
        _status.Text = faces ? L("Looking for faces…") : L("Looking for text…");
        if (faces)
        {
            _autoFaces.IsChecked = true;
        }
        else
        {
            _autoText.IsChecked = true;
        }
        Task.Run(() => faces ? Detection.Faces(image) : Detection.Text(image)).ContinueWith(task =>
        {
            if (task.Exception is { } error)
            {
                _status.Text = error.InnerException?.Message ?? error.Message;
                return;
            }
            _regions.AddRange(task.Result);
            _status.Text = task.Result.Count == 0
                ? (faces ? L("No faces found.") : L("No text found. Windows recognises the languages installed in Settings › Time & language."))
                : string.Format(faces ? L("Faces found: {0}") : L("Lines of text found: {0}"), task.Result.Count);
            Redraw();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void StartDrawing(MouseButtonEventArgs e)
    {
        if (_original is null)
        {
            return;
        }
        _drawStart = e.GetPosition(_overlay);
        _drawing = new Rectangle { Stroke = Brushes.White, StrokeThickness = Math.Max(1, _overlay.Width / 400), StrokeDashArray = [4, 3] };
        _overlay.Children.Add(_drawing);
        _overlay.CaptureMouse();
    }

    private void ContinueDrawing(MouseEventArgs e)
    {
        if (_drawStart is not { } start || _drawing is null)
        {
            return;
        }
        var point = e.GetPosition(_overlay);
        var rect = new Rect(start, point);
        Canvas.SetLeft(_drawing, rect.X);
        Canvas.SetTop(_drawing, rect.Y);
        _drawing.Width = rect.Width;
        _drawing.Height = rect.Height;
    }

    private void FinishDrawing(MouseButtonEventArgs e)
    {
        _overlay.ReleaseMouseCapture();
        if (_drawStart is not { } start)
        {
            return;
        }
        _drawStart = null;
        var rect = Rect.Intersect(new Rect(start, e.GetPosition(_overlay)), new Rect(0, 0, _overlay.Width, _overlay.Height));
        if (!rect.IsEmpty && rect.Width > 4 && rect.Height > 4)
        {
            _regions.Add(new UnitRect(rect.X / _overlay.Width, rect.Y / _overlay.Height, rect.Width / _overlay.Width, rect.Height / _overlay.Height));
        }
        Redraw();
    }

    private void Redraw()
    {
        _drawing = null;
        _overlay.Children.Clear();
        if (_original is not { } original)
        {
            return;
        }
        var shown = original.Clone();
        Redaction.Apply(shown, _regions, ChosenStyle, _color.Value);
        _image.Source = ImageIO.ToBitmap(shown);
        double w = _overlay.Width, h = _overlay.Height;
        for (var i = 0; i < _regions.Count; i++)
        {
            var region = _regions[i];
            var index = i;
            var outline = new Rectangle
            {
                Width = region.Width * w, Height = region.Height * h, Stroke = new SolidColorBrush(Color.FromRgb(0xC0, 0x5A, 0x80)),
                StrokeThickness = Math.Max(1.5, w / 350), Fill = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = L("Click to remove"),
            };
            outline.MouseLeftButtonDown += (_, e) =>
            {
                _regions.RemoveAt(index);
                e.Handled = true;
                Redraw();
            };
            Canvas.SetLeft(outline, region.X * w);
            Canvas.SetTop(outline, region.Y * h);
            _overlay.Children.Add(outline);
        }
    }

    protected override void Run()
    {
        var regions = _regions.ToList();
        var style = ChosenStyle;
        var color = _color.Value;
        var faces = _autoFaces.IsChecked == true;
        var text = _autoText.IsChecked == true;
        var first = _images.FirstOrDefault();
        Close();
        _jobs.RunEach(L("Hiding"), L("Hidden"), _images, (input, _) =>
        {
            var image = ImageIO.ToBgra(ImageIO.Load(input));
            var areas = input == first ? regions : [];
            if (input != first)
            {
                if (faces)
                {
                    areas.AddRange(Detection.Faces(image));
                }
                if (text)
                {
                    areas.AddRange(Detection.Text(image));
                }
            }
            Redaction.Apply(image, areas, style, color);
            var format = ToolOutput.KeepFormat(input);
            // No metadata: location and camera details are not copied.
            return ToolOutput.Write(input, L("redacted"), format, ImageIO.Encode(ImageIO.ToBitmap(image), format, 0.92));
        });
    }
}
