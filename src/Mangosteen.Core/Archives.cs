using System.Formats.Tar;
using System.IO.Compression;
using System.Text;

namespace Mangosteen.Core;

/// <summary>
/// ZIP, TAR and TAR.GZ through .NET; 7Z, RAR, ISO, CAB, TAR.XZ and TAR.BZ2 through Windows' own tar.exe (libarchive),
/// which also writes 7Z and ISO on Windows 11. Files and folders into archives, archives into each other, extraction.
/// </summary>
public static class Archives
{
    static Archives() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>
    /// Archives files and folders; each keeps its name as a top-level entry ("photo 2.jpg" when two items from
    /// different folders share a name).
    /// </summary>
    public static void Pack(IReadOnlyList<string> items, FileFormat target, string output)
    {
        if (items.Count == 0)
        {
            throw new ArgumentException("Nothing to archive.", nameof(items));
        }
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string EntryName(string item)
        {
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(item));
            var (stem, extension) = Directory.Exists(item) ? (name, "") : (Path.GetFileNameWithoutExtension(name), Path.GetExtension(name));
            for (var index = 2; !used.Add(name); index++)
            {
                name = $"{stem} {index}{extension}";
            }
            return name;
        }
        switch (target)
        {
            case FileFormat.Zip:
                using (var zip = ZipFile.Open(output, ZipArchiveMode.Create, Encoding.UTF8))
                {
                    foreach (var item in items)
                    {
                        AddToZip(zip, item, EntryName(item));
                    }
                }
                break;
            case FileFormat.Tar:
            case FileFormat.Tgz:
                using (var file = File.Create(output))
                using (Stream stream = target == FileFormat.Tgz ? new GZipStream(file, CompressionLevel.Optimal) : file)
                using (var tar = new TarWriter(stream, TarEntryFormat.Pax, leaveOpen: false))
                {
                    foreach (var item in items)
                    {
                        AddToTar(tar, item, EntryName(item));
                    }
                }
                break;
            case FileFormat.SevenZip:
            case FileFormat.Iso:
                PackWithTar(items, target, output, EntryName);
                break;
            default:
                throw new NotSupportedException($"{target.DisplayName()} is not an archive format.");
        }
    }

    /// <summary>Windows' own tar.exe (bsdtar), there since Windows 10 1803.</summary>
    public static string TarPath => Path.Combine(Environment.SystemDirectory, "tar.exe");

    /// <summary>Runs tar.exe and returns what it printed; throws with its message when it fails.</summary>
    public static string RunTar(IEnumerable<string> arguments, string? workingDirectory = null)
    {
        if (!File.Exists(TarPath))
        {
            throw new NotSupportedException(Loc.L("This version of Windows has no tar.exe, which reads and writes this kind of archive."));
        }
        var start = new System.Diagnostics.ProcessStartInfo(TarPath)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            StandardErrorEncoding = Encoding.UTF8, StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("tar.exe did not start.");
        var errors = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            var message = errors.Result.Trim();
            throw new InvalidDataException(message.Length > 0 ? message.Replace("tar.exe: ", "") : $"tar.exe exited with code {process.ExitCode}.");
        }
        return output;
    }

    /// <summary>7z and ISO 9660 (with Joliet names) through tar.exe; each item keeps its name at the top level.</summary>
    private static void PackWithTar(IReadOnlyList<string> items, FileFormat target, string output, Func<string, string> entryName)
    {
        var format = target == FileFormat.SevenZip ? "7zip" : "iso9660";
        var arguments = new List<string> { "--format", format };
        if (target == FileFormat.Iso)
        {
            var volume = new string(Path.GetFileNameWithoutExtension(output).ToUpperInvariant().Where(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_').Take(32).ToArray());
            arguments.AddRange(["--options", $"iso9660:joliet,iso9660:rockridge,iso9660:volume-id={(volume.Length > 0 ? volume : "MANGOSTEEN")}"]);
        }
        arguments.AddRange(["-cf", Path.GetFullPath(output)]);
        // Items with names of their own are added from where they are; a name clash is resolved in a staging folder.
        var names = items.Select(entryName).ToList();
        var renamed = items.Where((item, i) => Path.GetFileName(Path.TrimEndingDirectorySeparator(item)) != names[i]).Any();
        string? staging = null;
        try
        {
            if (renamed)
            {
                staging = Directory.CreateTempSubdirectory("mangosteen-").FullName;
                for (var i = 0; i < items.Count; i++)
                {
                    CopyItem(items[i], Path.Combine(staging, names[i]));
                }
                arguments.AddRange(["-C", staging]);
                arguments.AddRange(names);
            }
            else
            {
                foreach (var item in items)
                {
                    var full = Path.GetFullPath(Path.TrimEndingDirectorySeparator(item));
                    arguments.AddRange(["-C", Path.GetDirectoryName(full)!, Path.GetFileName(full)]);
                }
            }
            RunTar(arguments);
        }
        finally
        {
            if (staging is not null)
            {
                Directory.Delete(staging, recursive: true);
            }
        }
    }

    private static void CopyItem(string source, string destination)
    {
        if (Directory.Exists(source))
        {
            Directory.CreateDirectory(destination);
            foreach (var entry in Directory.EnumerateFileSystemEntries(source))
            {
                CopyItem(entry, Path.Combine(destination, Path.GetFileName(entry)));
            }
        }
        else
        {
            File.Copy(source, destination);
        }
    }

    /// <summary>True when tar.exe writes <paramref name="format"/> ("7zip" or "iso9660"): a tiny archive is made and listed.</summary>
    public static bool TarWrites(string format)
    {
        var folder = Directory.CreateTempSubdirectory("mangosteen-tar-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(folder, "a.txt"), "Mangosteen");
            var archive = Path.Combine(folder, "test.out");
            RunTar(["--format", format, "-cf", archive, "-C", folder, "a.txt"]);
            return RunTar(["-tf", archive]).Contains("a.txt", StringComparison.Ordinal);
        }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or IOException or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            return false;
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// "Archive": the selected files and folders go into one ZIP next to the first of them, named after the folder they
    /// share ("Photos.zip", or "Photos 2.zip" when that is taken), or after the item when there is only one.
    /// </summary>
    public static string AddToArchive(IReadOnlyList<string> paths)
    {
        var first = Path.GetFullPath(Path.TrimEndingDirectorySeparator(paths[0]));
        return OutputNaming.WriteAtomically(Path.GetDirectoryName(first) ?? first, BundleName(paths), "zip",
            output => Pack(paths, FileFormat.Zip, output));
    }

    /// <summary>
    /// The item's own name when there is one, else the name of the deepest folder all the items are in, or "Archive"
    /// when they only share a drive.
    /// </summary>
    public static string BundleName(IReadOnlyList<string> paths)
    {
        if (paths.Count == 1)
        {
            return OutputNaming.BaseName(Path.TrimEndingDirectorySeparator(paths[0]));
        }
        var parents = paths.Select(p => Path.GetDirectoryName(Path.GetFullPath(Path.TrimEndingDirectorySeparator(p))) ?? "").ToList();
        var common = parents[0];
        while (common.Length > 0 && !parents.All(p => p.Equals(common, StringComparison.OrdinalIgnoreCase)
                   || p.StartsWith(Path.TrimEndingDirectorySeparator(common) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
        {
            common = Path.GetDirectoryName(common) ?? "";
        }
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(common));
        return string.IsNullOrEmpty(name) ? "Archive" : name;
    }

    /// <summary>Unpacks an archive and packs its contents again in another format.</summary>
    public static void Repack(string archive, FileFormat source, FileFormat target, string output)
    {
        var workspace = Directory.CreateTempSubdirectory("mangosteen-").FullName;
        try
        {
            Extract(archive, source, workspace);
            var items = Directory.GetFileSystemEntries(workspace).Where(p => Path.GetFileName(p) != "__MACOSX").ToList();
            if (items.Count == 0)
            {
                throw new InvalidDataException($"{Path.GetFileName(archive)} is empty.");
            }
            Pack(items, target, output);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    /// <summary>Extracts into a new folder named after the archive, next to it. Returns the folder.</summary>
    public static string ExtractNextToArchive(string archive)
    {
        if (FileFormats.Detect(archive) is not { } format || format.Category() != FormatCategory.Archive)
        {
            throw new InvalidDataException($"{Path.GetFileName(archive)} is not an archive.");
        }
        var directory = Path.GetDirectoryName(Path.GetFullPath(archive))!;
        if (format == FileFormat.Gz)
        {
            // A single gzipped file comes out as that file, not a folder holding it.
            var inner = Path.GetFileNameWithoutExtension(archive);
            return OutputNaming.WriteAtomically(directory, Path.GetFileNameWithoutExtension(inner), Path.GetExtension(inner).TrimStart('.'), output =>
            {
                using var file = File.OpenRead(archive);
                using var gzip = new GZipStream(file, CompressionMode.Decompress);
                using var target = File.Create(output);
                gzip.CopyTo(target);
            });
        }
        return OutputNaming.WriteAtomically(directory, OutputNaming.BaseName(archive), "", folder => Extract(archive, format, folder));
    }

    /// <param name="oemCodePage">The code page for ZIP names without the UTF-8 flag; the system's OEM code page by default.</param>
    public static void Extract(string archive, FileFormat format, string folder, int? oemCodePage = null)
    {
        Directory.CreateDirectory(folder);
        switch (format)
        {
            case FileFormat.Zip:
                // Archives made by Windows Explorer store non-Latin names in the OEM code page without the UTF-8 flag.
                // The choice is made once for the whole archive: some OEM names happen to be valid UTF-8 too.
                var encoding = LegacyNamesAreUtf8(archive) ? Encoding.UTF8 : Encoding.GetEncoding(oemCodePage ?? SystemOemCodePage());
                using (var zip = ZipFile.Open(archive, ZipArchiveMode.Read, encoding))
                {
                    zip.ExtractToDirectory(folder);
                }
                break;
            case FileFormat.Tar:
                TarFile.ExtractToDirectory(archive, folder, overwriteFiles: false);
                break;
            case FileFormat.Tgz:
                using (var file = File.OpenRead(archive))
                using (var gzip = new GZipStream(file, CompressionMode.Decompress))
                {
                    TarFile.ExtractToDirectory(gzip, folder, overwriteFiles: false);
                }
                break;
            case FileFormat.Gz:
                // A single gzipped file: the file itself, without ".gz".
                using (var file = File.OpenRead(archive))
                using (var gzip = new GZipStream(file, CompressionMode.Decompress))
                using (var target = File.Create(Path.Combine(folder, Path.GetFileNameWithoutExtension(archive))))
                {
                    gzip.CopyTo(target);
                }
                break;
            case FileFormat.SevenZip or FileFormat.Rar or FileFormat.Iso or FileFormat.Cab or FileFormat.Txz or FileFormat.Tbz:
                RunTar(["-xf", Path.GetFullPath(archive), "-C", Path.GetFullPath(folder)]);
                if (!Directory.EnumerateFileSystemEntries(folder).Any())
                {
                    throw new InvalidDataException(format == FileFormat.Iso
                        ? string.Format(Loc.L("“{0}” could not be read: the image may use only UDF, which tar.exe cannot read."), Path.GetFileName(archive))
                        : string.Format(Loc.L("“{0}” is empty."), Path.GetFileName(archive)));
                }
                break;
            default:
                throw new NotSupportedException($"{format.DisplayName()} is not an archive format.");
        }
    }

    /// <summary>The largest unpacked size of one part of a document package, and of all parts together.</summary>
    public const long MaxPartBytes = 256L << 20, MaxTotalBytes = 1L << 30;

    /// <summary>
    /// Refuses an Office or OpenDocument file (a ZIP package) whose parts unpack to more than the limits, before
    /// anything is inflated, so a crafted file cannot fill the memory. The sizes come from the archive; .NET stops
    /// reading a part that inflates beyond its declared size, so a forged size cannot get past either. Parts that
    /// are never read (pictures, thumbnails) are named in <paramref name="skipped"/> and do not count.
    /// </summary>
    public static void CheckUnpackedSize(string path, params string[] skipped)
    {
        using var zip = ZipFile.OpenRead(path);
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            if (skipped.Any(prefix => entry.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            total += entry.Length;
            if (entry.Length > MaxPartBytes || total > MaxTotalBytes)
            {
                throw new InvalidDataException(
                    $"{Path.GetFileName(path)} is too large to read: {entry.FullName} unpacks to {entry.Length / (1 << 20)} MB.");
            }
        }
    }

    /// <summary>True when every entry name stored without the UTF-8 flag is still valid UTF-8.</summary>
    internal static bool LegacyNamesAreUtf8(string archive)
    {
        var probe = new ProbeEncoding();
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Read, probe))
        {
            foreach (var entry in zip.Entries)
            {
                _ = entry.FullName;
            }
        }
        return !probe.SawInvalidUtf8;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern int GetOEMCP();

    /// <summary>The OEM code page Explorer uses for ZIP names (866 on Russian Windows); 437 elsewhere.</summary>
    private static int SystemOemCodePage()
    {
        if (OperatingSystem.IsWindows())
        {
            var codePage = GetOEMCP();
            if (codePage is > 1 and not 65001)
            {
                return codePage;
            }
        }
        return 437;
    }

    private static void AddToZip(ZipArchive zip, string path, string name)
    {
        if (Directory.Exists(path))
        {
            var entries = Directory.GetFileSystemEntries(path);
            if (entries.Length == 0)
            {
                zip.CreateEntry(name + "/");
            }
            foreach (var entry in entries.Order(StringComparer.Ordinal))
            {
                AddToZip(zip, entry, name + "/" + Path.GetFileName(entry));
            }
        }
        else
        {
            zip.CreateEntryFromFile(path, name, CompressionLevel.Optimal);
        }
    }

    private static void AddToTar(TarWriter tar, string path, string name)
    {
        tar.WriteEntry(path, name);
        if (Directory.Exists(path))
        {
            foreach (var entry in Directory.GetFileSystemEntries(path).Order(StringComparer.Ordinal))
            {
                AddToTar(tar, entry, name + "/" + Path.GetFileName(entry));
            }
        }
    }

    /// <summary>Decodes names as UTF-8 and remembers whether any name was not valid UTF-8.</summary>
    private sealed class ProbeEncoding : Encoding
    {
        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);

        public bool SawInvalidUtf8 { get; private set; }

        private string Decode(byte[] bytes, int index, int count)
        {
            try
            {
                return StrictUtf8.GetString(bytes, index, count);
            }
            catch (DecoderFallbackException)
            {
                SawInvalidUtf8 = true;
                return Latin1.GetString(bytes, index, count);
            }
        }

        public override int GetByteCount(char[] chars, int index, int count) => UTF8.GetByteCount(chars, index, count);
        public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex) =>
            UTF8.GetBytes(chars, charIndex, charCount, bytes, byteIndex);
        public override int GetCharCount(byte[] bytes, int index, int count) => Decode(bytes, index, count).Length;
        public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex)
        {
            var text = Decode(bytes, byteIndex, byteCount);
            text.CopyTo(0, chars, charIndex, text.Length);
            return text.Length;
        }
        public override string GetString(byte[] bytes, int index, int count) => Decode(bytes, index, count);
        public override int GetMaxByteCount(int charCount) => UTF8.GetMaxByteCount(charCount);
        public override int GetMaxCharCount(int byteCount) => byteCount;
    }
}
