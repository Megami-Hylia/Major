[CmdletBinding()]
param(
    [switch]$Rebuild,
    [string]$WorkDirectory,
    [string]$MsysRoot = 'C:\msys64',
    [string]$PythonPath = 'python'
)
$ErrorActionPreference = 'Stop'
$majorProject = Split-Path $PSScriptRoot -Parent
$majorThirdParty = Join-Path $majorProject 'third-party\airplay'
$majorRuntime = Join-Path $majorProject 'tools\airplay'

function Assert-Runtime {
    $majorManifest = Join-Path $majorThirdParty 'runtime-files.sha256.json'
    if (-not (Test-Path -LiteralPath $majorManifest)) { throw 'Manifest AirPlay mancante.' }
    foreach ($majorFile in (Get-Content -LiteralPath $majorManifest -Raw | ConvertFrom-Json)) {
        $majorTarget = [IO.Path]::GetFullPath((Join-Path $majorRuntime $majorFile.path))
        $majorPrefix = [IO.Path]::GetFullPath($majorRuntime).TrimEnd('\') + '\'
        if (-not $majorTarget.StartsWith($majorPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Percorso runtime fuori dalla cartella AirPlay.' }
        if (-not (Test-Path -LiteralPath $majorTarget) -or (Get-FileHash -LiteralPath $majorTarget -Algorithm SHA256).Hash -ne $majorFile.sha256) {
            throw "Runtime AirPlay mancante o alterato: $($majorFile.path)"
        }
    }
    Write-Host 'Runtime AirPlay verificato. Nessuna installazione, servizio o modifica firewall.'
}

if (-not $Rebuild) { Assert-Runtime; return }
if (-not $WorkDirectory) { $WorkDirectory = Join-Path $majorProject '..\..\work\airplay-rebuild' }
$majorWork = [IO.Path]::GetFullPath($WorkDirectory)
$majorBash = Join-Path $MsysRoot 'usr\bin\bash.exe'
if (-not (Test-Path -LiteralPath $majorBash)) { throw 'Per ricompilare serve una base MSYS2 (anche estratta localmente). Specificare -MsysRoot. Nessuna installazione automatica globale.' }
& $PythonPath -c 'import sys; assert sys.version_info >= (3,14), "Python 3.14+ required for tar.zst"'
if ($LASTEXITCODE -ne 0) { throw 'Python 3.14 o successivo richiesto per estrarre i pacchetti zstd senza librerie aggiuntive.' }
New-Item -ItemType Directory -Force -Path $majorWork,(Join-Path $majorWork 'sources'),(Join-Path $majorWork 'toolchain\packages'),(Join-Path $majorWork 'toolchain\prefix') | Out-Null

function Get-Pinned([string]$Url, [string]$File, [string]$Expected) {
    if (-not (Test-Path -LiteralPath $File)) { Invoke-WebRequest -UseBasicParsing -Uri $Url -OutFile $File }
    if ((Get-FileHash -LiteralPath $File -Algorithm SHA256).Hash -ne $Expected) { throw "Hash non corrispondente: $File" }
}

foreach ($majorPackage in (Get-Content -LiteralPath (Join-Path $majorThirdParty 'build-packages.sha256.json') -Raw | ConvertFrom-Json)) {
    Get-Pinned $majorPackage.url (Join-Path $majorWork ('toolchain\packages\' + $majorPackage.file)) $majorPackage.sha256
}
$majorOriginalZip = Join-Path $majorThirdParty 'sources\UxPlay-15a6b87172eca38b0df4e615fbf1bba108734dde.zip'
Get-Pinned 'https://github.com/FDH2/UxPlay/archive/15a6b87172eca38b0df4e615fbf1bba108734dde.zip' $majorOriginalZip '437622bc66f81c88266e7b01ba93dcd9f8637b0bb9f3be1488556eaaae0853e8'
$majorFfmpeg = Join-Path $majorProject 'third-party\sources\ffmpeg-9.0.2.tar.xz'
Get-Pinned 'https://ffmpeg.org/releases/ffmpeg-9.0.2.tar.xz' $majorFfmpeg '8c3850283eb25fa026482078a04051e0be17347b09ef81a0849bec15a96e002e'
Expand-Archive -LiteralPath $majorOriginalZip -DestinationPath (Join-Path $majorWork 'sources') -Force
& tar -xf $majorFfmpeg -C (Join-Path $majorWork 'sources')
if ($LASTEXITCODE -ne 0) { throw 'Estrazione FFmpeg fallita.' }
$majorSource = Join-Path $majorWork 'sources\UxPlay-15a6b87172eca38b0df4e615fbf1bba108734dde'
& git -C $majorSource apply --check (Join-Path $majorThirdParty 'major-embedded.patch')
if ($LASTEXITCODE -ne 0) { throw 'La patch GPL non coincide con i sorgenti fissati.' }
& git -C $majorSource apply (Join-Path $majorThirdParty 'major-embedded.patch')
if ($LASTEXITCODE -ne 0) { throw 'Applicazione patch GPL fallita.' }

$majorOldWork = $env:MAJOR_AIRPLAY_WORK
$majorOldProject = $env:MAJOR_AIRPLAY_PROJECT
$majorOldAirRoot = $env:AIR_ROOT
try {
    $env:MAJOR_AIRPLAY_WORK = $majorWork
    $env:MAJOR_AIRPLAY_PROJECT = $majorProject
    $majorPosixWork = & (Join-Path $MsysRoot 'usr\bin\cygpath.exe') -u $majorWork
    $env:AIR_ROOT = $majorPosixWork
    & $PythonPath (Join-Path $PSScriptRoot 'airplay\extract_packages.py')
    if ($LASTEXITCODE -ne 0) { throw 'Estrazione toolchain locale fallita.' }
    & $majorBash (Join-Path $PSScriptRoot 'airplay\build_ffmpeg.sh')
    if ($LASTEXITCODE -ne 0) { throw 'Compilazione FFmpeg LGPL fallita.' }
    & $majorBash (Join-Path $PSScriptRoot 'airplay\build_uxplay.sh')
    if ($LASTEXITCODE -ne 0) { throw 'Compilazione UxPlay fallita.' }
    & $PythonPath (Join-Path $PSScriptRoot 'airplay\bundle_runtime.py')
    if ($LASTEXITCODE -ne 0) { throw 'Raccolta runtime fallita.' }
} finally {
    $env:MAJOR_AIRPLAY_WORK = $majorOldWork
    $env:MAJOR_AIRPLAY_PROJECT = $majorOldProject
    $env:AIR_ROOT = $majorOldAirRoot
}
Assert-Runtime
