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
    # The chain's own budget for THIS experiment. It must cover the intended ending: the default 300 s would
    # collect the mutator long before a 45/135-minute task limit and leave the shell sleeping, which is not an
    # observation of the hard limit. The driver computes it per run; production callers keep the default.
    [Parameter(ParameterSetName = 'Probe')][int]$ChainDeadlineSeconds = 300,
    [Parameter(ParameterSetName = 'Drive', Mandatory)][string]$TaskName,
    [Parameter(ParameterSetName = 'Drive')][string]$TwinNamePrefix = 'AeroLinkContextQualification-',
    [Parameter(ParameterSetName = 'Drive')][int]$EndingTimeoutSeconds = 600,
    # Where the EXPERIMENTS keep their durable state. The default is a disposable sibling root inside the
    # installation being qualified: a terminating experiment can leave an attempt whose termination cannot be
    # proven (the witness dies with its task), and admission - correctly - refuses every later transition while
    # such an attempt exists. Running the experiments in their own state root keeps that refusal out of the
    # installation the record is FOR, while the probe still measures the real context and the real kernel.
    [Parameter(ParameterSetName = 'Drive')][string]$ProbeStateRoot = ''
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'AeroLinkTransitionKernel.psm1') -DisableNameChecking
Import-Module (Join-Path $PSScriptRoot 'AeroLinkTransitionAuthority.psm1') -DisableNameChecking
# The one canonical identity/termination rule (Astra review 2ec778d5, F801-1): native creation FILETIME plus
# identity-bound termination through a single verified handle. No bare-pid fallback exists.
Import-Module (Join-Path $PSScriptRoot 'AeroLinkProcessTermination.psm1') -DisableNameChecking
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
            # Inside the PROBE this script's own state root IS the probe root, so the active record lives under
            # its own $runs; $probeRuns is a driver-side variable and is not defined here.
            $plan['activeRecordPath'] = Join-Path $runs "$RunId.active.json"
        }
        $chain = Invoke-AeroLinkTransitionChain -InstallationRoot $InstallationRoot -Lease $lease -Caller QualificationProbe `
            -Plan $plan -Faults $faults `
            -DelegateScript (Join-Path $PSScriptRoot 'Invoke-AeroLinkTransitionActor.ps1') -DelegateSourceIdentity $identity `
            -RequiredRoles @([pscustomobject]@{ role = 'qualification-probe'; launchRequired = $true; readiness = @{ kind = 'marker' } }) `
            -DeadlineSeconds $ChainDeadlineSeconds -Qualification $qualification -QualificationProbe
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
if ([string]::IsNullOrWhiteSpace($ProbeStateRoot)) { $ProbeStateRoot = Join-Path $InstallationRoot 'qualification-probe-state' }
$ProbeStateRoot = [IO.Path]::GetFullPath($ProbeStateRoot).TrimEnd('\')
New-Item -ItemType Directory -Path $ProbeStateRoot -Force | Out-Null
$probeRuns = Join-Path (Get-AeroLinkTransitionStateRoot -InstallationRoot $ProbeStateRoot) 'qualification-runs'
New-Item -ItemType Directory -Path $probeRuns -Force | Out-Null
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
$summary['probeStateRoot'] = $ProbeStateRoot
$probes = [System.Collections.Generic.List[object]]::new()
# Engine identities, never bare pids: a historical numeric engine pid may belong to an unrelated process by the
# time cleanup runs (Astra review 801751be, F801-1), so ownership must carry the creation time with the pid.
$instanceEngines = [System.Collections.Generic.List[object]]::new()
$twinEngineIdentityCache = @{}
$observedIdentities = [System.Collections.Generic.List[object]]::new()
# Tree entries that were discovered but whose native lifetime could not be read: they must survive to the final
# verdict even if the root exits before the final tree scan (Astra review 7e878834, F801-1).
$twinTreeUnresolved = [System.Collections.Generic.List[object]]::new()
$paths = [ordered]@{}
$descriptors = @{}
$runDescriptors = [ordered]@{}
$descriptor = $null
$cleanupErrors = [System.Collections.Generic.List[string]]::new()
$twinRegistered = $false

function Set-TwinArguments([string]$Id, [int]$Hold, [int]$MutatorSeconds = 0, [int]$ChainDeadlineSeconds = 300) {
    $probeCall = '-File "' + (Join-Path $PSScriptRoot 'Invoke-AeroLinkLaunchContextQualification.ps1') + '" -Probe -InstallationRoot "' + $ProbeStateRoot + '" -RunId ' + $Id + ' -HoldSeconds ' + $Hold
    if ($MutatorSeconds -gt 0) { $probeCall += ' -MutatorSeconds ' + $MutatorSeconds }
    $probeCall += ' -ChainDeadlineSeconds ' + $ChainDeadlineSeconds
    # Everything this definition put AFTER its product script is that product's own arguments and must not be
    # handed to the probe entry: the twin runs the probe INSTEAD of the product. What precedes -File (window
    # style, execution policy, a cmd wrapper that sets the environment) is preserved, and the twin's own output
    # is redirected to the run directory so it can never block on an unread inherited stdio handle.
    $twinLog = Join-Path $runs "$Id.twin.log"
    $rewritten = $originalArguments.Substring(0, $fileMatch.Index) + $probeCall + ' > "' + $twinLog + '" 2>&1'
    # cmd.exe strips the first and last quote of a `cmd /c "..."` command line. When the definition is wrapped
    # that way, the rewritten tail must still END in a quote or the closing quote of the redirection target is
    # the one cmd removes and the whole action fails as a malformed filename (measured).
    if ($originalArguments -match '/c\s+"' -and $originalArguments.TrimEnd().EndsWith('"')) { $rewritten += '"' }
    $script:argumentsNode.InnerText = $rewritten
    Register-ScheduledTask -TaskName $twin -Xml $document.OuterXml -Force -ErrorAction Stop | Out-Null
}

function New-TwinProcessIdentity([int]$ProcessId, [string]$Role = '', [int]$Depth = 0, [string]$Source = '') {
    <# Canonical full-precision identity (native creation FILETIME) - see AeroLinkProcessTermination.psm1. #>
    return New-AeroLinkProcessIdentity -ProcessId $ProcessId -Role $Role -Depth $Depth -Source $Source
}

function Test-TwinProcessIdentity($Identity) {
    <# Match | Gone | Reused | Unknown on the canonical FILETIME only. #>
    return Test-AeroLinkProcessIdentity -Identity $Identity
}

function Stop-VerifiedTwinIdentity($Identity, [int]$WaitSeconds = 15) {
    <# Identity-bound termination through one verified handle; no bare-pid fallback. #>
    return Stop-AeroLinkVerifiedIdentity -Identity $Identity -WaitSeconds $WaitSeconds
}

function Get-TwinInstance {
    <#
      The running instance of THIS twin through the Task Scheduler API, or $null when none is running. The engine's
      identity is read once per engine pid and cached for THIS run only, so later cleanup can verify ownership
      instead of trusting a historical numeric pid.
    #>
    $service = New-Object -ComObject 'Schedule.Service'
    $service.Connect()
    foreach ($running in @($service.GetRunningTasks(1))) {
        if ($running -and ([string]$running.Path).TrimEnd('\') -ieq $script:twinPath.TrimEnd('\')) {
            $enginePid = [int]$running.EnginePID
            $identity = $null
            if ($enginePid -gt 0) {
                if ($script:twinEngineIdentityCache.ContainsKey($enginePid)) { $identity = $script:twinEngineIdentityCache[$enginePid] }
                else {
                    # Canonical native identity: the formatted CIM string loses fractional seconds and is never used.
                    $identity = New-TwinProcessIdentity -ProcessId $enginePid -Role 'engine' -Source 'task-engine-at-first-observation'
                    if ($identity.creationFileTime -gt 0) { $script:twinEngineIdentityCache[$enginePid] = $identity }
                }
            }
            return [pscustomobject]@{ InstanceGuid = [string]$running.InstanceGuid; EnginePid = $enginePid; EngineIdentity = $identity }
        }
    }
    return $null
}

function Get-TwinInfo([datetime]$Since) {
    <# Task result bound to THIS run: no tolerance window, and Unknown is never absence. #>
    $last = $null
    # Task Scheduler records LastRunTime with one-second resolution, so the comparison floor is the run's own
    # start truncated to the second - not a tolerance window that could accept the previous instance's result.
    $floor = [datetime]::new($Since.Year, $Since.Month, $Since.Day, $Since.Hour, $Since.Minute, $Since.Second, $Since.Kind)
    for ($i = 0; $i -lt 20; $i++) {
        try { $last = Get-ScheduledTask -TaskName $twin -ErrorAction Stop | Get-ScheduledTaskInfo }
        catch { return [pscustomobject]@{ Class = 'Unknown'; Detail = $_.Exception.Message; Info = $null } }
        if ($last.LastRunTime -and $last.LastRunTime -ge $floor) { return [pscustomobject]@{ Class = 'Valid'; Detail = ''; Info = $last } }
        Start-Sleep -Milliseconds 500
    }
    return [pscustomobject]@{ Class = 'Stale'; Detail = "the scheduler still reports LastRunTime '$($last.LastRunTime)' before this run began at $floor"; Info = $last }
}

function Wait-RunRecord([string]$Id, [int]$Seconds) {
    $path = Join-Path $probeRuns "$Id.json"
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
    <#
      Stop every probe this tool OBSERVED, by its canonical identity: the launch record's exact startedAt (kernel
      FILETIME precision, converted to the native creation FILETIME) plus the image. Termination is identity-bound
      through the native helper; a probe whose identity cannot be read is reported and the accounting fails closed.
    #>
    $unresolved = [System.Collections.Generic.List[object]]::new()
    foreach ($identity in @($script:probes)) {
        $creationFileTime = ConvertTo-AeroLinkCreationFileTime -IsoUtc ([string]$identity.StartedAtUtc)
        $candidate = [ordered]@{ processId = [int]$identity.ProcessId; creationFileTime = $creationFileTime
            createdUtc = [string]$identity.StartedAtUtc; name = ''; image = [string]$identity.ImagePath }
        $outcome = Stop-VerifiedTwinIdentity $candidate
        if ($outcome.state -eq 'Unknown') { $unresolved.Add([ordered]@{ processId = [int]$outcome.processId; state = 'Unknown'; detail = [string]$outcome.detail }) }
        elseif ($outcome.state -eq 'PidReused') { $unresolved.Add([ordered]@{ processId = [int]$outcome.processId; state = 'PidReused'; detail = [string]$outcome.detail }) }
    }
    return $unresolved.ToArray()
}

function Stop-TwinInstanceTrees {
    <#
      Stop exactly the trees this tool OWNS. Ownership is NEVER reconstructed from a historical numeric pid:
      every recorded engine is re-verified by full identity (pid + creation time) against the live inventory, and
      only then are its live descendants selected (every step of a candidate's parent chain must be a live
      process, so an orphan or a reused pid cannot be adopted). Termination is bound to the verified process
      object and re-verified while it is carried out.
      -Engines narrows the stop to one run's tree, which is what the driver does between runs: a terminated
      action can leave its outer alive (that survival is the placement property), and that outer holds the
      installation lease until its chain ends - while the NEXT run's probe must acquire it.
      Returns the unresolved entries: Running (still alive after the bounded stop) and Unknown (unreadable).
      A PidReused entry is a preserved foreign process, reported for the record and never an error.
    #>
    param($Engines = @())
    $remaining = [System.Collections.Generic.List[object]]::new()
    $targets = if (@($Engines).Count) { @($Engines) } else { @($script:instanceEngines) }
    if (-not $targets.Count) { return @($remaining) }
    $selected = [System.Collections.Generic.List[object]]::new()
    foreach ($engine in @($targets)) {
        # Accept a legacy bare pid only to report it as unverifiable; it never authorizes a termination.
        $identity = if ($engine -is [int] -or $engine -is [long]) { [ordered]@{ processId = [int]$engine; creationFileTime = 0 } } else { $engine }
        $verified = Test-TwinProcessIdentity $identity
        if ($verified.state -eq 'Gone') { continue }
        if ($verified.state -ne 'Match') {
            # A proven reused pid (Reused) is a preserved FOREIGN process, reported as PidReused so the callers do
            # not treat it as a survivor; Unknown stays a fail-closed error.
            $state = if ([string]$verified.state -eq 'Reused') { 'PidReused' } else { [string]$verified.state }
            $remaining.Add([ordered]@{ processId = [int]$identity.processId; name = 'unverified-identity'; state = $state; detail = [string]$verified.detail })
            continue
        }
        # The tree selection re-validates the root inside the very inventory it selects from (F801-1 selection
        # boundary); a replacement root is never adopted.
        try { $tree = Get-TwinTreeIdentities -EngineIdentity $identity }
        catch { $remaining.Add([ordered]@{ processId = [int]$identity.processId; name = 'query-failed'; state = 'Unknown'; detail = $_.Exception.Message }); continue }
        if ([string]$tree.state -eq 'Gone') { continue }
        # Settle what the selection PROVED it owns; then carry every discovery gap forward. A tree entry whose
        # native lifetime is unknown, or was replaced, is never silently dropped.
        $candidates = @($tree.identities)
        foreach ($left in @($tree.unresolved)) { $remaining.Add([ordered]@{ processId = [int]$left.processId; name = 'unresolved-tree-entry'; state = 'Unknown'; detail = [string]$left.detail }) }
        foreach ($left in @($tree.replaced)) { $remaining.Add([ordered]@{ processId = [int]$left.processId; name = 'replaced-tree-entry'; state = 'PidReused'; detail = [string]$left.detail }) }
        if (-not $candidates.Count -and [string]$tree.state -eq 'Reused' -and -not @($tree.replaced).Count) { $remaining.Add([ordered]@{ processId = [int]$identity.processId; name = 'unverified-identity'; state = 'PidReused'; detail = [string]$tree.detail }) }
        if (-not $candidates.Count -and [string]$tree.state -eq 'Unknown' -and -not @($tree.unresolved).Count) { $remaining.Add([ordered]@{ processId = [int]$identity.processId; name = 'unverified-identity'; state = 'Unknown'; detail = [string]$tree.detail }) }
        foreach ($candidate in $candidates) {
            if (-not @($selected | Where-Object { $_.processId -eq $candidate.processId -and $_.creationFileTime -eq $candidate.creationFileTime }).Count) { $selected.Add($candidate) }
        }
    }
    # Deepest first: a child is settled before the wrapper that started it.
    foreach ($candidate in @($selected | Sort-Object -Property @{ Expression = { [int]$_.depth } }, @{ Expression = { [int]$_.processId } } -Descending)) {
        $outcome = Stop-VerifiedTwinIdentity $candidate
        if ($outcome.state -eq 'Unknown') { $remaining.Add([ordered]@{ processId = [int]$outcome.processId; name = [string]$candidate.name; state = 'Unknown'; detail = [string]$outcome.detail }) }
        elseif ($outcome.state -eq 'Running') { $remaining.Add([ordered]@{ processId = [int]$outcome.processId; name = [string]$candidate.name; state = 'Running'; detail = '' }) }
        elseif ($outcome.state -eq 'PidReused') { $remaining.Add([ordered]@{ processId = [int]$outcome.processId; name = [string]$candidate.name; state = 'PidReused'; detail = [string]$outcome.detail }) }
    }
    return @($remaining)
}

function Test-LeaseFree {
    <# Bounded: is the installation lease acquirable right now? #>
    param([int]$Seconds = 120)
    # The EXPERIMENTS' lease lives under the probe state root, not under the installation that receives the
    # qualification record (Astra R2-3): waiting on the receiving installation's lock would report a free lease
    # while the experiment root was still held.
    $path = Join-Path (Join-Path $ProbeStateRoot 'bootstrap') 'home-transition.lock'
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        if (-not (Test-Path -LiteralPath $path)) { return $true }
        try { $stream = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None); $stream.Dispose(); return $true }
        catch { Start-Sleep -Milliseconds 500 }
    }
    return $false
}

