using System.Runtime.InteropServices;
using Mangosteen.Core;
using Mangosteen.Platform;

namespace Mangosteen;

/// <summary>
/// The Store (MSIX) build's "Mangosteen ▸ Convert… / Tools… / Actions…" in File Explorer, on Windows 11 in the main
/// right-click menu. The package manifest declares this class as an out-of-process COM server in Mangosteen.exe;
/// the running Mangosteen registers it at start, so Explorer talks to it directly and a click opens the wheel at the
/// pointer for the selected items. The plain exe uses <see cref="ExplorerMenu"/> (registry verbs) instead.
/// </summary>
public static class ExplorerCommand
{
    /// <summary>Also in build/msix/AppxManifest.xml.</summary>
    public static readonly Guid ClassId = new("6B9C2A3E-58D1-4F7A-9E0B-4D2C8A71F3C5");

    private static uint _cookie;

    /// <param name="open">Opens a wheel for the paths; called on the thread that registered (the UI thread).</param>
    /// <param name="enabled">False hides the menu (the setting is off).</param>
    internal static void Register(Action<WheelKind, IReadOnlyList<string>> open, Func<bool> enabled)
    {
        var factory = new Factory(() => new Root(open, enabled));
        var clsid = ClassId;
        var result = CoRegisterClassObject(ref clsid, factory, ClsctxLocalServer, RegclsMultipleUse, out _cookie);
        Log.Write(result == 0 ? "explorer command registered" : $"explorer command not registered: 0x{result:X8}");
    }

    private const uint ClsctxLocalServer = 4, RegclsMultipleUse = 1;
    private const int S_OK = 0, S_FALSE = 1, E_NOTIMPL = unchecked((int)0x80004001), E_NOINTERFACE = unchecked((int)0x80004002);
    private const int CLASS_E_NOAGGREGATION = unchecked((int)0x80040110);
    private const uint EcfDefault = 0, EcfHasSubcommands = 1, EcsEnabled = 0, EcsHidden = 2;
    private const uint SigdnFileSysPath = 0x80058000;

    [DllImport("ole32.dll")]
    private static extern int CoRegisterClassObject(ref Guid clsid, [MarshalAs(UnmanagedType.IUnknown)] object factory, uint context,
        uint flags, out uint cookie);

    /// <summary>The file system paths of the selected items (items without one, like Control Panel, are left out).</summary>
    private static List<string> Paths(IShellItemArray? items)
    {
        var paths = new List<string>();
        if (items is null || items.GetCount(out var count) != S_OK)
        {
            return paths;
        }
        for (uint i = 0; i < count; i++)
        {
            if (items.GetItemAt(i, out var item) == S_OK && item is not null
                && item.GetDisplayName(SigdnFileSysPath, out var pointer) == S_OK && pointer != IntPtr.Zero)
            {
                paths.Add(Marshal.PtrToStringUni(pointer)!);
                Marshal.FreeCoTaskMem(pointer);
            }
        }
        return paths;
    }

