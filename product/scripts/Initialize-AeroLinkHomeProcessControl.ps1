#Requires -Version 5.1
[CmdletBinding()]
param([ValidateRange(1,1800)][int]$ControllerWaitSeconds = 900)

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
$deploymentTask = $null
try {
    foreach ($name in @('AeroLinkRemoteDemoRecovery', 'AeroLinkProductionSourceReconcile')) {
        $task = Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
        if (-not $task) { continue }
        $actionPath = Join-Path $configuration.SourceRoot 'product\scripts\AeroLinkRemoteDemo.ps1'
        $operatorName = [Security.Principal.WindowsIdentity]::GetCurrent().Name
        $taskAccount = New-Object Security.Principal.NTAccount($task.Principal.UserId)
        if ($taskAccount.Translate([Security.Principal.SecurityIdentifier]).Value -ne [Security.Principal.WindowsIdentity]::GetCurrent().User.Value -or
            $task.Principal.LogonType -ne 'S4U' -or $task.Principal.RunLevel -ne 'Limited' -or $task.Actions.Count -ne 1 -or
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

    $api = Get-AeroLinkPortOwner -Port (Get-AeroLinkServiceEndpoints).ApiPort
    $apiDirectory = Join-Path $configuration.SourceRoot 'product\src\AeroLink.Api'
    if ($api.Found) {
        if ($api.Ambiguous -or -not $api.Attributable -or
            -not (Test-AeroLinkProcessOwnership -CommandLine $api.CommandLine -ExecutablePath $api.ExecutablePath -OwnershipFragments @($apiDirectory))) {
            throw 'The legacy API listener is not owned by the dedicated source. It was not adopted.'
        }
        $identity = Get-AeroLinkRuntimeIdentity -BaseUri (Get-AeroLinkServiceEndpoints).ApiBaseUri
        if (-not $identity -or $identity.mode -ne 'HOME-PRODUCTION' -or
            $identity.instance.id -ne $instance.InstanceId -or $identity.instance.classification -ne $instance.Classification) {
            throw 'Legacy runtime mode/instance proof is incomplete. It was not adopted.'
        }
        if ([string]$identity.sourceIdentity -notmatch '^[0-9a-fA-F]{40}$') { throw 'Legacy source identity is not a clean approved revision.' }
        & git -C $setupRoot merge-base --is-ancestor $identity.sourceIdentity $posture.RemoteMainSha
        if ($LASTEXITCODE -ne 0) { throw 'Legacy runtime source is not in approved main history. It was not adopted.' }
    }
    $ngrok = if ($demoConfig) { Get-AeroLinkRemoteDemoNgrokProcess -Config $demoConfig } else { $null }
    if ($ngrok -and @($ngrok.Mismatched).Count) { throw 'A legacy ngrok process contradicts the protected contract. It was not adopted.' }
    if ($ngrok -and @($ngrok.Owned).Count -gt 1) { throw 'Multiple legacy ngrok processes are ambiguous. They were not adopted.' }
    # Finish all read-only ownership checks before granting access to either service.
    if ($api.Found) {
        Grant-AeroLinkCreatedProcessAccess -ProcessId $api.ProcessId -StartedAt $api.StartedAt `
            -ExpectedExecutable $api.ExecutablePath -ExpectedArguments @('--urls', (Get-AeroLinkServiceEndpoints).ApiBaseUri)
    }
    if ($ngrok) {
        foreach ($process in $ngrok.Owned) {
            Grant-AeroLinkCreatedProcessAccess -ProcessId $process.ProcessId -StartedAt $process.StartedAt `
                -ExpectedExecutable $demoConfig.NgrokExecutable -ExpectedArguments (Get-AeroLinkRemoteDemoNgrokArguments -Config $demoConfig)
        }
    }
    # This setup process performs NO teardown and NO advance (#1041, #1043, #1053). The first deployment runs in the
    # supported scheduled context as the OUTER authority of a contained HOME transition, which qualifies that context
    # about itself before it touches anything: an unqualified context refuses there, with every service untouched.
    $inspect = Update-AeroLinkProductionSource -SourceRoot $configuration.SourceRoot -InspectOnly
    if (-not $inspect.Canonical) { throw "First-deployment source inspection refused: $($inspect.Reason)" }
    if ($inspect.Action -eq 'UpdateAvailable' -and $inspect.TargetSha -ne $posture.RemoteMainSha) { throw 'Approved main moved during setup. Nothing was stopped; obtain setup from the new approved revision.' }

    # ONE staged request, bound to an id the result must repeat. A result without this id, or a stale task result from
    # an earlier invocation, can never establish success.
    $deploymentDirectory = Join-Path $leaseDirectory 'first-deployment'
    New-Item -ItemType Directory -Path $deploymentDirectory -Force | Out-Null
    $deploymentId = [guid]::NewGuid().ToString('N')
    $deploymentResult = Join-Path $deploymentDirectory "$deploymentId.result.json"
    $request = [ordered]@{ requestId = $deploymentId; setupPid = $PID; approvedSha = $posture.RemoteMainSha; sourceRoot = $configuration.SourceRoot
        configurationProfile = $env:LOCALAPPDATA; at = (Get-Date).ToUniversalTime().ToString('o') }
    $requestTemporary = Join-Path $deploymentDirectory ('request.' + $deploymentId + '.tmp')
    [IO.File]::WriteAllText($requestTemporary, ($request | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))
    Move-Item -LiteralPath $requestTemporary -Destination (Join-Path $deploymentDirectory 'request.json') -Force

    # A stable action: the image, principal and settings are what a launch-context qualification binds, so this
    # definition is qualified once rather than per checkout path.
    $deploymentScript = Join-Path $setupRoot 'product\scripts\Invoke-AeroLinkFirstDeployment.ps1'
    $deploymentTask = "AeroLinkHomeFirstDeployment_$deploymentId"
    $arguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "{0}" -InstallationRoot "{1}"' -f $deploymentScript, $installation.InstallationRoot
    $action = New-ScheduledTaskAction -Execute (Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe') -Argument $arguments
    $taskPrincipal = New-ScheduledTaskPrincipal -UserId ([Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType S4U -RunLevel Limited
    $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 135) -MultipleInstances IgnoreNew
    Register-ScheduledTask -TaskName $deploymentTask -Action $action -Principal $taskPrincipal -Settings $settings | Out-Null
    Exit-AeroLinkTransition -Lease $lease
    $lease = $null
    $startedAt = Get-Date
    Start-ScheduledTask -TaskName $deploymentTask

    # Wait for THIS invocation to end, then reconcile its outcome AND its task result. A Completed file alone is not
    # success, and neither is a zero LastTaskResult from an earlier run.
    $deadline = (Get-Date).AddMinutes(140)
    $ended = $false
    $info = $null
    while ((Get-Date) -lt $deadline) {
        $task = Get-ScheduledTask -TaskName $deploymentTask -ErrorAction Stop
        $info = $task | Get-ScheduledTaskInfo
        if ($task.State -ne 'Running' -and $info.LastRunTime -and $info.LastRunTime -ge $startedAt.AddSeconds(-5)) { $ended = $true; break }
        Start-Sleep -Seconds 5
    }
    $verdict = Resolve-AeroLinkFirstDeploymentResult -ResultPath $deploymentResult -RequestId $deploymentId -TaskEnded $ended -LastTaskResult $(if ($info) { $info.LastTaskResult } else { $null })
    if ($verdict.Unknown) { throw "The first-deployment result is UNKNOWN: $($verdict.Detail). The task was not unregistered while running and deployment will not be repeated; rerunning setup is admitted only after that attempt is proven quiescent. Evidence: $deploymentDirectory" }
    if (-not $verdict.Succeeded) { throw "The first-deployment transition did not complete: $($verdict.Detail) Existing task enabled states will be restored. Evidence: $deploymentDirectory" }
    Write-Host "HOME first-deployment setup completed from approved source $($posture.HeadSha)."
    Write-Host $verdict.Detail
    Write-Host 'Subsequent START_AEROLINK_PRODUCTION.bat launches and updates run from ordinary Explorer or PowerShell.'
}
finally {
    Exit-AeroLinkTransition -Lease $lease
    if ($deploymentTask) {
        $task = Get-ScheduledTask -TaskName $deploymentTask -ErrorAction SilentlyContinue
        if ($task -and $task.State -ne 'Running') { Unregister-ScheduledTask -TaskName $deploymentTask -Confirm:$false }
    }
    foreach ($name in $disabledTasks) { Enable-ScheduledTask -TaskName $name | Out-Null }
}
