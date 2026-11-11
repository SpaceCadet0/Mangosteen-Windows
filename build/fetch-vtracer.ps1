# Downloads the pinned vtracer build, checks its SHA-256 and puts vtracer.exe where Mangosteen.csproj embeds it
# (src/Mangosteen/Tools/vtracer.exe). Without it "To vector" traces colours with Mangosteen's own tracer.
$ErrorActionPreference = 'Stop'
$pin = @{}
Get-Content (Join-Path $PSScriptRoot 'vtracer.pin') | Where-Object { $_ -match '^(\w+)=(.+)$' } | ForEach-Object { $pin[$Matches[1]] = $Matches[2].Trim() }
$cache = Join-Path $PSScriptRoot '..' 'vtracer-cache'
New-Item -ItemType Directory -Force $cache | Out-Null
$zip = Join-Path $cache 'vtracer.zip'
if (-not (Test-Path $zip) -or (Get-FileHash $zip -Algorithm SHA256).Hash -ne $pin.sha256) {
    Write-Host "Downloading $($pin.url)"
    Invoke-WebRequest $pin.url -OutFile $zip
}
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash
if ($hash -ne $pin.sha256) { throw "vtracer checksum mismatch: $hash" }
$work = Join-Path $cache 'unpacked'
Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
Expand-Archive $zip $work
$exe = Get-ChildItem $work -Recurse -Filter 'vtracer.exe' | Select-Object -First 1
if (-not $exe) { throw 'vtracer.exe is not in the archive' }
$target = Join-Path $PSScriptRoot '..' 'src/Mangosteen/Tools/vtracer.exe'
Copy-Item $exe.FullName $target -Force
& $target --version
Write-Host ("vtracer.exe: {0:N2} MB" -f ((Get-Item $target).Length / 1MB))
