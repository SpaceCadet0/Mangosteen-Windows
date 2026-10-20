using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Mangosteen.Core;
using Mangosteen.Core.Imaging;

namespace Mangosteen.Tools;

/// <summary>Collage: several photos on one canvas as a grid, a row, a column or around a featured photo.</summary>
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
    private readonly Swatches _background = new(0xFFFFFFFF);
    private readonly FrameworkElement _columnsRow;
    private List<BitmapSource> _thumbnails = [];
    private bool _settled;

    public CollageTool(IReadOnlyList<string> files, Jobs jobs) : base(L("Collage"), files, L("Create"))
    {
        _images = ToolOutput.Images(files);
        _jobs = jobs;
        StatusText = WheelCatalog.FileCount(_images.Count);
        var (frame, image) = PreviewPane(520, 400);
        _preview = image;
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
        foreach (var combo in new[] { _columns, _aspect, _width, _format })
        {
            combo.SelectionChanged += (_, _) => Changed();
        }
        _spacing.ValueChanged += (_, _) => Changed();
        _corners.ValueChanged += (_, _) => Changed();
        _background.Changed += _ => Changed();

        _columnsRow = Row(L("Columns"), _columns);
        var settings = new StackPanel { Width = 470, Margin = new Thickness(20, 0, 0, 0) };
        settings.Children.Add(layouts);
        settings.Children.Add(_columnsRow);
        settings.Children.Add(Row(L("Proportions"), _aspect));
        settings.Children.Add(Row(L("Width"), _width));
        settings.Children.Add(Row(L("Spacing"), spacingView));
        settings.Children.Add(Row(L("Corners"), cornersView));
        settings.Children.Add(Row(L("Background"), _background));
        settings.Children.Add(Row(L("Format"), _format));
        settings.Children.Add(Note(L("Photos keep the order they had in Explorer. Featured photo puts the first one large.")));

        var body = new StackPanel { Orientation = Orientation.Horizontal };
        body.Children.Add(frame);
        body.Children.Add(settings);
        Body = body;
        ActionEnabled = _images.Count >= 2;
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
        if (_background is null || _columnsRow is null)
        {
            return;
        }
        _columnsRow.Visibility = Settings().Layout == CollageLayout.Grid ? Visibility.Visible : Visibility.Collapsed;
        _debounce.Stop();
        _debounce.Start();
    }

    private void Draw()
    {
        _debounce.Stop();
        if (_thumbnails.Count > 0)
        {
            _preview.Source = Compose.Collage(_thumbnails, Settings(1200), _background.Value);
        }
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
