using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mangosteen.Core;
using Mangosteen.Platform;
using static Mangosteen.Platform.Native;

namespace Mangosteen.Tools;

/// <summary>
/// A tool window: content with a live preview, and a footer with the file count, Cancel and the main action.
/// It uses the Windows 11 (Fluent) look and follows the system light or dark theme. Esc cancels, Enter runs.
/// </summary>
internal abstract class ToolWindow : Window
{
    private readonly Button _action;
    private readonly Button _cancel;
    private readonly TextBlock _status;
    private readonly ContentControl _body = new();

    protected ToolWindow(string title, IReadOnlyList<string> files, string actionTitle)
    {
#pragma warning disable WPF0001 // Fluent theme is marked experimental in .NET 10.
        ThemeMode = ThemeMode.System;
#pragma warning restore WPF0001
        Files = files;
        Title = files.Count switch
        {
            0 => title,
            1 => $"{title} — {Path.GetFileName(files[0])}",
            _ => $"{title} — {Path.GetFileName(files[0])} " + string.Format(L("and {0} more"), files.Count - 1),
        };
        ShowInTaskbar = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        // The layouts are made for their natural size; stretching them only spreads the controls apart.
        ResizeMode = ResizeMode.CanMinimize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        UseLayoutRounding = true;
        try
        {
            Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/Mangosteen.ico"));
        }
        catch (Exception error) when (error is IOException or UriFormatException or NotSupportedException)
        {
        }

        _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 };
        _action = new Button { Content = actionTitle, IsDefault = true, MinWidth = 96, Margin = new Thickness(8, 0, 0, 0) };
        _action.SetResourceReference(StyleProperty, "AccentButtonStyle");
        _action.Click += (_, _) => Run();
        var cancel = new Button { Content = L("Cancel"), IsCancel = true, MinWidth = 96 };
        cancel.Click += (_, _) => Close();
        _cancel = cancel;

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(_action);
        var footer = new DockPanel { Margin = new Thickness(0, 14, 0, 0), LastChildFill = true };
        DockPanel.SetDock(buttons, Dock.Right);
        footer.Children.Add(buttons);
        footer.Children.Add(_status);
        _status.Text = WheelCatalog.FileCount(files.Count);

        var root = new DockPanel { Margin = new Thickness(20) };
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(_body);
        Content = root;

        Loaded += (_, _) => FitOnScreen();
    }

    protected IReadOnlyList<string> Files { get; }

    /// <summary>The tool's own controls, above the footer.</summary>
    protected UIElement Body
    {
        set
        {
            RelaxMinimumWidths(value);
            _body.Content = value;
        }
    }

    /// <summary>
    /// The Fluent theme gives radio buttons, check boxes, sliders and combo boxes a large minimum width; in the
    /// compact tool windows they should take only the space they need.
    /// </summary>
    private static void RelaxMinimumWidths(object node)
    {
        if (node is RadioButton or CheckBox or Slider or ComboBox && node is FrameworkElement element
            && element.ReadLocalValue(MinWidthProperty) == DependencyProperty.UnsetValue)
        {
            element.MinWidth = 0;
        }
        if (node is DependencyObject parent)
        {
            foreach (var child in LogicalTreeHelper.GetChildren(parent))
            {
                RelaxMinimumWidths(child);
            }
        }
    }

    /// <summary>For windows with a single button (the welcome window); Esc still closes them.</summary>
    protected void HideCancel()
    {
        _cancel.Visibility = Visibility.Collapsed;
        _action.IsCancel = true;
    }

    protected bool ActionEnabled
    {
        set => _action.IsEnabled = value;
    }

    /// <summary>Replaces the file count in the footer, e.g. with a size estimate.</summary>
    protected string StatusText
    {
        set => _status.Text = value;
    }

    /// <summary>Called by the main button and by Enter.</summary>
    protected abstract void Run();

    /// <summary>For the snapshot mode: true once the preview has been drawn.</summary>
    public virtual bool IsSettled => true;

    /// <summary>Shows the window in front of everything, on the monitor under the cursor.</summary>
    public void Present()
    {
        Show();
        Activate();
        var hwnd = new WindowInteropHelper(this).Handle;
        if (GetForegroundWindow() != hwnd)
        {
            // The wheel never takes focus, so Windows may refuse to bring a new window forward.
            Topmost = true;
            Topmost = false;
            Activate();
        }
        Log.Write($"tool window \"{Title}\" shown, in front: {GetForegroundWindow() == hwnd}");
    }

    /// <summary>Keeps the window inside the work area of the monitor under the cursor, centred there.</summary>
    private void FitOnScreen()
    {
        GetCursorPos(out var cursor);
        var work = WorkArea(cursor);
        var source = PresentationSource.FromVisual(this);
        var toDip = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var topLeft = toDip.Transform(new Point(work.Left, work.Top));
        var bottomRight = toDip.Transform(new Point(work.Right, work.Bottom));
        var area = new Rect(topLeft, bottomRight);
        var width = Math.Min(ActualWidth, area.Width - 40);
        var height = Math.Min(ActualHeight, area.Height - 40);
        SizeToContent = SizeToContent.Manual;
        Width = width;
        Height = height;
        Left = area.Left + (area.Width - width) / 2;
        Top = area.Top + (area.Height - height) / 2;
    }

    /// <summary>A labelled row for the settings column.</summary>
    protected static FrameworkElement Row(string label, UIElement control)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        grid.Children.Add(text);
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return grid;
    }

    protected static TextBlock Note(string text) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 12, Margin = new Thickness(0, 0, 0, 10),
    };

    /// <summary>A slider with its value shown on the right; double-click resets it to <paramref name="reset"/>.</summary>
    protected static (FrameworkElement View, Slider Slider) LabeledSlider(double min, double max, double value, double reset,
        Func<double, string> format)
    {
        var slider = new Slider { Minimum = min, Maximum = max, Value = value, VerticalAlignment = VerticalAlignment.Center };
        var readout = new TextBlock { Width = 48, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        readout.Text = format(value);
        slider.ValueChanged += (_, e) => readout.Text = format(e.NewValue);
        slider.MouseDoubleClick += (_, _) => slider.Value = reset;
        var dock = new DockPanel();
        DockPanel.SetDock(readout, Dock.Right);
        dock.Children.Add(readout);
        dock.Children.Add(slider);
        return (dock, slider);
    }

    /// <summary>A rounded panel that holds a preview image, scaled to fit.</summary>
    protected static (Border Frame, Image Image) PreviewPane(double width, double height)
    {
        var image = new Image { Stretch = Stretch.Uniform, Margin = new Thickness(6) };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        var frame = new Border
        {
            Width = width, Height = height, CornerRadius = new CornerRadius(10), Child = image, VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(Color.FromArgb(0x18, 0x80, 0x80, 0x80)),
        };
        return (frame, image);
    }
}
