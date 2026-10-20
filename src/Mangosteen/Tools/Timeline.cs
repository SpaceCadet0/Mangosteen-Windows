using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Mangosteen.Core.Media;
using Path = System.Windows.Shapes.Path;

namespace Mangosteen.Tools;

/// <summary>
/// A media timeline: frames of a video or the waveform of a sound, a playhead, an optional selection with handles,
/// markers (cuts, frames) and ranges (bleeps).
/// </summary>
internal sealed class Timeline : Grid
{
    private const double StripHeight = 72;
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0xC0, 0x5A, 0x80));
    private readonly Canvas _strip = new() { Height = StripHeight, ClipToBounds = true, Background = new SolidColorBrush(Color.FromArgb(0x30, 0x80, 0x80, 0x80)) };
    private readonly Canvas _overlay = new() { Height = StripHeight, Background = Brushes.Transparent, Cursor = Cursors.IBeam };
    private readonly TextBlock _left = new() { FontSize = 11, Opacity = 0.7 };
    private readonly TextBlock _right = new() { FontSize = 11, Opacity = 0.7, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly TextBlock _center = new() { FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, FontWeight = FontWeights.SemiBold };
    private IReadOnlyList<BitmapSource> _frames = [];
    private float[] _peaks = [];
    private enum Drag { None, Playhead, Start, End }
    private Drag _drag;

    public Timeline()
    {
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(StripHeight) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Children.Add(_strip);
        Children.Add(_overlay);
        var labels = new Grid { Margin = new Thickness(0, 3, 0, 0) };
        labels.Children.Add(_left);
        labels.Children.Add(_center);
        labels.Children.Add(_right);
        SetRow(labels, 1);
        Children.Add(labels);
        _overlay.MouseLeftButtonDown += Down;
        _overlay.MouseMove += Move;
        _overlay.MouseLeftButtonUp += (_, _) =>
        {
            _drag = Drag.None;
            _overlay.ReleaseMouseCapture();
        };
        SizeChanged += (_, _) => Redraw();
    }

    public double Duration { get; private set; } = 1;
    public double Playhead { get; private set; }
    public bool ShowSelection { get; init; }
    public double SelectionStart { get; private set; }
    public double SelectionEnd { get; private set; } = 1;
    public List<double> Markers { get; } = [];
    public List<(double Start, double End)> Ranges { get; } = [];

    public event Action<double>? PlayheadChanged;
    public event Action? SelectionChanged;

    public void Load(double duration, IReadOnlyList<BitmapSource> frames, float[] peaks)
    {
        Duration = Math.Max(duration, 0.01);
        SelectionStart = 0;
        SelectionEnd = Duration;
        _frames = frames;
        _peaks = peaks;
        Redraw();
    }

    public void SetPlayhead(double seconds)
    {
        Playhead = Math.Clamp(seconds, 0, Duration);
        Redraw();
        PlayheadChanged?.Invoke(Playhead);
    }

    public void SetSelection(double start, double end)
    {
        SelectionStart = Math.Clamp(Math.Min(start, end), 0, Duration);
        SelectionEnd = Math.Clamp(Math.Max(start, end), 0, Duration);
        Redraw();
        SelectionChanged?.Invoke();
    }

    private double X(double seconds) => seconds / Duration * StripWidth();

    private double StripWidth() => Math.Max(ActualWidth, 1);

    private double Time(double x) => Math.Clamp(x / StripWidth() * Duration, 0, Duration);

    private void Down(object sender, MouseButtonEventArgs e)
    {
        var x = e.GetPosition(_overlay).X;
        const double grab = 8;
        if (ShowSelection && Math.Abs(x - X(SelectionStart)) < grab)
        {
            _drag = Drag.Start;
        }
        else if (ShowSelection && Math.Abs(x - X(SelectionEnd)) < grab)
        {
            _drag = Drag.End;
        }
        else
        {
            _drag = Drag.Playhead;
            SetPlayhead(Time(x));
        }
        _overlay.CaptureMouse();
    }

    private void Move(object sender, MouseEventArgs e)
    {
        var x = e.GetPosition(_overlay).X;
        if (ShowSelection)
        {
            var nearHandle = Math.Abs(x - X(SelectionStart)) < 8 || Math.Abs(x - X(SelectionEnd)) < 8;
            _overlay.Cursor = nearHandle ? Cursors.SizeWE : Cursors.IBeam;
        }
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }
        switch (_drag)
        {
            case Drag.Playhead:
                SetPlayhead(Time(x));
                break;
            case Drag.Start:
                SetSelection(Math.Min(Time(x), SelectionEnd - 0.05), SelectionEnd);
                SetPlayhead(SelectionStart);
                break;
            case Drag.End:
                SetSelection(SelectionStart, Math.Max(Time(x), SelectionStart + 0.05));
                SetPlayhead(SelectionEnd);
                break;
        }
    }

    public void Redraw()
    {
        var width = StripWidth();
        _strip.Children.Clear();
        _overlay.Children.Clear();
        if (_frames.Count > 0)
        {
            var tile = width / _frames.Count;
            for (var i = 0; i < _frames.Count; i++)
            {
                var image = new Image { Source = _frames[i], Width = tile + 1, Height = StripHeight, Stretch = Stretch.UniformToFill };
                Canvas.SetLeft(image, i * tile);
                _strip.Children.Add(image);
            }
        }
        else if (_peaks.Length > 0)
        {
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                var middle = StripHeight / 2;
                for (var i = 0; i < _peaks.Length; i++)
                {
                    var x = (i + 0.5) / _peaks.Length * width;
                    var h = Math.Max(1, _peaks[i] * (StripHeight - 8) / 2);
                    context.BeginFigure(new Point(x, middle - h), false, false);
                    context.LineTo(new Point(x, middle + h), true, false);
                }
            }
            geometry.Freeze();
            _strip.Children.Add(new Path { Data = geometry, Stroke = Accent, StrokeThickness = Math.Max(1, width / Math.Max(_peaks.Length, 1) * 0.7) });
        }

        foreach (var (start, end) in Ranges)
        {
            var rect = new Rectangle { Width = Math.Max(2, X(end) - X(start)), Height = StripHeight, Fill = new SolidColorBrush(Color.FromArgb(0x70, 0xE5, 0x48, 0x4D)), IsHitTestVisible = false };
            Canvas.SetLeft(rect, X(start));
            _overlay.Children.Add(rect);
        }
        if (ShowSelection)
        {
            AddShade(0, X(SelectionStart));
            AddShade(X(SelectionEnd), width);
            foreach (var t in new[] { SelectionStart, SelectionEnd })
            {
                var handle = new Rectangle { Width = 6, Height = StripHeight, Fill = Brushes.White, Stroke = Accent, StrokeThickness = 1.5, RadiusX = 2, RadiusY = 2, IsHitTestVisible = false };
                Canvas.SetLeft(handle, X(t) - 3);
                _overlay.Children.Add(handle);
            }
        }
        foreach (var marker in Markers)
        {
            var line = new Rectangle { Width = 2, Height = StripHeight, Fill = Brushes.Gold, IsHitTestVisible = false };
            Canvas.SetLeft(line, X(marker) - 1);
            _overlay.Children.Add(line);
        }
        var playhead = new Rectangle { Width = 2, Height = StripHeight + 6, Fill = Accent, IsHitTestVisible = false };
        Canvas.SetLeft(playhead, X(Playhead) - 1);
        Canvas.SetTop(playhead, -3);
        _overlay.Children.Add(playhead);

        _left.Text = MediaMath.Timecode(0);
        _right.Text = MediaMath.Timecode(Duration);
        _center.Text = ShowSelection
            ? $"{MediaMath.Timecode(SelectionStart)} – {MediaMath.Timecode(SelectionEnd)}  ({MediaMath.Timecode(SelectionEnd - SelectionStart)})"
            : MediaMath.Timecode(Playhead);
    }

    private void AddShade(double from, double to)
    {
        if (to <= from)
        {
            return;
        }
        var shade = new Rectangle { Width = to - from, Height = StripHeight, Fill = new SolidColorBrush(Color.FromArgb(0xA0, 0, 0, 0)), IsHitTestVisible = false };
        Canvas.SetLeft(shade, from);
        _overlay.Children.Add(shade);
    }
}
