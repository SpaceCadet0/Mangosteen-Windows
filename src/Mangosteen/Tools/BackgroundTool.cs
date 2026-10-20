using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Mangosteen.Core.Imaging;

namespace Mangosteen.Tools;

/// <summary>Background: puts a photo on a colour, gradient, image or a blurred copy of itself, with margin, corners and shadow.</summary>
internal sealed class BackgroundTool : ToolWindow
{
    private const int PreviewSide = 900;
    private readonly List<string> _images;
    private readonly Jobs _jobs;
    private readonly Image _preview;
    private readonly DispatcherTimer _debounce;
    private readonly RadioButton[] _kinds;
    private readonly Swatches _color;
    private readonly Swatches _start;
    private readonly Swatches _end;
    private readonly Slider _angle;
    private readonly Slider _blur;
    private readonly ComboBox _aspect = new();
    private readonly Slider _padding;
    private readonly Slider _corners;
    private readonly Slider _shadow;
    private readonly FrameworkElement _colorRow, _gradientRows, _imageRow, _blurRow;
    private readonly TextBlock _imageName = new() { Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
    private BitmapSource? _photo;
    private BitmapSource? _backgroundPreview;
    private string? _backgroundPath;
    private bool _settled;

    public BackgroundTool(IReadOnlyList<string> files, Jobs jobs) : base(L("Background"), files, L("Save"))
    {
        _images = ToolOutput.Images(files);
        _jobs = jobs;
        StatusText = Core.WheelCatalog.FileCount(_images.Count);
        var (frame, image) = PreviewPane(440, 340);
        _preview = image;
        _debounce = new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Background, (_, _) => Draw(), Dispatcher);
        _debounce.Stop();

        var defaults = new FrameSettings();
        _kinds = [Radio(L("Colour"), defaults.Background == FrameBackground.Color), Radio(L("Gradient"), defaults.Background == FrameBackground.Gradient),
            Radio(L("Image"), false), Radio(L("Source image"), false)];
        var kindsPanel = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        foreach (var kind in _kinds)
        {
            kind.Margin = new Thickness(0, 0, 14, 4);
            kind.Checked += (_, _) => Changed();
            kindsPanel.Children.Add(kind);
        }

        _color = new Swatches(defaults.Color);
        _start = new Swatches(defaults.GradientStart);
        _end = new Swatches(defaults.GradientEnd);
        foreach (var swatches in new[] { _color, _start, _end })
        {
            swatches.Changed += _ => Changed();
        }
        (var angleView, _angle) = LabeledSlider(0, 360, defaults.GradientAngle, defaults.GradientAngle, v => $"{Math.Round(v)}°");
        (var blurView, _blur) = LabeledSlider(0, 1, defaults.Blur, defaults.Blur, Percent);
        (var paddingView, _padding) = LabeledSlider(0, 0.3, defaults.Padding, defaults.Padding, Percent);
        (var cornersView, _corners) = LabeledSlider(0, 0.25, defaults.CornerRadius, defaults.CornerRadius, Percent);
        (var shadowView, _shadow) = LabeledSlider(0, 1, defaults.Shadow, defaults.Shadow, Percent);
        foreach (var slider in new[] { _angle, _blur, _padding, _corners, _shadow })
        {
            slider.ValueChanged += (_, _) => Changed();
        }
        foreach (var aspect in Enum.GetValues<CanvasAspect>())
        {
            _aspect.Items.Add(aspect.Title());
        }
        _aspect.SelectedIndex = 0;
        _aspect.SelectionChanged += (_, _) => Changed();

        var choose = new Button { Content = L("Choose…"), MinWidth = 90 };
        choose.Click += (_, _) => ChooseBackground();
        var imagePanel = new DockPanel();
        DockPanel.SetDock(choose, Dock.Left);
        imagePanel.Children.Add(choose);
        imagePanel.Children.Add(_imageName);
        _imageName.Text = L("No image chosen");

        _colorRow = Row(L("Colour"), _color);
        var gradient = new StackPanel();
        gradient.Children.Add(Row(L("From"), _start));
        gradient.Children.Add(Row(L("To"), _end));
        gradient.Children.Add(Row(L("Angle"), angleView));
        _gradientRows = gradient;
        _imageRow = Row(L("Image"), imagePanel);
        _blurRow = Row(L("Blur"), blurView);

        var settings = new StackPanel { Width = 440, Margin = new Thickness(20, 0, 0, 0) };
        settings.Children.Add(kindsPanel);
        settings.Children.Add(_colorRow);
        settings.Children.Add(_gradientRows);
        settings.Children.Add(_imageRow);
        settings.Children.Add(_blurRow);
        settings.Children.Add(Row(L("Proportions"), _aspect));
        settings.Children.Add(Row(L("Margin"), paddingView));
        settings.Children.Add(Row(L("Corners"), cornersView));
        settings.Children.Add(Row(L("Shadow"), shadowView));
        if (_images.Count > 1)
        {
            settings.Children.Add(Note(L("The same background is used for every image.")));
        }

        var body = new StackPanel { Orientation = Orientation.Horizontal };
        body.Children.Add(frame);
        body.Children.Add(settings);
        Body = body;
        ActionEnabled = _images.Count > 0;
        Changed();
        Load();
    }

