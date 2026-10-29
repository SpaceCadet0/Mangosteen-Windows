using System.Runtime.InteropServices;
using static Mangosteen.Platform.Native;

namespace Mangosteen.Platform;

/// <summary>
/// Reads the selected files of one Explorer window (the tab under the cursor) or of the desktop, through
/// the shell's own COM objects. Must run on an STA thread (the WPF UI thread).
/// </summary>
internal static class ShellSelection
{
    private static readonly Guid ClsidShellWindows = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    private static readonly Guid SidTopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private static readonly Guid IidShellBrowser = new("000214E2-0000-0000-C000-000000000046");
    private static readonly Guid IidShellItemArray = new("B63EA76D-1F85-456F-A19C-48159EFA858B");

    private const int SWC_DESKTOP = 8;
    private const int SWFO_NEEDDISPATCH = 1;
    private const uint SVGIO_SELECTION = 0x1;
    private const uint SVGIO_FLAG_VIEWORDER = 0x80000000;
    private const uint SIGDN_FILESYSPATH = 0x80058000;

    public sealed record Result(IReadOnlyList<string> Paths, string Source);

    /// <summary>
    /// The file-system paths selected in the shell view that contains <paramref name="target"/>. With
    /// <paramref name="topLevel"/>, <paramref name="target"/> is an Explorer window and its visible tab is read.
    /// </summary>
    public static Result Read(IntPtr target, string rootClass, bool topLevel = false)
    {
        var windows = (IShellWindows)Activator.CreateInstance(Type.GetTypeFromCLSID(ClsidShellWindows, throwOnError: true)!)!;
        try
        {
            if (rootClass is "Progman" or "WorkerW")
            {
                // The desktop's DefView can live under a WorkerW (wallpaper slideshow), so it is not matched by
                // ancestry: ask for the desktop view directly.
                object location = 0; // CSIDL_DESKTOP
                object root = null!;
                var desktop = windows.FindWindowSW(ref location, ref root, SWC_DESKTOP, out _, SWFO_NEEDDISPATCH);
                return new Result(desktop is null ? [] : SelectedPaths(Browser(desktop)), "desktop");
            }

            // Windows 11 tabs share one CabinetWClass window; each tab has its own browser window, and only the
            // visible tab's browser is an ancestor of the clicked view.
            var count = windows.Count;
            for (var i = 0; i < count; i++)
            {
                object? item;
                try
                {
                    item = windows.Item(i);
                }
                catch (COMException)
                {
                    continue;
                }
                if (item is null)
                {
                    continue;
                }
                var browser = Browser(item);
                if (browser is null || browser.GetWindow(out var browserWindow) != 0)
                {
                    continue;
                }
                if (browserWindow == target || IsChild(browserWindow, target)
                    || topLevel && IsChild(target, browserWindow) && IsWindowVisible(browserWindow))
                {
                    return new Result(SelectedPaths(browser), $"window {i + 1} of {count} ({ClassName(browserWindow)})");
                }
            }
            return new Result([], $"no match among {count} shell windows");
        }
        finally
        {
            Marshal.FinalReleaseComObject(windows);
        }
    }

    private static IShellBrowser? Browser(object window)
    {
        if (window is not IComServiceProvider provider)
        {
            return null;
        }
        var service = SidTopLevelBrowser;
        var iid = IidShellBrowser;
        return provider.QueryService(ref service, ref iid, out var browser) == 0 ? browser as IShellBrowser : null;
    }

    private static List<string> SelectedPaths(IShellBrowser? browser)
    {
        var paths = new List<string>();
        if (browser is null || browser.QueryActiveShellView(out var view) != 0 || view is not IFolderView folderView)
        {
            return paths;
        }
        var iid = IidShellItemArray;
        if (folderView.Items(SVGIO_SELECTION | SVGIO_FLAG_VIEWORDER, ref iid, out var result) != 0 ||
            result is not IShellItemArray items || items.GetCount(out var count) != 0)
        {
            return paths;
        }
        for (uint i = 0; i < count; i++)
        {
            if (items.GetItemAt(i, out var item) != 0 || item is null)
            {
                continue;
            }
            // Items outside the file system (This PC, Recycle Bin, files inside a zip) have no path.
            if (item.GetDisplayName(SIGDN_FILESYSPATH, out var pointer) == 0 && pointer != IntPtr.Zero)
            {
                var path = Marshal.PtrToStringUni(pointer);
                Marshal.FreeCoTaskMem(pointer);
                if (!string.IsNullOrEmpty(path) && (File.Exists(path) || Directory.Exists(path)))
                {
                    paths.Add(path);
                }
            }
        }
        return paths;
    }

