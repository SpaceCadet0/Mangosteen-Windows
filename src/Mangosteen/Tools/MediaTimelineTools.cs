using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Mangosteen.Core;
using Mangosteen.Core.Media;
using Mangosteen.Media;

namespace Mangosteen.Tools;

/// <summary>Trim: keep the selected part. Fast mode copies the streams (cuts land on key frames).</summary>
internal sealed class TrimTool : MediaToolWindow
{
    private readonly TextBox _start = TimeBox();
    private readonly TextBox _end = TimeBox();
    private readonly CheckBox _fast = new() { Content = L("Fast, without re-encoding (cuts at the nearest key frame)"), Margin = new Thickness(0, 8, 0, 0) };

    public TrimTool(IReadOnlyList<string> files, Jobs jobs) : base(L("Trim"), files, L("Trim"), jobs, selection: true)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        row.Children.Add(new TextBlock { Text = L("From"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        row.Children.Add(_start);
        row.Children.Add(new TextBlock { Text = L("to"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 8, 0) });
        row.Children.Add(_end);
        Timeline.SelectionChanged += () =>
        {
            _start.Text = MediaMath.Timecode(Timeline.SelectionStart);
            _end.Text = MediaMath.Timecode(Timeline.SelectionEnd);
        };
        _start.LostFocus += (_, _) => TypedTimes();
        _end.LostFocus += (_, _) => TypedTimes();
        Body = Layout(row, _fast, Note(L("Drag the yellow handles to choose the part to keep.")));
    }

    protected override void MediaLoaded(MediaInfo info) => Timeline.SetSelection(0, info.Duration ?? 0);

    private void TypedTimes()
    {
        if (MediaMath.ParseTimecode(_start.Text) is { } start && MediaMath.ParseTimecode(_end.Text) is { } end && end > start)
        {
            Timeline.SetSelection(start, end);
        }
    }

    protected override void Run()
    {
        if (Input is not { } input)
        {
            return;
        }
        var (start, end, fast) = (Timeline.SelectionStart, Timeline.SelectionEnd, _fast.IsChecked == true);
        Close();
        Jobs.RunEach(L("Trimming"), L("Trimmed"), [input], (path, token, progress) => MediaTools.Trim(path, start, end, fast, progress, token));
    }
}

/// <summary>Split: cut a file into parts at chosen points or into equal parts; the parts go into a folder.</summary>
internal sealed class SplitTool : MediaToolWindow
{
    private readonly TextBox _parts = new() { Text = "2", Width = 50 };
    private readonly CheckBox _fast = new() { Content = L("Fast, without re-encoding (cuts at the nearest key frame)"), Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock _count = new() { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7, Margin = new Thickness(12, 0, 0, 0) };

    public SplitTool(IReadOnlyList<string> files, Jobs jobs) : base(L("Split"), files, L("Split"), jobs, selection: false)
    {
        var add = new Button { Content = L("Cut at playhead"), Margin = new Thickness(0, 0, 8, 0) };
        add.Click += (_, _) => Change(() => Timeline.Markers.Add(Timeline.Playhead));
        var equal = new Button { Content = L("Equal parts"), Margin = new Thickness(8, 0, 8, 0) };
        equal.Click += (_, _) => Change(() =>
        {
            Timeline.Markers.Clear();
            if (int.TryParse(_parts.Text, out var parts))
            {
                Timeline.Markers.AddRange(MediaMath.EqualSplitPoints(Timeline.Duration, Math.Clamp(parts, 2, 100)));
            }
        });
        var clear = new Button { Content = L("Clear") };
        clear.Click += (_, _) => Change(Timeline.Markers.Clear);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        row.Children.Add(add);
        row.Children.Add(equal);
        row.Children.Add(_parts);
        row.Children.Add(new Border { Width = 8 });
        row.Children.Add(clear);
        row.Children.Add(_count);
        Body = Layout(row, _fast, Note(L("Click the timeline to move the playhead, then add a cut.")));
        Change(() => { });
    }

    private void Change(Action action)
    {
        action();
        Timeline.Redraw();
        _count.Text = Core.Loc.Count(Timeline.Markers.Count + 1, "part");
    }

    protected override void Run()
    {
        if (Input is not { } input || Timeline.Markers.Count == 0)
        {
            return;
        }
        var points = Timeline.Markers.ToList();
        var fast = _fast.IsChecked == true;
        Close();
        Jobs.RunEach(L("Splitting"), L("Split"), [input], (path, token, progress) => MediaTools.Split(path, points, fast, progress, token));
    }
}

/// <summary>Frames: save the frames at chosen moments as PNG.</summary>
internal sealed class FramesTool : MediaToolWindow
{
    private readonly TextBlock _count = new() { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7, Margin = new Thickness(12, 0, 0, 0) };

    public FramesTool(IReadOnlyList<string> files, Jobs jobs) : base(L("Frames"), files, L("Save"), jobs, selection: false, needsVideo: true)
    {
        var add = new Button { Content = L("Add this frame"), Margin = new Thickness(0, 0, 8, 0) };
        add.Click += (_, _) => Change(() => Timeline.Markers.Add(Timeline.Playhead));
        var clear = new Button { Content = L("Clear") };
        clear.Click += (_, _) => Change(Timeline.Markers.Clear);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        row.Children.Add(add);
        row.Children.Add(clear);
        row.Children.Add(_count);
        Body = Layout(row, Note(L("Without added frames, the frame under the playhead is saved.")));
        Change(() => { });
    }

    private void Change(Action action)
    {
        action();
        Timeline.Redraw();
        _count.Text = Core.Loc.Count(Timeline.Markers.Count, "frame");
    }

    protected override void Run()
    {
        if (Input is not { } input)
        {
            return;
        }
        var times = Timeline.Markers.Count > 0 ? Timeline.Markers.ToList() : [Timeline.Playhead];
        Close();
        Jobs.RunEach(L("Saving frames"), L("Frames saved"), [input], (path, token) => MediaTools.SaveFrames(path, times, token));
    }
}

/// <summary>Speed: faster or slower, with the pitch kept or changed.</summary>
internal sealed class SpeedTool : ToolWindow
{
    private static readonly double[] Presets = [0.25, 0.5, 0.75, 1.25, 1.5, 2, 3, 4];
    private readonly List<string> _media;
    private readonly Jobs _jobs;
    private readonly Slider _factor;
    private readonly CheckBox _pitch = new() { Content = L("Keep the pitch"), IsChecked = true };

    public SpeedTool(IReadOnlyList<string> files, Jobs jobs) : base(L("Speed"), files, L("Save"))
    {
        _media = files.Where(f => Core.FileFormats.Detect(f)?.Category().IsMedia() == true).ToList();
        _jobs = jobs;
        StatusText = Core.WheelCatalog.FileCount(_media.Count);
        (var view, _factor) = LabeledSlider(0.25, 4, 1.5, 1, MediaMath.SpeedLabel);
        var presets = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        foreach (var preset in Presets)
        {
            var button = new Button { Content = MediaMath.SpeedLabel(preset), Margin = new Thickness(0, 0, 6, 6), MinWidth = 54 };
            button.Click += (_, _) => _factor.Value = preset;
            presets.Children.Add(button);
        }
        var stack = new StackPanel { Width = 460 };
        stack.Children.Add(Row(L("Speed"), view));
        stack.Children.Add(presets);
        stack.Children.Add(_pitch);
        stack.Children.Add(Note(L("Without keeping the pitch, faster sounds higher, like a sped-up record.")));
        Body = stack;
        ActionEnabled = _media.Count > 0;
    }

    protected override void Run()
    {
        var factor = Math.Round(_factor.Value, 2);
        var pitch = _pitch.IsChecked == true;
        Close();
        _jobs.RunEach(L("Changing speed"), L("Speed changed"), _media, (path, token, progress) => MediaTools.ChangeSpeed(path, factor, pitch, progress, token));
    }
}

/// <summary>Volume: loudness normalisation to a standard level (EBU R128, two passes).</summary>
internal sealed class VolumeTool : ToolWindow
{
    private static readonly (string Title, double Integrated)[] Targets =
        [("Streaming and music (−14 LUFS)", -14), ("Podcast (−16 LUFS)", -16), ("TV and radio (−23 LUFS)", -23)];
    private readonly List<string> _media;
    private readonly Jobs _jobs;
    private readonly ComboBox _target = new();
    private readonly TextBlock _measured = new() { Opacity = 0.75, Margin = new Thickness(0, 4, 0, 10), TextWrapping = TextWrapping.Wrap };
    private bool _settled;

    public VolumeTool(IReadOnlyList<string> files, Jobs jobs) : base(L("Volume"), files, L("Save"))
    {
        _media = files.Where(f => Core.FileFormats.Detect(f)?.Category().IsMedia() == true).ToList();
        _jobs = jobs;
        StatusText = Core.WheelCatalog.FileCount(_media.Count);
        foreach (var (title, _) in Targets)
        {
            _target.Items.Add(L(title));
        }
        _target.SelectedIndex = 0;
        var listen = new Button { Content = L("Listen to 10 seconds"), HorizontalAlignment = HorizontalAlignment.Left };
        listen.Click += (_, _) => Preview(listen);
        var stack = new StackPanel { Width = 460 };
        stack.Children.Add(Row(L("Level"), _target));
        stack.Children.Add(_measured);
        stack.Children.Add(listen);
        stack.Children.Add(Note(L("Quiet recordings become louder and loud ones quieter, without clipping.")));
        Body = stack;
        ActionEnabled = _media.Count > 0;
        Measure();
    }

    public override bool IsSettled => _settled;

    private MediaMath.LoudnessTarget Target => new(Targets[Math.Max(0, _target.SelectedIndex)].Integrated);

    private void Measure()
    {
        if (_media.FirstOrDefault() is not { } first)
        {
            _settled = true;
            return;
        }
        _measured.Text = L("Measuring loudness…");
        var target = Target;
        Task.Run(() => MediaTools.MeasureLoudness(first, target, CancellationToken.None)).ContinueWith(task =>
        {
            _measured.Text = task.Exception is { } error
                ? error.InnerException?.Message ?? error.Message
                : string.Format(CultureInfo.InvariantCulture, L("Now: {0:0.0} LUFS, peaks {1:0.0} dBTP."), task.Result.Integrated, task.Result.TruePeak);
            _settled = true;
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void Preview(Button button)
    {
        if (_media.FirstOrDefault() is not { } first)
        {
            return;
        }
        button.IsEnabled = false;
        var target = Target;
        Task.Run(() =>
        {
            var info = MediaConverter.Probe(first);
            var start = Math.Max(0, (info.Duration ?? 0) / 2 - 5);
            var measured = MediaTools.MeasureLoudness(first, target, CancellationToken.None);
            return MediaTools.RenderPreview(first, MediaTools.NormalizeFilter(measured, target, info.SampleRate ?? 48000), null, start, 10, CancellationToken.None);
        }).ContinueWith(task =>
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
        var target = Target;
        Close();
        _jobs.RunEach(L("Adjusting volume"), L("Volume adjusted"), _media, (path, token, progress) => MediaTools.Normalize(path, target, progress, token));
    }
}

/// <summary>Channels: stereo or mono, the level of each side, and swapping left and right.</summary>
internal sealed class ChannelsTool : ToolWindow
{
    private readonly List<string> _media;
    private readonly Jobs _jobs;
    private readonly RadioButton _stereo = new() { Content = L("Stereo"), GroupName = "layout", IsChecked = true, Margin = new Thickness(0, 0, 16, 0) };
    private readonly RadioButton _mono = new() { Content = L("Mono"), GroupName = "layout" };
    private readonly Slider _left;
    private readonly Slider _right;
    private readonly CheckBox _swap = new() { Content = L("Swap left and right") };

    public ChannelsTool(IReadOnlyList<string> files, Jobs jobs) : base(L("Channels"), files, L("Save"))
    {
        _media = files.Where(f => Core.FileFormats.Detect(f)?.Category().IsMedia() == true).ToList();
        _jobs = jobs;
        StatusText = Core.WheelCatalog.FileCount(_media.Count);
        (var leftView, _left) = LabeledSlider(0, 2, 1, 1, v => $"{Math.Round(v * 100)}%");
        (var rightView, _right) = LabeledSlider(0, 2, 1, 1, v => $"{Math.Round(v * 100)}%");
        var layout = new StackPanel { Orientation = Orientation.Horizontal };
        layout.Children.Add(_stereo);
        layout.Children.Add(_mono);
        var stack = new StackPanel { Width = 460 };
        stack.Children.Add(Row(L("Output"), layout));
        stack.Children.Add(Row(L("Left"), leftView));
        stack.Children.Add(Row(L("Right"), rightView));
        stack.Children.Add(_swap);
        Body = stack;
        ActionEnabled = _media.Count > 0;
    }

    protected override void Run()
    {
        var layout = _mono.IsChecked == true ? MediaMath.ChannelLayout.Mono : MediaMath.ChannelLayout.Stereo;
        var (left, right, swap) = (_left.Value, _right.Value, _swap.IsChecked == true);
        Close();
        _jobs.RunEach(L("Changing channels"), L("Channels changed"), _media, (path, token, progress) =>
        {
            var channels = MediaConverter.Probe(path, token).AudioChannels ?? 2;
            return MediaTools.ProcessAudio(path, L("channels"), MediaMath.ChannelFilter(layout, left, right, swap, channels), null, progress, token);
        });
    }
}

/// <summary>Plays preview sounds rendered to WAV; WPF's MediaPlayer always plays WAV.</summary>
internal static class MediaToolWindowAudio
{
    private static System.Windows.Media.MediaPlayer? _player;

    public static void Play(string wav)
    {
        _player?.Close();
        var player = new System.Windows.Media.MediaPlayer();
        _player = player;
        player.Open(new Uri(wav));
        player.MediaEnded += (_, _) => player.Close();
        player.Play();
    }
}
