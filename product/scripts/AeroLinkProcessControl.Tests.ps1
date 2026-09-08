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
$continuingChild = $null
$savedCapability = $env:AEROLINK_TRANSITION_LEASE
try {
    $powershell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $child = Start-Process -FilePath $powershell -ArgumentList '-NoProfile -NonInteractive -Command "Start-Sleep -Seconds 60"' -WindowStyle Hidden -PassThru
    $started = $child.StartTime.ToUniversalTime()
    Grant-AeroLinkCreatedProcessAccess -ProcessId $child.Id -StartedAt $started -ExpectedExecutable $powershell -ExpectedArguments @('Start-Sleep', '60')
    $native = Get-AeroLinkNativeProcessIdentity -ProcessId $child.Id
    Check ($native.ExecutablePath -ieq $powershell -and $native.CommandLine -match 'Start-Sleep' -and
        ([DateTimeOffset]$native.StartedAt).UtcDateTime.Ticks -eq $started.Ticks) 'Native query must bind executable, launch contract and exact creation through one process handle.'
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
    $lease = Enter-AeroLinkTransition -InstallationRoot $root -Policy Preserve
    $continuationWitness = Join-Path $root 'witness.ps1'
    @'
param($Module, $Root)
$ErrorActionPreference = 'Stop'
Import-Module $Module
$lease = Enter-AeroLinkTransition -InstallationRoot $Root
try {
    Set-Content -LiteralPath (Join-Path $Root 'child-ready') -Value 'ready'
    for ($i=0; $i -lt 200 -and -not (Test-Path (Join-Path $Root 'child-release')); $i++) { Start-Sleep -Milliseconds 100 }
} finally { Exit-AeroLinkTransition $lease }
'@ | Set-Content -LiteralPath $continuationWitness -Encoding UTF8
    $continuingChild = Start-Process -FilePath $powershell -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$continuationWitness`" -Module `"$module`" -Root `"$root`"" -WindowStyle Hidden -PassThru
    for ($i=0; $i -lt 100 -and -not (Test-Path (Join-Path $root 'child-ready')); $i++) { Start-Sleep -Milliseconds 100 }
    Check (Test-Path (Join-Path $root 'child-ready')) 'The continuation witness must be held before parent interruption.'
    Exit-AeroLinkTransition $lease
    $lease = $null
    $env:AEROLINK_TRANSITION_LEASE = $null
    Refuses { Enter-AeroLinkTransition -InstallationRoot $root } 'A live child must exclude another coordinator after its parent releases the lease.'
    Set-Content -LiteralPath (Join-Path $root 'child-release') -Value 'release'
    $continuingChild.WaitForExit()
    $lease = Enter-AeroLinkTransition -InstallationRoot $root -Policy Preserve
    $intent = [pscustomobject]@{ SourceRoot=$root; PriorTunnel=$true; Stage='Quiesced'; Discharged=$false }
    Save-AeroLinkProductionObligation -Obligation $intent
    Exit-AeroLinkTransition $lease
    $lease = Enter-AeroLinkTransition -InstallationRoot $root -Policy Preserve
    Check ($lease.Pending -and $lease.Pending.PriorTunnel) 'Interrupted intent must survive without being treated as process ownership.'
    $intent.Discharged = $true
    Save-AeroLinkProductionObligation -Obligation $intent
    Exit-AeroLinkTransition $lease
    $lease = $null
    # A discharged intent must never replay on a fresh acquisition.
    $env:AEROLINK_TRANSITION_LEASE = $capability
    $lease = Enter-AeroLinkTransition -InstallationRoot $root -Policy KeepReady
    Check ($lease.Owner -and $lease.Policy -eq 'KeepReady' -and -not $lease.Pending) 'A stale released lease must acquire fresh ownership and policy.'
}
finally {
    Exit-AeroLinkTransition $lease
    $env:AEROLINK_TRANSITION_LEASE = $savedCapability
    if ($continuingChild) { if (-not $continuingChild.HasExited) { $continuingChild.Kill(); $continuingChild.WaitForExit() }; $continuingChild.Dispose() }
    if ($child) { if (-not $child.HasExited) { $child.Kill(); $child.WaitForExit() }; $child.Dispose() }
    if ($helper) { if (-not $helper.Process.HasExited) { $helper.Process.Kill(); $helper.Process.WaitForExit() }; $helper.Process.Dispose() }
    $resolved = [IO.Path]::GetFullPath($root)
    if (-not $resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected fixture cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
if ($failures.Count) { $failures | ForEach-Object { Write-Host "FAIL: $_" }; exit 1 }
Write-Host 'Managed-process and transition-lease contracts passed (disposable interactive processes; S4U not claimed).'
