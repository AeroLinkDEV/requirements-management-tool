#Requires -Version 5.1
<#
    AeroLink protected remote-demo operator CLI.

    Actions:
      Start      Start (or confirm) the protected remote demo: local production
                 AeroLink plus the policy-backed ngrok tunnel, then prove 401.
      Stop       Stop only the AeroLink-owned ngrok tunnel, or with
                 -IncludeLocalStack the whole remote-demo stack.
      Status     Read-only component status with a final
                 AEROLINK REMOTE DEMO READY / NOT READY verdict.
      Configure  Scheduled-recovery task management:
                 Preview | Install | Status | Remove.
      RequestRedeploy
                 Ask for production to be redeployed to origin/main now, inside the
                 Monday-Friday 08:00-18:00 Eastern hold (DEC-149), and start the
                 installed reconciliation task to do it.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    # Continue is not an operator action. It is the continuation half of a source transition: a process that
    # advanced the source hands the rest of the work to a fresh one running the UPDATED code, along with the
    # exact topology it took down and the policy that governs putting it back. Handing off to Start instead
    # meant the child could only guess, and its guess was "start the whole demo" - which republished a tunnel
    # an operator had deliberately stopped.
    [ValidateSet('Start', 'Stop', 'Status', 'Configure', 'Reconcile', 'Continue', 'RequestRedeploy')]
    [string]$Action,
    [ValidateSet('Preview', 'Install', 'Status', 'Remove')]
    [string]$ConfigureAction = 'Preview',
    [switch]$IncludeLocalStack,
    [switch]$Scheduled
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'AeroLinkRemoteDemo.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'AeroLinkProductionSource.psm1')

$moduleRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$localDemoRoot = Join-Path $env:LOCALAPPDATA 'AeroLink\RemoteDemo'

# The task must be registered against the DEDICATED production source, not against whichever checkout this
# script happens to have been started from. Before #881 this used $moduleRoot unconditionally, which is how
# a recovery task installed from the development checkout came to invoke the development checkout's own
# recovery script — the second half of the 2026-09-03 coupling, and the half a configured AeroLinkRoot alone
# would not have fixed.
$configureConfig = $null
try { $configureConfig = Get-AeroLinkRemoteDemoConfig }
catch {
    $configureConfig = [pscustomobject]@{
        AeroLinkRoot = $moduleRoot
        ProductionSourceRoot = $null
        ProductionSourceReason = "The remote-demo configuration could not be read: $($_.Exception.Message)"
        StatePath = Join-Path $localDemoRoot 'state'
        LogsPath = Join-Path $localDemoRoot 'logs'
        NgrokExecutable = ''
        PublicUrl = ''
        TrafficPolicyPath = ''
    }
}

