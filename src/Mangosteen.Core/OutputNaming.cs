namespace Mangosteen.Core;

/// <summary>Chooses where results go: next to the original, never overwriting anything.</summary>
public static class OutputNaming
{
    /// <summary><c>photo.png</c>, or <c>photo 2.png</c>, <c>photo 3.png</c>... when taken.</summary>
    public static string UniquePath(string directory, string baseName, string extension, Func<string, bool>? exists = null)
    {
        exists ??= p => File.Exists(p) || Directory.Exists(p);
        for (var index = 1; ; index++)
        {
            var candidate = Candidate(directory, baseName, extension, index);
            if (!exists(candidate))
            {
                return candidate;
            }
        }
    }

    public static string BaseName(string path)
    {
        var name = Path.GetFileName(path);
        foreach (var doubleExtension in new[] { ".tar.gz", ".tar.bz2", ".tar.xz" })
        {
            if (name.EndsWith(doubleExtension, StringComparison.OrdinalIgnoreCase))
            {
                return name[..^doubleExtension.Length];
            }
        }
        return Path.GetFileNameWithoutExtension(name);
    }

    /// <summary>
    /// Runs <paramref name="produce"/> against a hidden temporary file in <paramref name="directory"/>, then
    /// moves the result to a unique final name. <paramref name="produce"/> may create a file or a folder at the
    /// temporary path. Nothing is left behind if <paramref name="produce"/> throws.
    /// </summary>
    public static string WriteAtomically(string directory, string baseName, string extension, Action<string> produce)
    {
        var temp = Path.Combine(directory, $".mangosteen-{Guid.NewGuid():N}" + (extension.Length > 0 ? "." + extension : ""));
        try
        {
            produce(temp);
            var isFolder = Directory.Exists(temp);
            if (!isFolder && !File.Exists(temp))
            {
                throw new IOException("The converter produced no file.");
            }
            // Another job may claim the same name between choosing and moving; retry with the next one.
            for (var attempt = 0; attempt < 20; attempt++)
            {
                var final = UniquePath(directory, baseName, extension);
                try
                {
                    if (isFolder)
                    {
                        Directory.Move(temp, final);
                    }
                    else
                    {
                        File.Move(temp, final, overwrite: false);
                    }
                    return final;
                }
                catch (IOException) when (File.Exists(final) || Directory.Exists(final))
                {
                }
            }
            throw new IOException($"Could not find a free name for {baseName}.{extension}.");
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
            else if (Directory.Exists(temp))
            {
                Directory.Delete(temp, recursive: true);
            }
        }
    }

    private static string Candidate(string directory, string baseName, string extension, int index)
    {
        var name = index == 1 ? baseName : $"{baseName} {index}";
        return Path.Combine(directory, extension.Length > 0 ? $"{name}.{extension}" : name);
    }
}
