using System.Diagnostics;
using Mangosteen.Core;
using Mangosteen.Platform;
using Forms = System.Windows.Forms;

namespace Mangosteen;

/// <summary>The notification-area icon and its menu. Mangosteen has no main window.</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;

    public TrayIcon(Action<WheelKind> chooseFiles, Action quit)
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(new Forms.ToolStripMenuItem($"Mangosteen {Program.Version}") { Enabled = false });
        menu.Items.Add(new Forms.ToolStripMenuItem("Shift / Ctrl / Alt + right-click a file") { Enabled = false });
        menu.Items.Add(new Forms.ToolStripSeparator());

        var choose = new Forms.ToolStripMenuItem("Choose files…");
        choose.DropDownItems.Add("Convert…", null, (_, _) => chooseFiles(WheelKind.Convert));
        choose.DropDownItems.Add("Tools…", null, (_, _) => chooseFiles(WheelKind.Tools));
        choose.DropDownItems.Add("Actions…", null, (_, _) => chooseFiles(WheelKind.Actions));
        menu.Items.Add(choose);

        menu.Items.Add("Open log", null, (_, _) => OpenLog());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit Mangosteen", null, (_, _) => quit());

        using var stream = typeof(TrayIcon).Assembly.GetManifestResourceStream("Mangosteen.ico")!;
        _icon = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize),
            Text = "Mangosteen",
            ContextMenuStrip = menu,
            Visible = true,
        };
    }

    public void Notify(string title, string text, bool error = false) =>
        _icon.ShowBalloonTip(4000, title, text, error ? Forms.ToolTipIcon.Error : Forms.ToolTipIcon.None);

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
