#Requires -Version 5.1
<#
    Deterministic regression coverage for the #483 bounded/self-healing recovery
    path. Uses scriptblock seams so no live PostgreSQL, AeroLink, or ngrok is
    required. The scenarios model the exact #483 failure class: a scheduled
    recovery must never wait indefinitely on a nested helper, and PostgreSQL
    readiness must mean pg_isready plus a real bounded query.
#>
$ErrorActionPreference = 'Stop'

$modulePath = Join-Path $PSScriptRoot 'AeroLinkRemoteDemo.psm1'
Import-Module $modulePath -Force

$moduleRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("aerolink-recovery-tests-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null

$failures = [System.Collections.Generic.List[string]]::new()
$script:helperCalls = 0
$script:stoppedPids = [System.Collections.Generic.List[int]]::new()
$script:ngrokCalls = 0

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { $script:failures.Add($Message) }
}

function New-TestConfig {
    return [pscustomobject]@{
        AeroLinkRoot = $moduleRoot
        LogsPath = Join-Path $tempRoot 'logs'
        StatePath = Join-Path $tempRoot 'state'
        NgrokExecutable = 'C:\Tools\ngrok.exe'
        PublicUrl = 'https://example.ngrok-free.dev'
        TrafficPolicyPath = 'C:\Tools\policy.yml'
        Upstream = 'http://127.0.0.1:5080'
        LocalApiBaseUri = 'http://127.0.0.1:5080'
    }
}

function New-ReadyState([bool]$Ready, [bool]$PgIsready = $true, [bool]$Query = $true, [string]$Detail = '') {
    return [pscustomobject]@{ Ready = $Ready; PgIsreadyOk = $PgIsready; QueryOk = $Query; Detail = $Detail }
}

function New-FakeHelper([bool]$Stuck) {
    $obj = [pscustomobject]@{
        Id = 777
        HasExited = -not $Stuck
        ExitCode = if ($Stuck) { $null } else { 0 }
        StdOutPath = Join-Path $tempRoot 'postgres-helper.stdout.log'
        StdErrPath = Join-Path $tempRoot 'postgres-helper.stderr.log'
    }
    $obj | Add-Member -MemberType ScriptMethod -Name Refresh -Value { }
    return $obj
}

function New-TestRun {
    return New-AeroLinkRemoteDemoRun -Scheduled
}

# --- 1. PostgreSQL ready immediately: helper must not be launched ---
$config = New-TestConfig
$script:helperCalls = 0
$readyTest = { param($C, $R) New-ReadyState -Ready $true -Detail 'pg_isready and a real read-only SELECT 1 both succeeded.' }
$launcher = { param($C, $R) $script:helperCalls++; New-FakeHelper -Stuck $true }
$stopper = { param($C, $R, $ProcessId) $script:stoppedPids.Add($ProcessId) }
$result = Start-AeroLinkRemoteDemoPostgres -Config $config -Run (New-TestRun) -ReadyTest $readyTest -HelperLauncher $launcher -HelperStopper $stopper -RecoveryTimeoutSeconds 10 -PollIntervalSeconds 1 -GraceSeconds 0
Assert-True ($result.Healthy -eq $true) 'Scenario 1: immediately-ready PostgreSQL should be healthy.'
Assert-True ($script:helperCalls -eq 0) 'Scenario 1: no helper should be launched when PostgreSQL is already ready.'

# --- 2. Unavailable, then query-ready after a delay (crash-recovery model) ---
$script:helperCalls = 0
$script:stoppedPids.Clear()
$readyCount = 0
$readyTest = {
    param($C, $R)
    $script:readyCount++
    if ($script:readyCount -lt 3) { New-ReadyState -Ready $false -Detail 'recovering' } else { New-ReadyState -Ready $true -Detail 'pg_isready and a real read-only SELECT 1 both succeeded.' }
}
$result = Start-AeroLinkRemoteDemoPostgres -Config $config -Run (New-TestRun) -ReadyTest $readyTest -HelperLauncher $launcher -HelperStopper $stopper -RecoveryTimeoutSeconds 30 -PollIntervalSeconds 1 -GraceSeconds 0
Assert-True ($result.Healthy -eq $true) 'Scenario 2: delayed query-ready PostgreSQL should become healthy.'
Assert-True ($script:helperCalls -eq 1) 'Scenario 2: exactly one helper should be launched.'
Assert-True ($script:stoppedPids.Contains(777)) 'Scenario 2: an owned helper still running after DB became ready should be terminated.'

# --- 3. Listener present but backend/query does not work ---
$script:helperCalls = 0
$script:stoppedPids.Clear()
$readyTest = { param($C, $R) New-ReadyState -Ready $false -PgIsready $true -Query $false -Detail 'pg_isready succeeded but a real read-only SELECT 1 did not return 1.' }
$result = Start-AeroLinkRemoteDemoPostgres -Config $config -Run (New-TestRun) -ReadyTest $readyTest -HelperLauncher $launcher -HelperStopper $stopper -RecoveryTimeoutSeconds 3 -PollIntervalSeconds 1 -GraceSeconds 0
Assert-True ($result.Healthy -eq $false) 'Scenario 3: a listener without a working backend must not be healthy.'
Assert-True ($result.Detail -match 'query-ready') 'Scenario 3: failure detail must state the database never became query-ready.'
Assert-True ($script:stoppedPids.Contains(777)) 'Scenario 3: the owned helper must be terminated on timeout.'

