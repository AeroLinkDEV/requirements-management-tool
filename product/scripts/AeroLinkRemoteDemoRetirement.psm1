#Requires -Version 5.1
<#
    Retiring the HOME ngrok remote demo (#1614 D6).

    Sean, 15 Oct: once the company host is live, the remote demo is retired entirely: its scheduled tasks and its
    configuration are removed, and it does not run on the host. This is a one-time, explicit operator action on HOME
    (AeroLinkRemoteDemo.ps1 -Action Retire, Preview by default, -Apply to act) - never part of an install, an update
    or a launch, because until the host is live HOME's demo is still in use.

    What Retire removes:
      - the scheduled tasks AeroLinkRemoteDemoRecovery (boot recovery) and AeroLinkProductionSourceReconcile (the
        30-minute reconciler), both of which run the remote-demo script; through the Task Scheduler API;
      - the per-user remote-demo configuration and its ngrok traffic policy, when that policy is in the same folder.
    What it leaves: the logs and state (evidence of what ran), the Basic-Auth secret in the SecretManagement vault
    (named in the output, for the operator to remove), ngrok itself, and every HOME production-transition function
    other scripts import.

    After Retire the remote-demo entry points - START_, STOP_, CONFIGURE_ and AEROLINK_REMOTE_DEMO_STATUS.bat, and
    REDEPLOY_AEROLINK_PRODUCTION.bat, which asks the reconciler - stay where they are (AGENTS.md: no launcher moves)
    and print that the demo was retired, for one release, from a marker file Retire writes. The .bat files
    themselves are unchanged; the gate is in AeroLinkRemoteDemo.ps1, which every one of them runs.
#>

Set-StrictMode -Version Latest

$script:RetiredTaskNames = @('AeroLinkRemoteDemoRecovery', 'AeroLinkProductionSourceReconcile')
$script:RetiredLaunchers = @('START_AEROLINK_REMOTE_DEMO.bat', 'STOP_AEROLINK_REMOTE_DEMO.bat', 'CONFIGURE_AEROLINK_REMOTE_DEMO.bat',
    'AEROLINK_REMOTE_DEMO_STATUS.bat', 'REDEPLOY_AEROLINK_PRODUCTION.bat')

function Get-AeroLinkRemoteDemoRetirementMarkerPath {
    param([string]$LocalRoot = (Join-Path $env:LOCALAPPDATA 'AeroLink\RemoteDemo'))
    return Join-Path $LocalRoot 'retired.json'
}

function Get-AeroLinkRemoteDemoRetirement {
    <# .SYNOPSIS The retirement record, or $null while the remote demo is in service. #>
    param([string]$LocalRoot = (Join-Path $env:LOCALAPPDATA 'AeroLink\RemoteDemo'))
    $marker = Get-AeroLinkRemoteDemoRetirementMarkerPath -LocalRoot $LocalRoot
    if (-not (Test-Path -LiteralPath $marker -PathType Leaf)) { return $null }
    try { return (Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json) }
    catch { return [pscustomobject]@{ retiredAtUtc = 'unknown'; reason = '#1614 D6' } }
}

function Get-AeroLinkRemoteDemoGoLiveProof {
    <#
        .SYNOPSIS Proves the attended D6 prerequisite without changing the HOME demo.
        .DESCRIPTION
            The public HTTPS origin is checked at action time through the existing readiness and runtime-identity
            endpoints. The identity must be the SiteProduction installation. The operator confirmation is a separate
            attended input; an HTTP response alone never authorizes destructive HOME cleanup.
    #>
    [CmdletBinding()]
    param(
        [AllowNull()][string]$PublicHttpsOrigin,
        [switch]$OperatorConfirmed
    )
    if (-not $OperatorConfirmed) {
        return [pscustomobject]@{ Ready = $false; Refusal = 'D6 retirement requires explicit attended confirmation that the company host is live.' }
    }
    if ([string]::IsNullOrWhiteSpace($PublicHttpsOrigin)) {
        return [pscustomobject]@{ Ready = $false; Refusal = 'D6 retirement requires the company host public HTTPS origin.' }
    }
    $origin = $null
    if (-not [Uri]::TryCreate($PublicHttpsOrigin.Trim(), [UriKind]::Absolute, [ref]$origin) -or
        $origin.Scheme -ne 'https' -or [string]::IsNullOrWhiteSpace($origin.Host) -or $origin.AbsolutePath -ne '/' -or
        -not [string]::IsNullOrEmpty($origin.UserInfo) -or -not [string]::IsNullOrEmpty($origin.Query) -or
        -not [string]::IsNullOrEmpty($origin.Fragment)) {
        return [pscustomobject]@{ Ready = $false; Refusal = 'D6 retirement requires an absolute public HTTPS origin with no credentials, path, query or fragment.' }
    }
    $base = $origin.GetLeftPart([UriPartial]::Authority).TrimEnd('/')
    try {
        $ready = Invoke-RestMethod -Uri ($base + '/health/ready') -UseBasicParsing -TimeoutSec 10
        if ([string]$ready.status -ne 'ready' -or [string]$ready.database -ne 'connected') {
            return [pscustomobject]@{ Ready = $false; Refusal = "The company host readiness endpoint did not report ready/connected (status=$($ready.status), database=$($ready.database))." }
        }
        $identity = Invoke-RestMethod -Uri ($base + '/health/identity') -UseBasicParsing -TimeoutSec 10
        $classification = [string]$identity.instance.classification
        $mode = [string]$identity.mode
        $instanceId = [string]$identity.instance.id
        if ($classification -ne 'SiteProduction' -or $mode -ne 'SITE-PRODUCTION' -or [string]::IsNullOrWhiteSpace($instanceId) -or $instanceId -eq 'unknown') {
            return [pscustomobject]@{ Ready = $false; Refusal = "The company host identity is not SiteProduction (mode=$mode, classification=$classification, instanceId=$instanceId)." }
        }
        return [pscustomobject]@{
            Ready               = $true
            Refusal             = $null
            OperatorConfirmed   = $true
            PublicHttpsOrigin   = $base
            ObservedAtUtc       = (Get-Date).ToUniversalTime().ToString('o')
            ReadyStatus         = [string]$ready.status
            ReadyDatabase       = [string]$ready.database
            Identity            = [ordered]@{
                Mode           = $mode
                Classification = $classification
                InstanceId     = $instanceId
                InstanceLabel  = [string]$identity.instance.label
            }
        }
    }
    catch {
        return [pscustomobject]@{ Ready = $false; Refusal = "The company host public HTTPS proof failed: $($_.Exception.Message)" }
    }
}

function Get-AeroLinkRemoteDemoTargetAudit {
    <#
        .SYNOPSIS Confirms a retirement target is inside the declared RemoteDemo root.
        .DESCRIPTION
            Both the lexical path and the nearest existing filesystem object are checked. This catches a path that
            escapes through a junction/symlink as well as a plain absolute path outside the retirement scope.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$LocalRoot
    )
    try {
        if (-not (Test-Path -LiteralPath $LocalRoot -PathType Container)) {
            return [pscustomobject]@{ Allowed = $false; Path = $Path; Reason = "The declared RemoteDemo root does not exist: $LocalRoot" }
        }
        $scope = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $LocalRoot).Path).TrimEnd('\')
        $candidate = [IO.Path]::GetFullPath($Path)
        $prefix = $scope + '\'
        if (-not $candidate.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
            return [pscustomobject]@{ Allowed = $false; Path = $candidate; Reason = "Retirement target '$candidate' is outside the declared RemoteDemo root '$scope'." }
        }

        $probe = $candidate
        while (-not (Test-Path -LiteralPath $probe)) {
            $parent = Split-Path -Path $probe -Parent
            if ([string]::IsNullOrWhiteSpace($parent) -or $parent -eq $probe) { break }
            $probe = $parent
        }
        if (-not (Test-Path -LiteralPath $probe)) {
            return [pscustomobject]@{ Allowed = $false; Path = $candidate; Reason = "Retirement target '$candidate' has no existing parent to audit." }
        }
        $resolvedProbe = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $probe).Path)
        $suffix = if ($candidate.Length -gt $probe.Length) { $candidate.Substring($probe.Length).TrimStart('\') } else { '' }
        $resolvedCandidate = if ($suffix) { [IO.Path]::GetFullPath((Join-Path $resolvedProbe $suffix)) } else { $resolvedProbe }
        $resolvedPrefix = $scope + '\'
        if (-not $resolvedCandidate.StartsWith($resolvedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            return [pscustomobject]@{ Allowed = $false; Path = $candidate; Reason = "Retirement target '$candidate' resolves outside the declared RemoteDemo root '$scope'." }
        }
        return [pscustomobject]@{ Allowed = $true; Path = $candidate; ResolvedPath = $resolvedCandidate; ScopeRoot = $scope }
    }
    catch {
        return [pscustomobject]@{ Allowed = $false; Path = $Path; Reason = "Retirement target '$Path' could not be audited: $($_.Exception.Message)" }
    }
}

function Get-AeroLinkRemoteDemoRetirePlan {
    <#
        .SYNOPSIS What Retire would do, from what exists. Pure: the caller supplies what it observed.
    #>
    [CmdletBinding()]
    param(
        [AllowEmptyCollection()][string[]]$RegisteredTaskNames = @(),
        [Parameter(Mandatory)][string]$ConfigPath,
        [AllowNull()][string]$TrafficPolicyPath,
        [AllowNull()][string]$BasicAuthSecretName,
        [AllowNull()][string]$VaultName,
        [bool]$NgrokRunning = $false,
        [Parameter(Mandatory)][string]$LocalRoot,
        [AllowNull()]$GoLiveProof
    )
    $scopeAudit = Get-AeroLinkRemoteDemoTargetAudit -Path (Join-Path $LocalRoot '.d6-scope-probe') -LocalRoot $LocalRoot
    $refusals = New-Object System.Collections.Generic.List[string]
    if (-not $scopeAudit.Allowed) { $refusals.Add($scopeAudit.Reason) }
    if ($NgrokRunning) { $refusals.Add('An ngrok process is running. Stop the remote demo first (STOP_AEROLINK_REMOTE_DEMO.bat), then retire it.') }
    if ($null -eq $GoLiveProof -or -not [bool]$GoLiveProof.Ready) {
        $refusals.Add($(if ($GoLiveProof -and $GoLiveProof.Refusal) { [string]$GoLiveProof.Refusal } else { 'D6 retirement requires a verified company-host go-live proof.' }))
    }
    $configAudit = $null
    $policyAudit = $null
    if ($scopeAudit.Allowed) {
        $configAudit = Get-AeroLinkRemoteDemoTargetAudit -Path $ConfigPath -LocalRoot $scopeAudit.ScopeRoot
        if (-not $configAudit.Allowed) { $refusals.Add($configAudit.Reason) }
        if ($TrafficPolicyPath) {
            $policyAudit = Get-AeroLinkRemoteDemoTargetAudit -Path $TrafficPolicyPath -LocalRoot $scopeAudit.ScopeRoot
            if (-not $policyAudit.Allowed) { $refusals.Add($policyAudit.Reason) }
        }
    }
    $scopeRoot = if ($scopeAudit.ScopeRoot) { $scopeAudit.ScopeRoot } else { [IO.Path]::GetFullPath($LocalRoot) }
    if ($refusals.Count -gt 0) {
        return [pscustomobject]@{
            Refused          = $true
            Refusal          = ($refusals -join ' ')
            Actions          = @()
            Launchers        = $script:RetiredLaunchers
            Operator         = @()
            ScopeRoot        = $scopeRoot
            ConfigPath       = if ($configAudit) { $configAudit.Path } else { [IO.Path]::GetFullPath($ConfigPath) }
            TrafficPolicyPath = if ($policyAudit) { $policyAudit.Path } else { if ($TrafficPolicyPath) { [IO.Path]::GetFullPath($TrafficPolicyPath) } else { $null } }
            MarkerPath       = Get-AeroLinkRemoteDemoRetirementMarkerPath -LocalRoot $scopeRoot
            GoLiveProof      = $GoLiveProof
        }
    }
    $actions = New-Object System.Collections.Generic.List[object]
    foreach ($task in $script:RetiredTaskNames) {
        if ($RegisteredTaskNames -contains $task) { $actions.Add([pscustomobject]@{ Kind = 'UnregisterTask'; Target = $task }) }
    }
    if (Test-Path -LiteralPath $ConfigPath -PathType Leaf) { $actions.Add([pscustomobject]@{ Kind = 'DeleteFile'; Target = [IO.Path]::GetFullPath($ConfigPath) }) }
    if ($TrafficPolicyPath -and (Test-Path -LiteralPath $TrafficPolicyPath -PathType Leaf)) { $actions.Add([pscustomobject]@{ Kind = 'DeleteFile'; Target = $policyAudit.Path }) }
    $markerPath = Get-AeroLinkRemoteDemoRetirementMarkerPath -LocalRoot $scopeRoot
    $markerAudit = Get-AeroLinkRemoteDemoTargetAudit -Path $markerPath -LocalRoot $scopeRoot
    if (-not $markerAudit.Allowed) { throw $markerAudit.Reason }
    $actions.Add([pscustomobject]@{ Kind = 'WriteMarker'; Target = $markerAudit.Path })
    $operator = New-Object System.Collections.Generic.List[string]
    if ($BasicAuthSecretName) { $operator.Add("Remove the Basic-Auth secret from the SecretManagement vault: Remove-Secret -Name '$BasicAuthSecretName'$(if ($VaultName) { " -Vault '$VaultName'" })") }
    $operator.Add('Delete any desktop shortcuts or other machines'' references to the remote-demo launchers; the launchers print that the demo is retired.')
    $operator.Add('ngrok itself and its own configuration (ngrok.yml, authtoken) are not AeroLink''s; uninstall them separately if no longer wanted.')
    return [pscustomobject]@{
        Refused           = $false
        Refusal           = $null
        Actions           = $actions.ToArray()
        Launchers         = $script:RetiredLaunchers
        Operator          = $operator.ToArray()
        ScopeRoot         = $scopeRoot
        ConfigPath        = $configAudit.Path
        TrafficPolicyPath = if ($policyAudit) { $policyAudit.Path } else { $null }
        MarkerPath        = $markerAudit.Path
        GoLiveProof       = $GoLiveProof
    }
}

function Invoke-AeroLinkRemoteDemoRetirePlan {
    <# .SYNOPSIS Applies a retire plan. Tasks are removed through the Task Scheduler API, never schtasks. #>
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Plan)
    if ($Plan.Refused) { throw $Plan.Refusal }
    if ($null -eq $Plan.GoLiveProof -or -not [bool]$Plan.GoLiveProof.Ready) { throw 'D6 retirement requires a verified company-host go-live proof before any destructive action.' }
    $allowedTasks = @($script:RetiredTaskNames)
    $allowedFiles = @([IO.Path]::GetFullPath([string]$Plan.ConfigPath))
    if ($Plan.TrafficPolicyPath) { $allowedFiles += [IO.Path]::GetFullPath([string]$Plan.TrafficPolicyPath) }
    $allowedMarker = [IO.Path]::GetFullPath([string]$Plan.MarkerPath)
    foreach ($action in $Plan.Actions) {
        switch ($action.Kind) {
            'UnregisterTask' {
                if ($action.Target -notin $allowedTasks) { throw "Retirement plan names an unapproved scheduled task '$($action.Target)'." }
                if ($PSCmdlet.ShouldProcess($action.Target, 'Unregister remote-demo task')) { Get-ScheduledTask -TaskName $action.Target -ErrorAction SilentlyContinue | Unregister-ScheduledTask -Confirm:$false }
            }
            'DeleteFile' {
                $target = [IO.Path]::GetFullPath([string]$action.Target)
                if ($target -notin $allowedFiles) { throw "Retirement plan names an unapproved file '$target'." }
                $audit = Get-AeroLinkRemoteDemoTargetAudit -Path $target -LocalRoot $Plan.ScopeRoot
                if (-not $audit.Allowed) { throw $audit.Reason }
                if ($PSCmdlet.ShouldProcess($target, 'Delete remote-demo configuration')) { if (Test-Path -LiteralPath $target -PathType Leaf) { Remove-Item -LiteralPath $target -Force } }
            }
            'WriteMarker' {
                $target = [IO.Path]::GetFullPath([string]$action.Target)
                if ($target -ne $allowedMarker) { throw "Retirement plan names an unapproved marker '$target'." }
                $audit = Get-AeroLinkRemoteDemoTargetAudit -Path $target -LocalRoot $Plan.ScopeRoot
                if (-not $audit.Allowed) { throw $audit.Reason }
                if ($PSCmdlet.ShouldProcess($target, 'Write remote-demo retirement evidence')) {
                    New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
                    [ordered]@{
                        schema       = 'aerolink-remote-demo-retirement/v1'
                        retiredAtUtc = (Get-Date).ToUniversalTime().ToString('o')
                        reason       = '#1614 D6: the company host is live; the ngrok remote demo is retired.'
                        goLiveProof  = $Plan.GoLiveProof
                        targets      = @($Plan.Actions | Where-Object Kind -ne 'WriteMarker' | ForEach-Object { [ordered]@{ kind = $_.Kind; target = $_.Target } })
                    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $target -Encoding UTF8
                }
            }
        }
    }
    foreach ($task in @($Plan.Actions | Where-Object Kind -eq 'UnregisterTask' | ForEach-Object Target)) {
        if (Get-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue) { throw "The scheduled task $task is still registered after Retire." }
    }
}

Export-ModuleMember -Function Get-AeroLinkRemoteDemoRetirementMarkerPath, Get-AeroLinkRemoteDemoRetirement, Get-AeroLinkRemoteDemoGoLiveProof, Get-AeroLinkRemoteDemoRetirePlan, Invoke-AeroLinkRemoteDemoRetirePlan
