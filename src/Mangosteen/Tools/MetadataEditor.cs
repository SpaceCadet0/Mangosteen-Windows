using System.Collections;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media.Imaging;
using Mangosteen.Core;
using Mangosteen.Platform;

namespace Mangosteen.Tools;

/// <summary>Common fields; an empty value removes the field.</summary>
internal enum MetadataField
{
    Description, Author, Copyright, Make, Model,
}

internal sealed record MetadataChanges
{
    public bool RemoveAll { get; init; }
    public bool RemoveLocation { get; init; }
    public Dictionary<MetadataField, string> Text { get; init; } = [];
    public DateTime? DateTaken { get; init; }

    public bool IsEmpty => !RemoveAll && !RemoveLocation && Text.Count == 0 && DateTaken is null;
}

/// <summary>
/// Reads and edits image metadata on a copy of the file. JPEG metadata is removed by dropping its segments, and
/// fields are written in place, so the pixels are never re-encoded unless the file has no room for the new text.
/// </summary>
internal static class MetadataEditor
{
    public static readonly IReadOnlySet<FileFormat> Supported = new HashSet<FileFormat> { FileFormat.Jpg, FileFormat.Tiff, FileFormat.Png };

    public static string Title(MetadataField field) => field switch
    {
        MetadataField.Description => L("Description"),
        MetadataField.Author => L("Author"),
        MetadataField.Copyright => L("Copyright"),
        MetadataField.Make => L("Camera maker"),
        _ => L("Camera model"),
    };

    private static readonly string[] LocationQueries =
    [
        "/app1/ifd/gps", "/ifd/gps",
        "/xmp/exif:GPSLatitude", "/xmp/exif:GPSLongitude", "/xmp/exif:GPSAltitude", "/xmp/exif:GPSAltitudeRef",
        "/xmp/exif:GPSTimeStamp", "/xmp/exif:GPSVersionID", "/xmp/exif:GPSMapDatum", "/xmp/exif:GPSSpeed",
        "/xmp/exif:GPSImgDirection", "/xmp/exif:GPSDestLatitude", "/xmp/exif:GPSDestLongitude",
        "/app1/ifd/xmp/exif:GPSLatitude", "/app1/ifd/xmp/exif:GPSLongitude",
    ];

    public static BitmapMetadata? Read(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        return decoder.Frames[0].Metadata as BitmapMetadata;
    }

    /// <summary>Every metadata value, flattened as "path = value".</summary>
    public static List<(string Key, string Value)> Entries(BitmapMetadata? metadata)
    {
        var entries = new List<(string, string)>();
        void Walk(BitmapMetadata block, string prefix, int depth)
        {
            if (depth > 6)
            {
                return;
            }
            foreach (var query in block)
            {
                object? value;
                try
                {
                    value = block.GetQuery(query);
                }
                catch (Exception error) when (error is NotSupportedException or InvalidOperationException or ArgumentException)
                {
                    continue;
                }
                if (value is BitmapMetadata nested)
                {
                    Walk(nested, prefix + query, depth + 1);
                }
                else
                {
                    entries.Add((prefix + query, Describe(value)));
                }
            }
        }
        if (metadata is not null)
        {
            Walk(metadata, "", 0);
        }
        return entries;
    }