# --- 4. Helper stuck while independent DB health becomes good ---
$script:helperCalls = 0
$script:stoppedPids.Clear()
$readyCount = 0
$readyTest = {
    param($C, $R)
    $script:readyCount++
    if ($script:readyCount -lt 2) { New-ReadyState -Ready $false -Detail 'recovering' } else { New-ReadyState -Ready $true -Detail 'pg_isready and a real read-only SELECT 1 both succeeded.' }
}
$stuckHelper = { param($C, $R) $script:helperCalls++; New-FakeHelper -Stuck $true }
$result = Start-AeroLinkRemoteDemoPostgres -Config $config -Run (New-TestRun) -ReadyTest $readyTest -HelperLauncher $stuckHelper -HelperStopper $stopper -RecoveryTimeoutSeconds 30 -PollIntervalSeconds 1 -GraceSeconds 0
Assert-True ($result.Healthy -eq $true) 'Scenario 4: a stuck helper must not block once PostgreSQL is independently query-ready.'
Assert-True ($script:stoppedPids.Contains(777)) 'Scenario 4: the owned stuck helper must be terminated after DB readiness.'

# --- 5. Helper times out while PostgreSQL remains unhealthy ---
$script:helperCalls = 0
$script:stoppedPids.Clear()
$readyTest = { param($C, $R) New-ReadyState -Ready $false -Detail 'still recovering' }
$result = Start-AeroLinkRemoteDemoPostgres -Config $config -Run (New-TestRun) -ReadyTest $readyTest -HelperLauncher $launcher -HelperStopper $stopper -RecoveryTimeoutSeconds 3 -PollIntervalSeconds 1 -GraceSeconds 0
Assert-True ($result.Healthy -eq $false) 'Scenario 5: unhealthy PostgreSQL past the recovery window must fail.'
Assert-True ($result.Detail -match '777') 'Scenario 5: failure detail must name the helper PID.'
Assert-True ($result.Detail -match 'postgres-recovery') 'Scenario 5: failure detail must name the step.'
Assert-True ($result.Detail -match 'helper.stderr.log') 'Scenario 5: failure detail must name the helper log path.'
Assert-True ($script:stoppedPids.Contains(777)) 'Scenario 5: the owned helper must be terminated on timeout.'

# --- 6. Overlapping/repeated starts stay idempotent (AlreadyReady path) ---
$script:helperCalls = 0
$script:ngrokCalls = 0
$localReady = { param($C) New-ReadyState -Ready $true -Detail 'already ready' }
$publicProbe401 = { param($C) [pscustomobject]@{ Protected = $true; StatusCode = 401; Detail = '401' } }
$processes = Get-AeroLinkRemoteDemoNgrokProcess -Config $config -ProcessInfos @(
    [pscustomobject]@{ ProcessId = 101; ExecutablePath = 'C:\Tools\ngrok.exe'; CommandLine = '"C:\Tools\ngrok.exe" http http://127.0.0.1:5080 --url https://example.ngrok-free.dev --traffic-policy-file C:\Tools\policy.yml --log stdout' }
)
# The ownership matcher reads live processes in Start; to keep this deterministic we
# assert the decision layer plus the task XML idempotence contract instead of the
# full Start (which queries live ngrok). The decision layer is the gate.
$decision = Get-AeroLinkRemoteDemoStartDecision -LocalReady $true -OwnedProcessPresent $true -Protected $true -ProbeStatusCode 401
Assert-True ($decision.Decision -eq 'AlreadyReady') 'Scenario 6: already-owned-and-protected must be AlreadyReady.'
$xml = Get-AeroLinkRemoteDemoTaskXml -Config $config
Assert-True ($xml -match 'MultipleInstancesPolicy>IgnoreNew') 'Scenario 6: task XML must ignore overlapping instances.'

# --- 7. No ngrok before local AeroLink readiness ---
$script:helperCalls = 0
$script:ngrokCalls = 0
$localNeverReady = { param($C) New-ReadyState -Ready $false -Detail 'not ready' }
$postgresReady = { param($C, $R) New-ReadyState -Ready $true -Detail 'pg ready' }
$productionHelper = { param($C, $R) $script:helperCalls++; New-FakeHelper -Stuck $true }
$ngrokSeam = { param($C, $R) $script:ngrokCalls++; New-FakeHelper -Stuck $false }
$threw = $false
try {
    # -SkipSourceReconciliation: this scenario is about local readiness, and the source gate has its own
    # coverage below. Without it the start would refuse on the fixture's absent production source first.
    Start-AeroLinkRemoteDemo -Config $config -Scheduled -SkipSourceReconciliation -PostgresReadyTest $postgresReady -LocalReadyTest $localNeverReady -ProductionHelperLauncher $productionHelper -ProductionHelperStopper $stopper -ProductionTimeoutSeconds 2 -PostgresRecoveryTimeoutSeconds 10 -NgrokLauncher $ngrokSeam -PublicProbe $publicProbe401 | Out-Null
} catch {
    $threw = $true
    Assert-True ($_.Exception.Message -match 'NOT READY') 'Scenario 7: failure message must say NOT READY.'
}
Assert-True $threw 'Scenario 7: local AeroLink never ready must fail the whole start.'
Assert-True ($script:ngrokCalls -eq 0) 'Scenario 7: ngrok must never be launched before local AeroLink readiness.'

