[CmdletBinding()]
param([int]$RetentionDays = 30)

$ErrorActionPreference = 'Stop'
$productRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$repositoryRoot = (Resolve-Path (Join-Path $productRoot '..')).Path
$backupRoot = Join-Path $productRoot '.local\backups'
$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$staging = Join-Path $backupRoot "aerolink-$timestamp"
$archive = "$staging.zip"
$pgDump = Join-Path $productRoot '.local\postgresql\pgsql\bin\pg_dump.exe'
$storageModule = Join-Path $PSScriptRoot 'AeroLinkEvidenceStore.psm1'
Import-Module $storageModule -Force
$evidence = Get-AeroLinkEvidenceRoot -ProductRoot $productRoot

Import-Module (Join-Path $PSScriptRoot 'AeroLinkNativeRunner.psm1') -Force
$start = Invoke-AeroLinkChildScript -ScriptPath (Join-Path $PSScriptRoot 'Start-Postgres.ps1') `
    -StandardOutput (Join-Path $productRoot '.local\logs\backup-postgres-start.stdout.log') `
    -StandardError (Join-Path $productRoot '.local\logs\backup-postgres-start.stderr.log') `
    -TimeoutSeconds 420 -StepName 'Start-Postgres.ps1 (backup)'
if ($start.ExitCode -ne 0) { throw "PostgreSQL is not available for backup: $($start.Detail)" }
New-Item -ItemType Directory -Path $staging -Force | Out-Null

try {
    Assert-AeroLinkStorageLifecycleHealthy -Psql (Join-Path $productRoot '.local\postgresql\pgsql\bin\psql.exe') -Database 'aerolink'
    $inventoryBefore = @(Get-AeroLinkAttachmentInventory -Psql (Join-Path $productRoot '.local\postgresql\pgsql\bin\psql.exe') -Database 'aerolink')
    $sourceEvidence = Test-AeroLinkAttachmentInventory -Inventory $inventoryBefore -EvidenceRoot $evidence
    & $pgDump -h 127.0.0.1 -p 54329 -U postgres -d aerolink -Fc -f (Join-Path $staging 'aerolink-postgresql.dump')
    if ($LASTEXITCODE -ne 0) { throw 'pg_dump did not complete successfully.' }
    if (Test-Path -LiteralPath $evidence) { Copy-Item -LiteralPath $evidence -Destination (Join-Path $staging 'evidence') -Recurse -Force }
    $archivedEvidence = Join-Path $staging 'evidence'
    $archiveEvidence = Test-AeroLinkAttachmentInventory -Inventory $inventoryBefore -EvidenceRoot $archivedEvidence
    $inventoryAfter = @(Get-AeroLinkAttachmentInventory -Psql (Join-Path $productRoot '.local\postgresql\pgsql\bin\psql.exe') -Database 'aerolink')
    Assert-AeroLinkStorageLifecycleHealthy -Psql (Join-Path $productRoot '.local\postgresql\pgsql\bin\psql.exe') -Database 'aerolink'
    $beforeJson = ConvertTo-Json -InputObject @($inventoryBefore) -Depth 4 -Compress; $afterJson = ConvertTo-Json -InputObject @($inventoryAfter) -Depth 4 -Compress
    if ($beforeJson -ne $afterJson) { throw 'Controlled-attachment metadata changed during backup. No archive was published; retry after active storage operations complete.' }
    ConvertTo-Json -InputObject @($inventoryBefore) -Depth 4 | Set-Content -LiteralPath (Join-Path $staging 'attachment-inventory.json') -Encoding UTF8
    $config = Join-Path $staging 'configuration'; New-Item -ItemType Directory -Path $config | Out-Null
    Copy-Item -LiteralPath (Join-Path $productRoot 'src\AeroLink.Api\appsettings.json') -Destination $config
    Copy-Item -LiteralPath (Join-Path $productRoot 'src\AeroLink.Api\appsettings.Development.json') -Destination $config
    $files = @(Get-ChildItem -LiteralPath $staging -File -Recurse | ForEach-Object {
        [pscustomobject]@{ Path = $_.FullName.Substring($staging.Length + 1); Size = $_.Length; Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
    $applicationSha = try { (& git -C $repositoryRoot rev-parse HEAD 2>$null).Trim() } catch { 'unknown' }
    $schemaVersion = (Get-ChildItem -LiteralPath (Join-Path $productRoot 'src\AeroLink.Infrastructure\Persistence\Migrations') -Filter '*.cs' -File | Where-Object Name -notmatch 'Designer|Snapshot' | Sort-Object Name | Select-Object -Last 1).BaseName
    $manifest = [ordered]@{
        FormatVersion = 2
        CreatedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
        Application = [ordered]@{ SourceSha = $applicationSha; SchemaVersion = $schemaVersion }
        Database = [ordered]@{ Name = 'aerolink'; Dump = 'aerolink-postgresql.dump'; SnapshotCompletedAtUtc = (Get-Date).ToUniversalTime().ToString('o') }
        Storage = [ordered]@{ Scheme = 'filesystem-v1'; SourceRoot = $evidence; ArchiveRoot = 'evidence'; ObjectCount = $archiveEvidence.ReferencedObjects; AttachmentCount = $archiveEvidence.ReferencedAttachments; ReferencedBytes = $archiveEvidence.VerifiedBytes; UnreferencedObjectCount = $archiveEvidence.UnreferencedObjects.Count; UnreferencedObjects = @($archiveEvidence.UnreferencedObjects) }
        AttachmentInventory = 'attachment-inventory.json'
        Files = $files
    }
    $manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $staging 'manifest.json') -Encoding UTF8
    Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $archive -CompressionLevel Optimal
    $archiveHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    "$archiveHash  $(Split-Path $archive -Leaf)" | Set-Content -LiteralPath "$archive.sha256" -Encoding ASCII
}
finally {
    $resolvedBackup = [IO.Path]::GetFullPath($backupRoot) + [IO.Path]::DirectorySeparatorChar
    $resolvedStaging = [IO.Path]::GetFullPath($staging)
    if ((Test-Path $staging) -and $resolvedStaging.StartsWith($resolvedBackup, [StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $staging -Recurse -Force }
}

if ($RetentionDays -gt 0) {
    Get-ChildItem -LiteralPath $backupRoot -File -Filter 'aerolink-*.zip*' | Where-Object LastWriteTime -lt (Get-Date).AddDays(-$RetentionDays) | Remove-Item -Force
}
Write-Host "AeroLink backup complete: $archive" -ForegroundColor Green
Write-Host "SHA-256: $archiveHash"
