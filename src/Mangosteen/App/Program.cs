using System.Windows;
using System.Windows.Threading;
using Mangosteen.Platform;

namespace Mangosteen;

internal static class Program
{
    public static string Version { get; } =
        typeof(Program).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length >= 3 && args[0] == "--snapshot")
        {
            return RunSnapshot(args[1], args[2], args[3..]);
        }
        using var instance = new Mutex(initiallyOwned: true, @"Local\Mangosteen.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            return 0;
        }

        Log.Start(Version);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) =>
        {
            Log.Error("unhandled", e.Exception);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write($"FATAL {e.ExceptionObject}");

        using var controller = new AppController(app.Dispatcher);
        app.Dispatcher.BeginInvoke(controller.Start, DispatcherPriority.Normal);
        return app.Run();
    }

    /// <summary>Renders one tool window to a PNG and exits; no tray icon, no hooks.</summary>
    private static int RunSnapshot(string id, string output, IReadOnlyList<string> files)
    {
        Log.Start(Version + " (snapshot)");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) =>
        {
            Log.Error("snapshot", e.Exception);
            e.Handled = true;
            app.Shutdown(1);
        };
        app.Dispatcher.BeginInvoke(() => Tools.ToolRegistry.Snapshot(id, output, files));
        return app.Run();
    }
}
