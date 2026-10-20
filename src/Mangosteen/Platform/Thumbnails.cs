using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Mangosteen.Platform;

/// <summary>
/// File thumbnails from the Windows shell, the same ones Explorer shows (photos, videos, PDFs and anything else
/// with a thumbnail provider installed). Work happens on one background STA thread; results are cached per file,
/// size and modification time.
/// </summary>
internal static class Thumbnails
{
    private static readonly ConcurrentDictionary<(string Path, int Size, DateTime Changed), BitmapSource?> Cache = new();
    private static readonly BlockingCollection<Action> Queue = [];

    static Thumbnails()
    {
        var thread = new Thread(() =>
        {
            foreach (var work in Queue.GetConsumingEnumerable())
            {
                work();
            }
        })
        { IsBackground = true, Name = "Mangosteen thumbnails" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    /// <summary>Calls <paramref name="done"/> on <paramref name="dispatcher"/> with the thumbnail, or null when the file has none.</summary>
    public static void Request(string path, int size, Dispatcher dispatcher, Action<BitmapSource?> done)
    {
        DateTime changed;
        try
        {
            changed = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : Directory.GetLastWriteTimeUtc(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return;
        }
        var key = (path, size, changed);
        if (Cache.TryGetValue(key, out var cached))
        {
            done(cached);
            return;
        }
        Queue.Add(() =>
        {
            var thumbnail = Cache.GetOrAdd(key, _ => Load(path, size));
            dispatcher.BeginInvoke(() => done(thumbnail));
        });
    }

    private static BitmapSource? Load(string path, int size)
    {
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory) != 0 || factory is null)
            {
                return null;
            }
            try
            {
                // Thumbnails only: files without one keep their icon in the shelf.
                if (factory.GetImage(new SIZE { cx = size, cy = size }, SIIGBF_THUMBNAILONLY | SIIGBF_BIGGERSIZEOK, out var bitmap) != 0
                    || bitmap == IntPtr.Zero)
                {
                    return null;
                }
                try
                {
                    return FromHBitmap(bitmap);
                }
                finally
                {
                    DeleteObject(bitmap);
                }
            }
            finally
            {
                Marshal.FinalReleaseComObject(factory);
            }
        }
        catch (Exception error) when (error is COMException or ArgumentException or InvalidOperationException)
        {
            Log.Write($"thumbnail {path}: {error.Message}");
            return null;
        }
    }

    /// <summary>Copies a 32-bit shell bitmap, keeping its alpha (premultiplied); bitmaps without alpha become opaque.</summary>
    private static BitmapSource? FromHBitmap(IntPtr bitmap)
    {
        if (GetObject(bitmap, Marshal.SizeOf<BITMAP>(), out var info) == 0 || info.bmWidth <= 0 || info.bmHeight <= 0)
        {
            return null;
        }
        int width = info.bmWidth, height = info.bmHeight;
        var header = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = width, biHeight = -height, biPlanes = 1, biBitCount = 32,
        };
        var pixels = new byte[width * height * 4];
        var dc = GetDC(IntPtr.Zero);
        try
        {
            if (GetDIBits(dc, bitmap, 0, (uint)height, pixels, ref header, 0) == 0)
            {
                return null;
            }
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, dc);
        }
        var hasAlpha = false;
        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0)
            {
                hasAlpha = true;
                break;
            }
        }
        if (!hasAlpha)
        {
            for (var i = 3; i < pixels.Length; i += 4)
            {
                pixels[i] = 255;
            }
        }
        var result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, width * 4);
        result.Freeze();
        return result;
    }

    private const int SIIGBF_BIGGERSIZEOK = 0x1;
    private const int SIIGBF_THUMBNAILONLY = 0x8;

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [ComImport, Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, int flags, out IntPtr bitmap);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? item);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr handle, int size, out BITMAP bitmap);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER info, uint usage);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);
}
