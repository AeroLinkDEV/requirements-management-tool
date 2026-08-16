#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$Base,
    [string]$Head = 'HEAD',
    [string[]]$Paths,
    [switch]$SinceOriginMain,
    [ValidateSet('Fast', 'Full')]
    [string]$Mode = 'Fast',
    [switch]$Explain,
    [switch]$Json,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

# This is the Windows-friendly entry point for #568. It asks the shared Node planner for one JSON decision,
# prints the safety preamble before any optional command, and only executes known repository commands. It never
# fetches, rebases, resets, connects to the persistent PostgreSQL instance, or writes under product/.local.

if ($SinceOriginMain -and $Base) {
    throw '-SinceOriginMain cannot be combined with -Base.'
}
if ($Paths -and ($Base -or $SinceOriginMain -or $PSBoundParameters.ContainsKey('Head'))) {
    throw '-Paths cannot be combined with -Base, -Head or -SinceOriginMain.'
}

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$planner = Join-Path $repositoryRoot 'product\test-planner\tools\plan.mjs'
$node = Get-Command node.exe -ErrorAction SilentlyContinue
if (-not $node) { throw 'Node.js is required to run the shared AeroLink test planner.' }
if (-not (Test-Path -LiteralPath $planner -PathType Leaf)) { throw "Shared planner not found: $planner" }

function Invoke-GitText {
    param([Parameter(Mandatory)][string[]]$Arguments)
    $output = & git @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw "git $($Arguments -join ' ') failed: $($output -join ' ')" }
    return ($output -join "`n").Trim()
}

$nodeArguments = @($planner, '--json', '--dry-run')
if ($Paths) {
    $nodeArguments += '--files'
    $nodeArguments += '--'
    $nodeArguments += $Paths
}
else {
    if ($SinceOriginMain) {
        $nodeArguments += '--since-origin-main'
    }
    else {
        $nodeArguments += '--base'
        $nodeArguments += $(if ($Base) { $Base } else { 'origin/main' })
    }
    if ($Head) {
        $nodeArguments += '--head'
        $nodeArguments += $Head
    }
}

Push-Location $repositoryRoot
try {
    $jsonOutput = & $node.Source @nodeArguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw "The shared planner failed: $($jsonOutput -join ' ')" }
    $plan = ($jsonOutput -join "`n") | ConvertFrom-Json
}
finally {
    Pop-Location
}

$baseRef = $plan.compact.source.base
$staleWarning = $null
if ($baseRef -eq 'origin/main') {
    $originMainSha = Invoke-GitText -Arguments @('rev-parse', 'origin/main')
    $staleWarning = "origin/main is a local remote-tracking ref at $originMainSha and may be stale. No fetch or rebase was performed; refresh it deliberately before relying on this plan."
}

$wrapperSafety = [ordered]@{
    persistentPostgreSqlTouched = $false
    persistentEvidenceRootTouched = $false
    fetchOrRebasePerformed = $false
    networkAccessPerformed = $false
    # JSON is a reporting mode and returns before optional execution, so it is intrinsically plan-only.
    dryRun = [bool]($DryRun -or $Json)
    remainingFullEvidence = 'GitHub Actions full gate remains authoritative; local Fast output never satisfies merge evidence.'
}
$resourcePosture = [ordered]@{
    postgresql = 'never touched by this wrapper; PostgreSQL-sensitive merge evidence remains CI-only'
    sqlite = 'Full mode uses the repository broader disposable SQLite/browser subset; Fast mode does not claim SQLite evidence'
    browser = if ($plan.classification.browser) { 'selected browser smoke lane may start browser processes when execution is allowed' } else { 'not selected' }
    filesystemEvidence = 'persistent product/.local evidence roots are untouched; any disposable lane owns its temporary paths'
}

if ($Json) {
    $plan | Add-Member -NotePropertyName wrapper -NotePropertyValue ([ordered]@{ mode = $Mode; explain = [bool]$Explain; safety = $wrapperSafety; resources = $resourcePosture; staleWarning = $staleWarning })
    $plan | ConvertTo-Json -Depth 30
    exit 0
}

Write-Host "AeroLink changed validation plan ($Mode)" -ForegroundColor Cyan
Write-Host "Planner: $($plan.compact.planner.version) / $($plan.compact.planner.hash)"
$mergeBaseLabel = if ($plan.mergeBase) { $plan.mergeBase } else { '(explicit paths)' }
Write-Host "Changed paths: $($plan.changedPaths.Count); merge base: $mergeBaseLabel"
if ($staleWarning) { Write-Warning $staleWarning }
Write-Host 'Safety: persistent PostgreSQL and product/.local evidence roots are untouched; the planner performs no fetch or rebase.' -ForegroundColor Green
if ($DryRun) {
    Write-Host 'Dry-run: no build, test, database, evidence, package restore, or network operation was started.' -ForegroundColor Green
}
else {
    Write-Host 'Execution note: a fresh-checkout build may restore configured packages; no Git fetch or rebase is performed.' -ForegroundColor Yellow
}
Write-Host 'Full merge evidence remains with GitHub Actions.' -ForegroundColor Yellow
Write-Host 'Resource posture:' -ForegroundColor Cyan
Write-Host "  PostgreSQL: $($resourcePosture.postgresql)"
Write-Host "  SQLite: $($resourcePosture.sqlite)"
Write-Host "  Browser: $($resourcePosture.browser)"
Write-Host "  Evidence filesystem: $($resourcePosture.filesystemEvidence)"

