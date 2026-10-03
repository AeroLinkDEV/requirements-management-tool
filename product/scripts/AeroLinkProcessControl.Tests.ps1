#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'AeroLinkProcessControl.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'AeroLinkTransition.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'AeroLinkTransitionKernel.psm1') -DisableNameChecking
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
$treeParent = $null
$treeChildId = 0
$treeDecoy = $null
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
    # The brokered first deployment is judged by ONE invocation's bound result AND its task result (#1053, F2/F3).
    $deploymentDirectory = Join-Path $root 'first-deployment'
    New-Item -ItemType Directory -Path $deploymentDirectory -Force | Out-Null
    $requestId = [guid]::NewGuid().ToString('N')
    $resultPath = Join-Path $deploymentDirectory "$requestId.result.json"
    $writeResult = { param($Value) Publish-AeroLinkJsonAtomic -Path $resultPath -Value $Value }
    Check ((Resolve-AeroLinkFirstDeploymentResult -ResultPath $resultPath -RequestId $requestId -TaskEnded $false -LastTaskResult 0).Unknown) 'A deployment task that has not ended is Unknown.'
    Check ((Resolve-AeroLinkFirstDeploymentResult -ResultPath $resultPath -RequestId $requestId -TaskEnded $true -LastTaskResult 0).Unknown) 'An ended task with no result for this request is Unknown, never success.'
    & $writeResult ([ordered]@{ requestId = 'another-request'; decision = 'Completed'; exitCode = 0 })
    Check ((Resolve-AeroLinkFirstDeploymentResult -ResultPath $resultPath -RequestId $requestId -TaskEnded $true -LastTaskResult 0).Unknown) 'A result bound to another request cannot stand in for this one.'
    & $writeResult ([ordered]@{ requestId = $requestId; decision = 'Completed'; exitCode = 0 })
    $stale = Resolve-AeroLinkFirstDeploymentResult -ResultPath $resultPath -RequestId $requestId -TaskEnded $true -LastTaskResult 1
    Check (-not $stale.Succeeded -and -not $stale.Unknown) 'A Completed file with a nonzero task result is a failure.'
    & $writeResult ([ordered]@{ requestId = $requestId; decision = 'Completed'; exitCode = 24 })
    Check (-not (Resolve-AeroLinkFirstDeploymentResult -ResultPath $resultPath -RequestId $requestId -TaskEnded $true -LastTaskResult 0).Succeeded) 'A Completed decision contradicted by its exit code is a failure.'
    & $writeResult ([ordered]@{ requestId = $requestId; decision = 'RestorationFailed'; exitCode = 26 })
    Check (-not (Resolve-AeroLinkFirstDeploymentResult -ResultPath $resultPath -RequestId $requestId -TaskEnded $true -LastTaskResult 26).Succeeded) 'A failed transition is a failure.'
    & $writeResult ([ordered]@{ requestId = $requestId; decision = 'Completed'; exitCode = 0 })
    Check ((Resolve-AeroLinkFirstDeploymentResult -ResultPath $resultPath -RequestId $requestId -TaskEnded $true -LastTaskResult 0).Succeeded) 'Completed, exit 0 and task result 0 for this request is success.'

    # The task action itself: no staged request does nothing; outside the scheduled batch context it refuses and says so.
    $deployInstallation = Join-Path $root 'deploy-installation'
    New-Item -ItemType Directory -Path (Join-Path $deployInstallation 'bootstrap\first-deployment') -Force | Out-Null
    $deployScript = Join-Path $PSScriptRoot 'Invoke-AeroLinkFirstDeployment.ps1'
    & $powershell -NoProfile -ExecutionPolicy Bypass -File $deployScript -InstallationRoot $deployInstallation | Out-Null
    Check ($LASTEXITCODE -eq 2) 'The deployment action with no staged request exits 2 and does nothing.'
    Publish-AeroLinkJsonAtomic -Path (Join-Path $deployInstallation 'bootstrap\first-deployment\request.json') -Value ([ordered]@{ requestId = $requestId; sourceRoot = $root; configurationProfile = $env:LOCALAPPDATA })
    & $powershell -NoProfile -ExecutionPolicy Bypass -File $deployScript -InstallationRoot $deployInstallation | Out-Null
    $refusedResult = Read-AeroLinkJsonRecord -Path (Join-Path $deployInstallation "bootstrap\first-deployment\$requestId.result.json")
    Check ($LASTEXITCODE -eq 1 -and $refusedResult.Class -eq 'Valid' -and $refusedResult.Value.requestId -eq $requestId -and $refusedResult.Value.exitCode -eq 1 -and
        [string]$refusedResult.Value.detail -match 'scheduled batch') 'Outside the scheduled batch context the deployment action refuses with a result bound to its request.'
    Check (-not (Test-Path -LiteralPath (Join-Path $deployInstallation 'bootstrap\transitions'))) 'A refused deployment admits no transition attempt.'
    @'
