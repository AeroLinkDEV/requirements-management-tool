# The plumbing both launchers need, in one place.
#
# `Start-AeroLink.ps1` and `Start-AeroLinkProduction.ps1` start different things — a Vite dev server on two
# ports, and a compiled client served by the API on one — but the mechanics around that are identical: probe an
# endpoint, wait for it, take a port back off a dead process, start something and tail its log if it never comes
# up, make sure PostgreSQL exists. All of it was written twice.
#
# Copies drift, and these had. `Test-HttpEndpoint` accepted anything under 500 in one file and under 300 in the
# other; its probe timed out after two seconds in one and three in the other; `Wait-HttpEndpoint` defaulted to
# 75 seconds and to 120. None of those differences was written down, so nobody could tell which were meant.
# Worse, `Clear-StaleAeroLinkPort` existed as a parameterised function in the development launcher while the
# production launcher — the one other people run — carried an inlined copy hardcoded to port 5080.
#
# Where the two behaviours genuinely differed, the difference is now a named argument at the call site rather
# than a divergence between two files. Where it did not, it is settled here and said so.

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'AeroLinkNativeRunner.psm1') -Force

function Test-HttpEndpoint {
    <#
      .SYNOPSIS Whether an endpoint answers acceptably right now. Never throws; a dead port is a `$false`.
      .PARAMETER SuccessBelow
        The status code this call treats as still-healthy, exclusive. 300 means "2xx only", which is what a
        readiness probe wants. The development launcher passes 500 when asking whether the Vite server is up at
        all, where any answer that is not a server error means the port is live and serving.
    #>
    param(
        [Parameter(Mandatory)][string]$Uri,
        [int]$SuccessBelow = 300,
        [int]$TimeoutSec = 3
    )
    try {
        $response = Invoke-WebRequest -Uri $Uri -UseBasicParsing -TimeoutSec $TimeoutSec
        return $response.StatusCode -ge 200 -and $response.StatusCode -lt $SuccessBelow
    }
    catch { return $false }
}

function Wait-HttpEndpoint {
    <#
      .SYNOPSIS Blocks until an endpoint answers, or throws naming the service and how long it waited.
    #>
    param(
        [Parameter(Mandatory)][string]$Uri,
        [Parameter(Mandatory)][string]$ServiceName,
        [int]$TimeoutSeconds = 120,
        [int]$SuccessBelow = 300
    )
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        if (Test-HttpEndpoint -Uri $Uri -SuccessBelow $SuccessBelow) { return }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)
    throw "$ServiceName did not become ready within $TimeoutSeconds seconds."
}

function Clear-StaleAeroLinkPort {
    <#
      .SYNOPSIS Takes a port back from a previous AeroLink process, and refuses to take it from anything else.
      .DESCRIPTION
        Re-running a launcher while the last one is still up is the normal case, so the port is reclaimed rather
        than reported. What must not happen is killing somebody's unrelated process because it happened to be on
        5080 — so the owning command line is checked first and an unrecognised one is an error, not a casualty.
    #>
    param(
        [Parameter(Mandatory)][int]$Port,
        [Parameter(Mandatory)][string[]]$ExpectedCommandFragments
    )
    $listeners = @(Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue)
    foreach ($listener in $listeners) {
        $process = Get-CimInstance Win32_Process -Filter "ProcessId=$($listener.OwningProcess)" -ErrorAction SilentlyContinue
        $command = "$($process.ExecutablePath) $($process.CommandLine)"
        $recognized = $false
        foreach ($fragment in $ExpectedCommandFragments) {
            if ($command -like "*$fragment*") { $recognized = $true; break }
        }
        if (-not $recognized) {
            throw "Port $Port is occupied by another application (PID $($listener.OwningProcess)). Close it and run this launcher again."
        }
        Write-Host "      Stopping the AeroLink process already on port $Port (PID $($listener.OwningProcess))..." -ForegroundColor Yellow
        Stop-Process -Id $listener.OwningProcess -Force
        # The longer of the two waits the copies used. Reclaiming a port is not on the critical path, and a
        # start that races a not-quite-dead listener fails in a way nothing here would explain.
        Start-Sleep -Milliseconds 800
    }
}

