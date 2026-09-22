#Requires -Version 5.1
param(
    [Parameter(Mandatory)][string]$Dir,
    [Parameter(Mandatory)][int]$OwnerPid,
    [Parameter(Mandatory)][string]$OwnerStartedAt,
    [Parameter(Mandatory)][string]$OwnerImage,
    [int]$CollectTimeoutSeconds = 30,
    # Contract-suite fault seams only: OpenFailure | WaitFailure | WaitFailure+ClassifyUnknown
    [string]$FaultInjection = ''
)
<#
    The completion witness of one HOME transition.

    Why it exists. A job's NAME disappears when its last HANDLE closes, even while members still run; kill-on-close
    only INITIATES termination at that moment; and an empty job can still gain members through a full handle. So
    after an owner dies, "the name is gone" proves only that a kill was initiated. Completion is observable only by
    someone who still HOLDS a handle. The witness is that someone: it holds duplicated full handles to every job its
    owner registers, and when the owner has PROVABLY exited it

      * reads each staging job's kill-on-close flag from the kernel - cleared means COMMITTED (a launched service
        whose ownership transfer took effect; left running);
      * terminates every other job and observes, through its own handle, a membership of ZERO;
      * publishes that observation atomically as a receipt, and only then closes its handles.

    The owner's exit must be PROVEN: a signaled owner handle, or affirmative exit evidence by exact identity. A failed
    wait is not death - the witness keeps its handles and publishes no receipt, so resolvers see a live witness
    (Pending) rather than a false completion. If the witness dies too, no receipt exists and the answer is Unknown.
#>
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'AeroLinkTransitionKernel.psm1') -DisableNameChecking
$K = [AeroLink.TransitionV2.Kernel]
$faults = @($FaultInjection -split '\+' | Where-Object { $_ })

# A liveness PROBE holds this file for an instant; retry briefly rather than mistake a probe for a rival.
$lock = $null
for ($i = 0; $i -lt 100; $i++) { $lock = Enter-AeroLinkTransitionLock -Path (Join-Path $Dir 'witness.lock'); if ($lock.State -eq 'Held') { break }; Start-Sleep -Milliseconds 50 }
if ($lock.State -ne 'Held') { exit 3 }
$jobsPath = Join-Path $Dir 'jobs.jsonl'
$acksPath = Join-Path $Dir 'acks.jsonl'
$known = @{}

$owner = [IntPtr]::Zero
$startupFailure = $null
try {
    if ($faults -contains 'OpenFailure') { throw 'injected: OpenProcess on the owner failed' }
    $owner = $K::OpenProcessWait($OwnerPid, $OwnerStartedAt)
    if ($owner -eq [IntPtr]::Zero) { $startupFailure = "pid $OwnerPid does not have the recorded owner start time $OwnerStartedAt" }
    elseif ($K::Classify($OwnerPid, $OwnerStartedAt, $OwnerImage) -ne 'RunningMatch') { $startupFailure = "owner pid $OwnerPid is not running with its recorded identity" }
}
catch { $startupFailure = "owner monitoring could not be established: $($_.Exception.Message)" }
if ($startupFailure) {
    Publish-AeroLinkJsonAtomic -Path (Join-Path $Dir 'startup-failed.json') -Value ([ordered]@{ pid = $PID; ownerPid = $OwnerPid; reason = $startupFailure; at = (Get-Date).ToUniversalTime().ToString('o') })
    $K::CloseHandleChecked($owner); $lock.Stream.Dispose()
    exit 4
}
Publish-AeroLinkJsonAtomic -Path (Join-Path $Dir 'ready.json') -Value ([ordered]@{ pid = $PID; ownerPid = $OwnerPid; ownerStartedAt = $OwnerStartedAt; ownerMonitored = $true })

