using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Mangosteen.Core;
using Mangosteen.Core.Imaging;
using Path = System.IO.Path;
using Point = System.Windows.Point;

namespace Mangosteen.Tools;

/// <summary>
/// Crop: drag the frame or its handles, pick proportions, or enter an exact output size. Images can also be turned
/// and mirrored, and cut into a grid of parts along lines that can be dragged. With several images the frame drawn on
/// the first one is used as is; the others get a centred crop of the same proportions, and the same turn and grid.
/// </summary>
internal sealed class CropTool : ToolWindow
{
    private const int PreviewSide = 1600;
    private readonly List<string> _images;
    private readonly string? _video;
    private readonly Jobs _jobs;
    private readonly Canvas _overlay = new();
    private readonly Image _image = new() { Stretch = Stretch.Fill };
    private readonly Grid _surface = new();
    private readonly System.Windows.Shapes.Path _shade = new() { Fill = new SolidColorBrush(Color.FromArgb(0x90, 0, 0, 0)), IsHitTestVisible = false };
    private readonly Rectangle _border = new() { Stroke = Brushes.White, StrokeThickness = 2, IsHitTestVisible = false };
    private readonly List<(Rectangle Handle, CropMath.Edges Edges)> _handles = [];
    private readonly Canvas _cutLayer = new();
    private readonly TextBlock _acrossText = new() { VerticalAlignment = VerticalAlignment.Center, MinWidth = 70, TextAlignment = TextAlignment.Center };
    private readonly TextBlock _downText = new() { VerticalAlignment = VerticalAlignment.Center, MinWidth = 70, TextAlignment = TextAlignment.Center };
    private BitmapSource? _preview;
    private int _sourceWidth = 1, _sourceHeight = 1;
    private ImageTransform _transform;
    // Cut lines as fractions of the crop frame, for "Split into parts".
    private List<double> _columnCuts = [];
    private List<double> _rowCuts = [];
    private (bool Vertical, int Index)? _cutDrag;
    // "Size": the whole picture resized by one rule for every file.
    private readonly RadioButton _cropTab = new() { Content = L("Crop|tab"), GroupName = "cropMode", MinWidth = 110 };
    private readonly RadioButton _sizeTab = new() { Content = L("Size"), GroupName = "cropMode", MinWidth = 110 };
    private readonly StackPanel _cropControls = new();
    private readonly StackPanel _sizeControls = new() { Visibility = Visibility.Collapsed };
    private readonly StackPanel _splitPanel = new() { Orientation = Orientation.Horizontal };
    private readonly TextBox _sizeWidth = new() { Width = 80 };
    private readonly TextBox _sizeHeight = new() { Width = 80 };
    private readonly System.Windows.Controls.Primitives.ToggleButton _keepProportions = new() { IsChecked = true, MinWidth = 40, Margin = new Thickness(8, 0, 8, 0) };
    private readonly ComboBox _longSide = new() { MinWidth = 150 };
    private readonly TextBlock _allFiles = new() { Opacity = 0.7, Margin = new Thickness(0, 10, 0, 0) };
    private readonly TextBlock _sizeBadge = new() { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7, Margin = new Thickness(12, 0, 12, 0) };
    private ResizeRule _rule = ResizeRule.Percent(100);
    private bool _showingSize;
    private readonly ComboBox _aspect = new() { MinWidth = 180 };
    private readonly TextBox _exactWidth = new() { Text = "1080", Width = 70 };
    private readonly TextBox _exactHeight = new() { Text = "1080", Width = 70 };
    private readonly StackPanel _exactPanel = new() { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed };
    private readonly TextBlock _pixels = new() { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7, Margin = new Thickness(12, 0, 12, 0) };
    private int _width = 1, _height = 1;
    private UnitRect _rect = UnitRect.Full;
    private (Point Start, UnitRect Rect, CropMath.Edges? Edges)? _drag;
    private bool _loaded;