    public override bool IsSettled => _settled;

    private static string Percent(double value) => $"{Math.Round(value * 100)}%";

    private static RadioButton Radio(string text, bool isChecked) => new() { Content = text, GroupName = "background", IsChecked = isChecked };

    private FrameSettings Settings() => new()
    {
        Background = (FrameBackground)Math.Max(0, Array.FindIndex(_kinds, k => k.IsChecked == true)),
        Color = _color.Value,
        GradientStart = _start.Value,
        GradientEnd = _end.Value,
        GradientAngle = _angle.Value,
        BackgroundImage = _backgroundPath,
        Blur = _blur.Value,
        Aspect = (CanvasAspect)Math.Max(0, _aspect.SelectedIndex),
        Padding = _padding.Value,
        CornerRadius = _corners.Value,
        Shadow = _shadow.Value,
    };

    private void Load()
    {
        if (_images.Count == 0)
        {
            _settled = true;
            return;
        }
        var first = _images[0];
        Task.Run(() => ImageIO.Load(first, PreviewSide)).ContinueWith(task =>
        {
            if (task.Exception is null)
            {
                _photo = task.Result;
                Draw();
            }
            _settled = true;
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void ChooseBackground()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = L("Background image"), Filter = $"{L("Images")}|*.jpg;*.jpeg;*.png;*.webp;*.heic;*.tif;*.tiff;*.bmp;*.gif",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }
        try
        {
            _backgroundPreview = ImageIO.Load(dialog.FileName, PreviewSide);
            _backgroundPath = dialog.FileName;
            _imageName.Text = Path.GetFileName(dialog.FileName);
            _kinds[2].IsChecked = true;
            Changed();
        }
        catch (Exception error) when (error is IOException or NotSupportedException or FileFormatException)
        {
            MessageBox.Show(this, error.Message, L("Background"));
        }
    }

    private void Changed()
    {
        if (_shadow is null)
        {
            return;
        }
        var kind = Settings().Background;
        _colorRow.Visibility = kind == FrameBackground.Color ? Visibility.Visible : Visibility.Collapsed;
        _gradientRows.Visibility = kind == FrameBackground.Gradient ? Visibility.Visible : Visibility.Collapsed;
        _imageRow.Visibility = kind == FrameBackground.Image ? Visibility.Visible : Visibility.Collapsed;
        _blurRow.Visibility = kind is FrameBackground.Image or FrameBackground.Blur ? Visibility.Visible : Visibility.Collapsed;
        _debounce.Stop();
        _debounce.Start();
    }

    private void Draw()
    {
        _debounce.Stop();
        if (_photo is not null)
        {
            _preview.Source = Compose.Frame(_photo, Settings(), _backgroundPreview);
        }
    }

    protected override void Run()
    {
        var settings = Settings();
        Close();
        _jobs.RunEach(L("Adding background"), L("Background added"), _images, (input, _) =>
        {
            var photo = ImageIO.Load(input);
            var background = settings.BackgroundImage is { } path ? ImageIO.Load(path) : null;
            var result = Sta.Run(() => Compose.Frame(photo, settings, background));
            var format = ToolOutput.KeepFormat(input);
            return ToolOutput.Write(input, L("background"), format, ImageIO.Encode(result, format, 0.92));
        });
    }
}
