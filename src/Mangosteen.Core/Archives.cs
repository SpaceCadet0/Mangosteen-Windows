using System.Formats.Tar;
using System.IO.Compression;
using System.Text;

namespace Mangosteen.Core;

/// <summary>ZIP, TAR and TAR.GZ: files and folders into archives, archives into each other, and extraction.</summary>
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
            default:
                throw new NotSupportedException($"{target.DisplayName()} is not an archive format.");
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
        return OutputNaming.WriteAtomically(Path.GetDirectoryName(Path.GetFullPath(archive))!, OutputNaming.BaseName(archive), "",
            folder => Extract(archive, format, folder));
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