function Get-TwinTreeIdentities {
    <#
      Canonical tree selection (AeroLinkProcessTermination.Get-AeroLinkOwnedTreeIdentities): the root is verified
      natively, and the SAME topology snapshot that selects descendants is validated - every link must be live in
      that snapshot and every parent must predate its child, so a replacement root or a stale parent pid cannot be
      adopted. Returns @{ state; identities; detail }.
    #>
    param($EngineIdentity, [int]$MaxDepth = 12)
    if (-not $EngineIdentity) { return [ordered]@{ state = 'Unknown'; identities = @(); detail = 'no engine identity' } }
    return Get-AeroLinkOwnedTreeIdentities -RootIdentity $EngineIdentity -MaxDepth $MaxDepth
}

function Add-TwinTreeIdentities {
    <#
      Snapshot the twin's LIVE tree and add only the identities this tool has not already recorded. One snapshot
      taken the instant the instance appears is not enough: the action is a wrapper (cmd.exe > powershell.exe) and
      its real entry can spawn after that snapshot, while a terminated action then orphans the child - so at cleanup
      time the parent chain no longer proves ownership. Re-snapshotting while the chain is still intact keeps every
      entry process owned by identity (Astra: a surviving child must not disappear from ownership accounting).
      Measured on the 20260919T011739Z control-flow P run: three of four twin action processes outlived cleanup
      because they were spawned after the one-time snapshot.
    #>
    param($EngineIdentity)
    $tree = Get-TwinTreeIdentities -EngineIdentity $EngineIdentity
    foreach ($identity in @($tree.identities)) {
        if (-not @($script:observedIdentities | Where-Object { $_.processId -eq $identity.processId -and $_.creationFileTime -eq $identity.creationFileTime }).Count) {
            $script:observedIdentities.Add($identity)
        }
    }
    foreach ($left in @($tree.unresolved)) {
        if (-not @($script:twinTreeUnresolved | Where-Object { [int]$_.processId -eq [int]$left.processId }).Count) { $script:twinTreeUnresolved.Add($left) }
    }
    return $tree
}

