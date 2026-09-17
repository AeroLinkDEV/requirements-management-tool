#Requires -Version 5.1
<#
    Qualifies a scheduled-task launch context for HOME transitions (#1041, #1043, #1053).

    A HOME transition refuses before it touches anything unless its OWN launch context is qualified: a probe service
    launched through the real transition authority, in a context with this exact descriptor, survived every cleanup
    path that context applies. This tool produces that evidence and nothing else.

      -TaskName <task>   DRIVER (run it from an elevated Windows PowerShell for an S4U task; S4U registration needs
                         elevation). It registers a disposable TWIN of the task - same principal, same settings, same
                         action image, no triggers, the action arguments pointed at this script's probe entry - and
                         drives it three times:
                            run 1  the entry completes normally: transientJob, wrapperExit, taskCompletion
                            run 2  the task is stopped while the entry is still running: taskStop
                            run 3  the entry outlives the definition's ExecutionTimeLimit: hardTimeout
                         After each ending it checks, by exact identity, whether the probe service is still running,
                         then stops it. Every run must report the same descriptor, or nothing is written. The twin is
                         unregistered and every probe is proven stopped before the tool returns.
      -Probe             the ENTRY the twin's action runs, inside the context being qualified.

    A qualification binds the definition (principal, settings, action image), the native placement code, the host
    image and the ACTUAL token (elevation, type, integrity, Administrators attributes) the context produced - so the
    run record is also the B3 measurement of that token beside the exported definition.

    It changes no installed task and starts no AeroLink service. Run 3 lasts as long as the definition's limit.
#>
[CmdletBinding(DefaultParameterSetName = 'Drive')]
param(
    [Parameter(Mandatory)][string]$InstallationRoot,
    [Parameter(ParameterSetName = 'Probe', Mandatory)][switch]$Probe,
    [Parameter(ParameterSetName = 'Probe', Mandatory)][string]$RunId,
    [Parameter(ParameterSetName = 'Probe')][int]$HoldSeconds = 0,
    # Hold a transient mutator INSIDE the attempt for this long, so an interrupt lands during active work.
    [Parameter(ParameterSetName = 'Probe')][int]$MutatorSeconds = 0,
    [Parameter(ParameterSetName = 'Drive', Mandatory)][string]$TaskName,
    [Parameter(ParameterSetName = 'Drive')][string]$TwinNamePrefix = 'AeroLinkContextQualification-',
    [Parameter(ParameterSetName = 'Drive')][int]$EndingTimeoutSeconds = 600
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'AeroLinkTransitionKernel.psm1') -DisableNameChecking
Import-Module (Join-Path $PSScriptRoot 'AeroLinkTransitionAuthority.psm1') -DisableNameChecking
$InstallationRoot = [IO.Path]::GetFullPath($InstallationRoot).TrimEnd('\')
$runs = Join-Path (Get-AeroLinkTransitionStateRoot -InstallationRoot $InstallationRoot) 'qualification-runs'
New-Item -ItemType Directory -Path $runs -Force | Out-Null

if ($Probe) {
    $record = [ordered]@{ runId = $RunId; pid = $PID; at = (Get-Date).ToUniversalTime().ToString('o') }
    $recordPath = Join-Path $runs "$RunId.json"
    $lease = $null
    try {
        Import-Module (Join-Path $PSScriptRoot 'AeroLinkTransition.psm1')
        Import-Module (Join-Path $PSScriptRoot 'AeroLinkRuntimeIdentity.psm1') -DisableNameChecking
        $qualification = Test-AeroLinkLaunchContextQualification -InstallationRoot $InstallationRoot
        $record['contextValid'] = [bool]$qualification.Context.Valid
        $record['contextReason'] = [string]$qualification.Context.Reason
        $record['descriptor'] = $qualification.Descriptor
        $record['descriptorHash'] = $qualification.DescriptorHash
        $record['attestation'] = Get-AeroLinkProperty $qualification.Context 'Attestation' $null
        $record['token'] = Get-AeroLinkTokenFacts
        $record['breakawayPermittedByImmediateJob'] = [bool]$qualification.BreakawayPermittedByImmediateJob
        if (-not $qualification.Context.Valid) { throw "the launch context is unidentified: $($qualification.Context.Reason)" }
        # Qualifications of several definitions may overlap; a lease held by another probe is waited for, boundedly.
        $leaseDeadline = (Get-Date).AddSeconds(300)
        while ($true) {
            try { $lease = Enter-AeroLinkTransition -InstallationRoot $InstallationRoot; break }
            catch { if ((Get-Date) -ge $leaseDeadline) { throw }; Start-Sleep -Seconds 3 }
        }
        if (-not $lease.Owner) { throw 'the qualification probe must own the HOME transition lease' }
        $repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
        $identity = [string](Get-AeroLinkSourceFingerprint -RepositoryRoot $repositoryRoot).Identity
        $faults = @{}
        $plan = [ordered]@{ operation = 'QualificationProbe'; sourceRoot = $repositoryRoot; configPath = $null; policy = 'Preserve'; scheduled = $true
            topology = [ordered]@{ tunnelRunning = $false; runtimeRunning = $false } }
        if ($MutatorSeconds -gt 0) {
            $faults['DelegateMutatorSeconds'] = $MutatorSeconds
            $plan['runId'] = $RunId
            $plan['activeRecordPath'] = Join-Path $runs "$RunId.active.json"
        }
        $chain = Invoke-AeroLinkTransitionChain -InstallationRoot $InstallationRoot -Lease $lease -Caller QualificationProbe `
            -Plan $plan -Faults $faults `
            -DelegateScript (Join-Path $PSScriptRoot 'Invoke-AeroLinkTransitionActor.ps1') -DelegateSourceIdentity $identity `
            -RequiredRoles @([pscustomobject]@{ role = 'qualification-probe'; launchRequired = $true; readiness = @{ kind = 'marker' } }) `
            -DeadlineSeconds 300 -Qualification $qualification -QualificationProbe
        $launch = @(@(Get-AeroLinkProperty $chain.Outcome 'launches' @()) | Where-Object { $_.role -eq 'qualification-probe' -and $_.outcome -eq 'Succeeded' })
        $record['decision'] = $chain.Decision
        $record['attemptId'] = $chain.AttemptId
        $record['containmentProven'] = [bool](Get-AeroLinkProperty (Get-AeroLinkProperty $chain.Outcome 'cleanup' $null) 'transitionContainmentProven' $false)
        $record['probe'] = if ($launch.Count) { [ordered]@{ processId = [int]$launch[0].processId; startedAt = [string]$launch[0].startedAt; image = [string]$launch[0].image } } else { $null }
        $record['detail'] = $chain.Detail
    }
    catch { $record['error'] = $_.Exception.Message }
    finally {
        if ($lease) { try { Exit-AeroLinkTransition -Lease $lease } catch { $record['leaseReleaseError'] = $_.Exception.Message } }
        $record['publishedAt'] = (Get-Date).ToUniversalTime().ToString('o')
        Publish-AeroLinkJsonAtomic -Path $recordPath -Value $record
    }
    # Held AFTER the record, so a driver can end the context while this entry is still running.
    if ($HoldSeconds -gt 0) { Start-Sleep -Seconds $HoldSeconds }
    exit $(if ($record.Contains('error')) { 1 } else { 0 })
}

# ======================================== DRIVER ========================================
# A qualification is only usable if EVERY required observation was made and EVERY ending was the one it was
# supposed to be. The record is therefore written LAST, after the disposable twin is proven unregistered and
# every probe this driver ever saw is proven gone; a cleanup or query failure withholds the record instead of
# leaving a consumable Qualified beside a nonzero exit.
$K = [AeroLink.TransitionV1.Kernel]
$source = Get-ScheduledTask -TaskName $TaskName -ErrorAction Stop
$sourceXml = Export-ScheduledTask -TaskName $source.TaskName -TaskPath $source.TaskPath
$document = New-Object Xml.XmlDocument
$document.LoadXml($sourceXml)
$ns = New-Object Xml.XmlNamespaceManager($document.NameTable)
$ns.AddNamespace('t', 'http://schemas.microsoft.com/windows/2004/02/mit/task')
$exec = @($document.SelectNodes('//t:Actions/t:Exec', $ns))
if ($exec.Count -ne 1) { throw "Task '$TaskName' must have exactly one Exec action to be qualified; it has $($exec.Count)." }
$argumentsNode = $exec[0].SelectSingleNode('t:Arguments', $ns)
$originalArguments = if ($argumentsNode) { $argumentsNode.InnerText } else { '' }
$fileMatch = [regex]::Match($originalArguments, '(?i)-File\s+(?:"[^"]+\.ps1"|\S+\.ps1)(?:\s+[^"&|]*)?')
if (-not $fileMatch.Success) { throw "Task '$TaskName' does not run a PowerShell script with -File; its action cannot be probed." }
$limitNode = $document.SelectSingleNode('//t:Settings/t:ExecutionTimeLimit', $ns)
$limit = if ($limitNode -and $limitNode.InnerText -and $limitNode.InnerText -ne 'PT0S') { [Xml.XmlConvert]::ToTimeSpan($limitNode.InnerText) } else { $null }
# A path that cannot be exercised is not an observation, and it must never silently disappear from the
# verdict: a definition with no bounded ending cannot be shown to end a transition in a bounded way.
if (-not $limit) { throw "Task '$TaskName' has no ExecutionTimeLimit (or PT0S). The hard-timeout path cannot be exercised against it, so it cannot be qualified." }
$triggers = $document.SelectSingleNode('//t:Triggers', $ns)
if ($triggers) { [void]$triggers.ParentNode.RemoveChild($triggers) }
$registration = $document.SelectSingleNode('//t:RegistrationInfo/t:URI', $ns)
if ($registration) { [void]$registration.ParentNode.RemoveChild($registration) }

$twin = $TwinNamePrefix + [guid]::NewGuid().ToString('N').Substring(0, 8)
$twinPath = ($source.TaskPath.TrimEnd('\')) + '\' + $twin
if (Get-ScheduledTask -TaskName $twin -ErrorAction SilentlyContinue) { throw "A task named '$twin' already exists; refusing to overwrite a task this run does not own." }
$summaryPath = Join-Path $runs "$twin.summary.json"
$summary = [ordered]@{ tool = 'aerolink-context-qualification'; sourceTask = ($source.TaskPath + $source.TaskName); twin = $twin
    sourceDefinitionHash = (Get-AeroLinkSha256Text (Get-AeroLinkTaskDefinitionCanonical -Xml $sourceXml)); exportedXmlSha256 = (Get-AeroLinkSha256Text $sourceXml)
    executionTimeLimit = $(if ($limit) { $limit.ToString() } else { 'none' }); runs = @(); startedAt = (Get-Date).ToUniversalTime().ToString('o') }
$probes = [System.Collections.Generic.List[object]]::new()
$paths = [ordered]@{}
$descriptors = @{}
$descriptor = $null
$cleanupErrors = [System.Collections.Generic.List[string]]::new()
$twinRegistered = $false

function Set-TwinArguments([string]$Id, [int]$Hold, [int]$MutatorSeconds = 0) {
    $probeCall = '-File "' + (Join-Path $PSScriptRoot 'Invoke-AeroLinkLaunchContextQualification.ps1') + '" -Probe -InstallationRoot "' + $InstallationRoot + '" -RunId ' + $Id + ' -HoldSeconds ' + $Hold
    if ($MutatorSeconds -gt 0) { $probeCall += ' -MutatorSeconds ' + $MutatorSeconds }
    # Everything this definition put AFTER its product script is that product's own arguments and must not be
    # handed to the probe entry: the twin runs the probe INSTEAD of the product. What precedes -File (window
    # style, execution policy, a cmd wrapper that sets the environment) is preserved, and the twin's own output
    # is redirected to the run directory so it can never block on an unread inherited stdio handle.
    $twinLog = Join-Path $runs "$Id.twin.log"
    $script:argumentsNode.InnerText = $originalArguments.Substring(0, $fileMatch.Index) + $probeCall + ' > "' + $twinLog + '" 2>&1'
    Register-ScheduledTask -TaskName $twin -Xml $document.OuterXml -Force -ErrorAction Stop | Out-Null
}

function Get-TwinInstance {
    <# The running instance of THIS twin through the Task Scheduler API, or $null when none is running. #>
    $service = New-Object -ComObject 'Schedule.Service'
    $service.Connect()
    foreach ($running in @($service.GetRunningTasks(1))) {
        if ($running -and ([string]$running.Path).TrimEnd('\') -ieq $script:twinPath.TrimEnd('\')) {
            return [pscustomobject]@{ InstanceGuid = [string]$running.InstanceGuid; EnginePid = [int]$running.EnginePID }
        }
    }
    return $null
}

function Get-TwinInfo([datetime]$Since) {
    <# Task result bound to THIS run: no tolerance window, and Unknown is never absence. #>
    $last = $null
    for ($i = 0; $i -lt 20; $i++) {
        try { $last = Get-ScheduledTask -TaskName $twin -ErrorAction Stop | Get-ScheduledTaskInfo }
        catch { return [pscustomobject]@{ Class = 'Unknown'; Detail = $_.Exception.Message; Info = $null } }
        if ($last.LastRunTime -and $last.LastRunTime -ge $Since) { return [pscustomobject]@{ Class = 'Valid'; Detail = ''; Info = $last } }
        Start-Sleep -Milliseconds 500
    }
    return [pscustomobject]@{ Class = 'Stale'; Detail = "the scheduler still reports LastRunTime '$($last.LastRunTime)' before this run began at $Since"; Info = $last }
}

function Wait-RunRecord([string]$Id, [int]$Seconds) {
    $path = Join-Path $runs "$Id.json"
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) { $read = Read-AeroLinkJsonRecord -Path $path; if ($read.Class -eq 'Valid') { return $read.Value }; Start-Sleep -Seconds 1 }
    return $null
}
function Test-ProbeSurvived($Record) {
    if (-not $Record -or -not (Get-AeroLinkProperty $Record 'probe' $null)) { return $false }
    Start-Sleep -Milliseconds 1500
    return ($K::Classify([int]$Record.probe.processId, (ConvertTo-AeroLinkUtcIso $Record.probe.startedAt), [string]$Record.probe.image) -eq 'RunningMatch')
}
function Track-Probe($Record) {
    <# Every probe this driver ever OBSERVED is owned from the moment it is seen, so a later failure cannot
       leave one out of cleanup. #>
    if (-not $Record -or -not (Get-AeroLinkProperty $Record 'probe' $null)) { return $null }
    $identity = [pscustomobject]@{ ProcessId = [int]$Record.probe.processId; StartedAtUtc = (ConvertTo-AeroLinkUtcIso $Record.probe.startedAt); ImagePath = [string]$Record.probe.image }
    if (-not @($script:probes | Where-Object { $_.ProcessId -eq $identity.ProcessId -and $_.StartedAtUtc -eq $identity.StartedAtUtc }).Count) { $script:probes.Add($identity) }
    return $identity
}
function Stop-TrackedProbes {
    foreach ($identity in @($script:probes)) {
        if ($K::Classify($identity.ProcessId, $identity.StartedAtUtc, $identity.ImagePath) -eq 'RunningMatch') { Stop-Process -Id $identity.ProcessId -Force -ErrorAction SilentlyContinue }
    }
}
function Add-RunEvidence([string]$Name, $Record, [string[]]$PathNames, [hashtable]$Survival, $Info) {
    $observed = [bool]($Record -and (Get-AeroLinkProperty $Record 'probe' $null))
    if ($Record -and (Get-AeroLinkProperty $Record 'descriptorHash' '')) { $script:descriptors[[string]$Record.descriptorHash] = $true; $script:descriptor = $Record.descriptor }
    foreach ($path in $PathNames) {
        $script:paths[$path] = [ordered]@{ applicable = $true; observed = $observed; survived = $(if ($observed) { [bool]$Survival[$path] } else { $null })
            setupFailure = $(if ($observed) { '' } else { "no probe was launched (record: $(if ($Record) { "$($Record.decision) $(Get-AeroLinkProperty $Record 'error' '')" } else { 'none' }))" })
            run = $Name; lastTaskResult = $(if ($Info -and $Info.Class -eq 'Valid') { $Info.Info.LastTaskResult } else { $null })
            ending = $(if ($Survival['ending']) { $Survival['ending'] } else { '' }) }
    }
    $script:summary.runs += [ordered]@{ run = $Name; record = $Record; paths = $PathNames; survival = $Survival
        lastTaskResult = $(if ($Info -and $Info.Class -eq 'Valid') { $Info.Info.LastTaskResult } else { $null })
        endingClass = $(if ($Info) { $Info.Class } else { 'Unknown' }); endingDetail = $(if ($Info) { $Info.Detail } else { '' }) }
}

function Invoke-TwinRun {
    <#
      One run of the twin, with the ending CAUSED on purpose and then verified: the instance that was started is
      the one whose ending is judged, and the scheduler result must belong to it. 'survived' for a path means the
      probe survived the ending the path names - an early exit is a failed experiment, never evidence.
    #>
    param([Parameter(Mandatory)][string]$Name, [int]$HoldSeconds, [Parameter(Mandatory)][ValidateSet('Completion', 'DriverStop', 'HardLimit')][string]$Ending,
        [int]$LimitSeconds = 0, [int]$RecordTimeoutSeconds = 600, [int]$MutatorSeconds = 0)
    $id = "$twin-$Name"
    Set-TwinArguments $id $HoldSeconds $MutatorSeconds
    $since = Get-Date
    Start-ScheduledTask -TaskName $twin
    $instance = $null
    $appear = (Get-Date).AddSeconds(60)
    while (-not $instance -and (Get-Date) -lt $appear) { $instance = Get-TwinInstance; if (-not $instance) { Start-Sleep -Milliseconds 500 } }
    # The synchronization point for an interrupt-during-mutation run: wait until the attempt has PUBLISHED its
    # live mutator identity, so the ending this run causes cannot land after the transition already finished.
    $active = $null
    if ($MutatorSeconds -gt 0) {
        $activePath = Join-Path $runs "$id.active.json"
        $activeDeadline = (Get-Date).AddSeconds(300)
        while (-not $active -and (Get-Date) -lt $activeDeadline) { $read = Read-AeroLinkJsonRecord -Path $activePath; if ($read.Class -eq 'Valid') { $active = $read.Value }; Start-Sleep -Milliseconds 250 }
    }
    $record = $null
    $stopIssued = $false
    $deadline = $since.AddSeconds($LimitSeconds + $RecordTimeoutSeconds)
    if ($Ending -ne 'HardLimit') { $deadline = (Get-Date).AddSeconds($RecordTimeoutSeconds) }
    $cause = 'StillRunning'
    while ((Get-Date) -lt $deadline) {
        if (-not $record) { $record = Wait-RunRecord $id 2 }
        # The stop must land while the attempt is MUTATING. The active-mutation record (published by the entry
        # itself, with the preserved probe's identity) is the synchronization point; a completed run record is
        # only the fallback for a definition that cannot hold a mutator.
        if ($Ending -eq 'DriverStop' -and -not $stopIssued -and ($active -or $record)) { try { Stop-ScheduledTask -TaskName $twin -ErrorAction Stop; $stopIssued = $true } catch { } }
        $current = Get-TwinInstance
        if ($current -and -not $instance) { $instance = $current }
        if ($instance -and -not $current) {
            $elapsed = ((Get-Date) - $since).TotalSeconds
            $cause = switch ($Ending) {
                'Completion' { 'SelfEnded' }
                'DriverStop' { if ($stopIssued) { 'DriverStopped' } else { 'EndedBeforeStop' } }
                'HardLimit' { if ($LimitSeconds -gt 0 -and $elapsed -ge ($LimitSeconds - 20)) { 'HardLimitFired' } else { 'EndedEarly' } }
            }
            break
        }
        Start-Sleep -Milliseconds 500
    }
    if ($cause -eq 'StillRunning' -and $Ending -ne 'HardLimit') {
        # The bounded wait expired: this ending was not observed. Stop it and record the failure honestly.
        try { Stop-ScheduledTask -TaskName $twin -ErrorAction Stop } catch { }
        $cause = 'TimedOutWaitingForEnding'
    }
    $info = Get-TwinInfo $since
    if ($record) { $null = Track-Probe $record }
    elseif ($active) { $null = Track-Probe $active }
    $mutatorState = ''
    $attemptEvidence = $null
    if ($active) {
        $mutator = Get-AeroLinkProperty $active 'mutator' $null
        $mutatorPid = [int](Get-AeroLinkProperty $mutator 'processId' 0)
        if ($mutatorPid -gt 0) {
            $mutatorState = $K::Classify($mutatorPid, (ConvertTo-AeroLinkUtcIso (Get-AeroLinkProperty $mutator 'startedAt' '')), [string](Get-AeroLinkProperty $mutator 'image' ''))
        }
        # The attempt's own completion evidence, if any: did the old attempt prove its mutator terminated?
        $attemptId = [string](Get-AeroLinkProperty $active 'attemptId' '')
        if ($attemptId) {
            $attemptRoot = Join-Path (Get-AeroLinkTransitionStateRoot -InstallationRoot $InstallationRoot) $attemptId
            $receipt = Test-AeroLinkCleanupReceipt -Path (Join-Path $attemptRoot 'cleanup.json') -AttemptId $attemptId
            $attemptEvidence = [ordered]@{ attemptId = $attemptId; receiptClass = $receipt.Class; containmentProven = [bool](Get-AeroLinkProperty $receipt.Receipt 'containmentProven' $false)
                observedBy = [string](Get-AeroLinkProperty $receipt.Receipt 'observedBy' ''); detail = $receipt.Reason
                quiescence = (Get-AeroLinkTransitionQuiescence -AttemptRoot $attemptRoot -AttemptId $attemptId).State }
        }
    }
    return [pscustomobject]@{ Run = $Name; RunId = $id; Instance = $instance; Record = $record; Info = $info; Cause = $cause; Active = $active
        MutatorState = $mutatorState; AttemptEvidence = $attemptEvidence
        ElapsedSeconds = [int]((Get-Date) - $since).TotalSeconds; Since = $since; StopIssued = $stopIssued }
}

function Test-EndingMatched($Run, [string]$Expected, [bool]$RequireActive = $false) {
    <# { Matched, Detail }: the observed ending, the instance it belongs to, and the scheduler result. #>
    if (-not $Run.Instance) { return [pscustomobject]@{ Matched = $false; Detail = 'the task instance was never observed running' } }
    if ($Run.Cause -ne $Expected) { return [pscustomobject]@{ Matched = $false; Detail = "the run ended as $($Run.Cause), not $Expected" } }
    if ($RequireActive -and -not $Run.Active) { return [pscustomobject]@{ Matched = $false; Detail = 'the ending did not land on an active mutator: no active-mutation record was published' } }
    if ($Run.Info.Class -ne 'Valid') { return [pscustomobject]@{ Matched = $false; Detail = "the scheduler result is $($Run.Info.Class.ToLower()): $($Run.Info.Detail)" } }
    $result = [int]$Run.Info.Info.LastTaskResult
    if ($Expected -eq 'SelfEnded' -and $result -ne 0) { return [pscustomobject]@{ Matched = $false; Detail = "a self-ended run reported task result $result, not 0" } }
    if ($Expected -in @('DriverStopped', 'HardLimitFired') -and $result -ne 267014) { return [pscustomobject]@{ Matched = $false; Detail = "a terminated run reported task result $result, not 267014" } }
    if ($Expected -eq 'DriverStopped' -and $Run.ElapsedSeconds -ge ($limit.TotalSeconds - 60)) { return [pscustomobject]@{ Matched = $false; Detail = "the stop happened at $($Run.ElapsedSeconds)s, inside the definition's own $([int]$limit.TotalSeconds)s limit, so it was not a stop test" } }
    if ($Run.Record -and (Get-AeroLinkProperty (Get-AeroLinkProperty $Run.Record 'attestation' $null) 'instance' '')) {
        $recorded = [string](Get-AeroLinkProperty $Run.Record.attestation 'instance' '')
        if ($recorded -and $recorded -ne $Run.Instance.InstanceGuid) { return [pscustomobject]@{ Matched = $false; Detail = "the run record belongs to instance $recorded, not $($Run.Instance.InstanceGuid)" } }
    }
    return [pscustomobject]@{ Matched = $true; Detail = "$Expected at $($Run.ElapsedSeconds)s; task result $result" }
}

$exitCode = 1
$written = $null
try {
    # ---- run 1: normal completion ----
    $run1 = Invoke-TwinRun -Name 'run1' -HoldSeconds 0 -Ending Completion -RecordTimeoutSeconds $EndingTimeoutSeconds
    $ending1 = Test-EndingMatched $run1 'SelfEnded'
    $alive1 = Test-ProbeSurvived $run1.Record
    Add-RunEvidence 'run1' $run1.Record @('transientJob', 'wrapperExit', 'taskCompletion') @{
        transientJob = ($alive1 -and $ending1.Matched -and [bool](Get-AeroLinkProperty $run1.Record 'containmentProven' $false))
        wrapperExit = ($alive1 -and $ending1.Matched); taskCompletion = ($alive1 -and $ending1.Matched)
        ending = $ending1.Detail } $run1.Info
    Stop-TrackedProbes

    # ---- run 2: explicit stop while the entry is still running ----
    $run2 = Invoke-TwinRun -Name 'run2' -HoldSeconds 3600 -Ending DriverStop -RecordTimeoutSeconds $EndingTimeoutSeconds -MutatorSeconds 3600
    $ending2 = Test-EndingMatched $run2 'DriverStopped' -RequireActive $true
    # An interruption during active mutation leaves no outcome record here: the preserved probe's identity comes
    # from the active-mutation record, and its survival is what the taskStop path claims.
    $observation2 = if ($run2.Record) { $run2.Record } else { $run2.Active }
    $alive2 = Test-ProbeSurvived $observation2
    Add-RunEvidence 'run2' $observation2 @('taskStop') @{ taskStop = ($alive2 -and $ending2.Matched); ending = $ending2.Detail; active = $run2.Active
        mutatorState = $run2.MutatorState; attempt = $run2.AttemptEvidence } $run2.Info
    Stop-TrackedProbes

    # ---- run 3: the definition's own hard time limit ----
    $run3 = Invoke-TwinRun -Name 'run3' -HoldSeconds ([int]$limit.TotalSeconds + 600) -Ending HardLimit -LimitSeconds ([int]$limit.TotalSeconds) -RecordTimeoutSeconds $EndingTimeoutSeconds -MutatorSeconds ([int]$limit.TotalSeconds + 600)
    $ending3 = Test-EndingMatched $run3 'HardLimitFired' -RequireActive $true
    $observation3 = if ($run3.Record) { $run3.Record } else { $run3.Active }
    $alive3 = Test-ProbeSurvived $observation3
    Add-RunEvidence 'run3' $observation3 @('hardTimeout') @{ hardTimeout = ($alive3 -and $ending3.Matched); ending = $ending3.Detail; active = $run3.Active
        mutatorState = $run3.MutatorState; attempt = $run3.AttemptEvidence } $run3.Info
    Stop-TrackedProbes

    if ($descriptors.Count -ne 1 -or -not $descriptor) {
        $summary['verdict'] = 'Unqualifiable'
        $summary['detail'] = "the probe runs reported $($descriptors.Count) distinct descriptors; nothing was written"
    }
    else { $summary['verdict'] = 'PendingCleanup' }
}
catch { $summary['error'] = $_.Exception.Message }
finally {
    # ---- cleanup FIRST: every owned probe, then the twin, and only then any consumable record. ----
    Stop-TrackedProbes
    $existing = $null
    try { $existing = Get-ScheduledTask -TaskName $twin -ErrorAction Stop }
    catch { if (-not ($_.FullyQualifiedErrorId -like 'CmdletizationQuery_NotFound*')) { $cleanupErrors.Add("the twin's task state could not be read: $($_.Exception.Message)") } }
    if ($existing) {
        $twinRegistered = $true
        if ($existing.State -eq 'Running') { try { Stop-ScheduledTask -TaskName $twin } catch { } }
        try { Unregister-ScheduledTask -TaskName $twin -Confirm:$false -ErrorAction Stop }
        catch { $cleanupErrors.Add("the twin task could not be unregistered: $($_.Exception.Message)") }
    }
    Start-Sleep -Seconds 1
    try {
        $remaining = Get-ScheduledTask -TaskName $twin -ErrorAction Stop
        if ($remaining) { $cleanupErrors.Add('the twin task still exists after unregistration') }
    }
    catch { if (-not ($_.FullyQualifiedErrorId -like 'CmdletizationQuery_NotFound*')) { $cleanupErrors.Add("the twin's absence could not be proven: $($_.Exception.Message)") } }
    Stop-TrackedProbes
    Start-Sleep -Milliseconds 500
    $summary['probesProvenStopped'] = @($probes | ForEach-Object { [ordered]@{ processId = $_.ProcessId; state = $K::Classify($_.ProcessId, $_.StartedAtUtc, $_.ImagePath) } })
    foreach ($probeState in @($summary.probesProvenStopped | Where-Object { $_.state -eq 'RunningMatch' -or $_.state -like 'Unknown:*' })) {
        $cleanupErrors.Add("probe pid $($probeState.processId) is $($probeState.state), which is not a proven stop")
    }
    $summary['twinRemaining'] = [bool](Get-ScheduledTask -TaskName $twin -ErrorAction SilentlyContinue)
    $summary['cleanupErrors'] = @($cleanupErrors)
    if ($cleanupErrors.Count) { $summary['cleanupError'] = ($cleanupErrors -join '; ') }
    # Publish usable qualification ONLY after every observation and every cleanup succeeded.
    if ($cleanupErrors.Count -eq 0 -and -not $summary.Contains('error') -and $summary['verdict'] -eq 'PendingCleanup') {
        try {
            $d = [ordered]@{}; foreach ($property in $descriptor.PSObject.Properties) { $d[$property.Name] = $property.Value }
            $written = Write-AeroLinkLaunchContextQualification -InstallationRoot $InstallationRoot -Descriptor $d -DescriptorHash (@($descriptors.Keys)[0]) -Paths $paths `
                -RequiredPaths @('transientJob', 'wrapperExit', 'taskCompletion', 'taskStop', 'hardTimeout') -Detail "probe survived every applicable path of the twin of $($summary.sourceTask)"
            $summary['verdict'] = $written.verdict
            $summary['detail'] = $written.detail
            $summary['descriptorHash'] = @($descriptors.Keys)[0]
            $summary['descriptor'] = $d
        }
        catch { $summary['verdict'] = 'Unqualifiable'; $summary['detail'] = "the qualification record could not be written: $($_.Exception.Message)" }
    }
    elseif ($summary['verdict'] -eq 'PendingCleanup') {
        $summary['verdict'] = 'CleanupFailed'
        $summary['detail'] = "no qualification record was written: $(@($cleanupErrors) -join '; ')"
    }
    if ($summary['verdict'] -eq 'Qualified') { $exitCode = 0 }
    $summary['finishedAt'] = (Get-Date).ToUniversalTime().ToString('o')
    Publish-AeroLinkJsonAtomic -Path $summaryPath -Value $summary
    Write-Host "Launch-context qualification of $($summary.sourceTask): $($summary.verdict). $($summary['detail'])"
    Write-Host "Evidence: $summaryPath"
}
exit $exitCode
