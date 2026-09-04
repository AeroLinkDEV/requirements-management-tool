[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Database,
    [Parameter(Mandatory)][string]$EvidenceRoot,
    [Parameter(Mandatory)][object[]]$AttachmentInventory,
    [int]$PostgresPort = 54329,
    [int]$ApiPort = 5091,
    [string]$LogRoot
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http
$productRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
# Release is what the restore path builds, and therefore what it should validate with. The upgrade path
# reaches here after `dotnet run` has produced a Debug build and no Release one, so a Release-only
# requirement would fail clone validation for a reason that has nothing to do with the clone. Prefer
# Release, accept Debug, and name both when neither exists.
$apiExecutable = @(
    (Join-Path $productRoot 'src\AeroLink.Api\bin\Release\net10.0\AeroLink.Api.exe'),
    (Join-Path $productRoot 'src\AeroLink.Api\bin\Debug\net10.0\AeroLink.Api.exe')
) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
if (-not $apiExecutable) { throw "A built API executable is required for isolated validation. Neither bin\Release\net10.0\AeroLink.Api.exe nor bin\Debug\net10.0\AeroLink.Api.exe exists under $productRoot\src\AeroLink.Api." }
if (-not $LogRoot) { $LogRoot = Join-Path $productRoot '.local\restore-validation\logs' }
$logs = [IO.Path]::GetFullPath($LogRoot)
New-Item -ItemType Directory -Path $logs -Force | Out-Null
$stdout = Join-Path $logs "api-$ApiPort.stdout.log"; $stderr = Join-Path $logs "api-$ApiPort.stderr.log"

$listener = Get-NetTCPConnection -LocalPort $ApiPort -State Listen -ErrorAction SilentlyContinue
if ($listener) { throw "Restore-validation API port $ApiPort is already in use." }
$previous = @{}
$settings = [ordered]@{
    'ASPNETCORE_ENVIRONMENT' = 'Production'
    'ASPNETCORE_URLS' = "http://127.0.0.1:$ApiPort"
    'ConnectionStrings__AeroLink' = "Host=127.0.0.1;Port=$PostgresPort;Database=$Database;Username=postgres"
    'Evidence__Root' = [IO.Path]::GetFullPath($EvidenceRoot)
    'RestoreValidation__ReadOnly' = 'true'
    'RestoreValidation__Token' = ([Guid]::NewGuid().ToString('N') + [Guid]::NewGuid().ToString('N'))
    'DemoData__Enabled' = 'false'
    'Identity__SeedDemoAccounts' = 'false'
    'Identity__CookieSecure' = 'false'
}
$process = $null
try {
    foreach ($entry in $settings.GetEnumerator()) {
        $previous[$entry.Key] = [Environment]::GetEnvironmentVariable($entry.Key, 'Process')
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
    }
    # Launch the application executable itself so the tracked process is the listener, not a
    # dotnet-run parent whose child could survive cleanup and retain the validation port/database.
    $process = Start-Process -FilePath $apiExecutable -WorkingDirectory (Split-Path $apiExecutable -Parent) `
        -RedirectStandardOutput $stdout -RedirectStandardError $stderr -WindowStyle Hidden -PassThru
    $ready = $false
    for ($attempt = 0; $attempt -lt 120; $attempt++) {
        if ($process.HasExited) { break }
        try { $response = Invoke-WebRequest -Uri "http://127.0.0.1:$ApiPort/health/ready" -UseBasicParsing -TimeoutSec 2; if ($response.StatusCode -eq 200) { $ready = $true; break } } catch { }
        Start-Sleep -Milliseconds 500
    }
    if (-not $ready) { throw "The isolated read-only AeroLink validation API did not become ready. See $stderr" }

    $handler = [Net.Http.HttpClientHandler]::new(); $handler.CookieContainer = [Net.CookieContainer]::new()
    $client = [Net.Http.HttpClient]::new($handler); $client.BaseAddress = [Uri]"http://127.0.0.1:$ApiPort"
    try {
        $forbidden = $client.PostAsync('/api/auth/login', [Net.Http.StringContent]::new('{}',[Text.Encoding]::UTF8,'application/json')).GetAwaiter().GetResult()
        if ([int]$forbidden.StatusCode -ne 403) { throw "The read-only validation API accepted a non-download route with HTTP $([int]$forbidden.StatusCode)." }
        $managed = @($AttachmentInventory | Where-Object { [string]$_.ArtifactType -eq 'ManagedDocument' })
        if ($managed.Count -gt 0) {
            $wrongTokenClient = [Net.Http.HttpClient]::new(); $wrongTokenClient.BaseAddress = $client.BaseAddress
            try {
                [void]$wrongTokenClient.DefaultRequestHeaders.Add('X-AeroLink-Restore-Validation', ('0' * 64))
                $wrong = $wrongTokenClient.GetAsync("/api/managed-documents/attachments/$($managed[0].Id)").GetAwaiter().GetResult()
                if ([int]$wrong.StatusCode -ne 401) { throw "The read-only validation API accepted an invalid token with HTTP $([int]$wrong.StatusCode)." }
            } finally { $wrongTokenClient.Dispose() }
        }
        [void]$client.DefaultRequestHeaders.Add('X-AeroLink-Restore-Validation', $settings['RestoreValidation__Token'])
        $bytes = [long]0
        foreach ($attachment in $managed) {
            $download = $client.GetAsync("/api/managed-documents/attachments/$($attachment.Id)").GetAwaiter().GetResult()
            if (-not $download.IsSuccessStatusCode) { throw "Restored attachment $($attachment.Id) download failed with HTTP $([int]$download.StatusCode)." }
            $content = $download.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
            if ($content.LongLength -ne [long]$attachment.Size) { throw "Restored attachment $($attachment.Id) download size mismatch." }
            $sha = [Security.Cryptography.SHA256]::Create()
            try { $hash = ([BitConverter]::ToString($sha.ComputeHash($content))).Replace('-', '').ToLowerInvariant() }
            finally { $sha.Dispose() }
            if ($hash -ne ([string]$attachment.Sha256).ToLowerInvariant()) { throw "Restored attachment $($attachment.Id) download hash mismatch." }
            $bytes += $content.LongLength
        }
        [pscustomobject]@{ Passed = $true; Database = $Database; EvidenceRoot = [IO.Path]::GetFullPath($EvidenceRoot); ManagedDocumentDownloads = $managed.Count; DownloadedBytes = $bytes; ApiPort = $ApiPort }
    }
    finally { if ($client) { $client.Dispose() }; if ($handler) { $handler.Dispose() } }
}
finally {
    try {
        if ($process -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue; $process.WaitForExit(10000) | Out-Null }
        for ($attempt = 0; $attempt -lt 40 -and (Get-NetTCPConnection -LocalPort $ApiPort -State Listen -ErrorAction SilentlyContinue); $attempt++) { Start-Sleep -Milliseconds 250 }
        if (Get-NetTCPConnection -LocalPort $ApiPort -State Listen -ErrorAction SilentlyContinue) { throw "Restore-validation API port $ApiPort remained in use after process cleanup." }
    }
    finally {
        # Production rollback/restart must never inherit the validation database, evidence root,
        # token, or read-only mode, even when listener cleanup itself fails.
        foreach ($entry in $previous.GetEnumerator()) { [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process') }
    }
}
