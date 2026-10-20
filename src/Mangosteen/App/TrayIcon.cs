using System.Diagnostics;
using Mangosteen.Core;
using Mangosteen.Platform;
using Forms = System.Windows.Forms;

namespace Mangosteen;

/// <summary>The notification-area icon and its menu. Mangosteen has no main window.</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;

    private string? _reveal;

    public TrayIcon(Action<WheelKind> chooseFiles, Action toggleShelf, Action settings, Action welcome, Action quit)
    {
        // The menu is Mangosteen's own window (rounded, themed); the WinForms menu cannot be styled like that.
        void ShowMenu()
        {
            var menu = new TrayMenu();
            menu.AddHeader($"Mangosteen {Program.Version}", L("Shift / Ctrl / Alt + right-click a file"));
            menu.AddSeparator();
            menu.AddCaption(L("Choose files…"));
            menu.AddItem(L("Convert…"), "\uE8AB", () => chooseFiles(WheelKind.Convert), indent: true);
            menu.AddItem(L("Tools…"), "\uE90F", () => chooseFiles(WheelKind.Tools), indent: true);
            menu.AddItem(L("Actions…"), "\uE8B7", () => chooseFiles(WheelKind.Actions), indent: true);
            menu.AddSeparator();
            menu.AddItem(L("Float shelf"), "\uE718", toggleShelf);
            menu.AddItem(L("Settings…"), "\uE713", settings);
            menu.AddItem(L("Welcome…"), "\uE946", welcome);
            menu.AddItem(L("Open log"), "\uE7C3", OpenLog);
            menu.AddSeparator();
            menu.AddItem(L("Quit Mangosteen"), "\uE7E8", quit);
            menu.ShowAtCursor();
        }

        using var stream = typeof(TrayIcon).Assembly.GetManifestResourceStream("Mangosteen.ico")!;
        _icon = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize),
            Text = "Mangosteen",
            Visible = true,
        };
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button is Forms.MouseButtons.Right or Forms.MouseButtons.Left)
            {
                ShowMenu();
            }
        };
        // Clicking a "done" notification selects the result in Explorer.
        _icon.BalloonTipClicked += (_, _) =>
        {
            if (_reveal is { } path && (File.Exists(path) || Directory.Exists(path)))
            {
                FileActions.Reveal(path);
            }
        };
    }

    /// <param name="reveal">A file or folder to select in Explorer when the notification is clicked.</param>
    public void Notify(string title, string text, bool error = false, string? reveal = null)
    {
        _reveal = reveal;
        _icon.ShowBalloonTip(4000, title, text.Length == 0 ? " " : text, error ? Forms.ToolTipIcon.Error : Forms.ToolTipIcon.None);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }

    private static void OpenLog()
    {
        try
        {
            if (!File.Exists(Log.Path))
            {
                Log.Write("log opened");
            }
            Process.Start(new ProcessStartInfo(Log.Path) { UseShellExecute = true });
        }
        catch (Exception error)
        {
            Log.Error("open log", error);
        }
    }
}
