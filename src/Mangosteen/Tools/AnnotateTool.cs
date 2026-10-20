using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Mangosteen.Wheel;

namespace Mangosteen.Tools;

/// <summary>
/// Annotate: arrows, shapes, text, numbered steps, pencil, highlighter, magnifier and pictures (from a file or
/// pasted with Ctrl+V). Select a mark to move or delete it; Ctrl+Z undoes.
/// </summary>
internal sealed class AnnotateTool : ToolWindow
{
    private enum Mode
    {
        Select, Arrow, Rectangle, Ellipse, Text, Number, Pen, Highlight, Magnifier, Picture,
    }

    private static readonly (Mode Mode, string Glyph, string Title)[] Modes =
    [
        (Mode.Select, "", "Select"), (Mode.Arrow, "", "Arrow"), (Mode.Rectangle, "", "Rectangle"),
        (Mode.Ellipse, "", "Oval"), (Mode.Text, "", "Text"), (Mode.Number, "", "Number"),
        (Mode.Pen, "", "Pencil"), (Mode.Highlight, "", "Highlighter"), (Mode.Magnifier, "", "Magnifier"),
        (Mode.Picture, "", "Picture"),
    ];

    private readonly string? _image;
    private readonly Jobs _jobs;
    private readonly Canvas _canvas = new() { ClipToBounds = true, Background = Brushes.Transparent };
    private readonly Image _photoView = new() { Stretch = Stretch.Fill, IsHitTestVisible = false };
    private readonly Canvas _marks = new() { IsHitTestVisible = false };
    private readonly Rectangle _selection = new() { Stroke = new SolidColorBrush(Color.FromRgb(0xC0, 0x5A, 0x80)), StrokeDashArray = [4, 3], IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly List<Annotation> _items = [];
    private readonly Stack<List<Annotation>> _undo = new();
    private readonly Swatches _color = new(0xFFE5484D);
    private readonly Slider _width;
    private readonly Slider _font;
    private readonly List<RadioButton> _modeButtons = [];
    private BitmapSource? _preview;
    private BitmapSource? _fullSizeSource;
    private double _scale = 1; // full-size pixels per preview pixel
    private int _fullWidth = 1, _fullHeight = 1;
    private Mode _mode = Mode.Arrow;
    private Annotation? _draft;
    private int? _selected;
    private Point _dragStart;
    private Annotation? _dragOriginal;
    private bool _settled;

    public AnnotateTool(IReadOnlyList<string> files, Jobs jobs) : base(L("Annotate"), files, L("Save"))
    {
        _image = ToolOutput.Images(files).FirstOrDefault();
        _jobs = jobs;
        StatusText = WheelCatalogText(files);

        var toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        foreach (var (mode, glyph, title) in Modes)
        {
            var button = new RadioButton
            {
                GroupName = "annotate", IsChecked = mode == _mode, ToolTip = L(title), Margin = new Thickness(0, 0, 4, 4),
                MinWidth = 40, Padding = new Thickness(8, 4, 8, 4),
                Content = new TextBlock { Text = glyph, FontFamily = new FontFamily(WheelGlyphs.FontFamily), FontSize = 16 },
            };
            button.SetResourceReference(StyleProperty, typeof(ToggleButton));
            var chosen = mode;
            button.Checked += (_, _) => SetMode(chosen);
            _modeButtons.Add(button);
            toolbar.Children.Add(button);
        }

        (var widthView, _width) = LabeledSlider(1, 10, 3, 3, v => $"{Math.Round(v)}");
        (var fontView, _font) = LabeledSlider(1, 10, 4, 4, v => $"{Math.Round(v)}");
        _color.Changed += value => ApplyToSelected(a => a with { Color = value });
        _width.ValueChanged += (_, _) => ApplyToSelected(a => a with { LineWidth = LineWidth });
        _font.ValueChanged += (_, _) => ApplyToSelected(a => a with { FontSize = FontSize });

        _canvas.Children.Add(_photoView);
        _canvas.Children.Add(_marks);
        _canvas.Children.Add(_selection);
        _canvas.MouseLeftButtonDown += (_, e) => Down(e);
        _canvas.MouseMove += (_, e) => MoveTo(e);
        _canvas.MouseLeftButtonUp += (_, e) => Up(e);
        var frame = new Border
        {
            Width = 780, Height = 420, CornerRadius = new CornerRadius(10), Padding = new Thickness(8),
            Background = new SolidColorBrush(Color.FromArgb(0x18, 0x80, 0x80, 0x80)),
            Child = new Viewbox { Child = _canvas, Stretch = Stretch.Uniform },
        };

        var properties = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        properties.Children.Add(Labeled(L("Colour"), _color, 0));
        properties.Children.Add(Labeled(L("Line"), widthView, 160));
        properties.Children.Add(Labeled(L("Text size"), fontView, 160));
        var hint = Note(L("Ctrl+V pastes a picture. Delete removes the selected mark, Ctrl+Z undoes."));
        if (ToolOutput.Images(files).Count > 1)
        {
            hint.Text += " " + L("Only the first image is annotated.");
        }

        var stack = new StackPanel();
        stack.Children.Add(toolbar);
        stack.Children.Add(frame);
        stack.Children.Add(properties);
        stack.Children.Add(hint);
        Body = stack;
        ActionEnabled = _image is not null;
        PreviewKeyDown += KeyPressed;
        Load();
    }

    private static string WheelCatalogText(IReadOnlyList<string> files) => Core.WheelCatalog.FileCount(Math.Min(1, ToolOutput.Images(files).Count));

    public override bool IsSettled => _settled;

    private static FrameworkElement Labeled(string label, FrameworkElement control, double width)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 18, 6) };
        panel.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        if (width > 0)
        {
            control.Width = width;
        }
        panel.Children.Add(control);
        return panel;
    }

    /// <summary>Line width and text size scale with the photo, so marks look the same on small and large images.</summary>
    private double Unit => Math.Max(_fullWidth, _fullHeight) / 600.0;

    private double LineWidth => Math.Max(1, _width.Value * Unit);

    private new double FontSize => Math.Max(8, _font.Value * 5 * Unit);

    private void Load()
    {
        if (_image is null)
        {
            _settled = true;
            return;
        }
        var path = _image;
        Task.Run(() =>
        {
            var full = ImageIO.Load(path);
            return (Preview: ImageIO.Scale(full, 1600), full.PixelWidth, full.PixelHeight);
        }).ContinueWith(task =>
        {
            if (task.Exception is null)
            {
                (_preview, _fullWidth, _fullHeight) = task.Result;
                _scale = (double)_fullWidth / _preview.PixelWidth;
                _canvas.Width = _photoView.Width = _fullWidth;
                _canvas.Height = _photoView.Height = _fullHeight;
                _photoView.Source = _preview;
                _fullSizeSource = new ScaledSource(_preview, _scale).Bitmap;
            }
            _settled = true;
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void SetMode(Mode mode)
    {
        _mode = mode;
        _canvas.Cursor = mode == Mode.Select ? Cursors.Arrow : Cursors.Cross;
        if (mode == Mode.Picture)
        {
            ChoosePicture();
        }
    }

    private void Redraw()
    {
        _marks.Children.Clear();
        if (_preview is null)
        {
            return;
        }
        // The preview shows the scaled photo; marks use full-size pixels, so the magnifier needs a full-size brush.
        var source = _fullSizeSource ?? _preview;
        foreach (var item in _items.Append(_draft).OfType<Annotation>())
        {
            _marks.Children.Add(AnnotationDrawing.Build(item, source));
        }
        if (_selected is { } index && index < _items.Count)
        {
            var bounds = Bounds(_items[index]);
            Canvas.SetLeft(_selection, bounds.X - Unit * 4);
            Canvas.SetTop(_selection, bounds.Y - Unit * 4);
            _selection.Width = bounds.Width + Unit * 8;
            _selection.Height = bounds.Height + Unit * 8;
            _selection.StrokeThickness = Unit * 1.5;
            _selection.Visibility = Visibility.Visible;
        }
        else
        {
            _selection.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Bounds including text and badges, whose size depends on the font.</summary>
    private static Rect Bounds(Annotation a) => a.Kind switch
    {
        AnnotationKind.Text => new Rect(a.Points[0], new Size(Math.Max(a.FontSize, a.Text.Length * a.FontSize * 0.55), a.FontSize * 1.35)),
        AnnotationKind.Number => new Rect(a.Points[0].X - a.FontSize * 0.8, a.Points[0].Y - a.FontSize * 0.8, a.FontSize * 1.6, a.FontSize * 1.6),
        AnnotationKind.Magnifier => new Rect(a.Points[0].X - (a.Points[^1] - a.Points[0]).Length, a.Points[0].Y - (a.Points[^1] - a.Points[0]).Length,
            (a.Points[^1] - a.Points[0]).Length * 2, (a.Points[^1] - a.Points[0]).Length * 2),
        _ => Inflate(a.Bounds, a.LineWidth),
    };

    private static Rect Inflate(Rect rect, double by)
    {
        rect.Inflate(by, by);
        return rect;
    }

    private void Remember()
    {
        _undo.Push(_items.ToList());
    }

    private void ApplyToSelected(Func<Annotation, Annotation> change)
    {
        if (_selected is { } index && index < _items.Count)
        {
            Remember();
            _items[index] = change(_items[index]);
            Redraw();
        }
    }

    private void Down(MouseButtonEventArgs e)
    {
        if (_preview is null)
        {
            return;
        }
        var point = e.GetPosition(_canvas);
        _dragStart = point;
        switch (_mode)
        {
            case Mode.Select:
                _selected = null;
                for (var i = _items.Count - 1; i >= 0; i--)
                {
                    if (Bounds(_items[i]).Contains(point))
                    {
                        _selected = i;
                        _dragOriginal = _items[i];
                        Remember();
                        break;
                    }
                }
                Redraw();
                break;
            case Mode.Text:
                AskText(point);
                return;
            case Mode.Number:
                Remember();
                var next = _items.Count(a => a.Kind == AnnotationKind.Number) + 1;
                _items.Add(new Annotation(AnnotationKind.Number, [point], _color.Value, LineWidth, FontSize) { Number = next });
                Redraw();
                return;
            case Mode.Picture:
                return;
            default:
                var kind = _mode switch
                {
                    Mode.Arrow => AnnotationKind.Arrow,
                    Mode.Rectangle => AnnotationKind.Rectangle,
                    Mode.Ellipse => AnnotationKind.Ellipse,
                    Mode.Pen => AnnotationKind.Pen,
                    Mode.Highlight => AnnotationKind.Highlight,
                    _ => AnnotationKind.Magnifier,
                };
                var color = kind == AnnotationKind.Highlight && _color.Value == 0xFFE5484D ? 0xFFF5C542 : _color.Value;
                _draft = new Annotation(kind, [point, point], color, LineWidth, FontSize);
                break;
        }
        _canvas.CaptureMouse();
    }

    private void MoveTo(MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }
        var point = e.GetPosition(_canvas);
        if (_mode == Mode.Select && _selected is { } index && _dragOriginal is { } original)
        {
            _items[index] = original.Translated(point - _dragStart);
            Redraw();
        }
        else if (_draft is { } draft)
        {
            _draft = draft.Kind is AnnotationKind.Pen or AnnotationKind.Highlight
                ? draft with { Points = draft.Points.Append(point).ToList() }
                : draft with { Points = [draft.Points[0], point] };
            Redraw();
        }
    }

    private void Up(MouseButtonEventArgs e)
    {
        _canvas.ReleaseMouseCapture();
        _dragOriginal = null;
        if (_draft is { } draft)
        {
            _draft = null;
            var size = (draft.Points[^1] - draft.Points[0]).Length;
            if (size > Unit * 3 || draft.Points.Count > 2)
            {
                Remember();
                _items.Add(draft);
            }
            Redraw();
        }
    }

    private void AskText(Point point)
    {
        var box = new TextBox { MinWidth = 160 * Unit, FontSize = FontSize, Foreground = new SolidColorBrush(Swatches.ToColor(_color.Value)) };
        Canvas.SetLeft(box, point.X);
        Canvas.SetTop(box, point.Y);
        _canvas.Children.Add(box);
        var done = false;
        void Commit()
        {
            if (done)
            {
                return;
            }
            done = true;
            _canvas.Children.Remove(box);
            if (box.Text.Trim().Length > 0)
            {
                Remember();
                _items.Add(new Annotation(AnnotationKind.Text, [point], _color.Value, LineWidth, FontSize) { Text = box.Text });
                Redraw();
            }
        }
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Commit();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                box.Text = "";
                Commit();
                e.Handled = true;
            }
        };
        box.LostFocus += (_, _) => Commit();
        Dispatcher.BeginInvoke(() => box.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void ChoosePicture()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = L("Picture"), Filter = $"{L("Images")}|*.jpg;*.jpeg;*.png;*.webp;*.bmp;*.gif;*.tif;*.tiff" };
        if (dialog.ShowDialog(this) == true)
        {
            try
            {
                AddPicture(ImageIO.Load(dialog.FileName, 2000));
            }
            catch (Exception error) when (error is IOException or NotSupportedException or FileFormatException)
            {
                MessageBox.Show(this, error.Message, L("Annotate"));
            }
        }
        _modeButtons[0].IsChecked = true;
    }

    private void AddPicture(BitmapSource picture)
    {
        if (_preview is null)
        {
            return;
        }
        var width = _fullWidth / 3.0;
        var height = width * picture.PixelHeight / picture.PixelWidth;
        var origin = new Point((_fullWidth - width) / 2, (_fullHeight - height) / 2);
        Remember();
        _items.Add(new Annotation(AnnotationKind.Picture, [origin, origin + new Vector(width, height)], 0, 0, 0) { Picture = picture });
        _selected = _items.Count - 1;
        Redraw();
    }

    private void KeyPressed(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox)
        {
            return;
        }
        if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control && _undo.Count > 0)
        {
            _items.Clear();
            _items.AddRange(_undo.Pop());
            _selected = null;
            Redraw();
            e.Handled = true;
        }
        else if (e.Key is Key.Delete or Key.Back && _selected is { } index && index < _items.Count)
        {
            Remember();
            _items.RemoveAt(index);
            _selected = null;
            Redraw();
            e.Handled = true;
        }
        else if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && Clipboard.ContainsImage() && Clipboard.GetImage() is { } pasted)
        {
            var copy = new FormatConvertedBitmap(pasted, PixelFormats.Bgra32, null, 0);
            copy.Freeze();
            AddPicture(copy);
            e.Handled = true;
        }
    }

    protected override void Run()
    {
        if (_image is not { } input)
        {
            return;
        }
        var items = _items.ToList();
        if (items.Count == 0)
        {
            MessageBox.Show(this, L("Nothing was changed."), L("Annotate"));
            return;
        }
        Close();
        _jobs.RunEach(L("Annotating"), L("Annotated"), [input], (path, _) =>
        {
            var photo = ImageIO.Load(path);
            var result = Sta.Run(() => AnnotationDrawing.Render(photo, items));
            var format = ToolOutput.KeepFormat(path);
            return ToolOutput.Write(path, L("annotated"), format, ImageIO.Encode(result, format, 0.92, ImageIO.ReadMetadata(path)));
        });
    }

    /// <summary>The preview bitmap stretched to full-size coordinates, so a magnifier picks the right area.</summary>
    private sealed class ScaledSource(BitmapSource preview, double scale)
    {
        public BitmapSource Bitmap { get; } = scale == 1 ? preview : Scaled(preview, scale);

        private static BitmapSource Scaled(BitmapSource preview, double scale)
        {
            var bitmap = new TransformedBitmap(preview, new ScaleTransform(scale, scale));
            bitmap.Freeze();
            return bitmap;
        }
    }
}
