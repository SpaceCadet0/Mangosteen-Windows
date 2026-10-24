using System.Windows;
using Mangosteen.Platform;
using static Mangosteen.Platform.Native;

namespace Mangosteen;

/// <summary>
/// <c>Mangosteen.exe --menu-self-test</c>: opens a tray menu shaped like the real one and clicks through it with
/// the real mouse (SetCursorPos and SendInput), the way a person would: an item two submenus deep ("Recent Results"
/// ▸ a result ▸ Open) and an item one submenu deep (Clear History). Run in CI, which has a desktop.
/// </summary>
internal static class MenuSelfTest
{
    private const uint MouseLeftDown = 0x0002, MouseLeftUp = 0x0004;

    public static int Run()
    {
        Log.Start(Program.Version + " (menu self-test)");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        TrayMenu.Trace = true;
        var code = 1;
        app.Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                var top = await Click(path: [0], expected: "first");
                var deep = await Click(path: [1, 0, 0], expected: "open");
                var shallow = await Click(path: [1, 1], expected: "clear");
                code = top && deep && shallow ? 0 : 1;
            }
            catch (Exception error)
            {
                Log.Error("menu self-test", error);
            }
            Log.Write($"menu self-test: {(code == 0 ? "passed" : "FAILED")}");
            TerminateProcess(GetCurrentProcess(), (uint)code);
        });
        return app.Run();
    }

    /// <summary>Opens the menu, rests the mouse on each item of <paramref name="path"/> in turn and clicks the last.</summary>
    private static async Task<bool> Click(int[] path, string expected)
    {
        var chosen = "";
        SetCursorPos(500, 500);
        var menu = new TrayMenu(light: true);
        menu.AddHeader("Mangosteen", "self-test");
        menu.AddItem("First", "", () => chosen = "first");
        menu.AddSubmenu("Recent Results", "", recent =>
        {
            recent.AddSubmenu("photo (1920×1080).jpg", "", result =>
            {
                result.AddHeader("Converted to JPG", "today");
                result.AddItem("Open", "", () => chosen = "open");
                result.AddItem("Delete Result", "", () => chosen = "delete");
            }, tooltip: "Converted to JPG · today");
            recent.AddItem("Clear History", "", () => chosen = "clear");
        });
        menu.AddItem("Quit", "", () => chosen = "quit");
        menu.ShowAtCursor();
        await Task.Delay(800);

        var current = menu;
        for (var i = 0; i < path.Length; i++)
        {
            var target = current.ItemCenter(path[i]);
            await Glide(target);
            await Task.Delay(500);
            if (i == path.Length - 1)
            {
                Press();
                await Task.Delay(800);
                break;
            }
            current = current.OpenSubmenu ?? throw new InvalidOperationException($"submenu {i + 1} of [{string.Join(", ", path)}] did not open");
            Log.Write($"menu self-test: submenu {i + 1} open");
        }
        Log.Write($"menu self-test: clicked [{string.Join(", ", path)}], chose \"{chosen}\", expected \"{expected}\"");
        return chosen == expected;
    }

    /// <summary>
    /// Moves the pointer there in small steps, as a hand would, so the menus see it pass: across first (into the
    /// submenu, which opens level with its item), then up or down to the item.
    /// </summary>
    private static async Task Glide(Point target)
    {
        GetCursorPos(out var start);
        const int steps = 12;
        for (var step = 1; step <= steps; step++)
        {
            SetCursorPos((int)(start.X + (target.X - start.X) * step / steps), start.Y);
            await Task.Delay(15);
        }
        for (var step = 1; step <= steps; step++)
        {
            SetCursorPos((int)target.X, (int)(start.Y + (target.Y - start.Y) * step / steps));
            await Task.Delay(15);
        }
    }

    private static void Press()
    {
        var inputs = new[] { MouseInput(MouseLeftDown), MouseInput(MouseLeftUp) };
        var sent = SendInput((uint)inputs.Length, inputs, System.Runtime.InteropServices.Marshal.SizeOf<INPUT>());
        GetCursorPos(out var at);
        Log.Write($"menu self-test: click at {at.X},{at.Y}, SendInput sent {sent}, window there 0x{WindowFromPoint(at):X}");
    }

    private static INPUT MouseInput(uint flags) => new() { type = INPUT_MOUSE, u = new INPUTUNION { mi = new MOUSEINPUT { dwFlags = flags } } };
}