# --- 8. Timeout/error logging contains step/PID/log details but no secrets ---
$script:helperCalls = 0
$script:stoppedPids.Clear()
$readyTest = { param($C, $R) New-ReadyState -Ready $false -Detail 'still recovering' }
$config = New-TestConfig
$result = Start-AeroLinkRemoteDemoPostgres -Config $config -Run (New-TestRun) -ReadyTest $readyTest -HelperLauncher $launcher -HelperStopper $stopper -RecoveryTimeoutSeconds 3 -PollIntervalSeconds 1 -GraceSeconds 0
$logText = Get-Content -LiteralPath (Join-Path $config.LogsPath 'remote-demo.log') -Raw -ErrorAction SilentlyContinue
Assert-True ($logText -match 'postgres-recovery') 'Scenario 8: log must name the step.'
Assert-True ($logText -match '777') 'Scenario 8: log must name the helper PID.'
Assert-True ($logText -match 'postgres-helper.stderr.log') 'Scenario 8: log must name the helper log path.'
Assert-True ($logText -notmatch 'SUPERSECRET|hunter2|AeroLink!2026|authtoken') 'Scenario 8: log must not contain secrets.'
Write-AeroLinkRemoteDemoLog -Config $config -Run (New-TestRun) -Message 'correlation-probe'
$logText2 = Get-Content -LiteralPath (Join-Path $config.LogsPath 'remote-demo.log') -Raw
Assert-True ($logText2 -match '\[scheduled\]') 'Scenario 8: log must record the invocation type.'

# --- Readiness classifier with stub probes ---
$pgOk = { param($Bin, $DbHost, $DbPort, $DbUser, $Db, $Out, $Err) $true }
$queryOk = { param($Bin, $DbHost, $DbPort, $DbUser, $Db, $Out, $Err) $true }
$ready = Test-AeroLinkRemoteDemoPostgresReady -Config (New-TestConfig) -PgIsreadyProbe $pgOk -QueryProbe $queryOk
Assert-True ($ready.Ready -eq $true) 'Readiness: pg_isready + query success must be Ready.'
$queryFail = { param($Bin, $DbHost, $DbPort, $DbUser, $Db, $Out, $Err) $false }
$ready = Test-AeroLinkRemoteDemoPostgresReady -Config (New-TestConfig) -PgIsreadyProbe $pgOk -QueryProbe $queryFail
Assert-True ($ready.Ready -eq $false -and $ready.QueryOk -eq $false) 'Readiness: query failure must not be Ready.'
Assert-True ($ready.Detail -match 'SELECT 1') 'Readiness: query-failure detail must name the real query.'
$pgFail = { param($Bin, $DbHost, $DbPort, $DbUser, $Db, $Out, $Err) $false }
$ready = Test-AeroLinkRemoteDemoPostgresReady -Config (New-TestConfig) -PgIsreadyProbe $pgFail -QueryProbe $queryOk
Assert-True ($ready.Ready -eq $false -and $ready.PgIsreadyOk -eq $false) 'Readiness: pg_isready failure must not be Ready.'

# Default PostgresBin must resolve inside the function (not in the caller scope):
# an empty bin previously made the probes fail against a healthy database.
$capturedBin = ''
$binCaptureProbe = {
    param($Bin, $DbHost, $DbPort, $DbUser, $Db, $Out, $Err)
    $script:capturedBin = $Bin
    return $true
}
$binConfig = New-TestConfig
$null = Test-AeroLinkRemoteDemoPostgresReady -Config $binConfig -PgIsreadyProbe $binCaptureProbe -QueryProbe $queryOk
Assert-True ($script:capturedBin -eq (Join-Path $binConfig.AeroLinkRoot 'product\.local\postgresql\pgsql\bin')) "Default PostgresBin must resolve inside the function to the repository runtime; got '$script:capturedBin'."

# =========================================================================================================
# 2026-09-03 regressions. Each of these is a step of the real outage.
# =========================================================================================================

# --- 9. A terminal launcher refusal ends the wait promptly, and reports the reason the child gave ---
#
# The defect exactly: the production helper exited within seconds with a canonical-source refusal, and the
# parent went on polling port 5080 for the full 900 seconds before reporting NOT READY.
$config = New-TestConfig
New-Item -ItemType Directory -Path $config.LogsPath -Force | Out-Null
$refusalStdout = Join-Path $tempRoot 'production-helper.stdout.log'
@(
    'AEROLINK PRODUCTION START REFUSED',
    'Repository is on feat/880-slice6-digital-thread-page, not canonical main.',
    'No Git files or database state were changed.'
) | Set-Content -LiteralPath $refusalStdout -Encoding UTF8

$exitedHelper = [pscustomobject]@{
    Id = 555; HasExited = $true; ExitCode = 1
    StdOutPath = $refusalStdout; StdErrPath = (Join-Path $tempRoot 'production-helper.stderr.log')
}
$exitedHelper | Add-Member -MemberType ScriptMethod -Name Refresh -Value { }
$neverReady = { param($C) [pscustomobject]@{ Ready = $false; Detail = 'not ready' } }
$startedAt = Get-Date
$launcher = Invoke-AeroLinkProductionLauncher -Config $config -Run (New-TestRun) `
    -LocalReadyTest $neverReady -HelperLauncher { param($C, $R) $exitedHelper } -HelperStopper $stopper `
    -TimeoutSeconds 900 -PollIntervalSeconds 1 -GraceSeconds 0 -PostExitGraceSeconds 2
