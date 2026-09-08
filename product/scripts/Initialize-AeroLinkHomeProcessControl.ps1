#Requires -Version 5.1
[CmdletBinding()]
param([int]$ControllerWaitSeconds = 900)

$ErrorActionPreference = 'Stop'
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'First deployment requires this one setup invocation in elevated Windows PowerShell. Ordinary subsequent launches do not require elevation.'
}
Import-Module (Join-Path $PSScriptRoot 'AeroLinkProductionSource.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'AeroLinkRuntimeIdentity.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'AeroLinkRemoteDemo.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'AeroLinkProcessControl.psm1')
Import-Module (Join-Path $PSScriptRoot 'AeroLinkBootstrap.psm1')
Import-Module (Join-Path $PSScriptRoot 'AeroLinkInstallation.psm1')
Import-Module (Join-Path $PSScriptRoot 'AeroLinkTransition.psm1')

# A temporary clean detached worktree at fetched main may run setup. It never runs an API or points an
# alternate installation at HOME data. This lets deployment reach a legacy launcher that refuses to update.
$setupRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$configuration = Get-AeroLinkProductionSourceConfig
$binding = Get-AeroLinkProductionSourcePosture -SourceRoot $configuration.SourceRoot
if (-not $binding.Dedicated -or -not $binding.Canonical) { throw "Dedicated source binding is invalid: $($binding.Reason)" }
if (-not (Sync-AeroLinkRemoteRefs -RepositoryRoot $setupRoot)) { throw 'Setup could not refresh approved main; no process permissions changed.' }
$posture = Get-AeroLinkRepositoryPosture -RepositoryRoot $setupRoot
$origin = Invoke-AeroLinkBootstrapGitQuiet -RepositoryRoot $setupRoot -GitArguments @('remote', 'get-url', 'origin')
$productionOrigin = Invoke-AeroLinkBootstrapGitQuiet -RepositoryRoot $configuration.SourceRoot -GitArguments @('remote', 'get-url', 'origin')
if (-not (Test-AeroLinkSameRemote -Left $origin -Right $productionOrigin) -or
    $posture.HasTrackedChanges -or $posture.UntrackedFileCount -gt 0 -or $posture.HeadSha -ne $posture.RemoteMainSha) {
    throw 'Setup must run from clean fetched main of the same canonical repository. No process permissions changed.'
}
$installation = Get-AeroLinkInstallationPaths -ProductRoot (Join-Path $configuration.SourceRoot 'product')
$instance = Get-AeroLinkInstanceConfig -ProductRoot (Join-Path $configuration.SourceRoot 'product') -Mode HomeCanonical
if ($instance.Classification -ne 'HomeCanonical' -or -not $instance.InstanceId) { throw 'The canonical HOME instance binding is incomplete.' }
$demoConfig = $null
if (Test-Path -LiteralPath (Get-AeroLinkRemoteDemoConfigPath)) { $demoConfig = Get-AeroLinkRemoteDemoConfig }

$disabledTasks = @()
$lease = $null
try {
    foreach ($name in @('AeroLinkRemoteDemoRecovery', 'AeroLinkProductionSourceReconcile')) {
        $task = Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
        if (-not $task) { continue }
        $actionPath = Join-Path $configuration.SourceRoot 'product\scripts\AeroLinkRemoteDemo.ps1'
        $operatorName = [Security.Principal.WindowsIdentity]::GetCurrent().Name
        $taskAccount = New-Object Security.Principal.NTAccount($task.Principal.UserId)
        if ($taskAccount.Translate([Security.Principal.SecurityIdentifier]).Value -ne [Security.Principal.WindowsIdentity]::GetCurrent().User.Value -or
            $task.Principal.LogonType -ne 'S4U' -or $task.Actions.Count -ne 1 -or
            ([string]$task.Actions[0].Arguments).IndexOf('"' + $actionPath + '"', [StringComparison]::OrdinalIgnoreCase) -lt 0) {
            throw "Task $name does not match the existing supported HOME binding. Setup did not alter that task."
        }
        if ($task.Settings.Enabled) {
            Disable-ScheduledTask -TaskName $name | Out-Null
            $disabledTasks += $name
        }
    }
    $deadline = (Get-Date).AddSeconds($ControllerWaitSeconds)
    do {
        $running = @($disabledTasks | ForEach-Object { Get-ScheduledTask -TaskName $_ } | Where-Object State -eq 'Running')
        if (-not $running.Count) { break }
        if ((Get-Date) -ge $deadline) { throw 'A prior-version recovery controller is still running. Setup changed no process permissions; retry when it finishes.' }
        Write-Host 'Waiting for the prior-version recovery controller to finish before adoption...'
        Start-Sleep -Seconds 5
    } while ($true)

    # The shared lease directory is the sole filesystem permission change. No evidence, backup, attachment
    # or database ACL is changed. Explicit account access works across interactive and S4U logon SIDs.
    $leaseDirectory = Join-Path $installation.InstallationRoot 'bootstrap'
    New-Item -ItemType Directory -Path $leaseDirectory -Force | Out-Null
    $acl = Get-Acl -LiteralPath $leaseDirectory
    $rule = New-Object Security.AccessControl.FileSystemAccessRule(
        [Security.Principal.WindowsIdentity]::GetCurrent().User, 'Modify', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
    $acl.AddAccessRule($rule)
    Set-Acl -LiteralPath $leaseDirectory -AclObject $acl
    $lease = Enter-AeroLinkTransition -InstallationRoot $installation.InstallationRoot

    $api = Get-AeroLinkPortOwner -Port 5080
    $apiDirectory = Join-Path $configuration.SourceRoot 'product\src\AeroLink.Api'
    if ($api.Found) {
        if ($api.Ambiguous -or -not $api.Attributable -or
            -not (Test-AeroLinkProcessOwnership -CommandLine $api.CommandLine -ExecutablePath $api.ExecutablePath -OwnershipFragments @($apiDirectory))) {
            throw 'The legacy API listener is not owned by the dedicated source. It was not adopted.'
        }
        $identity = Get-AeroLinkRuntimeIdentity -BaseUri 'http://127.0.0.1:5080'
        if (-not $identity -or $identity.mode -ne 'HOME-PRODUCTION' -or
            $identity.instance.id -ne $instance.InstanceId -or $identity.instance.classification -ne $instance.Classification) {
            throw 'Legacy runtime mode/instance proof is incomplete. It was not adopted.'
        }
    }
    $ngrok = if ($demoConfig) { Get-AeroLinkRemoteDemoNgrokProcess -Config $demoConfig } else { $null }
    if ($ngrok -and @($ngrok.Mismatched).Count) { throw 'A legacy ngrok process contradicts the protected contract. It was not adopted.' }
    # Finish all read-only ownership checks before granting access to either service.
    if ($api.Found) {
        Grant-AeroLinkCreatedProcessAccess -ProcessId $api.ProcessId -StartedAt $api.StartedAt `
            -ExpectedExecutable $api.ExecutablePath -ExpectedArguments @('--urls', 'http://127.0.0.1:5080')
    }
    if ($ngrok) {
        foreach ($process in $ngrok.Owned) {
            Grant-AeroLinkCreatedProcessAccess -ProcessId $process.ProcessId -StartedAt $process.StartedAt `
                -ExpectedExecutable $demoConfig.NgrokExecutable -ExpectedArguments (Get-AeroLinkRemoteDemoNgrokArguments -Config $demoConfig)
        }
    }
    # Complete first deployment while old scheduled controllers are still paused. Leaving this to a later
    # click would allow an old recovery task to create inaccessible children again before the fixed source
    # arrived. The old launcher's existing strict update/re-entry path reaches the newly merged controller.
    $productionLauncher = Join-Path $configuration.SourceRoot 'product\scripts\Start-AeroLinkProduction.ps1'
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $productionLauncher -DoNotOpenBrowser
    if ($LASTEXITCODE -ne 0) { throw "The first-deployment production transition failed (exit $LASTEXITCODE). The existing task enabled states will be restored." }
    Write-Host "HOME first-deployment setup completed from approved source $($posture.HeadSha)."
    Write-Host 'Subsequent START_AEROLINK_PRODUCTION.bat launches and updates run from ordinary Explorer or PowerShell.'
}
finally {
    Exit-AeroLinkTransition -Lease $lease
    foreach ($name in $disabledTasks) { Enable-ScheduledTask -TaskName $name | Out-Null }
}
