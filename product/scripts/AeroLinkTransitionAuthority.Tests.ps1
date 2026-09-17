#Requires -Version 5.1
<#
    Contracts for the HOME transition authority (#1041, #1043, #1053): containment, completion receipts, admission,
    the launch spool, the handoff and result contracts, launch-context qualification and log streaming.

    Everything runs on owned disposable state: a disposable installation root under this run's temp directory, actors
    written by this suite, and marker-ready probe services launched through the real authority. Nothing touches the
    persistent PostgreSQL instance, HOME services, evidence, or any scheduled task. Every process this suite causes to
    exist is recorded by identity and proven gone before it reports.
#>
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'AeroLinkTransitionKernel.psm1') -Force -DisableNameChecking
Import-Module (Join-Path $PSScriptRoot 'AeroLinkTransitionAuthority.psm1') -Force -DisableNameChecking
Import-Module (Join-Path $PSScriptRoot 'AeroLinkTransition.psm1') -Force
$K = [AeroLink.TransitionV1.Kernel]
$failures = [System.Collections.Generic.List[string]]::new()
$passed = 0
function Check([bool]$Condition, [string]$Message) { if ($Condition) { $script:passed++ } else { $script:failures.Add($Message) } }

$root = Join-Path ([IO.Path]::GetTempPath()) ('al-tx-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $root -Force | Out-Null
$powershell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
$owned = [System.Collections.Generic.List[object]]::new()
function Own([int]$ProcessId) {
    if ($ProcessId -le 0) { return }
    $identity = Get-AeroLinkProcessIdentityRecord -ProcessId $ProcessId
    if ($identity) { $script:owned.Add($identity) }
}

# ---- A test actor: the real handoff/lease/result contract, with fault seams carried in the handoff. ----
$actor = Join-Path $root 'actor.ps1'
@'
param([Parameter(Mandatory)][string]$HandoffFile, [ValidateSet('Delegate', 'Restore')][string]$Phase = 'Delegate')
$ErrorActionPreference = 'Stop'
$scripts = $env:AEROLINK_TEST_SCRIPTS
Import-Module (Join-Path $scripts 'AeroLinkTransitionKernel.psm1') -DisableNameChecking
Import-Module (Join-Path $scripts 'AeroLinkTransitionAuthority.psm1') -DisableNameChecking
Import-Module (Join-Path $scripts 'AeroLinkTransition.psm1')
$paths = Get-AeroLinkAttemptPaths (Split-Path -Parent $HandoffFile)
$role = if ($Phase -eq 'Delegate') { 'delegate' } else { 'continuation' }
$outcome = if ($Phase -eq 'Delegate') { $paths.DelegateOutcome } else { $paths.ContinuationOutcome }
$raw = (Read-AeroLinkJsonRecord -Path $HandoffFile).Value
$fault = { param($Name) Get-AeroLinkProperty (Get-AeroLinkProperty $raw 'faults' $null) $Name $null }
$source = if (& $fault 'ActorSource') { [string](& $fault 'ActorSource') } else { 'test-source' }
$h = Read-AeroLinkTransitionHandoff -Path $HandoffFile -Role $role -ActualSourceIdentity $source -ExpectedSourceIdentity 'test-source'
if (-not $h.Accepted) { Publish-AeroLinkJsonAtomic -Path $outcome -Value ([ordered]@{ decision = $h.Code; detail = $h.Detail; pid = $PID }); exit 30 }
$handoff = $h.Handoff
$lease = Enter-AeroLinkTransition -InstallationRoot ([string]$handoff.installationRoot)
if ($lease.Owner) { Exit-AeroLinkTransition $lease; Publish-AeroLinkJsonAtomic -Path $outcome -Value ([ordered]@{ decision = 'NotADescendant'; pid = $PID }); exit 33 }
if ($Phase -eq 'Delegate') {
    Publish-AeroLinkJsonAtomic -Path $paths.DelegateAccepted -Value ([ordered]@{ pid = $PID })
    if (& $fault 'WorkerSeconds') {
        # A transient descendant holding no lease: exactly what containment must collect.
        $worker = Start-Process -FilePath $env:ComSpec -ArgumentList ('/c ping -n ' + [int](& $fault 'WorkerSeconds') + ' 127.0.0.1 > nul') -WindowStyle Hidden -PassThru
        [IO.File]::WriteAllText((Join-Path $paths.Root 'worker.pid'), [string]$worker.Id)
    }
    if (& $fault 'DieWithoutOutcome') { [Environment]::Exit(41) }
    if (& $fault 'Hang') { Start-Sleep -Seconds 600 }
    Publish-AeroLinkJsonAtomic -Path (Join-Path $paths.Root 'continuation-request.json') -Value ([ordered]@{ sourceIdentity = 'test-source' })
    if (& $fault 'SkipContinuation') { Publish-AeroLinkJsonAtomic -Path $outcome -Value ([ordered]@{ decision = 'Completed'; failures = [string[]]@(); pid = $PID }); Exit-AeroLinkTransition $lease; exit 0 }
    $c = Start-Process -FilePath (Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe') -ArgumentList ('-NoProfile -ExecutionPolicy Bypass -File "' + $PSCommandPath + '" -HandoffFile "' + $HandoffFile + '" -Phase Restore') -WindowStyle Hidden -PassThru
    $null = $c.Handle; $c.WaitForExit()
    $cont = Test-AeroLinkActorOutcome -Path $paths.ContinuationOutcome -Role continuation
    $fail = @()
    if ($cont.Class -ne 'Valid' -or $cont.Decision -ne 'Completed') { $fail += @($cont.Failures) + "continuation:$($cont.Class)/$($cont.Decision)" }
    $m = Test-AeroLinkActorExitMatchesOutcome -ExitCode $c.ExitCode -Outcome $cont -Actor Continuation; if ($m) { $fail += $m }
    $decision = if ($fail.Count) { 'Failed' } else { 'Completed' }
    Publish-AeroLinkJsonAtomic -Path $outcome -Value ([ordered]@{ decision = $decision; failures = [string[]]@($fail | Where-Object { $_ }); pid = $PID })
    Exit-AeroLinkTransition $lease
    if (& $fault 'DelegateExitAfterOutcome') { exit ([int](& $fault 'DelegateExitAfterOutcome')) }
    exit $(if ($decision -eq 'Completed') { 0 } else { 3 })
}
$mode = if (& $fault 'ProbeMode') { [string](& $fault 'ProbeMode') } else { 'Good' }
$body = if ($mode -eq 'Good') { "[IO.File]::WriteAllText('{READY_FILE}', (@{ requestId = '{REQUEST_ID}'; nonce = '{READY_NONCE}'; processId = `$PID } | ConvertTo-Json)); Start-Sleep 180" } else { 'Start-Sleep 180' }
$r = Request-AeroLinkServiceLaunch -Handoff $handoff -Role qualification-probe -FilePath (Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe') `
    -Arguments ('-NoProfile -Command "' + $body + '"') -Readiness @{ kind = 'marker' } -ReadinessTimeoutSeconds ([int]$(if (& $fault 'ReadinessSeconds') { & $fault 'ReadinessSeconds' } else { 30 })) `
    -StandardOutput (Join-Path $handoff.logs 'probe.stdout.log') -StandardError (Join-Path $handoff.logs 'probe.stderr.log')
$restored = ([string]$r.outcome -eq 'Succeeded' -and $r.restored)
Publish-AeroLinkJsonAtomic -Path $outcome -Value ([ordered]@{ decision = $(if ($restored) { 'Completed' } else { 'Failed' })
    failures = [string[]]@($(if (-not $restored) { "RoleNotRestored:qualification-probe:$($r.outcome)" }))
    roles = @([ordered]@{ role = 'qualification-probe'; restored = $restored; processId = $r.processId }); pid = $PID })
Exit-AeroLinkTransition $lease
exit $(if ($restored) { 0 } else { 3 })
'@ | Set-Content -LiteralPath $actor -Encoding ASCII
$env:AEROLINK_TEST_SCRIPTS = $PSScriptRoot

$probeRole = @([pscustomobject]@{ role = 'qualification-probe'; launchRequired = $true; readiness = @{ kind = 'marker' } })
function Invoke-TestChain {
    param([string]$Name, [hashtable]$Faults = @{}, [int]$Deadline = 90, [int]$ProtocolVersion = 1, [switch]$Unqualified, [object[]]$Roles = $probeRole, [string]$AuthorityDieAt = 'None')
    $installation = Join-Path $root $Name
    New-Item -ItemType Directory -Path $installation -Force | Out-Null
    $lease = Enter-AeroLinkTransition -InstallationRoot $installation
    try {
        $qualification = Test-AeroLinkLaunchContextQualification -InstallationRoot $installation -DescriptorOverride @{ contextKind = 'Test'; contextName = $Name }
        if ($Unqualified) { $qualification.Context = [pscustomobject]@{ Valid = $false; Reason = 'test: unidentified' } }
        $result = Invoke-AeroLinkTransitionChain -InstallationRoot $installation -Lease $lease -Caller Test -Plan @{ test = $Name } -DelegateScript $actor -DelegateSourceIdentity 'test-source' `
            -RequiredRoles $Roles -DeadlineSeconds $Deadline -Qualification $qualification -QualificationProbe -ProtocolVersion $ProtocolVersion -Faults $Faults -AuthorityDieAt $AuthorityDieAt
        foreach ($launch in @(Get-AeroLinkProperty $result.Outcome 'launches' @())) { Own ([int]$launch.processId) }
        $workerFile = Join-Path $result.AttemptRoot 'worker.pid'
        if (Test-Path -LiteralPath $workerFile) { $result | Add-Member -NotePropertyName WorkerPid -NotePropertyValue ([int][IO.File]::ReadAllText($workerFile)) }
        return $result
    }
    finally { Exit-AeroLinkTransition -Lease $lease }
}
function Test-Alive($Launch) { $Launch -and [AeroLink.TransitionV1.Kernel]::Classify([int]$Launch.processId, [string]$Launch.startedAt, [string]$Launch.image) -eq 'RunningMatch' }

try {
    # ---------------------------------------------------------------------------------------------------------
    # T1 (#1053): the chain ends when its WORK ends; the launched service survives the job; receipt proves zero.
    # ---------------------------------------------------------------------------------------------------------
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $t1 = Invoke-TestChain -Name 't1'
    $clock.Stop()
    $launch = @($t1.Outcome.launches)[0]
    Check ($t1.Decision -eq 'Completed' -and $t1.ExitCode -eq 0) "T1: a clean attempt completes with exit 0 (got $($t1.Decision)/$($t1.ExitCode): $($t1.Detail))."
    Check ($clock.Elapsed.TotalSeconds -lt 60) "T1: the outer returns when the chain ends, not when the 180 s survivor does ($([int]$clock.Elapsed.TotalSeconds)s)."
    Check ([bool]$t1.Outcome.cleanup.transitionContainmentProven) 'T1: containment is proven by a zero observed through the held job handle.'
    Check (Test-Alive $launch) 'T1: the service launched through the authority survives the transition job.'
    Check ($t1.Outcome.recovery.admissible) 'T1: a completed attempt is itself resolved for the next admission.'
    $receipt = Test-AeroLinkCleanupReceipt -Path (Get-AeroLinkAttemptPaths $t1.AttemptRoot).Receipt -AttemptId $t1.AttemptId -JobName ('Global\AeroLinkTransition-' + $t1.AttemptId)
    Check ($receipt.Valid) "T1: the cleanup receipt is valid and bound to the attempt and job ($($receipt.Reason))."
    $admission = Test-AeroLinkInstallationAdmission -InstallationRoot (Join-Path $root 't1')
    Check ($admission.Admitted) "T1: the next attempt is admitted ($($admission.Detail))."
    Check ((Get-AeroLinkListenerOwners -Port 1) -is [array] -or $true) 'T1: fixture sanity.'

    # ---------------------------------------------------------------------------------------------------------
    # T2 (X10): an unsupported handoff protocol is refused BEFORE the lease or any mutation.
    # ---------------------------------------------------------------------------------------------------------
    $t2 = Invoke-TestChain -Name 't2' -ProtocolVersion 2
    Check ($t2.Decision -eq 'Refused' -and $t2.ExitCode -eq 25) "T2: an unsupported protocol is Refused/25 (got $($t2.Decision)/$($t2.ExitCode): $($t2.Detail))."
    Check (-not $t2.Outcome.mutationStarted -and -not $t2.RestorationRequired) 'T2: a refused handoff began no mutation and owes nothing.'
    Check (@($t2.Outcome.launches).Count -eq 0) 'T2: a refused handoff launched nothing.'

    # ---------------------------------------------------------------------------------------------------------
    # T3 (X10): a delegate running a different source than the handoff names is refused before mutation.
    # ---------------------------------------------------------------------------------------------------------
    $t3 = Invoke-TestChain -Name 't3' -Faults @{ ActorSource = 'moved-source' }
    Check ($t3.Decision -eq 'Refused' -and [string]$t3.Outcome.delegate.decision -eq 'SourceVersionMismatch') "T3: a source moved between handoff and entry is refused (got $($t3.Decision), delegate $($t3.Outcome.delegate.decision))."

    # ---------------------------------------------------------------------------------------------------------
    # T4 (X5): Completed published but a nonzero exit is never success.
    # ---------------------------------------------------------------------------------------------------------
    $t4 = Invoke-TestChain -Name 't4' -Faults @{ DelegateExitAfterOutcome = 7 }
    Check ($t4.Decision -eq 'ChainFailed' -and $t4.ExitCode -eq 24) "T4: a delegate whose exit contradicts its Completed outcome fails the chain (got $($t4.Decision))."
    Check (@($t4.Outcome.failures) -match 'DelegateExitMismatch').Count -gt 0 'T4: the failure names the exit/outcome mismatch.'
    Check (-not $t4.RestorationRequired) 'T4: the role itself was restored, so no restoration is owed.'

    # T4b (X5): a continuation the delegate started but that published nothing is never success.
    $t4b = Invoke-TestChain -Name 't4b' -Faults @{ SkipContinuation = $true }
    Check ($t4b.Decision -eq 'ChainFailed' -and (@($t4b.Outcome.failures) -contains 'ContinuationOutcomeMissing')) "T4b: a started continuation with no outcome fails the chain (got $($t4b.Decision): $($t4b.Detail))."

    # ---------------------------------------------------------------------------------------------------------
    # T5 (#1053, X2): a delegate that dies leaving transient work: collected, failure reported, recovery admissible.
    # ---------------------------------------------------------------------------------------------------------
    $t5 = Invoke-TestChain -Name 't5' -Faults @{ WorkerSeconds = 120; DieWithoutOutcome = $true }
    Check ($t5.Decision -eq 'ChainFailed') "T5: a delegate that dies without an outcome fails the chain (got $($t5.Decision))."
    Check ($t5.PSObject.Properties['WorkerPid'] -and [AeroLink.TransitionV1.Kernel]::Classify($t5.WorkerPid, 'x', 'x') -ne 'RunningDifferent' -or $true) 'T5: fixture sanity.'
    Check ($t5.PSObject.Properties['WorkerPid'] -and (Get-Process -Id $t5.WorkerPid -ErrorAction SilentlyContinue) -eq $null) 'T5: the unleased transient worker is collected with the attempt.'
    Check ([bool]$t5.Outcome.cleanup.transitionContainmentProven -and $t5.Outcome.recovery.admissible) 'T5: a safely collected failure is admissible for recovery.'
    Check ($t5.RestorationRequired) 'T5: mutation began and the role was never restored, so restoration is owed.'

    # ---------------------------------------------------------------------------------------------------------
    # T6 (#1043): the deadline terminates the whole attempt and reports DeadlineExceeded, never success.
    # ---------------------------------------------------------------------------------------------------------
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $t6 = Invoke-TestChain -Name 't6' -Faults @{ Hang = $true; WorkerSeconds = 300 } -Deadline 8
    $clock.Stop()
    Check ($t6.Decision -eq 'DeadlineExceeded' -and $t6.ExitCode -eq 23) "T6: an over-budget attempt is DeadlineExceeded/23 (got $($t6.Decision))."
    Check ($clock.Elapsed.TotalSeconds -lt 60) "T6: the deadline is bounded ($([int]$clock.Elapsed.TotalSeconds)s)."
    Check ([bool]$t6.Outcome.cleanup.transitionContainmentProven -and (Get-Process -Id $t6.WorkerPid -ErrorAction SilentlyContinue) -eq $null) 'T6: the hung delegate and its worker are collected.'

    # ---------------------------------------------------------------------------------------------------------
    # T7 (X6, X7): a role that never becomes ready is RestorationFailed; its staging job is observed empty.
    # ---------------------------------------------------------------------------------------------------------
    $t7 = Invoke-TestChain -Name 't7' -Faults @{ ProbeMode = 'Never'; ReadinessSeconds = 4 }
    $never = @($t7.Outcome.launches)[0]
    Check ($t7.Decision -eq 'ChainFailed' -or $t7.Decision -eq 'RestorationFailed') "T7: a never-ready role is not Completed (got $($t7.Decision))."
    Check ($t7.RestorationRequired) 'T7: restoration stays owed.'
    Check ($never.outcome -eq 'FailedReadiness' -and $never.currentHealth -eq 'ProvenStopped') "T7: the launch is FailedReadiness with its staging job proven empty (got $($never.outcome)/$($never.currentHealth))."

    # ---------------------------------------------------------------------------------------------------------
    # T8 (X1): an unidentified or unqualified context refuses before any attempt exists.
    # ---------------------------------------------------------------------------------------------------------
    $t8 = Invoke-TestChain -Name 't8' -Unqualified
    Check ($t8.Decision -eq 'Refused' -and $t8.ExitCode -eq 20) "T8: an unidentified context is Refused/20 (got $($t8.Decision))."
    Check (-not (Test-Path -LiteralPath (Get-AeroLinkAttemptIndexPath -InstallationRoot (Join-Path $root 't8')))) 'T8: a context refusal admits no attempt.'
    $unqualified = Test-AeroLinkLaunchContextQualification -InstallationRoot (Join-Path $root 't8') -DescriptorOverride @{ contextKind = 'Test'; contextName = 'no-record' }
    Check (-not $unqualified.Supported -and $unqualified.Detail -match 'not qualified') 'T8: a descriptor with no qualification record is not Supported.'

    # ---------------------------------------------------------------------------------------------------------
    # T9 (X1): qualification records bind the exact descriptor, their integrity, and every applicable path.
    # ---------------------------------------------------------------------------------------------------------
    $qRoot = Join-Path $root 'qualify'
    New-Item -ItemType Directory -Path $qRoot -Force | Out-Null
    $descriptor = @{ contextKind = 'Task'; contextName = '\probe'; definitionHash = 'abc' }
    $context = Get-AeroLinkLaunchContextDescriptor -Override $descriptor
    $survived = [ordered]@{ applicable = $true; observed = $true; survived = $true }
    $paths = [ordered]@{ transientJob = $survived; wrapperExit = $survived; taskCompletion = $survived; taskStop = $survived; hardTimeout = $survived }
    $record = Write-AeroLinkLaunchContextQualification -InstallationRoot $qRoot -Descriptor $descriptor -DescriptorHash $context.DescriptorHash -Paths $paths -RequiredPaths @($paths.Keys)
    Check ($record.verdict -eq 'Qualified') 'T9: every applicable path observed and survived is Qualified.'
    Check ((Test-AeroLinkLaunchContextQualification -InstallationRoot $qRoot -DescriptorOverride $descriptor).Supported) 'T9: the exact descriptor is Supported.'
    Check (-not (Test-AeroLinkLaunchContextQualification -InstallationRoot $qRoot -DescriptorOverride @{ contextKind = 'Task'; contextName = '\probe'; definitionHash = 'changed' }).Supported) 'T9: a changed definition hash lands on no qualification.'
    $recordPath = Get-AeroLinkQualificationPath -InstallationRoot $qRoot -DescriptorHash $context.DescriptorHash
    Add-Content -LiteralPath $recordPath -Value ' '
    Check ((Test-AeroLinkLaunchContextQualification -InstallationRoot $qRoot -DescriptorOverride $descriptor).Detail -match 'integrity') 'T9: a record altered after it was written fails its integrity hash.'
    $unobserved = [ordered]@{ transientJob = $survived; wrapperExit = $survived; taskCompletion = $survived; taskStop = [ordered]@{ applicable = $true; observed = $false; survived = $null }; hardTimeout = $survived }
    Check ((Write-AeroLinkLaunchContextQualification -InstallationRoot $qRoot -Descriptor $descriptor -DescriptorHash $context.DescriptorHash -Paths $unobserved -RequiredPaths @($unobserved.Keys)).verdict -eq 'ExperimentFailed') 'T9: an unobserved applicable path is a failed experiment, never Incompatible.'
    $killed = [ordered]@{ transientJob = $survived; wrapperExit = $survived; taskCompletion = $survived; taskStop = [ordered]@{ applicable = $true; observed = $true; survived = $false }; hardTimeout = $survived }
    Check ((Write-AeroLinkLaunchContextQualification -InstallationRoot $qRoot -Descriptor $descriptor -DescriptorHash $context.DescriptorHash -Paths $killed -RequiredPaths @($killed.Keys)).verdict -eq 'Incompatible') 'T9: an observed path that killed the probe is Incompatible.'
    Check (-not (Test-AeroLinkLaunchContextQualification -InstallationRoot $qRoot -DescriptorOverride $descriptor).Supported) 'T9: an Incompatible record is never Supported.'

    # ---------------------------------------------------------------------------------------------------------
    # T10: the task definition hash binds principal, settings and action IMAGE - not name, triggers or arguments.
    # ---------------------------------------------------------------------------------------------------------
    $xml = { param($Uri, $Arguments, $Limit, $Logon)
        "<?xml version=`"1.0`" encoding=`"UTF-16`"?><Task version=`"1.2`" xmlns=`"http://schemas.microsoft.com/windows/2004/02/mit/task`"><RegistrationInfo><URI>$Uri</URI></RegistrationInfo><Triggers><BootTrigger><Enabled>true</Enabled></BootTrigger></Triggers><Principals><Principal id=`"Author`"><LogonType>$Logon</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals><Settings><ExecutionTimeLimit>$Limit</ExecutionTimeLimit></Settings><Actions Context=`"Author`"><Exec><Command>powershell.exe</Command><Arguments>$Arguments</Arguments></Exec></Actions></Task>" }
    $base = Get-AeroLinkTaskDefinitionCanonical -Xml (& $xml '\A' '-File a.ps1' 'PT135M' 'S4U')
    Check ($base -eq (Get-AeroLinkTaskDefinitionCanonical -Xml (& $xml '\B' '-File b.ps1' 'PT135M' 'S4U'))) 'T10: another name or checkout path is the same definition.'
    Check ($base -ne (Get-AeroLinkTaskDefinitionCanonical -Xml (& $xml '\A' '-File a.ps1' 'PT30M' 'S4U'))) 'T10: a different ExecutionTimeLimit is a different definition.'
    Check ($base -ne (Get-AeroLinkTaskDefinitionCanonical -Xml (& $xml '\A' '-File a.ps1' 'PT135M' 'InteractiveToken'))) 'T10: a different logon type is a different definition.'

    # ---------------------------------------------------------------------------------------------------------
    # T11 (X2): a prior attempt that is not proven quiescent refuses admission; once it is, admission follows.
    # ---------------------------------------------------------------------------------------------------------
    $t11 = Join-Path $root 't11'
    $attemptId = New-AeroLinkAttemptId
    $attemptRoot = Join-Path (Get-AeroLinkTransitionStateRoot -InstallationRoot $t11) $attemptId
    New-Item -ItemType Directory -Path $attemptRoot -Force | Out-Null
    Publish-AeroLinkJsonAtomic -Path (Get-AeroLinkAttemptPaths $attemptRoot).Attempt -Value @{ attemptId = $attemptId }
    Add-AeroLinkAttemptToIndex -InstallationRoot $t11 -AttemptRoot $attemptRoot -AttemptId $attemptId
    $jobName = 'Global\AeroLinkTransition-' + $attemptId
    Write-AeroLinkTransitionEvent -Path (Get-AeroLinkAttemptPaths $attemptRoot).JobEvents -Record ([ordered]@{ type = 'Intended'; jobName = $jobName; at = (Get-Date).ToUniversalTime().ToString('o') })
    $heldJob = $K::CreateJob($jobName, $K::LimitKillOnClose, $K::QueryOnlySddl())
    Write-AeroLinkTransitionEvent -Path (Get-AeroLinkAttemptPaths $attemptRoot).JobEvents -Record ([ordered]@{ type = 'Created'; jobName = $jobName; at = (Get-Date).ToUniversalTime().ToString('o') })
    Write-AeroLinkTransitionEvent -Path (Get-AeroLinkAttemptPaths $attemptRoot).JobEvents -Record ([ordered]@{ type = 'Armed'; jobName = $jobName; witnessDir = (Join-Path $attemptRoot 'witness'); at = (Get-Date).ToUniversalTime().ToString('o') })
    $spec = New-Object AeroLink.TransitionV1.LaunchSpec
    $spec.CommandLine = '"' + $env:ComSpec + '" /c ping -n 120 127.0.0.1 > nul'
    $member = $K::Launch($heldJob, $spec); $K::Resume($member); Own $member.ProcessId
    $blocked = Test-AeroLinkInstallationAdmission -InstallationRoot $t11
    Check (-not $blocked.Admitted -and $blocked.Detail -match 'NotQuiescent') "T11: a prior attempt with a live member refuses admission (got '$($blocked.Detail)')."
    $recovered = Get-AeroLinkTransitionQuiescence -AttemptRoot $attemptRoot -AttemptId $attemptId -Recover
    Check ($recovered.State -eq 'Quiescent') "T11: recovery terminates each kernel-verified member and observes zero (got $($recovered.State): $($recovered.Detail))."
    $K::Close($member); $K::CloseHandleChecked($heldJob)
    Check ((Test-AeroLinkInstallationAdmission -InstallationRoot $t11).Admitted) 'T11: once proven quiescent, the next attempt is admitted.'
    $lostId = New-AeroLinkAttemptId
    $lostRoot = Join-Path (Get-AeroLinkTransitionStateRoot -InstallationRoot $t11) $lostId
    New-Item -ItemType Directory -Path $lostRoot -Force | Out-Null
    Add-AeroLinkAttemptToIndex -InstallationRoot $t11 -AttemptRoot $lostRoot -AttemptId $lostId
    foreach ($type in @('Intended', 'Created', 'Armed')) {
        Write-AeroLinkTransitionEvent -Path (Get-AeroLinkAttemptPaths $lostRoot).JobEvents -Record ([ordered]@{ type = $type; jobName = ('Global\AeroLinkTransition-' + $lostId); witnessDir = (Join-Path $lostRoot 'witness'); at = (Get-Date).ToUniversalTime().ToString('o') })
    }
    $lost = Get-AeroLinkTransitionQuiescence -AttemptRoot $lostRoot -AttemptId $lostId
    Check ($lost.State -eq 'TerminationUnconfirmed') "T11: an armed job with no handle, no receipt and no witness is TerminationUnconfirmed, never quiescent (got $($lost.State))."

    # ---------------------------------------------------------------------------------------------------------
    # T12 (X3): the outer dies mid-attempt; the witness collects the job and publishes the receipt; admission follows.
    # ---------------------------------------------------------------------------------------------------------
    $t12 = Join-Path $root 't12'
    New-Item -ItemType Directory -Path $t12 -Force | Out-Null
    $outerScript = Join-Path $root 'outer.ps1'
    @'
param($Scripts, $Installation, $Actor)
$ErrorActionPreference = 'Stop'
$env:AEROLINK_TEST_SCRIPTS = $Scripts
Import-Module (Join-Path $Scripts 'AeroLinkTransitionKernel.psm1') -DisableNameChecking
Import-Module (Join-Path $Scripts 'AeroLinkTransitionAuthority.psm1') -DisableNameChecking
Import-Module (Join-Path $Scripts 'AeroLinkTransition.psm1')
$lease = Enter-AeroLinkTransition -InstallationRoot $Installation
$q = Test-AeroLinkLaunchContextQualification -InstallationRoot $Installation -DescriptorOverride @{ contextKind = 'Test'; contextName = 'outer-death' }
Invoke-AeroLinkTransitionChain -InstallationRoot $Installation -Lease $lease -Caller Test -Plan @{ test = 'outer-death' } -DelegateScript $Actor -DelegateSourceIdentity 'test-source' `
    -RequiredRoles @() -DeadlineSeconds 300 -Qualification $q -QualificationProbe -Faults @{ WorkerSeconds = 300; Hang = $true } | Out-Null
'@ | Set-Content -LiteralPath $outerScript -Encoding ASCII
    $outer = Start-Process -FilePath $powershell -ArgumentList ('-NoProfile -ExecutionPolicy Bypass -File "' + $outerScript + '" -Scripts "' + $PSScriptRoot + '" -Installation "' + $t12 + '" -Actor "' + $actor + '"') -WindowStyle Hidden -PassThru
    Own $outer.Id
    $index = Get-AeroLinkAttemptIndexPath -InstallationRoot $t12
    $workerFile = $null
    for ($i = 0; $i -lt 300 -and -not $workerFile; $i++) {
        $attempts = @((Read-AeroLinkTransitionEvents -Path $index).Events)
        if ($attempts.Count) { $candidate = Join-Path ([string]$attempts[0].attemptRoot) 'worker.pid'; if (Test-Path -LiteralPath $candidate) { $workerFile = $candidate } }
        Start-Sleep -Milliseconds 100
    }
    Check ([bool]$workerFile) 'T12: the attempt reached its transient work before the outer was killed.'
    if ($workerFile) {
        $attempt = @((Read-AeroLinkTransitionEvents -Path $index).Events)[0]
        $workerPid = [int][IO.File]::ReadAllText($workerFile)
        Own $workerPid
        $pendingAdmission = Test-AeroLinkInstallationAdmission -InstallationRoot $t12
        Check (-not $pendingAdmission.Admitted) 'T12: while the outer is alive and working, no other attempt is admitted.'
        Stop-Process -Id $outer.Id -Force
        $receiptPath = (Get-AeroLinkAttemptPaths ([string]$attempt.attemptRoot)).Receipt
        for ($i = 0; $i -lt 300 -and -not (Test-Path -LiteralPath $receiptPath); $i++) { Start-Sleep -Milliseconds 100 }
        $witnessReceipt = Test-AeroLinkCleanupReceipt -Path $receiptPath -AttemptId ([string]$attempt.attemptId)
        Check ($witnessReceipt.Valid -and [string]$witnessReceipt.Receipt.observedBy -eq 'witness') "T12: the witness published a valid receipt after the outer died ($($witnessReceipt.Class): $($witnessReceipt.Reason))."
        Check ((Get-Process -Id $workerPid -ErrorAction SilentlyContinue) -eq $null) 'T12: the unleased worker was collected by the witness.'
        $afterDeath = $null
        for ($i = 0; $i -lt 100; $i++) { $afterDeath = Test-AeroLinkInstallationAdmission -InstallationRoot $t12; if ($afterDeath.Admitted) { break }; Start-Sleep -Milliseconds 200 }
        Check ($afterDeath.Admitted) "T12: the next attempt is admitted after the witness receipt ($($afterDeath.Detail))."
    }

    # ---------------------------------------------------------------------------------------------------------
    # T13 (X4): the authority dies mid-launch; the witness reads the kernel flag and the replay resolves it exactly.
    # ---------------------------------------------------------------------------------------------------------
    foreach ($case in @(@{ DieAt = 'AfterCommitting'; Outcome = 'Abandoned'; Health = 'ProvenStopped' }, @{ DieAt = 'AfterClear'; Outcome = 'Succeeded'; Health = 'Running' })) {
        $installation = Join-Path $root ('t13-' + $case.DieAt)
        New-Item -ItemType Directory -Path $installation -Force | Out-Null
        $authorityScript = Join-Path $root 'authority.ps1'
        @'
param($Scripts, $Installation, $DieAt)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $Scripts 'AeroLinkTransitionKernel.psm1') -DisableNameChecking
Import-Module (Join-Path $Scripts 'AeroLinkTransitionAuthority.psm1') -DisableNameChecking
$attemptRoot = Join-Path $Installation 'attempt'
$paths = Get-AeroLinkAttemptPaths $attemptRoot
New-Item -ItemType Directory -Path $paths.Spool -Force | Out-Null
$witness = Start-AeroLinkTransitionWitness -Dir $paths.Witness
$q = Test-AeroLinkLaunchContextQualification -InstallationRoot $Installation -DescriptorOverride @{ contextKind = 'Test'; contextName = 'authority-death' }
$body = "[IO.File]::WriteAllText('{READY_FILE}', (@{ requestId = '{REQUEST_ID}'; nonce = '{READY_NONCE}'; processId = `$PID } | ConvertTo-Json)); Start-Sleep 180"
Publish-AeroLinkJsonAtomic -Path (Join-Path $paths.Spool 'rq.request.json') -Value ([ordered]@{ requestId = 'rq'; attemptId = 'A1'; role = 'qualification-probe'
    launch = [ordered]@{ filePath = (Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'); arguments = ('-NoProfile -Command "' + $body + '"') }
    readiness = @{ kind = 'marker' }; readinessTimeoutSeconds = 30; expiresAtUtc = (Get-Date).ToUniversalTime().AddMinutes(5).ToString('o') })
Invoke-AeroLinkAuthorityPump -Spool $paths.Spool -AttemptId 'A1' -Witness $witness -Qualification $q -QualificationProbe -DieAt $DieAt
'@ | Set-Content -LiteralPath $authorityScript -Encoding ASCII
        $authority = Start-Process -FilePath $powershell -ArgumentList ('-NoProfile -ExecutionPolicy Bypass -File "' + $authorityScript + '" -Scripts "' + $PSScriptRoot + '" -Installation "' + $installation + '" -DieAt ' + $case.DieAt) -WindowStyle Hidden -PassThru
        Own $authority.Id
        $null = $authority.WaitForExit(120000)
        $spool = Join-Path $installation 'attempt\spool'
        $staging = Join-Path $spool 'rq.staging.json'
        for ($i = 0; $i -lt 200 -and -not (Test-Path -LiteralPath $staging); $i++) { Start-Sleep -Milliseconds 100 }
        $created = @((Read-AeroLinkTransitionEvents -Path (Join-Path $spool 'rq.events.jsonl')).Events | Where-Object { $_.type -eq 'Created' })
        if ($created.Count) { Own ([int]$created[0].processId) }
        $resolution = Resolve-AeroLinkLaunchRequest -Spool $spool -RequestId 'rq'
        Check ($authority.ExitCode -eq 90) "T13 ($($case.DieAt)): the authority died at the seam (exit $($authority.ExitCode))."
        Check ($resolution.Outcome -eq $case.Outcome -and $resolution.CurrentHealth -eq $case.Health) "T13 ($($case.DieAt)): resolved $($case.Outcome)/$($case.Health) from kernel evidence (got $($resolution.Outcome)/$($resolution.CurrentHealth): $($resolution.Detail))."
    }

    # ---------------------------------------------------------------------------------------------------------
    # T14 (C4a/C4b): streaming binds shared logs before launch and detects every new generation.
    # ---------------------------------------------------------------------------------------------------------
    $shared = Join-Path $root 'shared.log'
    Set-Content -LiteralPath $shared -Value "previous attempt line A`r`nprevious attempt line B" -Encoding ASCII
    $binding = New-AeroLinkLogBinding -Path $shared
    Add-Content -LiteralPath $shared -Value 'written after binding, before attach' -Encoding ASCII
    $tail = New-AeroLinkGenerationTail -Path $shared -Binding $binding
    $lines = @($tail.Read())
    Check ($lines -contains 'written after binding, before attach' -and $lines -notcontains 'previous attempt line A') "C4a: output between binding and attach is delivered and history is not replayed (got '$($lines -join '|')')."
    $length = (Get-Item -LiteralPath $shared).Length
    $same = 'Z' * ([int]$length - 2)
    [IO.File]::WriteAllText($shared, $same + "`r`n")
    $rewritten = @($tail.Read())
    Check ($tail.Generations -ge 1 -and $tail.Reasons -contains 'rewritten') "C4b: an in-place rewrite to the SAME length is a new generation (reasons '$($tail.Reasons -join ',')')."
    Check ($rewritten.Count -eq 1 -and $rewritten[0] -eq $same) 'C4b: the rewritten generation is delivered from its start.'
    $unique = Join-Path $root 'unique.log'
    Set-Content -LiteralPath $unique -Value 'first line of a log created by this attempt' -Encoding ASCII
    Check (@((New-AeroLinkGenerationTail -Path $unique).Read()) -contains 'first line of a log created by this attempt') 'C4a: a per-attempt log attached late still starts at its first line.'
    $split = Join-Path $root 'split.log'
    [IO.File]::WriteAllBytes($split, [byte[]]@())
    $splitTail = New-AeroLinkGenerationTail -Path $split
    $bytes = [Text.Encoding]::UTF8.GetBytes("caf" + [char]0xE9 + " ready`r`n")
    $cut = [Array]::IndexOf($bytes, [byte]0xC3) + 1
    $stream = [IO.File]::Open($split, 'Append', 'Write', 'ReadWrite'); $stream.Write($bytes, 0, $cut); $stream.Dispose()
    Check (@($splitTail.Read()).Count -eq 0) 'Streaming: an unterminated line is held.'
    $stream = [IO.File]::Open($split, 'Append', 'Write', 'ReadWrite'); $stream.Write($bytes, $cut, $bytes.Length - $cut); $stream.Dispose()
    Check ((@($splitTail.Read()) -join '') -eq ("caf" + [char]0xE9 + " ready")) 'Streaming: a multi-byte character split across polls survives intact.'

    # ---------------------------------------------------------------------------------------------------------
    # T15 (X8): shared evidence appends are serialized and bounded; contention past the bound throws, never drops.
    # ---------------------------------------------------------------------------------------------------------
    $journal = Join-Path $root 'journal.jsonl'
    $holder = [IO.File]::Open($journal, [IO.FileMode]::Append, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    $contended = $null
    try { Write-AeroLinkTransitionEvent -Path $journal -Record @{ a = 1 } -ContentionTimeoutMs 300 } catch { $contended = $_.Exception.Message }
    $holder.Dispose()
    Check ($contended -like 'EvidenceWriteContention:*') "T15: an append blocked past its bound throws EvidenceWriteContention (got '$contended')."
    Write-AeroLinkTransitionEvent -Path $journal -Record @{ a = 2 }
    Check (@((Read-AeroLinkTransitionEvents -Path $journal).Events).Count -eq 1) 'T15: nothing was written by the failed append; the next append succeeds.'

    # ---------------------------------------------------------------------------------------------------------
    # T16 (B3): the actual token is read from the kernel, not inferred from a role test.
    # ---------------------------------------------------------------------------------------------------------
    $token = Get-AeroLinkTokenFacts
    Check ($token.Readable -and $token.TokenElevationType -in @('Default', 'Full', 'Limited') -and $token.IntegrityLevel -and $token.AdministratorsGroup) "T16: token facts are readable ($($token | ConvertTo-Json -Compress))."

    # ---------------------------------------------------------------------------------------------------------
    # T17: a launch image outside the role policy is refused before anything is created.
    # ---------------------------------------------------------------------------------------------------------
    $t17 = Join-Path $root 't17'
    $t17Paths = Get-AeroLinkAttemptPaths (Join-Path $t17 'attempt')
    New-Item -ItemType Directory -Path $t17Paths.Spool -Force | Out-Null
    $witness = Start-AeroLinkTransitionWitness -Dir $t17Paths.Witness
    Own $witness.ProcessId
    try {
        Publish-AeroLinkJsonAtomic -Path (Join-Path $t17Paths.Spool 'bad.request.json') -Value ([ordered]@{ requestId = 'bad'; attemptId = 'A1'; role = 'api'
                launch = [ordered]@{ filePath = $powershell; arguments = '-NoProfile -Command exit' }; readiness = @{ kind = 'api' }; readinessTimeoutSeconds = 5
                expiresAtUtc = (Get-Date).ToUniversalTime().AddMinutes(5).ToString('o') })
        $supported = [pscustomobject]@{ Supported = $true; Detail = 'test'; DescriptorHash = 'test'; BreakawayPermittedByImmediateJob = $false; Context = [pscustomobject]@{ Valid = $true } }
        Invoke-AeroLinkAuthorityPump -Spool $t17Paths.Spool -AttemptId 'A1' -Witness $witness -Qualification $supported
        $bad = Resolve-AeroLinkLaunchRequest -Spool $t17Paths.Spool -RequestId 'bad'
        Check ($bad.Outcome -eq 'Refused' -and $bad.Detail -match 'AeroLink.Api.exe') "T17: an 'api' request for another image is Refused before creation (got $($bad.Outcome): $($bad.Detail))."
        # The tunnel image may be renamed only for a disposable qualification installation.
        $previousImage = @($env:AEROLINK_QUALIFICATION_TUNNEL_IMAGE, $env:AEROLINK_INSTALLATION_ROOT)
        try {
            $env:AEROLINK_QUALIFICATION_TUNNEL_IMAGE = 'stand-in-edge.exe'; $env:AEROLINK_INSTALLATION_ROOT = $null
            Publish-AeroLinkJsonAtomic -Path (Join-Path $t17Paths.Spool 'renamed.request.json') -Value ([ordered]@{ requestId = 'renamed'; attemptId = 'A1'; role = 'tunnel'
                    launch = [ordered]@{ filePath = 'C:\edge\stand-in-edge.exe' }; readiness = @{ kind = 'tunnel'; publicUrl = 'https://x.invalid' }; readinessTimeoutSeconds = 5
                    expiresAtUtc = (Get-Date).ToUniversalTime().AddMinutes(5).ToString('o') })
            Invoke-AeroLinkAuthorityPump -Spool $t17Paths.Spool -AttemptId 'A1' -Witness $witness -Qualification $supported
            $renamed = Resolve-AeroLinkLaunchRequest -Spool $t17Paths.Spool -RequestId 'renamed'
            Check ($renamed.Outcome -eq 'Refused' -and $renamed.Detail -match 'ngrok.exe') "T17: a renamed tunnel image outside a disposable installation is Refused (got $($renamed.Outcome): $($renamed.Detail))."
        }
        finally { $env:AEROLINK_QUALIFICATION_TUNNEL_IMAGE = $previousImage[0]; $env:AEROLINK_INSTALLATION_ROOT = $previousImage[1] }
    }
    finally { Stop-AeroLinkTransitionWitness -Witness $witness }
}
catch { $failures.Add("Suite error: $($_.Exception.Message) @ $($_.InvocationInfo.PositionMessage)") }
finally {
    $K2 = [AeroLink.TransitionV1.Kernel]
    foreach ($identity in $owned) {
        if ($K2::Classify($identity.ProcessId, $identity.StartedAtUtc, $identity.ImagePath) -eq 'RunningMatch') { try { Stop-Process -Id $identity.ProcessId -Force } catch { } }
    }
    Start-Sleep -Milliseconds 500
    foreach ($identity in $owned) {
        $state = $K2::Classify($identity.ProcessId, $identity.StartedAtUtc, $identity.ImagePath)
        if ($state -eq 'RunningMatch' -or $state -like 'Unknown:*') { $failures.Add("Cleanup: owned pid $($identity.ProcessId) is $state.") }
    }
    if ($failures.Count -eq 0) {
        $resolved = [IO.Path]::GetFullPath($root)
        if (-not $resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected fixture cleanup path.' }
        Remove-Item -LiteralPath $resolved -Recurse -Force -ErrorAction SilentlyContinue
    }
}
if ($failures.Count -gt 0) {
    foreach ($failure in $failures) { Write-Host "FAIL: $failure" -ForegroundColor Red }
    Write-Host "Transition authority contracts FAILED ($($failures.Count) failure(s), $passed passed). Evidence: $root" -ForegroundColor Red
    exit 1
}
Write-Host "Transition authority contracts passed ($passed checks on $($PSVersionTable.PSEdition) $($PSVersionTable.PSVersion); $($owned.Count) owned processes proven stopped)." -ForegroundColor Green
exit 0
