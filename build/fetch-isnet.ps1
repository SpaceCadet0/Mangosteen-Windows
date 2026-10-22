# Downloads the pinned IS-Net model, checks its SHA-256, converts it to half precision (needs Python with
# onnx==1.23.2 and onnxruntime==1.31.0) and writes src/Mangosteen/Models/isnet.onnx and its hash.
$ErrorActionPreference = 'Stop'
$pin = @{}
Get-Content (Join-Path $PSScriptRoot 'isnet.pin') | Where-Object { $_ -match '^(\w+)=(.+)$' } | ForEach-Object { $pin[$Matches[1]] = $Matches[2].Trim() }
$cache = Join-Path $PSScriptRoot '..' 'isnet-cache'
New-Item -ItemType Directory -Force $cache | Out-Null
$source = Join-Path $cache 'isnet-general-use.onnx'
if (-not (Test-Path $source) -or (Get-FileHash $source -Algorithm SHA256).Hash -ne $pin.sha256) {
    Write-Host "Downloading $($pin.url)"
    try {
        Invoke-WebRequest $pin.url -OutFile $source
    } catch {
        throw "The pinned IS-Net model could not be downloaded ($($_.Exception.Message)). Update build/isnet.pin."
    }
}
$hash = (Get-FileHash $source -Algorithm SHA256).Hash
if ($hash -ne $pin.sha256) { throw "IS-Net checksum mismatch: $hash" }

$half = Join-Path $cache 'isnet-fp16.onnx'
if (-not (Test-Path $half)) {
    python (Join-Path $PSScriptRoot 'isnet-fp16.py') $source $half
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $half)) { throw "The IS-Net conversion failed" }
}
$target = Join-Path $PSScriptRoot '..' 'src/Mangosteen/Models/isnet.onnx'
New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
Copy-Item $half $target -Force
Set-Content ($target -replace 'isnet\.onnx$', 'isnet.sha256') (Get-FileHash $target -Algorithm SHA256).Hash.ToLowerInvariant() -NoNewline
Write-Host ("Model {0} ({1:N1} MB)" -f $target, ((Get-Item $target).Length / 1MB))
