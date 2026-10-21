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
        if (args.Length == 1 && args[0] == "--media-self-test")
        {
            Log.Start(Version + " (media self-test)");
            return Media.FFmpeg.SelfTest();
        }
        if (args.Length >= 3 && args[0] == "--snapshot")
        {
            return RunSnapshot(args[1], args[2], args[3..]);
        }
        // From Explorer's right-click menu: give the path to the running Mangosteen, or start it with the request.
        var explorer = ExplorerMenu.Parse(args);
        if (explorer is { } request && ExplorerMenu.Send(request.Kind, request.Path))
        {
            return 0;
        }
        using var instance = new Mutex(initiallyOwned: false, @"Local\Mangosteen.SingleInstance");
        // After a language change the old copy is still closing; wait for it.
        var wait = args.Contains("--restart") ? TimeSpan.FromSeconds(15) : TimeSpan.Zero;
        try
        {
            if (!instance.WaitOne(wait))
            {
                // Another copy is starting for the same Explorer selection; it is listening a moment later.
                if (explorer is { } late)
                {
                    Thread.Sleep(1500);
                    ExplorerMenu.Send(late.Kind, late.Path);
                }
                return 0;
            }
        }
        catch (AbandonedMutexException)
        {
        }
        var settings = AppSettings.Load();
        Core.Loc.SetLanguage(Environment.GetEnvironmentVariable("MANGOSTEEN_LANGUAGE") ?? settings.EffectiveLanguage);

        Log.Start(Version);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) =>
        {
            Log.Error("unhandled", e.Exception);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write($"FATAL {e.ExceptionObject}");

        using var controller = new AppController(app.Dispatcher, settings);
        app.Dispatcher.BeginInvoke(controller.Start, DispatcherPriority.Normal);
        if (explorer is { } first)
        {
            app.Dispatcher.BeginInvoke(() => controller.OpenFromExplorer(first.Kind, first.Path), DispatcherPriority.Background);
        }
        return app.Run();
    }

    /// <summary>Renders one tool window to a PNG and exits; no tray icon, no hooks.</summary>
    private static int RunSnapshot(string id, string output, IReadOnlyList<string> files)
    {
        Core.Loc.SetLanguage(Environment.GetEnvironmentVariable("MANGOSTEEN_LANGUAGE") ?? "en");
        Core.FormatCatalog.MediaAvailable = Media.FFmpeg.IsBundled;
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
