using System.Globalization;

namespace Mangosteen.Core;

/// <summary>Where results are saved.</summary>
public enum SaveMode
{
    /// <summary>Next to the original, as Mangosteen always did.</summary>
    NextToOriginal,
    /// <summary>Into one chosen folder.</summary>
    Folder,
    /// <summary>A folder is asked for before each job.</summary>
    Ask,
}

/// <summary>How results are placed and named: the save folder, the name template and whether the original's dates are kept.</summary>
public sealed record OutputPolicy(SaveMode Mode = SaveMode.NextToOriginal, string? Folder = null,
    string NameTemplate = OutputLocation.DefaultTemplate, bool KeepDates = false);

/// <summary>How pictures are encoded by a conversion. The standard settings keep Mangosteen's usual behaviour.</summary>
/// <param name="Quality">1…100 for every lossy picture format (JPG, WebP, AVIF); null keeps each format's usual quality.</param>
/// <param name="MaxPixelSize">Longest side of converted pictures in pixels; 0 keeps their size.</param>
/// <param name="KeepMetadata">Camera data (EXIF, GPS) is copied where the target format can hold it.</param>
public sealed record ConversionOptions(int? Quality = null, int MaxPixelSize = 0, bool KeepMetadata = false)
{
    public static readonly ConversionOptions Standard = new();
}

/// <summary>
/// A named set of conversion settings. The standard settings are not a preset; when a preset is active, its values
/// replace them for every conversion.
/// </summary>
public sealed class ConversionPreset
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    /// <summary>0.1…1 for every lossy picture format.</summary>
    public double Quality { get; set; } = 0.85;
    /// <summary>Longest side of converted pictures in pixels; 0 keeps their size.</summary>
    public int MaxPixelSize { get; set; }
    public bool PreserveMetadata { get; set; } = true;
    public SaveMode SaveMode { get; set; } = SaveMode.NextToOriginal;
    public string FolderPath { get; set; } = "";
    public string NameTemplate { get; set; } = OutputLocation.DefaultTemplate;
    public bool KeepDates { get; set; }

    public OutputPolicy Policy => new(SaveMode, FolderPath.Length == 0 ? null : FolderPath, NameTemplate, KeepDates);

    public ConversionOptions Options => new((int)Math.Round(Math.Clamp(Quality, 0.1, 1) * 100), Math.Max(0, MaxPixelSize), PreserveMetadata);
}

/// <summary>
/// The save policy every result follows. Settings set it whenever it changes; <see cref="OutputNaming.WriteAtomically"/>,
/// the single point where results are written, applies it.
/// </summary>
public static class OutputLocation
{
    public const string DefaultTemplate = "{name}";
    public static readonly IReadOnlyList<string> Tokens = ["{name}", "{date}", "{time}", "{format}"];

    private static readonly Lock Gate = new();
    private static OutputPolicy _policy = new();
    private static ConversionOptions _options = ConversionOptions.Standard;
    private static string? _askedFolder;
    private static readonly AsyncLocal<string?> CurrentSource = new();

    /// <summary>The policy jobs follow.</summary>
    public static OutputPolicy Policy
    {
        get { lock (Gate) { return _policy; } }
        set { lock (Gate) { _policy = value; } }
    }

    /// <summary>The picture settings conversions follow (the standard ones or the active preset's).</summary>
    public static ConversionOptions Options
    {
        get { lock (Gate) { return _options; } }
        set { lock (Gate) { _options = value; } }
    }

    /// <summary>The folder picked for the job being started when the policy is <see cref="SaveMode.Ask"/>.</summary>
    public static string? AskedFolder
    {
        get { lock (Gate) { return _askedFolder; } }
        set { lock (Gate) { _askedFolder = value; } }
    }

    /// <summary>The original the current job works on (flows with async work), for keeping its dates.</summary>
    public static string? Source
    {
        get => CurrentSource.Value;
        set => CurrentSource.Value = value;
    }

    /// <summary>The user's Downloads folder; the app sets the real known folder at start.</summary>
    public static string Downloads { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    /// <summary>
    /// The folder a result of a file in <paramref name="directory"/> goes to. Results go to the chosen (or asked) folder,
    /// except when <paramref name="directory"/> is already inside it: then they stay put, so a job's own subfolders keep
    /// their contents. A folder that cannot be written to (a DVD, a read-only share) sends the result to Downloads.
    /// </summary>
    public static string Directory(string directory, OutputPolicy? policy = null, string? asked = null, Func<string, bool>? canWrite = null)
    {
        policy ??= Policy;
        asked ??= AskedFolder;
        canWrite ??= IsWritable;
        var root = policy.Mode switch
        {
            SaveMode.Folder => policy.Folder,
            SaveMode.Ask => asked,
            _ => null,
        };
        if (!string.IsNullOrWhiteSpace(root) && !IsInside(directory, root) && canWrite(root))
        {
            return Path.GetFullPath(root);
        }
        if (canWrite(directory))
        {
            return directory;
        }
        return Downloads;
    }

    /// <summary>True when a file can be created in <paramref name="folder"/> (created first when it is missing).</summary>
    public static bool IsWritable(string folder)
    {
        try
        {
            System.IO.Directory.CreateDirectory(folder);
            var probe = Path.Combine(folder, $".mangosteen-probe-{Guid.NewGuid():N}");
            using (File.Create(probe, 1, FileOptions.DeleteOnClose))
            {
            }
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    public static bool IsInside(string path, string folder)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        return string.Equals(full, root, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static readonly char[] Invalid = [.. Path.GetInvalidFileNameChars(), '<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    /// <summary>
    /// <paramref name="baseName"/> put through the template: "photo" with "{name} {date}" → "photo 2026-11-10". Characters
    /// Windows does not allow in a name become dashes; an empty result falls back to <paramref name="baseName"/>.
    /// </summary>
    public static string Name(string baseName, string extension, string? template = null, DateTime? date = null)
    {
        template ??= Policy.NameTemplate;
        if (string.IsNullOrWhiteSpace(template) || template == DefaultTemplate)
        {
            return baseName;
        }
        var now = date ?? DateTime.Now;
        var format = extension.Split('.', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.ToUpperInvariant() ?? "";
        var filled = template
            .Replace("{date}", now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            .Replace("{time}", now.ToString("HH-mm-ss", CultureInfo.InvariantCulture))
            .Replace("{format}", format);
        // The name itself may hold characters the template must not touch, so it goes in last.
        var safe = new string(filled.Select(c => Array.IndexOf(Invalid, c) >= 0 ? '-' : c).ToArray());
        safe = safe.Replace("{name}", baseName).Trim().TrimEnd('.');
        if (safe.StartsWith('.'))
        {
            safe = "_" + safe;
        }
        return safe.Length == 0 ? baseName : safe;
    }

    /// <summary>Gives <paramref name="output"/> the creation and modification dates of <paramref name="original"/> when the policy says so.</summary>
    public static void KeepDates(string? original, string output, OutputPolicy? policy = null)
    {
        policy ??= Policy;
        if (!policy.KeepDates || original is null || !File.Exists(original) && !System.IO.Directory.Exists(original))
        {
            return;
        }
        try
        {
            var created = File.GetCreationTimeUtc(original);
            var modified = File.GetLastWriteTimeUtc(original);
            if (System.IO.Directory.Exists(output))
            {
                System.IO.Directory.SetCreationTimeUtc(output, created);
                System.IO.Directory.SetLastWriteTimeUtc(output, modified);
            }
            else
            {
                File.SetCreationTimeUtc(output, created);
                File.SetLastWriteTimeUtc(output, modified);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }
}
