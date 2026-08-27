#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$RepositoryRoot
)

<#
    Regression coverage for the repository layout/documentation guard.

    The fixture cases are intentionally launched in child Windows PowerShell processes. That keeps each
    failure independent and proves the guard's command-line boundary without Pester or any product/runtime
    setup. The guard only reads repository files; these fixtures are disposable copies under %TEMP%.
#>
$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path $PSScriptRoot 'Test-RepositoryLayout.ps1'
$root = if ($RepositoryRoot) {
    (Resolve-Path -LiteralPath $RepositoryRoot -ErrorAction Stop).Path
} else {
    (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
}
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('aerolink-layout-contract-' + [Guid]::NewGuid().ToString('N'))
$failures = [System.Collections.Generic.List[string]]::new()

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { [void]$script:failures.Add($Message) }
}

function Quote-ProcessArgument([string]$Value) {
    return '"' + $Value.Replace('"', '\"') + '"'
}

function Invoke-GuardInChild([string]$RepositoryRoot) {
    $stdout = Join-Path $tempRoot ([Guid]::NewGuid().ToString('N') + '.out')
    $stderr = Join-Path $tempRoot ([Guid]::NewGuid().ToString('N') + '.err')
    $shell = (Get-Command powershell.exe -ErrorAction SilentlyContinue)
    if (-not $shell) { $shell = (Get-Command pwsh.exe -ErrorAction Stop) }
    $arguments = '-NoProfile -ExecutionPolicy Bypass -File {0} -RepositoryRoot {1}' -f (Quote-ProcessArgument $scriptPath), (Quote-ProcessArgument $RepositoryRoot)
    $process = Start-Process -FilePath $shell.Source -ArgumentList $arguments -Wait -PassThru -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    $output = ''
    if (Test-Path -LiteralPath $stdout) { $output += [IO.File]::ReadAllText($stdout) }
    if (Test-Path -LiteralPath $stderr) { $output += [IO.File]::ReadAllText($stderr) }
    Remove-Item -LiteralPath $stdout, $stderr -Force -ErrorAction SilentlyContinue
    return [pscustomobject]@{ ExitCode = $process.ExitCode; Output = $output }
}

function Get-TrackedRootFileNames {
    param(
        [Parameter(Mandatory)][string]$SourceRoot,
        [Parameter(Mandatory)][string]$Pattern
    )

    $trackedPaths = @(& git -C $SourceRoot ls-files -- $Pattern)
    if ($LASTEXITCODE -ne 0) { throw "Unable to enumerate tracked fixture inputs from $SourceRoot." }
    return @($trackedPaths |
        Where-Object { ($_ -replace '\\', '/') -notmatch '/' } |
        ForEach-Object { [IO.Path]::GetFileName($_) } |
        Sort-Object -Unique)
}

function New-LegitimateFixture {
    param(
        [Parameter(Mandatory)][string]$Name,
        [string]$SourceRoot = $root
    )

    $fixture = Join-Path $tempRoot $Name
    New-Item -ItemType Directory -Path $fixture -Force | Out-Null
    foreach ($name in @(Get-TrackedRootFileNames -SourceRoot $SourceRoot -Pattern '*.md')) {
        Copy-Item -LiteralPath (Join-Path $SourceRoot $name) -Destination $fixture -Force
    }
    foreach ($name in @(Get-TrackedRootFileNames -SourceRoot $SourceRoot -Pattern '*.bat')) {
        Copy-Item -LiteralPath (Join-Path $SourceRoot $name) -Destination $fixture -Force
    }
    Copy-Item -LiteralPath (Join-Path $SourceRoot 'docs') -Destination $fixture -Recurse -Force
    foreach ($directory in @('design', 'outputs')) {
        Copy-Item -LiteralPath (Join-Path $SourceRoot $directory) -Destination $fixture -Recurse -Force
    }
    New-Item -ItemType Directory -Path (Join-Path $fixture 'product') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $SourceRoot 'product\README.md') -Destination (Join-Path $fixture 'product') -Force
    Copy-Item -LiteralPath (Join-Path $SourceRoot 'product\docs') -Destination (Join-Path $fixture 'product') -Recurse -Force
    # A conventional community file is allowed without becoming a product-authority document.
    Set-Content -LiteralPath (Join-Path $fixture 'CONTRIBUTING.md') -Value '# Contributing`r`n' -Encoding UTF8
    return $fixture
}

