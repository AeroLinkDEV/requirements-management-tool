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
    $pwsh = (Get-Command powershell.exe -ErrorAction SilentlyContinue)
    if (-not $pwsh) { $pwsh = (Get-Command pwsh.exe -ErrorAction Stop) }
    $output = & $pwsh.Source -NoProfile -ExecutionPolicy Bypass -File $scriptPath @Arguments 2>&1
    return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = ($output -join "`n") }
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
if (-not $originMainExists) {
    # actions/checkout tests a synthetic merge commit without fetching remote-tracking refs. Create only the
    # disposable ref needed by this contract, then remove it below; the wrapper itself must still do no fetch.
    & git -C $root update-ref $originMainRef HEAD
    if ($LASTEXITCODE -ne 0) { throw "Could not create disposable $originMainRef for the planner contract." }
}
try {
    $originPlan = Invoke-Plan @('-SinceOriginMain', '-Head', 'HEAD', '-DryRun')
    Assert-True ($originPlan.ExitCode -eq 0) "origin/main dry-run should succeed: $($originPlan.Output)"
    Assert-True ($originPlan.Output -match 'merge base:') 'origin/main mode must report the merge base.'
    Assert-True ($originPlan.Output -match 'origin/main is a local remote-tracking ref') 'origin/main mode must warn about stale local refs.'
    Assert-True ($originPlan.Output -match 'No\s+fetch\s+or\s+rebase\s+was\s+performed') 'origin/main mode must not silently fetch or rebase.'
}
finally {
    if (-not $originMainExists) {
        & git -C $root update-ref -d $originMainRef
        if ($LASTEXITCODE -ne 0) { throw "Could not remove disposable $originMainRef after the planner contract." }
    }
}

$jsonRun = Invoke-Plan @('-Paths', 'README.md', '-Json', '-DryRun')
Assert-True ($jsonRun.ExitCode -eq 0) "JSON dry-run should succeed: $($jsonRun.Output)"
$json = $jsonRun.Output | ConvertFrom-Json
Assert-True ($json.wrapper.mode -eq 'Fast') 'JSON output must identify the default Fast mode.'
Assert-True ($json.wrapper.safety.persistentPostgreSqlTouched -eq $false) 'JSON must prove PostgreSQL was untouched.'
Assert-True ($json.wrapper.safety.persistentEvidenceRootTouched -eq $false) 'JSON must prove evidence was untouched.'
Assert-True ($json.wrapper.safety.fetchOrRebasePerformed -eq $false) 'JSON must prove no fetch/rebase occurred.'
Assert-True ($json.classification.docsOnly -eq $true) 'README-only JSON plan should be documentation-only.'

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
