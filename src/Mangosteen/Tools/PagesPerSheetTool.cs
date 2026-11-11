using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Mangosteen.Conversion;
using Mangosteen.Core;
using Mangosteen.Core.Documents;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace Mangosteen.Tools;

/// <summary>
/// Several pages of a PDF on one sheet (2, 4, 6 or 9), for printing handouts. The pages are placed as they are, as
/// vector graphics, so text stays sharp and selectable. Saved as "name (N per sheet).pdf".
/// </summary>
internal sealed class PagesPerSheetTool : ToolWindow
{
    private readonly Jobs _jobs;
    private readonly List<string> _pdfs;
    private readonly ComboBox _count = new() { Width = 120 };
    private readonly CheckBox _frame = new() { Content = L("A thin frame around each page"), Margin = new Thickness(0, 6, 0, 0) };
    private readonly Canvas _preview = new() { Width = 360, Height = 360 };
    private readonly TextBlock _summary = new() { Opacity = 0.7, Margin = new Thickness(0, 8, 0, 0) };
    private List<BitmapSource> _thumbnails = [];
    private (double Width, double Height) _pageSize = (595, 842);
    private int _pageCount;
    private bool _settled;

    public PagesPerSheetTool(IReadOnlyList<string> files, Jobs jobs) : base(L("Pages per sheet"), files, L("Save"))
    {
        _jobs = jobs;
        _pdfs = files.Where(f => FileFormats.Detect(f) == FileFormat.Pdf).ToList();
        foreach (var n in PdfLayout.Choices)
        {
            _count.Items.Add(string.Format(L("{0} per sheet"), n));
        }
        _count.SelectedIndex = 1;
        _count.SelectionChanged += (_, _) => Draw();
        _frame.Checked += (_, _) => Draw();
        _frame.Unchecked += (_, _) => Draw();
        var form = new StackPanel { Width = 260, Margin = new Thickness(0, 0, 20, 0) };
        form.Children.Add(Row(L("Pages"), _count));
        form.Children.Add(_frame);
        form.Children.Add(_summary);
        form.Children.Add(Note(L("Pages stay vector graphics: text remains sharp and can be selected.")));
        var frame = new Border
        {
            Child = _preview, CornerRadius = new CornerRadius(10), Padding = new Thickness(10),
            Background = new SolidColorBrush(Color.FromArgb(0x18, 0x80, 0x80, 0x80)),
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(form);
        row.Children.Add(frame);
        Body = row;
        ActionEnabled = _pdfs.Count > 0;
        Load();
    }

    public override bool IsSettled => _settled;

    private int PerSheet => PdfLayout.Choices[Math.Max(0, _count.SelectedIndex)];

    private void Load()
    {
        if (_pdfs.Count == 0)
        {
            _settled = true;
            return;
        }
        var pdf = _pdfs[0];
        Task.Run(() =>
        {
            using var form = XPdfForm.FromFile(pdf);
            var size = (form.PointWidth, form.PointHeight);
            var count = form.PageCount;
            var pages = PdfRenderer.Thumbnails(pdf, 160).Take(9).ToList();
            return (size, count, pages);
        }).ContinueWith(task =>
        {
            if (task.Exception is { } error)
            {
                _summary.Text = error.InnerException?.Message ?? error.Message;
            }
            else
            {
                (_pageSize, _pageCount, _thumbnails) = task.Result;
            }
            Draw();
            _settled = true;
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>The first sheet as it will come out.</summary>
    private void Draw()
    {
        _preview.Children.Clear();
        var (sheetWidth, sheetHeight) = PdfLayout.Sheet(_pageSize.Width, _pageSize.Height, PerSheet);
        var scale = Math.Min(_preview.Width / sheetWidth, _preview.Height / sheetHeight);
        var (offsetX, offsetY) = ((_preview.Width - sheetWidth * scale) / 2, (_preview.Height - sheetHeight * scale) / 2);
        var sheet = new Rectangle { Width = sheetWidth * scale, Height = sheetHeight * scale, Fill = Brushes.White, Stroke = Brushes.Gray, StrokeThickness = 0.5 };
        Canvas.SetLeft(sheet, offsetX);
        Canvas.SetTop(sheet, offsetY);
        _preview.Children.Add(sheet);
        var cells = PdfLayout.Cells(sheetWidth, sheetHeight, PerSheet);
        for (var i = 0; i < cells.Count && i < Math.Max(_thumbnails.Count, 1); i++)
        {
            var box = PdfLayout.Fit(cells[i], _pageSize.Width, _pageSize.Height);
            FrameworkElement page = i < _thumbnails.Count
                ? new Image { Source = _thumbnails[i], Stretch = Stretch.Fill }
                : new Rectangle { Fill = Brushes.Gainsboro };
            page.Width = box.Width * scale;
            page.Height = box.Height * scale;
            Canvas.SetLeft(page, offsetX + box.X * scale);
            Canvas.SetTop(page, offsetY + box.Y * scale);
            _preview.Children.Add(page);
            if (_frame.IsChecked == true)
            {
                var border = new Rectangle { Width = page.Width, Height = page.Height, Stroke = Brushes.DarkGray, StrokeThickness = 0.6 };
                Canvas.SetLeft(border, offsetX + box.X * scale);
                Canvas.SetTop(border, offsetY + box.Y * scale);
                _preview.Children.Add(border);
            }
        }
        if (_pageCount > 0)
        {
            _summary.Text = string.Format(L("{0} pages on {1} sheets"), _pageCount, PdfLayout.SheetCount(_pageCount, PerSheet));
        }
    }

    protected override void Run()
    {
        var perSheet = PerSheet;
        var frame = _frame.IsChecked == true;
        Close();
        _jobs.RunEach(L("Placing pages"), L("Pages placed"), _pdfs, (input, token) =>
            OutputNaming.WriteAtomically(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(input))!,
                $"{OutputNaming.BaseName(input)} ({string.Format(L("{0} per sheet"), perSheet)})", "pdf",
                output => Write(input, perSheet, frame, output, token)));
    }

    /// <summary>The pages of <paramref name="input"/> drawn as forms (vector) onto sheets of <paramref name="perSheet"/>.</summary>
    internal static void Write(string input, int perSheet, bool frame, string output, CancellationToken token = default)
    {
        using var form = XPdfForm.FromFile(input);
        var (sheetWidth, sheetHeight) = PdfLayout.Sheet(form.PointWidth, form.PointHeight, perSheet);
        var cells = PdfLayout.Cells(sheetWidth, sheetHeight, perSheet);
        using var document = new PdfDocument();
        var pen = new XPen(XColors.DarkGray, 0.5);
        for (var first = 0; first < form.PageCount; first += perSheet)
        {
            token.ThrowIfCancellationRequested();
            var sheet = document.AddPage();
            sheet.Width = XUnit.FromPoint(sheetWidth);
            sheet.Height = XUnit.FromPoint(sheetHeight);
            using var graphics = XGraphics.FromPdfPage(sheet);
            for (var i = 0; i < perSheet && first + i < form.PageCount; i++)
            {
                form.PageNumber = first + i + 1;
                var box = PdfLayout.Fit(cells[i], form.PointWidth, form.PointHeight);
                var rect = new XRect(box.X, box.Y, box.Width, box.Height);
                graphics.DrawImage(form, rect);
                if (frame)
                {
                    graphics.DrawRectangle(pen, rect);
                }
            }
        }
        document.Save(output);
    }
}
