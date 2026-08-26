#Requires -Version 5.1
<#
    Regression coverage for the repository layout/documentation guard.

    The fixture cases are intentionally launched in child Windows PowerShell processes. That keeps each
    failure independent and proves the guard's command-line boundary without Pester or any product/runtime
    setup. The guard only reads repository files; these fixtures are disposable copies under %TEMP%.
#>
$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path $PSScriptRoot 'Test-RepositoryLayout.ps1'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
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

function New-LegitimateFixture([string]$Name) {
    $fixture = Join-Path $tempRoot $Name
    New-Item -ItemType Directory -Path $fixture -Force | Out-Null
    foreach ($name in @(Get-ChildItem -LiteralPath $root -File -Filter '*.md' | ForEach-Object Name)) {
        Copy-Item -LiteralPath (Join-Path $root $name) -Destination $fixture -Force
    }
    foreach ($name in @(Get-ChildItem -LiteralPath $root -File -Filter '*.bat' | ForEach-Object Name)) {
        Copy-Item -LiteralPath (Join-Path $root $name) -Destination $fixture -Force
    }
    Copy-Item -LiteralPath (Join-Path $root 'docs') -Destination $fixture -Recurse -Force
    foreach ($directory in @('design', 'outputs')) {
        Copy-Item -LiteralPath (Join-Path $root $directory) -Destination $fixture -Recurse -Force
    }
    New-Item -ItemType Directory -Path (Join-Path $fixture 'product') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $root 'product\README.md') -Destination (Join-Path $fixture 'product') -Force
    Copy-Item -LiteralPath (Join-Path $root 'product\docs') -Destination (Join-Path $fixture 'product') -Recurse -Force
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
Write-Host 'Repository layout regression contract passed (actual tree, legitimate fixture, and eight negative fixtures).' -ForegroundColor Green
exit 0