    /// <summary>"Mangosteen" with the three wheels under it.</summary>
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class Root(Action<WheelKind, IReadOnlyList<string>> open, Func<bool> enabled) : IExplorerCommand
    {
        private readonly Command[] _commands =
        [
            new(WheelKind.Convert, L("Convert…"), open), new(WheelKind.Tools, L("Tools…"), open), new(WheelKind.Actions, L("Actions…"), open),
        ];

        public int GetTitle(IShellItemArray? items, out string? name)
        {
            name = "Mangosteen";
            return S_OK;
        }

        public int GetIcon(IShellItemArray? items, out string? icon)
        {
            icon = $"\"{Environment.ProcessPath}\",0";
            return S_OK;
        }

        public int GetToolTip(IShellItemArray? items, out string? tip)
        {
            tip = null;
            return E_NOTIMPL;
        }

        public int GetCanonicalName(out Guid name)
        {
            name = ClassId;
            return S_OK;
        }

        public int GetState(IShellItemArray? items, bool okToBeSlow, out uint state)
        {
            state = enabled() ? EcsEnabled : EcsHidden;
            return S_OK;
        }

        public int Invoke(IShellItemArray? items, IntPtr bindContext) => E_NOTIMPL;

        public int GetFlags(out uint flags)
        {
            flags = EcfHasSubcommands;
            return S_OK;
        }

        public int EnumSubCommands(out IEnumExplorerCommand? commands)
        {
            commands = new Enumerator(_commands);
            return S_OK;
        }
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class Command(WheelKind kind, string title, Action<WheelKind, IReadOnlyList<string>> open) : IExplorerCommand
    {
        public int GetTitle(IShellItemArray? items, out string? name)
        {
            name = title;
            return S_OK;
        }

        public int GetIcon(IShellItemArray? items, out string? icon)
        {
            icon = null;
            return E_NOTIMPL;
        }

        public int GetToolTip(IShellItemArray? items, out string? tip)
        {
            tip = null;
            return E_NOTIMPL;
        }

        public int GetCanonicalName(out Guid name)
        {
            name = Guid.Empty;
            return E_NOTIMPL;
        }

        public int GetState(IShellItemArray? items, bool okToBeSlow, out uint state)
        {
            state = EcsEnabled;
            return S_OK;
        }

        public int Invoke(IShellItemArray? items, IntPtr bindContext)
        {
            var paths = Paths(items);
            Log.Write($"explorer command: {kind} for {paths.Count} items");
            if (paths.Count > 0)
            {
                open(kind, paths);
            }
            return S_OK;
        }

        public int GetFlags(out uint flags)
        {
            flags = EcfDefault;
            return S_OK;
        }

        public int EnumSubCommands(out IEnumExplorerCommand? commands)
        {
            commands = null;
            return E_NOTIMPL;
        }
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class Enumerator(IExplorerCommand[] commands) : IEnumExplorerCommand
    {
        private int _next;

        public int Next(uint count, IExplorerCommand?[] items, out uint fetched)
        {
            fetched = 0;
            while (fetched < count && _next < commands.Length)
            {
                items[fetched++] = commands[_next++];
            }
            return fetched == count ? S_OK : S_FALSE;
        }

        public int Skip(uint count)
        {
            _next = (int)Math.Min(commands.Length, _next + count);
            return _next < commands.Length ? S_OK : S_FALSE;
        }

        public int Reset()
        {
            _next = 0;
            return S_OK;
        }

        public int Clone(out IEnumExplorerCommand? copy)
        {
            copy = new Enumerator(commands) { _next = _next };
            return S_OK;
        }
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class Factory(Func<object> create) : IClassFactory
    {
        public int CreateInstance(IntPtr outer, ref Guid riid, out IntPtr result)
        {
            result = IntPtr.Zero;
            if (outer != IntPtr.Zero)
            {
                return CLASS_E_NOAGGREGATION;
            }
            var unknown = Marshal.GetIUnknownForObject(create());
            try
            {
                return Marshal.QueryInterface(unknown, in riid, out result) == S_OK ? S_OK : E_NOINTERFACE;
            }
            finally
            {
                Marshal.Release(unknown);
            }
        }

        public int LockServer(bool lockServer) => S_OK;
    }

    [ComImport, ComVisible(true), Guid("00000001-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IClassFactory
    {
        [PreserveSig] int CreateInstance(IntPtr outer, ref Guid riid, out IntPtr result);
        [PreserveSig] int LockServer([MarshalAs(UnmanagedType.Bool)] bool lockServer);
    }

    [ComImport, ComVisible(true), Guid("A08CE4D0-FA25-44AB-B57C-C7B1C323E0B9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IExplorerCommand
    {
        [PreserveSig] int GetTitle(IShellItemArray? items, [MarshalAs(UnmanagedType.LPWStr)] out string? name);
        [PreserveSig] int GetIcon(IShellItemArray? items, [MarshalAs(UnmanagedType.LPWStr)] out string? icon);
        [PreserveSig] int GetToolTip(IShellItemArray? items, [MarshalAs(UnmanagedType.LPWStr)] out string? tip);
        [PreserveSig] int GetCanonicalName(out Guid name);
        [PreserveSig] int GetState(IShellItemArray? items, [MarshalAs(UnmanagedType.Bool)] bool okToBeSlow, out uint state);
        [PreserveSig] int Invoke(IShellItemArray? items, IntPtr bindContext);
        [PreserveSig] int GetFlags(out uint flags);
        [PreserveSig] int EnumSubCommands(out IEnumExplorerCommand? commands);
    }

    [ComImport, ComVisible(true), Guid("A88826F8-186F-4987-AADE-EA0CEF8FBFE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IEnumExplorerCommand
    {
        [PreserveSig] int Next(uint count, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IExplorerCommand?[] items, out uint fetched);
        [PreserveSig] int Skip(uint count);
        [PreserveSig] int Reset();
        [PreserveSig] int Clone(out IEnumExplorerCommand? copy);
    }

    /// <summary>Only the prefix up to GetItemAt is declared.</summary>
    [ComImport, ComVisible(true), Guid("B63EA76D-1F85-456F-A19C-48159EFA858B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellItemArray
    {
        [PreserveSig] int BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid riid, out IntPtr result);
        [PreserveSig] int GetPropertyStore(int flags, ref Guid riid, out IntPtr result);
        [PreserveSig] int GetPropertyDescriptionList(IntPtr keyType, ref Guid riid, out IntPtr result);
        [PreserveSig] int GetAttributes(int attribFlags, uint mask, out uint attributes);
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetItemAt(uint index, out IShellItem? item);
    }

    /// <summary>Only the prefix up to GetDisplayName is declared.</summary>
    [ComImport, ComVisible(true), Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid riid, out IntPtr result);
        [PreserveSig] int GetParent(out IShellItem? parent);
        [PreserveSig] int GetDisplayName(uint sigdnName, out IntPtr name);
    }
}
