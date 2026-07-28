[CmdletBinding()]
param(
    [switch]$Json,
    [uri]$ApiBaseUri = 'http://127.0.0.1:5080',
    [uri]$ClientUri,
    [string]$DatabaseHost = '127.0.0.1',
    [int]$DatabasePort = 54329,
    [string]$DatabaseName = 'aerolink',
    [string]$DatabaseUser = 'postgres',
    [string]$BackupRoot,
    [string]$EvidenceRoot,
    [switch]$AuthenticatedProbe,
    [Security.SecureString]$ServiceApiKey
)

$ErrorActionPreference = 'Continue'
$productRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$postgresBin = Join-Path $productRoot '.local\postgresql\pgsql\bin'
$checks = @()

if (-not $ClientUri) { $ClientUri = $ApiBaseUri }
if (-not $BackupRoot) { $BackupRoot = Join-Path $productRoot '.local\backups' }
if (-not $EvidenceRoot) {
    $EvidenceRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'AeroLink\evidence'
}

function Add-Check([string]$Category, [string]$Name, [bool]$Healthy, [string]$Detail) {
    $script:checks += [pscustomobject]@{
        Category = $Category
        Name = $Name
        Healthy = $Healthy
        Detail = $Detail
    }
}

function Get-SafeFailureDetail([System.Management.Automation.ErrorRecord]$Failure) {
    if ($Failure.Exception.Response -and $Failure.Exception.Response.StatusCode) {
        return "HTTP $([int]$Failure.Exception.Response.StatusCode)"
    }
    return $Failure.Exception.GetType().Name
}

function Join-Endpoint([uri]$BaseUri, [string]$Path) {
    return [uri]::new(($BaseUri.AbsoluteUri.TrimEnd('/') + '/' + $Path.TrimStart('/')))
}

$pgReady = Join-Path $postgresBin 'pg_isready.exe'
$psql = Join-Path $postgresBin 'psql.exe'
if (Test-Path $pgReady) {
    & $pgReady -h $DatabaseHost -p $DatabasePort -U $DatabaseUser -d $DatabaseName *> $null
    Add-Check 'Readiness' 'Database listener' ($LASTEXITCODE -eq 0) "$DatabaseHost`:$DatabasePort / $DatabaseName"
}
else {
    Add-Check 'Readiness' 'Database listener' $false "pg_isready.exe was not found under the configured AeroLink runtime."
}

try {
    $response = Invoke-RestMethod (Join-Endpoint $ApiBaseUri '/health/live') -TimeoutSec 3
    Add-Check 'Liveness' 'API process' ($response.status -eq 'healthy') $ApiBaseUri.AbsoluteUri
}
catch {
    Add-Check 'Liveness' 'API process' $false (Get-SafeFailureDetail $_)
}

try {
    $response = Invoke-RestMethod (Join-Endpoint $ApiBaseUri '/health/ready') -TimeoutSec 5
    Add-Check 'Readiness' 'API and database' ($response.status -eq 'ready' -and $response.database -eq 'connected') $ApiBaseUri.AbsoluteUri
}
catch {
    Add-Check 'Readiness' 'API and database' $false (Get-SafeFailureDetail $_)
}

try {
    $response = Invoke-WebRequest $ClientUri -UseBasicParsing -TimeoutSec 3
    Add-Check 'Liveness' 'Client application' ($response.StatusCode -eq 200) $ClientUri.AbsoluteUri
}
catch {
    Add-Check 'Liveness' 'Client application' $false (Get-SafeFailureDetail $_)
}

if ($AuthenticatedProbe) {
    if (-not $ServiceApiKey) {
        $ServiceApiKey = Read-Host 'AeroLink service API key with integrations:read scope' -AsSecureString
    }
    $credential = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($ServiceApiKey)
    try {
        $plainApiKey = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($credential)
        $headers = @{ Authorization = "Bearer $plainApiKey" }
        $response = Invoke-RestMethod (Join-Endpoint $ApiBaseUri '/api/v1/integrations/health') -Headers $headers -TimeoutSec 5
        Add-Check 'Authentication capability' 'Scoped service identity' ($response.status -in @('healthy', 'attention')) 'Authenticated service API route accepted a least-privilege credential.'
    }
    catch {
        Add-Check 'Authentication capability' 'Scoped service identity' $false (Get-SafeFailureDetail $_)
    }
    finally {
        $plainApiKey = $null
        if ($credential -ne [IntPtr]::Zero) {
            [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($credential)
        }
    }
}
else {
    Add-Check 'Authentication capability' 'Scoped service identity' $true 'Not requested; standard diagnostics create no browser session.'
}

if (Test-Path $psql) {
    $migrationCount = & $psql -h $DatabaseHost -p $DatabasePort -U $DatabaseUser -d $DatabaseName -tAc 'SELECT COUNT(*) FROM "__EFMigrationsHistory"' 2>$null
    $migrationHealthy = $LASTEXITCODE -eq 0 -and [int]$migrationCount -gt 0
    Add-Check 'Migration posture' 'Applied migrations' $migrationHealthy "$migrationCount applied migration(s)"
}
else {
    Add-Check 'Migration posture' 'Applied migrations' $false 'psql.exe was not found under the configured AeroLink runtime.'
}

$driveRoot = [IO.Path]::GetPathRoot($productRoot)
$drive = Get-PSDrive -Name $driveRoot.Substring(0, 1)
Add-Check 'Storage' 'Disk space' ($drive.Free -gt 5GB) ("{0:N1} GB free" -f ($drive.Free / 1GB))

$latest = Get-ChildItem $BackupRoot -Filter 'aerolink-*.zip' -File -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
Add-Check 'Backup recency' 'Latest protected archive' ($latest -and $latest.LastWriteTime -gt (Get-Date).AddDays(-1)) $(if ($latest) { $latest.FullName } else { "No backup found in $BackupRoot" })
Add-Check 'Storage' 'Evidence root' (Test-Path $EvidenceRoot) $EvidenceRoot

$result = [pscustomobject]@{
    GeneratedAt = (Get-Date).ToUniversalTime().ToString('o')
    Healthy = -not ($checks.Healthy -contains $false)
    CreatesBrowserSession = $false
    Checks = $checks
}

if ($Json) {
    $result | ConvertTo-Json -Depth 4
}
else {
    $checks | Format-Table -AutoSize
    if ($result.Healthy) {
        Write-Host 'AeroLink diagnostics passed.' -ForegroundColor Green
    }
    else {
        Write-Host 'One or more AeroLink diagnostics need attention.' -ForegroundColor Yellow
    }
}