    [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IShellWindows
    {
        int Count { get; }

        [return: MarshalAs(UnmanagedType.IDispatch)]
        object? Item([MarshalAs(UnmanagedType.Struct)] object index);

        [return: MarshalAs(UnmanagedType.IUnknown)]
        object _NewEnum();

        void Register([MarshalAs(UnmanagedType.IDispatch)] object pid, int hwnd, int swClass, out int cookie);

        void RegisterPending(int threadId, [MarshalAs(UnmanagedType.Struct)] ref object loc,
            [MarshalAs(UnmanagedType.Struct)] ref object locRoot, int swClass, out int cookie);

        void Revoke(int cookie);

        void OnNavigate(int cookie, [MarshalAs(UnmanagedType.Struct)] ref object loc);

        void OnActivated(int cookie, [MarshalAs(UnmanagedType.VariantBool)] bool active);

        [return: MarshalAs(UnmanagedType.IDispatch)]
        object? FindWindowSW([MarshalAs(UnmanagedType.Struct)] ref object loc,
            [MarshalAs(UnmanagedType.Struct)] ref object locRoot, int swClass, out int hwnd, int options);
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IComServiceProvider
    {
        [PreserveSig]
        int QueryService(ref Guid service, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object? result);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        // IOleWindow
        [PreserveSig] int GetWindow(out IntPtr hwnd);
        [PreserveSig] int ContextSensitiveHelp(int enterMode);
        // IShellBrowser
        [PreserveSig] int InsertMenusSB(IntPtr hmenuShared, IntPtr widths);
        [PreserveSig] int SetMenuSB(IntPtr hmenuShared, IntPtr holemenuRes, IntPtr hwndActiveObject);
        [PreserveSig] int RemoveMenusSB(IntPtr hmenuShared);
        [PreserveSig] int SetStatusTextSB(IntPtr text);
        [PreserveSig] int EnableModelessSB(int enable);
        [PreserveSig] int TranslateAcceleratorSB(IntPtr msg, ushort id);
        [PreserveSig] int BrowseObject(IntPtr pidl, uint flags);
        [PreserveSig] int GetViewStateStream(uint mode, out IntPtr stream);
        [PreserveSig] int GetControlWindow(uint id, out IntPtr hwnd);
        [PreserveSig] int SendControlMsg(uint id, uint msg, IntPtr wParam, IntPtr lParam, out IntPtr result);
        [PreserveSig] int QueryActiveShellView([MarshalAs(UnmanagedType.IUnknown)] out object? view);
    }

    /// <summary>Only the prefix of IFolderView up to Items is declared; later methods are never called.</summary>
    [ComImport, Guid("CDE725B0-CCC9-4519-917E-325D72FAB4CE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFolderView
    {
        [PreserveSig] int GetCurrentViewMode(out uint mode);
        [PreserveSig] int SetCurrentViewMode(uint mode);
        [PreserveSig] int GetFolder(ref Guid riid, out IntPtr folder);
        [PreserveSig] int Item(int index, out IntPtr pidl);
        [PreserveSig] int ItemCount(uint flags, out int count);
        [PreserveSig] int Items(uint flags, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object? items);
    }

    /// <summary>Only the prefix up to GetItemAt is declared.</summary>
    [ComImport, Guid("B63EA76D-1F85-456F-A19C-48159EFA858B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemArray
    {
        [PreserveSig] int BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid riid, out IntPtr result);
        [PreserveSig] int GetPropertyStore(int flags, ref Guid riid, out IntPtr result);
        [PreserveSig] int GetPropertyDescriptionList(IntPtr keyType, ref Guid riid, out IntPtr result);
        [PreserveSig] int GetAttributes(int attribFlags, uint mask, out uint attributes);
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetItemAt(uint index, out IShellItem? item);
    }

    /// <summary>Only the prefix up to GetDisplayName is declared.</summary>
    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid riid, out IntPtr result);
        [PreserveSig] int GetParent(out IShellItem? parent);
        [PreserveSig] int GetDisplayName(uint sigdnName, out IntPtr name);
    }
}
