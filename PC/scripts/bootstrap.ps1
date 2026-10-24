[CmdletBinding()]
param(
    [switch]$InstallSdk,
    [switch]$FetchSources,
    [string]$SdkDirectory,
    [string]$CacheDirectory
)
$ErrorActionPreference = 'Stop'
$majorRoot = Split-Path $PSScriptRoot -Parent
if (-not $CacheDirectory) { $CacheDirectory = Join-Path $majorRoot '.cache' }
if (-not $SdkDirectory) { $SdkDirectory = Join-Path $majorRoot '.build-tools\dotnet' }
New-Item -ItemType Directory -Force -Path $CacheDirectory | Out-Null

function Get-VerifiedFile([string]$Url, [string]$Path, [string]$Hash, [string]$Algorithm = 'SHA256') {
    if (-not (Test-Path -LiteralPath $Path)) {
        Write-Host "Download: $Url"
        Invoke-WebRequest -UseBasicParsing -Uri $Url -OutFile $Path
    }
    $majorObserved = (Get-FileHash -LiteralPath $Path -Algorithm $Algorithm).Hash.ToLowerInvariant()
    if ($majorObserved -ne $Hash.ToLowerInvariant()) {
        throw "Hash mismatch: $Path. Atteso $Hash, osservato $majorObserved. Rimuovi manualmente il file e riprova."
    }
}

$majorScrcpyZip = Join-Path $CacheDirectory 'scrcpy-win64-v5.0.zip'
Get-VerifiedFile 'https://github.com/Genymobile/scrcpy/releases/download/v5.0/scrcpy-win64-v5.0.zip' $majorScrcpyZip '44c10d9e82f20ea67227d14d37bf9fbe3603117c5736df3f514544a02ba20a73'
$majorTools = Join-Path $majorRoot 'tools\scrcpy'
if (-not (Test-Path -LiteralPath $majorTools)) {
    $majorStage = Join-Path $CacheDirectory ('extract-' + [guid]::NewGuid().ToString('N'))
    Expand-Archive -LiteralPath $majorScrcpyZip -DestinationPath $majorStage
    New-Item -ItemType Directory -Force -Path (Split-Path $majorTools -Parent) | Out-Null
    $majorResolvedTools = [IO.Path]::GetFullPath($majorTools)
    $majorResolvedRoot = [IO.Path]::GetFullPath($majorRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $majorResolvedTools.StartsWith($majorResolvedRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Destinazione strumenti fuori dal progetto.' }
    Move-Item -LiteralPath (Join-Path $majorStage 'scrcpy-win64-v5.0') -Destination $majorTools
}
if (Test-Path -LiteralPath (Join-Path $majorRoot 'third-party\scrcpy-files.sha256.json')) {
    foreach ($majorFile in (Get-Content -LiteralPath (Join-Path $majorRoot 'third-party\scrcpy-files.sha256.json') -Raw | ConvertFrom-Json)) {
        $majorPath = Join-Path $majorTools $majorFile.name
        if (-not (Test-Path -LiteralPath $majorPath) -or (Get-FileHash -LiteralPath $majorPath -Algorithm SHA256).Hash -ne $majorFile.sha256) {
            throw "Dipendenza mancante o alterata: $majorPath. Ripristina tools\scrcpy dal ZIP verificato."
        }
    }
}

if ($InstallSdk) {
    $majorSdkZip = Join-Path $CacheDirectory 'dotnet-sdk-8.0.426-win-x64.zip'
    Get-VerifiedFile 'https://builds.dotnet.microsoft.com/dotnet/Sdk/8.0.426/dotnet-sdk-8.0.426-win-x64.zip' $majorSdkZip '10261657109f1c62126f106a9ac966739b025b5f6b4a9f88ab7c6e73cacce9aec771895daa3267b8da933a83c32ee91bf458dc6704e1ddcd9bcf0d5edd036abb' 'SHA512'
    New-Item -ItemType Directory -Force -Path $SdkDirectory | Out-Null
    Expand-Archive -LiteralPath $majorSdkZip -DestinationPath $SdkDirectory -Force
    & (Join-Path $SdkDirectory 'dotnet.exe') --version
    if ($LASTEXITCODE -ne 0) { throw 'SDK non avviabile' }
}

if ($FetchSources) {
    $majorSources = Join-Path $majorRoot 'third-party\sources'
    New-Item -ItemType Directory -Force -Path $majorSources | Out-Null
    foreach ($majorSource in (Get-Content -LiteralPath (Join-Path $majorRoot 'third-party\sources.json') -Raw | ConvertFrom-Json)) {
        Get-VerifiedFile $majorSource.url (Join-Path $majorSources $majorSource.file) $majorSource.sha256
    }
}
Write-Host 'Dipendenze verificate. Nessuna installazione globale e nessuna modifica al PATH.'
