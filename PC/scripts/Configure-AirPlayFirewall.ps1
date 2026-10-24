[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$ReceiverPath = (Join-Path $PSScriptRoot '..\tools\airplay\uxplay.exe'),
    [ValidateSet('Private', 'Public', 'Domain')][string]$Profile = 'Private'
)
$ErrorActionPreference = 'Stop'
$majorReceiver = (Resolve-Path -LiteralPath $ReceiverPath).Path
if ([IO.Path]::GetFileName($majorReceiver) -ne 'uxplay.exe') { throw 'Indicare il solo uxplay.exe incluso nella build Major.' }
$majorAdmin = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $majorAdmin.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) -and -not $WhatIfPreference) {
    throw 'La modifica del firewall richiede PowerShell come amministratore. Lo script non cambia il promajor della rete e non disattiva il firewall. Puoi prima usare -WhatIf.'
}
$majorBytes = [Text.Encoding]::UTF8.GetBytes($majorReceiver.ToLowerInvariant())
$majorHash = [Security.Cryptography.SHA256]::Create()
try { $majorId = ([BitConverter]::ToString($majorHash.ComputeHash($majorBytes))).Replace('-', '').Substring(0, 12) } finally { $majorHash.Dispose() }
$majorRules = @(
    @{ Suffix = 'Discovery'; Protocol = 'UDP'; Ports = '5353' },
    @{ Suffix = 'MediaUDP'; Protocol = 'UDP'; Ports = '6000-6002' },
    @{ Suffix = 'ControlTCP'; Protocol = 'TCP'; Ports = '7000-7002' }
)
foreach ($majorRule in $majorRules) {
    $majorName = 'Major-AirPlay-' + $majorId + '-' + $majorRule.Suffix
    if ($PSCmdlet.ShouldProcess($majorReceiver, "Consenti $($majorRule.Protocol) $($majorRule.Ports), rete $Profile, sola sottorete locale")) {
        if (Get-NetFirewallRule -Name $majorName -ErrorAction SilentlyContinue) {
            Set-NetFirewallRule -Name $majorName -Enabled True -Action Allow -Profile $Profile -RemoteAddress LocalSubnet | Out-Null
        } else {
            New-NetFirewallRule -Name $majorName -DisplayName "Major AirPlay ($($majorRule.Suffix))" -Group 'Major AirPlay' -Direction Inbound -Action Allow -Enabled True -Profile $Profile -Program $majorReceiver -Protocol $majorRule.Protocol -LocalPort $majorRule.Ports -RemoteAddress LocalSubnet | Out-Null
        }
    }
}
Write-Host 'Regole limitate al ricevitore e alla sottorete locale. Nessun cambio di promajor rete, nessuna disattivazione del firewall.'