    public CropTool(IReadOnlyList<string> files, Jobs jobs) : base(IsVideo(files) ? L("Reframe") : L("Crop"), files, IsVideo(files) ? L("Reframe") : L("Save"))
    {
        _images = ToolOutput.Images(files);
        // Crop video: the frame is drawn on a still from the video.
        _video = _images.Count == 0 ? files.FirstOrDefault(f => Core.FileFormats.Category(Core.FileFormats.Detect(f) ?? Core.FileFormat.Txt) == Core.FormatCategory.Video && Core.FileFormats.Detect(f) is not null) : null;
        _jobs = jobs;

        _surface.Children.Add(_image);
        _surface.Children.Add(_overlay);
        _overlay.Children.Add(_shade);
        _overlay.Children.Add(_border);
        _overlay.Children.Add(_cutLayer);
        foreach (var edges in new[] { CropMath.Edges.TopLeft, CropMath.Edges.Top, CropMath.Edges.TopRight, CropMath.Edges.Right,
                     CropMath.Edges.BottomRight, CropMath.Edges.Bottom, CropMath.Edges.BottomLeft, CropMath.Edges.Left })
        {
            var handle = new Rectangle { Fill = Brushes.White, Stroke = new SolidColorBrush(Color.FromRgb(0x67, 0x1E, 0x38)), StrokeThickness = 1 };
            handle.Cursor = edges switch
            {
                CropMath.Edges.TopLeft or CropMath.Edges.BottomRight => Cursors.SizeNWSE,
                CropMath.Edges.TopRight or CropMath.Edges.BottomLeft => Cursors.SizeNESW,
                CropMath.Edges.Left or CropMath.Edges.Right => Cursors.SizeWE,
                _ => Cursors.SizeNS,
            };
            var captured = edges;
            handle.MouseLeftButtonDown += (_, e) => StartDrag(e, captured);
            _handles.Add((handle, edges));
            _overlay.Children.Add(handle);
        }
        _overlay.Background = Brushes.Transparent;
        _overlay.Cursor = Cursors.SizeAll;
        _overlay.MouseLeftButtonDown += (_, e) => StartDrag(e, null);
        _overlay.MouseMove += (_, e) => Drag(e);
        _overlay.MouseLeftButtonUp += (_, _) =>
        {
            _drag = null;
            _cutDrag = null;
            _overlay.ReleaseMouseCapture();
        };

        var canvasFrame = new Border
        {
            Width = 720, Height = 380, CornerRadius = new CornerRadius(10), Padding = new Thickness(8),
            Background = new SolidColorBrush(Color.FromArgb(0x18, 0x80, 0x80, 0x80)),
            Child = new Viewbox { Child = _surface, Stretch = Stretch.Uniform },
        };

        foreach (var choice in CropMath.Aspects)
        {
            _aspect.Items.Add(choice.Original ? L("Same as original") : L(choice.Title));
        }
        _aspect.Items.Add(L("Exact size…"));
        _aspect.SelectedIndex = 0;
        _aspect.SelectionChanged += (_, _) =>
        {
            _exactPanel.Visibility = _aspect.SelectedIndex == CropMath.Aspects.Count ? Visibility.Visible : Visibility.Collapsed;
            ApplyAspect();
        };
        _exactPanel.Children.Add(_exactWidth);
        _exactPanel.Children.Add(new TextBlock { Text = " × ", VerticalAlignment = VerticalAlignment.Center });
        _exactPanel.Children.Add(_exactHeight);
        _exactPanel.Children.Add(new TextBlock { Text = " px", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 });
        _exactWidth.TextChanged += (_, _) => ApplyAspect();
        _exactHeight.TextChanged += (_, _) => ApplyAspect();
        var reset = new Button { Content = L("Reset"), MinWidth = 80 };
        reset.Click += (_, _) =>
        {
            _aspect.SelectedIndex = 0;
            _transform = default;
            _columnCuts = [];
            _rowCuts = [];
            _rule = ResizeRule.Percent(100);
            _keepProportions.IsChecked = true;
            _longSide.SelectedIndex = -1;
            ShowTransformed();
        };

        var controls = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(new TextBlock { Text = L("Proportions"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        left.Children.Add(_aspect);
        left.Children.Add(new Border { Width = 12 });
        left.Children.Add(_exactPanel);
        DockPanel.SetDock(reset, Dock.Right);
        DockPanel.SetDock(_pixels, Dock.Right);
        controls.Children.Add(reset);
        controls.Children.Add(_pixels);
        controls.Children.Add(left);

        var stack = new StackPanel();
        if (_video is null)
        {
            stack.Children.Add(ModeTabs());
        }
        stack.Children.Add(canvasFrame);
        _cropControls.Children.Add(controls);
        stack.Children.Add(_cropControls);
        if (_video is null)
        {
            stack.Children.Add(SizeControls(reset));
            stack.Children.Add(ImageControls());
        }
        if (_images.Count > 1)
        {
            _cropControls.Children.Add(Note(L("The frame is drawn on the first image; the others get a centred crop of the same proportions.")));
        }
        Body = stack;
        ActionEnabled = false;
        Load();
    }

    public override bool IsSettled => _loaded;

    /// <summary>Snapshots: show a 2 × 2 grid of parts (otherwise the screenshot demo shows a square frame).</summary>
    public static bool SnapshotSplit { get; set; }

    private static bool IsVideo(IReadOnlyList<string> files) =>
        ToolOutput.Images(files).Count == 0 && files.Any(f => Core.FileFormats.Detect(f) is { } format && Core.FileFormats.Category(format) == Core.FormatCategory.Video);

    private void Load()
    {
        if (_images.Count == 0 && _video is null)
        {
            _loaded = true;
            return;
        }
        var first = _video ?? _images[0];
        var video = _video is not null;
        Task.Run(() =>
        {
            var full = video ? Media.MediaTools.Frame(first, 1) : ImageIO.Load(first);
            return (Preview: ImageIO.Scale(full, PreviewSide), full.PixelWidth, full.PixelHeight);
        }).ContinueWith(task =>
        {
            if (task.Exception is { } error)
            {
                Platform.Log.Error("crop preview", error);
                return;
            }
            (_preview, _sourceWidth, _sourceHeight) = task.Result;
            if (SnapshotSplit && _video is null)
            {
                // Snapshot check of the grid: two by two.
                (_columnCuts, _rowCuts) = (CropMath.EvenCuts(2), CropMath.EvenCuts(2));
            }
            _loaded = true;
            ActionEnabled = true;
            if (SnapshotMode == "size")
            {
                _rule = ResizeRule.Percent(50);
            }
            if (MediaToolWindow.SnapshotDemo && !SnapshotSplit && _video is null)
            {
                // Screenshots: a square frame, as a person would pick for a profile picture.
                _aspect.SelectedIndex = CropMath.Aspects.ToList().FindIndex(a => a.Ratio == 1);
            }
            ShowTransformed();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>The locked proportions (pixel width / height), or null for a free frame.</summary>
    private double? PixelAspect(int width, int height)
    {
        var index = _aspect.SelectedIndex;
        if (index == CropMath.Aspects.Count)
        {
            return int.TryParse(_exactWidth.Text, out var w) && int.TryParse(_exactHeight.Text, out var h) && w > 0 && h > 0 ? (double)w / h : null;
        }
        var choice = CropMath.Aspects[Math.Max(index, 0)];
        return choice.Original ? (double)width / height : choice.Ratio;
    }

    private double? UnitAspect => PixelAspect(_width, _height) is { } aspect ? aspect * _height / _width : null;

    private void ApplyAspect()
    {
        if (_loaded && PixelAspect(_width, _height) is { } aspect)
        {
            _rect = CropMath.CenteredRect(aspect, _width, _height);
        }
        Redraw();
    }

    private void StartDrag(MouseButtonEventArgs e, CropMath.Edges? edges)
    {
        if (!_loaded)
        {
            return;
        }
        _drag = (e.GetPosition(_overlay), _rect, edges);
        _overlay.CaptureMouse();
        e.Handled = true;
    }

    private void Drag(MouseEventArgs e)
    {
        if (_drag is not { } drag || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }
        var point = e.GetPosition(_overlay);
        if (_cutDrag is { } cut)
        {
            var cuts = cut.Vertical ? _columnCuts : _rowCuts;
            var position = cut.Vertical
                ? (point.X / _overlay.Width - _rect.X) / _rect.Width
                : (point.Y / _overlay.Height - _rect.Y) / _rect.Height;
            cuts[cut.Index] = CropMath.ClampCut(cuts, cut.Index, position);
            Redraw();
            return;
        }
        var dx = (point.X - drag.Start.X) / _overlay.Width;
        var dy = (point.Y - drag.Start.Y) / _overlay.Height;
        if (drag.Edges is { } edges)
        {
            if (CropMath.Resize(drag.Rect, edges, dx, dy, UnitAspect) is { } resized)
            {
                _rect = resized;
            }
        }
        else
        {
            _rect = CropMath.Move(drag.Rect, dx, dy);
        }
        Redraw();
    }

    private void Redraw()
    {
        if (!_loaded)
        {
            return;
        }
        double w = _overlay.Width, h = _overlay.Height;
        var frame = new Rect(_rect.X * w, _rect.Y * h, _rect.Width * w, _rect.Height * h);
        _shade.Data = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(0, 0, w, h)), new RectangleGeometry(frame));
        Canvas.SetLeft(_border, frame.X);
        Canvas.SetTop(_border, frame.Y);
        _border.Width = frame.Width;
        _border.Height = frame.Height;
        _border.StrokeThickness = Math.Max(1, w / 400);
        var size = Math.Max(8, w / 70);
        foreach (var (handle, edges) in _handles)
        {
            var x = edges.HasFlag(CropMath.Edges.Left) ? frame.Left : edges.HasFlag(CropMath.Edges.Right) ? frame.Right : frame.Left + frame.Width / 2;
            var y = edges.HasFlag(CropMath.Edges.Top) ? frame.Top : edges.HasFlag(CropMath.Edges.Bottom) ? frame.Bottom : frame.Top + frame.Height / 2;
            handle.Width = handle.Height = size;
            handle.StrokeThickness = size / 8;
            Canvas.SetLeft(handle, x - size / 2);
            Canvas.SetTop(handle, y - size / 2);
        }
        var pixels = _rect.ToPixels(_width, _height);
        _pixels.Text = $"{pixels.Width} × {pixels.Height} px";
        DrawCuts(frame, w);
        if (_showingSize)
        {
            ShowSize();
        }
    }

    /// <summary>"Crop | Size" at the top; the choice is remembered.</summary>
    private FrameworkElement ModeTabs()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10), HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var tab in new[] { _cropTab, _sizeTab })
        {
            tab.SetResourceReference(StyleProperty, typeof(System.Windows.Controls.Primitives.ToggleButton));
            tab.Margin = new Thickness(0, 0, 4, 0);
            tab.Checked += (_, _) => SetMode(tab == _sizeTab, remember: true);
            row.Children.Add(tab);
        }
        var size = SnapshotMode == "size" || SnapshotMode is null && AppSettings.Load().CropTab == "size";
        (size ? _sizeTab : _cropTab).IsChecked = true;
        SetMode(size, remember: false);
        return row;
    }

    private void SetMode(bool size, bool remember)
    {
        _showingSize = size;
        _overlay.Visibility = size ? Visibility.Collapsed : Visibility.Visible;
        _cropControls.Visibility = size ? Visibility.Collapsed : Visibility.Visible;
        _sizeControls.Visibility = size ? Visibility.Visible : Visibility.Collapsed;
        _splitPanel.Visibility = size ? Visibility.Collapsed : Visibility.Visible;
        if (remember && SnapshotMode is null)
        {
            var settings = AppSettings.Load();
            settings.CropTab = size ? "size" : "crop";
            settings.Save();
        }
        Redraw();
        if (!_loaded)
        {
            ActionTitle = size ? L("Resize") : L("Save");
        }
    }

    /// <summary>Width and height with the proportions lock, 25 / 50 / 75 %, and a long side.</summary>
    private FrameworkElement SizeControls(Button reset)
    {
        var fields = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = L("Width"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        row.Children.Add(_sizeWidth);
        _keepProportions.Content = new TextBlock { Text = "\uE72E", FontFamily = new FontFamily(Wheel.WheelGlyphs.FontFamily), FontSize = 14 };
        _keepProportions.ToolTip = L("Keep proportions");
        row.Children.Add(_keepProportions);
        row.Children.Add(new TextBlock { Text = L("Height"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        row.Children.Add(_sizeHeight);
        row.Children.Add(new TextBlock { Text = " px", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 });
        var sizeReset = new Button { Content = L("Reset"), MinWidth = 80 };
        sizeReset.Click += (_, _) => reset.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        DockPanel.SetDock(sizeReset, Dock.Right);
        DockPanel.SetDock(_sizeBadge, Dock.Right);
        fields.Children.Add(sizeReset);
        fields.Children.Add(_sizeBadge);
        fields.Children.Add(row);

        var presets = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        foreach (var percent in ResizeRule.Percents)
        {
            var button = new Button { Content = $"{percent}%", MinWidth = 56, Margin = new Thickness(0, 0, 6, 0) };
            button.Click += (_, _) => SetRule(ResizeRule.Percent(percent));
            presets.Children.Add(button);
        }
        presets.Children.Add(new TextBlock { Text = L("Longer side"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 0, 8, 0) });
        foreach (var side in ResizeRule.LongSides)
        {
            _longSide.Items.Add($"{side} px");
        }
        _longSide.SelectionChanged += (_, _) =>
        {
            if (_longSide.SelectedIndex >= 0)
            {
                SetRule(ResizeRule.LongSide(ResizeRule.LongSides[_longSide.SelectedIndex]));
            }
        };
        presets.Children.Add(_longSide);

        // Typing only counts once the picture is loaded and the number really changed (a focused field must not
        // turn an empty value into 1 × 1 before the size is known).
        _sizeWidth.TextChanged += (_, _) => Typed(width: true);
        _sizeHeight.TextChanged += (_, _) => Typed(width: false);
        _keepProportions.Click += (_, _) =>
        {
            _keepProportions.Content = new TextBlock
            {
                Text = _keepProportions.IsChecked == true ? "\uE72E" : "\uE785", FontFamily = new FontFamily(Wheel.WheelGlyphs.FontFamily), FontSize = 14,
            };
            var (w, h) = _rule.Apply(_width, _height);
            SetRule(_keepProportions.IsChecked == true ? ResizeRule.Width(w) : ResizeRule.Exact(w, h));
        };

        _sizeControls.Children.Add(fields);
        _sizeControls.Children.Add(presets);
        if (_images.Count > 1)
        {
            _sizeControls.Children.Add(_allFiles);
        }
        return _sizeControls;
    }

    private bool _writingSize;

    private void Typed(bool width)
    {
        if (!_loaded || _writingSize || !int.TryParse((width ? _sizeWidth : _sizeHeight).Text, out var value) || value < 1)
        {
            return;
        }
        var (w, h) = _rule.Apply(_width, _height);
        if (value == (width ? w : h))
        {
            return;
        }
        _longSide.SelectedIndex = -1;
        SetRule(_keepProportions.IsChecked == true
            ? width ? ResizeRule.Width(value) : ResizeRule.HeightOnly(value)
            : width ? ResizeRule.Exact(value, h) : ResizeRule.Exact(w, value));
    }

    private void SetRule(ResizeRule rule)
    {
        _rule = rule;
        if (rule.Kind != ResizeKind.LongSide)
        {
            _longSide.SelectedIndex = -1;
        }
        ShowSize();
    }

    /// <summary>The fields, "4032 × 3024 → 1920 × 1440 px" and, for several files, the rule in words.</summary>
    private void ShowSize()
    {
        if (!_loaded)
        {
            return;
        }
        var (w, h) = _rule.Apply(_width, _height);
        _writingSize = true;
        if (!_sizeWidth.IsKeyboardFocused || _sizeWidth.Text != w.ToString())
        {
            _sizeWidth.Text = w.ToString();
        }
        if (!_sizeHeight.IsKeyboardFocused || _sizeHeight.Text != h.ToString())
        {
            _sizeHeight.Text = h.ToString();
        }
        _writingSize = false;
        _sizeBadge.Text = $"{_width} × {_height} → {w} × {h} px";
        _allFiles.Text = _rule.Kind switch
        {
            ResizeKind.Percent => string.Format(L("All files: {0}% of their size"), _rule.Value),
            ResizeKind.LongSide => string.Format(L("All files: long side {0} px"), _rule.Value),
            ResizeKind.Width => string.Format(L("All files: {0} px wide, height to scale"), _rule.Value),
            ResizeKind.Height => string.Format(L("All files: {0} px high, width to scale"), _rule.Value),
            _ => string.Format(L("All files: {0} × {1} px"), w, h),
        };
        ActionTitle = L("Resize");
    }

    /// <summary>Snapshots: "size" opens on the Size tab at 50 %, "crop" on the Crop tab; null uses the remembered tab.</summary>
    public static string? SnapshotMode { get; set; }

    private bool IsSplit => _columnCuts.Count > 0 || _rowCuts.Count > 0;

    /// <summary>Turn, mirror and "Split into parts" (images only).</summary>
    private FrameworkElement ImageControls()
    {
        var row = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        Button Glyph(string glyph, string tip, Action action, bool mirrored = false, string? font = null)
        {
            var text = new TextBlock { Text = glyph, FontSize = 16, FontFamily = new FontFamily(font ?? Wheel.WheelGlyphs.FontFamily) };
            if (mirrored)
            {
                text.RenderTransformOrigin = new Point(0.5, 0.5);
                text.RenderTransform = new ScaleTransform(-1, 1);
            }
            var button = new Button { Content = text, ToolTip = tip, MinWidth = 40, Margin = new Thickness(0, 0, 6, 0) };
            button.Click += (_, _) =>
            {
                action();
                ShowTransformed();
            };
            return button;
        }
        row.Children.Add(Glyph("\uE7AD", L("Rotate left"), () => _transform = _transform.Rotate(clockwise: false), mirrored: true));
        row.Children.Add(Glyph("\uE7AD", L("Rotate right"), () => _transform = _transform.Rotate(clockwise: true)));
        row.Children.Add(Glyph("\u21C6", L("Flip horizontally"), () => _transform = _transform.Flip(horizontal: true), font: "Segoe UI Symbol"));
        row.Children.Add(Glyph("\u21C5", L("Flip vertically"), () => _transform = _transform.Flip(horizontal: false), font: "Segoe UI Symbol"));
        _splitPanel.Children.Add(new TextBlock { Text = L("Split into parts:"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 0, 8, 0) });
        _splitPanel.Children.Add(Stepper(_acrossText, () => _columnCuts.Count + 1, n => _columnCuts = CropMath.EvenCuts(n)));
        _splitPanel.Children.Add(new Border { Width = 10 });
        _splitPanel.Children.Add(Stepper(_downText, () => _rowCuts.Count + 1, n => _rowCuts = CropMath.EvenCuts(n)));
        row.Children.Add(_splitPanel);
        return row;
    }

    private FrameworkElement Stepper(TextBlock text, Func<int> get, Action<int> set)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (label, step) in new[] { ("\u2212", -1), ("+", 1) })
        {
            var button = new Button { Content = label, MinWidth = 32, Padding = new Thickness(6, 2, 6, 4) };
            button.Click += (_, _) =>
            {
                set(Math.Clamp(get() + step, 1, 10));
                Redraw();
            };
            if (step > 0)
            {
                panel.Children.Add(text);
            }
            panel.Children.Add(button);
        }
        return panel;
    }

    /// <summary>Shows the preview turned and mirrored; the frame starts again from the whole picture.</summary>
    private void ShowTransformed()
    {
        if (_preview is null)
        {
            return;
        }
        var shown = Transformed(_preview, _transform);
        (_width, _height) = _transform.Size(_sourceWidth, _sourceHeight);
        _image.Source = shown;
        _surface.Width = _overlay.Width = shown.PixelWidth;
        _surface.Height = _overlay.Height = shown.PixelHeight;
        _rect = UnitRect.Full;
        ApplyAspect();
    }

    internal static BitmapSource Transformed(BitmapSource image, ImageTransform transform)
    {
        if (transform.IsIdentity)
        {
            return image;
        }
        // Turned first, then mirrored, as on screen.
        var group = new TransformGroup();
        group.Children.Add(new RotateTransform(90 * transform.QuarterTurns));
        group.Children.Add(new ScaleTransform(transform.FlipHorizontal ? -1 : 1, transform.FlipVertical ? -1 : 1));
        var result = new TransformedBitmap(image, group);
        result.Freeze();
        return result;
    }

    /// <summary>Dashed cut lines over the frame; each can be dragged.</summary>
    private void DrawCuts(Rect frame, double width)
    {
        _acrossText.Text = string.Format(L("{0} across"), _columnCuts.Count + 1);
        _downText.Text = string.Format(L("{0} down"), _rowCuts.Count + 1);
        if (_video is null)
        {
            ActionTitle = _showingSize ? L("Resize") : IsSplit ? L("Split") : L("Save");
        }
        _cutLayer.Children.Clear();
        var thickness = Math.Max(1, width / 400);
        var grab = Math.Max(10, width / 60);
        foreach (var (vertical, cuts) in new[] { (true, _columnCuts), (false, _rowCuts) })
        {
            for (var i = 0; i < cuts.Count; i++)
            {
                var at = vertical ? frame.Left + cuts[i] * frame.Width : frame.Top + cuts[i] * frame.Height;
                foreach (var (brush, dash) in new[] { (Brushes.Black, 0.0), (Brushes.White, 4.0) })
                {
                    _cutLayer.Children.Add(new Line
                    {
                        X1 = vertical ? at : frame.Left, X2 = vertical ? at : frame.Right,
                        Y1 = vertical ? frame.Top : at, Y2 = vertical ? frame.Bottom : at,
                        Stroke = brush, StrokeThickness = thickness, StrokeDashArray = dash > 0 ? [dash, dash] : null,
                        Opacity = dash > 0 ? 1 : 0.5, IsHitTestVisible = false,
                    });
                }
                var hit = new Rectangle
                {
                    Fill = Brushes.Transparent, Cursor = vertical ? Cursors.SizeWE : Cursors.SizeNS,
                    Width = vertical ? grab : frame.Width, Height = vertical ? frame.Height : grab,
                };
                Canvas.SetLeft(hit, vertical ? at - grab / 2 : frame.Left);
                Canvas.SetTop(hit, vertical ? frame.Top : at - grab / 2);
                var index = i;
                hit.MouseLeftButtonDown += (_, e) =>
                {
                    _drag = (e.GetPosition(_overlay), _rect, null);
                    _cutDrag = (vertical, index);
                    _overlay.CaptureMouse();
                    e.Handled = true;
                };
                _cutLayer.Children.Add(hit);
            }
        }
    }

    protected override void Run()
    {
        var firstRect = _rect;
        var first = _images.FirstOrDefault();
        var exact = _aspect.SelectedIndex == CropMath.Aspects.Count
            && int.TryParse(_exactWidth.Text, out var ew) && int.TryParse(_exactHeight.Text, out var eh) && ew > 0 && eh > 0
            ? (ew, eh) : ((int, int)?)null;
        var frameAspect = (double)firstRect.ToPixels(_width, _height).Width / firstRect.ToPixels(_width, _height).Height;
        var lockedFirst = PixelAspect(_width, _height);
        if (_showingSize && _video is null)
        {
            var (rule, turn) = (_rule, _transform);
            Close();
            _jobs.RunEach(L("Resizing"), L("Resized"), _images, (input, _) => ResizeFile(input, turn, rule));
            return;
        }
        Close();
        if (_video is { } video)
        {
            _jobs.RunEach(L("Cropping"), L("Cropped"), [video], (path, token, progress) => Media.MediaTools.CropVideo(path, firstRect, exact, progress, token));
            return;
        }
        var (transform, columnCuts, rowCuts) = (_transform, _columnCuts.ToList(), _rowCuts.ToList());
        _jobs.RunEach(L("Cropping"), L("Cropped"), _images, (input, _) => CropFile(input, transform,
            (width, height) => input == first ? firstRect : CropMath.CenteredRect(lockedFirst ?? frameAspect, width, height),
            exact, columnCuts, rowCuts));
    }

    /// <summary>
    /// Turns and mirrors the image, resizes it by <paramref name="rule"/> and writes "name (W×H).ext" next to it, in
    /// the original's format (an edited BMP stays BMP, read-only formats become PNG), keeping the metadata and profile.
    /// </summary>
    internal static string ResizeFile(string input, ImageTransform transform, ResizeRule rule)
    {
        var image = Transformed(ImageIO.Load(input), transform);
        var (width, height) = rule.Apply(image.PixelWidth, image.PixelHeight);
        var resized = width == image.PixelWidth && height == image.PixelHeight ? image : ImageIO.Resize(image, width, height);
        var format = ToolOutput.KeepFormat(input);
        var data = ImageIO.Encode(resized, format, 0.92, ImageIO.ReadMetadata(input), ImageIO.ReadColorContexts(input));
        var full = Path.GetFullPath(input);
        return Core.OutputNaming.WriteAtomically(Path.GetDirectoryName(full)!, ResizeRule.ResultName(Core.OutputNaming.BaseName(full), width, height),
            format.Extension(), temp => File.WriteAllBytes(temp, data));
    }

    /// <summary>
    /// Turns and mirrors the image, crops it (<paramref name="rect"/> gets the turned size), scales it to
    /// <paramref name="exact"/> when given, and writes "name (cropped).ext", or "name (changed).ext" for a turn alone.
    /// With cut lines the result is cut into parts in a "name (parts)" folder. The format, pixel format (grey, CMYK,
    /// 16 bits), colour profile and metadata of the original are kept.
    /// </summary>
    internal static string CropFile(string input, ImageTransform transform, Func<int, int, UnitRect> rect, (int, int)? exact,
        IReadOnlyList<double> columnCuts, IReadOnlyList<double> rowCuts)
    {
        var image = Transformed(ImageIO.Load(input), transform);
        var pixels = rect(image.PixelWidth, image.PixelHeight).ToPixels(image.PixelWidth, image.PixelHeight);
        var isCrop = exact is not null || pixels.Width != image.PixelWidth || pixels.Height != image.PixelHeight;
        BitmapSource cropped = isCrop ? new CroppedBitmap(image, new Int32Rect(pixels.X, pixels.Y, pixels.Width, pixels.Height)) : image;
        if (exact is { } size)
        {
            cropped = ImageIO.Resize(cropped, size.Item1, size.Item2);
        }
        var format = ToolOutput.KeepFormat(input);
        var metadata = ImageIO.ReadMetadata(input);
        var colors = ImageIO.ReadColorContexts(input);
        if (columnCuts.Count > 0 || rowCuts.Count > 0)
        {
            // The parts, row by row from the top-left, in a "name (parts)" folder.
            var tiles = CropMath.Tiles(columnCuts, rowCuts, cropped.PixelWidth, cropped.PixelHeight);
            var full = Path.GetFullPath(input);
            var baseName = Core.OutputNaming.BaseName(full);
            var digits = tiles.Count.ToString().Length;
            return Core.OutputNaming.WriteAtomically(Path.GetDirectoryName(full)!, $"{baseName} ({L("parts")})", "", folder =>
            {
                Directory.CreateDirectory(folder);
                for (var i = 0; i < tiles.Count; i++)
                {
                    var tile = tiles[i];
                    var part = new CroppedBitmap(cropped, new Int32Rect(tile.X, tile.Y, tile.Width, tile.Height));
                    var name = $"{baseName} {(i + 1).ToString().PadLeft(digits, '0')}.{format.Extension()}";
                    File.WriteAllBytes(Path.Combine(folder, name), ImageIO.Encode(part, format, 0.92, metadata, colors));
                }
            });
        }
        var suffix = isCrop || transform.IsIdentity ? L("cropped") : L("changed");
        return ToolOutput.Write(input, suffix, format, ImageIO.Encode(cropped, format, 0.92, metadata, colors));
    }
}
