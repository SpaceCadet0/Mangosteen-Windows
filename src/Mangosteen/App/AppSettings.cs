using System.Text.Json;
using Mangosteen.Platform;
using Microsoft.Win32;

namespace Mangosteen;

/// <summary>Settings kept in %LOCALAPPDATA%\Mangosteen\settings.json.</summary>
internal sealed class AppSettings
{
    private static readonly string FilePath = Path.Combine(Log.Directory, "settings.json");
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>"system", "en", "es" or "ru".</summary>
    public string Language { get; set; } = "system";

    public bool WelcomeShown { get; set; }

    /// <summary>"Mangosteen" in Explorer's right-click menu.</summary>
    public bool ExplorerMenu { get; set; } = true;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
            }
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Error("settings", error);
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Log.Directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Log.Error("settings", error);
        }
    }

    public string EffectiveLanguage => Language == "system" ? Core.Loc.SystemLanguage() : Language;

    /// <summary>Starts Mangosteen when you sign in to Windows (the Run key of the current user).</summary>
    public static bool StartsWithWindows
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue("Mangosteen") is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value && Environment.ProcessPath is { } path)
            {
                key.SetValue("Mangosteen", $"\"{path}\"");
            }
            else
            {
                key.DeleteValue("Mangosteen", throwOnMissingValue: false);
            }
        }
    }
}
