*English* · [Русский](README.ru.md)

# Mangosteen for Windows

The Windows sibling of [Mangosteen](https://github.com/SpaceCadet0/Mangosteen), the macOS menu-bar app that converts and edits files from a wheel. Hold a modifier key, right-click a file in File Explorer or on the desktop, and pick what to do from a round wheel at the cursor. The result is written next to the original. Everything runs offline.

This is an early version (0.4). The two repositories share no code; each has its own releases and CI.

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
| Images | PNG, JPG, WebP, TIFF, AVIF, BMP, GIF, PDF; also reads ICO, and WebP, HEIC and AVIF when their Microsoft Store codecs are installed |
| PDF | to PNG or JPG (a PDF with several pages becomes a folder of pages), TXT, Markdown, RTF, DOCX, HTML |
| Documents | DOCX, ODT, RTF, TXT, Markdown, HTML into each other and into PDF |
| Subtitles | SRT and VTT into each other and into TXT |
| Tables | CSV, TSV, XLSX into each other (a workbook with several sheets becomes a folder of tables) |
| Archives | ZIP, TAR, TAR.GZ into each other; folders into archives |
| Video | MP4, MOV, MKV, WebM, AVI, WMV into each other, GIF and MP3 |
| Audio | MP3, M4A, WAV, FLAC, OGG, Opus, AIFF, WMA |

When the target container accepts the original streams, video is copied without re-encoding, so it is instant and
lossless.

Results never overwrite anything: `photo.jpg`, then `photo 2.jpg`, `photo 3.jpg`. The original is never changed.
Text files in the old Windows Cyrillic encoding (Windows-1251) are read correctly. Longer jobs show a small
progress panel with a Stop button; click the final notification to see the result in Explorer.

Not yet on Windows: HEIC output, animated WebP and the old binary `.doc` format. Documents keep headings, paragraphs, lists, bold, italic, underline, links and code; images
and tables inside documents are not carried over.

### Tools (Ctrl)

Tools open a window with a live preview; close it and nothing is changed. Results are saved next to the original
as `photo (cropped).jpg`, `photo (edited).jpg` and so on.

- **Compress**: presets, quality or a target file size, a size limit and the format (JPG or WebP); works for PDF too.
- **Crop**: a free frame, fixed proportions or an exact size in pixels.
- **Editor**: light, colour, detail and effects. Hold the preview to see the original; double-click a slider to reset it.
- **Annotate**: arrows, rectangles, ovals, text, numbered steps, pencil, highlighter, magnifier and pictures (Ctrl+V pastes one).
- **Hide**: finds faces and text on the device and covers them with a fill, a blur or pixels; areas can be drawn by hand.
- **Background**: puts a photo on a colour, gradient, image or a blurred copy of itself, with margin, corners and shadow.
- **Collage**: a grid, a row, a column or a featured photo.
- **Metadata**: see everything, edit common fields, remove the location or all metadata (without re-encoding JPEG).
- **Pages** (PDF): reorder, rotate, duplicate and delete pages.
- **Extract** an archive, **PDF from images**, **Merge** PDFs.

Video and audio tools work on a timeline with frames or the waveform:

- **Trim** and **Split** (fast mode copies without re-encoding), **Frames** saves stills as PNG.
- **Speed** with or without keeping the pitch, **Volume** (EBU R128 loudness normalisation, listen first), **Channels**.
- **Compress** video and audio by quality, height or a target size, with an estimate; **Join** clips; **Crop video**.
- **Bleep** words and listen before saving; **Hide** areas of a video during chosen moments; **Visualizer** turns a song into an MP4.

### Actions (Alt)

- **Float** puts the files on a floating shelf with real thumbnails of photos and videos. Drag them from the shelf into any app later. Dragging out copies;
  switch the shelf to Move to move the originals instead. The shelf is also in the tray menu.
- **Copy** puts the files on the clipboard, ready to paste into a folder or a message.
- **Move** asks for a folder and moves the files there.
- **Path** copies the full paths.
- **Terminal** opens the folder in Windows Terminal (or PowerShell).

## Download

There is no installer yet. Every push builds a self-contained `Mangosteen.exe` (Windows 10 and 11, x64, no .NET install needed). It is about 170 MB because it carries FFmpeg, which is unpacked into `%LOCALAPPDATA%\Mangosteen\ffmpeg` on first use:

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

[MIT](LICENSE). Copyright (c) 2026 Igor Rusakov (SpaceCadet). The exe bundles [FFmpeg](https://ffmpeg.org) (GPL v3) as a
separate program. Open-source components and their licenses are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
