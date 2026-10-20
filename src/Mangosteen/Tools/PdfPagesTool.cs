using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mangosteen.Conversion;
using Mangosteen.Core;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Mangosteen.Tools;

/// <summary>PDF Pages: reorder, rotate, duplicate and delete pages across one or more PDFs.</summary>
internal sealed class PdfPagesTool : ToolWindow
{
    /// <summary>One page of the result: which file and page it comes from, and its extra clockwise turn.</summary>
    internal sealed record PageItem(int Document, int Page, int Rotation);

    private readonly List<string> _pdfs;
    private readonly Jobs _jobs;
    private readonly WrapPanel _tiles = new();
    private readonly List<PageItem> _items = [];
    private readonly HashSet<int> _selected = [];
    private readonly Dictionary<(int, int), BitmapSource> _thumbnails = [];
    private readonly TextBlock _hint = new() { Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center };
    private int? _dragFrom;
    private Point _pressedAt;
    private bool _settled;

    public PdfPagesTool(IReadOnlyList<string> files, Jobs jobs) : base(L("PDF pages"), files, L("Save"))
    {
        _pdfs = files.Where(f => FileFormats.Detect(f) == FileFormat.Pdf).ToList();
        _jobs = jobs;

        var toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        void Add(string text, Action action, string? tip = null)
        {
            var button = new Button { Content = text, Margin = new Thickness(0, 0, 8, 0), MinWidth = 40, ToolTip = tip };
            button.Click += (_, _) => action();
            toolbar.Children.Add(button);
        }
        Add(L("Rotate left"), () => Rotate(-90));
        Add(L("Rotate right"), () => Rotate(90));
        Add(L("Duplicate"), Duplicate);
        Add(L("Delete"), Delete);
        Add("◀", () => Shift(-1), L("Move earlier"));
        Add("▶", () => Shift(1), L("Move later"));
        toolbar.Children.Add(_hint);

        var scroll = new ScrollViewer { Content = _tiles, Width = 820, Height = 500, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var body = new DockPanel();
        DockPanel.SetDock(toolbar, Dock.Top);
        body.Children.Add(toolbar);
        body.Children.Add(scroll);
        Body = body;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Delete)
            {
                Delete();
                e.Handled = true;
            }
        };
        Load();
    }

    public override bool IsSettled => _settled;

    private void Load()
    {
        var pdfs = _pdfs.ToList();
        Task.Run(() => pdfs.Select(p => PdfRenderer.Thumbnails(p, 260)).ToList()).ContinueWith(task =>
        {
            if (task.Exception is { } error)
            {
                _hint.Text = error.InnerException?.Message ?? error.Message;
                ActionEnabled = false;
            }
            else
            {
                for (var d = 0; d < task.Result.Count; d++)
                {
                    for (var p = 0; p < task.Result[d].Count; p++)
                    {
                        _thumbnails[(d, p)] = task.Result[d][p];
                        _items.Add(new PageItem(d, p, 0));
                    }
                }
                Redraw();
            }
            _settled = true;
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void Redraw()
    {
        _tiles.Children.Clear();
        for (var i = 0; i < _items.Count; i++)
        {
            var item = _items[i];
            var index = i;
            var image = new Image
            {
                Source = _thumbnails[(item.Document, item.Page)], Height = 170, Stretch = Stretch.Uniform,
                LayoutTransform = new RotateTransform(item.Rotation),
            };
            var label = new TextBlock
            {
                Text = _pdfs.Count > 1 ? $"{i + 1} · {Path.GetFileNameWithoutExtension(_pdfs[item.Document])} {item.Page + 1}" : $"{i + 1}",
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0), FontSize = 12, MaxWidth = 170,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var stack = new StackPanel();
            stack.Children.Add(new Border { Height = 180, Child = image, Background = Brushes.Transparent });
            stack.Children.Add(label);
            var tile = new Border
            {
                Child = stack, Width = 180, Margin = new Thickness(6), Padding = new Thickness(4), CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(2), AllowDrop = true, Cursor = Cursors.Hand,
                BorderBrush = _selected.Contains(i) ? new SolidColorBrush(Color.FromRgb(0xC0, 0x5A, 0x80)) : Brushes.Transparent,
                Background = _selected.Contains(i) ? new SolidColorBrush(Color.FromArgb(0x30, 0xC0, 0x5A, 0x80)) : Brushes.Transparent,
            };
            tile.MouseLeftButtonDown += (_, e) =>
            {
                _pressedAt = e.GetPosition(this);
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                {
                    if (!_selected.Remove(index))
                    {
                        _selected.Add(index);
                    }
                }
                else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && _selected.Count > 0)
                {
                    var anchor = _selected.Min();
                    for (var k = Math.Min(anchor, index); k <= Math.Max(anchor, index); k++)
                    {
                        _selected.Add(k);
                    }
                }
                else if (!_selected.Contains(index))
                {
                    _selected.Clear();
                    _selected.Add(index);
                }
                Redraw();
            };
            tile.MouseMove += (_, e) =>
            {
                if (e.LeftButton != MouseButtonState.Pressed || (e.GetPosition(this) - _pressedAt).Length < 8)
                {
                    return;
                }
                _dragFrom = index;
                DragDrop.DoDragDrop(tile, new DataObject("mangosteen.page", index), DragDropEffects.Move);
                _dragFrom = null;
            };
            tile.Drop += (_, _) =>
            {
                if (_dragFrom is { } from && from != index)
                {
                    var moved = _items[from];
                    _items.RemoveAt(from);
                    _items.Insert(index, moved);
                    _selected.Clear();
                    _selected.Add(index);
                    Redraw();
                }
            };
            _tiles.Children.Add(tile);
        }
        _hint.Text = string.Format(L("{0} pages. Drag pages to reorder; Ctrl or Shift selects several."), _items.Count);
        ActionEnabled = _items.Count > 0;
    }

    private void Rotate(int degrees)
    {
        foreach (var index in _selected)
        {
            _items[index] = _items[index] with { Rotation = ((_items[index].Rotation + degrees) % 360 + 360) % 360 };
        }
        Redraw();
    }

    private void Duplicate()
    {
        foreach (var index in _selected.OrderDescending())
        {
            _items.Insert(index + 1, _items[index]);
        }
        _selected.Clear();
        Redraw();
    }

    private void Delete()
    {
        foreach (var index in _selected.OrderDescending())
        {
            _items.RemoveAt(index);
        }
        _selected.Clear();
        Redraw();
    }

    private void Shift(int direction)
    {
        var order = direction < 0 ? _selected.Order().ToList() : _selected.OrderDescending().ToList();
        var moved = new HashSet<int>();
        foreach (var index in order)
        {
            var target = index + direction;
            if (target < 0 || target >= _items.Count || moved.Contains(target))
            {
                moved.Add(index);
                continue;
            }
            (_items[index], _items[target]) = (_items[target], _items[index]);
            moved.Add(target);
        }
        _selected.Clear();
        _selected.UnionWith(moved);
        Redraw();
    }

    /// <summary>Builds the new PDF; "name (pages).pdf" next to the first file.</summary>
    internal static string Save(IReadOnlyList<PageItem> items, IReadOnlyList<string> pdfs)
    {
        var sources = pdfs.Select(p => PdfReader.Open(p, PdfDocumentOpenMode.Import)).ToList();
        try
        {
            using var output = new PdfDocument();
            foreach (var item in items)
            {
                var page = output.AddPage(sources[item.Document].Pages[item.Page]);
                page.Rotate = ((page.Rotate + item.Rotation) % 360 + 360) % 360;
            }
            var first = Path.GetFullPath(pdfs[0]);
            return OutputNaming.WriteAtomically(Path.GetDirectoryName(first)!, $"{OutputNaming.BaseName(first)} ({L("pages")})", "pdf",
                temp => output.Save(temp));
        }
        finally
        {
            foreach (var source in sources)
            {
                source.Dispose();
            }
        }
    }

    protected override void Run()
    {
        var items = _items.ToList();
        var pdfs = _pdfs.ToList();
        Close();
        _jobs.RunOnce(L("Saving pages"), L("Pages saved"), string.Format(L("{0} pages"), items.Count), () => Save(items, pdfs));
    }
}
