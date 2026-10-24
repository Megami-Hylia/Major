[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$majorRoot = Split-Path $PSScriptRoot -Parent
$majorCache = Join-Path $majorRoot '.cache'
New-Item -ItemType Directory -Path $majorCache -Force | Out-Null
$majorArchive = Join-Path $majorCache 'Major-0.5.1-win-x64.zip'
if (-not (Test-Path -LiteralPath $majorArchive)) {
    Invoke-WebRequest -UseBasicParsing -Uri 'https://github.com/Megami-Hylia/Major/releases/download/v0.5.1/Major-0.5.1-win-x64.zip' -OutFile $majorArchive
}
if ((Get-FileHash -LiteralPath $majorArchive -Algorithm SHA256).Hash.ToLowerInvariant() -ne '2196d658b8db598d051324d17443ea36dfada133e3eec3aa8bea200c5f03cb41') { throw 'SHA-256 del pacchetto Windows non corrispondente.' }
$majorStage = Join-Path $majorCache 'runtime-0.5.1'
Expand-Archive -LiteralPath $majorArchive -DestinationPath $majorStage -Force
foreach ($majorRelative in @('tools', 'third-party')) {
    Copy-Item -LiteralPath (Join-Path $majorStage ('Major\' + $majorRelative)) -Destination $majorRoot -Recurse -Force
}
Write-Host 'Dipendenze e sorgenti accompagnatori ripristinati e verificati.'