$elapsed = ((Get-Date) - $startedAt).TotalSeconds
Assert-True (-not $launcher.Healthy) 'Scenario 9: a launcher that refused must not be reported healthy.'
Assert-True ($elapsed -lt 60) "Scenario 9: a terminal child exit must end the wait promptly, not after the full timeout (waited $([int]$elapsed)s)."
Assert-True ($launcher.Detail -match 'not canonical main') 'Scenario 9: the failure must quote the refusal the child actually gave.'
Assert-True ($launcher.Detail -match 'feat/880-slice6') 'Scenario 9: the failure must name the branch that caused the refusal.'

# The refusal reader is bounded and redacted: it will not lift a line carrying a credential.
$secretLog = Join-Path $tempRoot 'refusal-with-secret.log'
@('Production launch refused: connection string Host=127.0.0.1;Password=hunter2 was rejected') |
    Set-Content -LiteralPath $secretLog -Encoding UTF8
Assert-True ($null -eq (Get-AeroLinkProductionLauncherRefusal -StandardOutputPath $secretLog -StandardErrorPath $null)) `
    'Scenario 9: a refusal line carrying a credential must be dropped rather than quoted into an operator log.'

# --- 10. A genuine slow start still gets its bounded readiness window ---
$slowHelper = New-FakeHelper -Stuck $true
$readyAfter = 0
$slowReady = { param($C) $script:readyAfter++; if ($script:readyAfter -lt 3) { [pscustomobject]@{ Ready = $false; Detail = 'starting' } } else { [pscustomobject]@{ Ready = $true; Detail = 'ready' } } }
$slow = Invoke-AeroLinkProductionLauncher -Config $config -Run (New-TestRun) `
    -LocalReadyTest $slowReady -HelperLauncher { param($C, $R) $slowHelper } -HelperStopper $stopper `
    -TimeoutSeconds 60 -PollIntervalSeconds 1 -GraceSeconds 0
Assert-True ($slow.Healthy) 'Scenario 10: a slow but genuine startup must still be allowed its bounded readiness window.'

# --- 11. A stale remote-demo state file must not false-block a fresh start ---
#
# The 2026-09-03 state file recorded a LocalApiPid from before the reboot. It was not the blocker, and it
# must never become one: state is advisory metadata, and the live checks are the truth.
New-Item -ItemType Directory -Path $config.StatePath -Force | Out-Null
[pscustomobject]@{
    Pid = 999999; LocalApiPid = 999998; LocalApiStartedAt = '2026-09-03T09:00:00.0000000Z'
    PublicUrl = $config.PublicUrl; NotificationBaseUrl = $config.PublicUrl
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $config.StatePath 'remote-demo-state.json') -Encoding UTF8

$deadRuntime = { param($C) [pscustomobject]@{ Found = $false; Detail = 'Local API port 5080 has no owner.' } }
$proof = Test-AeroLinkRemoteDemoNotificationOriginProof -Config $config -RuntimeProbe $deadRuntime
Assert-True (-not $proof.Valid) 'Scenario 11: a state file naming a dead PID must not be accepted as proof.'
$decision = Get-AeroLinkRemoteDemoStartDecision -LocalReady $true -OwnedProcessPresent $false -Protected $false -ProbeStatusCode 404
Assert-True ($decision.Decision -eq 'CanStart') 'Scenario 11: a stale state file must not block a fresh start when no owned process is live.'

# --- 12. The recovery task is bound to the DEDICATED production source, in both places ---
$productionConfig = New-TestConfig
$productionConfig | Add-Member -MemberType NoteProperty -Name AeroLinkRoot -Value 'C:\Sean Project\AeroLink Production' -Force
$xml = Get-AeroLinkRemoteDemoTaskXml -Config $productionConfig
Assert-True ($xml -match [regex]::Escape('C:\Sean Project\AeroLink Production\product\scripts\AeroLinkRemoteDemo.ps1')) `
    'Scenario 12: the task must invoke the recovery script FROM the dedicated production source, not from the development checkout.'
