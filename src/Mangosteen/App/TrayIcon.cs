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
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(new Forms.ToolStripMenuItem($"Mangosteen {Program.Version}") { Enabled = false });
        menu.Items.Add(new Forms.ToolStripMenuItem(L("Shift / Ctrl / Alt + right-click a file")) { Enabled = false });
        menu.Items.Add(new Forms.ToolStripSeparator());

        var choose = new Forms.ToolStripMenuItem(L("Choose files…"));
        choose.DropDownItems.Add(L("Convert…"), null, (_, _) => chooseFiles(WheelKind.Convert));
        choose.DropDownItems.Add(L("Tools…"), null, (_, _) => chooseFiles(WheelKind.Tools));
        choose.DropDownItems.Add(L("Actions…"), null, (_, _) => chooseFiles(WheelKind.Actions));
        menu.Items.Add(choose);
        menu.Items.Add(L("Float shelf"), null, (_, _) => toggleShelf());

        menu.Items.Add(L("Settings…"), null, (_, _) => settings());
        menu.Items.Add(L("Welcome…"), null, (_, _) => welcome());
        menu.Items.Add(L("Open log"), null, (_, _) => OpenLog());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(L("Quit Mangosteen"), null, (_, _) => quit());

        using var stream = typeof(TrayIcon).Assembly.GetManifestResourceStream("Mangosteen.ico")!;
        _icon = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize),
            Text = "Mangosteen",
            ContextMenuStrip = menu,
            Visible = true,
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
