using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Mangosteen.Core;
using Mangosteen.Core.Imaging;

namespace Mangosteen.Tools;

/// <summary>
/// Photo editor: light, colour, detail and effects. Hold the mouse on the preview to compare with the original;
/// double-click a slider to reset it, and again to bring the value back.
/// </summary>
internal sealed class EditorTool : ToolWindow
{
    private const int PreviewSide = 1100;
    private readonly List<string> _images;
    private readonly Jobs _jobs;
    private readonly Image _preview;
    private readonly PreviewScheduler<BitmapSource> _scheduler;
    private readonly Dictionary<string, Slider> _sliders = [];
    private readonly ComboBox _effect = new();
    private Bgra? _original;
    private BitmapSource? _originalBitmap;
    private BitmapSource? _edited;
    private double _scale = 1;
    private bool _settled;

    private static readonly (string Section, (string Key, string Title, double Min, double Max)[] Sliders)[] Layout =
    [
        ("Light", [("exposure", "Exposure", -2, 2), ("brightness", "Brightness", -1, 1), ("contrast", "Contrast", -1, 1),
            ("highlights", "Highlights", -1, 1), ("shadows", "Shadows", -1, 1)]),
        ("Colour", [("temperature", "Temperature", -1, 1), ("tint", "Tint", -1, 1), ("saturation", "Saturation", -1, 1),
            ("vibrance", "Vibrance", -1, 1)]),
        ("Detail", [("sharpness", "Sharpness", 0, 1), ("clarity", "Clarity", 0, 1), ("noise", "Noise reduction", 0, 1),
            ("dehaze", "Dehaze", 0, 1)]),
        ("Effects", [("vignette", "Vignette", 0, 1), ("grain", "Grain", 0, 1)]),
    ];

    public EditorTool(IReadOnlyList<string> files, Jobs jobs) : base(L("Photo editor"), files, L("Save"))
    {
        _images = ToolOutput.Images(files);
        _jobs = jobs;
        StatusText = WheelCatalog.FileCount(_images.Count);
        var (frame, image) = PreviewPane(520, 420);
        _preview = image;
        _scheduler = new PreviewScheduler<BitmapSource>(Dispatcher, bitmap =>
        {
            _edited = bitmap;
            _preview.Source = bitmap;
            _settled = true;
        });
        frame.ToolTip = L("Hold to see the original");
        frame.PreviewMouseLeftButtonDown += (_, _) => _preview.Source = _originalBitmap ?? _preview.Source;
        frame.PreviewMouseLeftButtonUp += (_, _) => _preview.Source = _edited ?? _originalBitmap;
        frame.MouseLeave += (_, _) => _preview.Source = _edited ?? _originalBitmap;

        var panel = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        foreach (var (section, sliders) in Layout)
        {
            panel.Children.Add(new TextBlock { Text = L(section), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 8) });
            if (section == "Effects")
            {
                foreach (var effect in Enum.GetValues<PhotoEffect>())
                {
                    _effect.Items.Add(L(effect == PhotoEffect.None ? "None" : effect.ToString()));
                }
                _effect.SelectedIndex = 0;
                _effect.SelectionChanged += (_, _) => Changed();
                panel.Children.Add(Row(L("Filter"), _effect));
            }
            foreach (var (key, title, min, max) in sliders)
            {
                panel.Children.Add(Row(L(title), MakeSlider(key, min, max)));
            }
        }
        var reset = new Button { Content = L("Reset all"), HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 100, Margin = new Thickness(0, 6, 0, 0) };
        reset.Click += (_, _) =>
        {
            foreach (var slider in _sliders.Values)
            {
                slider.Value = 0;
            }
            _effect.SelectedIndex = 0;
        };
        panel.Children.Add(reset);
        if (_images.Count > 1)
        {
            panel.Children.Add(Note(L("The same adjustments are applied to every image.")));
        }
        var scroll = new ScrollViewer { Content = panel, Width = 400, Height = 420, Margin = new Thickness(20, 0, 0, 0), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        var body = new StackPanel { Orientation = Orientation.Horizontal };
        body.Children.Add(frame);
        body.Children.Add(scroll);
        Body = body;
        ActionEnabled = _images.Count > 0;
        Load();
    }