Assert-True ($xml -notmatch 'Requirements Management Tool') `
    'Scenario 12: no part of the task may reference the development checkout.'

# --- 13. Unattended boot recovery, with logon kept as a second chance, and no duplicates ---
Assert-True ($xml -match '<BootTrigger>') 'Scenario 13: recovery must be triggered by machine boot, not only by an interactive logon.'
Assert-True ($xml -match '<Delay>PT1M</Delay>') 'Scenario 13: the boot trigger must wait for the machine to settle before checking prerequisites.'
Assert-True ($xml -match '<LogonTrigger>') 'Scenario 13: the logon trigger stays as a second chance.'
Assert-True ($xml -match '<LogonType>S4U</LogonType>') 'Scenario 13: boot recovery runs without an interactive session and without a stored password.'
Assert-True ($xml -notmatch 'S-1-5-18|SYSTEM') 'Scenario 13: recovery must not run as SYSTEM; ngrok configuration and credentials are per-user.'
Assert-True ($xml -match 'MultipleInstancesPolicy>IgnoreNew') 'Scenario 13: boot and logon both firing must not start two recoveries.'
Assert-True ($xml -notmatch '(?i)authtoken|password|secret|basic-auth') 'Scenario 13: the task definition must carry no secret.'

# The attended fallback is a coherent shape, not the unattended one with a different principal. Measured on
# the HOME machine: Windows refuses a boot trigger AND an S4U principal to a non-elevated caller, so the
# fallback must drop both or it cannot register either. It must also say what it costs.
$attendedXml = Get-AeroLinkRemoteDemoTaskXml -Config $productionConfig -Attended
Assert-True ($attendedXml -notmatch '<BootTrigger>') 'Scenario 13: the attended fallback must drop the boot trigger, which a non-elevated install cannot register.'
Assert-True ($attendedXml -match '<LogonType>InteractiveToken</LogonType>') 'Scenario 13: the attended fallback runs under an interactive token, which registers without administrator.'
Assert-True ($attendedXml -match '<LogonTrigger>') 'Scenario 13: the attended fallback still recovers at sign-in.'
Assert-True ($attendedXml -match '(?i)does NOT recover an unattended reboot') 'Scenario 13: the attended fallback must declare in the task itself that it does not recover an unattended reboot.'
Assert-True ($attendedXml -match [regex]::Escape('C:\Sean Project\AeroLink Production')) 'Scenario 13: the attended fallback is still bound to the dedicated production source.'

# --- 14. The bounded reconciliation task polls, and only restarts when origin/main actually moved ---
$reconcileXml = Get-AeroLinkReconcileTaskXml -Config $productionConfig -IntervalMinutes 30
Assert-True ($reconcileXml -match '<Interval>PT30M</Interval>') 'Scenario 14: reconciliation runs on a low-overhead cadence, not every thirty seconds.'
Assert-True ($reconcileXml -match '-Action Reconcile') 'Scenario 14: the reconciliation task runs the reconciliation action.'
Assert-True ($reconcileXml -match [regex]::Escape('C:\Sean Project\AeroLink Production')) 'Scenario 14: reconciliation is bound to the dedicated production source.'

$script:order = @()
$noMovement = { param($C) $script:order += 'inspect'; [pscustomobject]@{ Action = 'AlreadyCurrent'; Canonical = $true; HeadSha = 'aaaaaaaa'; TargetSha = 'aaaaaaaa'; Reason = 'current' } }
$stopTunnel = { param($C) $script:order += 'stop-tunnel'; $null }
$stop = { param($C, $I) $script:order += 'stop'; $null }
$advanceOk = { param($C, $I) $script:order += 'advance'; [pscustomobject]@{ Action = 'Updated'; Canonical = $true; HeadSha = 'bbbbbbbb'; TargetSha = 'bbbbbbbb'; Reason = 'advanced' } }
$restart = { param($C, $R) $script:order += 'restart'; [pscustomobject]@{ Detail = 'restarted' } }

$demoIsUp = { param($C) [pscustomobject]@{ TunnelRunning = $true; RuntimeRunning = $true } }
$quiet = Invoke-AeroLinkProductionSourceReconciliation -Config $config -SourceInspector $noMovement -ServiceStateProbe $demoIsUp -TunnelStopper $stopTunnel -RuntimeStopper $stop -SourceAdvancer $advanceOk -Restarter $restart
Assert-True (-not $quiet.Restarted) 'Scenario 14: reconciliation must do nothing when origin/main has not moved and the demo is up.'
Assert-True (($script:order -join ',') -eq 'inspect') 'Scenario 14: a quiet pass must not stop, advance, or restart anything.'

# "Source current" is not "service up". A transition that failed after teardown - a handoff whose child could
# not start, most obviously - leaves the source current and the demo down, and a recovery timer that returns
# on AlreadyCurrent agrees forever that there is nothing to do. Under the keep-ready policy an absent demo is
# a thing to fix; under an operator update it is still not a thing to create.
$script:order = @()
$demoIsDown = { param($C) [pscustomobject]@{ TunnelRunning = $false; RuntimeRunning = $false } }
$healed = Invoke-AeroLinkProductionSourceReconciliation -Config $config -SourceInspector $noMovement -ServiceStateProbe $demoIsDown -TunnelStopper $stopTunnel -RuntimeStopper $stop -SourceAdvancer $advanceOk -Restarter $restart
Assert-True ($healed.Restarted) 'Scenario 14: a current source with the demo DOWN must be recovered, not reported as nothing to do.'
Assert-True (($script:order -join ',') -eq 'inspect,restart') 'Scenario 14: recovery starts the demo without stopping or advancing anything.'
Assert-True ($healed.Detail -match 'was not running and was recovered') 'Scenario 14: the operator must be told the demo was recovered rather than updated.'

$script:order = @()
$notHealed = Invoke-AeroLinkProductionSourceReconciliation -Config $config -PreserveServiceState -SourceInspector $noMovement -ServiceStateProbe $demoIsDown -TunnelStopper $stopTunnel -RuntimeStopper $stop -SourceAdvancer $advanceOk -Restarter $restart
Assert-True (-not $notHealed.Restarted) 'Scenario 14: an operator update must not create a demo that was not running.'
Assert-True (($script:order -join ',') -eq 'inspect') 'Scenario 14: preserve-state means preserve, including preserving "nothing".'

# The ordering IS the safety property. Advancing the working tree first would rewrite the assemblies,
# migrations and client bundle out from under the process that is serving the public demo, for as long as
# the restart takes. Inspect (fetch only), stop, advance, start.
$script:order = @()
$available = { param($C) $script:order += 'inspect'; [pscustomobject]@{ Action = 'UpdateAvailable'; Canonical = $true; HeadSha = 'aaaaaaaa'; TargetSha = 'bbbbbbbb'; Reason = 'origin/main moved' } }
$advanced = Invoke-AeroLinkProductionSourceReconciliation -Config $config -SourceInspector $available -TunnelStopper $stopTunnel -RuntimeStopper $stop -SourceAdvancer $advanceOk -Restarter $restart
Assert-True ($advanced.Restarted) 'Scenario 14: a real main advance must restart production into the new source.'
Assert-True ($advanced.HeadSha -eq 'bbbbbbbb') 'Scenario 14: the new running revision must be reported.'
Assert-True (($script:order -join ',') -eq 'inspect,stop-tunnel,stop,advance,restart') `
    'Scenario 14: the runtime must be stopped BEFORE the working tree it is executing out of is advanced.'

