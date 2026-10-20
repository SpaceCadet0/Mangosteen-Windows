using System.Runtime.InteropServices;
using Mangosteen.Core;
using static Mangosteen.Platform.Native;

namespace Mangosteen.Platform;

/// <summary>What the hook saw when a modifier + right-button press landed on a shell file view.</summary>
internal sealed record ShellClick(
    WheelKind Kind,
    POINT DownPoint,
    IntPtr Target,
    IntPtr Root,
    string RootClass,
    string ClassChain,
    long DownTimestamp);

/// <summary>A circle in physical screen pixels, used to tell clicks on the wheel from clicks outside it.</summary>
internal sealed record ScreenCircle(double X, double Y, double Radius)
{
    public bool Contains(POINT p) => Math.Pow(p.X - X, 2) + Math.Pow(p.Y - Y, 2) <= Radius * Radius;
}

/// <summary>
/// Global low-level mouse and keyboard hooks, running on their own thread with its own message loop so
/// they never wait for the UI. Windows removes a low-level hook that is too slow, so the callbacks only
/// read cheap state and post everything else to the UI thread.
/// </summary>
/// <remarks>
/// Hold Shift, Ctrl or Alt and right-click a file in Explorer or on the desktop. The button-down passes
/// through so Explorer selects the item under the cursor; the matching button-up is swallowed so no
/// context menu opens, and <see cref="Triggered"/> fires instead. Every other click passes untouched.
/// </remarks>
internal sealed class InputHooks : IDisposable
{
    /// <summary>Marks input Mangosteen injects itself, so the hooks let it through.</summary>
    public static readonly IntPtr InjectedTag = new(0x4D414E47); // "MANG"

    private readonly HookProc _mouseProc;
    private readonly HookProc _keyboardProc;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _started = new();
    private uint _threadId;
    private IntPtr _mouseHook;
    private IntPtr _keyboardHook;
    private ShellClick? _armed;
    private volatile ScreenCircle? _wheel;

    /// <summary>Raised on the hook thread when a modifier + right-button press is armed. Handlers must only post.</summary>
    public event Action<ShellClick>? Armed;

    /// <summary>Raised on the hook thread after the matching button-up was swallowed. Handlers must only post.</summary>
    public event Action<ShellClick, POINT>? Triggered;

    /// <summary>Raised on the hook thread when a click lands outside the open wheel, or Esc is pressed.</summary>
    public event Action<string>? DismissWheel;

    public InputHooks()
    {
        _mouseProc = MouseProc;
        _keyboardProc = KeyboardProc;
        _thread = new Thread(Run) { IsBackground = true, Name = "Mangosteen input hooks", Priority = ThreadPriority.AboveNormal };
    }

    public bool Installed => _mouseHook != IntPtr.Zero;

    /// <summary>The open wheel, or null. Set from the UI thread.</summary>
    public ScreenCircle? Wheel
    {
        get => _wheel;
        set => _wheel = value;
    }

    public void Start()
    {
        _thread.Start();
        _started.Wait(TimeSpan.FromSeconds(5));
    }

    public void Dispose()
    {
        if (_threadId != 0)
        {
            PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            _thread.Join(TimeSpan.FromSeconds(2));
        }
    }

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        var module = GetModuleHandle(null);
        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, module, 0);
        var mouseError = Marshal.GetLastWin32Error();
        _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, module, 0);
        var keyboardError = Marshal.GetLastWin32Error();
        _started.Set();
        Log.Write($"Hooks installed: mouse={(_mouseHook != IntPtr.Zero ? "yes" : $"NO (error {mouseError})")}, " +
            $"keyboard={(_keyboardHook != IntPtr.Zero ? "yes" : $"NO (error {keyboardError})")}");

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        if (_mouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHook);
        }
        if (_keyboardHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_keyboardHook);
        }
    }

    private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
        {
            return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }
        var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
        if ((info.flags & LLMHF_INJECTED) != 0 && info.dwExtraInfo == InjectedTag)
        {
            return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        var message = (int)wParam;
        try
        {
            switch (message)
            {
                case WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN or WM_XBUTTONDOWN:
                    if (_wheel is { } wheel && !wheel.Contains(info.pt))
                    {
                        DismissWheel?.Invoke("click outside");
                    }
                    if (message == WM_RBUTTONDOWN)
                    {
                        _armed = TryArm(info.pt);
                        if (_armed is not null)
                        {
                            Armed?.Invoke(_armed);
                        }
                    }
                    break;

                case WM_RBUTTONUP when _armed is { } armed:
                    _armed = null;
                    Triggered?.Invoke(armed, info.pt);
                    return 1; // No WM_RBUTTONUP, so no context menu.
            }
        }
        catch (Exception)
        {
            // Never let an exception escape into the system hook chain.
            _armed = null;
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _wheel is not null && (int)wParam is WM_KEYDOWN or WM_SYSKEYDOWN)
        {
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if (info.vkCode == VK_ESCAPE)
            {
                DismissWheel?.Invoke("Esc");
                return 1;
            }
        }
        return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    /// <summary>Exactly one of Shift, Ctrl and Alt picks the wheel; any other combination is left alone.</summary>
    private static WheelKind? HeldWheel()
    {
        var shift = IsDown(VK_SHIFT);
        var control = IsDown(VK_CONTROL);
        var alt = IsDown(VK_MENU);
        if (IsDown(VK_LWIN) || IsDown(VK_RWIN))
        {
            return null;
        }
        return (shift, control, alt) switch
        {
            (true, false, false) => WheelKind.Convert,
            (false, true, false) => WheelKind.Tools,
            (false, false, true) => WheelKind.Actions,
            _ => null,
        };
    }

    private static ShellClick? TryArm(POINT pt)
    {
        if (HeldWheel() is not { } kind)
        {
            return null;
        }
        var target = WindowFromPoint(pt);
        if (target == IntPtr.Zero)
        {
            return null;
        }
        // Walk up to the top-level window. The click must be inside a shell file view (SHELLDLL_DefView),
        // so the navigation pane, address bar and ribbon keep their own right-click.
        var chain = new List<string>(12);
        var insideDefView = false;
        var window = target;
        var root = target;
        for (var depth = 0; window != IntPtr.Zero && depth < 24; depth++)
        {
            var name = ClassName(window);
            chain.Add(name);
            insideDefView |= name == "SHELLDLL_DefView";
            root = window;
            window = GetAncestor(window, GA_PARENT);
            if (window != IntPtr.Zero && ClassName(window) == "#32769") // the desktop window itself
            {
                break;
            }
        }
        var rootClass = chain[^1];
        if (!insideDefView || rootClass is not ("CabinetWClass" or "Progman" or "WorkerW"))
        {
            return null;
        }
        return new ShellClick(kind, pt, target, root, rootClass, string.Join(" < ", chain), Environment.TickCount64);
    }
}
