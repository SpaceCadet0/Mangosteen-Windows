using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Mangosteen.Core;

namespace Mangosteen.Tools;

/// <summary>Metadata: view everything, edit common fields, remove the location or all metadata. Results are copies.</summary>
internal sealed class MetadataTool : ToolWindow
{
    private readonly List<string> _images;
    private readonly Jobs _jobs;
    private readonly Dictionary<MetadataField, TextBox> _fields = [];
    private readonly Dictionary<MetadataField, string> _original = [];
    private readonly TextBox _date = new();
    private readonly CheckBox _removeLocation = new() { Content = L("Remove location"), Margin = new Thickness(0, 4, 0, 8) };
    private readonly CheckBox _removeAll = new() { Content = L("Remove all metadata") };
    private readonly ListView _list = new() { Height = 420, Width = 430 };
    private readonly TextBlock _location = new() { Opacity = 0.7, Margin = new Thickness(0, 0, 0, 4) };
    private string _originalDate = "";

    public MetadataTool(IReadOnlyList<string> files, Jobs jobs) : base(L("Metadata"), files, L("Save copy"))
    {
        _images = files.Where(f => FileFormats.Detect(f) is { } format && MetadataEditor.Supported.Contains(format)).ToList();
        _jobs = jobs;
        StatusText = WheelCatalog.FileCount(_images.Count);

        var grid = new GridView();
        grid.Columns.Add(new GridViewColumn { Header = L("Field"), Width = 210, DisplayMemberBinding = new System.Windows.Data.Binding("Item1") });
        grid.Columns.Add(new GridViewColumn { Header = L("Value"), Width = 200, DisplayMemberBinding = new System.Windows.Data.Binding("Item2") });
        _list.View = grid;

        var form = new StackPanel { Width = 400, Margin = new Thickness(20, 0, 0, 0) };
        foreach (var field in Enum.GetValues<MetadataField>())
        {
            var box = new TextBox();
            _fields[field] = box;
            form.Children.Add(Row(MetadataEditor.Title(field), box));
        }
        form.Children.Add(Row(L("Date taken"), _date));
        form.Children.Add(Note(L("Format: 2024-05-31 18:45. An empty field is removed.")));
        form.Children.Add(_location);
        form.Children.Add(_removeLocation);
        form.Children.Add(_removeAll);
        _removeAll.Checked += (_, _) => SetEditable(false);
        _removeAll.Unchecked += (_, _) => SetEditable(true);
        if (_images.Count > 1)
        {
            form.Children.Add(Note(L("The fields show the first image; the changes are applied to every image.")));
        }
        if (_images.Count < files.Count)
        {
            form.Children.Add(Note(L("Only JPG, PNG and TIFF metadata can be edited; other files are skipped.")));
        }

        var body = new StackPanel { Orientation = Orientation.Horizontal };
        body.Children.Add(_list);
        body.Children.Add(form);
        Body = body;
        ActionEnabled = _images.Count > 0;
        Load();
    }

    private void SetEditable(bool editable)
    {
        foreach (var box in _fields.Values)
        {
            box.IsEnabled = editable;
        }
        _date.IsEnabled = editable;
        _removeLocation.IsEnabled = editable;
    }

    private void Load()
    {
        if (_images.Count == 0)
        {
            return;
        }
        try
        {
            var metadata = MetadataEditor.Read(_images[0]);
            _list.ItemsSource = MetadataEditor.Entries(metadata);
            foreach (var (field, box) in _fields)
            {
                box.Text = _original[field] = MetadataEditor.Text(field, metadata);
            }
            _date.Text = _originalDate = MetadataEditor.DateTaken(metadata)?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "";
            _location.Text = MetadataEditor.Location(metadata) is { } place
                ? string.Format(CultureInfo.InvariantCulture, L("Location: {0:0.#####}, {1:0.#####}"), place.Latitude, place.Longitude)
                : L("No location");
            _removeLocation.IsEnabled = MetadataEditor.Location(metadata) is not null || _images.Count > 1;
        }
        catch (Exception error) when (error is IOException or NotSupportedException or FileFormatException or InvalidOperationException)
        {
            _location.Text = error.Message;
        }
    }

    protected override void Run()
    {
        DateTime? date = null;
        if (_date.Text.Trim() != _originalDate)
        {
            if (!DateTime.TryParse(_date.Text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                MessageBox.Show(this, L("The date is not in the format 2024-05-31 18:45."), L("Metadata"));
                return;
            }
            date = parsed;
        }
        var changes = new MetadataChanges
        {
            RemoveAll = _removeAll.IsChecked == true,
            RemoveLocation = _removeLocation.IsChecked == true,
            Text = _fields.Where(f => f.Value.Text != _original[f.Key]).ToDictionary(f => f.Key, f => f.Value.Text),
            DateTaken = date,
        };
        if (changes.IsEmpty)
        {
            MessageBox.Show(this, L("Nothing was changed."), L("Metadata"));
            return;
        }
        Close();
        _jobs.RunEach(L("Saving metadata"), L("Metadata saved"), _images, (input, _) => MetadataEditor.Apply(input, changes));
    }
}