    public override bool IsSettled => _settled;

    private FrameworkElement MakeSlider(string key, double min, double max)
    {
        var slider = new Slider { Minimum = min, Maximum = max, Value = 0, VerticalAlignment = VerticalAlignment.Center };
        var readout = new TextBlock { Width = 42, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Text = "0" };
        double? remembered = null;
        slider.ValueChanged += (_, e) =>
        {
            readout.Text = key == "exposure" ? $"{e.NewValue:+0.0;-0.0;0}" : $"{Math.Round(e.NewValue * 100):+0;-0;0}";
            Changed();
        };
        slider.MouseDoubleClick += (_, e) =>
        {
            if (slider.Value != 0)
            {
                remembered = slider.Value;
                slider.Value = 0;
            }
            else if (remembered is { } value)
            {
                slider.Value = value;
            }
            e.Handled = true;
        };
        _sliders[key] = slider;
        var dock = new DockPanel();
        DockPanel.SetDock(readout, Dock.Right);
        dock.Children.Add(readout);
        dock.Children.Add(slider);
        return dock;
    }

    private PhotoAdjustments Adjustments()
    {
        double V(string key) => _sliders.TryGetValue(key, out var slider) ? slider.Value : 0;
        return new PhotoAdjustments
        {
            Exposure = V("exposure"), Brightness = V("brightness"), Contrast = V("contrast"), Highlights = V("highlights"),
            Shadows = V("shadows"), Temperature = V("temperature"), Tint = V("tint"), Saturation = V("saturation"),
            Vibrance = V("vibrance"), Sharpness = V("sharpness"), Clarity = V("clarity"), NoiseReduction = V("noise"),
            Dehaze = V("dehaze"), Effect = (PhotoEffect)Math.Max(0, _effect.SelectedIndex), Vignette = V("vignette"), Grain = V("grain"),
        };
    }

    private void Load()
    {
        if (_images.Count == 0)
        {
            _settled = true;
            return;
        }
        var first = _images[0];
        Task.Run(() =>
        {
            var full = ImageIO.Load(first);
            var preview = ImageIO.Scale(full, PreviewSide);
            return (preview, Scale: (double)preview.PixelWidth / full.PixelWidth);
        }).ContinueWith(task =>
        {
            if (task.Exception is null)
            {
                _originalBitmap = task.Result.preview;
                _original = ImageIO.ToBgra(_originalBitmap);
                _scale = task.Result.Scale;
                _preview.Source = _originalBitmap;
            }
            _settled = true;
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void Changed()
    {
        if (_original is not { } original)
        {
            return;
        }
        var adjustments = Adjustments();
        var scale = _scale;
        _scheduler.Request(token => ImageIO.ToBitmap(PhotoEditor.Apply(original, adjustments, scale, token)), 60);
    }

    protected override void Run()
    {
        var adjustments = Adjustments();
        if (adjustments.IsIdentity)
        {
            MessageBox.Show(this, L("Nothing was changed."), L("Photo editor"));
            return;
        }
        Close();
        _jobs.RunEach(L("Editing"), L("Edited"), _images, (input, token) =>
        {
            var image = ImageIO.Load(input);
            var edited = PhotoEditor.Apply(ImageIO.ToBgra(image), adjustments, 1, token);
            var format = ToolOutput.KeepFormat(input);
            var bitmap = ImageIO.ToBitmap(edited, image.DpiX, image.DpiY);
            return ToolOutput.Write(input, L("edited"), format, ImageIO.Encode(bitmap, format, 0.92, ImageIO.ReadMetadata(input)));
        });
    }
}
