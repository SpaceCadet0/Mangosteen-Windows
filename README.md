*English* · [Русский](README.ru.md)

# Mangosteen for Windows

The Windows sibling of [Mangosteen](https://github.com/SpaceCadet0/Mangosteen), the macOS menu-bar app that converts and edits files from a wheel. Hold a modifier key, right-click a file in File Explorer or on the desktop, and pick what to do from a round wheel at the cursor. The result is written next to the original. Everything runs offline.

This is an early prototype (version 0.1). The two repositories share no code; each has its own releases and CI.

## Key map

| Hold | Right-click a file | Wheel |
| --- | --- | --- |
| Shift | in File Explorer or on the desktop | Convert |
| Ctrl | in File Explorer or on the desktop | Tools |
| Alt | in File Explorer or on the desktop | Actions |

Right-clicks without one of these keys, and right-clicks anywhere else, open the usual context menu.

Pick a segment with a left click. Esc or a click outside the wheel closes it. Picked the wrong wheel? While it is open, press Shift, Ctrl or Alt to switch to that wheel for the same files. As in Explorer, right-clicking a selected file uses the whole selection, and right-clicking a file that is not selected selects just that file.

For other apps, use the tray icon: **Choose files…** opens a file dialog and then shows the wheel at the cursor.

## What works in this prototype

- Convert images between PNG, JPG, TIFF, BMP and GIF. JPG, BMP and GIF have no transparency, so transparent areas become white. Multi-page TIFF and animated GIF inputs are converted from their first frame. WebP, HEIC and AVIF can be read when the matching codec from the Microsoft Store is installed.
- Results never overwrite anything: `photo.jpg`, then `photo 2.jpg`, `photo 3.jpg`. The original is never changed.
- Actions: **Path** copies the full paths of the files.
- The other Tools and Actions segments are shown, but their features arrive in later versions.

## Download

There is no installer yet. Every push builds a self-contained `Mangosteen.exe` (Windows 10 and 11, x64, no .NET install needed):

1. Open the [Actions tab](https://github.com/SpaceCadet0/Mangosteen-Windows/actions/workflows/build.yml) and click the latest green run.
2. Under **Artifacts**, download **Mangosteen-win-x64** (you need to be signed in to GitHub) and unzip it.
3. Run `Mangosteen.exe`. The build is not signed, so SmartScreen may warn you: click **More info**, then **Run anyway**.

Mangosteen lives in the notification area (click the arrow next to the clock if you do not see the icon). Quit it from the tray menu.

## Troubleshooting

Mangosteen writes a log to `%LOCALAPPDATA%\Mangosteen\mangosteen.log` (**Open log** in the tray menu). It records every right-click Mangosteen handles: the window under the cursor, the selected files and timings. Attach it when reporting a problem.

## Building

Requires the .NET 8 SDK. The app targets Windows, but it also compiles on macOS and Linux (`EnableWindowsTargeting`):

```
dotnet build Mangosteen.sln
dotnet test tests/Mangosteen.Core.Tests
dotnet publish src/Mangosteen/Mangosteen.csproj -c Release -r win-x64 -o publish
```

The image conversion tests (`tests/Mangosteen.Windows.Tests`) need Windows and run in CI.

## License

[MIT](LICENSE). Copyright (c) 2026 Igor Rusakov (SpaceCadet).
