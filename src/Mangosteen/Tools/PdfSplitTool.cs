using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Mangosteen.Conversion;
using Mangosteen.Core;
using Mangosteen.Wheel;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Path = System.IO.Path;

namespace Mangosteen.Tools;

/// <summary>
/// Split a PDF into several documents: click the gap between two pages to cut there, click a page to leave it out,
/// or use a preset. The parts go to a "name (parts)" folder; a single part is saved as "name (pages).pdf".
/// </summary>
internal sealed class PdfSplitTool : ToolWindow
{
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0xC0, 0x5A, 0x80));
    private static readonly Brush Excluded = new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D));
    private readonly string? _pdf;
    private readonly Jobs _jobs;
    private readonly WrapPanel _tiles = new();
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), FontWeight = FontWeights.SemiBold };
    private readonly TextBox _every = new() { Text = "2", Width = 48, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly HashSet<int> _cuts = [];
    private readonly HashSet<int> _excluded = [];
    private IReadOnlyList<BitmapSource> _thumbnails = [];
    private bool _settled;

    public PdfSplitTool(IReadOnlyList<string> files, Jobs jobs) : base(L("Split"), files, L("Split"))
    {
        _pdf = files.FirstOrDefault(f => FileFormats.Detect(f) == FileFormat.Pdf);
        _jobs = jobs;

        var presets = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        Button Preset(string text, Func<int, HashSet<int>> cuts)
        {
            var button = new Button { Content = text, Margin = new Thickness(0, 0, 8, 0) };
            button.Click += (_, _) => SetCuts(cuts(_thumbnails.Count));
            return button;
        }
        presets.Children.Add(Preset(L("Every page"), PdfSplit.EveryPage));
        presets.Children.Add(Preset(L("In half"), PdfSplit.InHalf));
        presets.Children.Add(Preset(L("Every N pages:"), count => PdfSplit.EveryN(count, int.TryParse(_every.Text, out var n) ? n : 0)));
        presets.Children.Add(_every);
        var reset = new Button { Content = L("Reset"), Margin = new Thickness(16, 0, 0, 0) };
        reset.Click += (_, _) =>
        {
            _excluded.Clear();
            SetCuts([]);
        };
        presets.Children.Add(reset);

        var scroll = new ScrollViewer { Content = _tiles, Width = 820, Height = 440, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var stack = new StackPanel();
        stack.Children.Add(presets);
        stack.Children.Add(scroll);
        stack.Children.Add(_summary);
        stack.Children.Add(Note(L("Click between two pages to cut there; click a page to leave it out. The original is not changed.")));
        Body = stack;
        ActionEnabled = false;
        Load();
    }

    public override bool IsSettled => _settled;

    private void Load()
    {
        if (_pdf is not { } pdf)
        {
            _settled = true;
            return;
        }
        Task.Run(() => PdfRenderer.Thumbnails(pdf, 200)).ContinueWith(task =>
        {
            if (task.Exception is { } error)
            {
                _summary.Text = error.InnerException?.Message ?? error.Message;
            }
            else
            {
                _thumbnails = task.Result;
                // Start with a cut in the middle, so the window shows what it does.
                SetCuts(PdfSplit.InHalf(_thumbnails.Count));
            }
            _settled = true;
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void SetCuts(HashSet<int> cuts)
    {
        _cuts.Clear();
        _cuts.UnionWith(cuts);
        Redraw();
    }

    private void Redraw()
    {
        _tiles.Children.Clear();
        for (var page = 0; page < _thumbnails.Count; page++)
        {
            var group = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            group.Children.Add(Tile(page));
            if (page < _thumbnails.Count - 1)
            {
                group.Children.Add(Gap(page));
            }
            _tiles.Children.Add(group);
        }
        var documents = PdfSplit.Documents(_thumbnails.Count, _cuts, _excluded);
        _summary.Text = documents.Count == 0
            ? L("No pages left.")
            : Core.Loc.Count(documents.Count, "document") + ": " + string.Join("  ·  ", documents.Select(PdfSplit.Ranges));
        ActionEnabled = documents.Count > 0;
    }

    private FrameworkElement Tile(int page)
    {
        var excluded = _excluded.Contains(page);
        var picture = new Grid { Height = 156 };
        picture.Children.Add(new Image { Source = _thumbnails[page], Height = 150, Stretch = Stretch.Uniform, Opacity = excluded ? 0.3 : 1 });
        if (excluded)
        {
            // Crossed out: the page is left out of every part.
            foreach (var (x1, y1, x2, y2) in new[] { (0.0, 0.0, 1.0, 1.0), (1.0, 0.0, 0.0, 1.0) })
            {
                picture.Children.Add(new Line
                {
                    X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stretch = Stretch.Fill, Stroke = Excluded, StrokeThickness = 3,
                    Margin = new Thickness(18, 8, 18, 8), IsHitTestVisible = false,
                });
            }
        }
        var stack = new StackPanel();
        stack.Children.Add(picture);
        stack.Children.Add(new TextBlock
        {
            Text = (page + 1).ToString(), HorizontalAlignment = HorizontalAlignment.Center, FontSize = 12, Margin = new Thickness(0, 4, 0, 0),
            Opacity = excluded ? 0.5 : 0.8,
        });
        var tile = new Border
        {
            Child = stack, Width = 140, Padding = new Thickness(4), CornerRadius = new CornerRadius(8), Cursor = Cursors.Hand,
            Background = Brushes.Transparent, ToolTip = excluded ? L("Click to include the page") : L("Click to leave the page out"),
        };
        tile.MouseLeftButtonUp += (_, _) =>
        {
            if (!_excluded.Remove(page))
            {
                _excluded.Add(page);
            }
            Redraw();
        };
        return tile;
    }

    private FrameworkElement Gap(int page)
    {
        var cut = _cuts.Contains(page);
        var grid = new Grid { Width = 26, Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = cut ? L("Remove the cut") : L("Cut here") };
        grid.Children.Add(new Line
        {
            X1 = 0, Y1 = 0, X2 = 0, Y2 = 1, Stretch = Stretch.Fill, HorizontalAlignment = HorizontalAlignment.Center,
            Stroke = cut ? Accent : new SolidColorBrush(Color.FromArgb(0x50, 0x80, 0x80, 0x80)), StrokeThickness = cut ? 2 : 1,
            StrokeDashArray = [4, 3], Margin = new Thickness(0, 8, 0, 24), IsHitTestVisible = false,
        });
        if (cut)
        {
            grid.Children.Add(new Border
            {
                Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Background = Accent, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 16), IsHitTestVisible = false,
                Child = new TextBlock
                {
                    Text = "", FontFamily = new FontFamily(WheelGlyphs.FontFamily), FontSize = 13, Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            });
        }
        grid.MouseLeftButtonUp += (_, _) =>
        {
            if (!_cuts.Remove(page))
            {
                _cuts.Add(page);
            }
            Redraw();
        };
        return grid;
    }

    /// <summary>
    /// Writes the parts as "name 1.pdf", "name 2.pdf"… in a "name (parts)" folder next to the original, or a single
    /// part as "name (pages).pdf".
    /// </summary>
    internal static string Save(IReadOnlyList<List<int>> documents, string pdf)
    {
        var full = Path.GetFullPath(pdf);
        var directory = Path.GetDirectoryName(full)!;
        var baseName = OutputNaming.BaseName(full);
        using var source = PdfReader.Open(full, PdfDocumentOpenMode.Import);
        void Write(IReadOnlyList<int> pages, string path)
        {
            using var output = new PdfDocument();
            foreach (var page in pages)
            {
                output.AddPage(source.Pages[page]);
            }
            output.Save(path);
        }
        if (documents.Count == 1)
        {
            return OutputNaming.WriteAtomically(directory, $"{baseName} ({L("pages")})", "pdf", temp => Write(documents[0], temp));
        }
        var digits = documents.Count.ToString().Length;
        return OutputNaming.WriteAtomically(directory, $"{baseName} ({L("parts")})", "", folder =>
        {
            Directory.CreateDirectory(folder);
            for (var i = 0; i < documents.Count; i++)
            {
                Write(documents[i], Path.Combine(folder, $"{baseName} {(i + 1).ToString().PadLeft(digits, '0')}.pdf"));
            }
        });
    }

    protected override void Run()
    {
        if (_pdf is not { } pdf)
        {
            return;
        }
        var documents = PdfSplit.Documents(_thumbnails.Count, _cuts, _excluded);
        Close();
        _jobs.RunOnce(L("Splitting"), L("Split"), Core.Loc.Count(documents.Count, "document"), () => Save(documents, pdf));
    }
}
