[CmdletBinding()]
param(
    # How long to keep waiting for PostgreSQL to finish normal crash recovery and
    # become genuinely query-ready after the postmaster is launched.
    [int]$WaitSeconds = 300
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
# Persistent paths come from the installation authority, not from this checkout's own folder. A dedicated
# production source checkout points at the canonical HOME installation, and must start THAT cluster rather
# than initdb an empty second one beside its own source (#881).
Import-Module (Join-Path $PSScriptRoot 'AeroLinkInstallation.psm1') -Force
$installation = Get-AeroLinkInstallationPaths -ProductRoot $root
$bin = $installation.PostgresBin
$data = $installation.PostgresData
$log = $installation.PostgresLog
$logs = $installation.Logs
New-Item -ItemType Directory -Path $logs -Force | Out-Null
Import-Module (Join-Path $PSScriptRoot 'AeroLinkNativeRunner.psm1') -Force

function Test-AeroLinkPostgresAccepting {
    <#
      .SYNOPSIS True only when pg_isready succeeds AND, when -RequireQuery is set,
        a bounded real read-only SELECT 1 against the aerolink database returns 1.
      .DESCRIPTION
        A listening socket alone is not health: the #483 postmaster could accept
        TCP but could not spawn backends. Every probe is file-redirected and
        bounded so no scheduled invocation can block on inherited stdio handles.
    #>
    param([switch]$RequireQuery)
    $readyRun = Invoke-AeroLinkNativeCommand -FilePath (Join-Path $bin 'pg_isready.exe') `
        -ArgumentList @('-h', '127.0.0.1', '-p', '54329', '-U', 'postgres', '-d', 'postgres') `
        -StandardOutput (Join-Path $logs 'postgres-pg_isready.stdout.log') `
        -StandardError (Join-Path $logs 'postgres-pg_isready.stderr.log') `
        -TimeoutSeconds 30 -StepName 'pg_isready' -CaptureOutput
    if ($readyRun.ExitCode -ne 0) { return $false }
    if (-not $RequireQuery) { return $true }
    $queryRun = Invoke-AeroLinkNativeCommand -FilePath (Join-Path $bin 'psql.exe') `
        -ArgumentList @('-X', '-h', '127.0.0.1', '-p', '54329', '-U', 'postgres', '-d', 'aerolink', '-tA', '-q', '-c', 'SELECT 1') `
        -StandardOutput (Join-Path $logs 'postgres-query.stdout.log') `
        -StandardError (Join-Path $logs 'postgres-query.stderr.log') `
        -TimeoutSeconds 30 -StepName 'postgres real query' -CaptureOutput
    if ($queryRun.ExitCode -ne 0) { return $false }
    $value = ($queryRun.StdOutText -split "`r?`n" | Where-Object { $_ -ne '' } | Select-Object -Last 1)
    return ([string]$value).Trim() -eq '1'
}

function Test-AeroLinkDatabaseExists {
    $existsRun = Invoke-AeroLinkNativeCommand -FilePath (Join-Path $bin 'psql.exe') `
        -ArgumentList @('-X', '-h', '127.0.0.1', '-p', '54329', '-U', 'postgres', '-d', 'postgres', '-tA', '-q', '-c', "SELECT 1 FROM pg_database WHERE datname='aerolink'") `
        -StandardOutput (Join-Path $logs 'postgres-dbexists.stdout.log') `
        -StandardError (Join-Path $logs 'postgres-dbexists.stderr.log') `
        -TimeoutSeconds 30 -StepName 'postgres database existence' -CaptureOutput
    if ($existsRun.ExitCode -ne 0) { return $false }
    $value = ($existsRun.StdOutText -split "`r?`n" | Where-Object { $_ -ne '' } | Select-Object -Last 1)
    return ([string]$value).Trim() -eq '1'
}

function Test-AeroLinkPostgresInstalled {
    $catalogue = $installation.PostgresCatalogue
    return (Test-Path (Join-Path $bin 'postgres.exe')) -and (Test-Path $catalogue)
}

# Fast path: already genuinely query-ready.
if (Test-AeroLinkPostgresAccepting -RequireQuery) {
    Write-Host 'PostgreSQL is already accepting connections and answering real queries on 127.0.0.1:54329.'
    exit 0
}

if (-not (Test-AeroLinkPostgresInstalled)) {
    throw 'PostgreSQL is not completely installed under the repository runtime. Run product\scripts\Setup-Postgres.ps1.'
}

# First-time install only: a missing data directory is created once. The canonical
# cluster already exists and is never reinitialized by this script.
if (-not (Test-Path (Join-Path $data 'PG_VERSION'))) {
    $initRun = Invoke-AeroLinkNativeCommand -FilePath (Join-Path $bin 'initdb.exe') `
        -ArgumentList @('-D', $data, '-U', 'postgres', '-A', 'trust', '--encoding=UTF8', '--no-locale') `
        -StandardOutput (Join-Path $logs 'postgres-initdb.stdout.log') `
        -StandardError (Join-Path $logs 'postgres-initdb.stderr.log') `
        -TimeoutSeconds 300 -StepName 'initdb'
    if ($initRun.ExitCode -ne 0) { throw "initdb failed: $($initRun.Detail)" }
}

# Recover from a stale postmaster.pid left by an unclean shutdown/reboot, only for
# a recorded process that is no longer running or is the recognized local postmaster.
$pidFile = Join-Path $data 'postmaster.pid'
if (Test-Path $pidFile) {
    $recordedPid = [int](Get-Content $pidFile -TotalCount 1)
    $owner = Get-Process -Id $recordedPid -ErrorAction SilentlyContinue
    if ($owner -and $owner.ProcessName -like 'postgres*') {
        $expectedPostgres = [IO.Path]::GetFullPath((Join-Path $bin 'postgres.exe'))
        if (-not $owner.Path -or [IO.Path]::GetFullPath($owner.Path) -ne $expectedPostgres) {
            throw "The local PostgreSQL PID file refers to an unexpected process at '$($owner.Path)'. Refusing to touch it."
        }
        Write-Host "PostgreSQL process $recordedPid is running but not accepting connections. Performing a controlled restart." -ForegroundColor Yellow
        $stopRun = Invoke-AeroLinkNativeCommand -FilePath (Join-Path $bin 'pg_ctl.exe') `
            -ArgumentList @('-D', $data, '-m', 'fast', '-w', '-t', '20', 'stop') `
            -StandardOutput (Join-Path $logs 'postgres-stop.stdout.log') `
            -StandardError (Join-Path $logs 'postgres-stop.stderr.log') `
            -TimeoutSeconds 60 -StepName 'pg_ctl fast stop'
        if ($stopRun.ExitCode -ne 0) {
            Write-Host 'Controlled PostgreSQL shutdown did not complete; stopping the recognized local postmaster.' -ForegroundColor Yellow
            Stop-Process -Id $recordedPid -Force -ErrorAction SilentlyContinue
            Start-Sleep -Seconds 2
        }
        $owner = Get-Process -Id $recordedPid -ErrorAction SilentlyContinue
        if ($owner) { throw "Local PostgreSQL process $recordedPid could not be stopped safely. Restart Windows and try again." }
    }
    # The recorded PID is not a live repository postmaster (for example after a
    # reboot): the file is stale and pg_ctl will refuse to start over it.
    Remove-Item -LiteralPath $pidFile -Force
    Write-Host "Removed stale PostgreSQL PID file from process $recordedPid."
}

# Start the postmaster through the bounded runner. File redirection means the
# postmaster inherits file handles, never the scheduled task's stdio pipes, so the
# parent cannot block waiting on the server's lifetime.
$startRun = Invoke-AeroLinkNativeCommand -FilePath (Join-Path $bin 'pg_ctl.exe') `
    -ArgumentList @('-D', $data, '-l', $log, '-o', '-p 54329 -h 127.0.0.1', 'start') `
    -StandardOutput (Join-Path $logs 'postgres-start.stdout.log') `
    -StandardError (Join-Path $logs 'postgres-start.stderr.log') `
    -TimeoutSeconds 120 -StepName 'pg_ctl start'
if ($startRun.ExitCode -ne 0 -and -not (Test-AeroLinkPostgresAccepting)) {
    throw "PostgreSQL could not be started: $($startRun.Detail)"
}

# Bounded crash-recovery window: wait until pg_isready AND a real read-only query
# succeed. TCP listener presence alone is never treated as healthy.
$deadline = (Get-Date).AddSeconds($WaitSeconds)
$ready = $false
while ((Get-Date) -lt $deadline) {
    if (Test-AeroLinkPostgresAccepting) {
        if (-not (Test-AeroLinkDatabaseExists)) {
            $createRun = Invoke-AeroLinkNativeCommand -FilePath (Join-Path $bin 'createdb.exe') `
                -ArgumentList @('-h', '127.0.0.1', '-p', '54329', '-U', 'postgres', 'aerolink') `
                -StandardOutput (Join-Path $logs 'postgres-createdb.stdout.log') `
                -StandardError (Join-Path $logs 'postgres-createdb.stderr.log') `
                -TimeoutSeconds 60 -StepName 'createdb aerolink'
            if ($createRun.ExitCode -ne 0) { throw "The aerolink database could not be created: $($createRun.Detail)" }
        }
        if (Test-AeroLinkPostgresAccepting -RequireQuery) { $ready = $true; break }
    }
    Start-Sleep -Seconds 2
}
if (-not $ready) {
    throw "PostgreSQL did not become query-ready within $WaitSeconds seconds after start. Inspect $log and $logs\postgres-start.stderr.log"
}

Write-Host 'PostgreSQL is accepting connections and answering real queries on 127.0.0.1:54329.'