function Sync-WitnessJobs {
    $read = Read-AeroLinkTransitionEvents -Path $jobsPath
    foreach ($entry in $read.Events) {
        $name = [string]$entry.jobName
        if ($known.ContainsKey($name)) { continue }
        $known[$name] = [pscustomobject]@{ Name = $name; Handle = [IntPtr][long]$entry.handle; Kind = [string]$entry.kind
            ReceiptPath = [string]$entry.receiptPath; AttemptId = [string](Get-AeroLinkProperty $entry 'attemptId' '') }
        Write-AeroLinkTransitionEvent -Path $acksPath -Record ([ordered]@{ jobName = $name; at = (Get-Date).ToUniversalTime().ToString('o') })
    }
}
function Get-WitnessIdentity([int]$ProcessId) {
    $identity = Get-AeroLinkProcessIdentityRecord -ProcessId $ProcessId
    if ($identity) { return [ordered]@{ processId = $ProcessId; startedAt = $identity.StartedAtUtc; image = $identity.ImagePath } }
    return [ordered]@{ processId = $ProcessId; startedAt = ''; image = '' }
}
function Get-OwnerExitEvidence {
    # '' = still running; 'signaled' / 'exit-evidence' = proven exited; 'Unknown:<why>' = cannot tell.
    $wait = if ($faults -contains 'WaitFailure' -and $known.Count -gt 0) { [uint32]4294967295 } else { $K::WaitHandle($owner, 150) }
    if ($wait -eq 258) { return '' }
    if ($wait -eq 0) { return 'signaled' }
    $classified = if ($faults -contains 'ClassifyUnknown') { 'Unknown:injected' } else { $K::Classify($OwnerPid, $OwnerStartedAt, $OwnerImage) }
    if ($classified -eq 'Gone' -or $classified -eq 'RunningDifferent') { return 'exit-evidence' }
    Start-Sleep -Milliseconds 150
    if ($classified -eq 'RunningMatch') { return '' }
    return "Unknown:$classified"
}

$released = $false
$unknownPublished = $false
$exitEvidence = ''
try {
    while ($true) {
        Sync-WitnessJobs
        if (Test-Path -LiteralPath (Join-Path $Dir 'release.json')) { $released = $true; break }
        $evidence = Get-OwnerExitEvidence
        if ($evidence -eq '') { continue }
        if ($evidence -like 'Unknown:*') {
            if ($unknownPublished) { continue }
            $unknownPublished = $true
            Publish-AeroLinkJsonAtomic -Path (Join-Path $Dir 'owner-state-unknown.json') -Value ([ordered]@{ pid = $PID; ownerPid = $OwnerPid; detail = $evidence; at = (Get-Date).ToUniversalTime().ToString('o') })
            continue
        }
        $exitEvidence = $evidence
        break
    }
    if ($released) { return }
    Sync-WitnessJobs
    foreach ($job in $known.Values) {
        $receipt = [ordered]@{ schemaVersion = 1; jobName = $job.Name; kind = $job.Kind; attemptId = $job.AttemptId; observedBy = 'witness'; witnessPid = $PID; ownerExitEvidence = $exitEvidence }
        try {
            $flags = $K::LimitFlags($job.Handle)
            if ($job.Kind -eq 'staging' -and (($flags -band $K::LimitKillOnClose) -eq 0)) {
                $receipt.verdict = 'Committed'
                $receipt.limitFlags = $flags
            }
            else {
                $before = $K::Members($job.Handle)
                $receipt.discoveredBeforeTermination = @($before.ProcessIds | ForEach-Object { Get-WitnessIdentity $_ })
                $K::Terminate($job.Handle)
                $deadline = (Get-Date).AddSeconds($CollectTimeoutSeconds)
                $after = $K::Members($job.Handle)
                while ($after.Assigned -ne 0 -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 50; $after = $K::Members($job.Handle) }
                $receipt.verdict = if ($after.Assigned -eq 0) { 'Collected' } else { 'CollectionUnconfirmed' }
                $receipt.containmentProven = ($after.Assigned -eq 0)
                $receipt.activeProcessesAfterTerminate = [int]$after.Assigned
            }
        }
        catch {
            $receipt.verdict = 'CollectionUnconfirmed'; $receipt.containmentProven = $false
            $receipt.activeProcessesAfterTerminate = -1; $receipt.error = $_.Exception.Message
        }
        $receipt.at = (Get-Date).ToUniversalTime().ToString('o')
        # Published BEFORE this handle closes, so the zero it reports was observed while held.
        Publish-AeroLinkJsonAtomic -Path $job.ReceiptPath -Value $receipt
    }
}
finally {
    foreach ($job in $known.Values) { $K::CloseHandleChecked($job.Handle) }
    $K::CloseHandleChecked($owner)
    $lock.Stream.Dispose()
}