try {
    New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null

    $actual = Invoke-GuardInChild $root
    Assert-True ($actual.ExitCode -eq 0) "The actual repository must pass the layout guard: $($actual.Output)"

    $legitimate = New-LegitimateFixture 'legitimate'
    $fixtureResult = Invoke-GuardInChild $legitimate
    Assert-True ($fixtureResult.ExitCode -eq 0) "A legitimate fixture with a community file must pass: $($fixtureResult.Output)"

    $sourceWithIgnoredScratch = New-LegitimateFixture 'source-with-ignored-scratch'
    Set-Content -LiteralPath (Join-Path $sourceWithIgnoredScratch '.gitignore') -Value @('HANDOFF-*.md', 'PR-BODY-*.md') -Encoding UTF8
    & git -C $sourceWithIgnoredScratch init --quiet
    Assert-True ($LASTEXITCODE -eq 0) 'The ignored-source fixture must initialize as a Git worktree.'
    & git -C $sourceWithIgnoredScratch -c core.autocrlf=false add .
    Assert-True ($LASTEXITCODE -eq 0) 'The ignored-source fixture must stage its legitimate repository inputs.'
    Set-Content -LiteralPath (Join-Path $sourceWithIgnoredScratch 'HANDOFF-LOCAL.md') -Value '# local scratch handoff' -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $sourceWithIgnoredScratch 'PR-BODY-LOCAL.md') -Value '# local pull request body' -Encoding UTF8

    $filteredFixture = New-LegitimateFixture -Name 'filtered-ignored-source' -SourceRoot $sourceWithIgnoredScratch
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $filteredFixture 'HANDOFF-LOCAL.md'))) 'Ignored source handoffs must not leak into constructed fixtures.'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $filteredFixture 'PR-BODY-LOCAL.md'))) 'Ignored source pull-request bodies must not leak into constructed fixtures.'
    $filteredFixtureResult = Invoke-GuardInChild $filteredFixture
    Assert-True ($filteredFixtureResult.ExitCode -eq 0) "A legitimate fixture constructed from a worktree with ignored scratch must pass: $($filteredFixtureResult.Output)"

    $ignoredScratch = New-LegitimateFixture 'ignored-local-scratch'
    Set-Content -LiteralPath (Join-Path $ignoredScratch '.gitignore') -Value 'HANDOFF-*.md' -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $ignoredScratch 'HANDOFF-LOCAL.md') -Value '# local scratch handoff' -Encoding UTF8
    & git -C $ignoredScratch init --quiet
    Assert-True ($LASTEXITCODE -eq 0) 'The ignored local scratch fixture must initialize as a Git worktree.'
    $ignoredScratchResult = Invoke-GuardInChild $ignoredScratch
    Assert-True ($ignoredScratchResult.ExitCode -eq 0) "An explicitly Git-ignored local scratch file must not become repository-layout content: $($ignoredScratchResult.Output)"

    $trackedIgnoredScratch = New-LegitimateFixture 'tracked-ignored-scratch'
    Set-Content -LiteralPath (Join-Path $trackedIgnoredScratch '.gitignore') -Value 'HANDOFF-*.md' -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $trackedIgnoredScratch 'HANDOFF-LOCAL.md') -Value '# tracked handoff' -Encoding UTF8
    & git -C $trackedIgnoredScratch init --quiet
    & git -C $trackedIgnoredScratch add --force HANDOFF-LOCAL.md
    Assert-True ($LASTEXITCODE -eq 0) 'The tracked ignored-name fixture must stage its prohibited file.'
    $trackedIgnoredResult = Invoke-GuardInChild $trackedIgnoredScratch
    Assert-True ($trackedIgnoredResult.ExitCode -ne 0) "A tracked file must remain subject to layout policy even when its name matches .gitignore: $($trackedIgnoredResult.Output)"
    Assert-True ($trackedIgnoredResult.Output -match 'Historical narrative') "The tracked ignored-name fixture must explain its failure: $($trackedIgnoredResult.Output)"

    $cases = @(
        @{ Name = 'dated handoff'; Setup = { param($path) Set-Content -LiteralPath (Join-Path $path 'CURRENT_PRODUCT_HANDOFF_2026-08-27.md') -Value '# historical handoff' -Encoding UTF8 }; Pattern = 'Dated handoff|Historical narrative' },
        @{ Name = 'missing canonical file'; Setup = { param($path) Remove-Item -LiteralPath (Join-Path $path 'PROJECT_STATE.md') -Force }; Pattern = 'Required file is missing: PROJECT_STATE.md' },
        @{ Name = 'broken maintained link'; Setup = { param($path) Add-Content -LiteralPath (Join-Path $path 'docs\REMOTE_DEMO_OPERATOR.md') -Value "`r`n[broken](missing-maintained-target.md)`r`n" }; Pattern = 'Broken maintained Markdown link' },
        @{ Name = 'unapproved root narrative'; Setup = { param($path) Set-Content -LiteralPath (Join-Path $path 'UNAPPROVED_PROJECT_NOTES.md') -Value '# notes' -Encoding UTF8 }; Pattern = 'Unapproved root Markdown file' },
        @{ Name = 'wrong compatibility target'; Setup = { param($path) Set-Content -LiteralPath (Join-Path $path 'FEATURE_CATALOG.md') -Value "# compatibility pointer`r`n`r`nThis is not a second copy or independent authority.`r`n`r`nUse [README.md](README.md).`r`n" -Encoding UTF8 }; Pattern = 'does not link to its declared target' },
        @{ Name = 'outside repository target'; Setup = { param($path) $outside = Join-Path (Split-Path $path -Parent) 'outside-maintained-target.md'; Set-Content -LiteralPath $outside -Value '# outside' -Encoding UTF8; Add-Content -LiteralPath (Join-Path $path 'docs\REMOTE_DEMO_OPERATOR.md') -Value "`r`n[outside](../../outside-maintained-target.md)`r`n" }; Pattern = 'escapes the repository' },
        @{ Name = 'encoded archive target'; Setup = { param($path) Add-Content -LiteralPath (Join-Path $path 'README.md') -Value "`r`n[encoded archive](docs%2Farchive%2FCURRENT_PRODUCT_HANDOFF_2026-07-29.md)`r`n" }; Pattern = 'must not link directly to an archived record' },
        @{ Name = 'normalized archive target'; Setup = { param($path) Add-Content -LiteralPath (Join-Path $path 'README.md') -Value "`r`n[normalized archive](docs/reference/../archive/CURRENT_PRODUCT_HANDOFF_2026-07-29.md)`r`n" }; Pattern = 'must not link directly to an archived record' }
    )
    foreach ($case in $cases) {
        $caseRoot = New-LegitimateFixture ('negative-' + ($case.Name -replace '\s+', '-'))
        & $case.Setup $caseRoot
        $result = Invoke-GuardInChild $caseRoot
        Assert-True ($result.ExitCode -ne 0) "The $($case.Name) fixture must fail the layout guard. Output: $($result.Output)"
        Assert-True ($result.Output -match $case.Pattern) "The $($case.Name) fixture must explain its failure. Output: $($result.Output)"
    }
}
finally {
    if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue }
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Host "FAIL: $_" -ForegroundColor Red }
    Write-Host "Repository layout regression contract FAILED ($($failures.Count) failure(s))." -ForegroundColor Red
    exit 1
}
Write-Host 'Repository layout regression contract passed (actual tree, legitimate, ignored-source and ignored-local fixtures, and nine negative fixtures).' -ForegroundColor Green
exit 0
