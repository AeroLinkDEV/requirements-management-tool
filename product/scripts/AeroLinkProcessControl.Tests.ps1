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
    $setup = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Initialize-AeroLinkHomeProcessControl.ps1') -Raw
    $invocation = [regex]::Match($setup, '(?s)    \$launcherPath = Join-Path \$Source.*?finally \{ \$launcher.Dispose\(\) \}')
    if (-not $invocation.Success) { throw 'First-deployment native invocation was not found.' }
    foreach ($expectedCode in @(0, 7)) {
        Set-Content -LiteralPath (Join-Path $helperScripts 'Start-AeroLinkProduction.ps1') -Value ('[Console]::Error.WriteLine("native notice"); exit ' + $expectedCode) -Encoding UTF8
        $Source = $root
        $Log = Join-Path $root "deployment-stderr-$expectedCode.log"
        $code = -1
        . ([scriptblock]::Create($invocation.Value))
        Check ($code -eq $expectedCode) 'First-deployment stderr must preserve both successful and failed native exit codes.'
        Check ($ErrorActionPreference -eq 'Stop') 'First-deployment invocation must restore terminating error handling.'
        Check ((Get-Content -LiteralPath "$Log.stderr" -Raw) -match 'native notice') 'Native stderr must remain in the deployment log.'
    }
    $survivor = $null
    try {
        @'
$child = Start-Process powershell.exe -ArgumentList '-NoProfile -Command "Start-Sleep -Seconds 30"' -WindowStyle Hidden -PassThru
$child.Id | Set-Content (Join-Path $PSScriptRoot 'survivor.pid')
exit 0
'@ | Set-Content -LiteralPath (Join-Path $helperScripts 'Start-AeroLinkProduction.ps1') -Encoding UTF8
        $Log = Join-Path $root 'deployment-survivor.log'
        $timer = [Diagnostics.Stopwatch]::StartNew()
        . ([scriptblock]::Create($invocation.Value))
        $survivor = Get-Process -Id ([int](Get-Content (Join-Path $helperScripts 'survivor.pid')))
        Check ($code -eq 0 -and $timer.Elapsed.TotalSeconds -lt 15 -and -not $survivor.HasExited) 'Setup must complete when its launcher exits while the launched service remains alive.'
    } finally {
        if ($survivor) { if (-not $survivor.HasExited) { $survivor.Kill(); $survivor.WaitForExit() }; $survivor.Dispose() }
    }
    Import-Module (Join-Path $PSScriptRoot 'AeroLinkBootstrap.psm1') -Force
    $reentrySurvivor = $null
    try {
        $timer = [Diagnostics.Stopwatch]::StartNew()
        $reentryCode = Invoke-AeroLinkBootstrapReentry -CurrentScriptPath (Join-Path $helperScripts 'Start-AeroLinkProduction.ps1') -ExpectedSha 'disposable-reentry-source'
        $reentrySurvivor = Get-Process -Id ([int](Get-Content (Join-Path $helperScripts 'survivor.pid')))
        Check ($reentryCode -eq 0 -and $timer.Elapsed.TotalSeconds -lt 15 -and -not $reentrySurvivor.HasExited) 'Source re-entry must finish while its replacement service remains alive.'
        Set-Content -LiteralPath (Join-Path $helperScripts 'Start-AeroLinkProduction.ps1') -Value 'exit 7'
        $reentryCode = Invoke-AeroLinkBootstrapReentry -CurrentScriptPath (Join-Path $helperScripts 'Start-AeroLinkProduction.ps1') -ExpectedSha 'disposable-reentry-source'
        Check ($reentryCode -eq 7) 'Source re-entry must retain a failed launcher exit code.'
    } finally {
        if ($reentrySurvivor) { if (-not $reentrySurvivor.HasExited) { $reentrySurvivor.Kill(); $reentrySurvivor.WaitForExit() }; $reentrySurvivor.Dispose() }
    }

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

    # Execute the real explicit Stop entry point with disposable service adapters. A failed
    # native/ownership stop must not reach PostgreSQL, and the installation lease always exits.
    $stopScripts = Join-Path $root 'stop-contract\product\scripts'
    New-Item -ItemType Directory -Path $stopScripts -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Stop-AeroLink.ps1') -Destination $stopScripts
    @'
function Stop-AeroLinkOwnedListener($Port, $OwnershipFragments) {
    if ($Port -eq 5080 -and -not $OwnershipFragments[0].EndsWith('src\AeroLink.Api')) { throw 'API ownership must use its exact directory.' }
    Add-Content (Join-Path $PSScriptRoot 'events.txt') "stop-$Port"
    if ($Port -eq 5080 -and (Test-Path (Join-Path $PSScriptRoot 'refuse'))) { throw 'Native/ownership stop refused.' }
    [pscustomobject]@{ Detail = 'Disposable owned listener stopped.' }
}
'@ | Set-Content (Join-Path $stopScripts 'AeroLinkRuntimeIdentity.psm1')
    'function Get-AeroLinkInstallationPaths($ProductRoot) { [pscustomobject]@{InstallationRoot=$ProductRoot} }' | Set-Content (Join-Path $stopScripts 'AeroLinkInstallation.psm1')
    @'
function Enter-AeroLinkTransition($InstallationRoot) { Add-Content (Join-Path $PSScriptRoot 'events.txt') 'enter'; [pscustomobject]@{Root=$InstallationRoot} }
function Exit-AeroLinkTransition($Lease) { Add-Content (Join-Path $PSScriptRoot 'events.txt') 'exit' }
'@ | Set-Content (Join-Path $stopScripts 'AeroLinkTransition.psm1')
    'Add-Content (Join-Path $PSScriptRoot "events.txt") "postgres"' | Set-Content (Join-Path $stopScripts 'Stop-Postgres.ps1')
    & $powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $stopScripts 'Stop-AeroLink.ps1')
    Check ($LASTEXITCODE -eq 0 -and ((Get-Content (Join-Path $stopScripts 'events.txt')) -join ',') -eq 'enter,stop-5173,stop-5080,postgres,exit') 'Explicit Stop must coordinate and prove both listeners before stopping PostgreSQL.'
    Clear-Content (Join-Path $stopScripts 'events.txt')
    Set-Content (Join-Path $stopScripts 'refuse') 'refuse'
    $stopPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & $powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $stopScripts 'Stop-AeroLink.ps1') *> (Join-Path $root 'stop-refusal.log')
        $stopCode = $LASTEXITCODE
    } finally { $ErrorActionPreference = $stopPreference }
    Check ($stopCode -ne 0 -and ((Get-Content (Join-Path $stopScripts 'events.txt')) -join ',') -eq 'enter,stop-5173,stop-5080,exit') 'Failed explicit Stop must release its lease and preserve PostgreSQL.'
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
