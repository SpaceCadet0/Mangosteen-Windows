using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Mangosteen.Platform;
using Mangosteen.Wheel;
using static Mangosteen.Platform.Native;

namespace Mangosteen;

/// <summary>
/// A small dark panel in the bottom-right corner that shows a running job: what it does, which file, how far,
/// and a Stop button. It never takes focus away from the app you are in.
/// </summary>
internal sealed class ProgressHud : Window
{
    private static readonly List<ProgressHud> Open = [];
    private readonly TextBlock _detail;
    private readonly ProgressBar _bar;
    private readonly Button _stop;

    public event Action? StopRequested;

    public ProgressHud(string title)
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Width = 320;
        SizeToContent = SizeToContent.Height;
        Title = "Mangosteen";

        var text = new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xEA));
        var secondary = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xA0));
        _detail = new TextBlock { Foreground = secondary, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 4, 0, 8) };
        _bar = new ProgressBar
        {
            Height = 6, Minimum = 0, Maximum = 1, Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x5A, 0x80)),
            Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3D)), BorderThickness = new Thickness(0), IsIndeterminate = true,
        };
        _stop = new Button
        {
            Content = "Stop", Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            Foreground = text, Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3D)), BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        _stop.Click += (_, _) =>
        {
            _stop.IsEnabled = false;
            _stop.Content = "Stopping…";
            StopRequested?.Invoke();
        };

        var header = new DockPanel();
        DockPanel.SetDock(_stop, Dock.Right);
        header.Children.Add(_stop);
        header.Children.Add(new TextBlock
        {
            Text = title, Foreground = Brushes.White, FontSize = 14, FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
        });
        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(_detail);
        stack.Children.Add(_bar);
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1E)), BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3D)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(16, 12, 16, 14),
            Margin = new Thickness(10), Child = stack,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Opacity = 0.4, Direction = 270 },
        };
        Loaded += (_, _) => Arrange();
        Closed += (_, _) =>
        {
            Open.Remove(this);
            foreach (var hud in Open)
            {
                hud.Arrange();
            }
        };
        Open.Add(this);
    }

    /// <param name="fraction">Progress within the current file (0…1), or null when unknown.</param>
    public void Report(int done, int total, string item, double? fraction = null)
    {
        _bar.IsIndeterminate = total <= 1 && fraction is null;
        _bar.Value = total == 0 ? 0 : (done + (fraction ?? 0)) / total;
        _detail.Text = total > 1 ? $"{Math.Min(done + 1, total)} of {total} · {item}" : item;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    /// <summary>Stacks open panels upwards from the bottom-right corner of the work area.</summary>
    private void Arrange()
    {
        var area = SystemParameters.WorkArea;
        var offset = 0.0;
        foreach (var hud in Open)
        {
            if (ReferenceEquals(hud, this))
            {
                break;
            }
            offset += hud.ActualHeight;
        }
        Left = area.Right - ActualWidth;
        Top = area.Bottom - ActualHeight - offset;
    }
}
