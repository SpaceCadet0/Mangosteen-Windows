using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mangosteen.Core;
using Mangosteen.Platform;
using Mangosteen.Tools;

namespace Mangosteen;

/// <summary>
/// "Check for Updates…": one request to GitHub for the latest release, only when the person asks; nothing is
/// downloaded or installed. The answer is shown in a small window.
/// </summary>
internal static class UpdateCheck
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
        // GitHub's API refuses requests without a User-Agent.
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"Mangosteen-Windows/{Program.Version}");
        return client;
    }

    public static async void Run()
    {
        UpdateDialog dialog;
        try
        {
            var json = await Http.GetStringAsync(Updates.LatestReleaseApi);
            var (tag, url) = Updates.ParseRelease(json);
            Log.Write($"update check: latest {tag}, this {Program.Version}");
            dialog = Updates.Compare(tag, Program.Version) > 0
                ? UpdateDialog.Available(tag.TrimStart('v', 'V'), url)
                : UpdateDialog.UpToDate();
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException
            or InvalidDataException or KeyNotFoundException)
        {
            Log.Error("update check", error);
            dialog = UpdateDialog.Failed();
        }
        dialog.Present();
    }
}

/// <summary>The answer of an update check, with the app's icon.</summary>
internal sealed class UpdateDialog : ToolWindow
{
    private readonly string? _open;

    private UpdateDialog(string title, string text, string action, string? open) : base("Mangosteen", [], action)
    {
        _open = open;
        StatusText = "";
        if (open is null)
        {
            HideCancel();
        }
        else
        {
            CancelTitle = L("Later");
        }
        var row = new DockPanel { Width = 440, Margin = new Thickness(4, 4, 4, 0) };
        var icon = new Image { Width = 56, Height = 56, Margin = new Thickness(0, 0, 18, 0), VerticalAlignment = VerticalAlignment.Top };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        try
        {
            var decoder = BitmapDecoder.Create(new Uri("pack://application:,,,/Assets/Mangosteen.ico"), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            icon.Source = decoder.Frames.MaxBy(frame => frame.PixelWidth);
        }
        catch (Exception error) when (error is IOException or UriFormatException or NotSupportedException)
        {
        }
        row.Children.Add(icon);
        var texts = new StackPanel();
        texts.Children.Add(new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        texts.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.8, Margin = new Thickness(0, 6, 0, 0) });
        row.Children.Add(texts);
        Body = row;
        ActionEnabled = true;
    }

    /// <summary>A newer release: the zip from GitHub, or for the Store build, Microsoft Store's updates page.</summary>
    public static UpdateDialog Available(string version, string page) => Package.IsPackaged
        ? new(string.Format(L("Version {0} is available"), version),
            string.Format(L("You have version {0}. Update Mangosteen in Microsoft Store."), Program.Version),
            L("Open Microsoft Store"), "ms-windows-store://downloadsandupdates")
        : new(string.Format(L("Version {0} is available"), version),
            string.Format(L("You have version {0}. Download the new one from the release page and replace Mangosteen.exe with it."), Program.Version),
            L("Download"), page);

    public static UpdateDialog UpToDate() =>
        new(L("You're up to date"), string.Format(L("Mangosteen {0} is the newest version."), Program.Version), L("OK"), null);

    public static UpdateDialog Failed() =>
        new(L("Couldn't check for updates"), L("Check your internet connection and try again."), L("OK"), null);

    protected override void Run()
    {
        Close();
        if (_open is { } target)
        {
            try
            {
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                Log.Error("open release page", error);
            }
        }
    }
}
