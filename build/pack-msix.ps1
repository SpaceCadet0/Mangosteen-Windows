# Packs publish/Mangosteen.exe into msix/Mangosteen.msix for the Microsoft Store, using build/msix/AppxManifest.xml,
# the identity in build/msix/identity.txt and the version in Directory.Build.props. The package is left unsigned:
# the Store signs it after certification.
param([string]$Exe = 'publish/Mangosteen.exe', [string]$Output = 'msix/Mangosteen.msix')
$ErrorActionPreference = 'Stop'
$msix = Join-Path $PSScriptRoot 'msix'
$identity = @{}
Get-Content (Join-Path $msix 'identity.txt') | Where-Object { $_ -match '^(\w+)=(.+)$' } | ForEach-Object { $identity[$Matches[1]] = $Matches[2].Trim() }
$version = ([xml](Get-Content (Join-Path $PSScriptRoot '..' 'Directory.Build.props'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
# The Store wants four parts and keeps the last one for itself.
$packageVersion = "$version.0"

$layout = Join-Path $PSScriptRoot '..' 'msix-layout'
Remove-Item -Recurse -Force $layout -ErrorAction SilentlyContinue
New-Item -ItemType Directory $layout | Out-Null
Copy-Item $Exe $layout
Copy-Item (Join-Path $msix 'Assets') $layout -Recurse
$manifest = (Get-Content (Join-Path $msix 'AppxManifest.xml') -Raw).
    Replace('{Name}', $identity.name).Replace('{Publisher}', [Security.SecurityElement]::Escape($identity.publisher)).
    Replace('{PublisherDisplayName}', [Security.SecurityElement]::Escape($identity.publisherDisplayName)).Replace('{Version}', $packageVersion)
Set-Content (Join-Path $layout 'AppxManifest.xml') $manifest -Encoding utf8

$makeappx = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.*\x64\makeappx.exe" | Sort-Object FullName | Select-Object -Last 1
if (-not $makeappx) { throw 'makeappx.exe (Windows SDK) was not found' }
New-Item -ItemType Directory -Force (Split-Path $Output) | Out-Null
& $makeappx.FullName pack /o /d $layout /p $Output
if ($LASTEXITCODE -ne 0) { throw "makeappx failed with $LASTEXITCODE" }
Write-Host ("Packed {0} {1} ({2:N1} MB)" -f $Output, $packageVersion, ((Get-Item $Output).Length / 1MB))