if ($Explain) {
    Write-Host ''
    Write-Host 'Changed paths and selected areas:' -ForegroundColor Cyan
    foreach ($row in $plan.explain) {
        $areas = if ($row.areas.Count -gt 0) { $row.areas -join ', ' } elseif (-not $row.product) { 'documentation/non-product' } else { 'unclassified fallback' }
        Write-Host "  $($row.path) -> $areas"
    }
}

Write-Host ''
Write-Host 'Classification:' -ForegroundColor Cyan
foreach ($area in @('docsOnly', 'backend', 'client', 'browser', 'postgresql')) {
    Write-Host ("  {0,-12} {1}" -f $area, $plan.classification.$area)
}
if ($plan.classification.reason) { Write-Host "  Reason: $($plan.classification.reason)" }

Write-Host ''
Write-Host 'Local plan:' -ForegroundColor Cyan
foreach ($step in $plan.local) {
    Write-Host "  - $($step.label)"
    if ($step.command) { Write-Host "      $($step.command)" }
    Write-Host "      $($step.why)"
}
Write-Host ''
Write-Host ("AEROLINK_TEST_PLAN_RESULT=" + ($plan.compact | ConvertTo-Json -Compress -Depth 10)) -ForegroundColor DarkCyan

function Invoke-CheckedProcess {
    param([Parameter(Mandatory)][string]$FilePath, [Parameter(Mandatory)][string[]]$Arguments)
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$FilePath exited with code $LASTEXITCODE." }
}

function Invoke-FastStep {
    param([Parameter(Mandatory)]$Step)
    switch ($Step.label) {
        'Build the solution' { Invoke-CheckedProcess 'dotnet' @('build', 'product/AeroLink.slnx', '--configuration', 'Release') }
        'Domain suite' { Invoke-CheckedProcess 'dotnet' @('test', 'product/tests/AeroLink.Domain.Tests', '--configuration', 'Release', '--no-build') }
        'Infrastructure suite' { Invoke-CheckedProcess 'dotnet' @('test', 'product/tests/AeroLink.Infrastructure.Tests', '--configuration', 'Release', '--no-build') }
        'Client lint, type-check and build' { Invoke-CheckedProcess 'npm.cmd' @('--prefix', 'product/client', 'run', 'lint'); Invoke-CheckedProcess 'npm.cmd' @('--prefix', 'product/client', 'run', 'build') }
        'Browser smoke journeys' { Invoke-CheckedProcess 'npm.cmd' @('--prefix', 'product/client', 'run', 'test:smoke') }
        default { Write-Host "  [CI-only] $($Step.label): $($Step.why)" -ForegroundColor Yellow }
    }
}

function Invoke-FullPlan {
    $classification = $plan.classification
    if ($classification.backend) {
        Invoke-CheckedProcess 'dotnet' @('build', 'product/AeroLink.slnx', '--configuration', 'Release')
        Invoke-CheckedProcess 'dotnet' @('test', 'product/tests/AeroLink.Api.Tests', '--configuration', 'Release', '--no-build')
        Invoke-CheckedProcess 'dotnet' @('test', 'product/tests/AeroLink.Domain.Tests', '--configuration', 'Release', '--no-build')
        Invoke-CheckedProcess 'dotnet' @('test', 'product/tests/AeroLink.Infrastructure.Tests', '--configuration', 'Release', '--no-build')
    }
    if ($classification.client) {
        Invoke-CheckedProcess 'npm.cmd' @('--prefix', 'product/client', 'run', 'lint')
        Invoke-CheckedProcess 'npm.cmd' @('--prefix', 'product/client', 'run', 'build')
    }
    if ($classification.browser) {
        # Both Playwright configs use unique temp SQLite files and loopback ports; neither targets product/.local.
        Invoke-CheckedProcess 'npm.cmd' @('--prefix', 'product/client', 'run', 'test:smoke')
        Invoke-CheckedProcess 'npm.cmd' @('--prefix', 'product/client', 'run', 'test:production')
    }
    Write-Host '  [CI-only] PostgreSQL-sensitive checks remain in the disposable GitHub service-container lane.' -ForegroundColor Yellow
}

if ($DryRun) {
    Write-Host ''
    Write-Host 'Dry run requested: no build, test, database, evidence, fetch, rebase, or network operation was started.' -ForegroundColor Green
    exit 0
}

$confirmation = if ($Mode -eq 'Full') { Read-Host 'Full mode will run a broader local disposable SQLite/browser subset; PostgreSQL remains CI-only. Continue? [y/N]' } else { 'y' }
if ($Mode -eq 'Full' -and $confirmation -notmatch '^(?i:y|yes)$') {
    Write-Host 'Full validation cancelled before any command ran.' -ForegroundColor Yellow
    exit 2
}

Push-Location $repositoryRoot
try {
    if ($Mode -eq 'Fast') {
        foreach ($step in $plan.local) { Invoke-FastStep -Step $step }
    }
    else {
        Invoke-FullPlan
    }
}
finally {
    Pop-Location
}

Write-Host ''
Write-Host 'Local validation completed. GitHub Actions full evidence is still required for merge.' -ForegroundColor Green
