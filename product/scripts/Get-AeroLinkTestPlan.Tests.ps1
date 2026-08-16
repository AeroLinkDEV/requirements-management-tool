#Requires -Version 5.1
<#
    Deterministic contract coverage for the Windows #568 entry point. Every scenario supplies explicit paths or
    asks for a dry run, so it does not build, start a service, fetch, rebase, touch PostgreSQL, or write evidence.
#>
$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path $PSScriptRoot 'Get-AeroLinkTestPlan.ps1'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$failures = [System.Collections.Generic.List[string]]::new()

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { $script:failures.Add($Message) }
}

function Invoke-Plan([string[]]$Arguments) {
    # Some contract cases intentionally exercise a non-zero planner exit. Keep native stderr as captured
    # output rather than allowing the outer Stop preference to abort before the exit code can be asserted.
    $ErrorActionPreference = 'Continue'
    $pwsh = (Get-Command powershell.exe -ErrorAction SilentlyContinue)
    if (-not $pwsh) { $pwsh = (Get-Command pwsh.exe -ErrorAction Stop) }
    $output = & $pwsh.Source -NoProfile -ExecutionPolicy Bypass -File $scriptPath @Arguments 2>&1
    return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = ($output -join "`n") }
}

function Invoke-PlanFrom([string]$WorkingDirectory, [string[]]$Arguments) {
    Push-Location -LiteralPath $WorkingDirectory
    try { return Invoke-Plan $Arguments }
    finally { Pop-Location }
}

$plain = Invoke-Plan @('-Paths', 'PRODUCT\SRC\AeroLink.Infrastructure\Persistence\Thing.cs', '-Explain', '-DryRun')
Assert-True ($plain.ExitCode -eq 0) "Windows dry-run should succeed: $($plain.Output)"
Assert-True ($plain.Output -match 'persistent PostgreSQL') 'Dry-run must state persistent PostgreSQL safety.'
Assert-True ($plain.Output -match 'no fetch or rebase') 'Dry-run must state no fetch/rebase.'
Assert-True ($plain.Output -match 'no build, test, database, evidence, package restore, or network') 'Dry-run must state no network-capable work started.'
Assert-True ($plain.Output -match 'postgresql\s+True') 'Windows persistence path must select PostgreSQL.'
Assert-True ($plain.Output -match 'AeroLink.Infrastructure') 'Explain mode must print the changed path.'
Assert-True ($plain.Output -match 'Resource posture:') 'Human output must state resource posture.'
Assert-True ($plain.Output -match 'SQLite:') 'Human output must state SQLite posture.'
Assert-True ($plain.Output -match 'Browser:') 'Human output must state browser-process posture.'
Assert-True ($plain.Output -match 'AEROLINK_TEST_PLAN_RESULT=') 'Human output must include a compact copyable result.'

$originMainRef = 'refs/remotes/origin/main'
& git -C $root show-ref --verify --quiet $originMainRef
$originMainExists = $LASTEXITCODE -eq 0
$originMainSha = $null
if ($originMainExists) {
    $originMainSha = (& git -C $root rev-parse --verify $originMainRef 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($originMainSha)) { throw "Could not read $originMainRef for the planner contract." }
}

# First prove that a shallow checkout without origin/main fails explicitly and safely. The wrapper must not
# invent a base or fetch one. If this checkout already has the ref, remove it only for this short contract
# assertion and restore the exact SHA in finally.
if ($originMainExists) {
    & git -C $root update-ref -d $originMainRef
    if ($LASTEXITCODE -ne 0) { throw "Could not temporarily remove $originMainRef for the absent-ref contract." }
}
try {
    $missingOriginPlan = Invoke-PlanFrom ([System.IO.Path]::GetTempPath()) @('-SinceOriginMain', '-Head', 'HEAD', '-DryRun')
    Assert-True ($missingOriginPlan.ExitCode -ne 0) "Missing origin/main must fail: $($missingOriginPlan.Output)"
    Assert-True ($missingOriginPlan.Output -match 'local origin/main ref is required') 'Missing origin/main must explain the required local ref.'
    Assert-True ($missingOriginPlan.Output -match 'no fetch or rebase') 'Missing origin/main must not fetch or rebase.'
}
finally {
    if ($originMainExists) {
        & git -C $root update-ref $originMainRef $originMainSha
        if ($LASTEXITCODE -ne 0) { throw "Could not restore $originMainRef after the absent-ref contract." }
    }
}

