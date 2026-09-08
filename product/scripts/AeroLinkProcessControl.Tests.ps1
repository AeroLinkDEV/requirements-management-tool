#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'AeroLinkProcessControl.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'AeroLinkTransition.psm1') -Force
$root = Join-Path ([IO.Path]::GetTempPath()) ('aerolink-924-contract-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$failures = [Collections.Generic.List[string]]::new()
function Check([bool]$Condition, [string]$Message) { if (-not $Condition) { $failures.Add($Message) } }
function Refuses([scriptblock]$Action, [string]$Message) {
    try { & $Action | Out-Null; $failures.Add($Message) } catch {}
}
$child = $null
$helper = $null
$lease = $null
$savedCapability = $env:AEROLINK_TRANSITION_LEASE
try {
    $powershell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $child = Start-Process -FilePath $powershell -ArgumentList '-NoProfile -NonInteractive -Command "Start-Sleep -Seconds 60"' -WindowStyle Hidden -PassThru
    $started = $child.StartTime.ToUniversalTime()
    Grant-AeroLinkCreatedProcessAccess -ProcessId $child.Id -StartedAt $started -ExpectedExecutable $powershell -ExpectedArguments @('Start-Sleep', '60')
    Refuses { Grant-AeroLinkCreatedProcessAccess -ProcessId $child.Id -StartedAt $started.AddSeconds(-1) -ExpectedExecutable $powershell -ExpectedArguments @('Start-Sleep') } 'Stale start identity must not grant access.'
    Refuses { Grant-AeroLinkCreatedProcessAccess -ProcessId $child.Id -StartedAt $started -ExpectedExecutable 'C:\foreign.exe' -ExpectedArguments @('Start-Sleep') } 'Contradictory executable must not grant access.'
    $forged = [pscustomobject]@{ ProcessId = $child.Id; StartedAt = $started.AddSeconds(-1).ToString('o'); ExecutablePath = $powershell }
    Refuses { Stop-AeroLinkProvenProcess -Process $forged } 'A copied/stale process record must not stop the live child.'
    $child.Refresh()
    Check (-not $child.HasExited) 'The child must survive rejected stale provenance.'
    $proven = [pscustomobject]@{ ProcessId = $child.Id; StartedAt = $started.ToString('o'); ExecutablePath = $powershell }
    $script:stopped = $false
    Stop-AeroLinkProvenProcess -Process $proven -OnStopped { $script:stopped = $true }
    $child.WaitForExit()
    Check $script:stopped 'Completed teardown must be recorded at the action boundary.'

    Import-Module (Join-Path $PSScriptRoot 'AeroLinkRemoteDemo.psm1') -Force
    $helperScripts = Join-Path $root 'product\scripts'
    New-Item -ItemType Directory -Path $helperScripts -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $helperScripts 'Start-AeroLinkProduction.ps1') -Value 'exit 7' -Encoding UTF8
    $helperConfig = [pscustomobject]@{ AeroLinkRoot=$root; LogsPath=(Join-Path $root 'logs'); PublicUrl='https://example.invalid' }
    $helper = Start-AeroLinkRemoteDemoProductionHelper -Config $helperConfig
    for ($poll = 0; $poll -lt 50; $poll++) {
        $helper.Refresh()
        if ($helper.HasExited) { break }
        Start-Sleep -Milliseconds 100
    }
    Check ($helper.HasExited -and $helper.ExitCode -eq 7) 'A real redirected Windows PowerShell helper must retain its non-zero exit code.'

    $lease = Enter-AeroLinkTransition -InstallationRoot $root -Policy Preserve
    $module = Join-Path $PSScriptRoot 'AeroLinkTransition.psm1'
    $continuation = Join-Path $root 'continue.ps1'
    @'
param($Module, $Root)
$ErrorActionPreference = 'Stop'
Import-Module $Module
$lease = Enter-AeroLinkTransition -InstallationRoot $Root -Policy KeepReady
try {
    if ($lease.Owner -or $lease.Policy -ne 'Preserve') { throw 'Continuation changed ownership/policy.' }
} finally { Exit-AeroLinkTransition $lease }
'@ | Set-Content -LiteralPath $continuation -Encoding UTF8
    & $powershell -NoProfile -ExecutionPolicy Bypass -File $continuation -Module $module -Root $root
    Check ($LASTEXITCODE -eq 0) 'A fresh descendant must continue without deadlock and retain Preserve policy.'
    $capability = $env:AEROLINK_TRANSITION_LEASE
    $env:AEROLINK_TRANSITION_LEASE = '{"token":"forged"}'
    Refuses { Enter-AeroLinkTransition -InstallationRoot $root } 'A forged capability must not enter a held lease.'
    $env:AEROLINK_TRANSITION_LEASE = $capability
    Exit-AeroLinkTransition $lease
    $lease = $null
    # An interrupted owner leaves a file but no OS lease. It must never permanently block or replay state.
    $env:AEROLINK_TRANSITION_LEASE = $capability
    $lease = Enter-AeroLinkTransition -InstallationRoot $root -Policy KeepReady
    Check ($lease.Owner -and $lease.Policy -eq 'KeepReady') 'A stale released lease must acquire fresh ownership and policy.'
}
finally {
    Exit-AeroLinkTransition $lease
    $env:AEROLINK_TRANSITION_LEASE = $savedCapability
    if ($child) { if (-not $child.HasExited) { $child.Kill(); $child.WaitForExit() }; $child.Dispose() }
    if ($helper) { if (-not $helper.Process.HasExited) { $helper.Process.Kill(); $helper.Process.WaitForExit() }; $helper.Process.Dispose() }
    $resolved = [IO.Path]::GetFullPath($root)
    if (-not $resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected fixture cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
if ($failures.Count) { $failures | ForEach-Object { Write-Host "FAIL: $_" }; exit 1 }
Write-Host 'Managed-process and transition-lease contracts passed (disposable interactive processes; S4U not claimed).'
