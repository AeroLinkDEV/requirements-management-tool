#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$ResultsDirectory = (Join-Path ([IO.Path]::GetTempPath()) ('aerolink-setup-pg-' + [Guid]::NewGuid().ToString('N'))),
    [switch]$NoBuild
)
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($env:AEROLINK_MIGRATIONS_CONNECTION)) {
    throw 'Required project-setup PostgreSQL qualification has no explicit disposable connection.'
}
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$previousRequired = $env:AEROLINK_REQUIRE_POSTGRES_QUALIFICATION
Push-Location -LiteralPath $repositoryRoot
try {
    $env:AEROLINK_REQUIRE_POSTGRES_QUALIFICATION = 'true'
    foreach ($testProject in @('AeroLink.Infrastructure.Tests', 'AeroLink.Api.Tests')) {
        $resultName = 'project-setup-postgres-' + $testProject + '-' + [Guid]::NewGuid().ToString('N') + '.trx'
        $testArguments = @('test', "product/tests/$testProject/$testProject.csproj",
            '--configuration', 'Release', '--filter', 'FullyQualifiedName~ProjectSetupPostgresQualificationTests',
            '--logger', ('trx;LogFileName=' + $resultName), '--results-directory', $ResultsDirectory,
            '--blame-hang-timeout', '6m')
        # Callers may use this only after building the exact candidate solution.
        if ($NoBuild) { $testArguments += '--no-build' }
        & dotnet @testArguments
        if ($LASTEXITCODE -ne 0) { throw 'Required project-setup PostgreSQL tests failed.' }
        $resultPath = Join-Path $ResultsDirectory $resultName
        if (-not (Test-Path -LiteralPath $resultPath -PathType Leaf)) { throw 'Required PostgreSQL TRX was not produced.' }
        [xml]$result = Get-Content -LiteralPath $resultPath -Raw
        $tests = @($result.TestRun.Results.UnitTestResult)
        $setupTests = @($tests | Where-Object { $_.testName -like "$testProject.ProjectSetupPostgresQualificationTests.*" })
        if ($setupTests.Count -eq 0 -or $setupTests.Count -ne $tests.Count -or
            @($setupTests | Where-Object { $_.outcome -ne 'Passed' }).Count -ne 0) {
            throw 'Required project-setup PostgreSQL evidence is missing, failed or skipped.'
        }
        Write-Host "Required project-setup PostgreSQL qualification: $($setupTests.Count) passed; none skipped. TRX: $resultPath"
    }
}
finally {
    $env:AEROLINK_REQUIRE_POSTGRES_QUALIFICATION = $previousRequired
    Pop-Location
}
