[CmdletBinding()]
param(
    [switch]$DoNotOpenBrowser,
    [switch]$SkipClientBuild
)

# Starts AeroLink the way a demonstration or an on-premises workstation should run it: the client compiled, and
# served by the API from one origin on one port.
#
# `Start-AeroLink.ps1` runs the Vite dev server, which is right for development and wrong for anything watched
# by other people. It recompiles on every keystroke, prints its own diagnostics into the page, serves unbundled
# modules, and needs a second process supervised on a second port with a CORS policy joining them. None of that
# is what a deployment looks like — and until this script existed, the built client had never been served at
# all, so the environment a demonstration depends on was also the only one never tried.
#
# This is the production *build*, run with local demonstration configuration: PostgreSQL, the FMSLIVE dataset
# and the demonstration identities, over plain HTTP on the loopback interface. It is not a production
# *deployment* — TLS, certificates, secret management, reverse-proxy topology and off-device backups are
# organization-specific work recorded in SECURITY_AND_IDENTITY_MODEL.md and not done here.

$ErrorActionPreference = 'Stop'
$productRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$repositoryRoot = (Resolve-Path (Join-Path $productRoot '..')).Path
$apiProject = Join-Path $productRoot 'src\AeroLink.Api\AeroLink.Api.csproj'
$clientRoot = Join-Path $productRoot 'client'
$distRoot = Join-Path $clientRoot 'dist'
$logs = Join-Path $productRoot '.local\logs'
$url = 'http://127.0.0.1:5080'

New-Item -ItemType Directory -Path $logs -Force | Out-Null

function Test-HttpEndpoint {
    param([Parameter(Mandatory)][string]$Uri)
    try {
        $response = Invoke-WebRequest -Uri $Uri -UseBasicParsing -TimeoutSec 3
        return $response.StatusCode -ge 200 -and $response.StatusCode -lt 300
    }
    catch { return $false }
}

function Wait-HttpEndpoint {
    param(
        [Parameter(Mandatory)][string]$Uri,
        [Parameter(Mandatory)][string]$ServiceName,
        [int]$TimeoutSeconds = 120
    )
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        if (Test-HttpEndpoint -Uri $Uri) { return }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)
    throw "$ServiceName did not become ready within $TimeoutSeconds seconds."
}

Write-Host '[1/4] Checking PostgreSQL...' -ForegroundColor Cyan
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Start-Postgres.ps1')
if ($LASTEXITCODE -ne 0) { throw 'PostgreSQL could not be started.' }

Write-Host '[2/4] Building the client...' -ForegroundColor Cyan
if ($SkipClientBuild) {
    if (-not (Test-Path (Join-Path $distRoot 'index.html'))) {
        throw "-SkipClientBuild was given but $distRoot holds no built client. Run without the switch once."
    }
    Write-Host '      Reusing the existing build.' -ForegroundColor Green
}
else {
    if (-not (Test-Path (Join-Path $clientRoot 'node_modules'))) {
        Write-Host '      Installing client dependencies (first run only)...'
        Push-Location $clientRoot
        try { & npm.cmd ci; if ($LASTEXITCODE -ne 0) { throw 'npm ci failed.' } } finally { Pop-Location }
    }
    Push-Location $clientRoot
    try {
        # Type checking is part of `npm run build`, and on purpose: a build that compiles is the whole claim
        # this script exists to make good on.
        & npm.cmd run build
        if ($LASTEXITCODE -ne 0) { throw 'The client build failed. AeroLink was not started.' }
    }
    finally { Pop-Location }
    Write-Host '      Client built.' -ForegroundColor Green
}

Write-Host '[3/4] Starting AeroLink...' -ForegroundColor Cyan
$listeners = @(Get-NetTCPConnection -LocalPort 5080 -State Listen -ErrorAction SilentlyContinue)
foreach ($listener in $listeners) {
    $process = Get-CimInstance Win32_Process -Filter "ProcessId=$($listener.OwningProcess)" -ErrorAction SilentlyContinue
    $command = "$($process.ExecutablePath) $($process.CommandLine)"
    if ($command -notlike '*AeroLink.Api*') {
        throw "Port 5080 is occupied by another application (PID $($listener.OwningProcess)). Close it and run this launcher again."
    }
    Write-Host "      Stopping the AeroLink process already on port 5080 (PID $($listener.OwningProcess))..." -ForegroundColor Yellow
    Stop-Process -Id $listener.OwningProcess -Force
    Start-Sleep -Milliseconds 800
}

$apiOut = Join-Path $logs 'production.stdout.log'
$apiErr = Join-Path $logs 'production.stderr.log'
# Release, and --no-launch-profile so launchSettings.json cannot quietly substitute development configuration.
# Client:StaticFiles is named rather than discovered, so this serves the build made moments ago and no other.
$arguments = "run --configuration Release --no-launch-profile --project `"$apiProject`" --urls `"$url`""
$environment = @{
    ASPNETCORE_ENVIRONMENT = 'Development'
    Client__StaticFiles    = $distRoot
}
foreach ($entry in $environment.GetEnumerator()) { [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process') }
Start-Process -FilePath 'dotnet' `
    -ArgumentList $arguments `
    -WorkingDirectory $repositoryRoot `
    -WindowStyle Hidden `
    -RedirectStandardOutput $apiOut `
    -RedirectStandardError $apiErr | Out-Null

Write-Host '[4/4] Waiting for AeroLink to be ready...' -ForegroundColor Cyan
try {
    # /health/ready, not /health. Liveness answers "is the process up", which it is even when the database is
    # unreachable — so waiting on it reports a working product over a dead database. Readiness opens a
    # connection, which is the question an operator is actually asking.
    Wait-HttpEndpoint -Uri "$url/health/ready" -ServiceName 'AeroLink'
}
catch {
    $tail = if (Test-Path $apiErr) { (Get-Content $apiErr -Tail 25) -join [Environment]::NewLine } else { 'No error log was produced.' }
    throw "$($_.Exception.Message)`nAeroLink error log:`n$tail"
}

# The document itself, because a ready API that serves no client is the failure this script was written to
# prevent: the previous launcher reported success while the site behind it was unusable.
$document = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 10
if ($document.Content -notmatch '/assets/index-[\w-]+\.js') {
    throw "AeroLink is running but is not serving the built client from $distRoot. Check Client:StaticFiles."
}
Write-Host '      Ready, and serving the built client.' -ForegroundColor Green

if (-not $DoNotOpenBrowser) { Start-Process $url }

Write-Host ''
Write-Host 'AeroLink is ready.' -ForegroundColor Green
Write-Host "Website and API: $url  (one origin, production build)"
Write-Host "Sign in: admin / AeroLink!2026"
Write-Host "Logs: $logs"
Write-Host ''
Write-Host 'This is the production build with local demonstration configuration. It is not a production' -ForegroundColor DarkGray
Write-Host 'deployment: no TLS, demonstration credentials, and demonstration data are all enabled.' -ForegroundColor DarkGray
