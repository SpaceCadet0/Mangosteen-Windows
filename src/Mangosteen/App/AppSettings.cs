using System.Text.Json;
using Mangosteen.Platform;
using Microsoft.Win32;

namespace Mangosteen;

/// <summary>Settings kept in %LOCALAPPDATA%\Mangosteen\settings.json.</summary>
internal sealed class AppSettings
{
    private static readonly string FilePath = Path.Combine(Log.Directory, "settings.json");
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>"system" or one of Loc.Languages ("en", "es", "ru", "de", "fr", "pt-BR", "zh-Hans", "ja").</summary>
    public string Language { get; set; } = "system";

    public bool WelcomeShown { get; set; }

    /// <summary>The tab the Crop window opens on: "crop" or "size".</summary>
    public string CropTab { get; set; } = "crop";

    /// <summary>"Mangosteen" in Explorer's right-click menu.</summary>
    public bool ExplorerMenu { get; set; } = true;

    /// <summary>The side of each wheel segment the user moved (segment id → front, back or hidden).</summary>
    public Dictionary<string, Core.SlicePlacement> WheelSides { get; set; } = [];

    /// <summary>The accent colour as #RRGGBB; empty for the brand colour.</summary>
    public string AccentHex { get; set; } = "";

    /// <summary>How opaque the wheel is, 0.4…1.</summary>
    public double WheelOpacity { get; set; } = 0.96;

    /// <summary>Blur what is behind the wheel (Acrylic).</summary>
    public bool WheelBlur { get; set; } = true;

    public Core.SaveMode SaveMode { get; set; } = Core.SaveMode.NextToOriginal;
    public string SaveFolder { get; set; } = "";
    public string NameTemplate { get; set; } = Core.OutputLocation.DefaultTemplate;
    public bool KeepDates { get; set; }
    public List<Core.ConversionPreset> Presets { get; set; } = [];
    /// <summary>The preset in effect; null for the standard settings.</summary>
    public Guid? ActivePreset { get; set; }

    public Core.Media.VideoCodec VideoCodec { get; set; } = Core.Media.VideoCodec.H264;
    public bool HardwareVideoEncoding { get; set; }

    /// <summary>The version whose "What's New" was shown; empty on a new installation.</summary>
    public string WhatsNewSeenVersion { get; set; } = "";

    /// <summary>The tab Settings opens on.</summary>
    public string SettingsTab { get; set; } = "general";

    public Core.ConversionPreset? Preset => ActivePreset is { } id ? Presets.FirstOrDefault(p => p.Id == id) : null;

    /// <summary>Hands the save policy, picture settings and video encoder to the jobs.</summary>
    public void Apply()
    {
        if (Preset is { } preset)
        {
            Core.OutputLocation.Policy = preset.Policy;
            Core.OutputLocation.Options = preset.Options;
        }
        else
        {
            Core.OutputLocation.Policy = new Core.OutputPolicy(SaveMode, SaveFolder.Length == 0 ? null : SaveFolder, NameTemplate, KeepDates);
            Core.OutputLocation.Options = Core.ConversionOptions.Standard;
        }
        Core.Media.VideoEncoding.Current = new Core.Media.VideoEncoding(VideoCodec, HardwareVideoEncoding);
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new AppSettings();
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
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
            Apply();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Log.Error("settings", error);
        }
    }

    public string EffectiveLanguage => Language == "system" ? Core.Loc.SystemLanguage() : Language;

    /// <summary>The startup task declared in the Store build's package manifest (build/msix/AppxManifest.xml).</summary>
    private const string StartupTaskId = "MangosteenStartup";

    /// <summary>
    /// Starts Mangosteen when you sign in to Windows: the Run key of the current user, or in the Store build the
    /// package's startup task (which Windows also shows in Settings › Apps › Startup).
    /// </summary>
    public static bool StartsWithWindows
    {
        get
        {
            if (Platform.Package.IsPackaged)
            {
                var task = Windows.ApplicationModel.StartupTask.GetAsync(StartupTaskId).AsTask().GetAwaiter().GetResult();
                return task.State is Windows.ApplicationModel.StartupTaskState.Enabled or Windows.ApplicationModel.StartupTaskState.EnabledByPolicy;
            }
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue("Mangosteen") is string;
        }
        set
        {
            if (Platform.Package.IsPackaged)
            {
                var task = Windows.ApplicationModel.StartupTask.GetAsync(StartupTaskId).AsTask().GetAwaiter().GetResult();
                if (value)
                {
                    var state = task.RequestEnableAsync().AsTask().GetAwaiter().GetResult();
                    Log.Write($"startup task: {state}");
                }
                else
                {
                    task.Disable();
                }
                return;
            }
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
