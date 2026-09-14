#Requires -Version 5.1
[CmdletBinding()]
param([int]$ApiPort = 5097, [int]$ClientPort = 5197)

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$runId = '1037-lineage-' + [Guid]::NewGuid().ToString('N')
$runRoot = Join-Path ([IO.Path]::GetTempPath()) ('aerolink-' + $runId)
New-Item -ItemType Directory -Path $runRoot | Out-Null
$databasePath = Join-Path ([IO.Path]::GetTempPath()) ('aerolink-e2e-' + $runId + '.db')
$fixturePath = Join-Path $runRoot 'stored-branch.json'
$projectPath = Join-Path $runRoot 'LineageQualification.csproj'
$infrastructure = [Security.SecurityElement]::Escape((Join-Path $repositoryRoot 'product/src/AeroLink.Infrastructure/AeroLink.Infrastructure.csproj'))
$fixture = [Security.SecurityElement]::Escape((Join-Path $PSScriptRoot 'qualification/ProjectSetupLineageQualification.cs'))
@"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
  <ItemGroup><ProjectReference Include="$infrastructure"/><Compile Include="$fixture"/></ItemGroup>
</Project>
"@ | Set-Content -LiteralPath $projectPath -Encoding UTF8
$headBefore = (& git -C $repositoryRoot rev-parse HEAD).Trim()
$statusBefore = (& git -C $repositoryRoot status --porcelain) -join "`n"
$settings = @{
    AEROLINK_E2E_RUN_ID = $runId
    AEROLINK_E2E_API_PORT = [string]$ApiPort
    AEROLINK_E2E_CLIENT_PORT = [string]$ClientPort
    AEROLINK_E2E_SKIP_BUILD = 'true'
    AEROLINK_E2E_SKIP_SHOWCASE_SEED = 'true'
    AEROLINK_E2E_LINEAGE_FIXTURE = $fixturePath
    AEROLINK_E2E_OUTPUT_DIR = (Join-Path $runRoot 'results')
    AEROLINK_E2E_REPORT_DIR = (Join-Path $runRoot 'report')
    AEROLINK_E2E_API_LOG_DIR = (Join-Path $runRoot 'api-logs')
}
$prior = @{}
$succeeded = $false
Push-Location $repositoryRoot
try {
    & dotnet build product/AeroLink.slnx --configuration Release *> (Join-Path $runRoot 'build.log')
    if ($LASTEXITCODE -ne 0) { throw 'Complete solution build failed; see retained build.log.' }
    & dotnet run --project $projectPath --configuration Release -- $databasePath $fixturePath *> (Join-Path $runRoot 'fixture.log')
    if ($LASTEXITCODE -ne 0) { throw 'Stored branch fixture creation failed; see retained fixture.log.' }
    foreach ($key in $settings.Keys) {
        $prior[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
        [Environment]::SetEnvironmentVariable($key, $settings[$key], 'Process')
    }
    Set-Location (Join-Path $repositoryRoot 'product/client')
    # Windows PowerShell treats native stderr as ErrorRecords. Retain warnings/diagnostics and judge
    # Playwright by its process exit code, rather than terminating before its result is written.
    try {
        $ErrorActionPreference = 'Continue'
        & npx playwright test tests/project-lineage-browser-contract.spec.ts --grep 'actual authorized build identities' *> (Join-Path $runRoot 'browser.log')
        $browserExit = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = 'Stop' }
    if ($browserExit -ne 0) { throw 'Stored branch browser qualification failed; see retained browser.log.' }
    $succeeded = $true
}
finally {
    foreach ($key in $prior.Keys) { [Environment]::SetEnvironmentVariable($key, $prior[$key], 'Process') }
    Pop-Location
    $headAfter = (& git -C $repositoryRoot rev-parse HEAD).Trim()
    $statusAfter = (& git -C $repositoryRoot status --porcelain) -join "`n"
    $sourceStable = $headBefore -eq $headAfter -and $statusBefore -eq $statusAfter
    [ordered]@{ headBefore = $headBefore; headAfter = $headAfter; statusBefore = $statusBefore;
        statusAfter = $statusAfter; passed = ($succeeded -and $sourceStable); databasePath = $databasePath; fixturePath = $fixturePath } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runRoot 'source-and-run.json') -Encoding UTF8
    Write-Host "Stored branch qualification evidence: $runRoot"
    if (-not $sourceStable) { throw 'Source changed during qualification; this run is not candidate evidence.' }
}
