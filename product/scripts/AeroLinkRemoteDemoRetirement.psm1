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

function Get-AeroLinkRemoteDemoProperty {
    param([AllowNull()]$Object, [Parameter(Mandatory)][string]$Name)
    if ($null -eq $Object -or $null -eq $Object.PSObject.Properties[$Name]) { return $null }
    return $Object.PSObject.Properties[$Name].Value
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
        [AllowNull()][string]$ExpectedProductionInstanceId,
        [AllowNull()][string]$ExpectedProductionSourceIdentity,
        [switch]$OperatorConfirmed
    )
    if (-not $OperatorConfirmed) {
        return [pscustomobject]@{ Ready = $false; Refusal = 'D6 retirement requires explicit attended confirmation that the company host is live.' }
    }
    if ([string]::IsNullOrWhiteSpace($PublicHttpsOrigin)) {
        return [pscustomobject]@{ Ready = $false; Refusal = 'D6 retirement requires the company host public HTTPS origin.' }
    }
    if ([string]::IsNullOrWhiteSpace($ExpectedProductionInstanceId) -or
        [string]::IsNullOrWhiteSpace($ExpectedProductionSourceIdentity)) {
        return [pscustomobject]@{ Ready = $false; Refusal = 'D6 retirement requires the configured expected SiteProduction instance and source identity.' }
    }
    $origin = $null
    if (-not [Uri]::TryCreate($PublicHttpsOrigin.Trim(), [UriKind]::Absolute, [ref]$origin) -or
        $origin.Scheme -ne 'https' -or [string]::IsNullOrWhiteSpace($origin.Host) -or $origin.HostNameType -ne [UriHostNameType]::Dns -or
        $origin.IsLoopback -or $origin.DnsSafeHost -ieq 'localhost' -or $origin.AbsolutePath -ne '/' -or
        -not [string]::IsNullOrEmpty($origin.UserInfo) -or -not [string]::IsNullOrEmpty($origin.Query) -or
        -not [string]::IsNullOrEmpty($origin.Fragment)) {
        return [pscustomobject]@{ Ready = $false; Refusal = 'D6 retirement requires the configured full-DNS HTTPS origin with no credentials, path, query, fragment, loopback address or localhost.' }
    }
    $base = $origin.GetLeftPart([UriPartial]::Authority).TrimEnd('/')
    try {
        # A redirect is not an identity proof: the exact configured origin must answer both endpoints itself.
        $ready = Invoke-RestMethod -Uri ($base + '/health/ready') -UseBasicParsing -TimeoutSec 10 -MaximumRedirection 0
        if ([string]$ready.status -ne 'ready' -or [string]$ready.database -ne 'connected') {
            return [pscustomobject]@{ Ready = $false; Refusal = "The company host readiness endpoint did not report ready/connected (status=$($ready.status), database=$($ready.database))." }
        }
        $identity = Invoke-RestMethod -Uri ($base + '/health/identity') -UseBasicParsing -TimeoutSec 10 -MaximumRedirection 0
        $identityInstance = Get-AeroLinkRemoteDemoProperty -Object $identity -Name 'instance'
        $classification = [string](Get-AeroLinkRemoteDemoProperty -Object $identityInstance -Name 'classification')
        $mode = [string](Get-AeroLinkRemoteDemoProperty -Object $identity -Name 'mode')
        $instanceId = [string](Get-AeroLinkRemoteDemoProperty -Object $identityInstance -Name 'id')
        $sourceCandidates = @(
            [string](Get-AeroLinkRemoteDemoProperty -Object $identity -Name 'sourceIdentity'),
            [string](Get-AeroLinkRemoteDemoProperty -Object $identity -Name 'sourceSha'),
            [string](Get-AeroLinkRemoteDemoProperty -Object (Get-AeroLinkRemoteDemoProperty -Object $identityInstance -Name 'snapshot') -Name 'sourceSha')
        ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and $_ -ne 'unknown' }
        $sourceCandidates = @($sourceCandidates)
        if ($classification -ne 'SiteProduction' -or $mode -ne 'SITE-PRODUCTION' -or
            [string]::IsNullOrWhiteSpace($instanceId) -or $instanceId -eq 'unknown' -or
            $instanceId -cne $ExpectedProductionInstanceId -or
            $sourceCandidates -notcontains $ExpectedProductionSourceIdentity) {
            $observedSource = if ($sourceCandidates.Count) { $sourceCandidates -join ',' } else { 'unknown' }
            return [pscustomobject]@{ Ready = $false; Refusal = "The company host identity/source did not match the configured SiteProduction expectation (mode=$mode, classification=$classification, instanceId=$instanceId, source=$observedSource)." }
        }
        return [pscustomobject]@{
            Ready               = $true
            Refusal             = $null
            OperatorConfirmed   = $true
            PublicHttpsOrigin   = $base
            ExpectedInstanceId  = $ExpectedProductionInstanceId
            ExpectedSourceIdentity = $ExpectedProductionSourceIdentity
            ObservedAtUtc       = (Get-Date).ToUniversalTime().ToString('o')
            ReadyStatus         = [string]$ready.status
            ReadyDatabase       = [string]$ready.database
            Identity            = [ordered]@{
                Mode           = $mode
                Classification = $classification
                InstanceId     = $instanceId
                InstanceLabel  = [string]$identity.instance.label
                SourceIdentity = $ExpectedProductionSourceIdentity
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

function Get-AeroLinkRemoteDemoCanonicalContext {
    <#
        The HOME RemoteDemo root is an installation-owned target, not plan input. Configuration is read only to
        discover the policy path and operator-facing secret names; all paths are audited against this canonical root.
    #>
    [CmdletBinding()]
    param()
    if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) { throw 'D6 retirement requires LOCALAPPDATA to identify the HOME RemoteDemo root.' }
    $root = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'AeroLink\RemoteDemo')).TrimEnd('\')
    $configPath = Join-Path $root 'remote-demo.config.psd1'
    $values = $null
    if (Test-Path -LiteralPath $configPath -PathType Leaf) {
        try { $values = Import-PowerShellDataFile -LiteralPath $configPath }
        catch { throw "The canonical remote-demo configuration could not be read at '$configPath': $($_.Exception.Message)" }
    }
    $productionSourceRoot = $null
    $productionConfigPath = Join-Path $env:LOCALAPPDATA 'AeroLink\Production\production-source.config.psd1'
    if (Test-Path -LiteralPath $productionConfigPath -PathType Leaf) {
        try { $productionValues = Import-PowerShellDataFile -LiteralPath $productionConfigPath }
        catch { throw "The canonical production-source configuration could not be read at '$productionConfigPath': $($_.Exception.Message)" }
        if (-not $productionValues.ContainsKey('SourceRoot') -or [string]::IsNullOrWhiteSpace([string]$productionValues['SourceRoot'])) {
            throw "The canonical production-source configuration at '$productionConfigPath' does not name SourceRoot."
        }
        $productionSourceRoot = [string]$productionValues['SourceRoot']
    }
    if ([string]::IsNullOrWhiteSpace($productionSourceRoot) -and $values -and $values.ContainsKey('AeroLinkRoot')) {
        $productionSourceRoot = [string]$values['AeroLinkRoot']
    }
    if ([string]::IsNullOrWhiteSpace($productionSourceRoot) -or -not [IO.Path]::IsPathRooted($productionSourceRoot)) {
        throw 'D6 retirement requires a canonical dedicated production source root to bind the scheduled task script path.'
    }
    $productionSourceRoot = [IO.Path]::GetFullPath($productionSourceRoot).TrimEnd('\')
    $productionScriptPath = [IO.Path]::GetFullPath((Join-Path $productionSourceRoot 'product\scripts\AeroLinkRemoteDemo.ps1'))
    if (-not (Test-Path -LiteralPath $productionScriptPath -PathType Leaf)) {
        throw "The canonical dedicated production script was not found at '$productionScriptPath'."
    }
    $policyPath = if ($values -and $values.ContainsKey('TrafficPolicyPath')) { [string]$values['TrafficPolicyPath'] } else { $null }
    return [pscustomobject]@{
        ScopeRoot = $root
        ConfigPath = $configPath
        ProductionSourceRoot = $productionSourceRoot
        ProductionScriptPath = $productionScriptPath
        TrafficPolicyPath = $policyPath
        BasicAuthSecretName = if ($values -and $values.ContainsKey('BasicAuthSecretName')) { [string]$values['BasicAuthSecretName'] } else { $null }
        VaultName = if ($values -and $values.ContainsKey('VaultName')) { [string]$values['VaultName'] } else { $null }
    }
}

function Get-AeroLinkRemoteDemoTaskScriptPath {
    param([AllowNull()][string]$Arguments)
    if ([string]::IsNullOrWhiteSpace($Arguments)) { return $null }
    $match = [regex]::Match($Arguments, '(?i)(?:^|\s)-File\s+(?:"(?<quoted>[^"]+)"|(?<bare>[^\s]+))')
    if (-not $match.Success) { return $null }
    $raw = if ($match.Groups['quoted'].Success) { $match.Groups['quoted'].Value } else { $match.Groups['bare'].Value }
    try { return [IO.Path]::GetFullPath($raw) } catch { return $null }
}

function Get-AeroLinkRemoteDemoExpectedTaskDefinition {
    param([Parameter(Mandatory)][string]$TaskName)
    switch ($TaskName) {
        'AeroLinkRemoteDemoRecovery' {
            return [pscustomobject]@{ TaskName = $TaskName; TaskPath = '\'; Uri = '\AeroLinkRemoteDemoRecovery'; Action = 'Start' }
        }
        'AeroLinkProductionSourceReconcile' {
            return [pscustomobject]@{ TaskName = $TaskName; TaskPath = '\'; Uri = '\AeroLinkProductionSourceReconcile'; Action = 'Reconcile' }
        }
        default { throw "The task '$TaskName' is not an AeroLink remote-demo retirement task." }
    }
}

function Get-AeroLinkRemoteDemoTaskInventory {
    <# Read and validate the exact owned task object before it can enter the closed retirement action set. #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$ExpectedScriptPath)
    $expectedScript = [IO.Path]::GetFullPath($ExpectedScriptPath)
    $inventory = New-Object System.Collections.Generic.List[object]
    foreach ($name in $script:RetiredTaskNames) {
        $expected = Get-AeroLinkRemoteDemoExpectedTaskDefinition -TaskName $name
        $matches = @(Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue)
        if ($matches.Count -gt 1) { throw "Retirement refused: more than one scheduled task is named '$name'; no task was changed." }
        if ($matches.Count -eq 0) { continue }
        $task = $matches[0]
        if ([string]$task.TaskPath -cne $expected.TaskPath) {
            throw "Retirement refused: scheduled task '$name' is at '$($task.TaskPath)', expected '$($expected.TaskPath)'; no task was changed."
        }
        $xmlText = Export-ScheduledTask -TaskName $name -TaskPath $expected.TaskPath -ErrorAction Stop
        try { $xml = [xml]$xmlText } catch { throw "Retirement refused: scheduled task '$name' has no readable definition; no task was changed." }
        $uri = [string]$xml.Task.RegistrationInfo.URI
        $exec = $xml.Task.Actions.Exec
        $arguments = [string]$exec.Arguments
        $commandMatches = [string]$exec.Command -match '(?i)(^|\\)powershell\.exe$'
        $taskScriptPath = Get-AeroLinkRemoteDemoTaskScriptPath -Arguments $arguments
        $scriptMatches = $null -ne $taskScriptPath -and $taskScriptPath -ieq $expectedScript
        $actionMatches = $arguments -match [regex]::Escape("-Action $($expected.Action)")
        $scheduleMatches = $arguments -match '(?i)(^|\s)-Scheduled(\s|$)'
        if ($uri -cne $expected.Uri -or -not $commandMatches -or -not $scriptMatches -or -not $actionMatches -or -not $scheduleMatches) {
            throw "Retirement refused: scheduled task '$name' does not match its expected owned definition; no task was changed."
        }
        $inventory.Add([pscustomobject]@{ TaskName = $name; TaskPath = $expected.TaskPath; Uri = $expected.Uri; Action = $expected.Action })
    }
    return $inventory.ToArray()
}

function Assert-AeroLinkRemoteDemoNgrokStopped {
    $running = @(Get-Process -Name 'ngrok' -ErrorAction SilentlyContinue)
    if ($running.Count -gt 0) { throw 'An ngrok process is running. Stop the remote demo first (STOP_AEROLINK_REMOTE_DEMO.bat), then retire it.' }
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
    <#
        .SYNOPSIS Applies a retire plan. Tasks are removed through the Task Scheduler API, never schtasks.
        .DESCRIPTION
            The supplied plan is preview evidence only. Apply recomputes the canonical HOME target, exact task
            inventory and closed action set, then obtains a fresh proof from the configured company origin. This
            keeps a stale or hand-built PSObject from becoming a destructive instruction.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Plan,
        [Parameter(Mandatory)][string]$ExpectedPublicHttpsOrigin,
        [Parameter(Mandatory)][string]$ExpectedProductionInstanceId,
        [Parameter(Mandatory)][string]$ExpectedProductionSourceIdentity
    )
    if ($Plan.Refused) { throw $Plan.Refusal }
    $context = Get-AeroLinkRemoteDemoCanonicalContext
    $proof = Get-AeroLinkRemoteDemoGoLiveProof -PublicHttpsOrigin $ExpectedPublicHttpsOrigin `
        -ExpectedProductionInstanceId $ExpectedProductionInstanceId -ExpectedProductionSourceIdentity $ExpectedProductionSourceIdentity -OperatorConfirmed
    if (-not $proof.Ready) { throw $proof.Refusal }
    Assert-AeroLinkRemoteDemoNgrokStopped
    $taskInventory = @(Get-AeroLinkRemoteDemoTaskInventory -ExpectedScriptPath $context.ProductionScriptPath)
    $canonicalPlan = Get-AeroLinkRemoteDemoRetirePlan -RegisteredTaskNames @($taskInventory | ForEach-Object TaskName) `
        -ConfigPath $context.ConfigPath -TrafficPolicyPath $context.TrafficPolicyPath -BasicAuthSecretName $context.BasicAuthSecretName `
        -VaultName $context.VaultName -LocalRoot $context.ScopeRoot -GoLiveProof $proof
    if ($canonicalPlan.Refused) { throw $canonicalPlan.Refusal }

    $allowedTasks = @($taskInventory | ForEach-Object TaskName)
    $allowedFiles = @($context.ConfigPath)
    if ($context.TrafficPolicyPath) { $allowedFiles += [IO.Path]::GetFullPath([string]$context.TrafficPolicyPath) }
    $allowedMarker = [IO.Path]::GetFullPath((Join-Path $context.ScopeRoot 'retired.json'))
    $closedActions = @($canonicalPlan.Actions)
    foreach ($action in $closedActions) {
        # Recheck the live boundary immediately before every destructive task/file mutation. A plan made while
        # ngrok was stopped or the host was healthy is not permission to proceed after either fact changes.
        if ($action.Kind -in @('UnregisterTask', 'DeleteFile', 'WriteMarker')) {
            $fresh = Get-AeroLinkRemoteDemoGoLiveProof -PublicHttpsOrigin $ExpectedPublicHttpsOrigin `
                -ExpectedProductionInstanceId $ExpectedProductionInstanceId -ExpectedProductionSourceIdentity $ExpectedProductionSourceIdentity -OperatorConfirmed
            if (-not $fresh.Ready) { throw $fresh.Refusal }
            Assert-AeroLinkRemoteDemoNgrokStopped
            $proof = $fresh
        }
        switch ($action.Kind) {
            'UnregisterTask' {
                $expected = Get-AeroLinkRemoteDemoExpectedTaskDefinition -TaskName ([string]$action.Target)
                if ($action.Target -notin $allowedTasks -or $expected.TaskPath -ne '\') { throw "Retirement plan names an unapproved scheduled task '$($action.Target)'." }
                $recheckedInventory = @(Get-AeroLinkRemoteDemoTaskInventory -ExpectedScriptPath $context.ProductionScriptPath)
                if (@($recheckedInventory | Where-Object TaskName -eq $expected.TaskName).Count -ne 1) { throw "The scheduled task '$($expected.TaskName)' changed before retirement; no task was changed for it." }
                $current = @(Get-ScheduledTask -TaskName $expected.TaskName -ErrorAction SilentlyContinue)
                if ($current.Count -ne 1 -or [string]$current[0].TaskPath -cne $expected.TaskPath) { throw "The scheduled task '$($expected.TaskName)' changed before retirement; no task was changed for it." }
                if ($PSCmdlet.ShouldProcess("$($expected.TaskPath)$($expected.TaskName)", 'Unregister remote-demo task')) {
                    Unregister-ScheduledTask -TaskName $expected.TaskName -TaskPath $expected.TaskPath -Confirm:$false
                }
            }
            'DeleteFile' {
                $target = [IO.Path]::GetFullPath([string]$action.Target)
                if ($target -notin $allowedFiles) { throw "Retirement plan names an unapproved file '$target'." }
                $audit = Get-AeroLinkRemoteDemoTargetAudit -Path $target -LocalRoot $context.ScopeRoot
                if (-not $audit.Allowed) { throw $audit.Reason }
                if ($PSCmdlet.ShouldProcess($target, 'Delete remote-demo configuration')) { if (Test-Path -LiteralPath $target -PathType Leaf) { Remove-Item -LiteralPath $target -Force } }
            }
            'WriteMarker' {
                $target = [IO.Path]::GetFullPath([string]$action.Target)
                if ($target -ne $allowedMarker) { throw "Retirement plan names an unapproved marker '$target'." }
                $audit = Get-AeroLinkRemoteDemoTargetAudit -Path $target -LocalRoot $context.ScopeRoot
                if (-not $audit.Allowed) { throw $audit.Reason }
                if ($PSCmdlet.ShouldProcess($target, 'Write remote-demo retirement evidence')) {
                    New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
                    [ordered]@{
                        schema       = 'aerolink-remote-demo-retirement/v1'
                        retiredAtUtc = (Get-Date).ToUniversalTime().ToString('o')
                        reason       = '#1614 D6: the company host is live; the ngrok remote demo is retired.'
                        goLiveProof  = $proof
                        targets      = @($closedActions | Where-Object Kind -ne 'WriteMarker' | ForEach-Object { [ordered]@{ kind = $_.Kind; target = $_.Target } })
                    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $target -Encoding UTF8
                }
            }
            default { throw "Retirement plan contains an unsupported action kind '$($action.Kind)'." }
        }
    }
    foreach ($task in @($closedActions | Where-Object Kind -eq 'UnregisterTask' | ForEach-Object Target)) {
        $expected = Get-AeroLinkRemoteDemoExpectedTaskDefinition -TaskName ([string]$task)
        if (Get-ScheduledTask -TaskName $expected.TaskName -TaskPath $expected.TaskPath -ErrorAction SilentlyContinue) { throw "The scheduled task $($expected.TaskName) is still registered after Retire." }
    }
}

Export-ModuleMember -Function Get-AeroLinkRemoteDemoRetirementMarkerPath, Get-AeroLinkRemoteDemoRetirement, Get-AeroLinkRemoteDemoGoLiveProof, Get-AeroLinkRemoteDemoRetirePlan, Invoke-AeroLinkRemoteDemoRetirePlan
