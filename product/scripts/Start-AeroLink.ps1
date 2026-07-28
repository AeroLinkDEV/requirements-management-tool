[CmdletBinding()]
param(
    [switch]$DoNotOpenBrowser
)

# Starts AeroLink for development: the API on 5080 and the Vite dev server on 5173, each supervised separately.
#
# For anything anybody else is going to look at, use Start-AeroLinkProduction.ps1 instead — this one recompiles
# on every keystroke and serves unbundled modules.
#
# The probing, waiting, port reclaiming and PostgreSQL plumbing live in AeroLinkLaunch.ps1, shared with the
# production launcher.

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'AeroLinkPrerequisites.ps1')
. (Join-Path $PSScriptRoot 'AeroLinkLaunch.ps1')

$productRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$repositoryRoot = (Resolve-Path (Join-Path $productRoot '..')).Path
$apiProject = Join-Path $productRoot 'src\AeroLink.Api\AeroLink.Api.csproj'
$clientRoot = Join-Path $productRoot 'client'
$logs = Join-Path $productRoot '.local\logs'
$apiUrl = 'http://127.0.0.1:5080'
$websiteUrl = 'http://127.0.0.1:5173'

New-Item -ItemType Directory -Path $logs -Force | Out-Null

# Prerequisites first, before anything that takes minutes, so a missing SDK is reported in seconds rather than
# after an npm install and a two-minute wait on a health endpoint that could never answer.
Write-Host '[0/4] Checking prerequisites...' -ForegroundColor Cyan
$dotnet = Resolve-AeroLinkDotnet
Assert-AeroLinkNode
Write-Host "      .NET SDK: $dotnet" -ForegroundColor Green

Write-Host '[1/4] Checking PostgreSQL...' -ForegroundColor Cyan
Assert-AeroLinkPostgres -ProductRoot $productRoot

Write-Host '[2/4] Checking AeroLink API...' -ForegroundColor Cyan
# /health/ready, not /health. Liveness answers "is the process listening", which it is even when PostgreSQL is
# unreachable — so this launcher used to print "AeroLink is ready" over a database that was not there, and the
# only sign was an 8 MB log of connection failures. Readiness opens a connection, which is the question being
# asked. The endpoint already existed and returns 503 until the database answers.
if (-not (Test-HttpEndpoint -Uri "$apiUrl/health/ready")) {
    Clear-StaleAeroLinkPort -Port 5080 -ExpectedCommandFragments @('AeroLink.Api', $apiProject)
    # Windows PowerShell flattens ArgumentList into a single command line, so paths containing spaces must be
    # quoted explicitly.
    Start-AeroLinkService `
        -FilePath $dotnet `
        -ArgumentList "run --project `"$apiProject`" --urls `"$apiUrl`"" `
        -WorkingDirectory $repositoryRoot `
        -StandardOutput (Join-Path $logs 'api.stdout.log') `
        -StandardError (Join-Path $logs 'api.stderr.log') `
        -ReadyUri "$apiUrl/health/ready" `
        -ServiceName 'AeroLink API' `
        -TailLines 20
}
Write-Host '      API ready on 127.0.0.1:5080, database reachable.' -ForegroundColor Green

Write-Host '[3/4] Checking website...' -ForegroundColor Cyan
# SuccessBelow 500 here, unlike the readiness probes: this asks whether the dev server is up and serving at all,
# and any answer that is not a server error means it is. The API checks above want 2xx and nothing else.
if (-not (Test-HttpEndpoint -Uri $websiteUrl -SuccessBelow 500)) {
    Clear-StaleAeroLinkPort -Port 5173 -ExpectedCommandFragments @('vite', $clientRoot)
    Start-AeroLinkService `
        -FilePath 'npm.cmd' `
        -ArgumentList @('run', 'dev', '--', '--host', '127.0.0.1', '--port', '5173', '--strictPort') `
        -WorkingDirectory $clientRoot `
        -StandardOutput (Join-Path $logs 'client.stdout.log') `
        -StandardError (Join-Path $logs 'client.stderr.log') `
        -ReadyUri $websiteUrl `
        -ServiceName 'AeroLink website' `
        -TimeoutSeconds 75 `
        -SuccessBelow 500 `
        -TailLines 20
}
Write-Host '      Website healthy on 127.0.0.1:5173.' -ForegroundColor Green

Write-Host '[4/4] Verifying authentication service...' -ForegroundColor Cyan
# Not Test-HttpEndpoint: 401 is the *expected* answer from an unauthenticated caller, so this has to tell a 401
# apart from a connection failure rather than folding both into false. Anything else means the endpoint is
# there but not behaving, which is worth failing on now instead of at the sign-in screen.
$authStatus = $null
try {
    $meResponse = Invoke-WebRequest -Uri "$apiUrl/api/auth/me" -UseBasicParsing -TimeoutSec 3
    $authStatus = [int]$meResponse.StatusCode
}
catch {
    if ($_.Exception.Response -and $_.Exception.Response.StatusCode) {
        $authStatus = [int]$_.Exception.Response.StatusCode
    }
    else {
        throw "The API health check passed, but the authentication endpoint is unreachable: $($_.Exception.Message)"
    }
}
if ($authStatus -notin 200, 401) {
    throw "The authentication endpoint returned unexpected HTTP status $authStatus."
}
Write-Host '      Authentication endpoint is responding.' -ForegroundColor Green

if (-not $DoNotOpenBrowser) {
    Start-Process $websiteUrl
}

Write-Host ''
Write-Host 'AeroLink is ready.' -ForegroundColor Green
Write-Host "Website: $websiteUrl"
Write-Host "Sign in: admin / AeroLink!2026"
Write-Host "Logs: $logs"