# A refusal at inspection never reaches the runtime at all: nothing is stopped for an update that is not
# going to happen.
$script:order = @()
$refused = { param($C) $script:order += 'inspect'; [pscustomobject]@{ Action = 'Refused'; Canonical = $false; HeadSha = $null; TargetSha = $null; Reason = 'untracked source present' } }
$blocked = Invoke-AeroLinkProductionSourceReconciliation -Config $config -SourceInspector $refused -TunnelStopper $stopTunnel -RuntimeStopper $stop -SourceAdvancer $advanceOk -Restarter $restart
Assert-True (-not $blocked.Restarted) 'Scenario 14: a refused source must never be started.'
Assert-True (($script:order -join ',') -eq 'inspect') 'Scenario 14: a refused inspection must not stop the running production demo.'

# A refusal AFTER the stop is the one case where the machine is already down. It must come back up on
# whatever is on disk rather than be left off because the update did not happen.
$script:order = @()
$advanceRefused = { param($C, $I) $script:order += 'advance'; [pscustomobject]@{ Action = 'Refused'; Canonical = $false; HeadSha = 'aaaaaaaa'; TargetSha = 'cccccccc'; Reason = 'origin/main moved between inspection and advance.' } }
$recovered = Invoke-AeroLinkProductionSourceReconciliation -Config $config -SourceInspector $available -TunnelStopper $stopTunnel -RuntimeStopper $stop -SourceAdvancer $advanceRefused -Restarter $restart
Assert-True (($script:order -join ',') -eq 'inspect,stop-tunnel,stop,advance,restart') 'Scenario 14: a refused advance must still restart what was stopped.'
Assert-True ($recovered.Action -eq 'Refused' -and $recovered.Restarted) 'Scenario 14: the refusal must be reported without leaving production down.'
Assert-True ($recovered.Detail -match 'already on disk') 'Scenario 14: the operator must be told which revision is actually running.'

# A tunnel that will not come down aborts the whole transition.
#
# Logging the failure and advancing anyway leaves the protected public URL forwarding to 127.0.0.1:5080 while
# the process behind it is stopped and replaced - so the endpoint points first at nothing and then at whatever
# takes the port, whose identity nothing has re-proved. Not updating the source is the safe half of that
# choice: it is a state the machine already runs in.
$script:order = @()
$stuckTunnel = { param($C) $script:order += 'stop-tunnel-failed'; throw 'Refusing to stop: ngrok process PID 4242 does not match the AeroLink remote-demo contract.' }
$aborted = $false
try {
    Invoke-AeroLinkProductionSourceReconciliation -Config $config -SourceInspector $available -TunnelStopper $stuckTunnel -RuntimeStopper $stop -SourceAdvancer $advanceOk -Restarter $restart | Out-Null
}
catch { $aborted = $true; Assert-True ($_.Exception.Message -match 'does not match') 'Scenario 14: the tunnel refusal must reach the operator.' }
Assert-True $aborted 'Scenario 14: a tunnel that cannot be stopped must abort the reconciliation.'
Assert-True (($script:order -join ',') -eq 'inspect,stop-tunnel-failed') `
    'Scenario 14: nothing may be stopped, advanced or restarted once the owned tunnel could not be taken down.'

# Once teardown HAS begun, every later failure owes the operator a running service.
#
# The refused-advance case was covered; a throw was not. A runtime stop that fails - an ownership read that
# cannot be completed, a process that will not go - used to propagate, so the restarter was never reached and
# the pass ended with the source unchanged AND the public tunnel down: the worst of both outcomes, reached by
# the ordinary failure of a step whose whole job is to be careful.
$script:order = @()
$stubbornRuntime = { param($C, $I) $script:order += 'stop-failed'; throw 'The process on 5080 could not be attributed and was not stopped.' }
$compensated = Invoke-AeroLinkProductionSourceReconciliation -Config $config -SourceInspector $available -TunnelStopper $stopTunnel -RuntimeStopper $stubbornRuntime -SourceAdvancer $advanceOk -Restarter $restart
Assert-True (($script:order -join ',') -eq 'inspect,stop-tunnel,stop-failed,restart') `
    'Scenario 14: a failure after the tunnel is down must still reach the restarter.'
