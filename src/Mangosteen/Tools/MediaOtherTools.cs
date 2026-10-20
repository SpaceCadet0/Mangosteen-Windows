using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Mangosteen.Core;
using Mangosteen.Core.Imaging;
using Mangosteen.Core.Media;
using Mangosteen.Media;
using Path = System.IO.Path;
using Point = System.Windows.Point;

namespace Mangosteen.Tools;

/// <summary>Compress video and audio: a quality preset or a target size, a height limit and H.265.</summary>
internal sealed class MediaCompressTool : ToolWindow
{
    private static readonly int[] Heights = [2160, 1440, 1080, 720, 480];
    private readonly List<string> _media;
    private readonly Jobs _jobs;
    private readonly RadioButton[] _quality;
    private readonly CheckBox _useTarget = new() { Content = L("Fit into a file size") };
    private readonly TextBox _target = new() { Text = "25", Width = 70 };
    private readonly ComboBox _height = new();
    private readonly CheckBox _hevc = new() { Content = L("H.265 (smaller files, slower, not every app plays it)") };
    private readonly TextBlock _estimate = new() { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly PreviewScheduler<string> _scheduler;
    private bool _settled;

    public MediaCompressTool(IReadOnlyList<string> files, Jobs jobs) : base(L("Compress"), files, L("Compress"))
    {
        _media = files.Where(f => FileFormats.Detect(f)?.Category().IsMedia() == true).ToList();
        _jobs = jobs;
        StatusText = WheelCatalog.FileCount(_media.Count);
        _scheduler = new PreviewScheduler<string>(Dispatcher, text =>
        {
            _estimate.Text = text;
            _settled = true;
        }, error =>
        {
            _estimate.Text = error.Message;
            _settled = true;
        });
        _quality = [Radio(L("High quality")), Radio(L("Balanced"), true), Radio(L("Smallest size"))];
        var qualities = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        foreach (var radio in _quality)
        {
            radio.Margin = new Thickness(0, 0, 16, 0);
            radio.Checked += (_, _) => Changed();
            qualities.Children.Add(radio);
        }
        _height.Items.Add(L("Unchanged"));
        foreach (var height in Heights)
        {
            _height.Items.Add($"{height}p");
        }
        _height.SelectedIndex = 0;
        var target = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        target.Children.Add(_useTarget);
        target.Children.Add(new Border { Width = 12 });
        target.Children.Add(_target);
        target.Children.Add(new TextBlock { Text = " " + L("MB"), VerticalAlignment = VerticalAlignment.Center });
        _useTarget.Checked += (_, _) => Changed();
        _useTarget.Unchecked += (_, _) => Changed();
        _target.TextChanged += (_, _) => Changed();
        _height.SelectionChanged += (_, _) => Changed();
        _hevc.Checked += (_, _) => Changed();
        _hevc.Unchecked += (_, _) => Changed();

        var stack = new StackPanel { Width = 520 };
        stack.Children.Add(qualities);
        stack.Children.Add(target);
        stack.Children.Add(Row(L("Height at most"), _height));
        stack.Children.Add(_hevc);
        stack.Children.Add(_estimate);
        stack.Children.Add(Note(L("Lossless audio (WAV, FLAC, AIFF) becomes M4A; video keeps MP4, MOV or MKV, others become MP4.")));
        Body = stack;
        ActionEnabled = _media.Count > 0;
        Changed();
    }

    public override bool IsSettled => _settled;

    private static RadioButton Radio(string text, bool isChecked = false) => new() { Content = text, GroupName = "quality", IsChecked = isChecked };

    private MediaMath.MediaCompression Settings() => new(
        (MediaMath.MediaQuality)Math.Max(0, Array.FindIndex(_quality, q => q.IsChecked == true)),
        _height.SelectedIndex > 0 ? Heights[_height.SelectedIndex - 1] : null,
        _hevc.IsChecked == true,
        _useTarget.IsChecked == true,
        (long)((double.TryParse(_target.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var mb) ? Math.Max(mb, 0.1) : 25) * 1_000_000));

    private void Changed()
    {
        if (_hevc is null || _media.FirstOrDefault() is not { } first)
        {
            _settled = true;
            return;
        }
        var settings = Settings();
        _estimate.Text = L("Estimating…");
        _scheduler.Request(token =>
        {
            var size = MediaTools.EstimateCompressedSize(first, settings, token);
            var original = new FileInfo(first).Length;
            var change = original > 0 ? (int)Math.Round(((double)size / original - 1) * 100) : 0;
            return $"{Path.GetFileName(first)}: {ImageIO.Size(original)} → ≈{ImageIO.Size(size)} ({(change > 0 ? "+" : "−")}{Math.Abs(change)}%)";
        }, 400);
    }

    protected override void Run()
    {
        var settings = Settings();
        Close();
        _jobs.RunEach(L("Compressing"), L("Compressed"), _media, (path, token, progress) => MediaTools.Compress(path, settings, progress, token));
    }
}

/// <summary>Join: videos one after another into one MP4, in the order shown.</summary>
internal sealed class JoinTool : ToolWindow
{
    private readonly ListBox _list = new() { Width = 520, Height = 260 };
    private readonly Jobs _jobs;

