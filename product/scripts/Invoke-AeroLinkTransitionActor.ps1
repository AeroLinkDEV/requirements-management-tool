#Requires -Version 5.1
<#
    The actors of one HOME transition attempt (#1041, #1043, #1053). Never an operator entry point.

    The OUTER authority (the caller's own process: remote-demo Start/Reconcile, Configure Update, first deployment)
    owns the lease, the transition job, the witness and the launch spool. This script runs INSIDE that job and owns
    nothing durable:

      -Phase Delegate   created by the outer from the checkout whose source identity the handoff names. Accepts the
                        versioned handoff BEFORE it enters the lease or touches anything, enters the lease as a
                        descendant, then performs the caller's operation: teardown, advance, and - after an advance -
                        a continuation from the advanced source. Without an advance it restores in-process.
      -Phase Restore    the continuation, created by the delegate from the source on disk. Proves it runs the exact
                        source the delegate named, then restores the required topology. Every service that must
                        outlive the attempt is obtained by launch request to the outer's authority.

    Result contract: exit 0 <=> outcome Completed with no failures; exit 3 <=> Failed with its reasons; refusals exit
    30 (handoff/protocol/source), 32 (lease rejected) or 33 (not a descendant) with nothing mutated; any other exit
    code, or an absent or malformed outcome, is a failure of this actor itself. A failure AFTER the outcome was
    published (lease release) republishes it as Failed, so the file and the exit code never disagree.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$HandoffFile,
    [Parameter(Mandatory)][ValidateSet('Delegate', 'Restore')][string]$Phase
)
$ErrorActionPreference = 'Stop'
$attemptRoot = Split-Path -Parent $HandoffFile
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$role = if ($Phase -eq 'Delegate') { 'delegate' } else { 'continuation' }
$outcomePath = Join-Path $attemptRoot $(if ($Phase -eq 'Delegate') { 'delegate-outcome.json' } else { 'continuation-outcome.json' })
$published = $false
$lease = $null

function Publish-ActorOutcome {
    param([Parameter(Mandatory)][string]$Decision, [string[]]$Failures = @(), [Collections.IDictionary]$Extra = @{})
    $record = [ordered]@{ decision = $Decision; failures = [string[]]@($Failures | Where-Object { $_ }); pid = $PID; phase = $Phase; at = (Get-Date).ToUniversalTime().ToString('o') }
    foreach ($key in $Extra.Keys) { $record[$key] = $Extra[$key] }
    Publish-AeroLinkJsonAtomic -Path $outcomePath -Value $record
}

function Get-ActorConfig {
    param($Plan)
    if ($Plan.configPath) { return Get-AeroLinkRemoteDemoConfig -ConfigPath ([string]$Plan.configPath) }
    return $null
}

function Get-RuntimeOnlyConfig {
    # The few settings a runtime-only transition uses, for an installation with no remote-demo configuration.
    param([Parameter(Mandatory)][string]$SourceRoot)
    $installation = Get-AeroLinkInstallationPaths -ProductRoot (Join-Path $SourceRoot 'product')
    return [pscustomobject]@{ AeroLinkRoot = $SourceRoot; LogsPath = $installation.Logs; StatePath = (Join-Path $installation.BootstrapState 'transitions'); PublicUrl = $null }
}

function Get-ActorRoleResults {
    # What this actor can observe of the required roles now. The outer verifies independently; this is the actor's
    # own account, so a Completed outcome that the outer cannot verify is reported as contradicted.
    param($Handoff, $Plan, $Config)
    $topology = [pscustomobject]@{ TunnelRunning = [bool]$Plan.topology.tunnelRunning; RuntimeRunning = [bool]$Plan.topology.runtimeRunning }
    $roles = @(Get-AeroLinkHomeTransitionRequiredRoles -SourceRoot ([string]$Plan.sourceRoot) -InstallationRoot ([string]$Handoff.installationRoot) -Config $Config `
            -Policy ([string]$Plan.policy) -Topology $topology -RequireRuntime:([string]$Plan.operation -eq 'FirstDeployment'))
    $results = @()
    foreach ($required in $roles) {
        $readiness = $required.readiness
        # Module-bound, parameterized callbacks (see Get-AeroLinkHomeTransitionRequiredRoles): the requirement
        # object is the parameter, so the callback needs no captured locals and keeps the module's command scope.
        if ($readiness -is [scriptblock]) { $readiness = & $readiness $required }
        $found = & $required.discover $required
        $check = if ($found -and $found.ProcessId) { Test-AeroLinkRoleReadiness -Readiness $readiness -ProcessId ([int]$found.ProcessId) } else { [pscustomobject]@{ Ready = $false; Evidence = 'no running instance was found' } }
        $results += [ordered]@{ role = [string]$required.role; restored = [bool]$check.Ready; processId = $(if ($found) { [int]$found.ProcessId } else { 0 }); evidence = [string]$check.Evidence }
    }
    return $results
}

try {
    Import-Module (Join-Path $PSScriptRoot 'AeroLinkTransitionKernel.psm1') -DisableNameChecking
    Import-Module (Join-Path $PSScriptRoot 'AeroLinkTransitionAuthority.psm1') -DisableNameChecking
    Import-Module (Join-Path $PSScriptRoot 'AeroLinkRuntimeIdentity.psm1') -DisableNameChecking
    $actualIdentity = [string](Get-AeroLinkSourceFingerprint -RepositoryRoot $repositoryRoot).Identity

    # ---- The handoff is accepted BEFORE the lease is entered or anything is touched. ----
    if ($Phase -eq 'Delegate') {
        $accepted = Read-AeroLinkTransitionHandoff -Path $HandoffFile -Role delegate -ActualSourceIdentity $actualIdentity
    }
    else {
        $request = Read-AeroLinkJsonRecord -Path (Join-Path $attemptRoot 'continuation-request.json')
        if ($request.Class -ne 'Valid') {
            $accepted = [pscustomobject]@{ Accepted = $false; Code = 'Unreadable'; Detail = "the continuation request is $($request.Class.ToLower())"; Handoff = $null }
        }
        else {
            $accepted = Read-AeroLinkTransitionHandoff -Path $HandoffFile -Role continuation -ActualSourceIdentity $actualIdentity -ExpectedSourceIdentity ([string]$request.Value.sourceIdentity)
        }
    }
    if (-not $accepted.Accepted) {
        Publish-ActorOutcome -Decision $accepted.Code -Extra @{ detail = $accepted.Detail; mutated = $false }
        $published = $true
        exit 30
    }
    $handoff = $accepted.Handoff
    $plan = $handoff.plan

    Import-Module (Join-Path $PSScriptRoot 'AeroLinkTransition.psm1')
    Import-Module (Join-Path $PSScriptRoot 'AeroLinkInstallation.psm1')
    Import-Module (Join-Path $PSScriptRoot 'AeroLinkProductionSource.psm1')
    Import-Module (Join-Path $PSScriptRoot 'AeroLinkRemoteDemo.psm1')
    try { $lease = Enter-AeroLinkTransition -InstallationRoot ([string]$handoff.installationRoot) }
    catch {
        Publish-ActorOutcome -Decision 'LeaseRejected' -Extra @{ detail = $_.Exception.Message; mutated = $false }
        $published = $true
        exit 32
    }
    if ($lease.Owner) {
        # The outer's lease was not held, so this process became an OWNER - which an actor must never be.
        Exit-AeroLinkTransition -Lease $lease
        $lease = $null
        Publish-ActorOutcome -Decision 'NotADescendant' -Extra @{ detail = 'the HOME transition lease was acquirable, so no live outer authority holds it'; mutated = $false }
        $published = $true
        exit 33
    }
    $identity = Get-AeroLinkProcessIdentityRecord -ProcessId $PID
    $acceptedPath = if ($Phase -eq 'Delegate') { Join-Path $attemptRoot 'delegate-accepted.json' } else { Join-Path $attemptRoot 'continuation-started.json' }
    Publish-AeroLinkJsonAtomic -Path $acceptedPath -Value ([ordered]@{ pid = $PID; startedAt = $identity.StartedAtUtc; image = $identity.ImagePath; sourceIdentity = $actualIdentity
            protocolVersion = [int]$handoff.protocolVersion; operation = [string]$plan.operation; at = (Get-Date).ToUniversalTime().ToString('o') })

    $config = Get-ActorConfig -Plan $plan
    $failures = @()
    $detail = ''
    if ($Phase -eq 'Restore') {
        $topology = [pscustomobject]@{ TunnelRunning = [bool]$request.Value.topology.tunnelRunning; RuntimeRunning = [bool]$request.Value.topology.runtimeRunning }
        $restoreConfig = if ($config) { $config } else { Get-RuntimeOnlyConfig -SourceRoot ([string]$plan.sourceRoot) }
        try {
            $restored = Restore-AeroLinkServiceTopology -Config $restoreConfig -Topology $topology -KeepReady:([bool]$request.Value.keepReady) -Scheduled:([bool]$request.Value.scheduled) -SkipSourceReconciliation
            $detail = [string]$restored.Detail
        }
        catch { $failures += "RestoreFailed: $($_.Exception.Message)" }
    }
    else {
        $operation = [string]$plan.operation
        $preserve = ([string]$plan.policy -eq 'Preserve')
        try {
            switch ($operation) {
                'RemoteDemoStart' {
                    $result = Start-AeroLinkRemoteDemo -Config $config -Scheduled:([bool]$plan.scheduled)
                    if (-not $result.Ready) { $failures += 'RemoteDemoNotReady' }
                    $detail = [string]$result.Detail
                }
                { $_ -in @('Reconcile', 'Update') } {
                    $result = Invoke-AeroLinkProductionSourceReconciliation -Config $config -Scheduled:([bool]$plan.scheduled) -PreserveServiceState:$preserve
                    if ($result.Action -notin @('Updated', 'AlreadyCurrent', 'CachedCanonical')) { $failures += "SourceNotUpdated:$($result.Action)" }
                    $detail = [string]$result.Detail
                }
                { $_ -in @('RuntimeUpdate', 'FirstDeployment') } {
                    $sourceRoot = [string]$plan.sourceRoot
                    $pseudo = if ($config) { $config } else { Get-RuntimeOnlyConfig -SourceRoot $sourceRoot }
                    $topology = [pscustomobject]@{ TunnelRunning = [bool]$plan.topology.tunnelRunning; RuntimeRunning = ($operation -eq 'FirstDeployment' -or [bool]$plan.topology.runtimeRunning) }
                    Assert-AeroLinkDedicatedProductionSource -SourceRoot $sourceRoot | Out-Null
                    $inspect = Update-AeroLinkProductionSource -SourceRoot $sourceRoot -InspectOnly
                    if (-not $inspect.Canonical) { throw "The production source is not canonical: $($inspect.Reason)" }
                    if ($inspect.Action -eq 'UpdateAvailable') {
                        $obligation = New-AeroLinkProductionObligation -SourceRoot $sourceRoot -Config $config -Policy $plan.policy
                        # Records each stop as it succeeds (tunnel first, then the API executing out of the tree).
                        Stop-AeroLinkProductionTransition -Obligation $obligation -Config $config
                        $advance = Update-AeroLinkProductionSource -SourceRoot $sourceRoot -AdvanceToSha $inspect.TargetSha
                        if (-not $advance.Canonical -or $advance.Action -ne 'Updated') { $failures += "SourceNotUpdated:$($advance.Action)" }
                        # Restoration always runs from the source ON DISK - advanced or not - in a fresh, contained process.
                        $continued = Invoke-AeroLinkRemoteDemoHandoff -Config $pseudo -PreserveServiceState -Topology $topology -HeadSha ([string]$advance.HeadSha)
                        $detail = "$($advance.Action): $($advance.Reason) $($continued.Detail)"
                    }
                    elseif ($operation -eq 'FirstDeployment' -or $topology.RuntimeRunning -or $topology.TunnelRunning) {
                        $restored = Restore-AeroLinkServiceTopology -Config $pseudo -Topology $topology -SkipSourceReconciliation
                        $detail = "$($inspect.Action): $($restored.Detail)"
                    }
                    else { $detail = "$($inspect.Action): nothing was running, so nothing was started." }
                }
                'QualificationProbe' {
                    # A launch-context qualification: one probe service through the real authority, nothing else.
                    $probeDirectory = Join-Path ([string]$handoff.logs) 'probe'
                    $powershellPath = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
                    $body = "[IO.File]::WriteAllText('{READY_FILE}', (@{ requestId = '{REQUEST_ID}'; nonce = '{READY_NONCE}'; processId = `$PID } | ConvertTo-Json)); Start-Sleep -Seconds 86400"
                    $response = Request-AeroLinkServiceLaunch -Handoff $handoff -Role qualification-probe -FilePath $powershellPath -Arguments ('-NoProfile -NonInteractive -Command "' + $body + '"') `
                        -Readiness @{ kind = 'marker' } -ReadinessTimeoutSeconds 60 -StandardOutput (Join-Path $probeDirectory 'probe.stdout.log') -StandardError (Join-Path $probeDirectory 'probe.stderr.log')
                    if ([string]$response.outcome -ne 'Succeeded' -or -not $response.restored) { $failures += "RoleNotRestored:qualification-probe:$($response.outcome)/$($response.currentHealth)" }
                    $detail = [string]$response.detail
                    # Qualification seam (never set by the product): hold a TRANSIENT MUTATOR inside the attempt
                    # job, so a task stop or the definition's own hard limit lands while the transition is
                    # actively mutating - not after it has already completed. The preserved probe's identity and
                    # the mutator's are published together for the driver, which cannot see inside the job: the
                    # driver has to be able to prove the probe SURVIVED an ending that leaves no outcome here.
                    $mutatorSeconds = [int](Get-AeroLinkProperty (Get-AeroLinkProperty $handoff 'faults' $null) 'DelegateMutatorSeconds' 0)
                    if ($mutatorSeconds -gt 0) {
                        $mutator = Start-Process -FilePath $env:ComSpec -ArgumentList ('/c ping -n ' + ($mutatorSeconds + 2) + ' 127.0.0.1 > nul') -WindowStyle Hidden -PassThru
                        $mutatorIdentity = Get-AeroLinkProcessIdentityRecord -ProcessId $mutator.Id
                        $activePath = [string](Get-AeroLinkProperty $plan 'activeRecordPath' '')
                        if ($activePath) {
                            $jobName = ''
                            foreach ($entry in @((Read-AeroLinkTransitionEvents -Path (Join-Path $attemptRoot 'transition-job.jsonl')).Events)) { if ($entry.type -in @('Intended', 'Created')) { $jobName = [string]$entry.jobName } }
                            Publish-AeroLinkJsonAtomic -Path $activePath -Value ([ordered]@{ runId = [string](Get-AeroLinkProperty $plan 'runId' ''); attemptId = [string]$handoff.attemptId
                                    operation = [string]$plan.operation; jobName = $jobName; activeAt = (Get-Date).ToUniversalTime().ToString('o')
                                    probe = [ordered]@{ processId = [int]$response.processId; startedAt = [string]$response.startedAt; image = [string]$response.image }
                                    mutator = [ordered]@{ processId = $mutator.Id; startedAt = $mutatorIdentity.StartedAtUtc; image = $mutatorIdentity.ImagePath }
                                    at = (Get-Date).ToUniversalTime().ToString('o') })
                        }
                        $null = $mutator.WaitForExit()
                    }
                }
                'Restore' {
                    $restoreConfig = if ($config) { $config } else { Get-RuntimeOnlyConfig -SourceRoot ([string]$plan.sourceRoot) }
                    $topology = [pscustomobject]@{ TunnelRunning = [bool]$plan.topology.tunnelRunning; RuntimeRunning = [bool]$plan.topology.runtimeRunning }
                    $restored = Restore-AeroLinkServiceTopology -Config $restoreConfig -Topology $topology -KeepReady:(-not $preserve) -Scheduled:([bool]$plan.scheduled) -SkipSourceReconciliation
                    $detail = [string]$restored.Detail
                }
                default { throw "Unknown transition operation '$operation'." }
            }
        }
        catch { $failures += "OperationFailed: $($_.Exception.Message)" }
    }

    $roles = @()
    if ([string]$plan.operation -eq 'QualificationProbe') { $roles = @([ordered]@{ role = 'qualification-probe'; restored = ($failures.Count -eq 0); evidence = $detail }) }
    else { try { $roles = @(Get-ActorRoleResults -Handoff $handoff -Plan $plan -Config $config) }
    catch { $failures += "RoleObservationFailed: $($_.Exception.Message)" }
    foreach ($r in $roles) { if (-not $r.restored) { $failures += "RoleNotRestored:$($r.role):$($r.evidence)" } } }
    $decision = if ($failures.Count) { 'Failed' } else { 'Completed' }
    Publish-ActorOutcome -Decision $decision -Failures $failures -Extra @{ detail = $detail; roles = @($roles); mutated = $true; sourceIdentity = $actualIdentity }
    $published = $true
    Exit-AeroLinkTransition -Lease $lease
    $lease = $null
    exit $(if ($decision -eq 'Completed') { 0 } else { 3 })
}
catch {
    $message = $_.Exception.Message
    try { [IO.File]::AppendAllText((Join-Path $attemptRoot "$role-error.log"), ($_ | Out-String) + $_.ScriptStackTrace + "`r`n") } catch { }
    try {
        if (-not $published) {
            $code = if ($message -like 'EvidenceWriteContention:*') { 'EvidenceWriteContention' } elseif ($message -like 'EvidenceWriteFailed:*') { 'EvidenceWriteFailed' } else { "$(if ($Phase -eq 'Delegate') { 'Delegate' } else { 'Continuation' })Exception" }
            Publish-ActorOutcome -Decision 'Failed' -Failures @("${code}: $message") -Extra @{ roles = @(); mutated = (Test-Path -LiteralPath (Join-Path $attemptRoot 'delegate-accepted.json')) }
        }
        else {
            $previous = (Read-AeroLinkJsonRecord -Path $outcomePath).Value
            $prior = @(@(Get-AeroLinkProperty $previous 'failures' @()) | ForEach-Object { [string]$_ })
            Publish-ActorOutcome -Decision 'Failed' -Failures @($prior + "FinalizationFailed: $message") -Extra @{ publishedDecision = [string](Get-AeroLinkProperty $previous 'decision' ''); roles = @(Get-AeroLinkProperty $previous 'roles' @()); mutated = $true }
        }
    }
    catch { }
    if ($lease) { try { Exit-AeroLinkTransition -Lease $lease } catch { } }
    exit 1
}
