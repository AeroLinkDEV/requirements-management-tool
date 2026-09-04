param(
    [string]$Configuration = "Release",
    [string]$TrustManifest = ""
)
$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$project = Join-Path $repo "product\tools\AeroLink.DocumentConnector\AeroLink.DocumentConnector.csproj"
Import-Module (Join-Path $PSScriptRoot "AeroLinkInstallation.psm1") -Force
$destination = (Get-AeroLinkInstallationPaths -ProductRoot (Join-Path $repo "product")).DocumentConnector
New-Item -ItemType Directory -Force -Path $destination | Out-Null
& dotnet publish $project -c $Configuration -r win-x64 --self-contained false -o $destination
if ($LASTEXITCODE -ne 0) { throw "The AeroLink desktop connector could not be built." }
$connector = Join-Path $destination "AeroLink.DocumentConnector.exe"
& $connector --install
if ($LASTEXITCODE -ne 0) { throw "The AeroLink desktop connector could not be registered." }
if ($TrustManifest) {
    $resolvedManifest = (Resolve-Path -LiteralPath $TrustManifest).Path
    & $connector --enroll $resolvedManifest
    if ($LASTEXITCODE -ne 0) { throw "The selected AeroLink deployment could not be enrolled." }
}
Write-Host "AeroLink desktop connector installed for this Windows account." -ForegroundColor Green
if (-not $TrustManifest) { Write-Host "No deployment was enrolled. Download a trust manifest from Documentation Center, then run the connector with --enroll <manifest-path>." -ForegroundColor Yellow }