function Start-AeroLinkService {
    <#
      .SYNOPSIS Starts a background service, waits for it, and shows its log if it never answers.
      .DESCRIPTION
        The tail is the point. A service that fails to start writes the reason to its own error log and nothing
        to the console, so without this the launcher reports only that something did not become ready within N
        seconds — true, and useless. Every caller wanted that behaviour and every caller wrote it out again.
    #>
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)]$ArgumentList,
        [Parameter(Mandatory)][string]$WorkingDirectory,
        [Parameter(Mandatory)][string]$StandardOutput,
        [Parameter(Mandatory)][string]$StandardError,
        [Parameter(Mandatory)][string]$ReadyUri,
        [Parameter(Mandatory)][string]$ServiceName,
        [int]$TimeoutSeconds = 120,
        [int]$SuccessBelow = 300,
        [int]$TailLines = 25,
        # Applied to this process before starting the child, which inherits it. Scoped to the run, so nothing
        # here outlives the launcher.
        [hashtable]$Environment
    )
    if ($Environment) {
        foreach ($entry in $Environment.GetEnumerator()) {
            [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
        }
    }
    Start-Process -FilePath $FilePath `
        -ArgumentList $ArgumentList `
        -WorkingDirectory $WorkingDirectory `
        -WindowStyle Hidden `
        -RedirectStandardOutput $StandardOutput `
        -RedirectStandardError $StandardError | Out-Null
    try {
        Wait-HttpEndpoint -Uri $ReadyUri -ServiceName $ServiceName -TimeoutSeconds $TimeoutSeconds -SuccessBelow $SuccessBelow
    }
    catch {
        $tail = if (Test-Path $StandardError) {
            (Get-Content $StandardError -Tail $TailLines) -join [Environment]::NewLine
        }
        else { "No $ServiceName error log was produced." }
        throw "$($_.Exception.Message)`n$ServiceName error log:`n$tail"
    }
}

function Assert-AeroLinkPostgres {
    <#
      .SYNOPSIS Makes sure PostgreSQL exists and is accepting connections, installing it the first time.
      .DESCRIPTION
        Installed here rather than left to a separate script, because a launcher whose first step is "now run a
        different script" is a launcher that does not launch. Setup is idempotent and returns immediately once
        PostgreSQL is present.

        Presence is judged by postgres.bki and not by the directory or the executable. A download interrupted
        part-way leaves both of those behind, and a server that starts against a half-extracted catalogue fails
        later and further away, with an error about the database rather than about the install.
    #>
    param([Parameter(Mandatory)][string]$ProductRoot)
    $catalogue = Join-Path $ProductRoot '.local\postgresql\pgsql\share\postgres.bki'
    if (-not (Test-Path $catalogue)) {
        Write-Host '      PostgreSQL is not installed on this machine yet. Installing it once (about 320 MB).' -ForegroundColor Yellow
        $setup = Invoke-AeroLinkChildScript -ScriptPath (Join-Path $PSScriptRoot 'Setup-Postgres.ps1') `
            -StandardOutput (Join-Path $ProductRoot '.local\logs\setup-postgres.stdout.log') `
            -StandardError (Join-Path $ProductRoot '.local\logs\setup-postgres.stderr.log') `
            -TimeoutSeconds 900 -StepName 'Setup-Postgres.ps1'
        if ($setup.ExitCode -ne 0) { throw "PostgreSQL could not be installed: $($setup.Detail)" }
        return
    }
    # Bounded, file-redirected child invocation: the postmaster must never inherit
    # this process's stdio pipes (the #483 scheduled-task hang) and the wait must
    # never be indefinite (crash recovery is bounded inside Start-Postgres.ps1).
    $start = Invoke-AeroLinkChildScript -ScriptPath (Join-Path $PSScriptRoot 'Start-Postgres.ps1') `
        -StandardOutput (Join-Path $ProductRoot '.local\logs\postgres-start.stdout.log') `
        -StandardError (Join-Path $ProductRoot '.local\logs\postgres-start.stderr.log') `
        -TimeoutSeconds 420 -StepName 'Start-Postgres.ps1'
    if ($start.ExitCode -ne 0) { throw "PostgreSQL could not be started: $($start.Detail)" }
}