    public JoinTool(IReadOnlyList<string> files, Jobs jobs) : base(L("Join"), files, L("Join"))
    {
        _jobs = jobs;
        foreach (var file in files.Where(f => FileFormats.Detect(f)?.Category() == FormatCategory.Video))
        {
            _list.Items.Add(file);
        }
        _list.DisplayMemberPath = null;
        _list.ItemTemplate = null;
        _list.SelectedIndex = 0;
        var up = new Button { Content = L("Move up"), Margin = new Thickness(0, 0, 0, 8), MinWidth = 110 };
        var down = new Button { Content = L("Move down"), MinWidth = 110 };
        up.Click += (_, _) => Shift(-1);
        down.Click += (_, _) => Shift(1);
        var buttons = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
        buttons.Children.Add(up);
        buttons.Children.Add(down);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_list);
        row.Children.Add(buttons);
        var stack = new StackPanel();
        stack.Children.Add(row);
        stack.Children.Add(Note(L("Videos of different sizes are fitted into the first one's frame. The result is Joined.mp4.")));
        Body = stack;
        StatusText = WheelCatalog.FileCount(_list.Items.Count);
        ActionEnabled = _list.Items.Count >= 2;
    }

    private void Shift(int direction)
    {
        var index = _list.SelectedIndex;
        var target = index + direction;
        if (index < 0 || target < 0 || target >= _list.Items.Count)
        {
            return;
        }
        var item = _list.Items[index];
        _list.Items.RemoveAt(index);
        _list.Items.Insert(target, item);
        _list.SelectedIndex = target;
    }

    protected override void Run()
    {
        var inputs = _list.Items.Cast<string>().ToList();
        Close();
        _jobs.RunOnce(L("Joining"), L("Joined"), WheelCatalog.FileCount(inputs.Count), () => MediaTools.Join(inputs, _ => { }, CancellationToken.None));
    }
}

/// <summary>Bleep: mark moments on the timeline and replace the sound there with a tone; listen before saving.</summary>
internal sealed class BleepTool : MediaToolWindow
{
    private readonly Slider _frequency;
    private readonly Slider _volume;
    private readonly TextBlock _count = new() { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7, Margin = new Thickness(12, 0, 0, 0) };

