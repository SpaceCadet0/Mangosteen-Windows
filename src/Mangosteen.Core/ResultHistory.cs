using System.Text.Json;

namespace Mangosteen.Core;

/// <summary>One finished operation: what it was, when, and the files or folders it made.</summary>
public sealed record ResultEntry(string Title, DateTime Time, List<string> Outputs);

/// <summary>
/// The recent results shown in the tray menu: the newest first, <see cref="Kept"/> kept on disk, <see cref="Shown"/>
/// shown. An entry whose files are all gone (deleted, moved) disappears.
/// </summary>
public sealed class ResultHistory(string file)
{
    public const int Kept = 20, Shown = 10;

    public List<ResultEntry> Entries { get; private set; } = [];

    public static ResultHistory Load(string file)
    {
        var history = new ResultHistory(file);
        try
        {
            if (File.Exists(file))
            {
                history.Entries = JsonSerializer.Deserialize<List<ResultEntry>>(File.ReadAllText(file)) ?? [];
            }
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        return history;
    }

    public void Add(string title, IEnumerable<string> outputs, DateTime time)
    {
        var list = outputs.ToList();
        if (list.Count == 0)
        {
            return;
        }
        Entries.Insert(0, new ResultEntry(title, time, list));
        if (Entries.Count > Kept)
        {
            Entries.RemoveRange(Kept, Entries.Count - Kept);
        }
        Save();
    }

    /// <summary>The entries to show, with only the outputs that still exist; entries with none left are dropped.</summary>
    public List<ResultEntry> Live(Func<string, bool>? exists = null)
    {
        exists ??= path => File.Exists(path) || Directory.Exists(path);
        var live = Entries.Select(e => e with { Outputs = e.Outputs.Where(exists).ToList() }).Where(e => e.Outputs.Count > 0).ToList();
        if (live.Count != Entries.Count)
        {
            Entries = live;
            Save();
        }
        return live.Take(Shown).ToList();
    }

    public void Remove(ResultEntry entry)
    {
        Entries.RemoveAll(e => e.Time == entry.Time && e.Title == entry.Title);
        Save();
    }

    public void Clear()
    {
        Entries.Clear();
        Save();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
            File.WriteAllText(file, JsonSerializer.Serialize(Entries, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }
}
