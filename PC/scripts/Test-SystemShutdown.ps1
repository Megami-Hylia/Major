[CmdletBinding()]
param([string]$AppPath = (Join-Path $PSScriptRoot '..\build\Major\Major.exe'))
$ErrorActionPreference = 'Stop'
$majorExecutable = (Resolve-Path -LiteralPath $AppPath).Path
$majorDirectory = Split-Path $majorExecutable -Parent
if (Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -eq $majorExecutable }) { throw 'Chiudere Major prima del test.' }
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class MajorShutdownMessages {
    [DllImport("user32.dll", SetLastError=true)] public static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint message, IntPtr wparam, IntPtr lparam, uint flags, uint timeout, out UIntPtr result);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
}
'@
foreach ($majorCase in @('shutdown', 'logoff', 'closing-race')) {
    $majorProcess = Start-Process -FilePath $majorExecutable -ArgumentList '--shutdown-test' -PassThru -WindowStyle Hidden
    try {
        $majorDeadline = [DateTime]::UtcNow.AddSeconds(30)
        $majorReady = $null
        while ([DateTime]::UtcNow -lt $majorDeadline -and -not $majorProcess.HasExited) {
            $majorReadyPath = Join-Path $majorDirectory 'shutdown-test-ready.json'
            if (Test-Path -LiteralPath $majorReadyPath) {
                try { $majorReady = Get-Content -LiteralPath $majorReadyPath -Raw | ConvertFrom-Json } catch { $majorReady = $null }
                if ($majorReady -and $majorReady.pid -eq $majorProcess.Id) { break }
            }
            Start-Sleep -Milliseconds 100
        }
        if (-not $majorReady -or $majorReady.pid -ne $majorProcess.Id) { throw 'La finestra di test non ha confermato avvio.' }
        [uint32]$majorOwner = 0
        $majorHandle = [IntPtr]::new([long]$majorReady.hwnd)
        $null = [MajorShutdownMessages]::GetWindowThreadProcessId($majorHandle, [ref]$majorOwner)
        if ($majorOwner -ne $majorProcess.Id) { throw 'Handle non appartenente al processo di test.' }
        # WPF receives session events on its hidden Application window, not on MainWindow.
        $majorApplicationHandle = [IntPtr]::new([long]$majorReady.applicationHwnd)
        $null = [MajorShutdownMessages]::GetWindowThreadProcessId($majorApplicationHandle, [ref]$majorOwner)
        if ($majorOwner -ne $majorProcess.Id) { throw 'Handle Application non appartenente al processo di test.' }
        $majorHelpers = @(Get-CimInstance Win32_Process | Where-Object { $_.ParentProcessId -eq $majorProcess.Id -and $_.Name -eq 'uxplay.exe' } | Select-Object -ExpandProperty ProcessId)
        if ($majorHelpers.Count -eq 0) { throw 'Backend AirPlay reale non avviato.' }
        $majorListener = @(Get-NetTCPConnection -LocalPort 49977 -State Listen -ErrorAction SilentlyContinue | Where-Object OwningProcess -eq $majorProcess.Id)
        if ($majorListener.Count -eq 0) { throw 'Ricevitore Android non avviato.' }
        $majorFlags = if ($majorCase -eq 'logoff') { [IntPtr]::new([long]2147483648) } else { [IntPtr]::Zero }
        [UIntPtr]$majorReply = [UIntPtr]::Zero
        if ($majorCase -eq 'closing-race') { $null = [MajorShutdownMessages]::SendMessageTimeout($majorHandle, 0x10, [IntPtr]::Zero, [IntPtr]::Zero, 2, 10000, [ref]$majorReply) }
        $majorQuery = [MajorShutdownMessages]::SendMessageTimeout($majorApplicationHandle, 0x11, [IntPtr]::Zero, $majorFlags, 2, 10000, [ref]$majorReply)
        if ($majorQuery -eq [IntPtr]::Zero -or $majorReply.ToUInt64() -ne 1) { throw 'La richiesta di fine sessione non e stata accettata.' }
        $null = [MajorShutdownMessages]::SendMessageTimeout($majorHandle, 0x16, [IntPtr]::new(1), $majorFlags, 2, 10000, [ref]$majorReply)
        if (-not $majorProcess.WaitForExit(10000)) { throw 'Timeout di uscita dal processo di test.' }
        if ($majorProcess.ExitCode -ne 0) { throw "Uscita anomala: $($majorProcess.ExitCode)" }
        $majorResult = Get-Content -LiteralPath (Join-Path $majorDirectory 'shutdown-test-result.json') -Raw | ConvertFrom-Json
        if ($majorResult.pid -ne $majorProcess.Id -or -not $majorResult.sessionEnding -or -not $majorResult.cleanupCompleted) { throw 'Pulizia di fine sessione non completata.' }
        foreach ($majorHelper in $majorHelpers) {
            if (Get-Process -Id $majorHelper -ErrorAction SilentlyContinue) { throw "Processo ausiliario rimasto attivo: $majorHelper" }
        }
        Write-Output "PASS $majorCase : WM_QUERYENDSESSION / WM_ENDSESSION, exit 0, ricevitori arrestati, nessun helper orfano."
    } finally {
        if (-not $majorProcess.HasExited) { Stop-Process -Id $majorProcess.Id -Force }
        $majorProcess.Dispose()
    }
}
Write-Output 'Simulazione limitata alle finestre di test di Major: il PC non viene spento e la sessione Windows non termina.'
