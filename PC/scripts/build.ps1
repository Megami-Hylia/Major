[CmdletBinding()]
param([string]$DotnetPath, [switch]$SkipTests, [switch]$FrameworkDependent)
$ErrorActionPreference = 'Stop'
$majorRoot = Split-Path $PSScriptRoot -Parent
$majorCandidates = @(
    $DotnetPath,
    $env:MAJOR_DOTNET,
    (Join-Path $majorRoot '..\..\work\dotnet\dotnet.exe'),
    (Join-Path $majorRoot '.build-tools\dotnet\dotnet.exe'),
    (Get-Command dotnet -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -First 1)
)
foreach ($majorCandidate in $majorCandidates) {
    if ($majorCandidate -and (Test-Path -LiteralPath $majorCandidate)) {
        $majorSdks = & $majorCandidate --list-sdks
        if ($majorSdks -match '^8\.0\.426 ') { $majorDotnet = (Resolve-Path -LiteralPath $majorCandidate).Path; break }
    }
}
if (-not $majorDotnet) { throw 'SDK .NET 8.0.426 mancante. Esegui scripts\bootstrap.ps1 -InstallSdk o passa -DotnetPath.' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
foreach ($majorFile in (Get-Content -LiteralPath (Join-Path $majorRoot 'third-party\scrcpy-files.sha256.json') -Raw | ConvertFrom-Json)) {
    $majorPath = Join-Path $majorRoot ('tools\scrcpy\' + $majorFile.name)
    if (-not (Test-Path -LiteralPath $majorPath) -or (Get-FileHash -LiteralPath $majorPath -Algorithm SHA256).Hash -ne $majorFile.sha256) {
        throw "Dipendenza mancante o alterata: $majorPath. Esegui bootstrap.ps1 per acquisire il bundle verificato."
    }
}
$majorAirPlayManifest = Join-Path $majorRoot 'third-party\airplay\runtime-files.sha256.json'
if (-not (Test-Path -LiteralPath $majorAirPlayManifest)) { throw 'Runtime AirPlay mancante. Esegui scripts\bootstrap-airplay.ps1 prima di compilare la versione 0.3.' }
$majorAirPlayRoot = [IO.Path]::GetFullPath((Join-Path $majorRoot 'tools\airplay')).TrimEnd('\') + '\'
foreach ($majorFile in (Get-Content -LiteralPath $majorAirPlayManifest -Raw | ConvertFrom-Json)) {
    $majorPath = [IO.Path]::GetFullPath((Join-Path $majorAirPlayRoot $majorFile.path))
    if (-not $majorPath.StartsWith($majorAirPlayRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Manifest AirPlay fuori dalla cartella runtime.' }
    if (-not (Test-Path -LiteralPath $majorPath) -or (Get-FileHash -LiteralPath $majorPath -Algorithm SHA256).Hash -ne $majorFile.sha256) {
        throw "Dipendenza AirPlay mancante o alterata: $majorPath"
    }
}
if (-not (Test-Path -LiteralPath (Join-Path $majorAirPlayRoot 'uxplay.exe'))) { throw 'Eseguibile AirPlay mancante.' }
Push-Location -LiteralPath $majorRoot
try {
$majorApp = Get-ChildItem -LiteralPath (Join-Path $majorRoot 'src') -Filter 'Major.csproj' -Recurse | Select-Object -First 1
if (-not $majorApp) { throw 'src/**/Major.csproj mancante' }
if (-not $SkipTests) {
    $majorTests = @(Get-ChildItem -LiteralPath (Join-Path $majorRoot 'tests') -Filter '*.csproj' -Recurse | Sort-Object FullName)
    if ($majorTests.Count -eq 0) { throw 'Progetto self-test mancante; usa -SkipTests solo consapevolmente.' }
    foreach ($majorTest in $majorTests) {
        & $majorDotnet run --project $majorTest.FullName --configuration Release
        if ($LASTEXITCODE -ne 0) { throw "Self-test fallito: $($majorTest.Name)" }
    }
}
$majorOut = Join-Path $majorRoot 'build\Major'
$majorSelfContained = if ($FrameworkDependent) { 'false' } else { 'true' }
& $majorDotnet publish $majorApp.FullName --configuration Release --runtime win-x64 --self-contained $majorSelfContained -p:RuntimeFrameworkVersion=8.0.32 -p:PublishSingleFile=false -p:PublishTrimmed=false --output $majorOut
if ($LASTEXITCODE -ne 0) { throw 'Publish fallito' }
New-Item -ItemType Directory -Force -Path (Join-Path $majorOut 'tools') | Out-Null
Copy-Item -LiteralPath (Join-Path $majorRoot 'tools\scrcpy') -Destination (Join-Path $majorOut 'tools') -Recurse -Force
Copy-Item -LiteralPath (Join-Path $majorRoot 'tools\airplay') -Destination (Join-Path $majorOut 'tools') -Recurse -Force
New-Item -ItemType Directory -Force -Path (Join-Path $majorOut 'scripts') | Out-Null
Copy-Item -LiteralPath (Join-Path $majorRoot 'scripts\Configure-AirPlayFirewall.ps1') -Destination (Join-Path $majorOut 'scripts') -Force
Copy-Item -LiteralPath (Join-Path $majorRoot 'third-party') -Destination $majorOut -Recurse -Force
Copy-Item -LiteralPath (Join-Path $majorRoot 'LICENSE'),(Join-Path $majorRoot 'THIRD_PARTY_NOTICES.md') -Destination $majorOut -Force
Copy-Item -LiteralPath (Join-Path $majorRoot 'docs\PORTABLE_README.txt') -Destination (Join-Path $majorOut 'LEGGIMI.txt') -Force
Write-Host "Build Windows x64: $majorOut\Major.exe"
} finally {
    Pop-Location
}