    public BleepTool(IReadOnlyList<string> files, Jobs jobs) : base(L("Bleep"), files, L("Save"), jobs, selection: true)
    {
        (var frequencyView, _frequency) = LabeledSlider(300, 3000, 1000, 1000, v => $"{Math.Round(v)} Hz");
        (var volumeView, _volume) = LabeledSlider(0.05, 1, 0.3, 0.3, v => $"{Math.Round(v * 100)}%");
        var add = new Button { Content = L("Bleep the selection"), Margin = new Thickness(0, 0, 8, 0) };
        add.Click += (_, _) => Change(() => Timeline.Ranges.Add((Timeline.SelectionStart, Timeline.SelectionEnd)));
        var undo = new Button { Content = L("Remove the last"), Margin = new Thickness(0, 0, 8, 0) };
        undo.Click += (_, _) => Change(() =>
        {
            if (Timeline.Ranges.Count > 0)
            {
                Timeline.Ranges.RemoveAt(Timeline.Ranges.Count - 1);
            }
        });
        var listen = new Button { Content = L("Listen") };
        listen.Click += (_, _) => Preview(listen);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 8) };
        row.Children.Add(add);
        row.Children.Add(undo);
        row.Children.Add(listen);
        row.Children.Add(_count);
        var settings = new StackPanel { Width = 480 };
        settings.Children.Add(Row(L("Tone"), frequencyView));
        settings.Children.Add(Row(L("Tone volume"), volumeView));
        Body = Layout(row, settings, Note(L("Select a word with the white handles, then bleep it. Listen plays the selection with a second around it.")));
        Change(() => { });
    }

    protected override void MediaLoaded(MediaInfo info) => Timeline.SetSelection(0, Math.Min(info.Duration ?? 0, 1));

    private void Change(Action action)
    {
        action();
        Timeline.Redraw();
        _count.Text = string.Format(L("{0} bleeps"), Timeline.Ranges.Count);
    }

    private void Preview(Button button)
    {
        if (Input is not { } input)
        {
            return;
        }
        var start = Math.Max(0, Timeline.SelectionStart - 1);
        var end = Math.Min(Timeline.Duration, Timeline.SelectionEnd + 1);
        // The preview starts at "start", so the bleep times are shifted to match.
        var ranges = Timeline.Ranges.Select(r => (r.Start - start, r.End - start)).Where(r => r.Item2 > 0).ToList();
        var graph = ranges.Count > 0 ? MediaMath.BleepGraph(ranges, _frequency.Value, _volume.Value, end - start) : null;
        button.IsEnabled = false;
        Task.Run(() => MediaTools.RenderPreview(input, null, graph, start, end - start, CancellationToken.None)).ContinueWith(task =>
        {
            button.IsEnabled = true;
            if (task.Exception is null)
            {
                MediaToolWindowAudio.Play(task.Result);
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    protected override void Run()
    {
        if (Input is not { } input || Timeline.Ranges.Count == 0)
        {
            return;
        }
        var ranges = Timeline.Ranges.ToList();
        var (frequency, volume, duration) = (_frequency.Value, _volume.Value, Timeline.Duration);
        Close();
        Jobs.RunEach(L("Bleeping"), L("Bleeped"), [input], (path, token, progress) =>
            MediaTools.ProcessAudio(path, L("bleeped"), null, MediaMath.BleepGraph(ranges, frequency, volume, duration), progress, token));
    }
}

/// <summary>Visualizer: turns a song into an MP4 with a waveform, a spectrum or a still picture.</summary>
internal sealed class VisualizerTool : MediaToolWindow
{
    private readonly RadioButton[] _styles;
    private readonly RadioButton[] _shapes;
    private readonly Swatches _wave = new(0xFFFF9E33);
    private readonly Swatches _background = new(0xFF141419);
    private readonly TextBlock _imageName = new() { Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    private readonly Image _picture = new() { Stretch = Stretch.Uniform };
    private readonly PreviewScheduler<BitmapSource> _scheduler;
    private string? _image;

    public VisualizerTool(IReadOnlyList<string> files, Jobs jobs) : base(L("Visualizer"), files, L("Create video"), jobs, selection: false)
    {
        _scheduler = new PreviewScheduler<BitmapSource>(Dispatcher, image => _picture.Source = image);
        _styles = [Radio(L("Waveform"), "style", true), Radio(L("Spectrum"), "style"), Radio(L("Picture"), "style")];
        _shapes = [Radio(L("Landscape 16:9"), "shape", true), Radio(L("Vertical 9:16"), "shape"), Radio(L("Square"), "shape")];
        var styles = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var radio in _styles)
        {
            styles.Children.Add(radio);
        }
        var shapes = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var radio in _shapes)
        {
            shapes.Children.Add(radio);
        }
        _wave.Changed += _ => Draw();
        _background.Changed += _ => Draw();
        var choose = new Button { Content = L("Choose…"), MinWidth = 90 };
        choose.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = L("Background image"), Filter = $"{L("Images")}|*.jpg;*.jpeg;*.png;*.webp;*.bmp" };
            if (dialog.ShowDialog(this) == true)
            {
                _image = dialog.FileName;
                _imageName.Text = Path.GetFileName(_image);
                Draw();
            }
        };
        var imageRow = new StackPanel { Orientation = Orientation.Horizontal };
        imageRow.Children.Add(choose);
        imageRow.Children.Add(_imageName);
        _imageName.Text = L("No image chosen");

        var settings = new StackPanel { Width = 520, Margin = new Thickness(0, 12, 0, 0) };
        settings.Children.Add(Row(L("Style"), styles));
        settings.Children.Add(Row(L("Shape"), shapes));
        settings.Children.Add(Row(L("Colour"), _wave));
        settings.Children.Add(Row(L("Background"), _background));
        settings.Children.Add(Row(L("Image"), imageRow));
        var frame = new Border
        {
            Width = 420, Height = 240, CornerRadius = new CornerRadius(10), Child = _picture, Margin = new Thickness(0, 0, 0, 10),
            Background = new SolidColorBrush(Color.FromArgb(0x18, 0x80, 0x80, 0x80)), HorizontalAlignment = HorizontalAlignment.Left,
        };
        var stack = new StackPanel();
        stack.Children.Add(frame);
        stack.Children.Add(Timeline);
        stack.Children.Add(settings);
        Body = stack;
    }

    private RadioButton Radio(string text, string group, bool isChecked = false)
    {
        var radio = new RadioButton { Content = text, GroupName = group, IsChecked = isChecked, Margin = new Thickness(0, 0, 14, 0) };
        radio.Checked += (_, _) => Draw();
        return radio;
    }

    private (MediaMath.VisualizerStyle, MediaMath.VisualizerShape) Choice() =>
        ((MediaMath.VisualizerStyle)Math.Max(0, Array.FindIndex(_styles, r => r.IsChecked == true)),
         (MediaMath.VisualizerShape)Math.Max(0, Array.FindIndex(_shapes, r => r.IsChecked == true)));

    protected override void MediaLoaded(MediaInfo info)
    {
        Timeline.SetPlayhead((info.Duration ?? 0) / 2);
    }

    protected override void PlayheadMoved(double time) => Draw();

    private void Draw()
    {
        if (Input is not { } input || Info?.Duration is not { } duration || _shapes is null)
        {
            return;
        }
        var (style, shape) = Choice();
        var (wave, background, image, at) = (_wave.Value, _background.Value, _image, Timeline.Playhead);
        _scheduler.Request(token => MediaTools.VisualizerFrame(input, style, shape, wave, background, image, duration, Math.Max(at, 0.8), token), 250);
    }

    protected override void Run()
    {
        if (Input is not { } input)
        {
            return;
        }
        var (style, shape) = Choice();
        var (wave, background, image) = (_wave.Value, _background.Value, _image);
        Close();
        Jobs.RunEach(L("Making a video"), L("Video created"), [input], (path, token, progress) =>
            MediaTools.Visualize(path, style, shape, wave, background, image, progress, token));
    }
}

