using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Mangosteen.Core;
using Mangosteen.Core.Media;
using Mangosteen.Tools;

namespace Mangosteen;

/// <summary>
/// Settings in five tabs: General (language, start with Windows, Explorer menu), Wheels (the side of every segment),
/// Appearance (accent, opacity, blur), Saving (folder, name template, dates, presets) and Video (codec, hardware).
/// A new language takes effect after a quick restart; everything else at once.
/// </summary>
internal sealed class SettingsWindow : ToolWindow
{
    private readonly AppSettings _settings;
    private readonly TabControl _tabs = new() { Width = 560, MinHeight = 430 };
    private static readonly string[] TabIds = ["general", "wheels", "appearance", "saving", "video"];

    // General
    private readonly ComboBox _language = new();
    private readonly CheckBox _autostart = new() { Content = L("Start Mangosteen when I sign in to Windows") };
    private readonly CheckBox _explorer = new() { Content = L("Show Mangosteen in Explorer's right-click menu"), Margin = new Thickness(0, 8, 0, 0) };

    // Wheels
    private readonly Dictionary<string, SlicePlacement> _sides;
    private readonly StackPanel _sideRows = new();
    private readonly ComboBox _sideWheel = new();

    // Appearance
    private string _accentHex;
    private readonly TextBox _customAccent = new() { Width = 90 };
    private readonly Slider _opacity;
    private readonly FrameworkElement _opacityView;
    private readonly CheckBox _blur = new() { Content = L("Blur what is behind the wheel") };
    private readonly Ellipse _accentPreview = new() { Width = 44, Height = 44 };
    private readonly TextBlock _accentPreviewText = new() { Text = "Aa", FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly WrapPanel _swatches = new();

    // Saving: the standard settings and the presets are edited in working copies, saved together.
    private readonly List<ConversionPreset> _presets;
    private readonly ComboBox _set = new() { MinWidth = 200 };
    private readonly RadioButton _nextTo = new() { Content = L("Next to the original"), GroupName = "save" };
    private readonly RadioButton _toFolder = new() { Content = L("To a folder:"), GroupName = "save" };
    private readonly RadioButton _ask = new() { Content = L("Ask every time"), GroupName = "save" };
    private readonly TextBox _folder = new() { IsReadOnly = true, MinWidth = 220 };
    private readonly TextBox _template = new() { MinWidth = 220 };
    private readonly TextBlock _example = new() { Opacity = 0.7, FontSize = 12, Margin = new Thickness(0, 4, 0, 8) };
    private readonly CheckBox _keepDates = new() { Content = L("Keep the original's dates") };
    private readonly StackPanel _presetOnly = new();
    private readonly TextBox _presetName = new() { MinWidth = 200 };
    private readonly Slider _quality;
    private readonly FrameworkElement _qualityView;
    private readonly TextBox _maxSide = new() { Width = 80 };
    private readonly CheckBox _metadata = new() { Content = L("Keep camera data (EXIF, location)") };
    private readonly Button _deletePreset = new() { Content = L("Delete"), Margin = new Thickness(8, 0, 0, 0) };
    private int _editing = -1;
    private bool _loading;
    private SaveMode _standardMode;
    private string _standardFolder, _standardTemplate;
    private bool _standardDates;
    private Guid? _active;

    // Video
    private readonly ComboBox _codec = new() { MinWidth = 200 };
    private readonly CheckBox _hardware = new() { Content = L("Use the graphics card (NVIDIA, Intel, AMD)") };

    public SettingsWindow(AppSettings settings, string? tab = null) : base(L("Settings"), [], L("Save"))
    {
        _settings = settings;
        _sides = new Dictionary<string, SlicePlacement>(settings.WheelSides);
        _accentHex = settings.AccentHex;
        _presets = settings.Presets.Select(p => new ConversionPreset
        {
            Id = p.Id, Name = p.Name, Quality = p.Quality, MaxPixelSize = p.MaxPixelSize, PreserveMetadata = p.PreserveMetadata,
            SaveMode = p.SaveMode, FolderPath = p.FolderPath, NameTemplate = p.NameTemplate, KeepDates = p.KeepDates,
        }).ToList();
        (_standardMode, _standardFolder, _standardTemplate, _standardDates) = (settings.SaveMode, settings.SaveFolder, settings.NameTemplate, settings.KeepDates);
        _active = settings.Preset?.Id;
        (_opacityView, _opacity) = LabeledSlider(40, 100, Math.Round(settings.WheelOpacity * 100), 96, v => $"{v:0}%");
        (_qualityView, _quality) = LabeledSlider(10, 100, 85, 85, v => $"{v:0}%");

        _tabs.Items.Add(new TabItem { Header = L("General"), Content = Pad(General()) });
        _tabs.Items.Add(new TabItem { Header = L("Wheels"), Content = Pad(Wheels()) });
        _tabs.Items.Add(new TabItem { Header = L("Appearance"), Content = Pad(Appearance()) });
        _tabs.Items.Add(new TabItem { Header = L("Saving"), Content = Pad(Saving()) });
        _tabs.Items.Add(new TabItem { Header = L("Video"), Content = Pad(Video()) });
        var index = Array.IndexOf(TabIds, tab ?? Environment.GetEnvironmentVariable("MANGOSTEEN_SETTINGS_TAB") ?? settings.SettingsTab);
        _tabs.SelectedIndex = Math.Max(0, index);
        StatusText = $"Mangosteen {Program.Version}";
        Body = _tabs;
        ActionEnabled = true;
    }

    private static FrameworkElement Pad(FrameworkElement content) =>
        new ScrollViewer { Content = content, Padding = new Thickness(12, 14, 12, 8), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 520 };

    private static TextBlock Header(string text) => new()
    {
        Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 8),
    };