$transitionLease = $null
if ($Action -in @('Start', 'Stop', 'Continue', 'Reconcile')) {
    Import-Module (Join-Path $PSScriptRoot 'AeroLinkTransition.psm1') -Force
    Import-Module (Join-Path $PSScriptRoot 'AeroLinkInstallation.psm1')
    $activeConfig = Get-AeroLinkRemoteDemoConfig
    $activeInstallation = Get-AeroLinkInstallationPaths -ProductRoot (Join-Path $activeConfig.AeroLinkRoot 'product')
    $policy = if ($Action -eq 'Start' -or $Action -eq 'Reconcile') { 'KeepReady' } else { 'Preserve' }
    $transitionLease = Enter-AeroLinkTransition -InstallationRoot $activeInstallation.InstallationRoot -Policy $policy
}
try {
switch ($Action) {
    'Start' {
        $config = Get-AeroLinkRemoteDemoConfig
        try {
            # Observation first. An idempotent start that finds the demo exactly ready creates no attempt at all.
            $assessment = Get-AeroLinkRemoteDemoStartAssessment -Config $config
            if ($assessment.Decision -eq 'Refused') { throw $assessment.Detail }
            if ($assessment.Decision -eq 'AlreadyReady') {
                Write-AeroLinkRemoteDemoLog -Config $config -Run (New-AeroLinkRemoteDemoRun -Scheduled:$Scheduled) -Message 'Remote demo already ready; no new processes started.'
                Write-Host 'AEROLINK REMOTE DEMO READY'
                Write-Host "Public URL: $($config.PublicUrl)"
                Write-Host $assessment.Detail
                exit 0
            }
            # Everything else is a transition, run by THIS process as its outer authority.
            $transition = Invoke-AeroLinkHomeTransitionOuter -InstallationRoot $activeInstallation.InstallationRoot -Lease $transitionLease -Operation RemoteDemoStart `
                -SourceRoot $config.AeroLinkRoot -Config $config -Policy KeepReady -Scheduled:$Scheduled -StreamToHost
            if ($transition.Decision -ne 'Completed') { throw $transition.Detail }
            Write-Host 'AEROLINK REMOTE DEMO READY'
            Write-Host "Public URL: $($config.PublicUrl)"
            Write-Host $transition.Detail
            exit 0
        }
        catch {
            $failureRun = New-AeroLinkRemoteDemoRun -Scheduled:$Scheduled
            Write-AeroLinkRemoteDemoLog -Config $config -Run $failureRun -Message "AEROLINK REMOTE DEMO NOT READY: $($_.Exception.Message)"
            Write-Host 'AEROLINK REMOTE DEMO NOT READY' -ForegroundColor Red
            Write-Host $_.Exception.Message
            exit $(if ($transition -and $transition.ExitCode) { $transition.ExitCode } else { 1 })
        }
    }
    'Stop' {
        $config = Get-AeroLinkRemoteDemoConfig
        Stop-AeroLinkRemoteDemo -Config $config -IncludeLocalStack:$IncludeLocalStack
        if ($env:AEROLINK_TRANSITION_JOURNAL -and (Test-Path -LiteralPath $env:AEROLINK_TRANSITION_JOURNAL)) {
            # An explicit successful operator Stop supersedes an interrupted restoration intent.
            Remove-Item -LiteralPath $env:AEROLINK_TRANSITION_JOURNAL -Force
        }
        exit 0
    }
    'Continue' {
        $config = Get-AeroLinkRemoteDemoConfig
        $continuation = Get-AeroLinkTransitionContinuation -SourceRoot $config.AeroLinkRoot
        if (-not $continuation) {
            Write-Host 'AEROLINK TRANSITION CONTINUATION NOT FOUND' -ForegroundColor Red
            Write-Host 'This action only runs as the continuation of a source transition, and none was handed to it.'
            Write-Host 'Nothing was started. Use Start if you want the protected remote demo.'
            exit 1
        }
        try {
            $restored = Restore-AeroLinkServiceTopology -Config $config -Topology $continuation.Topology `
                -KeepReady:$continuation.KeepReady -Scheduled:$Scheduled -Run (New-AeroLinkRemoteDemoRun -Scheduled:$Scheduled)
            Write-Host 'AEROLINK TRANSITION CONTINUED'
            Write-Host $restored.Detail
            exit 0
        }
        catch {
            Write-Host 'AEROLINK TRANSITION CONTINUATION FAILED' -ForegroundColor Red
            Write-Host $_.Exception.Message
            exit 1
        }
    }
    'Reconcile' {
        # Bounded polling: advance the dedicated production source, and restart production into it only when
        # origin/main actually moved. A machine that stays up for weeks should not stay weeks behind.
        $config = Get-AeroLinkRemoteDemoConfig
        try {
            Assert-AeroLinkDedicatedProductionSource -SourceRoot $config.AeroLinkRoot | Out-Null
            $inspect = Update-AeroLinkProductionSource -SourceRoot $config.AeroLinkRoot -InspectOnly
            # One redeploy request lets one pass through, so every pass that finds a request takes it, whatever that
            # pass then decides (DEC-149).
            $redeployRequest = Receive-AeroLinkRedeployRequest -Config $config
            if (-not $inspect.Canonical -or $inspect.Action -ne 'UpdateAvailable') {
                # Deliberately does NOTHING when the source has not moved, including when the demo is down: this is a
                # bounded SOURCE reconciler, and an operator's explicit Stop must stay stopped.
                if ($redeployRequest) {
                    Write-AeroLinkRemoteDemoLog -Config $config -Run (New-AeroLinkRemoteDemoRun -Scheduled:$Scheduled) -Message "Manual redeploy request taken with nothing to redeploy: $($inspect.Action) - $($inspect.Reason)"
                }
                Write-Host "AEROLINK PRODUCTION SOURCE $($inspect.Action.ToUpperInvariant())"
                Write-Host $inspect.Reason
                if ($inspect.Action -notin @('AlreadyCurrent', 'CachedCanonical')) { exit 1 }
                exit 0
            }
            if ($Scheduled) {
                # DEC-149: Monday-Friday 08:00-18:00 Eastern a timed pass redeploys only on a manual request. Decided
                # here, before the transition, so a held pass stops nothing; the inspection above has still refreshed
                # the main-currency observation the instance badge reads.
                $redeploy = Get-AeroLinkScheduledRedeployDecision -UtcNow (Get-Date).ToUniversalTime() -Request $redeployRequest
                if (-not $redeploy.Proceed -or $redeployRequest) {
                    Write-AeroLinkRemoteDemoLog -Config $config -Run (New-AeroLinkRemoteDemoRun -Scheduled:$Scheduled) -Message "$($inspect.Reason) $($redeploy.Detail)"
                }
                if (-not $redeploy.Proceed) {
                    Write-Host 'AEROLINK PRODUCTION SOURCE HELD'
                    Write-Host "$($inspect.Reason) $($redeploy.Detail)"
                    exit 0
                }
            }
            $transition = Invoke-AeroLinkHomeTransitionOuter -InstallationRoot $activeInstallation.InstallationRoot -Lease $transitionLease -Operation Reconcile `
                -SourceRoot $config.AeroLinkRoot -Config $config -Policy KeepReady -Scheduled:$Scheduled -StreamToHost `
                -AttemptDeadlineSeconds (Get-AeroLinkTransitionBudget).ContinuationSeconds
            Write-Host "AEROLINK PRODUCTION SOURCE $(if ($transition.Decision -eq 'Completed') { 'UPDATED' } else { $transition.Decision.ToUpperInvariant() })"
            Write-Host $transition.Detail
            exit $transition.ExitCode
        }
        catch {
            Write-Host 'AEROLINK PRODUCTION SOURCE RECONCILIATION FAILED' -ForegroundColor Red
            Write-Host $_.Exception.Message
            exit 1
        }
    }
    'RequestRedeploy' {
        # DEC-149: inside the work-hours hold a timed pass redeploys only when asked, and this is the asking. It records
        # the request and runs the installed reconciliation task now rather than wait up to 30 minutes for it. The
        # task, not this console, carries the transition, so the attested launch context is the one it always was.
        $config = Get-AeroLinkRemoteDemoConfig
        $request = Write-AeroLinkRedeployRequest -Config $config -UtcNow (Get-Date).ToUniversalTime()
        Write-AeroLinkRemoteDemoLog -Config $config -Run (New-AeroLinkRemoteDemoRun) -Message 'Manual redeploy requested; the reconciliation task takes the request on its next pass.'
        Write-Host 'AEROLINK REDEPLOY REQUESTED'
        Write-Host "Request recorded at $($request.RequestedAtUtc.ToString('o')) ($($request.Path)); it is honoured for two hours."
        try {
            Start-ScheduledTask -TaskName 'AeroLinkProductionSourceReconcile' -ErrorAction Stop
            Write-Host 'The reconciliation task was started. If origin/main has moved, AeroLink and the protected tunnel are down'
            Write-Host 'for about six to eight minutes while it redeploys. If a pass was already running, the next pass takes the'
            Write-Host 'request. AEROLINK_REMOTE_DEMO_STATUS.bat and remote-demo.log show the outcome.'
        }
        catch {
            Write-Host "The reconciliation task could not be started now: $($_.Exception.Message)" -ForegroundColor Yellow
            Write-Host 'The request stands, and the next scheduled pass (within 30 minutes) takes it.'
        }
        exit 0
    }
    'Status' {
        $config = Get-AeroLinkRemoteDemoConfig
        $status = Get-AeroLinkRemoteDemoStatus -Config $config
        $status.Checks | Format-Table -AutoSize
        Write-Host $status.Overall
        exit 0
    }
    'Configure' {
        switch ($ConfigureAction) {
            'Preview' {
                Write-Host "Production source: $($configureConfig.AeroLinkRoot)"
                Write-Host "Resolution: $($configureConfig.ProductionSourceReason)"
                Write-Host ''
                Write-Host (Get-AeroLinkRemoteDemoTaskXml -Config $configureConfig)
                exit 0
            }
            'Install' {
                $task = Install-AeroLinkRemoteDemoTask -Config $configureConfig
                $task | Format-List
                if (-not $task.UnattendedBootRecovery) {
                    Write-Host 'This machine would not accept the unattended (S4U) principal, so recovery happens at' -ForegroundColor Yellow
                    Write-Host 'sign-in rather than at boot. A reboot with nobody logged in will NOT recover the demo.' -ForegroundColor Yellow
                }
                $reconcileTask = Install-AeroLinkReconcileTask -Config $configureConfig
                $reconcileTask | Format-List
                Write-Host 'AeroLink remote-demo recovery and production-source reconciliation tasks installed'
                Write-Host '(current user, no admin, no secrets), both bound to the dedicated production source.'
                exit 0
            }
            'Status' {
                Get-AeroLinkRemoteDemoTaskStatus | Format-List
                Get-AeroLinkRemoteDemoTaskStatus -TaskName 'AeroLinkProductionSourceReconcile' | Format-List
                exit 0
            }
            'Remove' {
                Remove-AeroLinkRemoteDemoTask | Format-List
                Remove-AeroLinkRemoteDemoTask -TaskName 'AeroLinkProductionSourceReconcile' | Format-List
                exit 0
            }
        }
    }
}

} finally { if ($transitionLease) { Exit-AeroLinkTransition -Lease $transitionLease } }
