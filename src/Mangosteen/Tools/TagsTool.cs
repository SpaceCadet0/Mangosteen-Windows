using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Mangosteen.Core;
using Mangosteen.Media;
using PdfSharp.Pdf.IO;

namespace Mangosteen.Tools;

/// <summary>
/// Tags of video, audio and PDF files: title, artist and the like. Media tags are written by FFmpeg copying the
/// streams as they are, so nothing is re-encoded; PDF properties by PDFsharp. Results are new files,
/// "name (metadata).ext"; the original stays as it was.
/// </summary>
internal static class FileTags
{
    public enum Field { Title, Artist, Album, Date, Genre, Comment, Subject, Keywords }

    public static string Title(Field field) => field switch
    {
        Field.Title => L("Title"),
        Field.Artist => L("Author"),
        Field.Album => L("Album"),
        Field.Date => L("Year"),
        Field.Genre => L("Genre"),
        Field.Comment => L("Comment"),
        Field.Subject => L("Subject"),
        _ => L("Keywords"),
    };

    /// <summary>The fields that make sense for a format.</summary>
    public static IReadOnlyList<Field> Fields(FileFormat format) => format.Category() switch
    {
        FormatCategory.Audio => [Field.Title, Field.Artist, Field.Album, Field.Date, Field.Genre, Field.Comment],
        FormatCategory.Video => [Field.Title, Field.Artist, Field.Date, Field.Comment],
        FormatCategory.Pdf => [Field.Title, Field.Artist, Field.Subject, Field.Keywords],
        _ => [],
    };

    private static string Key(Field field) => field.ToString().ToLowerInvariant();

    public static Dictionary<Field, string> Read(string path)
    {
        var format = FileFormats.Detect(path) ?? throw new NotSupportedException(L("This file has no tags Mangosteen can edit."));
        var result = new Dictionary<Field, string>();
        if (format == FileFormat.Pdf)
        {
            using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
            void Put(Field field, string? value)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    result[field] = value;
                }
            }
            Put(Field.Title, document.Info.Title);
            Put(Field.Artist, document.Info.Author);
            Put(Field.Subject, document.Info.Subject);
            Put(Field.Keywords, document.Info.Keywords);
            return result;
        }
        var json = FFmpeg.Run("ffprobe.exe", ["-v", "error", "-show_entries", "format_tags", "-of", "json", path]);
        using var parsed = JsonDocument.Parse(json);
        // Containers spell keys differently (TITLE in Ogg and FLAC, ©nam mapped to title in MP4).
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (parsed.RootElement.TryGetProperty("format", out var formatElement) && formatElement.TryGetProperty("tags", out var tagElement))
        {
            foreach (var property in tagElement.EnumerateObject())
            {
                tags.TryAdd(property.Name, property.Value.GetString() ?? "");
            }
        }
        foreach (var field in Fields(format))
        {
            string[] keys = field switch
            {
                Field.Date => ["date", "year", "creation_time"],
                Field.Comment => ["comment", "description"],
                _ => [Key(field)],
            };
            if (keys.Select(k => tags.GetValueOrDefault(k)).FirstOrDefault(v => !string.IsNullOrEmpty(v)) is { } value)
            {
                result[field] = field == Field.Date && value.Length > 10 ? value[..10] : value;
            }
        }
        return result;
    }

    /// <summary>
    /// Writes a copy with <paramref name="changes"/> applied: a value sets the field, an empty string clears it; fields
    /// not listed stay as they were. <paramref name="removeAll"/> drops every tag first.
    /// </summary>
    public static string Write(string input, IReadOnlyDictionary<Field, string> changes, bool removeAll, Action<double>? progress, CancellationToken token)
    {
        var format = FileFormats.Detect(input) ?? throw new NotSupportedException(L("This file has no tags Mangosteen can edit."));
        var directory = Path.GetDirectoryName(Path.GetFullPath(input))!;
        var baseName = $"{OutputNaming.BaseName(input)} ({L("metadata")})";
        if (format == FileFormat.Pdf)
        {
            return OutputNaming.WriteAtomically(directory, baseName, "pdf", output =>
            {
                using var document = PdfReader.Open(input, PdfDocumentOpenMode.Modify);
                var info = document.Info;
                if (removeAll)
                {
                    info.Title = info.Author = info.Subject = info.Keywords = "";
                }
                foreach (var (field, value) in changes)
                {
                    switch (field)
                    {
                        case Field.Title:
                            info.Title = value;
                            break;
                        case Field.Artist:
                            info.Author = value;
                            break;
                        case Field.Subject:
                            info.Subject = value;
                            break;
                        case Field.Keywords:
                            info.Keywords = value;
                            break;
                    }
                }
                document.Save(output);
            });
        }
        // Not MediaConverter.Run: it adds "-map_metadata 0", which would undo "remove all".
        var arguments = new List<string> { "-i", input, "-map", "0", "-c", "copy", "-map_metadata", removeAll ? "-1" : "0" };
        // MP4 and MOV store tags in a way FFmpeg only writes with this flag.
        if (format is FileFormat.Mp4 or FileFormat.Mov or FileFormat.M4a or FileFormat.M4v)
        {
            arguments.AddRange(["-movflags", "use_metadata_tags"]);
        }
        foreach (var (field, value) in changes.OrderBy(c => c.Key))
        {
            arguments.AddRange(["-metadata", $"{Key(field)}={value}"]);
        }
        var duration = MediaConverter.Probe(input, token).Duration;
        return OutputNaming.WriteAtomically(directory, baseName, Path.GetExtension(input).TrimStart('.').ToLowerInvariant(),
            output => FFmpeg.Encode(arguments, output, duration, progress, token));
    }
}

