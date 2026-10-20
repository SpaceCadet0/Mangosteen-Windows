using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mangosteen.Core;
using Mangosteen.Media;

namespace Mangosteen.Tools;

/// <summary>
/// A video or audio tool with a timeline: loads the file's details, a strip of frames or the waveform, and shows the
/// frame under the playhead (video).
/// </summary>
internal abstract class MediaToolWindow : ToolWindow
{
    protected const int TimelineWidth = 760;
    private readonly PreviewScheduler<BitmapSource> _frames;
    private bool _settled;

    protected MediaToolWindow(string title, IReadOnlyList<string> files, string action, Jobs jobs, bool selection, bool needsVideo = false)
        : base(title, files, action)
    {
        Jobs = jobs;
        Media = files.Where(f => FileFormats.Detect(f)?.Category().IsMedia() == true).ToList();
        Input = Media.FirstOrDefault();
        IsVideo = Input is not null && FileFormats.Detect(Input)?.Category() == FormatCategory.Video;
        Timeline = new Timeline { ShowSelection = selection, Width = TimelineWidth, Margin = new Thickness(0, 10, 0, 0) };
        (FramePane, FrameImage) = PreviewPane(TimelineWidth, 330);
        _frames = new PreviewScheduler<BitmapSource>(Dispatcher, image => FrameImage.Source = image);
        Timeline.PlayheadChanged += time =>
        {
            if (IsVideo && Info?.HasVideo == true && Input is { } input)
            {
                _frames.Request(token => MediaTools.Frame(input, time, 480, token), 120);
            }
            PlayheadMoved(time);
        };
        ActionEnabled = false;
        if (needsVideo && !IsVideo)
        {
            Input = null;
        }
        Load();
    }

    protected Jobs Jobs { get; }
    protected List<string> Media { get; }
    protected string? Input { get; set; }
    protected bool IsVideo { get; }
    protected MediaInfo? Info { get; private set; }
    protected Timeline Timeline { get; }
    protected Border FramePane { get; }
    protected Image FrameImage { get; }

    public override bool IsSettled => _settled;

    /// <summary>The standard layout: the frame (for video), the timeline, then the tool's own controls.</summary>
    protected FrameworkElement Layout(params UIElement[] controls)
    {
        var stack = new StackPanel();
        if (IsVideo)
        {
            stack.Children.Add(FramePane);
        }
        stack.Children.Add(Timeline);
        foreach (var control in controls)
        {
            stack.Children.Add(control);
        }
        return stack;
    }

    protected virtual void PlayheadMoved(double time)
    {
    }

    protected virtual void MediaLoaded(MediaInfo info)
    {
    }

    private void Load()
    {
        if (Input is not { } input)
        {
            _settled = true;
            return;
        }
        Task.Run(() =>
        {
            var info = MediaConverter.Probe(input);
            var duration = info.Duration ?? 0;
            var frames = info.HasVideo ? MediaTools.Thumbnails(input, duration, 10, 72, CancellationToken.None) : [];
            var peaks = !info.HasVideo && info.HasAudio ? MediaTools.Waveform(input, duration, 400, CancellationToken.None) : [];
            var first = info.HasVideo ? MediaTools.Frame(input, Math.Min(duration / 2, 1), 480) : null;
            return (info, duration, frames, peaks, first);
        }).ContinueWith(task =>
        {
            if (task.Exception is { } error)
            {
                Platform.Log.Error("media tool", error);
                StatusText = error.InnerException?.Message ?? error.Message;
            }
            else
            {
                var (info, duration, frames, peaks, first) = task.Result;
                Info = info;
                Timeline.Load(duration, frames, peaks);
                if (first is not null)
                {
                    FrameImage.Source = first;
                }
                ActionEnabled = true;
                MediaLoaded(info);
            }
            _settled = true;
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>Plays a rendered preview sound.</summary>
    protected static void Listen(string wav)
    {
        var player = new MediaPlayer();
        player.Open(new Uri(wav));
        player.MediaEnded += (_, _) =>
        {
            player.Close();
            try
            {
                File.Delete(wav);
            }
            catch (IOException)
            {
            }
        };
        player.Play();
    }

    protected static TextBox TimeBox() => new() { Width = 110 };
}
