using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Mangosteen.Core;

/// <summary>ZIP, TAR and TAR.GZ: files and folders into archives, archives into each other, and extraction.</summary>
public static class Archives
{
    static Archives() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>Archives files and folders that share a parent; each keeps its name as a top-level entry.</summary>
    public static void Pack(IReadOnlyList<string> items, FileFormat target, string output)
    {
        if (items.Count == 0)
        {
            throw new ArgumentException("Nothing to archive.", nameof(items));
        }
        switch (target)
        {
            case FileFormat.Zip:
                using (var zip = ZipFile.Open(output, ZipArchiveMode.Create, Encoding.UTF8))
                {
                    foreach (var item in items)
                    {
                        AddToZip(zip, item, Path.GetFileName(Path.TrimEndingDirectorySeparator(item)));
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
                        AddToTar(tar, item, Path.GetFileName(Path.TrimEndingDirectorySeparator(item)));
                    }
                }
                break;
            default:
                throw new NotSupportedException($"{target.DisplayName()} is not an archive format.");
        }
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

    public static void Extract(string archive, FileFormat format, string folder)
    {
        Directory.CreateDirectory(folder);
        switch (format)
        {
            case FileFormat.Zip:
                // Archives made by Windows Explorer store non-Latin names in the OEM code page without the UTF-8 flag.
                using (var zip = ZipFile.Open(archive, ZipArchiveMode.Read, new LegacyNameEncoding()))
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

    /// <summary>
    /// Decodes ZIP entry names without the UTF-8 flag: strict UTF-8 first (many tools write UTF-8 without the flag),
    /// then the OEM code page Windows Explorer uses (866 on Russian Windows), then 437.
    /// </summary>
    internal sealed class LegacyNameEncoding : Encoding
    {
        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);
        private readonly Encoding _oem;

        public LegacyNameEncoding()
        {
            var oem = CultureInfo.CurrentCulture.TextInfo.OEMCodePage;
            _oem = GetEncoding(oem is 0 or 1 or 65001 ? 437 : oem);
        }

        private Encoding Pick(byte[] bytes, int index, int count)
        {
            try
            {
                StrictUtf8.GetCharCount(bytes, index, count);
                return StrictUtf8;
            }
            catch (DecoderFallbackException)
            {
                return _oem;
            }
        }

        public override int GetByteCount(char[] chars, int index, int count) => UTF8.GetByteCount(chars, index, count);
        public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex) =>
            UTF8.GetBytes(chars, charIndex, charCount, bytes, byteIndex);
        public override int GetCharCount(byte[] bytes, int index, int count) => Pick(bytes, index, count).GetCharCount(bytes, index, count);
        public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex) =>
            Pick(bytes, byteIndex, byteCount).GetChars(bytes, byteIndex, byteCount, chars, charIndex);
        public override string GetString(byte[] bytes, int index, int count) => Pick(bytes, index, count).GetString(bytes, index, count);
        public override int GetMaxByteCount(int charCount) => UTF8.GetMaxByteCount(charCount);
        public override int GetMaxCharCount(int byteCount) => byteCount;
    }
}
