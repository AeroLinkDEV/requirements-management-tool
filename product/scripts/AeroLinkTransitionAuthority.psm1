#Requires -Version 5.1
<#
    ONE OUTER AUTHORITY PER HOME TRANSITION (#1041, #1043, #1053).

    A HOME transition stops services, advances the production source, runs a clone-validated upgrade and restores
    what was running. Before this module the work was carried by a chain of child processes that nothing contained:
    a continuation could be "timed out" while its descendants kept migrating, recovery could start over them, and a
    restored API held its caller's output pipe open so the caller never returned (#1053).

    The shape now, for every caller (remote-demo Start/Reconcile, Configure Update and its delegation, first
    deployment):

      outer (the caller's own process, product lease OWNER)
        1. qualifies its OWN launch context before anything is touched;
        2. admits the attempt: every prior attempt of this installation is proven quiescent and every prior launch
           request resolved (a lease alone is not enough: a lease descendant's unleased grandchild outlives it);
        3. records the attempt in the durable index, starts a completion WITNESS and creates ONE armed transition
           job (kill-on-close, query-only DACL);
        4. publishes a versioned HANDOFF and creates the delegate actor suspended INSIDE the job, retaining its
           handle;
        5. pumps the LAUNCH AUTHORITY - the only way any descendant obtains a process that outlives the job - until
           the job is empty, the delegate fails, or the deadline;
        6. terminates the job and observes ZERO members through its held handle, publishes that receipt, resolves
           every launch request, reconciles the delegate's actual exit status against its outcome, verifies every
           REQUIRED role itself, and publishes three separate verdicts (operation, cleanup, recovery);
      the caller then releases the lease LAST.

    If the outer dies instead, the witness collects the job and publishes the receipt, and the next admission
    decides from durable records.
#>
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'AeroLinkTransitionKernel.psm1') -DisableNameChecking
Import-Module (Join-Path $PSScriptRoot 'AeroLinkRuntimeIdentity.psm1') -DisableNameChecking
Import-Module (Join-Path $PSScriptRoot 'AeroLinkProcessControl.psm1') -DisableNameChecking

$script:HandoffProtocolVersion = 1
$script:SupportedHandoffProtocols = @(1)
$script:QualifierVersion = 'aerolink-qualifier-1'
$script:RefusalDecisions = @('ProtocolIncompatible', 'SourceVersionMismatch', 'Unreadable', 'LeaseRejected', 'NotADescendant')

# ==================================================================================================================
# Layout
# ==================================================================================================================

function Get-AeroLinkTransitionStateRoot {
    param([Parameter(Mandatory)][string]$InstallationRoot)
    return (Join-Path ([IO.Path]::GetFullPath($InstallationRoot).TrimEnd('\')) 'bootstrap\transitions')
}

function Get-AeroLinkAttemptIndexPath {
    param([Parameter(Mandatory)][string]$InstallationRoot)
    return (Join-Path (Get-AeroLinkTransitionStateRoot -InstallationRoot $InstallationRoot) 'attempts.jsonl')
}

function New-AeroLinkAttemptId {
    return ((Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
}

function Get-AeroLinkAttemptPaths {
    param([Parameter(Mandatory)][string]$AttemptRoot)
    return [pscustomobject]@{
        Root = $AttemptRoot
        Attempt = (Join-Path $AttemptRoot 'attempt.json')
        Handoff = (Join-Path $AttemptRoot 'handoff.json')
        JobEvents = (Join-Path $AttemptRoot 'transition-job.jsonl')
        Receipt = (Join-Path $AttemptRoot 'cleanup.json')
        Witness = (Join-Path $AttemptRoot 'witness')
        Spool = (Join-Path $AttemptRoot 'spool')
        Outcome = (Join-Path $AttemptRoot 'outcome.json')
        Chain = (Join-Path $AttemptRoot 'chain.jsonl')
        DelegateAccepted = (Join-Path $AttemptRoot 'delegate-accepted.json')
        DelegateOutcome = (Join-Path $AttemptRoot 'delegate-outcome.json')
        ContinuationOutcome = (Join-Path $AttemptRoot 'continuation-outcome.json')
        Logs = (Join-Path $AttemptRoot 'logs')
    }
}

function Get-AeroLinkUtcNow { return (Get-Date).ToUniversalTime().ToString('o') }

# ==================================================================================================================
# Witness
# ==================================================================================================================

function Start-AeroLinkTransitionWitness {
    param([Parameter(Mandatory)][string]$Dir, [int]$ReadyTimeoutSeconds = 60, [switch]$Breakaway, [string]$FaultInjection = '')
    New-Item -ItemType Directory -Path $Dir -Force | Out-Null
    $K = [AeroLink.TransitionV1.Kernel]
    $me = Get-AeroLinkProcessIdentityRecord -ProcessId $PID
    if (-not $me) { throw "WitnessUnavailable: this owner's own identity could not be read, so it cannot be monitored." }
    $powershell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $spec = New-Object AeroLink.TransitionV1.LaunchSpec
    $spec.CommandLine = '"' + $powershell + '" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + (Join-Path $PSScriptRoot 'AeroLinkTransitionWitness.ps1') +
        '" -Dir "' + $Dir + '" -OwnerPid ' + $PID + ' -OwnerStartedAt ' + $me.StartedAtUtc + ' -OwnerImage "' + $me.ImagePath + '"'
    if ($FaultInjection) { $spec.CommandLine += ' -FaultInjection ' + $FaultInjection }
    $spec.StandardOutputPath = Join-Path $Dir 'witness.stdout.log'
    $spec.StandardErrorPath = Join-Path $Dir 'witness.stderr.log'
    # Outside every job this owner creates. In a qualified context it breaks away like a preserved service, so a
    # task stop that kills its owner does not kill it too: a double failure becomes a single, resolvable one.
    $spec.Breakaway = [bool]$Breakaway
    $staged = $K::Launch([IntPtr]::Zero, $spec)
    Publish-AeroLinkJsonAtomic -Path (Join-Path $Dir 'witness.json') -Value ([ordered]@{ pid = $staged.ProcessId; startedAt = $staged.StartedAtUtc; ownerPid = $PID })
    $K::Resume($staged)
    $readyPath = Join-Path $Dir 'ready.json'; $failedPath = Join-Path $Dir 'startup-failed.json'
    $deadline = (Get-Date).AddSeconds($ReadyTimeoutSeconds)
    while (-not (Test-Path -LiteralPath $readyPath) -and -not (Test-Path -LiteralPath $failedPath) -and (Get-Date) -lt $deadline) {
        if ($K::HasExited($staged)) { break }
        Start-Sleep -Milliseconds 50
    }
    $ready = Read-AeroLinkJsonRecord -Path $readyPath
    $ok = $ready.Class -eq 'Valid' -and (Get-AeroLinkProperty $ready.Value 'ownerMonitored' $false) -eq $true -and
        [int](Get-AeroLinkProperty $ready.Value 'pid' 0) -eq $staged.ProcessId -and [int](Get-AeroLinkProperty $ready.Value 'ownerPid' 0) -eq $PID -and
        (ConvertTo-AeroLinkUtcIso (Get-AeroLinkProperty $ready.Value 'ownerStartedAt' '')) -eq $me.StartedAtUtc
    if (-not $ok) {
        $why = if (Test-Path -LiteralPath $failedPath) { [string](Read-AeroLinkJsonRecord -Path $failedPath).Value.reason } else { "ready record $($ready.Class.ToLower()) or not bound to this owner" }
        [void]$K::TerminateChecked($staged, 5000); $K::Close($staged)
        throw "WitnessUnavailable: the completion witness could not establish owner monitoring ($why)."
    }
    return [pscustomobject]@{ Dir = $Dir; Staged = $staged; ProcessId = $staged.ProcessId }
}

function Register-AeroLinkWitnessJob {
    <#
      Arms the witness for a job BEFORE anything is created in it: duplicate -> journal -> wait for the ack. If the
      owner dies after duplicating but before journaling, the witness holds an unknown handle to an EMPTY job.
    #>
    param([Parameter(Mandatory)]$Witness, [Parameter(Mandatory)][IntPtr]$Job, [Parameter(Mandatory)][string]$JobName,
        [Parameter(Mandatory)][ValidateSet('staging', 'transition')][string]$Kind, [Parameter(Mandatory)][string]$ReceiptPath,
        [string]$AttemptId = '', [int]$AckTimeoutSeconds = 20)
    $K = [AeroLink.TransitionV1.Kernel]
    if ((Test-AeroLinkLockHolderAlive -Path (Join-Path $Witness.Dir 'witness.lock')) -ne 'Alive') { throw 'WitnessUnavailable: the completion witness is not alive.' }
    $remote = $K::DuplicateInto($Job, $Witness.Staged.Process)
    Write-AeroLinkTransitionEvent -Path (Join-Path $Witness.Dir 'jobs.jsonl') -Record ([ordered]@{ jobName = $JobName; handle = $remote; kind = $Kind; receiptPath = $ReceiptPath; attemptId = $AttemptId })
    $deadline = (Get-Date).AddSeconds($AckTimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $acks = Read-AeroLinkTransitionEvents -Path (Join-Path $Witness.Dir 'acks.jsonl')
        if (@($acks.Events | Where-Object { [string]$_.jobName -eq $JobName }).Count -gt 0) { return }
        if ($K::HasExited($Witness.Staged)) { break }
        Start-Sleep -Milliseconds 30
    }
    throw "WitnessUnavailable: the witness did not acknowledge job '$JobName'."
}

function Stop-AeroLinkTransitionWitness {
    param([Parameter(Mandatory)]$Witness)
    $K = [AeroLink.TransitionV1.Kernel]
    Publish-AeroLinkJsonAtomic -Path (Join-Path $Witness.Dir 'release.json') -Value ([ordered]@{ at = (Get-AeroLinkUtcNow) })
    if ($K::WaitHandle($Witness.Staged.Process, 20000) -ne 0) { [void]$K::TerminateChecked($Witness.Staged, 5000) }
    $K::Close($Witness.Staged)
}

function Get-AeroLinkWitnessState {
    <# Alive | Dead | Unknown | Absent #>
    param([Parameter(Mandatory)][string]$Dir)
    if (-not (Test-Path -LiteralPath (Join-Path $Dir 'witness.json'))) { return 'Absent' }
    return (Test-AeroLinkLockHolderAlive -Path (Join-Path $Dir 'witness.lock'))
}

# ==================================================================================================================
# Transition job and quiescence
# ==================================================================================================================

function New-AeroLinkTransitionJob {
    param([Parameter(Mandatory)][string]$AttemptRoot, [Parameter(Mandatory)][string]$AttemptId, [Parameter(Mandatory)]$Witness)
    $K = [AeroLink.TransitionV1.Kernel]
    $paths = Get-AeroLinkAttemptPaths $AttemptRoot
    $jobName = 'Global\AeroLinkTransition-' + $AttemptId
    # The NAME is durable before the object exists, so a later reader can always look for it.
    Write-AeroLinkTransitionEvent -Path $paths.JobEvents -Record ([ordered]@{ type = 'Intended'; jobName = $jobName; attemptId = $AttemptId; at = (Get-AeroLinkUtcNow) })
    try { $job = $K::CreateJob($jobName, $K::LimitKillOnClose, $K::QueryOnlySddl()) }
    catch {
        $collision = $_.Exception.Message -match 'JobNameCollision'
        Write-AeroLinkTransitionEvent -Path $paths.JobEvents -Record ([ordered]@{ type = $(if ($collision) { 'Collision' } else { 'CreateFailed' }); jobName = $jobName; detail = $_.Exception.Message; at = (Get-AeroLinkUtcNow) })
        throw
    }
    Write-AeroLinkTransitionEvent -Path $paths.JobEvents -Record ([ordered]@{ type = 'Created'; jobName = $jobName; at = (Get-AeroLinkUtcNow) })
    Register-AeroLinkWitnessJob -Witness $Witness -Job $job -JobName $jobName -Kind transition -ReceiptPath $paths.Receipt -AttemptId $AttemptId
    # Only after this may anything be created in the job.
    Write-AeroLinkTransitionEvent -Path $paths.JobEvents -Record ([ordered]@{ type = 'Armed'; jobName = $jobName; witnessDir = $Witness.Dir; at = (Get-AeroLinkUtcNow) })
    return [pscustomobject]@{ Handle = $job; Name = $jobName }
}

function Complete-AeroLinkTransitionJob {
    <# Normal completion: terminate, observe zero through the held handle, publish, THEN close. #>
    param([Parameter(Mandatory)]$Job, [Parameter(Mandatory)][string]$AttemptRoot, [Parameter(Mandatory)][string]$AttemptId, [int]$TimeoutSeconds = 30)
    $K = [AeroLink.TransitionV1.Kernel]
    $before = $K::Members($Job.Handle)
    $discovered = @($before.ProcessIds | ForEach-Object { Get-AeroLinkIdentityEntry $_ })
    $K::Terminate($Job.Handle)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $after = $K::Members($Job.Handle)
    while ($after.Assigned -ne 0 -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 50; $after = $K::Members($Job.Handle) }
    Publish-AeroLinkJsonAtomic -Path (Get-AeroLinkAttemptPaths $AttemptRoot).Receipt -Value ([ordered]@{ schemaVersion = 1; attemptId = $AttemptId; jobName = $Job.Name
            observedBy = 'outer'; containmentProven = ($after.Assigned -eq 0); activeProcessesAfterTerminate = [int]$after.Assigned
            discoveredBeforeTermination = $discovered; at = (Get-AeroLinkUtcNow) })
    $K::CloseHandleChecked($Job.Handle)
    return ($after.Assigned -eq 0)
}

function Get-AeroLinkIdentityEntry([int]$ProcessId) {
    $identity = Get-AeroLinkProcessIdentityRecord -ProcessId $ProcessId
    if ($identity) { return [ordered]@{ processId = $ProcessId; startedAt = $identity.StartedAtUtc; image = $identity.ImagePath } }
    return [ordered]@{ processId = $ProcessId; startedAt = ''; image = '' }
}

function Test-AeroLinkCleanupReceipt {
    <# Absent | Malformed | Mismatched | Unproven | Valid, bound to the attempt AND the job name. #>
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$AttemptId, [string]$JobName = '')
    $read = Read-AeroLinkJsonRecord -Path $Path
    if ($read.Class -eq 'Absent') { return [pscustomobject]@{ Class = 'Absent'; Valid = $false; Reason = 'no cleanup receipt was published'; Receipt = $null } }
    if ($read.Class -ne 'Valid') { return [pscustomobject]@{ Class = 'Malformed'; Valid = $false; Reason = "the cleanup receipt is $($read.Class.ToLower())"; Receipt = $null } }
    $r = $read.Value
    foreach ($field in @('schemaVersion', 'attemptId', 'containmentProven', 'activeProcessesAfterTerminate')) {
        if ($null -eq $r.PSObject.Properties[$field]) { return [pscustomobject]@{ Class = 'Malformed'; Valid = $false; Reason = "the cleanup receipt is missing '$field'"; Receipt = $null } }
    }
    if (-not (Test-AeroLinkIntegral $r.schemaVersion) -or [int64]$r.schemaVersion -ne 1) { return [pscustomobject]@{ Class = 'Malformed'; Valid = $false; Reason = 'unsupported schemaVersion'; Receipt = $null } }
    if ([string]$r.attemptId -ne $AttemptId) { return [pscustomobject]@{ Class = 'Mismatched'; Valid = $false; Reason = "the receipt is bound to attempt '$($r.attemptId)'"; Receipt = $null } }
    if ($JobName -and [string](Get-AeroLinkProperty $r 'jobName' '') -ne $JobName) { return [pscustomobject]@{ Class = 'Mismatched'; Valid = $false; Reason = "the receipt is for job '$(Get-AeroLinkProperty $r 'jobName' '')', not '$JobName'"; Receipt = $null } }
    if ($r.containmentProven -isnot [bool]) { return [pscustomobject]@{ Class = 'Malformed'; Valid = $false; Reason = 'containmentProven is not a boolean'; Receipt = $null } }
    if (-not (Test-AeroLinkIntegral $r.activeProcessesAfterTerminate)) { return [pscustomobject]@{ Class = 'Malformed'; Valid = $false; Reason = 'activeProcessesAfterTerminate is not an integer'; Receipt = $null } }
    if (-not $r.containmentProven -or $r.activeProcessesAfterTerminate -ne 0) { return [pscustomobject]@{ Class = 'Unproven'; Valid = $false; Reason = "the receipt states containment was NOT proven (activeProcessesAfterTerminate=$($r.activeProcessesAfterTerminate))"; Receipt = $r } }
    return [pscustomobject]@{ Class = 'Valid'; Valid = $true; Reason = "completion observed by $(Get-AeroLinkProperty $r 'observedBy' 'unknown')"; Receipt = $r }
}

function Get-AeroLinkTransitionQuiescence {
    <#
      After owner death:
        receipt Valid                          -> Quiescent
        receipt Unproven / Mismatched          -> refuse
        no valid receipt, witness alive        -> Pending
        name Present (someone holds a handle)  -> observe through our own query handle: 0 -> Quiescent, >0 -> NotQuiescent
                                                  (with -Recover: terminate each kernel-verified member, observe zero)
        name Unknown                           -> Unknown
        name Absent, job was armed             -> TerminationUnconfirmed, unless the machine restarted since creation
        never armed / never created / collided -> Quiescent: members are only created after arming
    #>
    param([Parameter(Mandatory)][string]$AttemptRoot, [Parameter(Mandatory)][string]$AttemptId, [switch]$Recover, [int]$RecoverTimeoutSeconds = 30)
    $K = [AeroLink.TransitionV1.Kernel]
    $paths = Get-AeroLinkAttemptPaths $AttemptRoot
    $out = { param($State, $Detail, $Discovered = @()) [pscustomobject]@{ State = $State; Detail = $Detail; Discovered = @($Discovered) } }

    $journal = Read-AeroLinkTransitionEvents -Path $paths.JobEvents
    if ($journal.Class -eq 'Absent') { return & $out 'Unknown' 'no transition-job record exists; the absence of a record is not the absence of work' }
    if ($journal.Class -ne 'Valid') { return & $out 'Unknown' "the transition-job record is $($journal.Class.ToLower())" }
    $by = @{}; foreach ($entry in $journal.Events) { $by[[string]$entry.type] = $entry }
    if (-not $by.ContainsKey('Intended')) { return & $out 'Unknown' 'the transition-job record names no job' }
    $name = [string]$by['Intended'].jobName
    if ($by.ContainsKey('Collision') -or $by.ContainsKey('CreateFailed')) { return & $out 'Quiescent' "job '$name' was never created by this attempt; nothing was started in it" }
    $witnessState = 'Absent'
    if ($by.ContainsKey('Armed')) {
        $receipt = Test-AeroLinkCleanupReceipt -Path $paths.Receipt -AttemptId $AttemptId -JobName $name
        if ($receipt.Valid) {
            $probe = $K::Probe($name)
            if ($probe.State -eq 'Present') {
                $h = [IntPtr]::Zero
                try { $h = $K::OpenJobQuery($name); if ($K::Members($h).Assigned -gt 0) { return & $out 'Unknown' 'a valid receipt exists but the job has live members again' } } catch { } finally { $K::CloseHandleChecked($h) }
            }
            return & $out 'Quiescent' $receipt.Reason @(Get-AeroLinkProperty $receipt.Receipt 'discoveredBeforeTermination' @())
        }
        if ($receipt.Class -eq 'Unproven') { return & $out 'Unproven' $receipt.Reason }
        if ($receipt.Class -eq 'Mismatched') { return & $out 'Mismatched' $receipt.Reason }
        $witnessState = Get-AeroLinkWitnessState -Dir ([string]$by['Armed'].witnessDir)
        if ($witnessState -eq 'Alive') { return & $out 'Pending' 'no completion receipt yet and the witness is still alive' }
        if ($witnessState -eq 'Unknown') { return & $out 'Unknown' 'whether the witness is still acting could not be established' }
    }
    elseif ($by.ContainsKey('Created')) {
        $probe = $K::Probe($name)
        if ($probe.State -eq 'Absent') { return & $out 'Quiescent' "job '$name' was created but never armed, and no handle to it remains; no member was ever created" }
        return & $out 'Unknown' "job '$name' was created but never armed and still probes $probe"
    }
    else {
        $probe = $K::Probe($name)
        if ($probe.State -eq 'Absent') { return & $out 'Quiescent' "job '$name' was only intended; it does not exist and nothing was created in it" }
        return & $out 'Unknown' "job '$name' was only intended but probes $probe"
    }

    $probe = $K::Probe($name)
    if ($probe.State -eq 'Unknown') { return & $out 'Unknown' "job '$name' exists or may exist but cannot be observed ($probe); unknown is not absence" }
    if ($probe.State -eq 'Absent') {
        $boot = Get-AeroLinkBootTimeUtc
        $createdAt = ConvertTo-AeroLinkUtcDate (Get-AeroLinkProperty $by['Created'] 'at')
        if ($boot -and $createdAt -and $boot -gt $createdAt) { return & $out 'Quiescent' 'no receipt, but the system restarted after the job was created, so none of its processes survive' }
        return & $out 'TerminationUnconfirmed' "job '$name' has no handle left, so kill-on-close was initiated, but nobody holding a handle observed it complete (no receipt; witness $witnessState)"
    }
    $h = [IntPtr]::Zero
    try {
        $h = $K::OpenJobQuery($name)
        $members = $K::Members($h)
        if ($members.Assigned -eq 0) {
            Publish-AeroLinkJsonAtomic -Path $paths.Receipt -Value ([ordered]@{ schemaVersion = 1; attemptId = $AttemptId; jobName = $name; observedBy = 'reconciliation'
                    containmentProven = $true; activeProcessesAfterTerminate = 0; discoveredBeforeTermination = @(); at = (Get-AeroLinkUtcNow) })
            return & $out 'Quiescent' "job '$name' is still held open but has zero members, observed through a held handle with no full-handle holder alive"
        }
        $discovered = @($members.ProcessIds | ForEach-Object { Get-AeroLinkIdentityEntry $_ })
        if (-not $Recover) { return & $out 'NotQuiescent' "job '$name' still has $($members.Assigned) live member(s)" $discovered }
        # A query handle cannot terminate; recovery needs the members' own handles, verified in the job by the kernel.
        $all = [System.Collections.Generic.List[object]]::new(); foreach ($d in $discovered) { $all.Add($d) }
        $deadline = (Get-Date).AddSeconds($RecoverTimeoutSeconds)
        while ($members.Assigned -gt 0 -and (Get-Date) -lt $deadline) {
            foreach ($id in $members.ProcessIds) {
                $result = $K::TerminateVerifiedMember($h, $id)
                if ($result -like 'Unknown:*') { return & $out 'Unknown' "member pid $id could not be terminated or verified ($result)" @($all) }
            }
            $members = $K::Members($h)
        }
        if ($members.Assigned -ne 0) { return & $out 'NotQuiescent' "recovery could not empty job '$name' within ${RecoverTimeoutSeconds}s" @($all) }
        Publish-AeroLinkJsonAtomic -Path $paths.Receipt -Value ([ordered]@{ schemaVersion = 1; attemptId = $AttemptId; jobName = $name; observedBy = 'recovery'
                containmentProven = $true; activeProcessesAfterTerminate = 0; discoveredBeforeTermination = @($all); at = (Get-AeroLinkUtcNow) })
        return & $out 'Quiescent' "recovery terminated every discovered member of '$name' and observed zero through its held handle" @($all)
    }
    catch { return & $out 'Unknown' "job '$name' could not be observed: $($_.Exception.Message)" }
    finally { $K::CloseHandleChecked($h) }
}

# ==================================================================================================================
# Admission
# ==================================================================================================================

function Test-AeroLinkAttemptResolved {
    param([Parameter(Mandatory)][string]$AttemptRoot, [Parameter(Mandatory)][string]$AttemptId)
    $paths = Get-AeroLinkAttemptPaths $AttemptRoot
    $problems = @()
    $admitted = Read-AeroLinkJsonRecord -Path $paths.Attempt
    $journal = Read-AeroLinkTransitionEvents -Path $paths.JobEvents
    $quiescence = ''
    if ($admitted.Class -eq 'Valid' -and $journal.Class -eq 'Absent') {
        # The job's Intended record is written before the job exists and the delegate is created only in the armed
        # job: an admission record with no job record means nothing was ever started.
        $quiescence = 'NothingStarted'
    }
    else {
        $q = Get-AeroLinkTransitionQuiescence -AttemptRoot $AttemptRoot -AttemptId $AttemptId
        $quiescence = $q.State
        if ($q.State -ne 'Quiescent') { $problems += "quiescence $($q.State): $($q.Detail)" }
    }
    if (Test-Path -LiteralPath $paths.Spool) {
        foreach ($file in @(Get-ChildItem -LiteralPath $paths.Spool -Filter '*.request.json' -File)) {
            $requestId = $file.Name.Substring(0, $file.Name.Length - '.request.json'.Length)
            $resolution = Resolve-AeroLinkLaunchRequest -Spool $paths.Spool -RequestId $requestId
            if ($resolution.Blocking) { $problems += "launch request $requestId unresolved ($($resolution.Outcome)/$($resolution.CurrentHealth)): $($resolution.Detail)" }
        }
    }
    return [pscustomobject]@{ AttemptRoot = $AttemptRoot; AttemptId = $AttemptId; Resolved = ($problems.Count -eq 0); Quiescence = $quiescence; Problems = @($problems) }
}

function Test-AeroLinkInstallationAdmission {
    <# Quiescence and launch accounting over the durable attempt index. Call only while holding the product lease. #>
    param([Parameter(Mandatory)][string]$InstallationRoot)
    $index = Read-AeroLinkTransitionEvents -Path (Get-AeroLinkAttemptIndexPath -InstallationRoot $InstallationRoot)
    if ($index.Class -eq 'Absent') { return [pscustomobject]@{ Admitted = $true; Detail = 'no transition attempt was ever admitted for this installation'; Attempts = @() } }
    if ($index.Class -ne 'Valid') { return [pscustomobject]@{ Admitted = $false; Detail = "the attempt index is $($index.Class.ToLower()); prior work cannot be enumerated"; Attempts = @() } }
    $results = @($index.Events | ForEach-Object { Test-AeroLinkAttemptResolved -AttemptRoot ([string]$_.attemptRoot) -AttemptId ([string]$_.attemptId) })
    $blocking = @($results | Where-Object { -not $_.Resolved })
    $detail = if ($blocking.Count) { (@($blocking | ForEach-Object { "$($_.AttemptId): $($_.Problems -join ' | ')" }) -join ' || ') } else { "$($results.Count) prior attempt(s) resolved" }
    return [pscustomobject]@{ Admitted = ($blocking.Count -eq 0); Detail = $detail; Attempts = $results }
}

function Add-AeroLinkAttemptToIndex {
    # Written AFTER admission and BEFORE the attempt's job is intended, so every attempt that could have started work is enumerable.
    param([Parameter(Mandatory)][string]$InstallationRoot, [Parameter(Mandatory)][string]$AttemptRoot, [Parameter(Mandatory)][string]$AttemptId, [string]$Caller = '')
    Write-AeroLinkTransitionEvent -Path (Get-AeroLinkAttemptIndexPath -InstallationRoot $InstallationRoot) -Record ([ordered]@{ attemptId = $AttemptId; attemptRoot = $AttemptRoot; caller = $Caller; pid = $PID; at = (Get-AeroLinkUtcNow) })
}

# ==================================================================================================================
# Launch authority: the only path to a process that outlives a transition
# ==================================================================================================================

# Which image each preserved role may run and which readiness it must DEMONSTRATE. A preserved role with no
# readiness requirement would let "the process is running" stand in for "the service is serving".
$script:RolePolicy = @{
    'postgres'            = @{ Kind = 'postgres'; Image = 'postgres.exe' }
    'api'                 = @{ Kind = 'api'; Image = 'AeroLink.Api.exe' }
    'tunnel'              = @{ Kind = 'tunnel'; Image = 'ngrok.exe' }
    'qualification-probe' = @{ Kind = 'marker'; Image = 'powershell.exe' }
}

function Get-AeroLinkSpoolPaths {
    param([Parameter(Mandatory)][string]$Spool, [Parameter(Mandatory)][string]$RequestId)
    $base = Join-Path $Spool $RequestId
    return [pscustomobject]@{ Request = "$base.request.json"; Owner = "$base.owner"; Lock = "$base.inflight"; Events = "$base.events.jsonl"
        Response = "$base.response.json"; StagingReceipt = "$base.staging.json"; ReadyDir = (Join-Path $Spool 'ready') }
}

function New-AeroLinkLaunchResolution {
    param([string]$Outcome, [string]$Health, [string]$Detail, [int]$ProcessId = 0, [switch]$Adoptable)
    $granted = ($Outcome -eq 'Succeeded')
    return [pscustomobject]@{ Outcome = $Outcome; CurrentHealth = $Health; Granted = $granted
        Restored = ($granted -and $Health -eq 'Running')
        RestorationObligation = $(if ($granted -and $Health -eq 'Running') { 'None' } elseif ($Health -eq 'Unknown' -or $Outcome -in @('Pending', 'Unknown')) { 'Unknown' } else { 'Retained' })
        Blocking = (($Outcome -in @('Pending', 'Unknown')) -or ($Health -eq 'Unknown')); ProcessId = $ProcessId; Detail = $Detail; Adoptable = [bool]$Adoptable }
}

function New-AeroLinkLaunchResponse {
    param([Parameter(Mandatory)][string]$RequestId, [Parameter(Mandatory)]$Resolution, $Registered = $null)
    return [ordered]@{ requestId = $RequestId; outcome = $Resolution.Outcome; granted = $Resolution.Granted
        currentHealth = $Resolution.CurrentHealth; restored = $Resolution.Restored; restorationObligation = $Resolution.RestorationObligation
        blocking = $Resolution.Blocking; processId = $Resolution.ProcessId
        startedAt = $(if ($Registered) { [string]$Registered.startedAt } else { '' }); image = $(if ($Registered) { [string]$Registered.image } else { '' })
        detail = $Resolution.Detail; at = (Get-AeroLinkUtcNow) }
}

function Get-AeroLinkHealthOfIdentity {
    param([int]$ProcessId, [AllowNull()]$StartedAt, [AllowNull()]$Image)
    $state = [AeroLink.TransitionV1.Kernel]::Classify($ProcessId, (ConvertTo-AeroLinkUtcIso $StartedAt), [string]$Image)
    switch -Wildcard ($state) {
        'RunningMatch' { return @('Running', "pid $ProcessId is running with its recorded identity") }
        'Gone' { return @('ProvenStopped', "pid $ProcessId is proven stopped") }
        'RunningDifferent' { return @('ProvenStopped', "pid $ProcessId now belongs to a different process; the recorded one is gone") }
        default { return @('Unknown', "pid $ProcessId could not be classified ($state)") }
    }
}

function Resolve-AeroLinkLaunchRequest {
    <#
      WHAT HAPPENED (Outcome; append-only history) is kept apart from WHAT IS TRUE NOW (CurrentHealth; observed).
      Granted = Succeeded; Restored = Succeeded AND Running. Pure: writes nothing. -LockHeld: the caller holds the
      request's in-flight lock, so no live authority is acting on it.
    #>
    param([Parameter(Mandatory)][string]$Spool, [Parameter(Mandatory)][string]$RequestId, [switch]$LockHeld)
    $p = Get-AeroLinkSpoolPaths -Spool $Spool -RequestId $RequestId
    $claim = Read-AeroLinkJsonRecord -Path $p.Owner
    if ($claim.Class -eq 'Absent') { return New-AeroLinkLaunchResolution 'NotAccepted' 'NotApplicable' 'no authority has accepted this request' }
    if ($claim.Class -ne 'Valid') { return New-AeroLinkLaunchResolution 'Unknown' 'Unknown' "the request's ownership claim is $($claim.Class.ToLower())" }
    $verdict = [string](Get-AeroLinkProperty $claim.Value 'verdict' '')
    if ($verdict -eq 'cancelled') { return New-AeroLinkLaunchResolution 'Cancelled' 'NeverRan' 'the request was cancelled before any authority accepted it' }
    if ($verdict -ne 'accepted') { return New-AeroLinkLaunchResolution 'Unknown' 'Unknown' "unrecognized claim verdict '$verdict'" }
    if (-not $LockHeld) {
        $alive = Test-AeroLinkLockHolderAlive -Path $p.Lock
        if ($alive -eq 'Alive') { return New-AeroLinkLaunchResolution 'Pending' 'Unknown' 'an authority is still acting on this accepted request' }
        if ($alive -eq 'Unknown') { return New-AeroLinkLaunchResolution 'Unknown' 'Unknown' 'whether an authority is still acting on this request could not be established' }
    }
    $journal = Read-AeroLinkTransitionEvents -Path $p.Events
    if ($journal.Class -in @('Corrupt', 'Unreadable')) { return New-AeroLinkLaunchResolution 'Unknown' 'Unknown' "the request journal is $($journal.Class.ToLower()) ($($journal.Detail))" }
    $by = @{}
    foreach ($entry in @($journal.Events)) { $by[[string]$entry.type] = $entry }
    $created = if ($by.ContainsKey('Created')) { $by['Created'] } else { $null }
    $pidOf = if ($created) { [int]$created.processId } else { 0 }

    foreach ($terminal in @('Registered', 'RolledBack', 'FailedReadiness', 'FailedStart', 'Refused')) {
        if (-not $by.ContainsKey($terminal)) { continue }
        $t = $by[$terminal]
        if ($terminal -eq 'Registered') {
            $h = Get-AeroLinkHealthOfIdentity ([int]$t.processId) (Get-AeroLinkProperty $t 'startedAt') (Get-AeroLinkProperty $t 'image')
            return New-AeroLinkLaunchResolution 'Succeeded' $h[0] ("launch succeeded (commit evidence: $($t.commitEvidence)); now: $($h[1])") -ProcessId ([int]$t.processId)
        }
        if ($terminal -eq 'Refused') { return New-AeroLinkLaunchResolution 'Refused' 'NeverRan' "refused before anything was created: $($t.reason)" }
        $members = Get-AeroLinkProperty (Get-AeroLinkProperty $t 'cleanup') 'membersAfterTerminate' -1
        $health = if ((Test-AeroLinkIntegral $members) -and $members -eq 0) { 'ProvenStopped' } elseif (-not $created) { 'NeverRan' } else { 'Unknown' }
        return New-AeroLinkLaunchResolution $terminal $health ("$($t.detail); cleanup observed membersAfterTerminate=$members") -ProcessId $pidOf
    }
    if (-not $by.ContainsKey('Accepted')) { return New-AeroLinkLaunchResolution 'Abandoned' 'NeverRan' 'the accepting authority stopped before recording acceptance; nothing was created' }
    if (-not $by.ContainsKey('StagingRegistered')) { return New-AeroLinkLaunchResolution 'Abandoned' 'NeverRan' 'the authority stopped before its staging job was armed; nothing was created' }
    if (-not $created) { return New-AeroLinkLaunchResolution 'Abandoned' 'NeverRan' 'the authority stopped before recording creation; any process created was never resumed and executed nothing' }
    $witnessDir = [string]$by['Accepted'].witnessDir
    $receipt = Read-AeroLinkJsonRecord -Path $p.StagingReceipt
    $receiptVerdict = if ($receipt.Class -eq 'Valid' -and [string](Get-AeroLinkProperty $receipt.Value 'jobName') -eq [string]$by['StagingRegistered'].jobName) { [string]$receipt.Value.verdict } else { '' }
    if ($receipt.Class -in @('Malformed', 'Unreadable')) { return New-AeroLinkLaunchResolution 'Unknown' 'Unknown' "the witness receipt is $($receipt.Class.ToLower())" -ProcessId $pidOf }
    $committing = $by.ContainsKey('Committing')
    if ($receiptVerdict -eq 'Committed') {
        if (-not $committing) { return New-AeroLinkLaunchResolution 'Unknown' 'Unknown' 'the kernel reports a committed staging job that the journal never began committing' -ProcessId $pidOf }
        $h = Get-AeroLinkHealthOfIdentity $pidOf (Get-AeroLinkProperty $created 'startedAt') (Get-AeroLinkProperty $created 'image')
        return New-AeroLinkLaunchResolution 'Succeeded' $h[0] "committed (witness read kill-on-close cleared) but never registered; adoptable. now: $($h[1])" -ProcessId $pidOf -Adoptable
    }
    if ($receiptVerdict -eq 'Collected') {
        $detail = if ($committing) { 'the authority stopped while committing, before the flag changed' } else { 'the authority stopped before committing' }
        return New-AeroLinkLaunchResolution 'Abandoned' 'ProvenStopped' "$detail; the witness read kill-on-close set, collected the staging job and observed zero members" -ProcessId $pidOf
    }
    if ($receiptVerdict -eq 'CollectionUnconfirmed') { return New-AeroLinkLaunchResolution 'Abandoned' 'Unknown' 'the witness could not confirm the staging job emptied' -ProcessId $pidOf }
    $w = Get-AeroLinkWitnessState -Dir $witnessDir
    if ($w -eq 'Alive') { return New-AeroLinkLaunchResolution 'Pending' 'Unknown' 'the authority is gone but its witness is still acting on the staging job' -ProcessId $pidOf }
    $boot = Get-AeroLinkBootTimeUtc
    $createdAt = ConvertTo-AeroLinkUtcDate (Get-AeroLinkProperty $created 'at')
    if ($boot -and $createdAt -and $boot -gt $createdAt) {
        $outcome = if ($committing) { 'Indeterminate' } else { 'Abandoned' }
        return New-AeroLinkLaunchResolution $outcome 'ProvenStopped' 'authority and witness were lost, but the system has restarted since creation, so nothing from this launch survives' -ProcessId $pidOf
    }
    $stage = if ($committing) { 'while committing (whether the commit took effect is unknown)' } else { 'before committing' }
    return New-AeroLinkLaunchResolution 'Unknown' 'Unknown' "the authority and its witness both stopped $stage and no completion was observed (witness=$w)" -ProcessId $pidOf
}

function Get-AeroLinkListenerOwners {
    param([Parameter(Mandatory)][int]$Port)
    try { return @(Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction Stop | ForEach-Object { [int]$_.OwningProcess } | Select-Object -Unique) }
    catch {
        if (Test-AeroLinkNoMatchingConnection -ErrorRecord $_) { return @() }
        throw "the listener table for port $Port could not be read: $($_.Exception.Message)"
    }
}

function Get-AeroLinkPostgresInstance {
    <# Exact instance identity from the cluster's own postmaster.pid. Class: Absent | Unreadable | Valid #>
    param([Parameter(Mandatory)][string]$DataDirectory)
    $file = Join-Path $DataDirectory 'postmaster.pid'
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { return [pscustomobject]@{ Class = 'Absent' } }
    try {
        $fs = [IO.File]::Open($file, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
        try { $lines = (New-Object IO.StreamReader($fs)).ReadToEnd() -split "`r?`n" } finally { $fs.Dispose() }
        if ($lines.Count -lt 4 -or $lines[0] -notmatch '^\d+$' -or $lines[3] -notmatch '^\d+$') { return [pscustomobject]@{ Class = 'Unreadable' } }
        return [pscustomobject]@{ Class = 'Valid'; ProcessId = [int]$lines[0]; DataDirectory = $lines[1]; StartEpoch = $lines[2]; Port = [int]$lines[3] }
    }
    catch { return [pscustomobject]@{ Class = 'Unreadable' } }
}

function Test-AeroLinkSamePath([string]$A, [string]$B) {
    try { return ([IO.Path]::GetFullPath($A).TrimEnd('\', '/') -ieq [IO.Path]::GetFullPath($B).TrimEnd('\', '/')) } catch { return $false }
}

function Test-AeroLinkTunnelProtection {
    <#
      An unauthenticated request must return 401 at the protected edge. Mirrors Test-AeroLinkRemoteDemoPublicProtection
      without importing the remote-demo module into the authority.

      A disposable qualification installation (AEROLINK_INSTALLATION_ROOT set) may name a loopback stand-in edge in
      AEROLINK_QUALIFICATION_PROTECTION_PROBE, because a second agent on the real public URL would take the HOME
      endpoint over. The override is ignored for every normal installation.
    #>
    param([Parameter(Mandatory)][string]$PublicUrl)
    $target = $PublicUrl
    if ($env:AEROLINK_INSTALLATION_ROOT -and $env:AEROLINK_QUALIFICATION_PROTECTION_PROBE -and ([uri]$env:AEROLINK_QUALIFICATION_PROTECTION_PROBE).IsLoopback) {
        $target = $env:AEROLINK_QUALIFICATION_PROTECTION_PROBE
    }
    try {
        $response = Invoke-WebRequest -Uri $target -Headers @{ 'ngrok-skip-browser-warning' = '1' } -UseBasicParsing -TimeoutSec 20 -MaximumRedirection 0
        return [pscustomobject]@{ Protected = $false; StatusCode = [int]$response.StatusCode; Detail = "the public endpoint returned HTTP $([int]$response.StatusCode); 401 was required" }
    }
    catch {
        $status = $null
        try { if ($_.Exception.Response -and $_.Exception.Response.StatusCode) { $status = [int]$_.Exception.Response.StatusCode } } catch { }
        if ($status -eq 401) { return [pscustomobject]@{ Protected = $true; StatusCode = 401; Detail = 'an unauthenticated public request returned 401 at the protected edge' } }
        if ($null -ne $status) { return [pscustomobject]@{ Protected = $false; StatusCode = $status; Detail = "the public endpoint returned HTTP $status; 401 was required" } }
        return [pscustomobject]@{ Protected = $false; StatusCode = $null; Detail = "the public endpoint was unreachable ($($_.Exception.GetType().Name))" }
    }
}

function Test-AeroLinkRoleReadiness {
    <#
      Per-role readiness of an EXACT instance (pid), at launch and again at discharge. { Ready, Evidence }.
        marker    a marker bound to request, nonce and pid (qualification probe only)
        postgres  postmaster.pid names this pid, data directory and port; the port is listened on BY this pid; pg_isready
        api       the port is listened on BY this pid; /health/ready ready + database connected; /health/identity mode,
                  source identity and installation binding
        tunnel    the process is running with its identity; the public endpoint returns 401
    #>
    param([Parameter(Mandatory)]$Readiness, [Parameter(Mandatory)][int]$ProcessId, [string]$MarkerPath = '', [string]$Nonce = '', [string]$RequestId = '')
    $kind = [string](Get-AeroLinkProperty $Readiness 'kind' '')
    $result = { param($ok, $why) [pscustomobject]@{ Ready = [bool]$ok; Evidence = $why } }
    try {
        switch ($kind) {
            'marker' {
                $m = Read-AeroLinkJsonRecord -Path $MarkerPath
                if ($m.Class -ne 'Valid') { return & $result $false "the readiness marker is $($m.Class.ToLower())" }
                if ([string](Get-AeroLinkProperty $m.Value 'requestId') -ne $RequestId) { return & $result $false "the marker names request '$(Get-AeroLinkProperty $m.Value 'requestId')'" }
                if ([string](Get-AeroLinkProperty $m.Value 'nonce') -ne $Nonce) { return & $result $false 'the marker nonce does not match this launch' }
                if ([int](Get-AeroLinkProperty $m.Value 'processId' 0) -ne $ProcessId) { return & $result $false "the marker was written for pid $(Get-AeroLinkProperty $m.Value 'processId'), not $ProcessId" }
                return & $result $true "a marker bound to the request, nonce and pid $ProcessId"
            }
            'postgres' {
                $instance = Get-AeroLinkPostgresInstance -DataDirectory ([string]$Readiness.dataDirectory)
                if ($instance.Class -ne 'Valid') { return & $result $false "postmaster.pid is $($instance.Class.ToLower())" }
                if ($instance.ProcessId -ne $ProcessId) { return & $result $false "postmaster.pid names pid $($instance.ProcessId), not $ProcessId" }
                if (-not (Test-AeroLinkSamePath $instance.DataDirectory ([string]$Readiness.dataDirectory))) { return & $result $false "postmaster.pid names data directory '$($instance.DataDirectory)'" }
                if ($instance.Port -ne [int]$Readiness.port) { return & $result $false "postmaster.pid names port $($instance.Port)" }
                # The port must be listened on BY THIS PROCESS: pg_isready alone proves only that SOME server answers there.
                $owners = @(Get-AeroLinkListenerOwners -Port ([int]$Readiness.port))
                if ($owners -notcontains $ProcessId) { return & $result $false "port $($Readiness.port) is listened on by pid(s) '$($owners -join ',')', not $ProcessId" }
                $isready = Start-Process -FilePath (Join-Path ([string]$Readiness.binDir) 'pg_isready.exe') -ArgumentList @('-h', '127.0.0.1', '-p', [string]$Readiness.port, '-t', '3') -WindowStyle Hidden -PassThru
                $null = $isready.Handle
                if (-not $isready.WaitForExit(15000)) { try { $isready.Kill() } catch { }; return & $result $false 'pg_isready did not return within its bound' }
                if ($isready.ExitCode -ne 0) { return & $result $false "pg_isready exited $($isready.ExitCode)" }
                return & $result $true "postmaster pid $ProcessId owns '$($instance.DataDirectory)' on port $($instance.Port) and accepts connections"
            }
            'api' {
                $port = [int]$Readiness.port
                $owners = @(Get-AeroLinkListenerOwners -Port $port)
                if ($owners -notcontains $ProcessId -or $owners.Count -ne 1) { return & $result $false "port $port is listened on by pid(s) '$($owners -join ',')', not only $ProcessId" }
                $base = [string]$Readiness.baseUri
                if (-not (Test-AeroLinkReadyEndpoint -BaseUri $base -TimeoutSec 5)) { return & $result $false '/health/ready does not report ready with a connected database' }
                $identity = Get-AeroLinkRuntimeIdentity -BaseUri $base -TimeoutSec 5
                if ($null -eq $identity) { return & $result $false 'the API publishes no runtime identity' }
                $expectedMode = [string](Get-AeroLinkProperty $Readiness 'expectedMode' '')
                if ($expectedMode -and [string]$identity.mode -ne $expectedMode) { return & $result $false "the API reports mode '$($identity.mode)', not '$expectedMode'" }
                $expectedSource = [string](Get-AeroLinkProperty $Readiness 'expectedSourceIdentity' '')
                if ($expectedSource -and [string]$identity.sourceIdentity -ne $expectedSource) { return & $result $false "the API runs source '$($identity.sourceIdentity)', not '$expectedSource'" }
                foreach ($binding in @(@('expectedInstanceId', 'id'), @('expectedClassification', 'classification'))) {
                    $expected = [string](Get-AeroLinkProperty $Readiness $binding[0] '')
                    if (-not $expected) { continue }
                    $instance = Get-AeroLinkProperty $identity 'instance' $null
                    if ([string](Get-AeroLinkProperty $instance $binding[1] '') -ne $expected) { return & $result $false "the API does not prove installation $($binding[1]) '$expected'" }
                }
                return & $result $true "pid $ProcessId alone listens on $port, is ready, and proves mode '$($identity.mode)' and source '$([string]$identity.sourceIdentity)'"
            }
            'tunnel' {
                $protection = Test-AeroLinkTunnelProtection -PublicUrl ([string]$Readiness.publicUrl)
                if (-not $protection.Protected) { return & $result $false $protection.Detail }
                return & $result $true "tunnel pid $ProcessId is running and $($protection.Detail)"
            }
            default { return & $result $false "readiness kind '$kind' is not supported" }
        }
    }
    catch { return & $result $false "readiness could not be established: $($_.Exception.Message)" }
}

function Stop-AeroLinkStagingJob {
    # Terminate, then OBSERVE zero members through the handle still held. The observation is the evidence.
    param([Parameter(Mandatory)][IntPtr]$Job, [int]$TimeoutSeconds = 20)
    $K = [AeroLink.TransitionV1.Kernel]
    try {
        $before = $K::Members($Job)
        $K::Terminate($Job)
        $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
        $after = $K::Members($Job)
        while ($after.Assigned -ne 0 -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 50; $after = $K::Members($Job) }
        return [ordered]@{ membersBeforeTerminate = $before.Assigned; membersAfterTerminate = [int]$after.Assigned; observedThroughHeldHandle = $true }
    }
    catch { return [ordered]@{ membersAfterTerminate = -1; detail = $_.Exception.Message } }
}

function New-AeroLinkServiceEnvironment {
    <#
      The COMPLETE environment of a preserved service: the authority's own, the request's overrides applied (a null
      value removes a name), and the transition's capabilities removed. A restored API must not carry a lease token,
      a journal path or a handoff into its lifetime.
    #>
    param($Overrides)
    $environment = New-Object 'System.Collections.Generic.Dictionary[string,string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in [Environment]::GetEnvironmentVariables([EnvironmentVariableTarget]::Process).GetEnumerator()) { $environment[[string]$entry.Key] = [string]$entry.Value }
    if ($Overrides) {
        foreach ($property in @($Overrides.PSObject.Properties)) {
            if ($null -eq $property.Value) { [void]$environment.Remove($property.Name) } else { $environment[$property.Name] = [string]$property.Value }
        }
    }
    foreach ($name in @('AEROLINK_TRANSITION_LEASE', 'AEROLINK_TRANSITION_JOURNAL', 'AEROLINK_TRANSITION_HANDOFF', 'AEROLINK_PRODUCTION_OBLIGATION', 'AEROLINK_TRANSITION_CONTINUATION', 'AEROLINK_REMOTE_DEMO_HANDOFF')) {
        [void]$environment.Remove($name)
    }
    return $environment
}

function Invoke-AeroLinkAuthorityPump {
    <#
      One pass over the spool. Every request ends this pass with a published response, or with none only while its
      outcome is genuinely Pending.

      Ownership transfer has no gap-free ordering, so the intermediate states are recoverable instead: 'Committing'
      is durable BEFORE kill-on-close is cleared, and after an authority crash the witness reads the kill-on-close
      flag itself - the kernel's own record of whether the commit happened.
    #>
    param(
        [Parameter(Mandatory)][string]$Spool, [Parameter(Mandatory)][string]$AttemptId, [Parameter(Mandatory)]$Witness,
        # From Test-AeroLinkLaunchContextQualification, evaluated by THIS process about itself.
        [Parameter(Mandatory)]$Qualification,
        [switch]$QualificationProbe,
        # Contract-suite seams.
        [ValidateSet('None', 'AfterAcceptedEvent', 'AfterStagingRegistered', 'AfterCreated', 'AfterResumed', 'AfterReady', 'AfterCommitting', 'AfterClear', 'AfterRegistered')][string]$DieAt = 'None',
        [ValidateSet('None', 'Create', 'Resume', 'ReadinessThrows', 'Clear', 'RegisterWrite')][string]$InjectFailure = 'None'
    )
    $K = [AeroLink.TransitionV1.Kernel]
    if (-not (Test-Path -LiteralPath $Spool)) { return }
    New-Item -ItemType Directory -Path (Join-Path $Spool 'ready') -Force | Out-Null
    $seam = { param($Here) if ($DieAt -eq $Here) { [Environment]::Exit(90) } }
    foreach ($file in @(Get-ChildItem -LiteralPath $Spool -Filter '*.request.json' -File)) {
        $requestId = $file.Name.Substring(0, $file.Name.Length - '.request.json'.Length)
        $p = Get-AeroLinkSpoolPaths -Spool $Spool -RequestId $requestId
        if (Test-Path -LiteralPath $p.Response) { continue }
        $lock = Enter-AeroLinkTransitionLock -Path $p.Lock
        if ($lock.State -ne 'Held') { continue }
        try {
            if (Test-Path -LiteralPath $p.Response) { continue }
            $read = Read-AeroLinkJsonRecord -Path $p.Request
            $body = $read.Value
            if ($read.Class -ne 'Valid' -or [string](Get-AeroLinkProperty $body 'requestId') -ne $requestId) {
                Publish-AeroLinkJsonAtomic -Path $p.Response -Value (New-AeroLinkLaunchResponse $requestId (New-AeroLinkLaunchResolution 'Refused' 'NeverRan' "the request is $($read.Class.ToLower()) or names another request id"))
                continue
            }
            if ([string]$body.attemptId -ne $AttemptId) {
                Publish-AeroLinkJsonAtomic -Path $p.Response -Value (New-AeroLinkLaunchResponse $requestId (New-AeroLinkLaunchResolution 'Refused' 'NeverRan' "the request is bound to attempt '$($body.attemptId)'"))
                continue
            }
            $me = Get-AeroLinkProcessIdentityRecord -ProcessId $PID
            $claim = New-AeroLinkExclusiveRecord -Path $p.Owner -Value ([ordered]@{ verdict = 'accepted'; byPid = $PID; byStartedAt = $me.StartedAtUtc; at = (Get-AeroLinkUtcNow) })
            if (-not $claim.Won) {
                # REPLAY: we hold the in-flight lock, so no live authority is acting on this request.
                $resolution = Resolve-AeroLinkLaunchRequest -Spool $Spool -RequestId $requestId -LockHeld
                if ($resolution.Outcome -eq 'Pending') { continue }
                $registered = $null
                if ($resolution.Adoptable) {
                    $createdEvent = @((Read-AeroLinkTransitionEvents -Path $p.Events).Events | Where-Object { $_.type -eq 'Created' })[0]
                    Write-AeroLinkTransitionEvent -Path $p.Events -Record ([ordered]@{ type = 'Adopted'; by = $PID; evidence = 'witness read kill-on-close cleared'; at = (Get-AeroLinkUtcNow) })
                    $registered = [ordered]@{ type = 'Registered'; processId = [int]$createdEvent.processId; startedAt = $createdEvent.startedAt; image = $createdEvent.image
                        role = [string]$body.role; commitEvidence = 'witness-kernel-flag'; at = (Get-AeroLinkUtcNow) }
                    Write-AeroLinkTransitionEvent -Path $p.Events -Record $registered
                    $resolution = Resolve-AeroLinkLaunchRequest -Spool $Spool -RequestId $requestId -LockHeld
                }
                Publish-AeroLinkJsonAtomic -Path $p.Response -Value (New-AeroLinkLaunchResponse $requestId $resolution $registered)
                continue
            }

            # ---- NEW WORK. Refusals happen here, before anything exists. ----
            $refuse = {
                param([string]$Reason)
                Write-AeroLinkTransitionEvent -Path $p.Events -Record ([ordered]@{ type = 'Refused'; reason = $Reason; at = (Get-AeroLinkUtcNow) })
                Publish-AeroLinkJsonAtomic -Path $p.Response -Value (New-AeroLinkLaunchResponse $requestId (Resolve-AeroLinkLaunchRequest -Spool $Spool -RequestId $requestId -LockHeld))
            }
            $expires = ConvertTo-AeroLinkUtcDate (Get-AeroLinkProperty $body 'expiresAtUtc')
            if (-not $expires -or (Get-Date).ToUniversalTime() -gt $expires) { & $refuse 'the request expired before it was accepted'; continue }
            $role = [string]$body.role
            if (-not $script:RolePolicy.ContainsKey($role)) { & $refuse "role '$role' has no launch policy"; continue }
            $policy = $script:RolePolicy[$role]
            $readiness = Get-AeroLinkProperty $body 'readiness' $null
            if ([string](Get-AeroLinkProperty $readiness 'kind' '') -ne $policy.Kind) { & $refuse "role '$role' requires readiness of kind '$($policy.Kind)'"; continue }
            $launch = Get-AeroLinkProperty $body 'launch' $null
            $filePath = [string](Get-AeroLinkProperty $launch 'filePath' '')
            $allowedImage = $policy.Image
            # A disposable qualification installation beside a live HOME cannot run a second agent named ngrok.exe: the
            # live one would be a foreign tunnel to it, and a refusal. Only there may the tunnel image be renamed.
            if ($role -eq 'tunnel' -and $env:AEROLINK_INSTALLATION_ROOT -and $env:AEROLINK_QUALIFICATION_TUNNEL_IMAGE) { $allowedImage = [string]$env:AEROLINK_QUALIFICATION_TUNNEL_IMAGE }
            if (-not $filePath -or -not [IO.Path]::IsPathRooted($filePath) -or -not [string]::Equals([IO.Path]::GetFileName($filePath), $allowedImage, [StringComparison]::OrdinalIgnoreCase)) {
                & $refuse "role '$role' may only run $allowedImage by absolute path, not '$filePath'"; continue
            }
            if ($QualificationProbe) {
                if ($role -ne 'qualification-probe') { & $refuse "a qualification probe pass launches only the probe role, not '$role'"; continue }
                if (-not $Qualification.Context.Valid) { & $refuse "the launch context is unidentified: $($Qualification.Context.Reason)"; continue }
            }
            else {
                if ($role -eq 'qualification-probe') { & $refuse 'the probe role is only launched by a qualification pass'; continue }
                if (-not $Qualification.Supported) { & $refuse "preservation is not supported in this launch context: $($Qualification.Detail)"; continue }
            }
            if ((Get-AeroLinkWitnessState -Dir $Witness.Dir) -ne 'Alive') { & $refuse 'no live completion witness; an authority crash could not be resolved'; continue }
            $breakaway = [bool]$Qualification.BreakawayPermittedByImmediateJob

            Write-AeroLinkTransitionEvent -Path $p.Events -Record ([ordered]@{ type = 'Accepted'; authorityPid = $PID; witnessDir = $Witness.Dir; breakaway = $breakaway
                    descriptorHash = $Qualification.DescriptorHash; qualification = $(if ($QualificationProbe) { 'probe' } else { $Qualification.Detail }); at = (Get-AeroLinkUtcNow) })
            & $seam 'AfterAcceptedEvent'

            $jobName = 'Global\AeroLinkStage-' + $AttemptId + '-' + $requestId
            Write-AeroLinkTransitionEvent -Path $p.Events -Record ([ordered]@{ type = 'StagingIntended'; jobName = $jobName; at = (Get-AeroLinkUtcNow) })
            $job = [IntPtr]::Zero; $staged = $null; $committed = $false
            try {
                $job = $K::CreateJob($jobName, $K::LimitKillOnClose, $K::QueryOnlySddl())
                Register-AeroLinkWitnessJob -Witness $Witness -Job $job -JobName $jobName -Kind staging -ReceiptPath $p.StagingReceipt -AttemptId $AttemptId
                Write-AeroLinkTransitionEvent -Path $p.Events -Record ([ordered]@{ type = 'StagingRegistered'; jobName = $jobName; at = (Get-AeroLinkUtcNow) })
                & $seam 'AfterStagingRegistered'

                $nonce = [guid]::NewGuid().ToString('N')
                $marker = Join-Path $p.ReadyDir "$requestId.$nonce.json"
                $arguments = ([string](Get-AeroLinkProperty $launch 'arguments' '')).Replace('{READY_FILE}', $marker).Replace('{READY_NONCE}', $nonce).Replace('{REQUEST_ID}', $requestId)
                $spec = New-Object AeroLink.TransitionV1.LaunchSpec
                $spec.CommandLine = '"' + $filePath + '"' + $(if ($arguments) { ' ' + $arguments } else { '' })
                if ($InjectFailure -eq 'Create') { $spec.CommandLine = '"C:\definitely\not\here\' + $policy.Image + '"' }
                $spec.WorkingDirectory = [string](Get-AeroLinkProperty $launch 'workingDirectory' '')
                $spec.StandardOutputPath = [string](Get-AeroLinkProperty $launch 'standardOutput' '')
                $spec.StandardErrorPath = [string](Get-AeroLinkProperty $launch 'standardError' '')
                foreach ($log in @($spec.StandardOutputPath, $spec.StandardErrorPath)) { if ($log) { $d = Split-Path -Parent $log; if ($d -and -not (Test-Path -LiteralPath $d)) { New-Item -ItemType Directory -Path $d -Force | Out-Null } } }
                $spec.Environment = New-AeroLinkServiceEnvironment -Overrides (Get-AeroLinkProperty $launch 'environment' $null)
                $spec.RestrictAdministrators = [bool](Get-AeroLinkProperty $launch 'restrictAdministrators' $false)
                $spec.Breakaway = $breakaway
                $staged = $K::Launch($job, $spec)
                Write-AeroLinkTransitionEvent -Path $p.Events -Record ([ordered]@{ type = 'Created'; processId = $staged.ProcessId; startedAt = $staged.StartedAtUtc; image = $staged.ImagePath; at = (Get-AeroLinkUtcNow) })
                & $seam 'AfterCreated'

                if ($InjectFailure -eq 'Resume') { throw 'injected: ResumeThread failed' }
                $K::Resume($staged)
                Write-AeroLinkTransitionEvent -Path $p.Events -Record ([ordered]@{ type = 'Resumed'; at = (Get-AeroLinkUtcNow) })
                & $seam 'AfterResumed'

                # Operator access for services a scheduled context creates, established by the creator as before.
                $grant = Get-AeroLinkProperty $launch 'grantOperatorAccess' $null
                if ($grant) {
                    Grant-AeroLinkCreatedProcessAccess -ProcessId $staged.ProcessId -StartedAt ([DateTimeOffset]::Parse($staged.StartedAtUtc, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind)) `
                        -ExpectedExecutable $staged.ImagePath -ExpectedArguments @(@(Get-AeroLinkProperty $grant 'expectedArguments' @()) | ForEach-Object { [string]$_ })
                }

                if ($InjectFailure -eq 'ReadinessThrows') { throw 'injected: the readiness probe threw' }
                $deadline = (Get-Date).AddSeconds([Math]::Max(1, [int](Get-AeroLinkProperty $body 'readinessTimeoutSeconds' 60)))
                $ready = [pscustomobject]@{ Ready = $false; Evidence = 'no readiness evidence observed' }
                while ((Get-Date) -lt $deadline) {
                    if ($K::HasExited($staged)) { $ready = [pscustomobject]@{ Ready = $false; Evidence = "the service exited (code $($K::ExitCode($staged))) before it became ready" }; break }
                    $ready = Test-AeroLinkRoleReadiness -Readiness $readiness -ProcessId $staged.ProcessId -MarkerPath $marker -Nonce $nonce -RequestId $requestId
                    if ($ready.Ready) { break }
                    Start-Sleep -Milliseconds 500
                }
                if (-not $ready.Ready) {
                    $cleanup = Stop-AeroLinkStagingJob -Job $job
                    Write-AeroLinkTransitionEvent -Path $p.Events -Record ([ordered]@{ type = 'FailedReadiness'; detail = "the service did not become ready: $($ready.Evidence)"; cleanup = $cleanup; at = (Get-AeroLinkUtcNow) })
                    Publish-AeroLinkJsonAtomic -Path $p.Response -Value (New-AeroLinkLaunchResponse $requestId (Resolve-AeroLinkLaunchRequest -Spool $Spool -RequestId $requestId -LockHeld))
                    continue
                }
                Write-AeroLinkTransitionEvent -Path $p.Events -Record ([ordered]@{ type = 'Ready'; kind = $policy.Kind; evidence = $ready.Evidence; readiness = $readiness; markerPath = $marker; nonce = $nonce; at = (Get-AeroLinkUtcNow) })
                & $seam 'AfterReady'

                Write-AeroLinkTransitionEvent -Path $p.Events -Record ([ordered]@{ type = 'Committing'; at = (Get-AeroLinkUtcNow) })
                & $seam 'AfterCommitting'
                if ($InjectFailure -eq 'Clear') { throw 'injected: clearing kill-on-close failed' }
                $K::SetLimits($job, 0)
                $committed = $true
                & $seam 'AfterClear'
                $registered = [ordered]@{ type = 'Registered'; processId = $staged.ProcessId; startedAt = $staged.StartedAtUtc; image = $staged.ImagePath
                    role = $role; commitEvidence = 'authority'; readiness = $ready.Evidence; at = (Get-AeroLinkUtcNow) }
                try {
                    if ($InjectFailure -eq 'RegisterWrite') { throw 'injected: the registration write failed' }
                    Write-AeroLinkTransitionEvent -Path $p.Events -Record $registered
                }
                catch {
                    # Committed but UNRECORDED while this authority is alive: reverse the commit deterministically.
                    $K::SetLimits($job, $K::LimitKillOnClose)
                    $committed = $false
                    throw
                }
                & $seam 'AfterRegistered'
                Publish-AeroLinkJsonAtomic -Path $p.Response -Value (New-AeroLinkLaunchResponse $requestId (Resolve-AeroLinkLaunchRequest -Spool $Spool -RequestId $requestId -LockHeld) $registered)
            }
            catch {
                $message = $_.Exception.Message
                if (-not $committed) {
                    $cleanup = if ($job -ne [IntPtr]::Zero) { Stop-AeroLinkStagingJob -Job $job } else { [ordered]@{ membersAfterTerminate = 0; detail = 'no staging job was created' } }
                    $type = if ($message -like 'injected: the registration write failed*') { 'RolledBack' } else { 'FailedStart' }
                    try {
                        Write-AeroLinkTransitionEvent -Path $p.Events -Record ([ordered]@{ type = $type; detail = "launch failed: $message"; cleanup = $cleanup; at = (Get-AeroLinkUtcNow) })
                        Publish-AeroLinkJsonAtomic -Path $p.Response -Value (New-AeroLinkLaunchResponse $requestId (Resolve-AeroLinkLaunchRequest -Spool $Spool -RequestId $requestId -LockHeld))
                    }
                    catch { }   # unrecordable: the journal stays non-terminal and resolves Unknown, never success
                }
            }
            finally {
                if ($staged) { $K::Close($staged) }
                if ($job -ne [IntPtr]::Zero) { $K::CloseHandleChecked($job) }
            }
        }
        finally { $lock.Stream.Dispose() }
    }
}

function Test-AeroLinkRestorationDischarge {
    <#
      May a restoration obligation for this request be DISCHARGED now? Only when the launch succeeded AND the role's
      readiness is re-established against the exact registered instance at this moment. PID liveness is an input,
      never the answer.
    #>
    param([Parameter(Mandatory)][string]$Spool, [Parameter(Mandatory)][string]$RequestId)
    $resolution = Resolve-AeroLinkLaunchRequest -Spool $Spool -RequestId $RequestId
    $out = { param($ok, $why) [pscustomobject]@{ Discharged = [bool]$ok; Detail = $why; Resolution = $resolution } }
    if ($resolution.Outcome -ne 'Succeeded') { return & $out $false "the launch outcome is $($resolution.Outcome)" }
    if ($resolution.CurrentHealth -ne 'Running') { return & $out $false "the registered process is $($resolution.CurrentHealth)" }
    $ready = @(@((Read-AeroLinkTransitionEvents -Path (Get-AeroLinkSpoolPaths -Spool $Spool -RequestId $RequestId).Events).Events) | Where-Object { $_.type -eq 'Ready' })
    if (-not $ready.Count) { return & $out $false 'no readiness evidence was recorded for this launch' }
    $check = Test-AeroLinkRoleReadiness -Readiness $ready[0].readiness -ProcessId $resolution.ProcessId -MarkerPath ([string]$ready[0].markerPath) -Nonce ([string]$ready[0].nonce) -RequestId $RequestId
    return & $out $check.Ready "readiness re-checked now: $($check.Evidence)"
}

function Get-AeroLinkTransitionHandoffFromEnvironment {
    <# The handoff of the transition this process runs inside, or $null outside a transition. #>
    $path = $env:AEROLINK_TRANSITION_HANDOFF
    if ([string]::IsNullOrWhiteSpace($path)) { return $null }
    $read = Read-AeroLinkJsonRecord -Path $path
    if ($read.Class -ne 'Valid') { throw "This process runs inside a HOME transition, but its handoff at $path is $($read.Class.ToLower()). No preserved service was launched." }
    return $read.Value
}

function Request-AeroLinkServiceLaunch {
    <#
      Requester side. Publishes the request into the outer authority's spool and waits for its response. On timeout it
      races to CANCEL through the same exclusive claim; if acceptance won, the answer is the request's resolution
      (Pending while work can still complete) - never a definitive failure.

      Returns the response: outcome, restored, processId, startedAt, image, detail.
    #>
    param(
        [Parameter(Mandatory)]$Handoff,
        [Parameter(Mandatory)][ValidateSet('postgres', 'api', 'tunnel', 'qualification-probe')][string]$Role,
        [Parameter(Mandatory)][string]$FilePath, [string]$Arguments = '', [string]$WorkingDirectory = '',
        [string]$StandardOutput = '', [string]$StandardError = '', [hashtable]$Environment = @{}, [switch]$RestrictAdministrators,
        [Parameter(Mandatory)][hashtable]$Readiness, [int]$ReadinessTimeoutSeconds = 120, [string[]]$GrantOperatorAccessArguments,
        [int]$AcceptTimeoutSeconds = 60, [int]$SettleSeconds = 60
    )
    $requestId = [guid]::NewGuid().ToString('N').Substring(0, 16)
    $spool = [string]$Handoff.spool
    $p = Get-AeroLinkSpoolPaths -Spool $spool -RequestId $requestId
    $launch = [ordered]@{ filePath = $FilePath; arguments = $Arguments; workingDirectory = $WorkingDirectory; standardOutput = $StandardOutput; standardError = $StandardError
        environment = $Environment; restrictAdministrators = [bool]$RestrictAdministrators }
    if ($PSBoundParameters.ContainsKey('GrantOperatorAccessArguments')) { $launch['grantOperatorAccess'] = [ordered]@{ expectedArguments = @($GrantOperatorAccessArguments) } }
    Publish-AeroLinkJsonAtomic -Path $p.Request -Value ([ordered]@{ requestId = $requestId; attemptId = [string]$Handoff.attemptId; role = $Role; launch = $launch
            readiness = $Readiness; readinessTimeoutSeconds = $ReadinessTimeoutSeconds; requesterPid = $PID
            expiresAtUtc = (Get-Date).ToUniversalTime().AddSeconds($AcceptTimeoutSeconds).ToString('o'); at = (Get-AeroLinkUtcNow) })
    $readResponse = { $r = Read-AeroLinkJsonRecord -Path $p.Response; if ($r.Class -eq 'Valid') { $r.Value } else { $null } }
    $acceptDeadline = (Get-Date).AddSeconds($AcceptTimeoutSeconds)
    # Readiness runs inside the authority, so once accepted the wait covers readiness too.
    $deadline = $acceptDeadline.AddSeconds($ReadinessTimeoutSeconds + 30)
    while ((Get-Date) -lt $deadline) {
        $value = & $readResponse; if ($value) { return $value }
        if ((Get-Date) -ge $acceptDeadline -and -not (Test-Path -LiteralPath $p.Owner)) {
            $cancel = New-AeroLinkExclusiveRecord -Path $p.Owner -Value ([ordered]@{ verdict = 'cancelled'; byPid = $PID; at = (Get-AeroLinkUtcNow) })
            if ($cancel.Won) { return (New-AeroLinkLaunchResponse $requestId (New-AeroLinkLaunchResolution 'Cancelled' 'NeverRan' 'cancelled by the requester before any authority accepted it')) }
        }
        Start-Sleep -Milliseconds 200
    }
    $cancel = New-AeroLinkExclusiveRecord -Path $p.Owner -Value ([ordered]@{ verdict = 'cancelled'; byPid = $PID; at = (Get-AeroLinkUtcNow) })
    if ($cancel.Won) { return (New-AeroLinkLaunchResponse $requestId (New-AeroLinkLaunchResolution 'Cancelled' 'NeverRan' 'cancelled by the requester before any authority accepted it')) }
    $settle = (Get-Date).AddSeconds($SettleSeconds)
    while ((Get-Date) -lt $settle) { $value = & $readResponse; if ($value) { return $value }; Start-Sleep -Milliseconds 200 }
    return (New-AeroLinkLaunchResponse $requestId (Resolve-AeroLinkLaunchRequest -Spool $spool -RequestId $requestId))
}

# ==================================================================================================================
# Launch context
# ==================================================================================================================

function Get-AeroLinkProcessParentInfo {
    param([Parameter(Mandatory)][int]$ProcessId)
    $process = Get-CimInstance -ClassName Win32_Process -Filter "ProcessId=$ProcessId" -ErrorAction Stop
    if (-not $process) { return $null }
    return [pscustomobject]@{ ProcessId = [int]$process.ProcessId; ParentProcessId = [int]$process.ParentProcessId; Name = [string]$process.Name
        CommandLine = [string]$process.CommandLine; CreationDate = $process.CreationDate }
}

function Get-AeroLinkTaskDefinitionCanonical {
    <#
      The parts of a task definition that decide how its action process is placed and ended: the principal, every
      setting, and the IMAGE each action runs. Triggers, registration info, the task URI and action arguments are
      excluded: they do not place or end the process, and including the arguments would tie a qualification to one
      checkout path, so the same definition registered for qualification under another name yields the same hash.
    #>
    param([Parameter(Mandatory)][string]$Xml)
    $document = New-Object Xml.XmlDocument
    $document.PreserveWhitespace = $false
    $document.LoadXml($Xml)
    $parts = foreach ($name in @('Principals', 'Settings')) {
        $node = @($document.DocumentElement.ChildNodes | Where-Object { $_.LocalName -eq $name })[0]
        if ($null -eq $node) { "<$name/>" } else { ($node.OuterXml -replace '\s+xmlns="[^"]*"', '') -replace '>\s+<', '><' }
    }
    $actions = @($document.DocumentElement.ChildNodes | Where-Object { $_.LocalName -eq 'Actions' })[0]
    $images = if ($actions) { @($actions.ChildNodes | ForEach-Object { "$($_.LocalName):" + [string](@($_.ChildNodes | Where-Object { $_.LocalName -eq 'Command' })[0]).InnerText.Trim().ToLowerInvariant() }) } else { @() }
    $parts += '<Actions>' + ($images -join ';') + '</Actions>'
    return ($parts -join "`n")
}

function Get-AeroLinkRunningTaskForProcess {
    <# The registered task whose running instance's engine is this process, through the Task Scheduler API. #>
    param([Parameter(Mandatory)][int]$ProcessId)
    $service = New-Object -ComObject 'Schedule.Service'
    $service.Connect()
    foreach ($running in @($service.GetRunningTasks(1))) {
        if ([int]$running.EnginePID -ne $ProcessId) { continue }
        $path = [string]$running.Path
        $folderPath = Split-Path -Parent $path
        if ([string]::IsNullOrEmpty($folderPath)) { $folderPath = '\' }
        $task = $service.GetFolder($folderPath).GetTask((Split-Path -Leaf $path))
        return [pscustomobject]@{ Path = $path; InstanceGuid = [string]$running.InstanceGuid; Xml = [string]$task.Xml }
    }
    return $null
}

function Get-AeroLinkLaunchContextDescriptor {
    <#
      .SYNOPSIS What THIS process is running in, computed about itself. { Valid, Reason, Descriptor, DescriptorHash, Attestation }
      .DESCRIPTION
        Attested by real ancestry, never a label: a Task Scheduler context is a process whose parent is the Schedule
        service host AND which the Task Scheduler API names as the engine of a running task; an operator console is
        cmd.exe started by explorer.exe. Anything else is unidentified and cannot be qualified.

        The descriptor holds only what decides placement: the context and its definition, the native placement code,
        the host image, the principal and logon type, the session class, the ACTUAL token (elevation, type, integrity,
        Administrators attributes) and the immediate job flags. A change to any of them lands on a different
        qualification. The entry script's bytes are deliberately not included: they do not place processes, and
        including them would disqualify every scheduled context at every product merge.
    #>
    param([hashtable]$Override)
    if ($Override) {
        $canonical = (@($Override.Keys | Sort-Object | ForEach-Object { "$_=$($Override[$_])" }) -join ';')
        return [pscustomobject]@{ Valid = $true; Reason = 'injected by the contract suite'; Descriptor = $Override; DescriptorHash = (Get-AeroLinkSha256Text $canonical); Attestation = $null }
    }
    $K = [AeroLink.TransitionV1.Kernel]
    $fail = { param($why) [pscustomobject]@{ Valid = $false; Reason = $why; Descriptor = $null; DescriptorHash = ''; Attestation = $null } }
    try {
        $me = Get-AeroLinkProcessIdentityRecord -ProcessId $PID
        if (-not $me) { return & $fail "this process's own identity could not be read" }
        $self = Get-AeroLinkProcessParentInfo -ProcessId $PID
        $parent = Get-AeroLinkProcessParentInfo -ProcessId $self.ParentProcessId
        if (-not $parent) { return & $fail "the parent process (pid $($self.ParentProcessId)) no longer exists; the context cannot be attested" }
        $parentIdentity = Get-AeroLinkProcessIdentityRecord -ProcessId $parent.ProcessId
        if (-not $parentIdentity -and $parent.Name -ine 'svchost.exe') { return & $fail "the parent process identity could not be read" }
        if ($parentIdentity -and (ConvertTo-AeroLinkUtcDate $parentIdentity.StartedAtUtc) -gt (ConvertTo-AeroLinkUtcDate $me.StartedAtUtc)) { return & $fail 'the recorded parent pid now belongs to a younger process' }
        $kind = ''; $name = ''; $definition = ''; $attestation = $null
        # A task action may be this process, or a bounded shell wrapper (cmd.exe / powershell.exe) that started it.
        # The Task Scheduler API - not a command line, which a non-elevated caller cannot read for the service
        # host - names the action process as the running task's engine, and every link must be a live, older parent.
        $engine = $self; $engineParent = $parent; $chain = @($self.Name.ToLowerInvariant())
        for ($depth = 0; $depth -lt 2 -and $engineParent -and $engineParent.Name -ine 'svchost.exe' -and $engineParent.Name -in @('cmd.exe', 'powershell.exe'); $depth++) {
            $engine = $engineParent
            $engineParent = Get-AeroLinkProcessParentInfo -ProcessId $engine.ParentProcessId
            if ($engineParent -and $engineParent.CreationDate -and $engine.CreationDate -and $engineParent.CreationDate -gt $engine.CreationDate) { $engineParent = $null }
            $chain = @($engine.Name.ToLowerInvariant()) + $chain
        }
        $task = $null
        if ($engineParent -and $engineParent.Name -ieq 'svchost.exe') { $task = Get-AeroLinkRunningTaskForProcess -ProcessId $engine.ProcessId }
        if ($task) {
            # The task PATH is attestation, not identity: a disposable twin of a definition qualifies the definition.
            $kind = 'Task'; $name = 'task|' + ($chain -join '>')
            $definition = Get-AeroLinkSha256Text (Get-AeroLinkTaskDefinitionCanonical -Xml $task.Xml)
            $attestation = [ordered]@{ taskPath = $task.Path; instance = $task.InstanceGuid; enginePid = $engine.ProcessId; chain = ($chain -join '>') }
        }
        elseif ($parent.Name -ieq 'svchost.exe') {
            return & $fail 'this process was started by a service host, but no running scheduled task names it or its wrapper as its engine'
        }
        elseif ($parent.Name -ieq 'cmd.exe') {
            $grandparent = Get-AeroLinkProcessParentInfo -ProcessId $parent.ParentProcessId
            if ($grandparent -and $grandparent.Name -ieq 'explorer.exe') {
                $kind = 'Operator'; $name = 'explorer.exe>cmd.exe'
                $definition = Get-AeroLinkSha256Text $name
                $attestation = [ordered]@{ parentPid = $parent.ProcessId; grandparentPid = $grandparent.ProcessId; console = $parent.CommandLine }
            }
            else { return & $fail "cmd.exe was started by '$(if ($grandparent) { $grandparent.Name } else { 'an exited process' })', not explorer.exe; this console context is not identified" }
        }
        else { return & $fail "this process was started by '$($parent.Name)', which is neither the Task Scheduler service nor an Explorer console" }

        $token = Get-AeroLinkTokenFacts
        if (-not $token.Readable) { return & $fail "the process token could not be read: $($token.Detail)" }
        $logonType = if ($token.LogonSids -contains 'S-1-5-3') { 'Batch' } elseif ($token.LogonSids -contains 'S-1-5-4') { 'Interactive' } elseif ($token.LogonSids -contains 'S-1-5-6') { 'Service' } else { 'Unknown' }
        $inJob = $K::InJob(0, [IntPtr]::Zero)
        $descriptor = [ordered]@{
            contextKind = $kind
            contextName = $name
            definitionHash = $definition
            placementProtocol = [AeroLink.TransitionV1.Kernel]::PlacementProtocol
            kernelSourceHash = Get-AeroLinkTransitionKernelSourceHash
            hostImage = $me.ImagePath.ToLowerInvariant()
            principalSid = $token.UserSid
            logonType = $logonType
            sessionClass = $(if ($token.SessionId -eq 0) { 'session0' } else { 'interactive-session' })
            tokenElevation = [string]$token.TokenElevation
            tokenElevationType = $token.TokenElevationType
            integrityLevel = $token.IntegrityLevel
            administratorsGroup = $token.AdministratorsGroup
            immediateJobFlags = $(if ($inJob) { '0x{0:X}' -f $K::LimitFlags([IntPtr]::Zero) } else { 'none' })
        }
        $canonical = (@($descriptor.Keys | ForEach-Object { "$_=$($descriptor[$_])" }) -join ';')
        return [pscustomobject]@{ Valid = $true; Reason = 'attested by ancestry'; Descriptor = $descriptor; DescriptorHash = (Get-AeroLinkSha256Text $canonical); Attestation = $attestation; Token = $token }
    }
    catch { return & $fail "the launch context could not be established: $($_.Exception.Message)" }
}

function Get-AeroLinkQualificationPath {
    param([Parameter(Mandatory)][string]$InstallationRoot, [Parameter(Mandatory)][string]$DescriptorHash)
    return (Join-Path (Get-AeroLinkTransitionStateRoot -InstallationRoot $InstallationRoot) "qualifications\$DescriptorHash.json")
}

function Test-AeroLinkLaunchContextQualification {
    <# { Supported, Detail, Descriptor, DescriptorHash, BreakawayPermittedByImmediateJob, Context } #>
    param([Parameter(Mandatory)][string]$InstallationRoot, [hashtable]$DescriptorOverride)
    $K = [AeroLink.TransitionV1.Kernel]
    $context = Get-AeroLinkLaunchContextDescriptor -Override $DescriptorOverride
    $breakaway = $false
    try { if ($K::InJob(0, [IntPtr]::Zero)) { $breakaway = (($K::LimitFlags([IntPtr]::Zero) -band ($K::LimitBreakawayOk -bor $K::LimitSilentBreakawayOk)) -ne 0) } } catch { }
    $out = { param($ok, $why) [pscustomobject]@{ Supported = [bool]$ok; Detail = $why; Descriptor = $context.Descriptor; DescriptorHash = $context.DescriptorHash; BreakawayPermittedByImmediateJob = $breakaway; Context = $context } }
    if (-not $context.Valid) { return & $out $false "launch context unidentified: $($context.Reason)" }
    $path = Get-AeroLinkQualificationPath -InstallationRoot $InstallationRoot -DescriptorHash $context.DescriptorHash
    $record = Read-AeroLinkJsonRecord -Path $path
    if ($record.Class -eq 'Absent') { return & $out $false "the $($context.Descriptor.contextKind) context '$($context.Descriptor.contextName)' is not qualified for this exact descriptor ($($context.DescriptorHash.Substring(0, 12)))" }
    if ($record.Class -ne 'Valid') { return & $out $false "the qualification record is $($record.Class.ToLower())" }
    $sidecar = "$path.sha256"
    if (-not (Test-Path -LiteralPath $sidecar) -or ([IO.File]::ReadAllText($sidecar).Trim() -ne (Get-AeroLinkSha256File $path))) { return & $out $false 'the qualification record fails its integrity hash' }
    $r = $record.Value
    if ([string]$r.qualifierVersion -ne $script:QualifierVersion) { return & $out $false "the qualification was made by '$($r.qualifierVersion)', not '$($script:QualifierVersion)'" }
    foreach ($key in @($context.Descriptor.Keys)) {
        if ([string](Get-AeroLinkProperty $r.descriptor $key '') -ne [string]$context.Descriptor[$key]) { return & $out $false "the qualification record's '$key' does not match this context" }
    }
    if ([string]$r.verdict -ne 'Qualified') { return & $out $false "this context was qualified as $($r.verdict): $($r.detail)" }
    foreach ($property in @($r.paths.PSObject.Properties)) {
        $evidence = $property.Value
        if ($evidence.applicable -and -not ($evidence.observed -and $evidence.survived)) { return & $out $false "cleanup path '$($property.Name)' did not preserve the probe" }
    }
    return & $out $true "the $($context.Descriptor.contextKind) context '$($context.Descriptor.contextName)' was qualified at $($r.at) for this exact descriptor"
}

function Write-AeroLinkLaunchContextQualification {
    <#
      Written ONLY from probe observations. An applicable path whose probe never launched was NOT OBSERVED: that is a
      failed experiment, never evidence of incompatibility, and it takes precedence.
    #>
    param([Parameter(Mandatory)][string]$InstallationRoot, [Parameter(Mandatory)]$Descriptor, [Parameter(Mandatory)][string]$DescriptorHash,
        [Parameter(Mandatory)][Collections.IDictionary]$Paths, [Parameter(Mandatory)][string[]]$RequiredPaths, [string]$Detail = '')
    $missing = @($RequiredPaths | Where-Object { -not $Paths.Contains($_) })
    $applicable = @($RequiredPaths | Where-Object { $Paths.Contains($_) -and $Paths[$_].applicable })
    $unobserved = @($applicable | Where-Object { $Paths[$_].observed -ne $true })
    $failed = @($applicable | Where-Object { $unobserved -notcontains $_ -and -not $Paths[$_].survived })
    $verdict = if ($missing.Count) { 'Incomplete' } elseif ($unobserved.Count) { 'ExperimentFailed' } elseif ($failed.Count) { 'Incompatible' } else { 'Qualified' }
    $record = [ordered]@{ qualifierVersion = $script:QualifierVersion; descriptorHash = $DescriptorHash; descriptor = $Descriptor; paths = $Paths; verdict = $verdict
        detail = $(if ($missing.Count) { "no evidence: $($missing -join ', ')" } elseif ($unobserved.Count) { "experiment failed, placement NOT observed on: $($unobserved -join ', '); rerun qualification" }
            elseif ($failed.Count) { "did not survive: $($failed -join ', ')" } else { $Detail })
        at = (Get-AeroLinkUtcNow) }
    $path = Get-AeroLinkQualificationPath -InstallationRoot $InstallationRoot -DescriptorHash $DescriptorHash
    Publish-AeroLinkJsonAtomic -Path $path -Value $record
    [IO.File]::WriteAllText("$path.sha256", (Get-AeroLinkSha256File $path))
    return [pscustomobject]$record
}

# ==================================================================================================================
# Handoff and result contract
# ==================================================================================================================

function Read-AeroLinkTransitionHandoff {
    <#
      Validates the versioned handoff BEFORE the actor enters the lease or mutates anything.
      Code: Accepted | Unreadable | ProtocolIncompatible | SourceVersionMismatch
    #>
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][ValidateSet('delegate', 'continuation')][string]$Role,
        [Parameter(Mandatory)][AllowEmptyString()][string]$ActualSourceIdentity, [string]$ExpectedSourceIdentity)
    $read = Read-AeroLinkJsonRecord -Path $Path
    if ($read.Class -ne 'Valid') { return [pscustomobject]@{ Accepted = $false; Code = 'Unreadable'; Detail = "the handoff record is $($read.Class.ToLower())"; Handoff = $null } }
    $h = $read.Value
    $version = Get-AeroLinkProperty $h 'protocolVersion' $null
    if (-not (Test-AeroLinkIntegral $version) -or $script:SupportedHandoffProtocols -notcontains [int]$version) {
        return [pscustomobject]@{ Accepted = $false; Code = 'ProtocolIncompatible'; Detail = "handoff protocol '$version' is not supported by this $Role (supports $($script:SupportedHandoffProtocols -join ','))"; Handoff = $h }
    }
    $expected = if ($PSBoundParameters.ContainsKey('ExpectedSourceIdentity')) { $ExpectedSourceIdentity } else { [string](Get-AeroLinkProperty (Get-AeroLinkProperty $h 'delegate' $null) 'sourceIdentity' '') }
    if ([string]::IsNullOrWhiteSpace($expected) -or [string]::IsNullOrWhiteSpace($ActualSourceIdentity) -or $expected -ne $ActualSourceIdentity) {
        return [pscustomobject]@{ Accepted = $false; Code = 'SourceVersionMismatch'; Detail = "the outer expected source '$expected' but this $Role runs '$ActualSourceIdentity'; the source changed between handoff and entry"; Handoff = $h }
    }
    return [pscustomobject]@{ Accepted = $true; Code = 'Accepted'; Detail = 'protocol and source identity accepted'; Handoff = $h }
}

function Test-AeroLinkRoleFailureCode { param([string]$Code) return ($Code -match '^RoleNotRestored:|^RoleNotRequested:') }

function Test-AeroLinkActorOutcome {
    <#
      Result contract: exit 0 <=> Completed with no failures; exit 3 <=> Failed with >=1 failure; refusals 30/32/33.
      Class: Valid | Absent | Malformed | Invalid. A parse failure or schema violation is never "Completed".
    #>
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][ValidateSet('delegate', 'continuation')][string]$Role)
    $out = { param($Class, $Detail, $Decision = '', $Failures = @(), $Roles = @(), $Value = $null)
        [pscustomobject]@{ Class = $Class; Detail = $Detail; Decision = $Decision; Failures = @($Failures); Roles = @($Roles); Value = $Value } }
    $read = Read-AeroLinkJsonRecord -Path $Path
    if ($read.Class -eq 'Absent') { return & $out 'Absent' "no $Role outcome was published" }
    if ($read.Class -ne 'Valid') { return & $out 'Malformed' "the $Role outcome is $($read.Class.ToLower()) ($($read.Detail))" }
    $v = $read.Value
    if ($v -is [string] -or $v -is [array]) { return & $out 'Invalid' "the $Role outcome is not an object" }
    $names = @($v.PSObject.Properties | ForEach-Object { $_.Name })
    if ($names -notcontains 'decision' -or $v.decision -isnot [string]) { return & $out 'Invalid' "the $Role outcome has no string decision" }
    $decision = [string]$v.decision
    if ($names -notcontains 'pid' -or -not (Test-AeroLinkIntegral $v.pid)) { return & $out 'Invalid' "the $Role outcome has no integer pid" }
    if ($script:RefusalDecisions -contains $decision) { return & $out 'Valid' "refused: $decision" $decision @() @() $v }
    if ($decision -notin @('Completed', 'Failed')) { return & $out 'Invalid' "the $Role outcome decision '$decision' is not part of the contract" }
    if ($names -notcontains 'failures') { return & $out 'Invalid' "the $Role outcome has no failures list" }
    $failures = @(@($v.failures) | Where-Object { $null -ne $_ } | ForEach-Object { [string]$_ })
    if ($decision -eq 'Completed' -and $failures.Count -gt 0) { return & $out 'Invalid' "the $Role outcome says Completed but lists failures: $($failures -join '; ')" }
    if ($decision -eq 'Failed' -and $failures.Count -eq 0) { return & $out 'Invalid' "the $Role outcome says Failed without a reason" }
    $roles = @()
    if ($Role -eq 'continuation') {
        if ($names -notcontains 'roles') { return & $out 'Invalid' 'the continuation outcome has no roles list' }
        $roles = @(@($v.roles) | Where-Object { $null -ne $_ })
        if ($decision -eq 'Completed' -and @($roles | Where-Object { -not ($null -ne $_.PSObject.Properties['restored'] -and $_.restored -eq $true) }).Count -gt 0) {
            return & $out 'Invalid' 'the continuation outcome says Completed but a role is not restored'
        }
    }
    return & $out 'Valid' 'valid' $decision $failures $roles $v
}

function Test-AeroLinkActorExitMatchesOutcome {
    <# The exit-code half of the contract. Returns '' when consistent, else a failure code. #>
    param([AllowNull()]$ExitCode, $Outcome, [Parameter(Mandatory)][string]$Actor)
    if ($null -eq $ExitCode) { return "${Actor}ExitUnknown" }
    if ($Outcome.Class -ne 'Valid') { return $(if ([int]$ExitCode -eq 0) { "${Actor}ExitedZeroWithoutValidOutcome" } else { "${Actor}Exit:$ExitCode" }) }
    $expected = switch ($Outcome.Decision) {
        'Completed' { 0 } 'Failed' { 3 }
        'ProtocolIncompatible' { 30 } 'SourceVersionMismatch' { 30 } 'Unreadable' { 30 }
        'LeaseRejected' { 32 } 'NotADescendant' { 33 }
        default { $null }
    }
    if ($null -eq $expected) { return "${Actor}ExitUnmapped:exit=$ExitCode/decision=$($Outcome.Decision)" }
    if ([int]$ExitCode -ne $expected) { return "${Actor}ExitMismatch:exit=$ExitCode/decision=$($Outcome.Decision)" }
    return ''
}

# ==================================================================================================================
# The outer
# ==================================================================================================================

function New-AeroLinkLogBinding {
    <#
      Where "this attempt" begins in a SHARED, reused log, captured BEFORE anything is launched (C4a): the file's
      identity, its length and a fingerprint of the bytes before that offset. The tail resets on a replaced file,
      a truncation, or changed bytes before the position - including an in-place rewrite to the same length (C4b).
    #>
    param([Parameter(Mandatory)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return [pscustomobject]@{ Exists = $false; Length = [long]0; LastWrite = ''; Fingerprint = [byte[]]@() } }
    $fs = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
    try {
        $length = $fs.Length
        $count = [int][Math]::Min([long]4096, $length)
        $fingerprint = New-Object byte[] $count
        if ($count -gt 0) { [void]$fs.Seek($length - $count, [IO.SeekOrigin]::Begin); $read = 0; while ($read -lt $count) { $n = $fs.Read($fingerprint, $read, $count - $read); if ($n -le 0) { break }; $read += $n } }
        return [pscustomobject]@{ Exists = $true; Length = $length; CreationTicks = (Get-Item -LiteralPath $Path).CreationTimeUtc.Ticks; Fingerprint = $fingerprint }
    }
    finally { $fs.Dispose() }
}

function Invoke-AeroLinkTransitionChain {
    <#
      .SYNOPSIS Runs one transition attempt as its OUTER authority and returns its outcome. The caller holds the lease.
      .DESCRIPTION
        Decisions (exit code in parentheses): Completed (0) | Refused (context 20 / prior-attempt 22 / handoff 25) |
        DeadlineExceeded (23) | ChainFailed (24: an actor failed, or its result is absent, invalid or contradicted, or
        anything is unresolved) | RestorationFailed (26: the chain ran but a required role is not restored) |
        HostError (1). restorationRequired = mutation began AND a required role is not restored.
    #>
    param(
        [Parameter(Mandatory)][string]$InstallationRoot,
        [Parameter(Mandatory)]$Lease,
        [Parameter(Mandatory)][string]$Caller,
        # Caller-specific transition instructions, carried verbatim in the handoff.
        [Parameter(Mandatory)][Collections.IDictionary]$Plan,
        # The actor the delegate runs, from the checkout whose source identity it must prove.
        [Parameter(Mandatory)][string]$DelegateScript,
        [Parameter(Mandatory)][string]$DelegateSourceIdentity,
        # Roles that must be restored and verified BY THE OUTER: { role, readiness, launchRequired }.
        [object[]]$RequiredRoles = @(),
        [Parameter(Mandatory)][int]$DeadlineSeconds,
        [Parameter(Mandatory)]$Qualification,
        [switch]$QualificationProbe,
        [switch]$StreamToHost,
        [string[]]$SharedProgressLog = @(),
        [int]$ProtocolVersion = $script:HandoffProtocolVersion,
        [string]$AttemptId = (New-AeroLinkAttemptId),
        # Contract-suite seams.
        [string]$AuthorityDieAt = 'None', [string]$WitnessFault = '', [hashtable]$Faults = @{}
    )
    $K = [AeroLink.TransitionV1.Kernel]
    $attemptRoot = Join-Path (Get-AeroLinkTransitionStateRoot -InstallationRoot $InstallationRoot) $AttemptId
    $paths = Get-AeroLinkAttemptPaths $attemptRoot
    New-Item -ItemType Directory -Path $attemptRoot, $paths.Spool, $paths.Logs -Force | Out-Null
    $startedAt = Get-Date
    $script:published = $false
    $finalizedByCatch = $false
    $emit = {
        param([Collections.IDictionary]$Value, [int]$ExitCode)
        $Value['attemptId'] = $AttemptId; $Value['caller'] = $Caller; $Value['outerPid'] = $PID; $Value['exitCode'] = $ExitCode
        $Value['elapsedSeconds'] = [math]::Round(((Get-Date) - $startedAt).TotalSeconds, 1); $Value['at'] = (Get-AeroLinkUtcNow)
        Publish-AeroLinkJsonAtomic -Path $paths.Outcome -Value $Value
        $script:published = $true
        return [pscustomobject]@{ Decision = [string]$Value['decision']; ExitCode = $ExitCode; AttemptId = $AttemptId; AttemptRoot = $attemptRoot; Outcome = [pscustomobject]$Value
            RestorationRequired = [bool](Get-AeroLinkProperty $Value 'restorationRequired' $false); Detail = [string](Get-AeroLinkProperty $Value 'detail' '') }
    }
    if (-not $Lease -or -not $Lease.Owner) { return & $emit ([ordered]@{ decision = 'Refused'; stage = 'lease'; detail = 'the outer must own the HOME transition lease'; mutationStarted = $false }) 21 }
    if (-not $QualificationProbe -and -not $Qualification.Supported) {
        return & $emit ([ordered]@{ decision = 'Refused'; stage = 'context'; detail = $Qualification.Detail; mutationStarted = $false; descriptorHash = $Qualification.DescriptorHash; descriptor = $Qualification.Descriptor }) 20
    }
    if ($QualificationProbe -and -not $Qualification.Context.Valid) {
        return & $emit ([ordered]@{ decision = 'Refused'; stage = 'context'; detail = "launch context unidentified: $($Qualification.Context.Reason)"; mutationStarted = $false }) 20
    }

    $witness = $null; $job = $null; $delegate = $null
    try {
        $admission = Test-AeroLinkInstallationAdmission -InstallationRoot $InstallationRoot
        if (-not $admission.Admitted) { return & $emit ([ordered]@{ decision = 'Refused'; stage = 'prior-attempt'; detail = $admission.Detail; mutationStarted = $false }) 22 }
        Publish-AeroLinkJsonAtomic -Path $paths.Attempt -Value ([ordered]@{ attemptId = $AttemptId; caller = $Caller; installationRoot = $InstallationRoot; pid = $PID; admission = $admission.Detail; descriptorHash = $Qualification.DescriptorHash; at = (Get-AeroLinkUtcNow) })
        Add-AeroLinkAttemptToIndex -InstallationRoot $InstallationRoot -AttemptRoot $attemptRoot -AttemptId $AttemptId -Caller $Caller

        $witnessArguments = @{}
        if ($WitnessFault) { $witnessArguments['FaultInjection'] = $WitnessFault }
        $witness = Start-AeroLinkTransitionWitness -Dir $paths.Witness -Breakaway:([bool]$Qualification.BreakawayPermittedByImmediateJob) @witnessArguments
        $job = New-AeroLinkTransitionJob -AttemptRoot $attemptRoot -AttemptId $AttemptId -Witness $witness

        $deadline = (Get-Date).AddSeconds($DeadlineSeconds)
        $outerIdentity = Get-AeroLinkProcessIdentityRecord -ProcessId $PID
        Publish-AeroLinkJsonAtomic -Path $paths.Handoff -Value ([ordered]@{ protocolVersion = $ProtocolVersion; attemptId = $AttemptId; attemptRoot = $attemptRoot; caller = $Caller
                installationRoot = $InstallationRoot; spool = $paths.Spool; logs = $paths.Logs; plan = $Plan; faults = $Faults
                delegate = [ordered]@{ script = $DelegateScript; sourceIdentity = $DelegateSourceIdentity }
                requiredRoles = @($RequiredRoles | ForEach-Object { [string]$_.role })
                outerPid = $PID; outerStartedAt = $outerIdentity.StartedAtUtc; deadlineUtc = $deadline.ToUniversalTime().ToString('o'); at = (Get-AeroLinkUtcNow) })

        # Shared logs are bound BEFORE the delegate exists (C4a); the delegate's own logs are unique to this attempt.
        $tails = @()
        if ($StreamToHost) {
            foreach ($shared in $SharedProgressLog) { if ($shared) { $tails += (New-AeroLinkGenerationTail -Path $shared -Prefix "      [$(Split-Path -Leaf $shared)] " -Binding (New-AeroLinkLogBinding -Path $shared)) } }
        }
        $delegateOut = Join-Path $paths.Logs 'delegate.stdout.log'
        $delegateErr = Join-Path $paths.Logs 'delegate.stderr.log'
        $spec = New-Object AeroLink.TransitionV1.LaunchSpec
        $powershell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
        $spec.CommandLine = '"' + $powershell + '" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + $DelegateScript + '" -HandoffFile "' + $paths.Handoff + '" -Phase Delegate'
        $spec.StandardOutputPath = $delegateOut; $spec.StandardErrorPath = $delegateErr
        $environment = New-Object 'System.Collections.Generic.Dictionary[string,string]' ([StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in [Environment]::GetEnvironmentVariables([EnvironmentVariableTarget]::Process).GetEnumerator()) { $environment[[string]$entry.Key] = [string]$entry.Value }
        $environment['AEROLINK_TRANSITION_HANDOFF'] = $paths.Handoff
        $spec.Environment = $environment
        # The delegate's creation HANDLE is retained for its whole life: its liveness and exit status come from it.
        $delegate = $K::Launch($job.Handle, $spec)
        try { $K::Resume($delegate) } catch { [void]$K::TerminateChecked($delegate, 5000); throw }
        Write-AeroLinkTransitionEvent -Path $paths.Chain -Record ([ordered]@{ type = 'DelegateStarted'; processId = $delegate.ProcessId; startedAt = $delegate.StartedAtUtc; image = $delegate.ImagePath; at = (Get-AeroLinkUtcNow) })
        if ($StreamToHost) {
            $tails = @((New-AeroLinkGenerationTail -Path $delegateOut -Prefix '      '), (New-AeroLinkGenerationTail -Path $delegateErr -Prefix '      [stderr] ')) + $tails
        }

        $reason = $null; $graceUntil = $null; $lastProgress = [Diagnostics.Stopwatch]::StartNew()
        while ($true) {
            Invoke-AeroLinkAuthorityPump -Spool $paths.Spool -AttemptId $AttemptId -Witness $witness -Qualification $Qualification -QualificationProbe:$QualificationProbe -DieAt $AuthorityDieAt
            foreach ($tail in $tails) { foreach ($line in @($tail.Read())) { Write-Host "$($tail.Prefix)$line" } }
            if ($StreamToHost -and $lastProgress.Elapsed.TotalSeconds -ge 30) {
                Write-Host ("      [transition $AttemptId] running for $([int]((Get-Date) - $startedAt).TotalSeconds)s; $([int][Math]::Max(0, ($deadline - (Get-Date)).TotalSeconds))s of the ${DeadlineSeconds}s budget remaining") -ForegroundColor DarkGray
                $lastProgress.Restart()
            }
            if ($K::Members($job.Handle).Assigned -eq 0) { $reason = 'ChainExited'; break }
            if ((Get-Date) -ge $deadline) { $reason = 'DeadlineExceeded'; break }
            if ($K::WaitHandle($delegate.Process, 0) -eq 0) {
                $delegateRecord = Read-AeroLinkJsonRecord -Path $paths.DelegateOutcome
                if ($delegateRecord.Class -eq 'Absent') { $reason = 'DelegateDiedWithoutOutcome'; break }
                if ($delegateRecord.Class -ne 'Valid') { $reason = 'DelegateOutcomeUnreadable'; break }
                if ([string]$delegateRecord.Value.decision -ne 'Completed') { $reason = 'Delegate' + [string]$delegateRecord.Value.decision; break }
                if (-not $graceUntil) { $graceUntil = (Get-Date).AddSeconds(10) }
                elseif ((Get-Date) -ge $graceUntil) { $reason = 'DescendantsOutlivedDelegate'; break }
            }
            Start-Sleep -Milliseconds 250
        }
        if ($reason -eq 'ChainExited') {
            $delegateRecord = Read-AeroLinkJsonRecord -Path $paths.DelegateOutcome
            if ($delegateRecord.Class -eq 'Absent') { $reason = 'DelegateDiedWithoutOutcome' } elseif ($delegateRecord.Class -ne 'Valid') { $reason = 'DelegateOutcomeUnreadable' }
            elseif ([string]$delegateRecord.Value.decision -ne 'Completed') { $reason = 'Delegate' + [string]$delegateRecord.Value.decision }
            # A request written just before a normal chain exit is legitimate work; past a failure or deadline, no new work.
            if ($reason -eq 'ChainExited') { Invoke-AeroLinkAuthorityPump -Spool $paths.Spool -AttemptId $AttemptId -Witness $witness -Qualification $Qualification -QualificationProbe:$QualificationProbe -DieAt $AuthorityDieAt }
        }

        # Contract seam: a host error raised while the attempt's own work has ended but its job has NOT yet been
        # collected - the one place where the catch's cleanup is the only thing that can produce containment proof.
        if ($Faults['OuterHostErrorAt'] -eq 'AfterChain') { throw 'injected: a host error before the job was collected' }
        $proven = Complete-AeroLinkTransitionJob -Job $job -AttemptRoot $attemptRoot -AttemptId $AttemptId
        $job = $null
        foreach ($tail in $tails) { try { foreach ($line in @($tail.Flush())) { Write-Host "$($tail.Prefix)$line" } } catch { } }
        $receipt = (Read-AeroLinkJsonRecord -Path $paths.Receipt).Value

        $delegateExit = [ordered]@{ pid = $delegate.ProcessId; observation = 'Unknown'; exitCode = $null; terminatedByOuter = $false; detail = '' }
        try {
            if ($Faults['OuterDelegateExitUnobservable']) { throw 'injected: the delegate exit status could not be observed' }
            $w = $K::WaitHandle($delegate.Process, 10000)
            if ($w -eq 0) { $delegateExit.exitCode = $K::ExitCode($delegate); $delegateExit.observation = 'Observed' }
            else { $delegateExit.observation = 'NotExited'; $delegateExit.detail = "wait returned $w" }
        }
        catch { $delegateExit.observation = 'Unknown'; $delegateExit.detail = $_.Exception.Message }
        $delegateExit.terminatedByOuter = [bool]@(@(Get-AeroLinkProperty $receipt 'discoveredBeforeTermination' @()) | Where-Object { [int]$_.processId -eq $delegate.ProcessId -and (ConvertTo-AeroLinkUtcIso $_.startedAt) -eq $delegate.StartedAtUtc }).Count

        # Unaccepted requests from a chain that no longer exists are cancelled through the same exclusive claim.
        $launches = @()
        foreach ($file in @(Get-ChildItem -LiteralPath $paths.Spool -Filter '*.request.json' -File)) {
            $requestId = $file.Name.Substring(0, $file.Name.Length - '.request.json'.Length)
            $null = New-AeroLinkExclusiveRecord -Path (Get-AeroLinkSpoolPaths -Spool $paths.Spool -RequestId $requestId).Owner -Value ([ordered]@{ verdict = 'cancelled'; byPid = $PID; reason = "outer: $reason"; at = (Get-AeroLinkUtcNow) })
            $resolution = Resolve-AeroLinkLaunchRequest -Spool $paths.Spool -RequestId $requestId
            $registered = @((Read-AeroLinkTransitionEvents -Path (Get-AeroLinkSpoolPaths -Spool $paths.Spool -RequestId $requestId).Events).Events | Where-Object { $_.type -eq 'Registered' })
            $launches += [ordered]@{ requestId = $requestId; role = [string](Get-AeroLinkProperty (Read-AeroLinkJsonRecord -Path $file.FullName).Value 'role' ''); outcome = $resolution.Outcome
                currentHealth = $resolution.CurrentHealth; restored = $resolution.Restored; blocking = $resolution.Blocking; processId = $resolution.ProcessId; detail = $resolution.Detail
                startedAt = $(if ($registered.Count) { ConvertTo-AeroLinkUtcIso $registered[0].startedAt } else { '' }); image = $(if ($registered.Count) { [string]$registered[0].image } else { '' }) }
        }
        Stop-AeroLinkTransitionWitness -Witness $witness
        $witness = $null

        $delegateCheck = Test-AeroLinkActorOutcome -Path $paths.DelegateOutcome -Role delegate
        $continuationCheck = Test-AeroLinkActorOutcome -Path $paths.ContinuationOutcome -Role continuation
        $accepted = Test-Path -LiteralPath $paths.DelegateAccepted
        $blocking = @($launches | Where-Object { $_.blocking }).Count -gt 0
        $failures = [System.Collections.Generic.List[string]]::new()
        $add = { param([string]$Code) if ($Code -and -not $failures.Contains($Code)) { $failures.Add($Code) } }
        # Contract seam: a host error raised where the outer is verifying what it was told, not where a child failed.
        if ($Faults['OuterHostErrorAt'] -eq 'Verification') { throw 'injected: a host error during required-role verification' }

        # (a) Every REQUIRED role, verified by the outer - never taken from a child's word. A role is restored by a
        # launch this attempt registered and that is ready NOW, or (when no launch was required) by the exact
        # instance already running and ready now.
        $roleResults = @()
        foreach ($required in @($RequiredRoles)) {
            $role = [string]$required.role
            $mine = @($launches | Where-Object { $_.role -eq $role })
            $restored = $false; $evidence = ''
            $outerReadiness = Get-AeroLinkProperty $required 'readiness' $null
            # A scriptblock requirement is MODULE-BOUND and takes the requirement itself as its parameter: the
            # caller builds it in the remote-demo module, and this chain executes it from here, where that module's
            # own commands are not visible. Passing the requirement object is what keeps the callback free of
            # captured locals (a closure would lose the module's command scope entirely).
            if ($outerReadiness -is [scriptblock]) { try { $outerReadiness = & $outerReadiness $required } catch { $outerReadiness = $null; $evidence = "the required readiness could not be computed: $($_.Exception.Message)" } }
            $succeeded = @($mine | Where-Object { $_.outcome -eq 'Succeeded' -and $_.currentHealth -eq 'Running' })
            if ($succeeded.Count) {
                $check = Test-AeroLinkRestorationDischarge -Spool $paths.Spool -RequestId ([string]$succeeded[-1].requestId)
                $restored = $check.Discharged; $evidence = $check.Detail
                # The requester's readiness is necessary, not sufficient: the outer re-checks the registered instance
                # against what IT requires now (for the API, the source identity actually on disk).
                if ($restored -and $outerReadiness -and [string](Get-AeroLinkProperty $outerReadiness 'kind' '') -ne 'marker') {
                    $own = Test-AeroLinkRoleReadiness -Readiness $outerReadiness -ProcessId ([int]$succeeded[-1].processId)
                    $restored = $own.Ready; $evidence = "$evidence; outer: $($own.Evidence)"
                }
            }
            elseif (-not [bool](Get-AeroLinkProperty $required 'launchRequired' $false) -and (Get-AeroLinkProperty $required 'discover' $null)) {
                $discovered = & $required.discover $required
                if ($discovered -and $discovered.ProcessId -and $outerReadiness) {
                    $check = Test-AeroLinkRoleReadiness -Readiness $outerReadiness -ProcessId ([int]$discovered.ProcessId)
                    $restored = $check.Ready; $evidence = "existing instance: $($check.Evidence)"
                }
                elseif (-not $evidence) { $evidence = 'no existing instance was found' }
            }
            else { $evidence = if ($mine.Count) { "launch $($mine[-1].outcome)/$($mine[-1].currentHealth): $($mine[-1].detail)" } else { 'no launch was requested' } }
            $last = if ($mine.Count) { $mine[-1] } else { $null }
            $roleResults += [ordered]@{ role = $role; requested = [bool]$mine.Count; restored = [bool]$restored; evidence = $evidence
                outcome = $(if ($succeeded.Count) { 'Succeeded' } elseif ($last) { $last.outcome } else { 'NotRequested' }) }
            if ($accepted -and -not $restored) {
                if (-not $mine.Count -and [bool](Get-AeroLinkProperty $required 'launchRequired' $false)) { & $add "RoleNotRequested:$role" }
                else { & $add "RoleNotRestored:${role}:$(if ($last) { "$($last.outcome)/$($last.currentHealth)" } else { 'NotRunning' })" }
            }
        }
        $restoredAll = (@($roleResults | Where-Object { -not $_.restored }).Count -eq 0)

        # (b) The actors' results against the contract.
        $handoffRefused = $delegateCheck.Class -eq 'Valid' -and $delegateCheck.Decision -in @('ProtocolIncompatible', 'SourceVersionMismatch', 'Unreadable') -and -not $accepted
        if (-not $handoffRefused -and $reason -ne 'DeadlineExceeded') {
            if ($reason -eq 'DelegateDiedWithoutOutcome') { & $add 'DelegateDiedWithoutOutcome' }
            if ($reason -eq 'DescendantsOutlivedDelegate') { & $add 'DescendantsOutlivedDelegate' }
            switch ($delegateCheck.Class) {
                'Absent' { & $add 'DelegateOutcomeMissing' }
                'Valid' {
                    if ($delegateCheck.Decision -ne 'Completed') {
                        if ($delegateCheck.Failures.Count) { foreach ($f in $delegateCheck.Failures) { & $add $(if (Test-AeroLinkRoleFailureCode $f) { $f } else { "delegate/$f" }) } }
                        else { & $add "Delegate:$($delegateCheck.Decision)" }
                    }
                }
                default { & $add "DelegateOutcome$($delegateCheck.Class): $($delegateCheck.Detail)" }
            }
            # A continuation outcome is required exactly when the delegate started one (it records the request first).
            if ($accepted -and (Test-Path -LiteralPath (Join-Path $attemptRoot 'continuation-request.json'))) {
                switch ($continuationCheck.Class) {
                    'Absent' { & $add 'ContinuationOutcomeMissing' }
                    'Valid' { if ($continuationCheck.Decision -ne 'Completed' -and -not $continuationCheck.Failures.Count) { & $add "Continuation:$($continuationCheck.Decision)" } }
                    default { & $add "ContinuationOutcome$($continuationCheck.Class): $($continuationCheck.Detail)" }
                }
            }
            if ($delegateCheck.Class -eq 'Valid' -and $delegateCheck.Decision -eq 'Completed' -and -not $restoredAll) { & $add 'DelegateOutcomeContradicted' }
            if ($continuationCheck.Class -eq 'Valid' -and $continuationCheck.Decision -eq 'Completed' -and -not $restoredAll) { & $add 'ContinuationOutcomeContradicted' }
        }
        # (c) The delegate's durable outcome must agree with how it ACTUALLY exited.
        if ($reason -ne 'DeadlineExceeded') {
            if ($delegateExit.terminatedByOuter) { & $add 'DelegateTerminatedByOuter' }
            elseif ($delegateExit.observation -ne 'Observed') { & $add "DelegateExitUnknown:$($delegateExit.observation)" }
            else { $mismatch = Test-AeroLinkActorExitMatchesOutcome -ExitCode $delegateExit.exitCode -Outcome $delegateCheck -Actor 'Delegate'; if ($mismatch) { & $add $mismatch } }
        }
        if ($blocking) { foreach ($l in @($launches | Where-Object { $_.blocking })) { & $add "LaunchUnresolved:$($l.requestId)" } }
        if (-not $proven) { & $add 'ContainmentUnproven' }

        $roleFailures = @($failures | Where-Object { Test-AeroLinkRoleFailureCode $_ })
        $chainFailures = @($failures | Where-Object { -not (Test-AeroLinkRoleFailureCode $_) })
        $decision = if ($reason -eq 'DeadlineExceeded') { 'DeadlineExceeded' }
            elseif ($handoffRefused -and $chainFailures.Count -eq 0) { 'Refused' }
            elseif ($chainFailures.Count) { 'ChainFailed' }
            elseif ($roleFailures.Count) { 'RestorationFailed' }
            else { 'Completed' }
        $exitCode = switch ($decision) { 'Completed' { 0 } 'DeadlineExceeded' { 23 } 'ChainFailed' { 24 } 'Refused' { 25 } 'RestorationFailed' { 26 } }
        $restorationRequired = [bool]($accepted -and -not $restoredAll)
        $self = Test-AeroLinkAttemptResolved -AttemptRoot $attemptRoot -AttemptId $AttemptId
        $result = & $emit ([ordered]@{ decision = $decision; stage = $(if ($decision -eq 'Refused') { 'handoff' } else { 'chain' }); reason = $reason
                detail = $(if ($failures.Count) { $failures -join '; ' } else { "the transition completed; $($roleResults.Count) required role(s) verified by the outer" })
                failures = @($failures); chainFailures = $chainFailures; roleFailures = $roleFailures
                operation = [ordered]@{ succeeded = ($decision -eq 'Completed'); roles = $roleResults; restorationRequired = $restorationRequired }
                cleanup = [ordered]@{ transitionContainmentProven = $proven; launchesBlocking = $blocking; collected = @(Get-AeroLinkProperty $receipt 'discoveredBeforeTermination' @()) }
                recovery = [ordered]@{ admissible = $self.Resolved; quiescence = $self.Quiescence; problems = @($self.Problems) }
                mutationStarted = $accepted; restored = $restoredAll; restorationRequired = $restorationRequired
                launches = $launches; delegate = $delegateCheck.Value; delegateOutcomeClass = $delegateCheck.Class; delegateExit = $delegateExit
                continuation = $continuationCheck.Value; continuationOutcomeClass = $continuationCheck.Class
                protocolVersion = $ProtocolVersion; descriptorHash = $Qualification.DescriptorHash; plan = $Plan }) $exitCode
        if ($Faults['OuterFinalizationFault']) { throw 'injected: outer finalization failed after the outcome was published' }
        return $result
    }
    catch {
        $primary = $_
        $message = $primary.Exception.Message
        try { [IO.File]::AppendAllText((Join-Path $attemptRoot 'outer-error.log'), ($primary | Out-String) + $primary.ScriptStackTrace + "`r`n") } catch { }
        $previous = (Read-AeroLinkJsonRecord -Path $paths.Outcome).Value
        if ($script:published -and $previous) {
            # Published, then finalization failed: the durable result must not keep claiming a decision this outer
            # will not return. The verdicts that were already established (cleanup proof, admission decision, the
            # durable obligation and the attempts taken) are carried forward unchanged - they were computed from
            # evidence that this failure did not invalidate - and the primary error is preserved in 'detail'.
            return & $emit ([ordered]@{ decision = 'HostError'; stage = 'finalization'; detail = "finalization failed after the attempt result was published: $message"; primaryError = $message
                    publishedDecision = [string](Get-AeroLinkProperty $previous 'decision' '')
                    mutationStarted = [bool](Get-AeroLinkProperty $previous 'mutationStarted' $false); restorationRequired = [bool](Get-AeroLinkProperty $previous 'restorationRequired' $true)
                    recovery = (Get-AeroLinkProperty $previous 'recovery' $null); cleanup = (Get-AeroLinkProperty $previous 'cleanup' $null)
                    operation = (Get-AeroLinkProperty $previous 'operation' $null); launches = (Get-AeroLinkProperty $previous 'launches' $null)
                    published = $previous }) 1
        }
        # ---- A host error BEFORE any result was published. Finish the attempt's cleanup HERE, then publish the
        # same attempt-bound evidence a normal completion carries. A host error must neither suppress safely
        # admissible recovery (a proven-quiescent attempt with resolved launches) nor authorize recovery on an
        # attempt whose termination is Unknown: admissibility comes only from the durable records, after cleanup.
        $cleanupErrors = [System.Collections.Generic.List[string]]::new()
        $containment = $null
        $finalizedByCatch = $true
        if ($job) {
            try {
                # Contract seam: a cleanup that cannot complete must be RECORDED, never retried behind the
                # published verdict (the finally closes the handle without claiming a new observation).
                if ($Faults['OuterCleanupFault']) { throw 'injected: the outer could not complete the transition job' }
                $containment = [bool](Complete-AeroLinkTransitionJob -Job $job -AttemptRoot $attemptRoot -AttemptId $AttemptId); $job = $null
            }
            catch { $cleanupErrors.Add("the transition job could not be completed: $($_.Exception.Message)") }
        }
        if ($witness) { try { Stop-AeroLinkTransitionWitness -Witness $witness; $witness = $null } catch { $cleanupErrors.Add("the completion witness could not be released: $($_.Exception.Message)") } }
        if ($delegate) { try { $K::Close($delegate); $delegate = $null } catch { $cleanupErrors.Add("the delegate handle could not be closed: $($_.Exception.Message)") } }
        $receipt = Read-AeroLinkJsonRecord -Path $paths.Receipt
        if ($null -eq $containment -and $receipt.Class -eq 'Valid') { $containment = [bool](Get-AeroLinkProperty $receipt.Value 'containmentProven' $false) }
        if ($null -eq $containment) { $containment = $false }
        $mutationStarted = (Test-Path -LiteralPath $paths.DelegateAccepted)
        $self = $null
        try { $self = Test-AeroLinkAttemptResolved -AttemptRoot $attemptRoot -AttemptId $AttemptId }
        catch { $cleanupErrors.Add("admission evidence could not be computed: $($_.Exception.Message)") }
        # Always an array: under StrictMode a single-element pipeline result is a scalar, and `.Count` on a
        # scalar is itself an error in Windows PowerShell 5.1.
        $problems = @()
        if ($self) { $problems = @($self.Problems) }
        if ($problems.Count -eq 0 -and -not ($self -and $self.Resolved)) { $problems = @('the attempt could not be proven resolved after the host error') }
        if ($cleanupErrors.Count) { $problems = @($problems) + @($cleanupErrors) }
        return & $emit ([ordered]@{ decision = 'HostError'; stage = 'host'; detail = $message; primaryError = $message; cleanupErrors = @($cleanupErrors)
                # Mutation began and no verdict about the required roles was ever reached, so the obligation is
                # retained - a host error here cannot assert that any role is restored. Recovery still has to earn
                # admissibility from the durable records above; this flag only says what is owed, not what is safe.
                mutationStarted = $mutationStarted; restorationRequired = $mutationStarted
                cleanup = [ordered]@{ transitionContainmentProven = $containment; receiptClass = $receipt.Class
                    collected = @(Get-AeroLinkProperty $receipt.Value 'discoveredBeforeTermination' @()); errors = @($cleanupErrors) }
                recovery = [ordered]@{ admissible = [bool]($self -and $self.Resolved); quiescence = $(if ($self) { $self.Quiescence } else { 'Unknown' }); problems = @($problems) }
                attempts = @([ordered]@{ attemptId = $AttemptId; decision = 'HostError'; stage = 'host' }) }) 1
    }
    finally {
        # A host error while this outer is still alive: it completes the job itself - terminate, observe zero through
        # its held handle, publish the receipt - so the next admission has proof rather than a witness-only answer.
        # When the catch already attempted that completion, the finally only closes the handle: a second attempt
        # must not publish a receipt behind a verdict that was already saying containment was not proven.
        if ($job -and -not $finalizedByCatch) {
            try { $null = Complete-AeroLinkTransitionJob -Job $job -AttemptRoot $attemptRoot -AttemptId $AttemptId } catch { }
        }
        elseif ($job) { try { $K::CloseHandleChecked($job.Handle) } catch { } }
        if ($witness) { try { Stop-AeroLinkTransitionWitness -Witness $witness } catch { } }
        if ($delegate) { $K::Close($delegate) }
        $script:published = $false
    }
}

# ==================================================================================================================
# Streaming (C4a / C4b)
# ==================================================================================================================

function New-AeroLinkGenerationTail {
    <#
      A stateful tail over a log another process is writing. One decoder and a partial-line buffer live for the whole
      wait (a UTF-8 character split across polls is not destroyed; a half-written line is not emitted in pieces).
      A log created by this attempt has no previous content, so its tail starts at 0 whenever it is attached - launch
      and attach ordering stops mattering (C4a). A SHARED log is bound before launch, and a new generation is detected
      three ways: a different creation time (replaced), a length below the position (truncated), or changed bytes
      immediately before the position (rewritten in place, including to the same length - C4b).
    #>
    param([Parameter(Mandatory)][string]$Path, [string]$Prefix = '', $Binding = $null)
    $tail = [pscustomobject]@{
        Path = $Path; Prefix = $Prefix
        Position = $(if ($Binding -and $Binding.Exists) { [long]$Binding.Length } else { [long]0 })
        CreationTicks = $(if ($Binding -and $Binding.Exists) { [long]$Binding.CreationTicks } else { [long]0 })
        Fingerprint = $(if ($Binding -and $Binding.Exists) { [byte[]]$Binding.Fingerprint } else { [byte[]]@() })
        Decoder = [Text.Encoding]::UTF8.GetDecoder(); Pending = ''
        Generations = 0; Reasons = [System.Collections.Generic.List[string]]::new()
    }
    $tail | Add-Member -MemberType ScriptMethod -Name Reset -Value {
        param([string]$Why)
        $this.Position = [long]0; $this.Fingerprint = [byte[]]@(); $this.Decoder = [Text.Encoding]::UTF8.GetDecoder(); $this.Pending = ''
        $this.Generations = $this.Generations + 1; $this.Reasons.Add($Why)
    }
    $tail | Add-Member -MemberType ScriptMethod -Name ReadChunk -Value {
        if (-not (Test-Path -LiteralPath $this.Path -PathType Leaf)) { return $false }
        $fs = $null
        try { $fs = [IO.File]::Open($this.Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete) } catch { return $false }
        try {
            $creation = (Get-Item -LiteralPath $this.Path).CreationTimeUtc.Ticks
            if ($this.CreationTicks -and $creation -ne $this.CreationTicks) { $this.Reset('replaced') }
            elseif ($fs.Length -lt $this.Position) { $this.Reset('truncated') }
            elseif ($this.Position -gt 0 -and $this.Fingerprint.Length -gt 0) {
                $k = $this.Fingerprint.Length
                $now = New-Object byte[] $k
                [void]$fs.Seek($this.Position - $k, [IO.SeekOrigin]::Begin)
                $read = 0; while ($read -lt $k) { $n = $fs.Read($now, $read, $k - $read); if ($n -le 0) { break }; $read += $n }
                if ($read -ne $k -or [Convert]::ToBase64String($now) -ne [Convert]::ToBase64String([byte[]]$this.Fingerprint)) { $this.Reset('rewritten') }
            }
            $this.CreationTicks = $creation
            if ($fs.Length -le $this.Position) { return $false }
            [void]$fs.Seek($this.Position, [IO.SeekOrigin]::Begin)
            $count = [int][Math]::Min([long]65536, $fs.Length - $this.Position)
            $buffer = New-Object byte[] $count
            $got = $fs.Read($buffer, 0, $count)
            if ($got -le 0) { return $false }
            $this.Position = $this.Position + $got
            [byte[]]$old = if ($this.Fingerprint) { [byte[]]$this.Fingerprint } else { New-Object byte[] 0 }
            $combined = New-Object byte[] ($old.Length + $got)
            [Array]::Copy($old, 0, $combined, 0, $old.Length)
            [Array]::Copy($buffer, 0, $combined, $old.Length, $got)
            $keep = [int][Math]::Min(4096, $combined.Length)
            $fingerprint = New-Object byte[] $keep
            [Array]::Copy($combined, $combined.Length - $keep, $fingerprint, 0, $keep)
            $this.Fingerprint = $fingerprint
            $chars = New-Object char[] ($this.Decoder.GetCharCount($buffer, 0, $got))
            $decoded = $this.Decoder.GetChars($buffer, 0, $got, $chars, 0)
            $this.Pending = $this.Pending + (New-Object string($chars, 0, $decoded))
            return $true
        }
        catch { return $false }
        finally { $fs.Dispose() }
    }
    $tail | Add-Member -MemberType ScriptMethod -Name TakeLines -Value {
        $lines = @()
        while ($true) {
            $i = $this.Pending.IndexOf("`n")
            if ($i -lt 0) { break }
            $lines += $this.Pending.Substring(0, $i).TrimEnd("`r")
            $this.Pending = $this.Pending.Substring($i + 1)
        }
        return $lines
    }
    $tail | Add-Member -MemberType ScriptMethod -Name Read -Value { [void]$this.ReadChunk(); return $this.TakeLines() }
    $tail | Add-Member -MemberType ScriptMethod -Name Flush -Value {
        # Bounded by a length snapshot and a chunk ceiling: drains what exists now, never waits for EOF.
        $snapshot = [long]0
        try { $snapshot = (Get-Item -LiteralPath $this.Path).Length } catch { }
        $lines = @(); $chunks = 0
        while ($this.Position -lt $snapshot -and $chunks -lt 4096) { if (-not $this.ReadChunk()) { break }; $chunks++; $lines += $this.TakeLines() }
        $lines += $this.TakeLines()
        if ($this.Pending) { $lines += $this.Pending.TrimEnd("`r"); $this.Pending = '' }
        return $lines
    }
    return $tail
}

Export-ModuleMember -Function Get-AeroLinkTransitionStateRoot, Get-AeroLinkAttemptIndexPath, New-AeroLinkAttemptId, Get-AeroLinkAttemptPaths, `
    Start-AeroLinkTransitionWitness, Register-AeroLinkWitnessJob, Stop-AeroLinkTransitionWitness, Get-AeroLinkWitnessState, `
    New-AeroLinkTransitionJob, Complete-AeroLinkTransitionJob, Test-AeroLinkCleanupReceipt, Get-AeroLinkTransitionQuiescence, `
    Test-AeroLinkAttemptResolved, Test-AeroLinkInstallationAdmission, Add-AeroLinkAttemptToIndex, `
    Get-AeroLinkSpoolPaths, Resolve-AeroLinkLaunchRequest, Invoke-AeroLinkAuthorityPump, Test-AeroLinkRestorationDischarge, `
    Test-AeroLinkRoleReadiness, Test-AeroLinkTunnelProtection, Get-AeroLinkPostgresInstance, Get-AeroLinkListenerOwners, Stop-AeroLinkStagingJob, `
    Get-AeroLinkTransitionHandoffFromEnvironment, Request-AeroLinkServiceLaunch, New-AeroLinkLaunchResolution, `
    Get-AeroLinkLaunchContextDescriptor, Get-AeroLinkTaskDefinitionCanonical, Test-AeroLinkLaunchContextQualification, `
    Write-AeroLinkLaunchContextQualification, Get-AeroLinkQualificationPath, `
    Read-AeroLinkTransitionHandoff, Test-AeroLinkRoleFailureCode, Test-AeroLinkActorOutcome, Test-AeroLinkActorExitMatchesOutcome, `
    Invoke-AeroLinkTransitionChain, New-AeroLinkLogBinding, New-AeroLinkGenerationTail
