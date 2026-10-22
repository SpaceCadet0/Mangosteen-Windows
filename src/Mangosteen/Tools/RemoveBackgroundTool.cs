using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mangosteen.Core;
using Mangosteen.Core.Imaging;

namespace Mangosteen.Tools;

/// <summary>
/// Remove background: the subject over a checkerboard, then saved on transparency (PNG) or on a colour (the
/// original's format), optionally cropped to the subject. Writes "name (no background).ext".
/// </summary>
internal sealed class RemoveBackgroundTool : ToolWindow
{
    private const int PreviewSide = 1200;
    private readonly List<string> _images;
    private readonly Jobs _jobs;
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly RadioButton _transparent = new() { Content = L("Transparent (PNG)"), IsChecked = true, Margin = new Thickness(0, 0, 16, 0), GroupName = "background" };
    private readonly RadioButton _colour = new() { Content = L("Colour"), Margin = new Thickness(0, 0, 8, 0), GroupName = "background" };
    private readonly Swatches _fill = new(0xFFFFFFFF);
    private readonly CheckBox _crop = new() { Content = L("Crop to subject"), Margin = new Thickness(24, 0, 0, 0) };
    private readonly TextBlock _status = new() { Opacity = 0.7, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
    private Bgra? _preview;
    private byte[]? _mask; // the model's mask of the first image
    private byte[]? _previewMask;
    private bool _settled;

    public RemoveBackgroundTool(IReadOnlyList<string> files, Jobs jobs) : base(L("Remove background"), files, L("Save"))
    {
        _images = ToolOutput.Images(files).Where(p => FileFormats.Detect(p) != FileFormat.Svg).ToList();
        _jobs = jobs;
        StatusText = WheelCatalog.FileCount(_images.Count);

        var frame = new Border
        {
            Width = 720, Height = 440, CornerRadius = new CornerRadius(10), Padding = new Thickness(8), ClipToBounds = true,
            Background = Checkerboard(), Child = _image,
        };
        var options = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        options.Children.Add(_transparent);
        options.Children.Add(_colour);
        options.Children.Add(_fill);
        options.Children.Add(_crop);
        _transparent.Checked += (_, _) => Redraw();
        _colour.Checked += (_, _) => Redraw();
        _fill.Changed += _ =>
        {
            _colour.IsChecked = true;
            Redraw();
        };
        _crop.Click += (_, _) => Redraw();

        var stack = new StackPanel();
        stack.Children.Add(frame);
        stack.Children.Add(options);
        stack.Children.Add(_status);
        if (_images.Count > 1)
        {
            stack.Children.Add(Note(L("The preview shows the first image; every image is processed the same way.")));
        }
        Body = stack;
        ActionEnabled = false;
        Load();
    }

    public override bool IsSettled => _settled;

    /// <summary>The usual sign of transparency: a grey checkerboard.</summary>
    private static Brush Checkerboard()
    {
        var light = new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6));
        var dark = new SolidColorBrush(Color.FromRgb(0xC4, 0xC4, 0xC4));
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(light, null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
        drawing.Children.Add(new GeometryDrawing(dark, null, new RectangleGeometry(new Rect(0, 0, 8, 8))));
        drawing.Children.Add(new GeometryDrawing(dark, null, new RectangleGeometry(new Rect(8, 8, 8, 8))));
        var brush = new DrawingBrush(drawing) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 16, 16), ViewportUnits = BrushMappingMode.Absolute };
        brush.Freeze();
        return brush;
    }

    private void Load()
    {
        if (_images.Count == 0)
        {
            _settled = true;
            return;
        }
        var first = _images[0];
        _status.Text = L("Finding the subject…");
        Task.Run(() =>
        {
            var image = ImageIO.Load(first);
            var preview = ImageIO.ToBgra(Sta.Run(() => ImageIO.Scale(image, PreviewSide)));
            var mask = BackgroundRemoval.Mask(image);
            return (preview, mask, BackgroundRemoval.Scale(mask, preview.Width, preview.Height));
        }).ContinueWith(task =>
        {
            if (task.Exception is { } error)
            {
                Platform.Log.Error("remove background", error);
                _status.Text = error.InnerException?.Message ?? error.Message;
            }
            else
            {
                (_preview, _mask, _previewMask) = task.Result;
                if (SubjectMask.HasSubject(_mask))
                {
                    _status.Text = "";
                    ActionEnabled = true;
                }
                else
                {
                    _status.Text = NoSubject(first);
                }
                Redraw();
            }
            _settled = true;
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private static string NoSubject(string path) =>
        string.Format(L("No subject was found in “{0}” to separate from the background."), Path.GetFileName(path));

    private void Redraw()
    {
        if (_preview is not { } preview || _previewMask is not { } mask)
        {
            return;
        }
        var result = Compose(preview, mask, _colour.IsChecked == true ? _fill.Value : null, _crop.IsChecked == true);
        _image.Source = ImageIO.ToBitmap(result);
    }

    /// <summary>The cut-out, on a colour when given, cropped to the subject when asked.</summary>
    private static Bgra Compose(Bgra image, byte[] mask, uint? colour, bool crop)
    {
        var result = SubjectMask.Cut(image, mask);
        if (crop && SubjectMask.Bounds(mask, image.Width, image.Height) is { } bounds)
        {
            result = CropMath.Crop(result, bounds);
        }
        return colour is { } fill ? SubjectMask.OnColor(result, fill) : result;
    }

    /// <summary>
    /// Removes the background of one image and writes "name (no background).png", or keeps the original's format on
    /// a colour. <paramref name="mask"/> is the model's mask when it was already found (the previewed image).
    /// </summary>
    internal static string RemoveFile(string input, byte[]? mask, uint? colour, bool crop)
    {
        var image = ImageIO.Load(input);
        mask ??= BackgroundRemoval.Mask(image);
        if (!SubjectMask.HasSubject(mask))
        {
            throw new ShownException(NoSubject(input));
        }
        var pixels = ImageIO.ToBgra(image);
        var result = Compose(pixels, BackgroundRemoval.Scale(mask, pixels.Width, pixels.Height), colour, crop);
        var format = colour is null ? FileFormat.Png : ToolOutput.KeepFormat(input);
        var bitmap = ImageIO.ToBitmap(result, image.DpiX, image.DpiY);
        return ToolOutput.Write(input, L("no background"), format, ImageIO.Encode(bitmap, format, 0.92, ImageIO.ReadMetadata(input)));
    }

    protected override void Run()
    {
        var (first, mask) = (_images[0], _mask);
        uint? colour = _colour.IsChecked == true ? _fill.Value : null;
        var crop = _crop.IsChecked == true;
        Close();
        _jobs.RunEach(L("Removing the background"), L("Background removed"), _images,
            (input, _) => RemoveFile(input, input == first ? mask : null, colour, crop));
    }
}
