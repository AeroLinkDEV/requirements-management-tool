#Requires -Version 5.1
<#
    Canonical Windows launcher for changed-area validation.

    The underlying planner remains the source of truth for classification, selected commands, safety
    reporting and execution. This launcher performs one plan-only probe before real execution so it can:

      * avoid rebuilding the API inside Playwright when Fast mode has already selected the solution build;
      * reject an incompatible Docker daemon before a PostgreSQL Full run spends time on unrelated gates.

    The probe is JSON + DryRun, so it starts no build/test/database/network operation and never fetches or
    rebases. Local execution remains non-authoritative; GitHub Actions still supplies merge evidence.
#>

$ErrorActionPreference = 'Stop'
$planner = Join-Path $PSScriptRoot 'Get-AeroLinkTestPlan.ps1'
if (-not (Test-Path -LiteralPath $planner -PathType Leaf)) {
    Write-Error "Changed-area planner not found: $planner"
    exit 1
}

$shell = Get-Command powershell.exe -ErrorAction SilentlyContinue
if (-not $shell) { $shell = Get-Command pwsh.exe -ErrorAction SilentlyContinue }
if (-not $shell) {
    Write-Error 'PowerShell is required to run AeroLink changed validation.'
    exit 1
}

$forwardArguments = @($args | ForEach-Object { [string]$_ })

function Test-HasArgument {
    param([Parameter(Mandatory)][string]$Name)
    return @($forwardArguments | Where-Object { $_.Equals($Name, [StringComparison]::OrdinalIgnoreCase) }).Count -gt 0
}

function Get-RequestedMode {
    $mode = 'Fast'
    for ($index = 0; $index -lt $forwardArguments.Count; $index++) {
        if ($forwardArguments[$index].Equals('-Mode', [StringComparison]::OrdinalIgnoreCase)) {
            if ($index + 1 -ge $forwardArguments.Count) { return $mode }
            return $forwardArguments[$index + 1]
        }
    }
    return $mode
}

function Invoke-PlannerProcess {
    param(
        [Parameter(Mandatory)][string[]]$PlannerArguments,
        [switch]$Capture
    )
    $childArguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $planner) + $PlannerArguments
    if ($Capture) {
        $savedPreference = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            $output = @(& $shell.Source @childArguments 2>&1)
            $exitCode = $LASTEXITCODE
        }
        finally {
            $ErrorActionPreference = $savedPreference
        }
        return [pscustomobject]@{ ExitCode = $exitCode; Output = $output }
    }

    # Stream the child planner's human output, but do not let that output become this function's return value.
    # The caller must receive one integer exit code rather than an array containing every console line.
    $savedPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $shell.Source @childArguments 2>&1 | ForEach-Object { Write-Host $_ }
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $savedPreference
    }
    return [int]$exitCode
}

function Assert-LinuxDockerForPostgreSqlFull {
    $docker = Get-Command docker.exe -ErrorAction SilentlyContinue
    if (-not $docker) { $docker = Get-Command docker -ErrorAction SilentlyContinue }
    if (-not $docker) {
        throw 'Full validation selected PostgreSQL, but Docker is unavailable. No validation was started.'
    }

    $savedPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $dockerOutput = @(& $docker.Source info --format '{{.OSType}}' 2>&1)
        $dockerExitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $savedPreference
    }
    if ($dockerExitCode -ne 0) {
        throw 'Full validation selected PostgreSQL, but the Docker daemon could not be queried. No validation was started.'
    }

    $dockerOsType = (($dockerOutput | ForEach-Object { [string]$_ }) -join "`n").Trim().ToLowerInvariant()
    if ($dockerOsType -ne 'linux') {
        $reportedType = if ([string]::IsNullOrWhiteSpace($dockerOsType)) { 'unknown' } else { $dockerOsType }
        throw "Full validation selected PostgreSQL, but disposable postgres:17 requires a Linux-container Docker daemon; detected '$reportedType'. No validation was started."
    }
    Write-Host 'Docker preflight: Linux-container daemon available for disposable PostgreSQL.' -ForegroundColor Green
}

# JSON and DryRun are intrinsically plan-only in the underlying script. Delegate them directly so a request
# for zero-side-effect inspection never probes Docker or changes the process environment.
if ((Test-HasArgument '-Json') -or (Test-HasArgument '-DryRun')) {
    $exitCode = Invoke-PlannerProcess -PlannerArguments $forwardArguments
    exit $exitCode
}

# Ask the same planner for a no-side-effect decision before the real run. This avoids restating any path rules
# here: the optimization keys only off the planner's own backend/browser/PostgreSQL classification.
$probeArguments = @($forwardArguments) + @('-Json', '-DryRun')
$probeResult = Invoke-PlannerProcess -PlannerArguments $probeArguments -Capture
if ($probeResult.ExitCode -ne 0) {
    $probeResult.Output | ForEach-Object { Write-Host $_ }
    Write-Error "Changed-area plan probe failed with exit code $($probeResult.ExitCode). No validation was started."
    exit $probeResult.ExitCode
}

try {
    $probeText = (($probeResult.Output | ForEach-Object { [string]$_ }) -join "`n").Trim()
    $probe = $probeText | ConvertFrom-Json
}
catch {
    Write-Error 'Changed-area plan probe returned invalid JSON. No validation was started.'
    exit 1
}

$mode = Get-RequestedMode
if ($mode -ieq 'Full' -and [bool]$probe.classification.postgresql) {
    try { Assert-LinuxDockerForPostgreSqlFull }
    catch {
        Write-Error $_.Exception.Message
        exit 1
    }
}

$reuseBuiltApi = $mode -ieq 'Fast' -and [bool]$probe.classification.backend -and [bool]$probe.classification.browser
$previousSkipBuild = $env:AEROLINK_E2E_SKIP_BUILD
try {
    if ($reuseBuiltApi) {
        # Fast backend plans always build the Release solution before Domain/Infrastructure/browser steps.
        # If that build fails, the planner stops before browser smoke; if it succeeds, Playwright can safely
        # launch the exact build produced earlier in this same validation run instead of compiling it again.
        $env:AEROLINK_E2E_SKIP_BUILD = 'true'
        Write-Host 'Fast optimization: browser smoke will reuse the Release API built earlier in this validation run.' -ForegroundColor Green
    }
    $exitCode = Invoke-PlannerProcess -PlannerArguments $forwardArguments
}
finally {
    $env:AEROLINK_E2E_SKIP_BUILD = $previousSkipBuild
}

exit $exitCode
