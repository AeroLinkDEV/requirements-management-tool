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
    Start-AeroLinkRemoteDemo -Config $config -Scheduled -PostgresReadyTest $postgresReady -LocalReadyTest $localNeverReady -ProductionHelperLauncher $productionHelper -ProductionHelperStopper $stopper -ProductionTimeoutSeconds 2 -PostgresRecoveryTimeoutSeconds 10 -NgrokLauncher $ngrokSeam -PublicProbe $publicProbe401 | Out-Null
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

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Host "FAIL: $_" -ForegroundColor Red }
    Write-Host "Remote-demo recovery regression FAILED ($($failures.Count) failure(s))." -ForegroundColor Red
    exit 1
}

Write-Host 'Remote-demo recovery regression passed.' -ForegroundColor Green
exit 0
