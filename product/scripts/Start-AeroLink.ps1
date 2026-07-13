[CmdletBinding()]
param(
    [switch]$DoNotOpenBrowser
)

$ErrorActionPreference = 'Stop'
$productRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$repositoryRoot = (Resolve-Path (Join-Path $productRoot '..')).Path
$apiProject = Join-Path $productRoot 'src\AeroLink.Api\AeroLink.Api.csproj'
$clientRoot = Join-Path $productRoot 'client'
$logs = Join-Path $productRoot '.local\logs'
$apiUrl = 'http://127.0.0.1:5080'
$websiteUrl = 'http://127.0.0.1:5173'

New-Item -ItemType Directory -Path $logs -Force | Out-Null

function Test-HttpEndpoint {
    param([Parameter(Mandatory)][string]$Uri)
    try {
        $response = Invoke-WebRequest -Uri $Uri -UseBasicParsing -TimeoutSec 2
        return $response.StatusCode -ge 200 -and $response.StatusCode -lt 500
    }
    catch { return $false }
}

function Wait-HttpEndpoint {
    param(
        [Parameter(Mandatory)][string]$Uri,
        [Parameter(Mandatory)][string]$ServiceName,
        [int]$TimeoutSeconds = 75
    )
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        if (Test-HttpEndpoint -Uri $Uri) { return }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)
    throw "$ServiceName did not become ready within $TimeoutSeconds seconds."
}

function Clear-StaleAeroLinkPort {
    param(
        [Parameter(Mandatory)][int]$Port,
        [Parameter(Mandatory)][string[]]$ExpectedCommandFragments
    )
    $listeners = @(Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue)
    foreach ($listener in $listeners) {
        $process = Get-CimInstance Win32_Process -Filter "ProcessId=$($listener.OwningProcess)" -ErrorAction SilentlyContinue
        $command = "$($process.ExecutablePath) $($process.CommandLine)"
        $recognized = $false
        foreach ($fragment in $ExpectedCommandFragments) {
            if ($command -like "*$fragment*") { $recognized = $true; break }
        }
        if (-not $recognized) {
            throw "Port $Port is occupied by another application (PID $($listener.OwningProcess)). Close it and run this launcher again."
        }
        Write-Host "Stopping an unresponsive AeroLink process on port $Port (PID $($listener.OwningProcess))..." -ForegroundColor Yellow
        Stop-Process -Id $listener.OwningProcess -Force
        Start-Sleep -Milliseconds 500
    }
}

Write-Host '[1/4] Checking PostgreSQL...' -ForegroundColor Cyan
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Start-Postgres.ps1')
if ($LASTEXITCODE -ne 0) { throw 'PostgreSQL could not be started.' }

Write-Host '[2/4] Checking AeroLink API...' -ForegroundColor Cyan
if (-not (Test-HttpEndpoint -Uri "$apiUrl/health")) {
    Clear-StaleAeroLinkPort -Port 5080 -ExpectedCommandFragments @('AeroLink.Api', $apiProject)
    $apiOut = Join-Path $logs 'api.stdout.log'
    $apiErr = Join-Path $logs 'api.stderr.log'
    # Windows PowerShell flattens ArgumentList into a single command line, so
    # paths containing spaces must be quoted explicitly.
    $apiArguments = "run --project `"$apiProject`" --urls `"$apiUrl`""
    Start-Process -FilePath 'dotnet' `
        -ArgumentList $apiArguments `
        -WorkingDirectory $repositoryRoot `
        -WindowStyle Hidden `
        -RedirectStandardOutput $apiOut `
        -RedirectStandardError $apiErr | Out-Null
    try { Wait-HttpEndpoint -Uri "$apiUrl/health" -ServiceName 'AeroLink API' }
    catch {
        $tail = if (Test-Path $apiErr) { (Get-Content $apiErr -Tail 20) -join [Environment]::NewLine } else { 'No API error log was produced.' }
        throw "$($_.Exception.Message)`nAPI error log:`n$tail"
    }
}
Write-Host '      API healthy on 127.0.0.1:5080.' -ForegroundColor Green

Write-Host '[3/4] Checking website...' -ForegroundColor Cyan
if (-not (Test-HttpEndpoint -Uri $websiteUrl)) {
    Clear-StaleAeroLinkPort -Port 5173 -ExpectedCommandFragments @('vite', $clientRoot)
    $clientOut = Join-Path $logs 'client.stdout.log'
    $clientErr = Join-Path $logs 'client.stderr.log'
    Start-Process -FilePath 'npm.cmd' `
        -ArgumentList @('run', 'dev', '--', '--host', '127.0.0.1', '--port', '5173', '--strictPort') `
        -WorkingDirectory $clientRoot `
        -WindowStyle Hidden `
        -RedirectStandardOutput $clientOut `
        -RedirectStandardError $clientErr | Out-Null
    try { Wait-HttpEndpoint -Uri $websiteUrl -ServiceName 'AeroLink website' }
    catch {
        $tail = if (Test-Path $clientErr) { (Get-Content $clientErr -Tail 20) -join [Environment]::NewLine } else { 'No website error log was produced.' }
        throw "$($_.Exception.Message)`nWebsite error log:`n$tail"
    }
}
Write-Host '      Website healthy on 127.0.0.1:5173.' -ForegroundColor Green

Write-Host '[4/4] Verifying authentication service...' -ForegroundColor Cyan
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