    private static string Describe(object? value) => value switch
    {
        null => "",
        string text => text,
        byte[] bytes => string.Format(L("{0} bytes"), bytes.Length),
        BitmapMetadataBlob blob => string.Format(L("{0} bytes"), blob.GetBlobValue().Length),
        IEnumerable items and not string => string.Join(", ", items.Cast<object?>().Take(12).Select(Describe)),
        IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    public static string Text(MetadataField field, BitmapMetadata? metadata)
    {
        if (metadata is null)
        {
            return "";
        }
        try
        {
            return field switch
            {
                MetadataField.Description => metadata.Title ?? metadata.Subject ?? "",
                MetadataField.Author => metadata.Author is { Count: > 0 } authors ? string.Join("; ", authors) : "",
                MetadataField.Copyright => metadata.Copyright ?? "",
                MetadataField.Make => metadata.CameraManufacturer ?? "",
                _ => metadata.CameraModel ?? "",
            };
        }
        catch (Exception error) when (error is NotSupportedException or InvalidOperationException)
        {
            return "";
        }
    }

    public static DateTime? DateTaken(BitmapMetadata? metadata)
    {
        try
        {
            return metadata?.DateTaken is { } text && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                ? date
                : null;
        }
        catch (Exception error) when (error is NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    public static (double Latitude, double Longitude)? Location(BitmapMetadata? metadata)
    {
        if (metadata is null)
        {
            return null;
        }
        // Raw EXIF first (rationals), then the property-system names some files use.
        foreach (var gps in new[] { "/app1/ifd/gps", "/ifd/gps" })
        {
            try
            {
                if (metadata.GetQuery(gps + "/{ushort=2}") is ulong[] { Length: 3 } latRaw && metadata.GetQuery(gps + "/{ushort=4}") is ulong[] { Length: 3 } lonRaw)
                {
                    var latitude = Degrees(latRaw);
                    var longitude = Degrees(lonRaw);
                    if (metadata.GetQuery(gps + "/{ushort=1}") as string == "S")
                    {
                        latitude = -latitude;
                    }
                    if (metadata.GetQuery(gps + "/{ushort=3}") as string == "W")
                    {
                        longitude = -longitude;
                    }
                    return (latitude, longitude);
                }
            }
            catch (Exception error) when (error is NotSupportedException or InvalidOperationException or ArgumentException)
            {
            }
        }
        try
        {
            if (metadata?.GetQuery("System.GPS.Latitude") is double[] { Length: 3 } lat
                && metadata.GetQuery("System.GPS.Longitude") is double[] { Length: 3 } lon)
            {
                var latitude = lat[0] + lat[1] / 60 + lat[2] / 3600;
                var longitude = lon[0] + lon[1] / 60 + lon[2] / 3600;
                if (metadata.GetQuery("System.GPS.LatitudeRef") as string == "S")
                {
                    latitude = -latitude;
                }
                if (metadata.GetQuery("System.GPS.LongitudeRef") as string == "W")
                {
                    longitude = -longitude;
                }
                return (latitude, longitude);
            }
        }
        catch (Exception error) when (error is NotSupportedException or InvalidOperationException or ArgumentException)
        {
        }
        return null;
    }

    private static double Degrees(ulong[] rationals)
    {
        static double Value(ulong rational)
        {
            var denominator = (uint)(rational >> 32);
            return denominator == 0 ? 0 : (double)(uint)rational / denominator;
        }
        return Value(rationals[0]) + Value(rationals[1]) / 60 + Value(rationals[2]) / 3600;
    }

    /// <summary>Writes "name (metadata).ext" next to the original with the changes applied.</summary>
    public static string Apply(string input, MetadataChanges changes)
    {
        var format = FileFormats.Detect(input) ?? throw new NotSupportedException(L("This file has no editable metadata."));
        if (!Supported.Contains(format))
        {
            throw new NotSupportedException(string.Format(L("Metadata in {0} files cannot be edited."), format.DisplayName()));
        }
        var full = Path.GetFullPath(input);
        return OutputNaming.WriteAtomically(Path.GetDirectoryName(full)!, $"{OutputNaming.BaseName(full)} ({L("metadata")})", format.Extension(), output =>
        {
            if (changes.RemoveAll)
            {
                if (format == FileFormat.Jpg)
                {
                    File.WriteAllBytes(output, StripJpegMetadata(File.ReadAllBytes(full)));
                }
                else
                {
                    Reencode(full, output, format, null);
                }
                return;
            }
            File.Copy(full, output);
            if (!TryInPlace(output, changes))
            {
                Log.Write($"metadata: no room in {Path.GetFileName(full)}, rebuilding its metadata segments");
                File.Delete(output);
                var metadata = Read(full)?.Clone() as BitmapMetadata ?? new BitmapMetadata(format switch
                {
                    FileFormat.Jpg => "jpg",
                    FileFormat.Png => "png",
                    _ => "tiff",
                });
                Change(metadata, changes);
                if (format == FileFormat.Jpg)
                {
                    File.WriteAllBytes(output, SpliceJpegMetadata(File.ReadAllBytes(full), metadata));
                }
                else
                {
                    Reencode(full, output, format, metadata);
                }
            }
        });
    }

    private static bool TryInPlace(string path, MetadataChanges changes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.Default);
        var writer = decoder.Frames[0].CreateInPlaceBitmapMetadataWriter();
        if (writer is null)
        {
            return false;
        }
        try
        {
            Change(writer, changes);
            return writer.TrySave();
        }
        catch (Exception error) when (error is NotSupportedException or InvalidOperationException or ArgumentException
            or System.Runtime.InteropServices.COMException)
        {
            Log.Error("metadata in place", error);
            return false;
        }
    }

    private static void Change(BitmapMetadata metadata, MetadataChanges changes)
    {
        foreach (var (field, value) in changes.Text)
        {
            var text = value.Trim();
            Try(() =>
            {
                switch (field)
                {
                    case MetadataField.Description:
                        metadata.Title = text;
                        metadata.Subject = text;
                        break;
                    case MetadataField.Author:
                        metadata.Author = new ReadOnlyCollection<string>(text.Split(';').Select(a => a.Trim()).Where(a => a.Length > 0).ToList());
                        break;
                    case MetadataField.Copyright:
                        metadata.Copyright = text;
                        break;
                    case MetadataField.Make:
                        metadata.CameraManufacturer = text;
                        break;
                    case MetadataField.Model:
                        metadata.CameraModel = text;
                        break;
                }
            });
        }
        if (changes.DateTaken is { } date)
        {
            Try(() => metadata.DateTaken = date.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));
        }
        if (changes.RemoveLocation)
        {
            foreach (var query in LocationQueries)
            {
                Try(() =>
                {
                    if (metadata.ContainsQuery(query))
                    {
                        metadata.RemoveQuery(query);
                    }
                });
            }
        }
    }

    private static void Try(Action action)
    {
        try
        {
            action();
        }
        catch (Exception error) when (error is NotSupportedException or InvalidOperationException or ArgumentException
            or System.Runtime.InteropServices.COMException)
        {
        }
    }

    private static void Reencode(string input, string output, FileFormat format, BitmapMetadata? metadata)
    {
        using var source = File.OpenRead(input);
        var decoder = BitmapDecoder.Create(source, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        BitmapEncoder encoder = format switch
        {
            FileFormat.Jpg => new JpegBitmapEncoder { QualityLevel = 100 },
            FileFormat.Png => new PngBitmapEncoder(),
            _ => new TiffBitmapEncoder(),
        };
        encoder.Frames.Add(BitmapFrame.Create(frame, null, metadata, frame.ColorContexts));
        using var stream = File.Create(output);
        encoder.Save(stream);
    }

    /// <summary>
    /// Replaces the metadata of a JPEG without touching its image data: WIC writes the new metadata into a 1×1
    /// JPEG, and its EXIF, XMP and IPTC segments are moved into the original in place of the old ones.
    /// </summary>
    public static byte[] SpliceJpegMetadata(byte[] jpeg, BitmapMetadata metadata)
    {
        var dot = BitmapSource.Create(1, 1, 96, 96, System.Windows.Media.PixelFormats.Bgr24, null, new byte[3], 3);
        var encoder = new JpegBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(dot, null, metadata, null));
        using var donorStream = new MemoryStream();
        encoder.Save(donorStream);
        var donor = Segments(donorStream.ToArray()).Where(s => IsMetadata(s.Marker)).ToList();

        using var output = new MemoryStream(jpeg.Length + donor.Sum(s => s.Bytes.Length));
        output.Write(jpeg, 0, 2);
        var inserted = false;
        var end = 2;
        foreach (var (marker, bytes, next) in Segments(jpeg))
        {
            end = next;
            if (!inserted && marker != 0xE0)
            {
                // After the JFIF header (if any), before everything else.
                foreach (var segment in donor)
                {
                    output.Write(segment.Bytes);
                }
                inserted = true;
            }
            if (!IsMetadata(marker))
            {
                output.Write(bytes);
            }
        }
        if (!inserted)
        {
            foreach (var segment in donor)
            {
                output.Write(segment.Bytes);
            }
        }
        output.Write(jpeg, end, jpeg.Length - end);
        return output.ToArray();
    }

    private static bool IsMetadata(byte marker) => marker is 0xE1 or 0xED;

    /// <summary>The segments before the image data: marker, bytes (with the marker) and the offset after the segment.</summary>
    private static IEnumerable<(byte Marker, byte[] Bytes, int Next)> Segments(byte[] jpeg)
    {
        var i = 2;
        while (i + 4 <= jpeg.Length && jpeg[i] == 0xFF && jpeg[i + 1] != 0xDA)
        {
            var length = (jpeg[i + 2] << 8) | jpeg[i + 3];
            var count = Math.Min(length + 2, jpeg.Length - i);
            yield return (jpeg[i + 1], jpeg.AsSpan(i, count).ToArray(), i + count);
            i += count;
        }
    }

    /// <summary>
    /// Removes EXIF, XMP, IPTC and comment segments from a JPEG, keeping the colour profile and the compressed
    /// image data byte for byte.
    /// </summary>
    public static byte[] StripJpegMetadata(byte[] jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
        {
            throw new InvalidDataException(L("This is not a JPEG file."));
        }
        using var output = new MemoryStream(jpeg.Length);
        output.Write(jpeg, 0, 2);
        var i = 2;
        while (i + 4 <= jpeg.Length && jpeg[i] == 0xFF)
        {
            var marker = jpeg[i + 1];
            if (marker == 0xDA) // start of scan: the rest is image data
            {
                break;
            }
            var length = (jpeg[i + 2] << 8) | jpeg[i + 3];
            var segment = jpeg.AsSpan(i, Math.Min(length + 2, jpeg.Length - i));
            var isIcc = marker == 0xE2 && segment.Length > 15 && System.Text.Encoding.ASCII.GetString(segment.Slice(4, 11)) == "ICC_PROFILE";
            var drop = marker is (>= 0xE1 and <= 0xEF) or 0xFE;
            if (!drop || isIcc)
            {
                output.Write(segment);
            }
            i += length + 2;
        }
        output.Write(jpeg, i, jpeg.Length - i);
        return output.ToArray();
    }
}
