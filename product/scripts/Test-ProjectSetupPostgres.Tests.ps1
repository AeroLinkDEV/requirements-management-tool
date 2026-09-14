#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
$runner = Join-Path $PSScriptRoot 'Test-ProjectSetupPostgres.ps1'
$evidence = Join-Path ([IO.Path]::GetTempPath()) ('aerolink-setup-runner-contract-' + [Guid]::NewGuid().ToString('N'))
$previousConnection = $env:AEROLINK_MIGRATIONS_CONNECTION
$previousRequired = $env:AEROLINK_REQUIRE_POSTGRES_QUALIFICATION
$runnerFixture = @{ Outcome = 'Passed'; ApiOutcome = 'Passed'; Invocations = 0 }

# Script-scoped mock: no process, database or network is used by these runner contract tests.
function dotnet {
    $runnerFixture.Invocations++
    if ($env:AEROLINK_REQUIRE_POSTGRES_QUALIFICATION -ne 'true') { throw 'Runner did not require qualification.' }
    $arguments = @($args)
    $logger = [string]($arguments | Where-Object { $_ -like 'trx;LogFileName=*' })
    $fileName = $logger.Substring('trx;LogFileName='.Length)
    $directory = [string]$arguments[[Array]::IndexOf($arguments, '--results-directory') + 1]
    $null = New-Item -ItemType Directory -Path $directory -Force
    $testProject = if ($arguments[1] -like '*AeroLink.Api.Tests*') { 'AeroLink.Api.Tests' } else { 'AeroLink.Infrastructure.Tests' }
    $outcome = if ($testProject -eq 'AeroLink.Api.Tests') { $runnerFixture.ApiOutcome } else { $runnerFixture.Outcome }
    if ($outcome -ne 'Missing') {
        $testName = "$testProject.ProjectSetupPostgresQualificationTests.Probe"
        $xml = '<TestRun><Results><UnitTestResult testName="' + $testName + '" outcome="' + $outcome + '" /></Results></TestRun>'
        Set-Content -LiteralPath (Join-Path $directory $fileName) -Value $xml -Encoding UTF8
    }
    $global:LASTEXITCODE = 0
}
try {
    $env:AEROLINK_MIGRATIONS_CONNECTION = $null
    $failed = $false
    try { & $runner -NoBuild -ResultsDirectory $evidence } catch { $failed = $_.Exception.Message -match 'no explicit disposable connection' }
    if (-not $failed -or $runnerFixture.Invocations -ne 0) { throw 'Missing connection did not fail before execution.' }

    $env:AEROLINK_MIGRATIONS_CONNECTION = 'Host=127.0.0.1;Port=55437;Database=postgres;Username=contract-only'
    $env:AEROLINK_REQUIRE_POSTGRES_QUALIFICATION = 'previous-value'
    & $runner -NoBuild -ResultsDirectory $evidence
    if ($runnerFixture.Invocations -ne 2) { throw 'Runner did not execute both Infrastructure and API provider suites.' }
    if ($env:AEROLINK_REQUIRE_POSTGRES_QUALIFICATION -ne 'previous-value') { throw 'Runner did not restore required setting after success.' }
    foreach ($badOutcome in @('NotExecuted', 'Failed', 'Missing')) {
        $runnerFixture.Outcome = $badOutcome
        $failed = $false
        try { & $runner -NoBuild -ResultsDirectory $evidence } catch { $failed = $true }
        if (-not $failed) { throw "Runner accepted $badOutcome evidence." }
        if ($env:AEROLINK_REQUIRE_POSTGRES_QUALIFICATION -ne 'previous-value') { throw 'Runner did not restore required setting after failure.' }
    }
    $runnerFixture.Outcome = 'Passed'
    foreach ($badOutcome in @('NotExecuted', 'Failed', 'Missing')) {
        $runnerFixture.ApiOutcome = $badOutcome
        $failed = $false
        try { & $runner -NoBuild -ResultsDirectory $evidence } catch { $failed = $true }
        if (-not $failed) { throw "Runner accepted API $badOutcome evidence." }
    }
    Write-Host 'Project setup PostgreSQL runner contracts passed: required connection, success, skip/failure/missing evidence, environment restoration.'
}
finally {
    $env:AEROLINK_MIGRATIONS_CONNECTION = $previousConnection
    $env:AEROLINK_REQUIRE_POSTGRES_QUALIFICATION = $previousRequired
    Remove-Item Function:\dotnet
}
