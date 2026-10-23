using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Mangosteen.Core;
using Mangosteen.Tools;

namespace Mangosteen;

/// <summary>Language and starting with Windows. A new language takes effect after a quick restart.</summary>
internal sealed class SettingsWindow : ToolWindow
{
    private readonly AppSettings _settings;
    private readonly ComboBox _language = new();
    private readonly CheckBox _autostart = new() { Content = L("Start Mangosteen when I sign in to Windows") };
    private readonly CheckBox _explorer = new() { Content = L("Show Mangosteen in Explorer's right-click menu"), Margin = new Thickness(0, 8, 0, 0) };

    public SettingsWindow(AppSettings settings) : base(L("Settings"), [], L("Save"))
    {
        _settings = settings;
        _language.Items.Add(L("Same as Windows"));
        foreach (var (_, name) in Loc.Languages)
        {
            _language.Items.Add(name);
        }
        var index = Loc.Languages.ToList().FindIndex(l => l.Code == settings.Language);
        _language.SelectedIndex = index < 0 ? 0 : index + 1;
        _autostart.IsChecked = AppSettings.StartsWithWindows;
        _explorer.IsChecked = settings.ExplorerMenu;
        StatusText = $"Mangosteen {Program.Version}";
        var stack = new StackPanel { Width = 460 };
        stack.Children.Add(Row(L("Language"), _language));
        stack.Children.Add(_autostart);
        stack.Children.Add(_explorer);
        if (!Platform.Package.IsPackaged)
        {
            stack.Children.Add(Note(L("On Windows 11 it is under “Show more options”.")));
        }
        stack.Children.Add(new Border { Height = 10 });
        stack.Children.Add(Note(L("The keys: hold Shift for Convert, Ctrl for Tools, Alt for Actions, and right-click a file.")));
        Body = stack;
        ActionEnabled = true;
    }

    protected override void Run()
    {
        var language = _language.SelectedIndex <= 0 ? "system" : Loc.Languages[_language.SelectedIndex - 1].Code;
        var changed = language != _settings.Language;
        _settings.Language = language;
        _settings.ExplorerMenu = _explorer.IsChecked == true;
        _settings.Save();
        // The Store build's menu is part of the package; there the setting only hides it.
        if (!Platform.Package.IsPackaged)
        {
            if (_settings.ExplorerMenu)
            {
                ExplorerMenu.Register();
            }
            else
            {
                ExplorerMenu.Unregister();
            }
        }
        try
        {
            AppSettings.StartsWithWindows = _autostart.IsChecked == true;
        }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Platform.Log.Error("autostart", error);
        }
        Close();
        if (changed && Environment.ProcessPath is { } path)
        {
            // Every window and menu is built in the new language after a restart.
            Process.Start(new ProcessStartInfo(path, "--restart") { UseShellExecute = false });
            Application.Current.Shutdown();
        }
    }
}