/// <summary>Hide on video: draw areas on a frame; each covers its time range (the selection when it was drawn).</summary>
internal sealed class VideoHideTool : MediaToolWindow
{
    private readonly Canvas _overlay = new() { Background = Brushes.Transparent, Cursor = Cursors.Cross };
    private readonly Grid _surface = new();
    private readonly Image _shown = new() { Stretch = Stretch.Fill };
    private readonly List<MediaMath.VideoRegion> _regions = [];
    private readonly RadioButton[] _styles;
    private readonly Swatches _color = new(0xFF000000);
    private readonly PreviewScheduler<BitmapSource> _scheduler;
    private int _frameWidth = 1, _frameHeight = 1;
    private Point? _start;
    private Rectangle? _drawing;

    public VideoHideTool(IReadOnlyList<string> files, Jobs jobs) : base(L("Hide on video"), files, L("Save"), jobs, selection: true, needsVideo: true)
    {
        _scheduler = new PreviewScheduler<BitmapSource>(Dispatcher, image => _shown.Source = image);
        _surface.Children.Add(_shown);
        _surface.Children.Add(_overlay);
        FramePane.Child = new Viewbox { Child = _surface, Stretch = Stretch.Uniform, Margin = new Thickness(6) };
        _overlay.MouseLeftButtonDown += (_, e) =>
        {
            _start = e.GetPosition(_overlay);
            _drawing = new Rectangle { Stroke = Brushes.White, StrokeThickness = Math.Max(1, _frameWidth / 300.0), StrokeDashArray = [4, 3] };
            _overlay.Children.Add(_drawing);
            _overlay.CaptureMouse();
        };
        _overlay.MouseMove += (_, e) =>
        {
            if (_start is not { } start || _drawing is null)
            {
                return;
            }
            var rect = new Rect(start, e.GetPosition(_overlay));
            Canvas.SetLeft(_drawing, rect.X);
            Canvas.SetTop(_drawing, rect.Y);
            _drawing.Width = rect.Width;
            _drawing.Height = rect.Height;
        };
        _overlay.MouseLeftButtonUp += (_, e) =>
        {
            _overlay.ReleaseMouseCapture();
            if (_start is { } start)
            {
                var rect = Rect.Intersect(new Rect(start, e.GetPosition(_overlay)), new Rect(0, 0, _frameWidth, _frameHeight));
                if (!rect.IsEmpty && rect.Width > 4 && rect.Height > 4)
                {
                    _regions.Add(new MediaMath.VideoRegion(new UnitRect(rect.X / _frameWidth, rect.Y / _frameHeight, rect.Width / _frameWidth, rect.Height / _frameHeight),
                        Timeline.SelectionStart, Timeline.SelectionEnd));
                }
            }
            _start = null;
            Redraw();
        };
        _styles = [Radio(L("Fill"), true), Radio(L("Blur")), Radio(L("Pixels"))];
        var styles = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var radio in _styles)
        {
            styles.Children.Add(radio);
        }
        _color.Changed += _ => Redraw();
        var clear = new Button { Content = L("Clear"), HorizontalAlignment = HorizontalAlignment.Left };
        clear.Click += (_, _) =>
        {
            _regions.Clear();
            Redraw();
        };
        var settings = new StackPanel { Width = 520, Margin = new Thickness(0, 12, 0, 0) };
        settings.Children.Add(Row(L("Style"), styles));
        settings.Children.Add(Row(L("Colour"), _color));
        settings.Children.Add(clear);
        Body = Layout(settings, Note(L("Choose the time with the white handles, then draw an area on the frame. Each area is hidden only during its time.")));
    }

    private RadioButton Radio(string text, bool isChecked = false)
    {
        var radio = new RadioButton { Content = text, GroupName = "videostyle", IsChecked = isChecked, Margin = new Thickness(0, 0, 14, 0) };
        radio.Checked += (_, _) => Redraw();
        return radio;
    }

    private RedactionStyle ChosenStyle => (RedactionStyle)Math.Max(0, Array.FindIndex(_styles, r => r.IsChecked == true));

    protected override void MediaLoaded(MediaInfo info)
    {
        _frameWidth = info.Width ?? 640;
        _frameHeight = info.Height ?? 360;
        _surface.Width = _overlay.Width = _frameWidth;
        _surface.Height = _overlay.Height = _frameHeight;
        _shown.Source = FrameImage.Source;
        Redraw();
    }

    protected override void PlayheadMoved(double time) => Redraw();

    private void Redraw()
    {
        _overlay.Children.Clear();
        if (Input is not { } input || _styles is null)
        {
            return;
        }
        foreach (var region in _regions)
        {
            var outline = new Rectangle
            {
                Width = region.Rect.Width * _frameWidth, Height = region.Rect.Height * _frameHeight,
                Stroke = new SolidColorBrush(Color.FromRgb(0xC0, 0x5A, 0x80)), StrokeThickness = Math.Max(1.5, _frameWidth / 250.0),
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(outline, region.Rect.X * _frameWidth);
            Canvas.SetTop(outline, region.Rect.Y * _frameHeight);
            _overlay.Children.Add(outline);
        }
        var regions = _regions.ToList();
        var (style, color, at, width, height) = (ChosenStyle, _color.Value, Timeline.Playhead, _frameWidth, _frameHeight);
        _scheduler.Request(token => regions.Count == 0
            ? MediaTools.Frame(input, at, null, token)
            : MediaTools.RedactedFrame(input, regions, style, color, width, height, at, token), 150);
    }

    protected override void Run()
    {
        if (Input is not { } input || _regions.Count == 0)
        {
            return;
        }
        var (regions, style, color) = (_regions.ToList(), ChosenStyle, _color.Value);
        Close();
        Jobs.RunEach(L("Hiding"), L("Hidden"), [input], (path, token, progress) => MediaTools.RedactVideo(path, regions, style, color, progress, token));
    }
}
