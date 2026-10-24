using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Mangosteen.Core;
using Mangosteen.Core.Imaging;

namespace Mangosteen.Tools;

/// <summary>Compress: a preset, a quality or a target file size, optional resize and format, with a live estimate on the first image.</summary>
internal sealed class CompressTool : ToolWindow
{
    private readonly List<string> _images;
    private readonly List<string> _pdfs;
    private readonly Jobs _jobs;
    private readonly Image _preview;
    private readonly TextBlock _estimate = new() { Margin = new Thickness(0, 6, 0, 0), FontWeight = FontWeights.SemiBold };
    private readonly PreviewScheduler<(BitmapSource Image, string Text)> _scheduler;
    private readonly RadioButton[] _modes;
    private readonly RadioButton[] _presets;
    private readonly Slider _quality;
    private readonly TextBox _target = new() { Text = "1", Width = 70 };
    private readonly ComboBox _unit = new() { Width = 70, Margin = new Thickness(8, 0, 0, 0) };
    private readonly ComboBox _maxSide = new();
    private readonly TextBox _customSide = new() { Text = "1600", Width = 80 };
    private FrameworkElement? _customSideRow;
    private readonly ComboBox _format = new();
    private readonly CheckBox _metadata = new() { Content = L("Keep metadata"), IsChecked = true };
    private readonly StackPanel _presetPanel = new();
    private readonly FrameworkElement _qualityRow;
    private readonly FrameworkElement _targetRow;
    private bool _settled;

