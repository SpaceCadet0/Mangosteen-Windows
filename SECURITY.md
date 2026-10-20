# Security policy

Mangosteen for Windows works offline: it does not send files or data anywhere, has no accounts and no telemetry. It
runs with the rights of the signed-in user and installs global low-level mouse and keyboard hooks, which it uses only
to notice Shift, Ctrl or Alt with a right-click in File Explorer or on the desktop, and Esc while a wheel is open.
The bundled FFmpeg is unpacked into `%LOCALAPPDATA%\Mangosteen\ffmpeg` and runs as a separate process.

## Supported versions

Only the [latest release](https://github.com/SpaceCadet0/Mangosteen-Windows/releases/latest) gets fixes.

## Reporting a vulnerability

Please report security problems privately through
[GitHub's private vulnerability reporting](https://github.com/SpaceCadet0/Mangosteen-Windows/security/advisories/new)
rather than in a public issue. Include the version, what you did and what happened. You will get an answer within a
week.

Problems in FFmpeg itself belong to the [FFmpeg project](https://ffmpeg.org/security.html); a new Mangosteen release
picks up their fixes.
