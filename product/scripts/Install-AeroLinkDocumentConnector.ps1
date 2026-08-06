param([string]$Configuration = "Release")
$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$project = Join-Path $repo "product\tools\AeroLink.DocumentConnector\AeroLink.DocumentConnector.csproj"
$destination = Join-Path $repo "product\.local\document-connector"
New-Item -ItemType Directory -Force -Path $destination | Out-Null
& dotnet publish $project -c $Configuration -r win-x64 --self-contained false -o $destination
if ($LASTEXITCODE -ne 0) { throw "The AeroLink desktop connector could not be built." }
$connector = Join-Path $destination "AeroLink.DocumentConnector.exe"
& $connector --install
if ($LASTEXITCODE -ne 0) { throw "The AeroLink desktop connector could not be registered." }
Write-Host "AeroLink desktop connector installed for this Windows account." -ForegroundColor Green