function Stop-RecordedIdentities {
    <#
      Stop exactly these recorded identities: full identity verified immediately before the termination, bound to
      the verified process object. A reused pid is a preserved foreign process (reported, never stopped); an
      unreadable identity is returned as Unknown so the caller fails closed. Returns what remains.
    #>
    param($Identities)
    $remaining = [System.Collections.Generic.List[object]]::new()
    foreach ($identity in @($Identities)) {
        if (-not $identity) { continue }
        $outcome = Stop-VerifiedTwinIdentity $identity
        if ($outcome.state -eq 'Unknown') { $remaining.Add([ordered]@{ processId = [int]$outcome.processId; name = 'unresolved-identity'; state = 'Unknown'; detail = [string]$outcome.detail }) }
        elseif ($outcome.state -eq 'PidReused') { $remaining.Add([ordered]@{ processId = [int]$outcome.processId; name = 'foreign-process'; state = 'PidReused'; detail = [string]$outcome.detail }) }
        elseif ($outcome.state -eq 'Stopped') { Write-Verbose "stopped recorded twin process $($outcome.processId) by verified identity" }
    }
    return $remaining.ToArray()
}

function Complete-TwinRun {
    <#
      Ends one run's containment: the preserved probes were already stopped by the caller, and the twin's own
      action tree is stopped here, bounded, before the next run needs the installation lease. Anything that
      survives is a cleanup error, and a cleanup error withholds the qualification record.
    #>
    param([Parameter(Mandatory)][string]$Name, $Run)
    if (-not $Run -or -not $Run.Instance) { return }
    # The engine is passed as its full identity, not its pid: a pid that has been reused since the instance was
    # observed must never authorize a termination (Astra review 801751be, F801-1).
    foreach ($left in @(Stop-TwinInstanceTrees -Engines @($Run.Instance.EngineIdentity))) {
        if ([string]$left.state -eq 'PidReused') { continue }
        $cleanupErrors.Add("${Name}: a twin action process (pid $($left.processId) $($left.name)) survived its ending")
    }
}
function Get-TwinObservationDescriptorHash($Record) {
    <# The descriptor a run actually measured: a completed record carries it at the top level, a terminating
       run's active record carries it under 'qualification' (published by the actor from the outer's handoff). #>
    $direct = [string](Get-AeroLinkProperty $Record 'descriptorHash' '')
    if ($direct) { return $direct }
    return [string](Get-AeroLinkProperty (Get-AeroLinkProperty $Record 'qualification' $null) 'descriptorHash' '')
}
function Get-TwinObservationDescriptor($Record) {
    $direct = Get-AeroLinkProperty $Record 'descriptor' $null
    if ($direct) { return $direct }
    return (Get-AeroLinkProperty (Get-AeroLinkProperty $Record 'qualification' $null) 'descriptor' $null)
}
function Add-RunEvidence([string]$Name, $Record, [string[]]$PathNames, [hashtable]$Survival, $Info) {
    $observed = [bool]($Record -and (Get-AeroLinkProperty $Record 'probe' $null))
    # EVERY run must contribute the descriptor of the context it measured - a run that never completed must
    # still show it measured the same context (Astra R2-2: one run's descriptor must not stand in for three).
    $runHash = if ($Record) { Get-TwinObservationDescriptorHash $Record } else { '' }
    $script:runDescriptors[$Name] = $runHash
    if ($runHash) {
        $script:descriptors[$runHash] = $true
        $runDescriptor = Get-TwinObservationDescriptor $Record
        if ($runDescriptor) { $script:descriptor = $runDescriptor }
    }
    foreach ($path in $PathNames) {
        $script:paths[$path] = [ordered]@{ applicable = $true; observed = $observed; survived = $(if ($observed) { [bool]$Survival[$path] } else { $null })
            setupFailure = $(if ($observed) { '' } else { "no probe was launched (record: $(if ($Record) { "$($Record.decision) $(Get-AeroLinkProperty $Record 'error' '')" } else { 'none' }))" })
            run = $Name; lastTaskResult = $(if ($Info -and $Info.Class -eq 'Valid') { $Info.Info.LastTaskResult } else { $null })
            ending = $(if ($Survival['ending']) { $Survival['ending'] } else { '' })
            recoveryProven = $(if ($Survival.ContainsKey('recovery')) { [bool]$Survival['recovery'].Proven } else { $null })
            recoveryDetail = $(if ($Survival.ContainsKey('recovery')) { [string]$Survival['recovery'].Detail } else { '' }) }
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
        [int]$LimitSeconds = 0, [int]$RecordTimeoutSeconds = 600, [int]$MutatorSeconds = 0, [int]$ChainDeadlineSeconds = 300)
    $id = "$twin-$Name"
    Set-TwinArguments $id $HoldSeconds $MutatorSeconds $ChainDeadlineSeconds
    # A fresh engine identity per run: a pid cached from an earlier run must never stand in for this run's engine.
    $script:twinEngineIdentityCache = @{}
    # A task instance whose ACTION was terminated can leave the TASK itself Running for as long as a surviving
    # child holds its job, and MultipleInstancesPolicy = IgnoreNew then makes the next Start-ScheduledTask a
    # no-op: measured in the first full qualification, where run3's instance appeared five minutes after its
    # start call, after the driver's active-mutation wait had already expired. Unregister and re-register the
    # twin for every run so each start creates a fresh instance, then require the instance to exist BEFORE
    # waiting for that run's active-mutation record.
    $previous = Get-ScheduledTask -TaskName $twin -ErrorAction SilentlyContinue
    if ($previous) {
        if ($previous.State -eq 'Running') { try { Stop-ScheduledTask -TaskName $twin -ErrorAction Stop } catch { } }
        try { Unregister-ScheduledTask -TaskName $twin -Confirm:$false -ErrorAction Stop }
        catch { $cleanupErrors.Add("the previous twin instance could not be unregistered before ${Name}: $($_.Exception.Message)") }
    }
    Register-ScheduledTask -TaskName $twin -Xml $document.OuterXml -Force -ErrorAction Stop | Out-Null
    try { if (-not (Test-LeaseFree -Seconds 300)) { $cleanupErrors.Add("${Name}: the installation lease was still held when the run was about to start") } }
    catch { $cleanupErrors.Add("${Name}: the lease check threw: $($_.Exception.Message)") }
    $since = Get-Date
    Start-ScheduledTask -TaskName $twin
    $instance = $null
    $appear = (Get-Date).AddSeconds(60)
    while (-not $instance -and (Get-Date) -lt $appear) { $instance = Get-TwinInstance; if (-not $instance) { Start-Sleep -Milliseconds 500 } }
    if ($instance) {
        # Record the engine's FULL identity. If it could not be read, record the pid with no creation time: the
        # cleanup paths then report Unknown and withhold the record instead of trusting a bare pid (fail closed).
        if ($instance.EngineIdentity) { $script:instanceEngines.Add($instance.EngineIdentity) }
        else { $script:instanceEngines.Add([ordered]@{ processId = [int]$instance.EnginePid; creationFileTime = 0L; createdUtc = ''; name = ''; image = ''; role = 'engine'; depth = 0; source = 'engine-identity-unreadable' }) }
    }
    # Record the instance's own tree by IDENTITY now, while the chain is intact: a terminated action can orphan
    # these processes, and the final cleanup must be able to stop them without a parent chain.
    if ($instance) {
        Add-TwinTreeIdentities -EngineIdentity $instance.EngineIdentity
    }
    # The synchronization point for an interrupt-during-mutation run: wait until the attempt has PUBLISHED its
    # live mutator identity, so the ending this run causes cannot land after the transition already finished.
    $active = $null
    if ($MutatorSeconds -gt 0) {
        $activePath = Join-Path $probeRuns "$id.active.json"
        $activeDeadline = (Get-Date).AddSeconds(300)
        while (-not $active -and (Get-Date) -lt $activeDeadline) {
            $read = Read-AeroLinkJsonRecord -Path $activePath
            if ($read.Class -eq 'Valid') { $active = $read.Value; if (Track-Probe $active) { } }
            Start-Sleep -Milliseconds 250
        }
    }
    $record = $null
    $stopIssued = $false
    # Current-activity tracking (Astra R2-2): the active record alone is historical. The mutator must be
    # observed RUNNING immediately before the ending this run causes, so a stale active record whose mutator is
    # long gone cannot satisfy RequireActive.
    $mutatorIdentity = if ($active) { Get-AeroLinkProperty $active 'mutator' $null } else { $null }
    $lastMutatorAliveAt = $null
    $endedAt = $null
    $deadline = $since.AddSeconds($LimitSeconds + $RecordTimeoutSeconds)
    if ($Ending -ne 'HardLimit') { $deadline = (Get-Date).AddSeconds($RecordTimeoutSeconds) }
    $cause = 'StillRunning'
    # Re-snapshot the live tree on a WALL-CLOCK cadence, not an iteration count: when the run record is absent the
    # loop spends 2 s inside Wait-RunRecord, so a count-based tick can stretch past the whole run and never fire
    # (measured on the 20260919T013442Z control-flow P run: run2's stop lands at ~8 s, count-based snapshots never
    # fired, and both run2 entry processes survived a cleanup that reported zero errors).
    $lastSnapshotAt = Get-Date
    $snapshotEverySeconds = 3
    while ((Get-Date) -lt $deadline) {
        if (-not $record) { $record = Wait-RunRecord $id 2; if ($record) { $null = Track-Probe $record } }
        $current = Get-TwinInstance
        if ($current -and -not $instance) { $instance = $current }
        if ($instance -and -not $current) {
            $endedAt = Get-Date
            $elapsed = ((Get-Date) - $since).TotalSeconds
            $cause = switch ($Ending) {
                'Completion' { 'SelfEnded' }
                'DriverStop' { if ($stopIssued) { 'DriverStopped' } else { 'EndedBeforeStop' } }
                'HardLimit' { if ($LimitSeconds -gt 0 -and $elapsed -ge ($LimitSeconds - 20)) { 'HardLimitFired' } else { 'EndedEarly' } }
            }
            break
        }
        # The stop must land while the attempt is MUTATING. The active-mutation record (published by the entry
        # itself, with the preserved probe's identity) is the synchronization point; a completed run record is
        # only the fallback for a definition that cannot hold a mutator.
        if ($current -and $Ending -eq 'DriverStop' -and -not $stopIssued -and ($active -or $record)) {
            # Snapshot the still-intact tree immediately BEFORE the stop: once the action is killed its entry is
            # orphaned and the parent chain can no longer prove ownership, so this is the last deterministic moment
            # at which the surviving entry process can be recorded by identity.
            $stopIdentity = if ($current.EngineIdentity) { $current.EngineIdentity } else { $instance.EngineIdentity }
            if ($stopIdentity) { Add-TwinTreeIdentities -EngineIdentity $stopIdentity }
            else { $cleanupErrors.Add("${Name}: the engine identity could not be read, so the pre-stop tree snapshot is unavailable and cleanup will fail closed") }
            try { Stop-ScheduledTask -TaskName $twin -ErrorAction Stop; $stopIssued = $true } catch { }
        }
        if ($current -and ((Get-Date) - $lastSnapshotAt).TotalSeconds -ge $snapshotEverySeconds) {
            # The wrapper's real entry may spawn after the first snapshot. Re-snapshot while the parent chain is
            # intact so the entry is owned before a terminated action can orphan it. Only a VERIFIED engine
            # identity can authorize the snapshot; an unreadable one is left for the fail-closed accounting.
            $snapshotIdentity = if ($current.EngineIdentity) { $current.EngineIdentity } else { $instance.EngineIdentity }
            if ($snapshotIdentity) { Add-TwinTreeIdentities -EngineIdentity $snapshotIdentity }
            $lastSnapshotAt = Get-Date
        }
        if ($mutatorIdentity) {
            $mutatorPid = [int](Get-AeroLinkProperty $mutatorIdentity 'processId' 0)
            if ($mutatorPid -gt 0 -and $K::Classify($mutatorPid, (ConvertTo-AeroLinkUtcIso (Get-AeroLinkProperty $mutatorIdentity 'startedAt' '')), [string](Get-AeroLinkProperty $mutatorIdentity 'image' '')) -eq 'RunningMatch') {
                $lastMutatorAliveAt = Get-Date
            }
        }
        Start-Sleep -Milliseconds 500
    }
    if ($cause -eq 'StillRunning' -and $Ending -ne 'HardLimit') {
        # The bounded wait expired: this ending was not observed. Stop it and record the failure honestly.
        try { Stop-ScheduledTask -TaskName $twin -ErrorAction Stop } catch { }
        $cause = 'TimedOutWaitingForEnding'
    }
    # The entry publishes its record just before it exits, so the poll above can straddle the publication and the
    # ending: the record lands inside the two-second window and the instance is already gone at the next check.
    # One bounded re-read after the ending keeps that race from dropping a whole run's descriptor - and with it the
    # launched probe whose identity this tool must own (measured: both concurrent definitions of the control-flow
    # P run 20260919T011739Z lost run1 that way and left the run's probe process running).
    if (-not $record) { $record = Wait-RunRecord $id 20; if ($record) { $null = Track-Probe $record } }
    $info = Get-TwinInfo $since
    if ($record) { $null = Track-Probe $record }
    elseif ($active) { $null = Track-Probe $active }
    $mutatorState = ''
    $mutatorAliveAfterEnding = $false
    $attemptEvidence = $null
    if ($active) {
        $mutator = Get-AeroLinkProperty $active 'mutator' $null
        $mutatorPid = [int](Get-AeroLinkProperty $mutator 'processId' 0)
        if ($mutatorPid -gt 0) {
            $mutatorState = $K::Classify($mutatorPid, (ConvertTo-AeroLinkUtcIso (Get-AeroLinkProperty $mutator 'startedAt' '')), [string](Get-AeroLinkProperty $mutator 'image' ''))
            $mutatorAliveAfterEnding = ($mutatorState -eq 'RunningMatch')
        }
        # The attempt's own completion evidence, if any: did the old attempt prove its mutator terminated?
        $attemptId = [string](Get-AeroLinkProperty $active 'attemptId' '')
        if ($attemptId) {
            $attemptRoot = Join-Path (Get-AeroLinkTransitionStateRoot -InstallationRoot $ProbeStateRoot) $attemptId
            $receipt = Test-AeroLinkCleanupReceipt -Path (Join-Path $attemptRoot 'cleanup.json') -AttemptId $attemptId
            $attemptEvidence = [ordered]@{ attemptId = $attemptId; receiptClass = $receipt.Class; containmentProven = [bool](Get-AeroLinkProperty $receipt.Receipt 'containmentProven' $false)
                observedBy = [string](Get-AeroLinkProperty $receipt.Receipt 'observedBy' ''); detail = $receipt.Reason
                quiescence = (Get-AeroLinkTransitionQuiescence -AttemptRoot $attemptRoot -AttemptId $attemptId).State }
        }
    }
    return [pscustomobject]@{ Run = $Name; RunId = $id; Instance = $instance; Record = $record; Info = $info; Cause = $cause; Active = $active
        MutatorState = $mutatorState; AttemptEvidence = $attemptEvidence
        ElapsedSeconds = [int]((Get-Date) - $since).TotalSeconds; Since = $since; StopIssued = $stopIssued
        EndedAt = $endedAt; LastMutatorAliveAt = $lastMutatorAliveAt; MutatorAliveAfterEnding = $mutatorAliveAfterEnding }
}

