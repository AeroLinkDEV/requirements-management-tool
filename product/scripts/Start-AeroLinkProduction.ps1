[CmdletBinding()]
param(
    [switch]$DoNotOpenBrowser,
    [switch]$SkipClientBuild,
    [switch]$Shared
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
# and the demonstration identities, over plain HTTP. It is not a production *deployment* — TLS, certificates,
# secret management, reverse-proxy topology and off-device backups are organization-specific work recorded in
# SECURITY_AND_IDENTITY_MODEL.md and not done here.
#
# `-Shared` lets colleagues on the same network open it from their own machines. Off by default, because the
# same run also prints a known administrator password and loads demonstration data, and a launcher somebody
# double-clicks out of habit should not put that on an office network without being asked to.

$ErrorActionPreference = 'Stop'
$productRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$repositoryRoot = (Resolve-Path (Join-Path $productRoot '..')).Path
$apiProject = Join-Path $productRoot 'src\AeroLink.Api\AeroLink.Api.csproj'
$clientRoot = Join-Path $productRoot 'client'
$distRoot = Join-Path $clientRoot 'dist'
$logs = Join-Path $productRoot '.local\logs'

# Reaching this machine from another one takes two changes, not one, and the second is the one nobody expects.
#
# Binding to 0.0.0.0 makes the socket accept connections from off this box. That alone is not enough: ASP.NET
# Core's host filtering compares the Host header against `AllowedHosts`, which appsettings.json sets to
# "localhost;127.0.0.1". So a colleague typing this machine's address reaches a server that is listening
# perfectly well and gets back a bare HTTP 400 with no body — which reads exactly like a binding problem, and
# is not one. Both settings move together or neither should.
if ($Shared) {
    $bindUrl = 'http://0.0.0.0:5080'
    $allowedHosts = '*'
}
else {
    $bindUrl = 'http://127.0.0.1:5080'
    $allowedHosts = 'localhost;127.0.0.1'
}
# Everything this script checks for itself goes over loopback, whichever mode is in force: it is reachable in
# both, and it does not depend on which network this machine happens to be on today.
$url = 'http://127.0.0.1:5080'

New-Item -ItemType Directory -Path $logs -Force | Out-Null

function Get-AeroLinkLanAddress {
    # The IPv4 address a colleague would type. Physical adapters that are actually up, preferred over the
    # virtual ones Hyper-V, WSL, Docker and VPN clients leave behind — those have real-looking addresses that
    # nobody else on the network can route to, and handing one out sends someone off debugging their own machine.
    $candidates = @(Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Where-Object { $_.IPAddress -ne '127.0.0.1' -and $_.IPAddress -notlike '169.254.*' })
    foreach ($candidate in $candidates) {
        $adapter = Get-NetAdapter -InterfaceIndex $candidate.InterfaceIndex -ErrorAction SilentlyContinue
        if ($adapter -and $adapter.Status -eq 'Up' -and -not $adapter.Virtual) { return $candidate.IPAddress }
    }
    if ($candidates.Count -gt 0) { return $candidates[0].IPAddress }
    return $null
}

function Test-AeroLinkFirewallRule {
    # Whether Windows will let those connections in at all. Kestrel binding to every interface is a decision
    # this process can make on its own; the firewall is not, and its default answer to an inbound connection on
    # port 5080 is to drop it silently. Reported rather than fixed: opening a port is an administrator's
    # decision about their machine, and a launcher that quietly edits firewall rules has overstepped.
    $rules = @(Get-NetFirewallRule -DisplayName 'AeroLink*' -ErrorAction SilentlyContinue |
        Where-Object { $_.Enabled -eq 'True' -and $_.Direction -eq 'Inbound' -and $_.Action -eq 'Allow' })
    return $rules.Count -gt 0
}

# Prerequisites first, before anything that takes minutes. Without this the launcher installed npm packages,
# compiled the client, started the API and waited two minutes for a health endpoint that could never answer,
# then reported "No .NET SDKs were found" — the right diagnosis, four minutes after it was knowable.
. (Join-Path $PSScriptRoot 'AeroLinkPrerequisites.ps1')
Write-Host '[0/4] Checking prerequisites...' -ForegroundColor Cyan
$dotnet = Resolve-AeroLinkDotnet
Assert-AeroLinkNode
Write-Host "      .NET SDK: $dotnet" -ForegroundColor Green

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
# Installs it if this machine has never had it, rather than failing with an instruction to go and run another
# script. A launcher whose first step is "now run a different script" is a launcher that does not launch, and
# the repository is cloned to a different path on every machine — so there is nothing to configure here, only
# something to do. Setup is idempotent and returns immediately once PostgreSQL is present.
$catalogue = Join-Path $productRoot '.local\postgresql\pgsql\share\postgres.bki'
if (-not (Test-Path $catalogue)) {
    Write-Host '      PostgreSQL is not installed on this machine yet. Installing it once (about 320 MB).' -ForegroundColor Yellow
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Setup-Postgres.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'PostgreSQL could not be installed. The output above says why.' }
}
else {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Start-Postgres.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'PostgreSQL could not be started.' }
}

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
$arguments = "run --configuration Release --no-launch-profile --project `"$apiProject`" --urls `"$bindUrl`""
$environment = @{
    ASPNETCORE_ENVIRONMENT = 'Development'
    Client__StaticFiles    = $distRoot
    AllowedHosts           = $allowedHosts
}
foreach ($entry in $environment.GetEnumerator()) { [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process') }
Start-Process -FilePath $dotnet `
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

if ($Shared) {
    $lan = Get-AeroLinkLanAddress
    Write-Host ''
    if ($lan) {
        Write-Host 'Other people on this network can open AeroLink here:' -ForegroundColor Cyan
        Write-Host "      http://${lan}:5080" -ForegroundColor Cyan
    }
    else {
        Write-Host 'Shared mode is on, but this machine has no network address to share. Check that it is' -ForegroundColor Yellow
        Write-Host 'connected to a network, then run this launcher again.' -ForegroundColor Yellow
    }
    if (-not (Test-AeroLinkFirewallRule)) {
        Write-Host ''
        Write-Host 'Windows Firewall will block those connections until port 5080 is allowed in. Nothing here' -ForegroundColor Yellow
        Write-Host 'can do that for you - it needs an administrator. Open PowerShell as Administrator, once:' -ForegroundColor Yellow
        Write-Host '      New-NetFirewallRule -DisplayName "AeroLink on this network" -Direction Inbound -Action Allow -Protocol TCP -LocalPort 5080 -Profile Private,Domain' -ForegroundColor Gray
    }
}
else {
    Write-Host ''
    Write-Host 'Only this machine can reach it. To let colleagues on the same network open it, use' -ForegroundColor DarkGray
    Write-Host 'START_AEROLINK_SHARED.bat instead.' -ForegroundColor DarkGray
}

Write-Host ''
Write-Host 'This is the production build with local demonstration configuration. It is not a production' -ForegroundColor DarkGray
Write-Host 'deployment: no TLS, demonstration credentials, and demonstration data are all enabled.' -ForegroundColor DarkGray
if ($Shared) {
    Write-Host 'Shared over plain HTTP, so anything typed into it crosses the office network unencrypted, and' -ForegroundColor DarkGray
    Write-Host 'anybody who reaches it can sign in with the password printed above.' -ForegroundColor DarkGray
}