    public CompressTool(IReadOnlyList<string> files, Jobs jobs) : base(L("Compress"), files, L("Compress"))
    {
        _images = ToolOutput.Images(files);
        _pdfs = files.Where(f => FileFormats.Detect(f) == FileFormat.Pdf).ToList();
        _jobs = jobs;
        StatusText = WheelCatalog.FileCount(_images.Count + _pdfs.Count);

        var (frame, image) = PreviewPane(340, 260);
        _preview = image;
        _scheduler = new PreviewScheduler<(BitmapSource, string)>(Dispatcher, result =>
        {
            _preview.Source = result.Item1;
            _estimate.Text = result.Item2;
            _settled = true;
        }, error =>
        {
            _estimate.Text = error.Message;
            _settled = true;
        });

        _modes = [Radio(L("Preset"), "mode", true), Radio(L("Quality"), "mode"), Radio(L("File size"), "mode")];
        var modeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        foreach (var mode in _modes)
        {
            mode.Margin = new Thickness(0, 0, 16, 0);
            mode.Checked += (_, _) => Changed();
            modeRow.Children.Add(mode);
        }

        _presets = Enum.GetValues<CompressionPreset>().Select(p =>
        {
            var radio = new RadioButton
            {
                GroupName = "preset", IsChecked = p == CompressionPreset.Balanced, Margin = new Thickness(0, 0, 0, 8),
                Content = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = CompressionSettings.Title(p) },
                        new TextBlock { Text = CompressionSettings.Detail(p), Opacity = 0.65, FontSize = 12, TextWrapping = TextWrapping.Wrap },
                    },
                },
            };
            radio.Checked += (_, _) => Changed();
            return radio;
        }).ToArray();
        foreach (var preset in _presets)
        {
            _presetPanel.Children.Add(preset);
        }

        var (qualityView, quality) = LabeledSlider(0.05, 1, 0.7, 0.7, v => $"{Math.Round(v * 100)}%");
        _quality = quality;
        _quality.ValueChanged += (_, _) => Changed();
        _qualityRow = Row(L("Quality"), qualityView);

        _unit.Items.Add(L("KB"));
        _unit.Items.Add(L("MB"));
        _unit.SelectedIndex = 1;
        _target.TextChanged += (_, _) => Changed();
        _unit.SelectionChanged += (_, _) => Changed();
        var targetPanel = new StackPanel { Orientation = Orientation.Horizontal };
        targetPanel.Children.Add(_target);
        targetPanel.Children.Add(_unit);
        _targetRow = Row(L("At most"), targetPanel);

        _maxSide.Items.Add(L("Unchanged"));
        foreach (var size in CompressionSettings.SizeLimits)
        {
            _maxSide.Items.Add($"{size} px");
        }
        _maxSide.Items.Add(L("Custom…"));
        _maxSide.SelectedIndex = 0;
        _maxSide.SelectionChanged += (_, _) => Changed();
        _customSide.TextChanged += (_, _) => Changed();
        var customPanel = new StackPanel { Orientation = Orientation.Horizontal };
        customPanel.Children.Add(_customSide);
        customPanel.Children.Add(new TextBlock { Text = " px", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 });
        _customSideRow = Row(L("Custom long side"), customPanel);
        _customSideRow.Visibility = Visibility.Collapsed;
        if (SnapshotCustomSide)
        {
            _maxSide.SelectedIndex = CompressionSettings.SizeLimits.Count + 1;
        }
        _format.Items.Add(L("Same as original"));
        foreach (var format in CompressionSettings.SelectableFormats)
        {
            _format.Items.Add(format.DisplayName());
        }
        _format.SelectedIndex = 0;
        _format.SelectionChanged += (_, _) => Changed();
        _metadata.Checked += (_, _) => Changed();
        _metadata.Unchecked += (_, _) => Changed();

        var settings = new StackPanel { Width = 400, Margin = new Thickness(20, 0, 0, 0) };
        settings.Children.Add(modeRow);
        settings.Children.Add(_presetPanel);
        settings.Children.Add(_qualityRow);
        settings.Children.Add(_targetRow);
        settings.Children.Add(Row(L("Longer side"), _maxSide));
        settings.Children.Add(_customSideRow);
        settings.Children.Add(Row(L("Format"), _format));
        settings.Children.Add(_metadata);
        settings.Children.Add(new Border { Height = 10 });
        if (_pdfs.Count > 0)
        {
            settings.Children.Add(Note(L("Pictures in PDF files are re-encoded as JPEG at screen resolution.")));
        }
        if (_images.Any(p => FileFormats.Detect(p) is { } f && !CompressionSettings.IsLossy(f)))
        {
            settings.Children.Add(Note(string.Format(L("PNG, TIFF and other lossless formats are saved as {0}."), FileFormat.Webp.DisplayName())));
        }

        var left = new StackPanel { Width = 340 };
        if (_images.Count > 0)
        {
            left.Children.Add(frame);
            left.Children.Add(new TextBlock { Text = Path.GetFileName(_images[0]), Opacity = 0.7, FontSize = 12, Margin = new Thickness(0, 6, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
            left.Children.Add(_estimate);
        }
        else
        {
            left.Children.Add(Note(string.Format(L("{0} will be compressed."), WheelCatalog.FileCount(_pdfs.Count))));
            _settled = true;
        }

        var body = new StackPanel { Orientation = Orientation.Horizontal };
        body.Children.Add(left);
        body.Children.Add(settings);
        Body = body;
        ActionEnabled = _images.Count + _pdfs.Count > 0;
        Changed();
    }

    public override bool IsSettled => _settled;

    private static RadioButton Radio(string text, string group, bool isChecked = false) =>
        new() { Content = text, GroupName = group, IsChecked = isChecked };

    private CompressionSettings Settings()
    {
        var mode = _modes[1].IsChecked == true ? CompressionMode.Quality : _modes[2].IsChecked == true ? CompressionMode.TargetSize : CompressionMode.Preset;
        var preset = (CompressionPreset)Math.Max(0, Array.FindIndex(_presets, p => p.IsChecked == true));
        var target = double.TryParse(_target.Text.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value : 1;
        return new CompressionSettings
        {
            Mode = mode,
            Preset = preset,
            Quality = _quality.Value,
            TargetBytes = (long)(Math.Max(target, 0.001) * (_unit.SelectedIndex == 0 ? 1_000 : 1_000_000)),
            MaxPixelSize = MaxSide(),
            Format = _format.SelectedIndex > 0 ? CompressionSettings.SelectableFormats[_format.SelectedIndex - 1] : null,
            PreserveMetadata = _metadata.IsChecked == true,
        };
    }

    /// <summary>Snapshots: open with "Custom…" chosen as the longer side.</summary>
    public static bool SnapshotCustomSide { get; set; }

    /// <summary>The chosen longer side: a preset, the custom one (16…20000 px, typed), or null to keep the size.</summary>
    private int? MaxSide()
    {
        var custom = CompressionSettings.SizeLimits.Count + 1;
        if (_maxSide.SelectedIndex == custom)
        {
            return int.TryParse(_customSide.Text, out var side) ? Math.Clamp(side, 16, 20000) : 1600;
        }
        return _maxSide.SelectedIndex > 0 ? CompressionSettings.SizeLimits[_maxSide.SelectedIndex - 1] : null;
    }

    private void Changed()
    {
        if (_qualityRow is null)
        {
            return; // still building the window
        }
        if (_customSideRow is not null)
        {
            _customSideRow.Visibility = _maxSide.SelectedIndex == CompressionSettings.SizeLimits.Count + 1 ? Visibility.Visible : Visibility.Collapsed;
        }
        var settings = Settings();
        _presetPanel.Visibility = settings.Mode == CompressionMode.Preset ? Visibility.Visible : Visibility.Collapsed;
        _qualityRow.Visibility = settings.Mode == CompressionMode.Quality ? Visibility.Visible : Visibility.Collapsed;
        _targetRow.Visibility = settings.Mode == CompressionMode.TargetSize ? Visibility.Visible : Visibility.Collapsed;
        _maxSide.IsEnabled = !(settings.Mode == CompressionMode.Preset && settings.Preset == CompressionPreset.Small);
        if (_images.Count == 0)
        {
            return;
        }
        var first = _images[0];
        _estimate.Text = L("Estimating…");
        _scheduler.Request(token =>
        {
            var (data, format) = Compression.CompressImage(first, settings, token);
            var decoder = BitmapDecoder.Create(new MemoryStream(data), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var shown = ImageIO.Scale(decoder.Frames[0], 900);
            var original = new FileInfo(first).Length;
            var change = original > 0 ? (int)Math.Round(((double)data.Length / original - 1) * 100) : 0;
            var text = $"{ImageIO.Size(original)} → {ImageIO.Size(data.Length)} {format.DisplayName()} ({(change > 0 ? "+" : "−")}{Math.Abs(change)}%)";
            if (data.Length >= original)
            {
                text += L(" — not smaller than the original, the file will not be saved");
            }
            return (shown, text);
        }, 250);
    }

    protected override void Run()
    {
        var settings = Settings();
        var inputs = _images.Concat(_pdfs).ToList();
        Close();
        _jobs.RunEach(L("Compressing"), L("Compressed"), inputs, (input, token) => Compression.CompressFile(input, settings, token));
    }
}