# Then exercise the successful stale-warning path with a disposable local ref. This simulates a checkout
# that has a remote-tracking ref without granting the wrapper permission to refresh it.
& git -C $root update-ref $originMainRef HEAD
if ($LASTEXITCODE -ne 0) { throw "Could not create disposable $originMainRef for the stale-ref contract." }
try {
    # The wrapper resolves its repository from $PSScriptRoot, so the stale-ref warning must also work when
    # the operator launches the PowerShell entry point from an unrelated directory.
    $originPlan = Invoke-PlanFrom ([System.IO.Path]::GetTempPath()) @('-SinceOriginMain', '-Head', 'HEAD', '-DryRun')
    Assert-True ($originPlan.ExitCode -eq 0) "origin/main dry-run should succeed: $($originPlan.Output)"
    Assert-True ($originPlan.Output -match 'merge base:') 'origin/main mode must report the merge base.'
    Assert-True ($originPlan.Output -match 'origin/main is a local remote-tracking ref') 'origin/main mode must warn about stale local refs.'
    Assert-True ($originPlan.Output -match 'No\s+fetch\s+or\s+rebase\s+was\s+performed') 'origin/main mode must not silently fetch or rebase.'
}
finally {
    if ($originMainExists) {
        & git -C $root update-ref $originMainRef $originMainSha
    }
    else {
        & git -C $root update-ref -d $originMainRef
    }
    if ($LASTEXITCODE -ne 0) { throw "Could not restore $originMainRef after the stale-ref contract." }
}

$jsonRun = Invoke-Plan @('-Paths', 'README.md', '-Json', '-DryRun')
Assert-True ($jsonRun.ExitCode -eq 0) "JSON dry-run should succeed: $($jsonRun.Output)"
$json = $jsonRun.Output | ConvertFrom-Json
Assert-True ($json.wrapper.mode -eq 'Fast') 'JSON output must identify the default Fast mode.'
Assert-True ($json.wrapper.safety.persistentPostgreSqlTouched -eq $false) 'JSON must prove PostgreSQL was untouched.'
Assert-True ($json.wrapper.safety.persistentEvidenceRootTouched -eq $false) 'JSON must prove evidence was untouched.'
Assert-True ($json.wrapper.safety.fetchOrRebasePerformed -eq $false) 'JSON must prove no fetch/rebase occurred.'
Assert-True ($json.classification.docsOnly -eq $true) 'README-only JSON plan should be documentation-only.'
Assert-True ($json.wrapper.execution.status -eq 'not-run') 'JSON mode must report that execution did not run.'
Assert-True ($json.wrapper.execution.authoritative -eq $false) 'Local JSON output must remain non-authoritative.'
Assert-True ($json.wrapper.execution.timing.totalMs -eq 0) 'Plan-only JSON must not fabricate elapsed execution time.'
Assert-True ($null -ne $json.wrapper.execution.ciOnlyJobs) 'JSON must expose selected CI-only jobs.'

$fullPostgresJsonRun = Invoke-Plan @('-Paths', 'product\src\AeroLink.Infrastructure\Persistence\Migrations\0001_init.cs', '-Mode', 'Full', '-Json', '-DryRun')
Assert-True ($fullPostgresJsonRun.ExitCode -eq 0) "Full PostgreSQL dry-run should succeed without Docker: $($fullPostgresJsonRun.Output)"
$fullPostgresJson = $fullPostgresJsonRun.Output | ConvertFrom-Json
Assert-True ($fullPostgresJson.wrapper.execution.selectedCiJobs -contains 'postgresql-smoke') 'Full PostgreSQL plan must expose the selected PostgreSQL CI job.'
Assert-True ($fullPostgresJson.wrapper.execution.ciOnlyJobs -contains 'postgresql-smoke') 'A dry-run must report PostgreSQL as not executed.'
Assert-True ($fullPostgresJson.wrapper.execution.resources.persistentPostgreSqlTouched -eq $false) 'Full PostgreSQL dry-run must prove persistent PostgreSQL was untouched.'
Assert-True ($fullPostgresJson.wrapper.execution.resources.disposableDockerPostgreSql -match 'unique container') 'Full PostgreSQL output must describe its disposable Docker boundary.'

$broad = Invoke-Plan @('-Paths', 'product\test-planner\lib\classify.mjs', '-Json', '-DryRun')
Assert-True ($broad.ExitCode -eq 0) 'Planner self-change dry-run should succeed.'
$broadJson = $broad.Output | ConvertFrom-Json
foreach ($area in @('backend', 'client', 'browser', 'postgresql')) {
    Assert-True ($broadJson.classification.$area -eq $true) "Planner self-change must select $area."
}

Assert-True (Test-Path -LiteralPath (Join-Path $root 'TEST_AEROLINK_CHANGED.bat') -PathType Leaf) 'Friendly root BAT entry point is missing.'

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Host "FAIL: $_" -ForegroundColor Red }
    Write-Host "Windows test planner contract FAILED ($($failures.Count) failure(s))." -ForegroundColor Red
    exit 1
}

Write-Host 'Windows test planner contract passed.' -ForegroundColor Green
exit 0