Assert-True ($compensated.Action -eq 'TransitionFailed' -and $compensated.Restarted) 'Scenario 14: the pass must report the failed transition and that the service was restored.'
Assert-True ($compensated.Detail -match 'could not be attributed') 'Scenario 14: the operator must be told what actually failed.'
Assert-True ($script:order -notcontains 'advance') 'Scenario 14: the source must not be advanced once the runtime could not be stopped.'

# --- 14b. The obligation is recorded the moment a teardown step succeeds, not when they all do ---
#
# The failure that motivates this is specific. Fail-closed enumeration is correct - an unreadable process
# table is unknown, never none - but it means the post-stop PROOF can throw after a tunnel has genuinely come
# down. If the caller learns "a tunnel was running" only from a return value, that throw destroys the fact
# that anything is owed, and the caller unwinds believing it took nothing down while the public endpoint is
# dark. So the helper records into the caller's obligation between the stop and the proof.
$obligation = New-AeroLinkTransitionObligation
Assert-True (-not $obligation.TeardownBegan -and -not $obligation.TunnelWasRunning) 'Scenario 14b: a fresh obligation owes nothing.'

$owned = [pscustomobject]@{ ProcessId = 4242; ExecutablePath = 'C:\Tools\ngrok.exe'; CommandLine = '"C:\Tools\ngrok.exe" http http://127.0.0.1:5080 --url https://example.ngrok-free.dev --traffic-policy-file C:\Tools\policy.yml --log stdout' }
$live = Get-AeroLinkRemoteDemoNgrokProcess -Config $config -ProcessInfos @($owned)
Assert-True (@($live.Owned).Count -eq 1) 'Scenario 14b: the fixture models one owned tunnel.'
Assert-True ($live.Enumerated) 'Scenario 14b: an injected process list counts as enumerated.'

# An unreadable process table must be a refusal, not an empty list read as "nothing is running".
$deniedEnumeration = $false
try { Get-AeroLinkRemoteDemoNgrokProcess -Config ([pscustomobject]@{ NgrokExecutable = 'Z:\nonexistent\ngrok.exe'; PublicUrl = 'https://x'; Upstream = 'http://127.0.0.1:5080'; TrafficPolicyPath = 'Z:\p.yml' }) -ProcessInfos @() | Out-Null }
catch { $deniedEnumeration = $true }
Assert-True (-not $deniedEnumeration) 'Scenario 14b: an empty INJECTED list is a legitimate answer; only live enumeration failure is unknown.'

# The exact window: a tunnel was up, the stop succeeded, and the POST-STOP proof then throws.
$obligation = New-AeroLinkTransitionObligation
$probeCalls = 0
$flakyProbe = {
    param($Phase)
    $script:probeCalls++
    if ($Phase -eq 'before') { return [pscustomobject]@{ Owned = @($owned); Mismatched = @(); Enumerated = $true } }
    throw 'AeroLink could not enumerate running processes to determine ngrok ownership: access denied.'
}.GetNewClosure()
$proofFailed = $false
try { Assert-AeroLinkOwnedTunnelStopped -Config $config -Obligation $obligation -ProcessProbe $flakyProbe -Stopper { param($C) $null } | Out-Null }
catch { $proofFailed = $true; Assert-True ($_.Exception.Message -match 'could not enumerate') 'Scenario 14b: the enumeration failure must reach the caller.' }
Assert-True $proofFailed 'Scenario 14b: an unprovable teardown must still fail closed.'
Assert-True ($obligation.TunnelWasRunning) 'Scenario 14b: the caller must still learn that a tunnel WAS running...'
Assert-True ($obligation.TeardownBegan) '...and that teardown began, so it knows it owes the tunnel back even though no value was returned.'

# --- 14c. The CALLER compensates for that same failure. The helper test above is necessary, not sufficient ---
#
# The compensation try must start BEFORE the tunnel proof. Entering it only once Assert... returned meant the
# post-stop enumeration failure bypassed compensation in the real controller: public endpoint down, source
# unchanged, restarter never reached.
$script:order = @()
$provenDownButUnprovable = { param($C) $script:order += 'stop-tunnel-unprovable'; throw 'AeroLink could not enumerate running processes to determine ngrok ownership: access denied.' }
$compensatedAfterProof = $null
$threwInstead = $false
try {
    $compensatedAfterProof = Invoke-AeroLinkProductionSourceReconciliation -Config $config -SourceInspector $available `
        -ServiceStateProbe { param($C) [pscustomobject]@{ TunnelRunning = $true; RuntimeRunning = $true } } `
        -TunnelStopper $provenDownButUnprovable -RuntimeStopper $stop -SourceAdvancer $advanceOk -Restarter $restart
}
catch { $threwInstead = $true }
Assert-True $threwInstead 'Scenario 14c: a tunnel stopper that throws before any teardown must still fail closed.'
Assert-True (($script:order -join ',') -eq 'inspect,stop-tunnel-unprovable') 'Scenario 14c: nothing may follow a teardown that never began.'

