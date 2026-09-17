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

$transitionLease = $null
if ($Action -eq 'Update') {
    Import-Module (Join-Path $PSScriptRoot 'AeroLinkTransition.psm1') -Force
    $transitionLease = Enter-AeroLinkTransition -InstallationRoot $InstallationRoot -Policy Preserve
}
try {
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
        # One mutating path for the dedicated production source, run by THIS process as the outer authority of a
        # HOME transition (#1041, #1043, #1053). Nothing is stopped, advanced or restarted in this process: the
        # delegate actor does that inside the attempt's job, from the verified dedicated source, and this process
        # verifies what it left running.
        #
        # The dedicated source is still the control plane for a mutation of it (#881): when this BAT runs from any
        # other checkout, the delegate actor is the DEDICATED source's own, and it refuses unless it proves it runs
        # exactly the source the handoff names. Preview, Install and Status stay development-side.
        Import-Module (Join-Path $PSScriptRoot 'AeroLinkRemoteDemo.psm1') -Force
        Import-Module (Join-Path $PSScriptRoot 'AeroLinkRuntimeIdentity.psm1') -Force

        # A continuation handed to this process by a PRE-#1055 parent (the legacy environment contract). That parent
        # advanced the source and holds the lease; this process only restores what it stopped, exactly as before.
        if ($env:AEROLINK_PRODUCTION_SOURCE_HANDOFF) {
            $config = Get-AeroLinkProductionSourceConfig
            $owed = ($env:AEROLINK_RUNTIME_OWED -eq $config.SourceRoot)
            $alreadyAdvanced = ($env:AEROLINK_SOURCE_ALREADY_ADVANCED -eq $config.SourceRoot)
            $env:AEROLINK_RUNTIME_OWED = $null
            $env:AEROLINK_SOURCE_ALREADY_ADVANCED = $null
            if ($owed) {
                $onDisk = Get-AeroLinkProductionSourcePosture -SourceRoot $config.SourceRoot -RemoteName $config.RemoteName
                if (-not $onDisk.Canonical) { throw "The production runtime was stopped for this update, and the revision now on disk is not canonical: $($onDisk.Reason) AeroLink was NOT restarted." }
                & (Join-Path $config.SourceRoot 'product\scripts\Start-AeroLinkProduction.ps1') -DoNotOpenBrowser
            }
            exit ($(if ($alreadyAdvanced) { 0 } else { 1 }))
        }
        if (-not $transitionLease.Owner) {
            throw 'This Update was started by another transition that already holds the HOME transition lease. Run Update from the dedicated production source or from an up-to-date checkout; nothing was changed.'
        }

        $delegation = Assert-AeroLinkRunningFromProductionSource -RepositoryRoot $repositoryRoot
        $config = Get-AeroLinkProductionSourceConfig
        $delegateSourceRoot = if ($delegation.DelegateTo) { [string]$delegation.DelegateTo } else { $repositoryRoot }
        if (-not (Test-Path -LiteralPath (Join-Path $delegateSourceRoot 'product\scripts\Invoke-AeroLinkTransitionActor.ps1') -PathType Leaf)) {
            throw "The dedicated production source at $delegateSourceRoot predates contained HOME transitions, so it cannot run this Update. Its scheduled reconciliation advances it; nothing was changed."
        }
        if ($delegation.DelegateTo) {
            Write-Host 'This checkout is not the dedicated production source.' -ForegroundColor Yellow
            Write-Host "      The update runs from: $delegateSourceRoot" -ForegroundColor Cyan
        }

        # Absent is not unreadable. A missing configuration means this machine has no remote demo; one that exists and
        # will not parse may still have a live tunnel behind it.
        $demoConfig = $null
        $demoConfigPath = Get-AeroLinkRemoteDemoConfigPath
        if (Test-Path -LiteralPath $demoConfigPath -PathType Leaf) {
            try { $demoConfig = Get-AeroLinkRemoteDemoConfig -ConfigPath $demoConfigPath }
            catch { throw "This machine has a remote-demo configuration at $demoConfigPath that could not be read ($($_.Exception.Message)). A tunnel started while it was valid may still be publishing port 5080, so the production source was NOT advanced and nothing was stopped." }
        }

        # Decide with a fetch (remote-tracking refs only). Nothing to do is not a transition.
        Assert-AeroLinkDedicatedProductionSource -SourceRoot $config.SourceRoot | Out-Null
        $inspect = Update-AeroLinkProductionSource -SourceRoot $config.SourceRoot -RemoteName $config.RemoteName -FetchTimeoutSeconds $config.FetchTimeoutSeconds -InspectOnly
        if (-not $inspect.Canonical -or $inspect.Action -ne 'UpdateAvailable') {
            $inspect | Format-List
            exit ($(if ($inspect.Canonical) { 0 } else { 1 }))
        }

        # -Preserve: an operator update restores exactly what was running, and creates nothing that was not.
        $operation = if ($demoConfig) { 'Update' } else { 'RuntimeUpdate' }
        $transition = Invoke-AeroLinkHomeTransitionOuter -InstallationRoot $InstallationRoot -Lease $transitionLease -Operation $operation `
            -SourceRoot $config.SourceRoot -DelegateSourceRoot $delegateSourceRoot -Config $demoConfig -Policy Preserve -StreamToHost `
            -AttemptDeadlineSeconds (Get-AeroLinkTransitionBudget).ContinuationSeconds
        $transition | Format-List Decision, ExitCode, Restored, RestorationRequired, Detail
        foreach ($attempt in $transition.Attempts) { Write-Host "      attempt $($attempt.AttemptId): $($attempt.Decision) (exit $($attempt.ExitCode))" -ForegroundColor DarkGray }
        if ($transition.Decision -eq 'Completed') { Write-Host 'The production source update is complete; what was running before is running on it.' -ForegroundColor Green }
        exit $transition.ExitCode
    }
}

} finally { if ($transitionLease) { Exit-AeroLinkTransition -Lease $transitionLease } }
