[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Database,
    [Parameter(Mandatory)][string]$EvidenceRoot,
    [Parameter(Mandatory)][object[]]$AttachmentInventory,
    [int]$PostgresPort = 54329,
    [int]$ApiPort = 5091,
    [string]$LogRoot,
    # The exact build to validate with. Named by the caller, never guessed - see below.
    [Parameter(Mandatory)][string]$ApiExecutable
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http
$productRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
# The CALLER names the executable, because only the caller knows which one it just built.
#
# Preferring Release and falling back to Debug looked harmless and was not: an established installation keeps
# a Release executable from its last production run, while the clone-upgrade path arrives having just built
# current source into Debug. The preference order would then pick the stale Release binary as the thing that
# supposedly proves the upgraded clone works with CURRENT AeroLink - and a binary predating the read-only
# boundary would ignore these settings entirely and start the ordinary mutating host, with its workers, over
# copied production data. That result can authorise mutating the real database.
#
# So there is no preference order any more. Restore passes the Release build it produced; clone validation
# passes the Debug build `dotnet run` produced on the way here. Guessing is the defect.
if (-not $ApiExecutable) {
    throw 'Test-AeroLinkRestoredDownloads requires -ApiExecutable naming the build to validate with. It must be the executable the caller has just built from current source; selecting whichever build happens to exist can run a stale binary as proof about the current one.'
}
$apiExecutable = [IO.Path]::GetFullPath($ApiExecutable)
if (-not (Test-Path -LiteralPath $apiExecutable -PathType Leaf)) {
    throw "The API executable named for isolated validation does not exist: $apiExecutable"
}
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
        # What this proves, precisely: the read-only BOUNDARY refuses every route that is not an authenticated
        # controlled read. It does NOT prove the login route exists, and must not be read as proving it - the
        # middleware short-circuits before endpoint routing, so an absent or broken /api/auth/login would
        # answer 403 here exactly as a present one does. Asserting the boundary's own error code is what keeps
        # that honest: a 403 carrying restore_validation_read_only is the middleware, by construction.
        #
        # Proving a live login would need the ordinary host, which on copied production data means every
        # seeder, every startup mutation and every outbound worker. That trade is not worth a stronger
        # sentence in a log. What IS proven here about the identity stack is that the host composed and became
        # ready with the full DI graph and the authentication scheme registered - validate-on-build would have
        # failed startup otherwise - and that an authenticated controlled read below returns exact bytes.
        $forbidden = $client.PostAsync('/api/auth/login', [Net.Http.StringContent]::new('{}',[Text.Encoding]::UTF8,'application/json')).GetAwaiter().GetResult()
        if ([int]$forbidden.StatusCode -ne 403) { throw "The read-only validation API accepted a non-download route with HTTP $([int]$forbidden.StatusCode)." }
        $forbiddenBody = $forbidden.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        if ($forbiddenBody -notmatch 'restore_validation_read_only') {
            throw "The read-only validation API refused a non-download route, but not through the read-only boundary: $forbiddenBody"
        }
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
