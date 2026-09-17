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
        $chain = Invoke-AeroLinkTransitionChain -InstallationRoot $InstallationRoot -Lease $lease -Caller QualificationProbe `
            -Plan ([ordered]@{ operation = 'QualificationProbe'; sourceRoot = $repositoryRoot; configPath = $null; policy = 'Preserve'; scheduled = $true
                topology = [ordered]@{ tunnelRunning = $false; runtimeRunning = $false } }) `
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
$triggers = $document.SelectSingleNode('//t:Triggers', $ns)
if ($triggers) { [void]$triggers.ParentNode.RemoveChild($triggers) }
$registration = $document.SelectSingleNode('//t:RegistrationInfo/t:URI', $ns)
if ($registration) { [void]$registration.ParentNode.RemoveChild($registration) }

$twin = $TwinNamePrefix + [guid]::NewGuid().ToString('N').Substring(0, 8)
$summaryPath = Join-Path $runs "$twin.summary.json"
$summary = [ordered]@{ tool = 'aerolink-context-qualification'; sourceTask = ($source.TaskPath + $source.TaskName); twin = $twin
    sourceDefinitionHash = (Get-AeroLinkSha256Text (Get-AeroLinkTaskDefinitionCanonical -Xml $sourceXml)); exportedXmlSha256 = (Get-AeroLinkSha256Text $sourceXml)
    executionTimeLimit = $(if ($limit) { $limit.ToString() } else { 'none' }); runs = @(); startedAt = (Get-Date).ToUniversalTime().ToString('o') }
$probes = [System.Collections.Generic.List[object]]::new()
$paths = [ordered]@{}
$descriptors = @{}
$descriptor = $null

function Set-TwinArguments([string]$Id, [int]$Hold) {
    $probeCall = '-File "' + (Join-Path $PSScriptRoot 'Invoke-AeroLinkLaunchContextQualification.ps1') + '" -Probe -InstallationRoot "' + $InstallationRoot + '" -RunId ' + $Id + ' -HoldSeconds ' + $Hold
    $script:argumentsNode.InnerText = $originalArguments.Substring(0, $fileMatch.Index) + $probeCall + $originalArguments.Substring($fileMatch.Index + $fileMatch.Length)
    Register-ScheduledTask -TaskName $twin -Xml $document.OuterXml -Force -ErrorAction Stop | Out-Null
}
function Wait-TaskEnded([datetime]$Since, [int]$Seconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        $task = Get-ScheduledTask -TaskName $twin -ErrorAction Stop
        $info = $task | Get-ScheduledTaskInfo
        if ($task.State -ne 'Running' -and $info.LastRunTime -and $info.LastRunTime -ge $Since.AddSeconds(-5)) { return $info }
        Start-Sleep -Seconds 2
    }
    return $null
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
function Stop-Probe($Record) {
    if (-not $Record -or -not (Get-AeroLinkProperty $Record 'probe' $null)) { return }
    $identity = [pscustomobject]@{ ProcessId = [int]$Record.probe.processId; StartedAtUtc = (ConvertTo-AeroLinkUtcIso $Record.probe.startedAt); ImagePath = [string]$Record.probe.image }
    $script:probes.Add($identity)
    if ($K::Classify($identity.ProcessId, $identity.StartedAtUtc, $identity.ImagePath) -eq 'RunningMatch') { Stop-Process -Id $identity.ProcessId -Force }
}
function Add-RunEvidence([string]$Name, $Record, [string[]]$PathNames, [hashtable]$Survival, $Info) {
    $observed = [bool]($Record -and (Get-AeroLinkProperty $Record 'probe' $null))
    if ($Record -and (Get-AeroLinkProperty $Record 'descriptorHash' '')) { $script:descriptors[[string]$Record.descriptorHash] = $true; $script:descriptor = $Record.descriptor }
    foreach ($path in $PathNames) {
        $script:paths[$path] = [ordered]@{ applicable = $true; observed = $observed; survived = $(if ($observed) { [bool]$Survival[$path] } else { $null })
            setupFailure = $(if ($observed) { '' } else { "no probe was launched (record: $(if ($Record) { "$($Record.decision) $(Get-AeroLinkProperty $Record 'error' '')" } else { 'none' }))" })
            run = $Name; lastTaskResult = $(if ($Info) { $Info.LastTaskResult } else { $null }) }
    }
    $script:summary.runs += [ordered]@{ run = $Name; record = $Record; paths = $PathNames; survival = $Survival; lastTaskResult = $(if ($Info) { $Info.LastTaskResult } else { $null }) }
}

