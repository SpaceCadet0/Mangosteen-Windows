# Downloads the pinned FFmpeg build, checks its SHA-256 and repacks the parts Mangosteen needs
# (ffmpeg.exe, ffprobe.exe, the DLLs and the licence) into src/Mangosteen/Media/ffmpeg.zip.
$ErrorActionPreference = 'Stop'
$pin = @{}
Get-Content (Join-Path $PSScriptRoot 'ffmpeg.pin') | Where-Object { $_ -match '^(\w+)=(.+)$' } | ForEach-Object { $pin[$Matches[1]] = $Matches[2].Trim() }
$cache = Join-Path $PSScriptRoot '..' 'ffmpeg-cache'
New-Item -ItemType Directory -Force $cache | Out-Null
$zip = Join-Path $cache 'ffmpeg.zip'
if (-not (Test-Path $zip) -or (Get-FileHash $zip -Algorithm SHA256).Hash -ne $pin.sha256) {
    Write-Host "Downloading $($pin.url)"
    try {
        Invoke-WebRequest $pin.url -OutFile $zip
    } catch {
        throw "The pinned FFmpeg build could not be downloaded ($($_.Exception.Message)). Update build/ffmpeg.pin."
    }
}
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash
if ($hash -ne $pin.sha256) { throw "FFmpeg checksum mismatch: $hash" }

$work = Join-Path $cache 'unpacked'
Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
Expand-Archive $zip $work
$root = Get-ChildItem $work -Directory | Select-Object -First 1
$pack = Join-Path $cache 'pack'
Remove-Item -Recurse -Force $pack -ErrorAction SilentlyContinue
New-Item -ItemType Directory $pack | Out-Null
Copy-Item (Join-Path $root.FullName 'bin/ffmpeg.exe'), (Join-Path $root.FullName 'bin/ffprobe.exe') $pack
Copy-Item (Join-Path $root.FullName 'bin/*.dll') $pack
Copy-Item (Join-Path $root.FullName 'LICENSE.txt') (Join-Path $pack 'FFmpeg-LICENSE.txt')
Set-Content (Join-Path $pack 'FFmpeg-VERSION.txt') "$($root.Name)`nSource: https://ffmpeg.org/download.html`nBuild scripts: https://github.com/BtbN/FFmpeg-Builds`nArchive: $($pin.url)"
$target = Join-Path $PSScriptRoot '..' 'src/Mangosteen/Media/ffmpeg.zip'
Remove-Item $target -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $pack '*') -DestinationPath $target -CompressionLevel Optimal
Set-Content ($target -replace 'ffmpeg\.zip$', 'ffmpeg.sha256') (Get-FileHash $target -Algorithm SHA256).Hash.ToLowerInvariant() -NoNewline
Write-Host ("Packed {0} ({1:N1} MB)" -f $target, ((Get-Item $target).Length / 1MB))
