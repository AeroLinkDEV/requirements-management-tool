#Requires -Version 5.1
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'This explicit qualification requires Windows and installed Microsoft Word.' }
if (Get-Process WINWORD -ErrorAction SilentlyContinue) { throw 'Close existing Word sessions before running isolated qualification.' }
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$runRoot = Join-Path $repositoryRoot ('product\artifacts\project-setup-word\' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runRoot | Out-Null
$headBefore = (& git -C $repositoryRoot rev-parse HEAD).Trim()
$statusBefore = (& git -C $repositoryRoot status --porcelain) -join "`n"
$apiTests = [System.Security.SecurityElement]::Escape((Join-Path $repositoryRoot 'product\tests\AeroLink.Api.Tests\AeroLink.Api.Tests.csproj'))
$connector = [System.Security.SecurityElement]::Escape((Join-Path $repositoryRoot 'product\tools\AeroLink.DocumentConnector\AeroLink.DocumentConnector.csproj'))
$harness = [System.Security.SecurityElement]::Escape((Join-Path $PSScriptRoot 'qualification\ProjectSetupWordQualification.cs'))
$project = Join-Path $runRoot 'WordQualification.csproj'
@"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0-windows</TargetFramework><UseWindowsForms>true</UseWindowsForms><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
  <ItemGroup><ProjectReference Include="$apiTests"/><ProjectReference Include="$connector"/><Compile Include="$harness"/></ItemGroup>
</Project>
"@ | Set-Content -LiteralPath $project -Encoding UTF8
$log = Join-Path $runRoot 'qualification.log'
Push-Location $repositoryRoot
try {
    & dotnet run --project $project --configuration Release *> $log
    $qualificationExit = $LASTEXITCODE
    $headAfter = (& git rev-parse HEAD).Trim()
    $statusAfter = (& git status --porcelain) -join "`n"
    [ordered]@{ headBefore = $headBefore; headAfter = $headAfter; statusBefore = $statusBefore;
        statusAfter = $statusAfter; exitCode = $qualificationExit; log = $log } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runRoot 'source-and-run.json') -Encoding UTF8
    Get-Content -LiteralPath $log
    Write-Host "Qualification record: $runRoot"
    if ($headBefore -ne $headAfter -or $statusBefore -ne $statusAfter) { throw 'Source changed during Word qualification; this run is not candidate evidence.' }
    if ($qualificationExit -ne 0) { throw 'Actual Word qualification failed; retained documents and diagnostics are identified in the log.' }
}
finally { Pop-Location }