function Test-EndingMatched($Run, [string]$Expected, [bool]$RequireActive = $false) {
    <# { Matched, Detail }: the observed ending, the instance it belongs to, and the scheduler result. #>
    if (-not $Run.Instance) { return [pscustomobject]@{ Matched = $false; Detail = 'the task instance was never observed running' } }
    if ($Run.Cause -ne $Expected) { return [pscustomobject]@{ Matched = $false; Detail = "the run ended as $($Run.Cause), not $Expected" } }
    if ($RequireActive) {
        if (-not $Run.Active) { return [pscustomobject]@{ Matched = $false; Detail = 'the ending did not land on an active mutator: no active-mutation record was published' } }
        # A historical active record is not evidence. The mutator it names must have been observed RUNNING
        # immediately before this ending, or still running at the first observation after it - the latter is the
        # stronger, race-free proof and the only one available when a task stop lands within the poll interval
        # (Astra R2-2: a stale Active plus MutatorState=Gone must still fail).
        if (-not $Run.LastMutatorAliveAt -and -not $Run.MutatorAliveAfterEnding) { return [pscustomobject]@{ Matched = $false; Detail = 'the active-mutation record is stale: its mutator was never observed running while this instance ran' } }
        if ($Run.LastMutatorAliveAt) {
            $gap = ($Run.EndedAt - $Run.LastMutatorAliveAt).TotalSeconds
            if ($gap -gt 5) { return [pscustomobject]@{ Matched = $false; Detail = "the mutator was last observed running $([int]$gap)s before the ending, so the ending did not land on active mutation" } }
        }
        $activeAttestation = Get-AeroLinkProperty (Get-AeroLinkProperty $Run.Active 'qualification' $null) 'attestation' $null
        $activeInstance = [string](Get-AeroLinkProperty $activeAttestation 'instance' '')
        if ($activeInstance -and $activeInstance -ne $Run.Instance.InstanceGuid) { return [pscustomobject]@{ Matched = $false; Detail = "the active record belongs to instance $activeInstance, not $($Run.Instance.InstanceGuid)" } }
    }
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

function Get-RunRecoveryEvidence($Run) {
    <#
      { Proven, Detail }: whether THIS ending left the old attempt provably terminated. Survivor evidence alone
      never proves recovery. The transient mutator must no longer be running, and the attempt itself must have
      published a containment receipt whose quiescence is proven - which is exactly what a context whose witness
      dies with its task cannot produce. Both facts are recorded, and a record that lacks them is written as a
      placement-only qualification rather than a full one.
    #>
    if (-not $Run.Active) { return [pscustomobject]@{ Proven = $false; Detail = 'no active-mutation record was published, so no old mutator was observed' } }
    $mutator = Get-AeroLinkProperty $Run.Active 'mutator' $null
    $mutatorPid = [int](Get-AeroLinkProperty $mutator 'processId' 0)
    if ([string]$Run.MutatorState -eq 'RunningMatch') { return [pscustomobject]@{ Proven = $false; Detail = "the transient mutator pid $mutatorPid was still running after the ending" } }
    if ([string]$Run.MutatorState -notin @('Gone', 'RunningDifferent')) { return [pscustomobject]@{ Proven = $false; Detail = "the transient mutator pid $mutatorPid could not be classified ($($Run.MutatorState))" } }
    $evidence = $Run.AttemptEvidence
    if (-not $evidence) { return [pscustomobject]@{ Proven = $false; Detail = 'the attempt published no completion evidence' } }
    if ([string]$evidence.receiptClass -ne 'Valid') { return [pscustomobject]@{ Proven = $false; Detail = "the attempt's cleanup receipt is $([string]$evidence.receiptClass.ToLower()): $($evidence.detail)" } }
    if (-not [bool]$evidence.containmentProven) { return [pscustomobject]@{ Proven = $false; Detail = 'the attempt did not prove containment' } }
    if ([string]$evidence.quiescence -ne 'Quiescent') { return [pscustomobject]@{ Proven = $false; Detail = "the attempt's quiescence is $($evidence.quiescence)" } }
    return [pscustomobject]@{ Proven = $true; Detail = "the attempt proved containment and its transient mutator pid $mutatorPid is no longer running" }
}

$exitCode = 1
$written = $null
$recoveryProven = $false
$recoveryDetail = 'the terminating paths were not observed'
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
    try { Complete-TwinRun -Name 'run1' -Run $run1 } catch { $cleanupErrors.Add("run1 tree cleanup threw: $($_.Exception.Message)") }

    # ---- run 2: explicit stop while the entry is still running ----
    # The chain's deadline must outlast the stop the driver is about to issue, and it must also END soon after:
    # the surviving outer holds the installation lease until its chain finishes, and the next run cannot start
    # until that lease is released. 180 s is bounded, well past the driver's synchronization point, and short
    # enough that the run's attempt resolves (witness receipt) before run3.
    $run2 = Invoke-TwinRun -Name 'run2' -HoldSeconds 3600 -Ending DriverStop -RecordTimeoutSeconds $EndingTimeoutSeconds -MutatorSeconds 3600 -ChainDeadlineSeconds 180
    $ending2 = Test-EndingMatched $run2 'DriverStopped' -RequireActive $true
    $recovery2 = Get-RunRecoveryEvidence $run2
    # An interruption during active mutation leaves no outcome record here: the preserved probe's identity comes
    # from the active-mutation record, and its survival is what the taskStop path claims.
    $observation2 = if ($run2.Record) { $run2.Record } else { $run2.Active }
    $alive2 = Test-ProbeSurvived $observation2
    Add-RunEvidence 'run2' $observation2 @('taskStop') @{ taskStop = ($alive2 -and $ending2.Matched); ending = $ending2.Detail; active = $run2.Active
        mutatorState = $run2.MutatorState; attempt = $run2.AttemptEvidence; recovery = $recovery2 } $run2.Info
    Stop-TrackedProbes
    try { Complete-TwinRun -Name 'run2' -Run $run2 } catch { $cleanupErrors.Add("run2 tree cleanup threw: $($_.Exception.Message)") }

    # ---- run 3: the definition's own hard time limit ----
    # The mutator must still be working when the task's own limit fires, so the chain's budget covers the limit
    # plus the mutator's margin. Production deadlines are untouched: this is the qualification's own parameter.
    $run3 = Invoke-TwinRun -Name 'run3' -HoldSeconds ([int]$limit.TotalSeconds + 600) -Ending HardLimit -LimitSeconds ([int]$limit.TotalSeconds) -RecordTimeoutSeconds $EndingTimeoutSeconds -MutatorSeconds ([int]$limit.TotalSeconds + 600) -ChainDeadlineSeconds ([int]$limit.TotalSeconds + 1200)
    $ending3 = Test-EndingMatched $run3 'HardLimitFired' -RequireActive $true
    $recovery3 = Get-RunRecoveryEvidence $run3
    $observation3 = if ($run3.Record) { $run3.Record } else { $run3.Active }
    $alive3 = Test-ProbeSurvived $observation3
    Add-RunEvidence 'run3' $observation3 @('hardTimeout') @{ hardTimeout = ($alive3 -and $ending3.Matched); ending = $ending3.Detail; active = $run3.Active
        mutatorState = $run3.MutatorState; attempt = $run3.AttemptEvidence; recovery = $recovery3 } $run3.Info
    Stop-TrackedProbes
    try { Complete-TwinRun -Name 'run3' -Run $run3 } catch { $cleanupErrors.Add("run3 tree cleanup threw: $($_.Exception.Message)") }

    # Placement and recovery are separate gates: both terminating paths must have proven the old attempt's
    # termination before this record may be written as a full qualification.
    $recoveryProven = [bool]($recovery2.Proven -and $recovery3.Proven)
    $recoveryDetail = "taskStop: $($recovery2.Detail); hardTimeout: $($recovery3.Detail)"
    $summary['recoveryProven'] = $recoveryProven
    $summary['recoveryDetail'] = $recoveryDetail

    $missingDescriptorRuns = @($runDescriptors.Keys | Where-Object { -not $runDescriptors[$_] })
    $summary['runDescriptorHashes'] = $runDescriptors
    if ($descriptors.Count -ne 1 -or -not $descriptor -or $missingDescriptorRuns.Count -or $runDescriptors.Count -ne 3) {
        $summary['verdict'] = 'Unqualifiable'
        $summary['detail'] = if ($missingDescriptorRuns.Count -or $runDescriptors.Count -ne 3) {
            "these probe runs published no descriptor of the context they measured, so three identical contexts cannot be claimed: $(@($missingDescriptorRuns | Sort-Object) -join ', '); nothing was written"
        } else { "the probe runs reported $($descriptors.Count) distinct descriptors; nothing was written" }
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
    # The twin's own action trees are this tool's disposable processes; a probe entry that published its record
    # sleeps on as a live action process. Stop exactly those trees and fail the qualification if any survives.
    try {
        foreach ($left in @(Stop-TwinInstanceTrees)) {
            # PidReused is a preserved foreign process, not a survivor of ours.
            if ([string]$left.state -eq 'PidReused') { continue }
            $cleanupErrors.Add("a twin action process (pid $($left.processId) $($left.name)) survived the twin's endings ($($left.state))")
        }
    }
    catch { $cleanupErrors.Add("the twin tree cleanup threw: $($_.Exception.Message)") }
    # The recorded identities are what this tool owns even after the parent chain is gone.
    try {
        foreach ($left in @(Stop-RecordedIdentities -Identities $script:observedIdentities)) {
            if ([string]$left.state -eq 'PidReused') { continue }
            $cleanupErrors.Add("a recorded twin process (pid $($left.processId) $($left.name)) survived cleanup ($($left.state))")
        }
    }
    catch { $cleanupErrors.Add("the recorded-identity cleanup threw: $($_.Exception.Message)") }
    # A tree entry discovered during the run that could not be attributed to the twin is NOT a proof of absence, and
    # it was deliberately never terminated. It is re-checked here BY IDENTITY: a positively gone pid, or a pid that
    # now belongs to another lifetime, clears it; the same process still running, or an identity that still cannot
    # be read, withholds the qualification (Astra review 7e878834: unresolved discovery must reach the verdict).
    foreach ($entry in @($script:twinTreeUnresolved)) {
        $processId = [int]$entry.processId
        $recordedFileTime = [long]0
        if ($entry.PSObject.Properties['creationFileTime']) { $recordedFileTime = [long]$entry.creationFileTime }
        $read = $null
        try { $read = Get-AeroLinkProcessCreationFileTime -ProcessId $processId } catch { $read = $null }
        if ($read -and $read.state -eq 'Gone') { continue }
        if ($read -and $read.state -eq 'Ok' -and $recordedFileTime -gt 0 -and [long]$read.creationFileTime -ne $recordedFileTime) { continue }
        $state = if ($read) { [string]$read.state } else { 'Unknown' }
        $detail = if ($read) { [string]$read.detail } else { 'the identity could not be re-read' }
        $cleanupErrors.Add("a tree entry observed during the run (pid $processId) was never proven gone ($state): $($entry.detail) / $detail")
    }
    $summary['treeUnresolved'] = @($script:twinTreeUnresolved)
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
                -RequiredPaths @('transientJob', 'wrapperExit', 'taskCompletion', 'taskStop', 'hardTimeout') -Detail "probe survived every applicable path of the twin of $($summary.sourceTask)" `
                -RecoveryProven:$recoveryProven -RecoveryDetail $recoveryDetail
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
    # A written record is a completed qualification, whatever the recovery gate found: Qualified means both
    # placement and recovery were proven, QualifiedPlacementOnly means the record is usable at admission (which
    # enforces quiescence itself) and names the recovery gap. Anything else (no record, cleanup failure,
    # unobserved path) exits nonzero.
    if ($summary['verdict'] -in @('Qualified', 'QualifiedPlacementOnly')) { $exitCode = 0 }
    $summary['finishedAt'] = (Get-Date).ToUniversalTime().ToString('o')
    Publish-AeroLinkJsonAtomic -Path $summaryPath -Value $summary
    Write-Host "Launch-context qualification of $($summary.sourceTask): $($summary.verdict). $($summary['detail'])"
    Write-Host "Evidence: $summaryPath"
}
exit $exitCode