$child = Start-Process powershell.exe -ArgumentList '-NoProfile -Command "Start-Sleep -Seconds 30"' -WindowStyle Hidden -PassThru
$child.Id | Set-Content (Join-Path $PSScriptRoot 'survivor.pid')
exit 0
'@ | Set-Content -LiteralPath (Join-Path $helperScripts 'Start-AeroLinkProduction.ps1') -Encoding UTF8
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

    # Supported backup/retention entry points against an owned installation. Real admission and
    # archive locks; dependency adapters never start PostgreSQL or read an application's evidence.
    $backupScripts = Join-Path $root 'backup-admission\product\scripts'
    New-Item -ItemType Directory -Path $backupScripts -Force | Out-Null
    foreach ($name in @('Backup-AeroLink.ps1','Remove-AeroLinkSurplusBackups.ps1','AeroLinkInstallation.psm1','AeroLinkTransition.psm1','AeroLinkBackupRetention.psm1')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $backupScripts
    }
    'function Get-AeroLinkEvidenceRoot { Join-Path $env:AEROLINK_INSTALLATION_ROOT "evidence" }' |
        Set-Content -LiteralPath (Join-Path $backupScripts 'AeroLinkEvidenceStore.psm1') -Encoding UTF8
    '# No archive operation is reached.' | Set-Content -LiteralPath (Join-Path $backupScripts 'AeroLinkBackupArchive.psm1') -Encoding UTF8
    @'
function Invoke-AeroLinkChildScript {
    Set-Content -LiteralPath $env:AEROLINK_1478_HELPER -Value 'unexpected-helper'
    [pscustomobject]@{ExitCode=1;Detail='Disposable helper; no PostgreSQL started.'}
}
'@ | Set-Content -LiteralPath (Join-Path $backupScripts 'AeroLinkNativeRunner.psm1') -Encoding UTF8
    $entryDriver = Join-Path $root 'backup-entry-driver.ps1'
    @'
param($Entry, $Installation, $BackupRoot, $Helper, $WorkingDirectory, [switch]$Explicit, [switch]$JoinParent)
$ErrorActionPreference = 'Stop'
if ($WorkingDirectory) {
    Set-Location -LiteralPath $WorkingDirectory
    [Environment]::CurrentDirectory=$WorkingDirectory
    if (-not (Test-Path -LiteralPath $Installation -PathType Container)) { throw 'Partial-path positive-control target is missing.' }
    Write-Output 'Existing partial path target verified.'
}
$env:AEROLINK_INSTALLATION_ROOT = $Installation
$env:AEROLINK_1478_HELPER = $Helper
if (-not $JoinParent) { $env:AEROLINK_TRANSITION_LEASE=$null; $env:AEROLINK_TRANSITION_JOURNAL=$null }
if ($BackupRoot) {
    if ($Explicit) { & $Entry -BackupRoot $BackupRoot -InstallationRoot $Installation }
    else { & $Entry -BackupRoot $BackupRoot }
} else { & $Entry }
'@ | Set-Content -LiteralPath $entryDriver -Encoding UTF8
    $backupInstallation = Join-Path $root 'backup-admission-installation'
    New-Item -ItemType Directory -Path $backupInstallation | Out-Null
    $canonicalBackups = Join-Path $backupInstallation 'backups'
    $externalBackups = Join-Path $root 'unassociated-archives'
    $helperEvent = Join-Path $root 'backup-helper-event'
    $backupAdmissionLease = $null
    $retentionChild = $null
    $retentionRelease = Join-Path $root 'retention-release'
    $backupPriorObligation = $env:AEROLINK_PRODUCTION_OBLIGATION
    try {
        $backupAdmissionLease = Enter-AeroLinkTransition -InstallationRoot $backupInstallation -Policy KeepReady
        foreach ($entry in @('Backup-AeroLink.ps1','Remove-AeroLinkSurplusBackups.ps1')) {
            $entryPath = Join-Path $backupScripts $entry
            $extra = if ($entry -eq 'Remove-AeroLinkSurplusBackups.ps1') { @('-BackupRoot',$canonicalBackups) } else { @() }
            $entryPreference = $ErrorActionPreference
            try {
                $ErrorActionPreference = 'Continue'
                & $powershell -NoProfile -ExecutionPolicy Bypass -File $entryDriver -Entry $entryPath -Installation $backupInstallation -Helper $helperEvent @extra *> (Join-Path $root ($entry + '.refusal.log'))
                $entryCode = $LASTEXITCODE
            } finally { $ErrorActionPreference = $entryPreference }
            $refusal=Get-Content -LiteralPath (Join-Path $root ($entry + '.refusal.log')) -Raw
            Check ($entryCode -ne 0 -and $refusal -match 'Another HOME transition owns' -and -not (Test-Path -LiteralPath $helperEvent) -and -not (Test-Path -LiteralPath (Join-Path $canonicalBackups '.backup.lock'))) "$entry must refuse a held transition before helper/PostgreSQL or backup-root lock work."
        }
        $retentionEntry = Join-Path $backupScripts 'Remove-AeroLinkSurplusBackups.ps1'
        & $powershell -NoProfile -ExecutionPolicy Bypass -File $entryDriver -Entry $retentionEntry -Installation $backupInstallation -BackupRoot $externalBackups -Helper $helperEvent
        Check ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath (Join-Path $externalBackups '.backup.lock'))) 'Unassociated external archives retain their root-lock preview while this installation is busy.'
        $entryPreference = $ErrorActionPreference
        try {
            $ErrorActionPreference = 'Continue'
            & $powershell -NoProfile -ExecutionPolicy Bypass -File $entryDriver -Entry $retentionEntry -Installation $backupInstallation -BackupRoot $externalBackups -Helper $helperEvent -Explicit *> (Join-Path $root 'explicit-retention-refusal.log')
            $entryCode = $LASTEXITCODE
        } finally { $ErrorActionPreference = $entryPreference }
        Check ($entryCode -ne 0 -and (Get-Content -LiteralPath (Join-Path $root 'explicit-retention-refusal.log') -Raw) -match 'Another HOME transition owns') 'Explicitly associated external archives must not downgrade a held installation lease to root-only.'

        # A real authenticated retention child holds both locks during a disposable plan boundary.
        # It must not deadlock, change the parent's policy or lose exclusion when the parent releases.
        @'
