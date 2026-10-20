*English* · [Русский](README.ru.md)

# Mangosteen for Windows

The Windows sibling of [Mangosteen](https://github.com/SpaceCadet0/Mangosteen), the macOS menu-bar app that converts and edits files from a wheel. Hold a modifier key, right-click a file in File Explorer or on the desktop, and pick what to do from a round wheel at the cursor. The result is written next to the original. Everything runs offline.

This is an early version (0.2). The two repositories share no code; each has its own releases and CI.

## Key map

| Hold | Right-click a file | Wheel |
| --- | --- | --- |
| Shift | in File Explorer or on the desktop | Convert |
| Ctrl | in File Explorer or on the desktop | Tools |
| Alt | in File Explorer or on the desktop | Actions |

Right-clicks without one of these keys, and right-clicks anywhere else, open the usual context menu.

Pick a segment with a left click. Esc or a click outside the wheel closes it. Picked the wrong wheel? While it is open, press Shift, Ctrl or Alt to switch to that wheel for the same files. As in Explorer, right-clicking a selected file uses the whole selection, and right-clicking a file that is not selected selects just that file.

For other apps, use the tray icon: **Choose files…** opens a file dialog and then shows the wheel at the cursor.

## What it does

### Convert (Shift)

| Kind | Formats |
| --- | --- |
| Images | PNG, JPG, WebP, TIFF, BMP, GIF, PDF; also reads ICO, and WebP, HEIC and AVIF when their Microsoft Store codecs are installed |
| PDF | to PNG or JPG (a PDF with several pages becomes a folder of pages), TXT, Markdown, RTF, DOCX, HTML |
| Documents | DOCX, ODT, RTF, TXT, Markdown, HTML into each other and into PDF |
| Subtitles | SRT and VTT into each other and into TXT |
| Tables | CSV, TSV, XLSX into each other (a workbook with several sheets becomes a folder of tables) |
| Archives | ZIP, TAR, TAR.GZ into each other; folders into archives |

Results never overwrite anything: `photo.jpg`, then `photo 2.jpg`, `photo 3.jpg`. The original is never changed.
Text files in the old Windows Cyrillic encoding (Windows-1251) are read correctly. Longer jobs show a small
progress panel with a Stop button; click the final notification to see the result in Explorer.

Not yet on Windows: HEIC and AVIF output, animated WebP, video and audio (they arrive with ffmpeg), and the old
binary `.doc` format. Documents keep headings, paragraphs, lists, bold, italic, underline, links and code; images
and tables inside documents are not carried over.

### Tools (Ctrl)

- **Extract** an archive into a folder next to it.
- **PDF from images**: one PDF with a page per image.
- **Merge** several PDFs into one.

The other tools (compress, crop, editor, annotate and more) are shown greyed out and arrive in the next versions.

### Actions (Alt)

- **Float** puts the files on a floating shelf. Drag them from the shelf into any app later. Dragging out copies;
  switch the shelf to Move to move the originals instead. The shelf is also in the tray menu.
- **Copy** puts the files on the clipboard, ready to paste into a folder or a message.
- **Move** asks for a folder and moves the files there.
- **Path** copies the full paths.
- **Terminal** opens the folder in Windows Terminal (or PowerShell).

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

[MIT](LICENSE). Copyright (c) 2026 Igor Rusakov (SpaceCadet). Open-source components and their licenses are
listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