/// <summary>"Metadata" for video, audio and PDF: the common tags, edited and saved as a copy.</summary>
internal sealed class TagsTool : ToolWindow
{
    private readonly Jobs _jobs;
    private readonly List<string> _files;
    private readonly Dictionary<FileTags.Field, TextBox> _boxes = [];
    private readonly Dictionary<FileTags.Field, string> _original = [];
    private readonly CheckBox _removeAll = new() { Content = L("Remove all metadata first"), Margin = new Thickness(0, 6, 0, 0) };

    public TagsTool(IReadOnlyList<string> files, Jobs jobs) : base(L("Metadata"), files, L("Save"))
    {
        _jobs = jobs;
        _files = files.Where(f => FileFormats.Detect(f) is { } format && WheelCatalog.HasTags(format)).ToList();
        var stack = new StackPanel { Width = 440 };
        if (_files.Count == 0)
        {
            stack.Children.Add(Note(L("This file has no tags Mangosteen can edit.")));
            Body = stack;
            return;
        }
        // The fields every file has; the values shown are the first file's.
        var fields = _files.Select(f => FileTags.Fields(FileFormats.Detect(f)!.Value)).Aggregate((a, b) => a.Intersect(b).ToList());
        try
        {
            foreach (var (field, value) in FileTags.Read(_files[0]))
            {
                _original[field] = value;
            }
        }
        catch (Exception error)
        {
            Platform.Log.Error("read tags", error);
        }
        foreach (var field in fields)
        {
            var box = new TextBox { Text = _original.GetValueOrDefault(field, "") };
            _boxes[field] = box;
            stack.Children.Add(Row(FileTags.Title(field), box));
        }
        if (_files.Count > 1)
        {
            stack.Children.Add(Note(L("The values are the first file's; what you change is written to every file.")));
        }
        stack.Children.Add(_removeAll);
        stack.Children.Add(Note(L("A copy is saved; streams are copied as they are, nothing is re-encoded.")));
        Body = stack;
        ActionEnabled = true;
    }

    protected override void Run()
    {
        var single = _files.Count == 1;
        var changes = _boxes.Where(b => _removeAll.IsChecked == true ? b.Value.Text.Length > 0 : b.Value.Text != _original.GetValueOrDefault(b.Key, "") || !single && b.Value.Text.Length > 0)
            .ToDictionary(b => b.Key, b => b.Value.Text.Trim());
        var removeAll = _removeAll.IsChecked == true;
        Close();
        _jobs.RunEach(L("Writing metadata"), L("Metadata saved"), _files, (input, token, progress) => FileTags.Write(input, changes, removeAll, progress, token));
    }
}