function Enter-AeroLinkBackupLock($BackupRoot) {
    [void][IO.Directory]::CreateDirectory($BackupRoot)
    [IO.File]::Open((Join-Path $BackupRoot '.backup.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
}
function Get-AeroLinkBackupRetentionPlan {
    Set-Content -LiteralPath $env:AEROLINK_1478_HELPER -Value 'retention-plan-admitted'
    $release = Join-Path (Split-Path $env:AEROLINK_1478_HELPER -Parent) 'retention-release'
    for ($i=0;$i -lt 200 -and -not(Test-Path -LiteralPath $release);$i++){Start-Sleep -Milliseconds 100}
    @()
}
'@ | Set-Content -LiteralPath (Join-Path $backupScripts 'AeroLinkBackupRetention.psm1') -Encoding UTF8
        $retentionChild = Start-Process -FilePath $powershell -WindowStyle Hidden -PassThru -ArgumentList (
            '-NoProfile -ExecutionPolicy Bypass -File "{0}" -Entry "{1}" -Installation "{2}" -BackupRoot "{3}" -Helper "{4}" -JoinParent' -f
            $entryDriver,$retentionEntry,$backupInstallation,$canonicalBackups,$helperEvent) `
            -RedirectStandardOutput (Join-Path $root 'retention-child.stdout.log') -RedirectStandardError (Join-Path $root 'retention-child.stderr.log')
        # Hold the launched lifetime before polling; the established production helper uses
        # this same native handle because PS5's polled Start-Process ExitCode can be unavailable.
        $retentionChildHandle = $retentionChild.Handle
        for ($i=0;$i -lt 100 -and -not(Test-Path -LiteralPath $helperEvent) -and -not $retentionChild.HasExited;$i++){Start-Sleep -Milliseconds 100}
        Check (Test-Path -LiteralPath $helperEvent) 'Authenticated retention must reach its plan without deadlock.'
        Check (@(Get-ChildItem -LiteralPath (Join-Path $backupInstallation 'bootstrap') -Filter '*.active').Count -eq 1) 'Supported retention must hold a descendant witness.'
        Exit-AeroLinkTransition $backupAdmissionLease
        $backupAdmissionLease=$null
        Refuses { Enter-AeroLinkTransition -InstallationRoot $backupInstallation } 'A live retention child must exclude another transition after parent interruption.'
        Set-Content -LiteralPath $retentionRelease -Value 'release'
        if (-not $retentionChild.WaitForExit(10000)) { throw 'Owned retention child did not finish after release.' }
        $retentionExit = [ordered]@{ processId=$retentionChild.Id; propertyBeforeRefresh=$null; propertyBeforeRefreshType=$null;
            propertyBeforeRefreshError=$null; propertyAfterRefresh=$null; propertyAfterRefreshType=$null;
            propertyAfterRefreshError=$null; nativeExit=$null; nativeError=$null }
        try {
            $retentionExit.propertyBeforeRefresh=$retentionChild.ExitCode
            if ($null -ne $retentionExit.propertyBeforeRefresh) { $retentionExit.propertyBeforeRefreshType=$retentionExit.propertyBeforeRefresh.GetType().FullName }
        } catch { $retentionExit.propertyBeforeRefreshError=$_.Exception.Message }
        try { $retentionExit.nativeExit=[AeroLink.ProcessAccess]::ExitCode($retentionChildHandle) }
        catch { $retentionExit.nativeError=$_.Exception.Message }
        try {
            $retentionChild.Refresh()
            $retentionExit.propertyAfterRefresh=$retentionChild.ExitCode
            if ($null -ne $retentionExit.propertyAfterRefresh) { $retentionExit.propertyAfterRefreshType=$retentionExit.propertyAfterRefresh.GetType().FullName }
        } catch { $retentionExit.propertyAfterRefreshError=$_.Exception.Message }
        Write-Host ('Retention child exit observation: ' + ($retentionExit | ConvertTo-Json -Compress))
        Check ($null -ne $retentionExit.nativeExit -and $retentionExit.nativeExit -eq 0) 'Authenticated retention must complete normally.'
        $backupAdmissionLease=Enter-AeroLinkTransition -InstallationRoot $backupInstallation -Policy Preserve
        Check ($backupAdmissionLease.Owner) 'Installation admission must reopen after the retention child releases.'
        $releasedRootLock=[IO.File]::Open((Join-Path $canonicalBackups '.backup.lock'),[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
        $releasedRootLock.Dispose()
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'AeroLinkBackupRetention.psm1') -Destination $backupScripts -Force
        Remove-Item -LiteralPath $helperEvent
        Exit-AeroLinkTransition $backupAdmissionLease
        $backupAdmissionLease=$null
        $heldRootLock=[IO.File]::Open((Join-Path $canonicalBackups '.backup.lock'),[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
        try {
            $entryPreference=$ErrorActionPreference
            try {
                $ErrorActionPreference='Continue'
                & $powershell -NoProfile -ExecutionPolicy Bypass -File $entryDriver -Entry $retentionEntry -Installation $backupInstallation -BackupRoot $canonicalBackups -Helper $helperEvent *> (Join-Path $root 'retention-root-lock-failure.log')
                $entryCode=$LASTEXITCODE
            } finally { $ErrorActionPreference=$entryPreference }
            Check ($entryCode -ne 0 -and (Get-Content -LiteralPath (Join-Path $root 'retention-root-lock-failure.log') -Raw) -match 'Another backup or retention operation owns') 'A held archive-root lock must retain its existing refusal.'
            $backupAdmissionLease=Enter-AeroLinkTransition -InstallationRoot $backupInstallation -Policy Preserve
            Check ($backupAdmissionLease.Owner) 'Root-lock acquisition failure must release installation admission.'
        } finally { $heldRootLock.Dispose() }
        Save-AeroLinkProductionObligation -Obligation ([pscustomobject]@{SourceRoot=$root;PriorTunnel=$true;Stage='Quiesced';Discharged=$false})
        Exit-AeroLinkTransition $backupAdmissionLease
        $backupAdmissionLease=$null
        $pendingJournal=Join-Path $backupInstallation 'bootstrap\home-transition-intent.json'
        $pendingBefore=[IO.File]::ReadAllBytes($pendingJournal)
        foreach ($entry in @('Backup-AeroLink.ps1','Remove-AeroLinkSurplusBackups.ps1')) {
            $extra = if ($entry -eq 'Remove-AeroLinkSurplusBackups.ps1') { @('-BackupRoot',$canonicalBackups) } else { @() }
            $entryPreference=$ErrorActionPreference
            try {
                $ErrorActionPreference='Continue'
                & $powershell -NoProfile -ExecutionPolicy Bypass -File $entryDriver -Entry (Join-Path $backupScripts $entry) -Installation $backupInstallation -Helper $helperEvent @extra *> (Join-Path $root ($entry + '.pending.log'))
                $entryCode=$LASTEXITCODE
            } finally { $ErrorActionPreference=$entryPreference }
            Check ($entryCode -ne 0 -and (Get-Content -LiteralPath (Join-Path $root ($entry + '.pending.log')) -Raw) -match 'requires recovery' -and -not(Test-Path -LiteralPath $helperEvent)) "$entry must not perform backup/retention work over pending recovery."
            Check ([Convert]::ToBase64String([IO.File]::ReadAllBytes($pendingJournal)) -eq [Convert]::ToBase64String($pendingBefore)) "$entry must preserve the pending recovery journal."
        }
        $missingInstallation=Join-Path $root 'not-an-installation'
        $entryPreference=$ErrorActionPreference
        try {
            $ErrorActionPreference='Continue'
            & $powershell -NoProfile -ExecutionPolicy Bypass -File $entryDriver -Entry $retentionEntry -Installation $missingInstallation -BackupRoot $externalBackups -Helper $helperEvent -Explicit *> (Join-Path $root 'invalid-explicit-retention.log')
            $entryCode=$LASTEXITCODE
        } finally { $ErrorActionPreference=$entryPreference }
        Check ($entryCode -ne 0 -and (Get-Content -LiteralPath (Join-Path $root 'invalid-explicit-retention.log') -Raw) -match 'explicit InstallationRoot must name an existing absolute' -and -not(Test-Path -LiteralPath $missingInstallation)) 'An invalid explicit association must refuse without initializing an installation.'
        $partialInstallations=@(($backupInstallation.Substring(0,2)+(Split-Path $backupInstallation -Leaf)),$backupInstallation.Substring(2))
        for ($partialIndex=0; $partialIndex -lt $partialInstallations.Count; $partialIndex++) {
            $partialLog=Join-Path $root ('partial-explicit-retention-'+$partialIndex+'.log')
            $entryPreference=$ErrorActionPreference
            try {
                $ErrorActionPreference='Continue'
                & $powershell -NoProfile -ExecutionPolicy Bypass -File $entryDriver -Entry $retentionEntry -Installation $partialInstallations[$partialIndex] -WorkingDirectory $root -BackupRoot $externalBackups -Helper $helperEvent -Explicit *> $partialLog
                $entryCode=$LASTEXITCODE
            } finally { $ErrorActionPreference=$entryPreference }
            $partialOutput=Get-Content -LiteralPath $partialLog -Raw
            Check ($entryCode -ne 0 -and $partialOutput -match 'Existing partial path target verified' -and $partialOutput -match 'explicit InstallationRoot must name an existing absolute') 'Drive-relative and root-relative existing targets must refuse explicit association before installation lookup.'
            Check ([Convert]::ToBase64String([IO.File]::ReadAllBytes($pendingJournal)) -eq [Convert]::ToBase64String($pendingBefore)) 'Partial explicit association must not touch the existing installation journal.'
        }
        & $powershell -NoProfile -ExecutionPolicy Bypass -File $entryDriver -Entry $retentionEntry -Installation $missingInstallation -BackupRoot $externalBackups -Helper $helperEvent *> (Join-Path $root 'unavailable-unassociated-retention.log')
        Check ($LASTEXITCODE -eq 0 -and -not(Test-Path -LiteralPath $missingInstallation)) 'Unrelated retention must not require a usable source installation or guess one.'
        $entryPreference=$ErrorActionPreference
        try {
            $ErrorActionPreference='Continue'
            & $powershell -NoProfile -ExecutionPolicy Bypass -File $entryDriver -Entry $retentionEntry -Installation $missingInstallation -BackupRoot (Join-Path $missingInstallation 'backups') -Helper $helperEvent *> (Join-Path $root 'unavailable-canonical-retention.log')
            $entryCode=$LASTEXITCODE
        } finally { $ErrorActionPreference=$entryPreference }
        Check ($entryCode -ne 0 -and -not(Test-Path -LiteralPath $missingInstallation)) 'A failed intended canonical lookup must not downgrade or initialize another installation.'
    } finally {
        Set-Content -LiteralPath $retentionRelease -Value 'release'
        foreach ($streamName in @('stdout','stderr')) {
            $childLog=Join-Path $root ("retention-child.$streamName.log")
            if (Test-Path -LiteralPath $childLog) {
                try { Write-Host ("Retention child $streamName log: " + [IO.File]::ReadAllText($childLog)) }
                catch { Write-Host "Retention child $streamName log unavailable: $($_.Exception.Message)"; Check $false "Retention child $streamName log must be retained." }
            }
        }
        if ($retentionChild) { if (-not $retentionChild.HasExited) { $retentionChild.Kill(); $retentionChild.WaitForExit() }; $retentionChild.Dispose() }
        Exit-AeroLinkTransition $backupAdmissionLease
        $env:AEROLINK_PRODUCTION_OBLIGATION = $backupPriorObligation
    }

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

    # ---------------------------------------------------------------------------------------------------------
    # #1055 F801-1: the owned-tree selector binds every candidate AND every ancestry link to the lifetime the CIM
    # inventory described, and never turns a discovery gap into absence. Real processes first (the shipped module,
    # a real parent/child tree and an unrelated decoy); then the shipped selector text with its read-only seams
    # controlled and no termination at all.
    # ---------------------------------------------------------------------------------------------------------
    Import-Module (Join-Path $PSScriptRoot 'AeroLinkProcessTermination.psm1') -Force -DisableNameChecking
    Check (Test-AeroLinkTerminationNative) 'F801-1: the native identity-bound termination helper must be available on this host.'
    $treeWitness = Join-Path $root 'tree-witness.ps1'
    @'
param([string]$ChildPidPath, [int]$Seconds)
$ErrorActionPreference = 'Stop'
$powershell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
$child = Start-Process -FilePath $powershell -ArgumentList '-NoProfile -NonInteractive -Command "Start-Sleep -Seconds 300"' -WindowStyle Hidden -PassThru
Set-Content -LiteralPath $ChildPidPath -Value $child.Id
Start-Sleep -Seconds $Seconds
'@ | Set-Content -LiteralPath $treeWitness -Encoding UTF8
    $treeChildPidPath = Join-Path $root 'tree-child.pid'
    $treeParent = Start-Process -FilePath $powershell -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$treeWitness`" -ChildPidPath `"$treeChildPidPath`" -Seconds 300" -WindowStyle Hidden -PassThru
    for ($i = 0; $i -lt 200 -and $treeChildId -le 0; $i++) {
        if (Test-Path -LiteralPath $treeChildPidPath) {
            $text = (Get-Content -LiteralPath $treeChildPidPath -Raw).Trim()
            if ($text -match '^[0-9]+$') { $treeChildId = [int]$text }
        }
        if ($treeChildId -le 0) { Start-Sleep -Milliseconds 100 }
    }
    $treeDecoy = Start-Process -FilePath $powershell -ArgumentList '-NoProfile -NonInteractive -Command "Start-Sleep -Seconds 300"' -WindowStyle Hidden -PassThru
    Check ($treeChildId -gt 0) 'F801-1: the owned-tree witness must publish the pid of its real child.'
    $treeRootIdentity = New-AeroLinkProcessIdentity -ProcessId $treeParent.Id
    Check ($treeRootIdentity.creationFileTime -gt 0) 'F801-1: the witness root must have a canonical native identity.'
    $treeSelection = Get-AeroLinkOwnedTreeIdentities -RootIdentity $treeRootIdentity
    $treeSelectedIds = @($treeSelection.identities | ForEach-Object { [int]$_.processId })
    Check ($treeSelectedIds -contains $treeParent.Id) 'F801-1: the verified root must be selected.'
    Check ($treeSelectedIds -contains $treeChildId) 'F801-1: the real child of the verified root must be selected.'
    Check (-not ($treeSelectedIds -contains $treeDecoy.Id)) 'F801-1: an unrelated process must never be adopted into the owned tree.'
    # The machine is shared: an unrelated process whose stale parent pointer lands inside this root's pid space is
    # legitimately reported unresolved/replaced by design (and is never adopted). The assertions that matter here
    # are about THIS test's processes: none of them may ever be dismissed as unattributable or as a replacement,
    # and every identity the selection would terminate must be that live process's OWN lifetime.
    $treeOwnedPids = @($treeParent.Id, $treeChildId, $treeDecoy.Id)
    Check (-not @($treeSelection.unresolved | Where-Object { $treeOwnedPids -contains [int]$_.processId }).Count) 'F801-1: an owned process must never be reported as unattributable.'
    Check (-not @($treeSelection.replaced | Where-Object { $treeOwnedPids -contains [int]$_.processId }).Count) 'F801-1: an owned process must never be reported as a replacement.'
    foreach ($treeBound in @($treeSelection.identities)) {
        $treeLive = Test-AeroLinkProcessIdentity -Identity $treeBound
        Check ($treeLive.state -in @('Match', 'Gone')) "F801-1: the selected identity of pid $($treeBound.processId) must be that process's own lifetime (got $($treeLive.state))."
    }
    $treeChildIdentity = @($treeSelection.identities | Where-Object { [int]$_.processId -eq $treeChildId })[0]
    Check ([long]$treeChildIdentity.creationFileTime -eq [long](Get-AeroLinkProcessCreationFileTime -ProcessId $treeChildId).creationFileTime) 'F801-1: the chosen child identity is the child''s own native lifetime.'
    $treeStopped = 0
    foreach ($treeIdentity in @($treeSelection.identities | Sort-Object -Property @{ Expression = { [int]$_.depth } } -Descending)) {
        $treeOutcome = Stop-AeroLinkVerifiedIdentity -Identity $treeIdentity
        Check ($treeOutcome.state -in @('Stopped', 'AlreadyGone')) "F801-1: the bound identity of pid $($treeIdentity.processId) must be settled (got $($treeOutcome.state))."
        if ($treeOutcome.state -eq 'Stopped') { $treeStopped++ }
    }
    Check ($treeStopped -ge 2) 'F801-1: the real owned tree must be stopped through identity-bound termination.'
    Check ((Get-AeroLinkProcessCreationFileTime -ProcessId $treeParent.Id).state -eq 'Gone') 'F801-1: the stopped root must be positively proven gone.'
    Check ((Get-AeroLinkProcessCreationFileTime -ProcessId $treeChildId).state -eq 'Gone') 'F801-1: the stopped child must be positively proven gone.'
    Check ((Test-AeroLinkProcessIdentity -Identity (New-AeroLinkProcessIdentity -ProcessId $treeDecoy.Id)).state -eq 'Match') 'F801-1: an unrelated process must survive the owned tree''s cleanup.'

    # The SHIPPED selector text with controlled inventory/native-read seams; the destructive seam is never called.
    $terminationSource = Join-Path $PSScriptRoot 'AeroLinkProcessTermination.psm1'
    $terminationTokens = $null; $terminationErrors = $null
    $terminationAst = [System.Management.Automation.Language.Parser]::ParseFile($terminationSource, [ref]$terminationTokens, [ref]$terminationErrors)
    $selectorNode = $terminationAst.Find({ param($candidate) $candidate -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $candidate.Name -eq 'Get-AeroLinkOwnedTreeIdentities' }, $true)
    if (-not $selectorNode) { throw 'F801-1: the termination module no longer defines Get-AeroLinkOwnedTreeIdentities.' }
    . ([scriptblock]::Create($selectorNode.Extent.Text))
    function New-F801Item([int]$ProcessId, [int]$ParentProcessId, [long]$CreationFileTime) {
        [pscustomobject]@{ ProcessId = $ProcessId; ParentProcessId = $ParentProcessId; CreationDate = [DateTime]::FromFileTimeUtc($CreationFileTime)
            Name = 'owned.exe'; ExecutablePath = 'C:\owned.exe' }
    }
    function New-F801Native([long]$CreationFileTime) { [ordered]@{ state = 'Ok'; creationFileTime = $CreationFileTime; detail = '' } }
    function Test-AeroLinkProcessIdentity { param($Identity) [ordered]@{ state = 'Match'; detail = '' } }
    function Get-AeroLinkProcessSnapshot { @($script:f801Inventory) }
    function Get-AeroLinkProcessCreationFileTime { param([int]$ProcessId) $script:f801Native[$ProcessId] }
    function Get-F801Ids($Entries) { @(@($Entries) | ForEach-Object { [int]$_.processId } | Sort-Object) -join ',' }
    $f801Epoch = [DateTime]::Parse('2026-09-19T10:00:00Z').ToUniversalTime().ToFileTimeUtc()
    $script:f801Root = [ordered]@{ processId = 100; creationFileTime = $f801Epoch }
    $f801RootOnly = @(100)
    $f801Cases = @(
        @{ Name = 'stable-child'; Expect = 'Match'; Selected = @(100, 101); Replaced = @(); Unresolved = @()
           Inventory = @((New-F801Item 100 99 $f801Epoch), (New-F801Item 101 100 ($f801Epoch + 10000000)))
           Native = @{ 100 = (New-F801Native $f801Epoch); 101 = (New-F801Native ($f801Epoch + 10000000)) } },
        @{ Name = 'stable-child-within-measured-cim-skew'; Expect = 'Match'; Selected = @(100, 101); Replaced = @(); Unresolved = @()
           Inventory = @((New-F801Item 100 99 $f801Epoch), (New-F801Item 101 100 ($f801Epoch + 10000000)))
           Native = @{ 100 = (New-F801Native $f801Epoch); 101 = (New-F801Native ($f801Epoch + 10000009)) } },
        @{ Name = 'child-replaced-after-inventory'; Expect = 'Match'; Selected = $f801RootOnly; Replaced = @(101); Unresolved = @()
           Inventory = @((New-F801Item 100 99 $f801Epoch), (New-F801Item 101 100 ($f801Epoch + 10000000)))
           Native = @{ 100 = (New-F801Native $f801Epoch); 101 = (New-F801Native ($f801Epoch + 90000000)) } },
        @{ Name = 'child-replaced-inside-one-second'; Expect = 'Match'; Selected = $f801RootOnly; Replaced = @(101); Unresolved = @()
           Inventory = @((New-F801Item 100 99 $f801Epoch), (New-F801Item 101 100 ($f801Epoch + 10000000)))
           Native = @{ 100 = (New-F801Native $f801Epoch); 101 = (New-F801Native ($f801Epoch + 15000000)) } },
        @{ Name = 'intermediate-parent-replaced-after-inventory'; Expect = 'Unknown'; Selected = $f801RootOnly; Replaced = @(101); Unresolved = @(102)
           Inventory = @((New-F801Item 100 99 $f801Epoch), (New-F801Item 101 100 ($f801Epoch + 10000000)), (New-F801Item 102 101 ($f801Epoch + 20000000)))
           Native = @{ 100 = (New-F801Native $f801Epoch); 101 = (New-F801Native ($f801Epoch + 90000000)); 102 = (New-F801Native ($f801Epoch + 20000000)) } },
        @{ Name = 'unreadable-child-identity'; Expect = 'Unknown'; Selected = $f801RootOnly; Replaced = @(); Unresolved = @(101)
           Inventory = @((New-F801Item 100 99 $f801Epoch), (New-F801Item 101 100 ($f801Epoch + 10000000)))
           Native = @{ 100 = (New-F801Native $f801Epoch); 101 = [ordered]@{ state = 'Unknown'; creationFileTime = 0; detail = 'Access denied' } } },
        @{ Name = 'unreadable-intermediate-identity'; Expect = 'Unknown'; Selected = $f801RootOnly; Replaced = @(); Unresolved = @(101, 102)
           Inventory = @((New-F801Item 100 99 $f801Epoch), (New-F801Item 101 100 ($f801Epoch + 10000000)), (New-F801Item 102 101 ($f801Epoch + 20000000)))
           Native = @{ 100 = (New-F801Native $f801Epoch); 101 = [ordered]@{ state = 'Unknown'; creationFileTime = 0; detail = 'Access denied' }; 102 = (New-F801Native ($f801Epoch + 20000000)) } },
        @{ Name = 'child-positively-gone'; Expect = 'Match'; Selected = $f801RootOnly; Replaced = @(); Unresolved = @()
           Inventory = @((New-F801Item 100 99 $f801Epoch), (New-F801Item 101 100 ($f801Epoch + 10000000)))
           Native = @{ 100 = (New-F801Native $f801Epoch); 101 = [ordered]@{ state = 'Gone'; creationFileTime = 0; detail = 'no such process' } } },
        # A live orphan under a POSITIVELY GONE ancestor: a vanished parent proves only that parent gone, so the
        # child is reported with its own bound lifetime and withholds a clean verdict (Astra review 4979b47d).
        @{ Name = 'live-orphan-with-gone-ancestor-is-unresolved'; Expect = 'Unknown'; Selected = $f801RootOnly; Replaced = @(); Unresolved = @(104)
           Inventory = @((New-F801Item 100 99 $f801Epoch), (New-F801Item 103 100 ($f801Epoch + 5000000)), (New-F801Item 104 103 ($f801Epoch + 10000000)))
           Native = @{ 100 = (New-F801Native $f801Epoch); 103 = [ordered]@{ state = 'Gone'; creationFileTime = 0; detail = 'no such process' }; 104 = (New-F801Native ($f801Epoch + 10000000)) } },
        # F497-2: a distinct lifetime 5,000 ticks (0.5 ms) after the snapshot's own lifetime must never be adopted.
        @{ Name = 'close-but-distinct-lifetime-is-rejected'; Expect = 'Match'; Selected = $f801RootOnly; Replaced = @(101); Unresolved = @()
           Inventory = @((New-F801Item 100 99 $f801Epoch), (New-F801Item 101 100 ($f801Epoch + 10000000)))
           Native = @{ 100 = (New-F801Native $f801Epoch); 101 = (New-F801Native ($f801Epoch + 10005000)) } },
        # F497-2: the genuine conversion difference (native 9 ticks ahead, inside the same microsecond) IS the same
        # lifetime, and the identity the selection hands out is the NATIVE one.
        @{ Name = 'conversion-skew-inside-the-representation-is-bound'; Expect = 'Match'; Selected = @(100, 101); Replaced = @(); Unresolved = @()
           Inventory = @((New-F801Item 100 99 $f801Epoch), (New-F801Item 101 100 ($f801Epoch + 10000000)))
           Native = @{ 100 = (New-F801Native $f801Epoch); 101 = (New-F801Native ($f801Epoch + 10000009)) } },
        # A snapshot entry outside the CIM microsecond representation cannot be bound at all: Unknown, fail closed.
        @{ Name = 'snapshot-entry-outside-the-cim-representation'; Expect = 'Unknown'; Selected = $f801RootOnly; Replaced = @(); Unresolved = @(101)
           Inventory = @((New-F801Item 100 99 $f801Epoch), (New-F801Item 101 100 ($f801Epoch + 10000005)))
           Native = @{ 100 = (New-F801Native $f801Epoch); 101 = (New-F801Native ($f801Epoch + 10000005)) } },
        @{ Name = 'child-inventory-entry-without-creation-time'; Expect = 'Unknown'; Selected = $f801RootOnly; Replaced = @(); Unresolved = @(101)
           Inventory = @((New-F801Item 100 99 $f801Epoch), (New-F801Item 101 100 0))
           Native = @{ 100 = (New-F801Native $f801Epoch); 101 = (New-F801Native ($f801Epoch + 10000000)) } },
        @{ Name = 'root-replaced-after-inventory'; Expect = 'Reused'; Selected = @(); Replaced = @(100); Unresolved = @()
           Inventory = @((New-F801Item 100 99 $f801Epoch), (New-F801Item 101 100 ($f801Epoch + 10000000)))
           Native = @{ 100 = (New-F801Native ($f801Epoch + 90000000)); 101 = (New-F801Native ($f801Epoch + 10000000)) } },
        @{ Name = 'root-identity-unreadable'; Expect = 'Unknown'; Selected = @(); Replaced = @(); Unresolved = @(100)
           Inventory = @((New-F801Item 100 99 $f801Epoch), (New-F801Item 101 100 ($f801Epoch + 10000000)))
           Native = @{ 100 = [ordered]@{ state = 'Unknown'; creationFileTime = 0; detail = 'Access denied' }; 101 = (New-F801Native ($f801Epoch + 10000000)) } },
        @{ Name = 'root-positively-gone'; Expect = 'Gone'; Selected = @(); Replaced = @(); Unresolved = @()
           Inventory = @((New-F801Item 100 99 $f801Epoch))
           Native = @{ 100 = [ordered]@{ state = 'Gone'; creationFileTime = 0; detail = 'no such process' } } }
    )
    foreach ($f801 in $f801Cases) {
        $script:f801Inventory = @($f801.Inventory)
        $script:f801Native = $f801.Native
        $tree = Get-AeroLinkOwnedTreeIdentities -RootIdentity $script:f801Root
        $label = "F801-1 $($f801.Name)"
        Check ($tree.state -eq $f801.Expect) "$label`: the selector must report $($f801.Expect) (got $($tree.state): $($tree.detail))."
        Check ((Get-F801Ids $tree.identities) -eq ((@($f801.Selected) | Sort-Object) -join ',')) "$label`: the selected identities must be exactly [$((@($f801.Selected) | Sort-Object) -join ',')] (got [$(Get-F801Ids $tree.identities)])."
        Check ((Get-F801Ids $tree.replaced) -eq ((@($f801.Replaced) | Sort-Object) -join ',')) "$label`: the replaced identities must be exactly [$((@($f801.Replaced) | Sort-Object) -join ',')] (got [$(Get-F801Ids $tree.replaced)])."
        Check ((Get-F801Ids $tree.unresolved) -eq ((@($f801.Unresolved) | Sort-Object) -join ',')) "$label`: the unresolved identities must be exactly [$((@($f801.Unresolved) | Sort-Object) -join ',')] (got [$(Get-F801Ids $tree.unresolved)])."
        # Every adopted identity must be the process's OWN current lifetime: the selection may never hand a caller
        # an identity the live pid does not actually hold (the case expectations above are the oracle - this only
        # re-reads the identity the selection produced).
        foreach ($adopted in @($tree.identities)) {
            $live = $f801.Native[[int]$adopted.processId]
            if (-not $live -or [string]$live.state -ne 'Ok') { Check $false "$label`: pid $($adopted.processId) was selected although no live lifetime was read for it." ; continue }
            Check ([long]$adopted.creationFileTime -eq [long]$live.creationFileTime) "$label`: the selected identity of pid $($adopted.processId) must be the live lifetime the native read returned."
        }
    }
}
finally {
    Exit-AeroLinkTransition $lease
    $env:AEROLINK_TRANSITION_LEASE = $savedCapability
    if ($continuingChild) { if (-not $continuingChild.HasExited) { $continuingChild.Kill(); $continuingChild.WaitForExit() }; $continuingChild.Dispose() }
    if ($child) { if (-not $child.HasExited) { $child.Kill(); $child.WaitForExit() }; $child.Dispose() }
    if ($helper) { if (-not $helper.Process.HasExited) { $helper.Process.Kill(); $helper.Process.WaitForExit() }; $helper.Process.Dispose() }
    # The F801-1 tree processes are disposable and owned by this suite: the parent is closed through the live
    # Process object that holds its handle, and the child (which an early failure could leave orphaned) only ever
    # through the module's identity-bound stop. Module-qualified calls: the controlled seams above shadow the
    # module's own commands in this script scope.
    if ($treeParent) { if (-not $treeParent.HasExited) { $treeParent.Kill(); $treeParent.WaitForExit() }; $treeParent.Dispose() }
    if ($treeChildId -gt 0) {
        $treeChildLive = AeroLinkProcessTermination\Get-AeroLinkProcessCreationFileTime -ProcessId $treeChildId
        if ($treeChildLive.state -eq 'Ok') {
            AeroLinkProcessTermination\Stop-AeroLinkVerifiedIdentity -Identity ([ordered]@{ processId = $treeChildId; creationFileTime = [long]$treeChildLive.creationFileTime }) | Out-Null
        }
    }
    if ($treeDecoy) { if (-not $treeDecoy.HasExited) { $treeDecoy.Kill(); $treeDecoy.WaitForExit() }; $treeDecoy.Dispose() }
    $resolved = [IO.Path]::GetFullPath($root)
    if (-not $resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected fixture cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
if ($failures.Count) { $failures | ForEach-Object { Write-Host "FAIL: $_" }; exit 1 }
Write-Host 'Managed-process and transition-lease contracts passed (disposable interactive processes; S4U not claimed).'
