using System.Windows;
using System.Windows.Threading;
using Mangosteen.Platform;

namespace Mangosteen;

internal static class Program
{
    public static string Version { get; } =
        typeof(Program).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";

    [STAThread]
    private static int Main()
    {
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
}
