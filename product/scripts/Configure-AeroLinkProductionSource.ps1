#Requires -Version 5.1
<#
    Operator CLI for the dedicated HOME production source.

    Actions:
      Preview   Show exactly what Install would create and where, and change nothing.
      Install   Create (or confirm) the dedicated production clone, point it at this machine's canonical
                AeroLink installation, and write the per-user production-source configuration.
      Status    Read-only: where the production source is, whether it is canonical, and how it relates to
                origin/main.
      Update    Bring the production source to the current approved origin/main by strict fast-forward.

    This never touches the development checkout it is run from beyond reading its origin URL and its
    installation root, and it never initializes, copies, or migrates persistent AeroLink data.
#>
[CmdletBinding()]
param(
    [ValidateSet('Preview', 'Install', 'Status', 'Update')]
    [string]$Action = 'Preview',

    # Where the dedicated production source should live. The default sits beside the development checkout so
    # both are visible in one place, and neither is inside the other.
    [string]$SourceRoot,

    # The canonical persistent installation the production source must use. Defaults to this checkout's,
    # which is the point: source is separated, data is not.
    [string]$InstallationRoot
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'AeroLinkProductionSource.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'AeroLinkInstallation.psm1') -Force

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$productRoot = Join-Path $repositoryRoot 'product'

if (-not $SourceRoot) { $SourceRoot = Join-Path (Split-Path $repositoryRoot -Parent) 'AeroLink Production' }
if (-not $InstallationRoot) { $InstallationRoot = (Get-AeroLinkInstallationPaths -ProductRoot $productRoot).InstallationRoot }

switch ($Action) {
    'Preview' {
        Write-Host 'AeroLink dedicated production source - preview' -ForegroundColor Cyan
        Write-Host ''
        Write-Host "Development checkout (untouched): $repositoryRoot"
        Write-Host "Production source to create:      $SourceRoot"
        Write-Host "Canonical installation to use:    $InstallationRoot"
        Write-Host "Configuration to write:           $(Get-AeroLinkProductionSourceConfigPath)"
        Write-Host ''
        Write-Host 'Install would clone origin/main into the production source, mark it as dedicated'
        Write-Host 'production source, and record an installation pointer so it runs the canonical HOME'
        Write-Host 'PostgreSQL, evidence, attachments and backups rather than initializing new ones.'
        Write-Host 'No persistent data is created, copied, reset, or migrated.'
        exit 0
    }
    'Install' {
        $result = Initialize-AeroLinkProductionSource -SourceRoot $SourceRoot -InstallationRoot $InstallationRoot `
            -ReferenceRepositoryRoot $repositoryRoot -WriteConfig
        $result | Format-List
        if (-not $result.Canonical) {
            Write-Host "The production source exists but is not canonical: $($result.Reason)" -ForegroundColor Yellow
            exit 1
        }
        # Declare the installation HOME CANONICAL as part of setting HOME production up.
        #
        # Not only a badge. Import-AeroLinkHomeSnapshot refuses to overwrite an installation declared
        # HomeCanonical, so an installation left Undeclared — which is what a normally configured HOME was,
        # because nothing established it — had no protection against having the canonical database replaced
        # by a laptop snapshot. Declaring it is what arms that guard.
        $existingInstance = Get-AeroLinkInstanceConfig -ProductRoot $productRoot -Mode HomeCanonical -EnsureInstanceId
        if ($existingInstance.Classification -eq 'HomeCanonical') {
            Write-Host "Instance already declared: $($existingInstance.Label) ($($existingInstance.Classification))." -ForegroundColor DarkGray
        }
        elseif ($existingInstance.Classification -ne 'Undeclared') {
            Write-Host "This installation is declared $($existingInstance.Label) ($($existingInstance.Classification)), not HOME CANONICAL." -ForegroundColor Yellow
            Write-Host 'Leaving it alone: reclassifying an installation is an operator decision, not a side effect of' -ForegroundColor Yellow
            Write-Host 'setting up a production source. Correct it with Set-AeroLinkInstanceConfig if that is wrong.' -ForegroundColor Yellow
        }
        else {
            Set-AeroLinkInstanceConfig -ProductRoot $productRoot -Label 'HOME CANONICAL' -Classification 'HomeCanonical' | Out-Null
            Write-Host 'Instance declared: HOME CANONICAL.' -ForegroundColor Green
            Write-Host 'The HOME-to-laptop snapshot import will now refuse to replace this database.' -ForegroundColor Green
        }

        Write-Host 'AeroLink dedicated production source ready.' -ForegroundColor Green
        Write-Host 'Reinstall the remote-demo recovery task so it invokes this source:' -ForegroundColor DarkGray
        Write-Host '      CONFIGURE_AEROLINK_REMOTE_DEMO.bat' -ForegroundColor Gray
        exit 0
    }
    'Status' {
        $config = Get-AeroLinkProductionSourceConfig
        $posture = Get-AeroLinkProductionSourcePosture -SourceRoot $config.SourceRoot -RemoteName $config.RemoteName
        [pscustomobject]@{
            SourceRoot       = $config.SourceRoot
            InstallationRoot = (Get-AeroLinkInstallationPaths -ProductRoot (Join-Path $config.SourceRoot 'product')).InstallationRoot
            Dedicated        = $posture.Dedicated
            Canonical        = $posture.Canonical
            Branch           = if ($posture.Posture) { $posture.Posture.Branch } else { $null }
            HeadSha          = if ($posture.Posture) { $posture.Posture.ShortSha } else { $null }
            RemoteMainSha    = if ($posture.Posture) { $posture.Posture.ShortRemoteMainSha } else { $null }
            Relationship     = if ($posture.Posture) { $posture.Posture.Relationship } else { $null }
            Reason           = $posture.Reason
        } | Format-List
        exit 0
    }
    'Update' {
        # Delegate before mutating, for the same reason production itself delegates.
        #
        # This BAT runs the script from whichever checkout contains it, and in the supported architecture that
        # checkout is explicitly allowed to be a dirty feature branch under agent development. Update then
        # stops, advances and restarts canonical HOME production using THOSE bytes - unmerged development code
        # controlling the canonical transition, which is precisely the coupling #881 exists to remove. The
        # verified dedicated source is the control plane for a mutating operation on it.
        #
        # Preview, Install and Status stay development-side: Install has to run somewhere before a dedicated
        # source exists, and the read-only actions change nothing.
        $delegation = $null
        try { $delegation = Assert-AeroLinkRunningFromProductionSource -RepositoryRoot $repositoryRoot }
        catch { throw }
        if ($delegation.DelegateTo -and $env:AEROLINK_PRODUCTION_SOURCE_DELEGATED -ne '1') {
            $delegateScript = Join-Path $delegation.DelegateTo 'product\scripts\Configure-AeroLinkProductionSource.ps1'
            if (-not (Test-Path -LiteralPath $delegateScript -PathType Leaf)) {
                throw "The configured production source has no configuration script at $delegateScript. Nothing was changed."
            }
            Write-Host 'This checkout is not the dedicated production source.' -ForegroundColor Yellow
            Write-Host "      Running the update from: $($delegation.DelegateTo)" -ForegroundColor Cyan
            $previousDelegated = $env:AEROLINK_PRODUCTION_SOURCE_DELEGATED
            try {
                $env:AEROLINK_PRODUCTION_SOURCE_DELEGATED = '1'
                & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $delegateScript -Action Update
                exit $LASTEXITCODE
            }
            finally { $env:AEROLINK_PRODUCTION_SOURCE_DELEGATED = $previousDelegated }
        }
        if ($delegation.DelegateTo) {
            throw "Delegation did not reach the dedicated production source: $($delegation.Reason) Nothing was changed."
        }

        # Through the same inspect / stop / advance / restart controller as the timed pass, not a bare
        # fast-forward.
        #
        # This action is documented, an operator can run it at any time, and it used to fetch and fast-forward
        # immediately - so running it while production or the remote demo was live rewrote the working tree
        # underneath them. That is the same defect the scheduled reconciliation was corrected for; having one
        # controller and one exception to it is not having a controller.
        $config = Get-AeroLinkProductionSourceConfig
        Import-Module (Join-Path $PSScriptRoot 'AeroLinkRemoteDemo.psm1') -Force
        # Absent is not unreadable. A missing configuration means this machine has no remote demo; one that
        # exists and will not parse may still have a live tunnel behind it, and treating that as "no tunnel"
        # advances the source while the public endpoint keeps forwarding to a replaced runtime.
        $demoConfig = $null
        $demoConfigPath = Get-AeroLinkRemoteDemoConfigPath
        if (Test-Path -LiteralPath $demoConfigPath -PathType Leaf) {
            try { $demoConfig = Get-AeroLinkRemoteDemoConfig -ConfigPath $demoConfigPath }
            catch { throw "This machine has a remote-demo configuration at $demoConfigPath that could not be read ($($_.Exception.Message)). A tunnel started while it was valid may still be publishing port 5080, so the production source was NOT advanced and nothing was stopped." }
        }
        if ($demoConfig) {
            # -PreserveServiceState: this is an operator command, not the recovery timer. The scheduled pass
            # is deliberately keep-ready - having the demo up is its job - but a configuration file outlives
            # a demo somebody deliberately stopped, so an update that ended by starting the tunnel would
            # publish a public endpoint on the strength of a file existing. Restore what was running.
            $result = Invoke-AeroLinkProductionSourceReconciliation -Config $demoConfig -PreserveServiceState
            $result | Format-List
            exit ($(if ($result.Action -in 'Updated', 'AlreadyCurrent', 'CachedCanonical') { 0 } else { 1 }))
        }

        # No remote-demo configuration on this machine, so there is no tunnel and no supervised runtime to
        # coordinate with. Still two-phase: decide with a fetch, stop anything of ours executing out of the
        # tree, then advance.
        Import-Module (Join-Path $PSScriptRoot 'AeroLinkRuntimeIdentity.psm1') -Force
        $inspect = Update-AeroLinkProductionSource -SourceRoot $config.SourceRoot -RemoteName $config.RemoteName `
            -FetchTimeoutSeconds $config.FetchTimeoutSeconds -InspectOnly
        # What was ACTUALLY running, from the stop's own result rather than from the fact that a stop was
        # attempted. Assuming a runtime was there meant a refused advance could START production that had not
        # been running before this command was invoked - the opposite of preserving prior state.
        # An obligation inherited from the process that handed off to us: it stopped a runtime, advanced the
        # source, and handed the duty to restart it to this fresh process running the updated code. Bound to
        # the source root and cleared on read, so it is one-shot and local.
        $stoppedTheRuntime = ($env:AEROLINK_RUNTIME_OWED -eq $config.SourceRoot)
        $env:AEROLINK_RUNTIME_OWED = $null
        # Did the update already happen, in the process that handed off to us? Our own inspection will say
        # AlreadyCurrent and be right, and without this the continuation reported the update as not having
        # happened - and exited 1 - immediately after completing it.
        $sourceAlreadyAdvanced = ($env:AEROLINK_SOURCE_ALREADY_ADVANCED -eq $config.SourceRoot)
        $env:AEROLINK_SOURCE_ALREADY_ADVANCED = $null
        if ($inspect.Canonical -and $inspect.Action -eq 'UpdateAvailable') {
            Write-Host "      Stopping the production runtime before advancing to $($inspect.TargetSha)..." -ForegroundColor Yellow
            $apiDirectory = Join-Path $config.SourceRoot 'product\src\AeroLink.Api'
            $stopResult = Stop-AeroLinkOwnedListener -Port 5080 -OwnershipFragments @($apiDirectory)
            # -or, not =: an obligation inherited from the process that handed off to us is not cancelled by
            # this process finding nothing left to stop. It already stopped it.
            $stoppedTheRuntime = $stoppedTheRuntime -or [bool]$stopResult.Stopped
            if (-not $stoppedTheRuntime) { Write-Host '      No AeroLink-owned runtime was on 5080; none will be started by this command.' -ForegroundColor DarkGray }
            # Everything after the stop is inside the compensation boundary, the same as the scheduled pass
            # and the production bootstrap. This is a documented operator action; once it has taken a running
            # service down, a refused or failed advance must not leave it down to report that nothing
            # happened.
            try {
                $result = Update-AeroLinkProductionSource -SourceRoot $config.SourceRoot -RemoteName $config.RemoteName `
                    -FetchTimeoutSeconds $config.FetchTimeoutSeconds -AdvanceToSha $inspect.TargetSha
            }
            catch {
                $result = [pscustomobject]@{
                    Action = 'Refused'; Canonical = $false; HeadSha = $inspect.HeadSha; TargetSha = $inspect.TargetSha
                    RemoteReachable = $true; Reason = "The fast-forward failed and nothing was changed: $($_.Exception.Message)"
                }
            }
        }
        else { $result = $inspect }
        $result | Format-List

        # The control plane is part of what an update replaces, here too.
        #
        # This script has already loaded itself and its modules; a successful advance may have replaced any of
        # them, and everything below - posture re-check, restart, reporting - would then run on bytes the
        # update superseded. Hand the rest to a fresh process from the updated source, exactly as the
        # production launcher's re-entry and the remote-demo handoff do. AEROLINK_PRODUCTION_SOURCE_HANDOFF is
        # bound to the source root and consumed by the child, so it is one-shot and cannot recurse.
        if ($result.Action -eq 'Updated' -and $env:AEROLINK_PRODUCTION_SOURCE_HANDOFF -ne $config.SourceRoot) {
            $updatedScript = Join-Path $config.SourceRoot 'product\scripts\Configure-AeroLinkProductionSource.ps1'
            if (-not (Test-Path -LiteralPath $updatedScript -PathType Leaf)) {
                throw "The source was advanced but the updated tree has no configuration script at $updatedScript. The runtime was stopped and has NOT been restarted; start it with START_AEROLINK_PRODUCTION.bat."
            }
            Write-Host 'The source advanced; completing the update from the new revision...' -ForegroundColor Cyan
            $previousHandoff = $env:AEROLINK_PRODUCTION_SOURCE_HANDOFF
            $previousOwed = $env:AEROLINK_RUNTIME_OWED
            $previousAdvanced = $env:AEROLINK_SOURCE_ALREADY_ADVANCED
            $childExit = 1
            try {
                $env:AEROLINK_PRODUCTION_SOURCE_HANDOFF = $config.SourceRoot
                # The obligation crosses the process boundary: the child must know a runtime was taken down,
                # or the transition ends with production stopped and nothing owning the duty to restart it.
                if ($stoppedTheRuntime) { $env:AEROLINK_RUNTIME_OWED = $config.SourceRoot }
                # And it must know the update ALREADY HAPPENED, in this parent. Its own inspection will see
                # AlreadyCurrent - correctly - and without this it reported "THE SOURCE UPDATE DID NOT HAPPEN"
                # and exited 1 after successfully completing the very update it was continuing.
                $env:AEROLINK_SOURCE_ALREADY_ADVANCED = $config.SourceRoot
                & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $updatedScript -Action Update
                $childExit = $LASTEXITCODE
            }
            finally {
                $env:AEROLINK_PRODUCTION_SOURCE_HANDOFF = $previousHandoff
                $env:AEROLINK_RUNTIME_OWED = $previousOwed
                $env:AEROLINK_SOURCE_ALREADY_ADVANCED = $previousAdvanced
            }

            # The obligation is retained until the child positively discharges it. Exiting on the child's
            # status meant a child that failed before restarting left the source current, the runtime down,
            # and nobody owning the duty to bring it back - and a later pass would see nothing to do.
            if ($childExit -ne 0 -and $stoppedTheRuntime) {
                Write-Host 'THE UPDATE COMPLETED BUT THE CONTINUATION DID NOT' -ForegroundColor Yellow
                Write-Host 'The source was advanced. Restoring the production runtime that was stopped for it...' -ForegroundColor Yellow
                $onDisk = Get-AeroLinkProductionSourcePosture -SourceRoot $config.SourceRoot -RemoteName $config.RemoteName
                if (-not $onDisk.Canonical) {
                    throw "The source was advanced but the continuation failed, and the revision on disk is not canonical: $($onDisk.Reason) The production runtime is NOT running."
                }
                & (Join-Path $config.SourceRoot 'product\scripts\Start-AeroLinkProduction.ps1') -DoNotOpenBrowser
                Write-Host "Production was restored on main @ $($onDisk.Posture.ShortSha), but the update's continuation reported failure." -ForegroundColor Yellow
            }
            exit $childExit
        }

        # Restore exactly what was running, and only what was running.
        #
        # Both halves matter. A runtime that WAS up is owed a restart whether the advance succeeded or was
        # refused - this action is the same inspect / stop / advance / RESTART controller as the timed pass,
        # and telling the operator to go and start it again by hand is not that. A runtime that was NOT up
        # must not be created: an update command that leaves a production API listening because it was invoked
        # is a surprise, not a service.
        # The operation as a whole succeeded if the source advanced HERE or in the process that handed off to
        # us. Reporting only on this process's own inspection made a continuation announce "the update did not
        # happen" and exit 1 straight after completing the update it existed to finish - which outer
        # automation would read as a failed update that in fact succeeded.
        $updateHappened = ($result.Action -eq 'Updated') -or $sourceAlreadyAdvanced
        if ($stoppedTheRuntime) {
            if (-not $updateHappened) {
                Write-Host 'THE SOURCE UPDATE DID NOT HAPPEN' -ForegroundColor Yellow
                Write-Host $result.Reason -ForegroundColor Yellow
            }
            $onDisk = Get-AeroLinkProductionSourcePosture -SourceRoot $config.SourceRoot -RemoteName $config.RemoteName
            if (-not $onDisk.Canonical) {
                throw "The production runtime was stopped for this update, and the revision now on disk is not canonical: $($onDisk.Reason) AeroLink was NOT restarted."
            }
            Write-Host "Restarting production on main @ $($onDisk.Posture.ShortSha)..." -ForegroundColor Cyan
            & (Join-Path $config.SourceRoot 'product\scripts\Start-AeroLinkProduction.ps1') -DoNotOpenBrowser
            if ($updateHappened) { Write-Host 'The production source update is complete and production is running on it.' -ForegroundColor Green }
            exit ($(if ($updateHappened) { 0 } else { 1 }))
        }
        if ($updateHappened) {
            Write-Host 'The production source was advanced. Nothing was running here, so nothing was started.' -ForegroundColor Green
            Write-Host 'Start it with START_AEROLINK_PRODUCTION.bat when you want it.' -ForegroundColor DarkGray
        }
        exit ($(if ($updateHappened -or $result.Canonical) { 0 } else { 1 }))
    }
}