    // ------------------------------------------------------------------ General

    private FrameworkElement General()
    {
        _language.Items.Add(L("Same as Windows"));
        foreach (var (_, name) in Loc.Languages)
        {
            _language.Items.Add(name);
        }
        var index = Loc.Languages.ToList().FindIndex(l => l.Code == _settings.Language);
        _language.SelectedIndex = index < 0 ? 0 : index + 1;
        _autostart.IsChecked = SafeStartsWithWindows();
        _explorer.IsChecked = _settings.ExplorerMenu;
        var stack = new StackPanel();
        stack.Children.Add(Row(L("Language"), _language));
        stack.Children.Add(_autostart);
        stack.Children.Add(_explorer);
        if (!Platform.Package.IsPackaged)
        {
            stack.Children.Add(Note(L("On Windows 11 it is under “Show more options”.")));
        }
        stack.Children.Add(new Border { Height = 10 });
        stack.Children.Add(Note(L("The keys: hold Shift for Convert, Ctrl for Tools, Alt for Actions, and right-click a file.")));
        stack.Children.Add(Note(L("Mouse wheel or Tab: the other side of the wheel")));
        stack.Children.Add(Links());
        return stack;
    }

    private static bool SafeStartsWithWindows()
    {
        try
        {
            return AppSettings.StartsWithWindows;
        }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>"Check for Updates…" and the project page.</summary>
    private static FrameworkElement Links()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        System.Windows.Documents.Hyperlink Link(string text, Action click)
        {
            var link = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run(text));
            link.Click += (_, _) => click();
            return link;
        }
        row.Children.Add(new TextBlock(Link(L("Check for Updates…"), UpdateCheck.Run)) { Margin = new Thickness(0, 0, 18, 0) });
        row.Children.Add(new TextBlock(Link(L("What's New"), () => new WhatsNewWindow().Present())) { Margin = new Thickness(0, 0, 18, 0) });
        row.Children.Add(new TextBlock(Link("GitHub", () => Process.Start(new ProcessStartInfo("https://github.com/SpaceCadet0/Mangosteen-Windows") { UseShellExecute = true }))));
        return row;
    }

    // ------------------------------------------------------------------ Wheels

    private FrameworkElement Wheels()
    {
        var stack = new StackPanel();
        stack.Children.Add(Note(string.Format(L("Up to {0} segments are on the face of a wheel; the rest are on its back. Turn the wheel over with the mouse wheel or Tab."), WheelLayout.MaxFront)));
        foreach (var kind in new[] { WheelKind.Convert, WheelKind.Tools })
        {
            _sideWheel.Items.Add(WheelCatalog.Title(kind));
        }
        _sideWheel.SelectionChanged += (_, _) => FillSides();
        _sideWheel.SelectedIndex = 1;
        stack.Children.Add(Row(L("Wheel"), _sideWheel));
        stack.Children.Add(_sideRows);
        var reset = new Button { Content = L("Reset"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
        reset.Click += (_, _) =>
        {
            var kind = _sideWheel.SelectedIndex == 0 ? WheelKind.Convert : WheelKind.Tools;
            foreach (var item in WheelCatalog.AllItems(kind))
            {
                _sides.Remove(item.Id);
            }
            FillSides();
        };
        stack.Children.Add(reset);
        return stack;
    }

    private void FillSides()
    {
        _sideRows.Children.Clear();
        var kind = _sideWheel.SelectedIndex == 0 ? WheelKind.Convert : WheelKind.Tools;
        string[] names = [L("Front"), L("Back"), L("Hidden")];
        foreach (var item in WheelCatalog.AllItems(kind))
        {
            var choice = new ComboBox { Width = 140 };
            foreach (var name in names)
            {
                choice.Items.Add(name);
            }
            choice.SelectedIndex = (int)WheelLayout.Placement(item.Id, _sides);
            var id = item.Id;
            choice.SelectionChanged += (_, _) => _sides[id] = (SlicePlacement)choice.SelectedIndex;
            var label = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            label.Children.Add(new TextBlock
            {
                Text = Wheel.WheelGlyphs.For(item.Icon), FontFamily = new FontFamily(Wheel.WheelGlyphs.FontFamily), Width = 24,
                VerticalAlignment = VerticalAlignment.Center, Opacity = 0.8,
            });
            label.Children.Add(new TextBlock { Text = ToolHint(item), VerticalAlignment = VerticalAlignment.Center });
            var dock = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            DockPanel.SetDock(choice, Dock.Right);
            dock.Children.Add(choice);
            dock.Children.Add(label);
            _sideRows.Children.Add(dock);
        }
    }

    /// <summary>Tools that share a title ("Hide", "Compress") say what they are for.</summary>
    private static string ToolHint(WheelItem item) => item.Id switch
    {
        "tool.redactVideo" => $"{item.Title} ({L("video")})",
        "tool.compressMedia" => $"{item.Title} ({L("video and audio")})",
        "tool.compressAll" => $"{item.Title} ({L("mixed files")})",
        "tool.splitMedia" => $"{item.Title} ({L("video and audio")})",
        "tool.splitPDF" => $"{item.Title} (PDF)",
        "tool.tags" => $"{item.Title} ({L("video, audio, PDF")})",
        _ => item.Title,
    };

    // ------------------------------------------------------------------ Appearance

    private FrameworkElement Appearance()
    {
        var stack = new StackPanel();
        stack.Children.Add(Header(L("Accent colour")));
        foreach (var (hex, name) in Accent.Presets)
        {
            var color = Brand.ToColor(Accent.Resolve(hex));
            var swatch = new Button
            {
                Width = 30, Height = 30, Margin = new Thickness(0, 0, 8, 8), ToolTip = L(name), Tag = hex,
                Background = new SolidColorBrush(color), BorderThickness = new Thickness(2),
            };
            swatch.Click += (_, _) =>
            {
                _accentHex = hex;
                _customAccent.Text = "";
                ShowAccent();
            };
            _swatches.Children.Add(swatch);
        }
        stack.Children.Add(_swatches);
        _customAccent.Text = Accent.Presets.Any(p => p.Hex.Equals(_accentHex, StringComparison.OrdinalIgnoreCase)) ? "" : _accentHex;
        _customAccent.TextChanged += (_, _) =>
        {
            if (Accent.Parse(_customAccent.Text) is { } color)
            {
                _accentHex = Accent.Hex(color);
                ShowAccent();
            }
        };
        var pick = new Button { Content = L("Choose…"), Margin = new Thickness(8, 0, 0, 0) };
        pick.Click += (_, _) =>
        {
            var current = Accent.Resolve(_accentHex);
            using var dialog = new System.Windows.Forms.ColorDialog { FullOpen = true, Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B) };
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                _customAccent.Text = Accent.Hex(new Rgb(dialog.Color.R, dialog.Color.G, dialog.Color.B));
            }
        };
        var custom = new StackPanel { Orientation = Orientation.Horizontal };
        custom.Children.Add(_customAccent);
        custom.Children.Add(pick);
        stack.Children.Add(Row(L("Your own"), custom));
        var preview = new Grid { Width = 44, Height = 44, HorizontalAlignment = HorizontalAlignment.Left };
        preview.Children.Add(_accentPreview);
        preview.Children.Add(_accentPreviewText);
        stack.Children.Add(Row(L("Preview"), preview));
        stack.Children.Add(Note(L("Text on the accent is white or black, whichever stands out more.")));

        stack.Children.Add(Header(L("Wheel")));
        stack.Children.Add(Row(L("Opacity"), _opacityView));
        _blur.IsChecked = _settings.WheelBlur;
        stack.Children.Add(_blur);
        stack.Children.Add(Note(L("The blur shows what was behind the wheel when it opened.")));
        ShowAccent();
        return stack;
    }

    private void ShowAccent()
    {
        var rgb = Accent.Resolve(_accentHex);
        _accentPreview.Fill = new SolidColorBrush(Brand.ToColor(rgb));
        _accentPreviewText.Foreground = Accent.TextIsWhite(rgb) ? Brushes.White : Brushes.Black;
        foreach (var swatch in _swatches.Children.OfType<Button>())
        {
            swatch.BorderBrush = string.Equals((string)swatch.Tag, _accentHex, StringComparison.OrdinalIgnoreCase)
                ? SystemColors.ControlTextBrush : Brushes.Transparent;
        }
    }

    // ------------------------------------------------------------------ Saving

    private FrameworkElement Saving()
    {
        var stack = new StackPanel();
        // The set of settings in effect: the standard one or a preset.
        var setRow = new StackPanel { Orientation = Orientation.Horizontal };
        setRow.Children.Add(_set);
        var add = new Button { Content = L("New preset"), Margin = new Thickness(8, 0, 0, 0) };
        add.Click += (_, _) =>
        {
            Store();
            var preset = new ConversionPreset { Name = string.Format(L("Preset {0}"), _presets.Count + 1) };
            _presets.Add(preset);
            FillSets(_presets.Count);
        };
        _deletePreset.Click += (_, _) =>
        {
            if (_editing >= 0)
            {
                _presets.RemoveAt(_editing);
                _editing = -1;
                FillSets(0);
            }
        };
        setRow.Children.Add(add);
        setRow.Children.Add(_deletePreset);
        stack.Children.Add(Row(L("Settings in use"), setRow));
        stack.Children.Add(Note(L("A preset replaces the standard settings for every conversion while it is in use.")));

        _presetOnly.Children.Add(Row(L("Name"), _presetName));
        _presetOnly.Children.Add(Row(L("Quality"), _qualityView));
        var side = new StackPanel { Orientation = Orientation.Horizontal };
        side.Children.Add(_maxSide);
        side.Children.Add(new TextBlock { Text = L("px, 0 keeps the size"), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 });
        _presetOnly.Children.Add(Row(L("Long side"), side));
        _presetOnly.Children.Add(_metadata);
        _presetOnly.Children.Add(new Border { Height = 8 });
        stack.Children.Add(_presetOnly);

        stack.Children.Add(Header(L("Where results go")));
        stack.Children.Add(_nextTo);
        var folderRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
        folderRow.Children.Add(_toFolder);
        folderRow.Children.Add(new Border { Width = 8 });
        folderRow.Children.Add(_folder);
        var choose = new Button { Content = L("Choose…"), Margin = new Thickness(8, 0, 0, 0) };
        choose.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = L("Folder for results") };
            if (dialog.ShowDialog() == true)
            {
                _folder.Text = dialog.FolderName;
                _toFolder.IsChecked = true;
            }
        };
        folderRow.Children.Add(choose);
        stack.Children.Add(folderRow);
        stack.Children.Add(_ask);
        stack.Children.Add(Note(L("A folder that cannot be written to sends results to Downloads.")));

        stack.Children.Add(Header(L("Name")));
        stack.Children.Add(Row(L("Template"), _template));
        var tokens = new WrapPanel { Margin = new Thickness(120, 0, 0, 0) };
        foreach (var token in OutputLocation.Tokens)
        {
            var button = new Button { Content = token, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(6, 2, 6, 2), FontSize = 12 };
            button.Click += (_, _) =>
            {
                var at = _template.CaretIndex;
                _template.Text = _template.Text.Insert(at, (at > 0 && _template.Text[at - 1] != ' ' ? " " : "") + token);
                _template.CaretIndex = _template.Text.Length;
            };
            tokens.Children.Add(button);
        }
        stack.Children.Add(tokens);
        _example.Margin = new Thickness(120, 6, 0, 8);
        stack.Children.Add(_example);
        _template.TextChanged += (_, _) => ShowExample();
        stack.Children.Add(_keepDates);

        _set.SelectionChanged += (_, _) =>
        {
            if (_loading)
            {
                return;
            }
            Store();
            Load(_set.SelectedIndex - 1);
        };
        FillSets(_active is { } id ? _presets.FindIndex(p => p.Id == id) + 1 : 0);
        return stack;
    }

    private void FillSets(int select)
    {
        _loading = true;
        _set.Items.Clear();
        _set.Items.Add(L("Standard"));
        foreach (var preset in _presets)
        {
            _set.Items.Add(preset.Name.Length > 0 ? preset.Name : L("Preset"));
        }
        _set.SelectedIndex = Math.Clamp(select, 0, _presets.Count);
        _loading = false;
        Load(_set.SelectedIndex - 1);
    }

    /// <summary>Shows the standard settings (-1) or a preset in the fields.</summary>
    private void Load(int index)
    {
        _editing = index;
        var preset = index >= 0 ? _presets[index] : null;
        _presetOnly.Visibility = preset is null ? Visibility.Collapsed : Visibility.Visible;
        _deletePreset.IsEnabled = preset is not null;
        var mode = preset?.SaveMode ?? _standardMode;
        _nextTo.IsChecked = mode == SaveMode.NextToOriginal;
        _toFolder.IsChecked = mode == SaveMode.Folder;
        _ask.IsChecked = mode == SaveMode.Ask;
        _folder.Text = preset?.FolderPath ?? _standardFolder;
        _template.Text = preset?.NameTemplate ?? _standardTemplate;
        _keepDates.IsChecked = preset?.KeepDates ?? _standardDates;
        if (preset is not null)
        {
            _presetName.Text = preset.Name;
            _quality.Value = Math.Round(preset.Quality * 100);
            _maxSide.Text = preset.MaxPixelSize.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _metadata.IsChecked = preset.PreserveMetadata;
        }
        ShowExample();
    }

    /// <summary>Keeps what the fields say in the set being edited.</summary>
    private void Store()
    {
        var mode = _toFolder.IsChecked == true ? SaveMode.Folder : _ask.IsChecked == true ? SaveMode.Ask : SaveMode.NextToOriginal;
        var template = _template.Text.Trim().Length == 0 ? OutputLocation.DefaultTemplate : _template.Text.Trim();
        if (_editing >= 0 && _editing < _presets.Count)
        {
            var preset = _presets[_editing];
            preset.Name = _presetName.Text.Trim();
            preset.Quality = _quality.Value / 100;
            preset.MaxPixelSize = int.TryParse(_maxSide.Text, out var side) ? Math.Max(0, side) : 0;
            preset.PreserveMetadata = _metadata.IsChecked == true;
            preset.SaveMode = mode;
            preset.FolderPath = _folder.Text;
            preset.NameTemplate = template;
            preset.KeepDates = _keepDates.IsChecked == true;
        }
        else
        {
            (_standardMode, _standardFolder, _standardTemplate, _standardDates) = (mode, _folder.Text, template, _keepDates.IsChecked == true);
        }
    }

    private void ShowExample() =>
        _example.Text = string.Format(L("For example: {0}"), OutputLocation.Name(L("photo"), "jpg", _template.Text) + ".jpg");

    // ------------------------------------------------------------------ Video

    private FrameworkElement Video()
    {
        var stack = new StackPanel();
        foreach (var codec in Enum.GetValues<VideoCodec>())
        {
            _codec.Items.Add(VideoEncoding.Title(codec));
        }
        _codec.SelectedIndex = (int)_settings.VideoCodec;
        stack.Children.Add(Row(L("Codec"), _codec));
        stack.Children.Add(Note(L("Used whenever video is re-encoded into MP4, MOV or MKV. A video the container already accepts is copied without re-encoding. MOV cannot hold AV1, so it gets H.264.")));
        _hardware.IsChecked = _settings.HardwareVideoEncoding;
        stack.Children.Add(_hardware);
        stack.Children.Add(Note(L("Several times faster and lighter on the processor; files come out somewhat larger. When the graphics card has no encoder for the codec, the processor encodes as before.")));
        if (!Media.FFmpeg.IsBundled)
        {
            stack.Children.Add(Note(L("This build has no FFmpeg, so video is not converted.")));
        }
        return stack;
    }

    // ------------------------------------------------------------------ Save

    protected override void Run()
    {
        Store();
        var language = _language.SelectedIndex <= 0 ? "system" : Loc.Languages[_language.SelectedIndex - 1].Code;
        var changed = language != _settings.Language;
        _settings.Language = language;
        _settings.ExplorerMenu = _explorer.IsChecked == true;
        _settings.WheelSides = _sides.Where(p => WheelLayout.Placement(p.Key, null) != p.Value).ToDictionary();
        _settings.AccentHex = _accentHex;
        _settings.WheelOpacity = Math.Round(_opacity.Value) / 100;
        _settings.WheelBlur = _blur.IsChecked == true;
        (_settings.SaveMode, _settings.SaveFolder, _settings.NameTemplate, _settings.KeepDates) = (_standardMode, _standardFolder, _standardTemplate, _standardDates);
        _settings.Presets = _presets;
        _settings.ActivePreset = _set.SelectedIndex > 0 && _set.SelectedIndex - 1 < _presets.Count ? _presets[_set.SelectedIndex - 1].Id : null;
        _settings.VideoCodec = (VideoCodec)Math.Max(0, _codec.SelectedIndex);
        _settings.HardwareVideoEncoding = _hardware.IsChecked == true;
        _settings.SettingsTab = TabIds[Math.Max(0, _tabs.SelectedIndex)];
        _settings.Save();
        Brand.Load(_settings);
        // The Store build's menu is part of the package; there the setting only hides it.
        if (!Platform.Package.IsPackaged)
        {
            if (_settings.ExplorerMenu)
            {
                ExplorerMenu.Register();
            }
            else
            {
                ExplorerMenu.Unregister();
            }
        }
        try
        {
            AppSettings.StartsWithWindows = _autostart.IsChecked == true;
        }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Platform.Log.Error("autostart", error);
        }
        Close();
        if (changed && Environment.ProcessPath is { } path)
        {
            // Every window and menu is built in the new language after a restart.
            Process.Start(new ProcessStartInfo(path, "--restart") { UseShellExecute = false });
            Application.Current.Shutdown();
        }
    }
}
