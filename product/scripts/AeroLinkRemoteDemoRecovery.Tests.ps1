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

$restarts = 0
$noMovement = { param($C) [pscustomobject]@{ Action = 'AlreadyCurrent'; Canonical = $true; HeadSha = 'aaaaaaaa'; Reason = 'current' } }
$restart = { param($C, $R) $script:restarts++; [pscustomobject]@{ Detail = 'restarted' } }
$quiet = Invoke-AeroLinkProductionSourceReconciliation -Config $config -SourceReconciler $noMovement -Restarter $restart
Assert-True (-not $quiet.Restarted -and $script:restarts -eq 0) 'Scenario 14: reconciliation must do nothing when origin/main has not moved.'

$moved = { param($C) [pscustomobject]@{ Action = 'Updated'; Canonical = $true; HeadSha = 'bbbbbbbb'; Reason = 'advanced' } }
$advanced = Invoke-AeroLinkProductionSourceReconciliation -Config $config -SourceReconciler $moved -Restarter $restart
Assert-True ($advanced.Restarted -and $script:restarts -eq 1) 'Scenario 14: a real main advance must restart production into the new source.'
Assert-True ($advanced.HeadSha -eq 'bbbbbbbb') 'Scenario 14: the new running revision must be reported.'

$refused = { param($C) [pscustomobject]@{ Action = 'Refused'; Canonical = $false; HeadSha = $null; Reason = 'untracked source present' } }
$blocked = Invoke-AeroLinkProductionSourceReconciliation -Config $config -SourceReconciler $refused -Restarter $restart
Assert-True (-not $blocked.Restarted -and $script:restarts -eq 1) 'Scenario 14: a refused source must never be started.'

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