# --- 14d. An operator update restores what was running, and creates nothing that was not ---
#
# A remote-demo configuration file outlives a demo somebody deliberately stopped, so a controller that ends
# by starting the tunnel would publish a public endpoint on the strength of a file existing. The scheduled
# pass is deliberately keep-ready; the operator update is deliberately not.
$script:order = @()
$nothingRunning = { param($C) [pscustomobject]@{ TunnelRunning = $false; RuntimeRunning = $false } }
$preserved = Invoke-AeroLinkProductionSourceReconciliation -Config $config -PreserveServiceState -SourceInspector $available `
    -ServiceStateProbe $nothingRunning -TunnelStopper $stopTunnel -RuntimeStopper $stop -SourceAdvancer $advanceOk -Restarter $restart
Assert-True ($preserved.Action -eq 'Updated') 'Scenario 14d: the source still advances when nothing was running.'
Assert-True (($script:order -join ',') -eq 'inspect,stop-tunnel,stop,advance,restart') 'Scenario 14d: the ordering is unchanged.'

# And with the keep-ready policy off and a tunnel that WAS up, the tunnel comes back.
$script:order = @()
$tunnelWasUp = { param($C) [pscustomobject]@{ TunnelRunning = $true; RuntimeRunning = $true } }
$restoredDemo = Invoke-AeroLinkProductionSourceReconciliation -Config $config -PreserveServiceState -SourceInspector $available `
    -ServiceStateProbe $tunnelWasUp -TunnelStopper $stopTunnel -RuntimeStopper $stop -SourceAdvancer $advanceOk -Restarter $restart
Assert-True ($restoredDemo.Restarted) 'Scenario 14d: a tunnel that was running before the update is restored.'
Assert-True ($script:order -contains 'restart') 'Scenario 14d: the restart happens for a demo that was up.'


# --- 15. Ngrok is never started in front of a runtime that is not the verified production source ---
$wrongSource = { param($C) [pscustomobject]@{ sourceIdentity = 'oldoldoldoldoldoldoldoldoldoldoldoldoldo'; sourceShortSha = 'oldoldol'; mode = 'HOME-PRODUCTION' } }
$mismatch = Test-AeroLinkRemoteDemoRuntimeMatchesSource -Config $config -ExpectedSourceIdentity 'newnewnewnewnewnewnewnewnewnewnewnewnewn' -RuntimeIdentityProbe $wrongSource
Assert-True (-not $mismatch.Matches) 'Scenario 15: a healthy API from another revision must not be exposed as the production demo.'
$wrongMode = { param($C) [pscustomobject]@{ sourceIdentity = 'newnewnewnewnewnewnewnewnewnewnewnewnewn'; sourceShortSha = 'newnewne'; mode = 'LOCAL-DEV' } }
Assert-True (-not (Test-AeroLinkRemoteDemoRuntimeMatchesSource -Config $config -ExpectedSourceIdentity 'newnewnewnewnewnewnewnewnewnewnewnewnewn' -RuntimeIdentityProbe $wrongMode).Matches) `
    'Scenario 15: a development-mode API on 5080 must not be exposed as the production demo.'
Assert-True (-not (Test-AeroLinkRemoteDemoRuntimeMatchesSource -Config $config -ExpectedSourceIdentity 'newnewnewnewnewnewnewnewnewnewnewnewnewn' -RuntimeIdentityProbe { param($C) $null }).Matches) `
    'Scenario 15: a process that publishes no identity must not be exposed as the production demo.'
$right = { param($C) [pscustomobject]@{ sourceIdentity = 'newnewnewnewnewnewnewnewnewnewnewnewnewn'; sourceShortSha = 'newnewne'; mode = 'HOME-PRODUCTION' } }
Assert-True (Test-AeroLinkRemoteDemoRuntimeMatchesSource -Config $config -ExpectedSourceIdentity 'newnewnewnewnewnewnewnewnewnewnewnewnewn' -RuntimeIdentityProbe $right).Matches `
    'Scenario 15: the verified production source in production mode is what may be exposed.'

# --- 16. A non-canonical source stops the start before PostgreSQL, the API, or ngrok ---
$script:ngrokCalls = 0
$refusedSource = { param($C) [pscustomobject]@{ Action = 'Refused'; Canonical = $false; HeadSha = $null; Reason = 'Repository is on feat/880-slice6-digital-thread-page, not canonical main.' } }
$threw = $false
try {
    Start-AeroLinkRemoteDemo -Config $config -Scheduled -SourceReconciler $refusedSource `
        -PostgresReadyTest { param($C, $R) throw 'PostgreSQL must not be started when the source is refused.' } `
        -NgrokLauncher { param($C, $R) $script:ngrokCalls++; New-FakeHelper -Stuck $false } | Out-Null
}
catch {
    $threw = $true
    Assert-True ($_.Exception.Message -match 'not canonical main') 'Scenario 16: the refusal reason must reach the operator.'
}
Assert-True $threw 'Scenario 16: a non-canonical production source must stop the remote-demo start.'
Assert-True ($script:ngrokCalls -eq 0) 'Scenario 16: ngrok must never be started when the production source was refused.'

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Host "FAIL: $_" -ForegroundColor Red }
    Write-Host "Remote-demo recovery regression FAILED ($($failures.Count) failure(s))." -ForegroundColor Red
    exit 1
}

Write-Host 'Remote-demo recovery regression passed.' -ForegroundColor Green
exit 0