$exitCode = 1
try {
    # ---- run 1: normal completion ----
    $id = "$twin-run1"; Set-TwinArguments $id 0
    $since = Get-Date; Start-ScheduledTask -TaskName $twin
    $record = Wait-RunRecord $id $EndingTimeoutSeconds
    $info = Wait-TaskEnded $since $EndingTimeoutSeconds
    $alive = Test-ProbeSurvived $record
    Add-RunEvidence 'run1' $record @('transientJob', 'wrapperExit', 'taskCompletion') @{
        transientJob = ($alive -and [bool](Get-AeroLinkProperty $record 'containmentProven' $false)); wrapperExit = $alive; taskCompletion = ($alive -and $null -ne $info) } $info
    Stop-Probe $record

    # ---- run 2: explicit stop while the entry is still running ----
    $id = "$twin-run2"; Set-TwinArguments $id 3600
    $since = Get-Date; Start-ScheduledTask -TaskName $twin
    $record = Wait-RunRecord $id $EndingTimeoutSeconds
    if ($record) { Stop-ScheduledTask -TaskName $twin -ErrorAction Stop }
    $info = Wait-TaskEnded $since $EndingTimeoutSeconds
    Add-RunEvidence 'run2' $record @('taskStop') @{ taskStop = ((Test-ProbeSurvived $record) -and $null -ne $info) } $info
    Stop-Probe $record

    # ---- run 3: the definition's own hard time limit ----
    if ($limit) {
        $id = "$twin-run3"; Set-TwinArguments $id ([int]$limit.TotalSeconds + 600)
        $since = Get-Date; Start-ScheduledTask -TaskName $twin
        $record = Wait-RunRecord $id $EndingTimeoutSeconds
        $info = Wait-TaskEnded $since ([int]$limit.TotalSeconds + $EndingTimeoutSeconds)
        Add-RunEvidence 'run3' $record @('hardTimeout') @{ hardTimeout = ((Test-ProbeSurvived $record) -and $null -ne $info) } $info
        Stop-Probe $record
    }
    else { $paths['hardTimeout'] = [ordered]@{ applicable = $false; observed = $null; survived = $null; setupFailure = ''; evidence = 'the definition has no execution time limit' } }

    if ($descriptors.Count -ne 1 -or -not $descriptor) {
        $summary['verdict'] = 'Unqualifiable'
        $summary['detail'] = "the probe runs reported $($descriptors.Count) distinct descriptors; nothing was written"
    }
    else {
        $d = [ordered]@{}; foreach ($property in $descriptor.PSObject.Properties) { $d[$property.Name] = $property.Value }
        $written = Write-AeroLinkLaunchContextQualification -InstallationRoot $InstallationRoot -Descriptor $d -DescriptorHash (@($descriptors.Keys)[0]) -Paths $paths `
            -RequiredPaths @('transientJob', 'wrapperExit', 'taskCompletion', 'taskStop', 'hardTimeout') -Detail "probe survived every applicable path of the twin of $($summary.sourceTask)"
        $summary['verdict'] = $written.verdict
        $summary['detail'] = $written.detail
        $summary['descriptorHash'] = @($descriptors.Keys)[0]
        $summary['descriptor'] = $d
        if ($written.verdict -eq 'Qualified') { $exitCode = 0 }
    }
}
catch { $summary['error'] = $_.Exception.Message }
finally {
    $existing = Get-ScheduledTask -TaskName $twin -ErrorAction SilentlyContinue
    if ($existing) {
        if ($existing.State -eq 'Running') { try { Stop-ScheduledTask -TaskName $twin } catch { } }
        try { Unregister-ScheduledTask -TaskName $twin -Confirm:$false -ErrorAction Stop } catch { $summary['cleanupError'] = "twin task not unregistered: $($_.Exception.Message)"; $exitCode = 1 }
    }
    Start-Sleep -Seconds 1
    $summary['probesProvenStopped'] = @($probes | ForEach-Object { [ordered]@{ processId = $_.ProcessId; state = $K::Classify($_.ProcessId, $_.StartedAtUtc, $_.ImagePath) } })
    if (@($summary.probesProvenStopped | Where-Object { $_.state -eq 'RunningMatch' -or $_.state -like 'Unknown:*' }).Count) { $summary['cleanupError'] = 'a probe service could not be proven stopped'; $exitCode = 1 }
    $summary['twinRemaining'] = [bool](Get-ScheduledTask -TaskName $twin -ErrorAction SilentlyContinue)
    $summary['finishedAt'] = (Get-Date).ToUniversalTime().ToString('o')
    Publish-AeroLinkJsonAtomic -Path $summaryPath -Value $summary
    Write-Host "Launch-context qualification of $($summary.sourceTask): $($summary.verdict). $($summary.detail)"
    Write-Host "Evidence: $summaryPath"
}
exit $exitCode
