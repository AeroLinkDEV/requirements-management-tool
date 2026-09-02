[CmdletBinding()]
param(
    [int]$RetentionDays = 30,
    [string]$Database = 'aerolink',
    [int]$PostgresPort = 54329,
    [string]$BackupRoot,
    [string]$PostgresBin,
    [switch]$PostgresAlreadyRunning
)

$ErrorActionPreference = 'Stop'
$productRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$repositoryRoot = (Resolve-Path (Join-Path $productRoot '..')).Path
if (-not $BackupRoot) { $BackupRoot = Join-Path $productRoot '.local\backups' }
$backupRoot = [IO.Path]::GetFullPath($BackupRoot)
$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$staging = Join-Path $backupRoot "aerolink-$timestamp"
$archive = "$staging.zip"
if (-not $PostgresBin) { $PostgresBin = Join-Path $productRoot '.local\postgresql\pgsql\bin' }
$PostgresBin = [IO.Path]::GetFullPath($PostgresBin)
$pgDump = Join-Path $PostgresBin 'pg_dump.exe'
$storageModule = Join-Path $PSScriptRoot 'AeroLinkEvidenceStore.psm1'
Import-Module $storageModule -Force
Import-Module (Join-Path $PSScriptRoot 'AeroLinkBackupArchive.psm1') -Force
$evidence = Get-AeroLinkEvidenceRoot -ProductRoot $productRoot

Import-Module (Join-Path $PSScriptRoot 'AeroLinkNativeRunner.psm1') -Force
if (-not $PostgresAlreadyRunning) {
    if ($PostgresPort -ne 54329 -or $Database -ne 'aerolink') { throw 'A non-default backup target requires -PostgresAlreadyRunning and must be isolated qualification infrastructure.' }
    $start = Invoke-AeroLinkChildScript -ScriptPath (Join-Path $PSScriptRoot 'Start-Postgres.ps1') `
        -StandardOutput (Join-Path $productRoot '.local\logs\backup-postgres-start.stdout.log') `
        -StandardError (Join-Path $productRoot '.local\logs\backup-postgres-start.stderr.log') `
        -TimeoutSeconds 420 -StepName 'Start-Postgres.ps1 (backup)'
    if ($start.ExitCode -ne 0) { throw "PostgreSQL is not available for backup: $($start.Detail)" }
}
New-Item -ItemType Directory -Path $staging -Force | Out-Null

try {
    Assert-AeroLinkStorageLifecycleHealthy -Psql (Join-Path $PostgresBin 'psql.exe') -Database $Database -Port $PostgresPort
    $inventoryBefore = @(Get-AeroLinkAttachmentInventory -Psql (Join-Path $PostgresBin 'psql.exe') -Database $Database -Port $PostgresPort)
    $sourceEvidence = Test-AeroLinkAttachmentInventory -Inventory $inventoryBefore -EvidenceRoot $evidence
    & $pgDump -h 127.0.0.1 -p $PostgresPort -U postgres -d $Database -Fc -f (Join-Path $staging 'aerolink-postgresql.dump')
    if ($LASTEXITCODE -ne 0) { throw 'pg_dump did not complete successfully.' }
    if (Test-Path -LiteralPath $evidence) { Copy-AeroLinkEvidenceTree -Source $evidence -Destination (Join-Path $staging 'evidence') }
    $archivedEvidence = Join-Path $staging 'evidence'
    $archiveEvidence = Test-AeroLinkAttachmentInventory -Inventory $inventoryBefore -EvidenceRoot $archivedEvidence
    $inventoryAfter = @(Get-AeroLinkAttachmentInventory -Psql (Join-Path $PostgresBin 'psql.exe') -Database $Database -Port $PostgresPort)
    Assert-AeroLinkStorageLifecycleHealthy -Psql (Join-Path $PostgresBin 'psql.exe') -Database $Database -Port $PostgresPort
    $beforeJson = ConvertTo-Json -InputObject @($inventoryBefore) -Depth 4 -Compress; $afterJson = ConvertTo-Json -InputObject @($inventoryAfter) -Depth 4 -Compress
    if ($beforeJson -ne $afterJson) { throw 'Controlled-attachment metadata changed during backup. No archive was published; retry after active storage operations complete.' }
    ConvertTo-Json -InputObject @($inventoryBefore) -Depth 4 | Set-Content -LiteralPath (Join-Path $staging 'attachment-inventory.json') -Encoding UTF8
    $config = Join-Path $staging 'configuration'; New-Item -ItemType Directory -Path $config | Out-Null
    Copy-Item -LiteralPath (Join-Path $productRoot 'src\AeroLink.Api\appsettings.json') -Destination $config
    Copy-Item -LiteralPath (Join-Path $productRoot 'src\AeroLink.Api\appsettings.Development.json') -Destination $config
    $files = Get-AeroLinkBackupFileInventory -StagingRoot $staging
    $applicationSha = try { (& git -C $repositoryRoot rev-parse HEAD 2>$null).Trim() } catch { 'unknown' }
    $schemaVersion = (Get-ChildItem -LiteralPath (Join-Path $productRoot 'src\AeroLink.Infrastructure\Persistence\Migrations') -Filter '*.cs' -File | Where-Object Name -notmatch 'Designer|Snapshot' | Sort-Object Name | Select-Object -Last 1).BaseName
    $manifest = [ordered]@{
        FormatVersion = 2
        CreatedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
        Application = [ordered]@{ SourceSha = $applicationSha; SchemaVersion = $schemaVersion }
        Database = [ordered]@{ Name = $Database; Dump = 'aerolink-postgresql.dump'; SnapshotCompletedAtUtc = (Get-Date).ToUniversalTime().ToString('o') }
        Storage = [ordered]@{ Scheme = 'filesystem-v1'; SourceRoot = $evidence; ArchiveRoot = 'evidence'; ObjectCount = $archiveEvidence.ReferencedObjects; AttachmentCount = $archiveEvidence.ReferencedAttachments; ReferencedBytes = $archiveEvidence.VerifiedBytes; UnreferencedObjectCount = $archiveEvidence.UnreferencedObjects.Count; UnreferencedObjects = @($archiveEvidence.UnreferencedObjects) }
        AttachmentInventory = 'attachment-inventory.json'
        Files = $files
    }
    $manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $staging 'manifest.json') -Encoding UTF8
    Compress-AeroLinkBackupArchive -SourceDirectory $staging -DestinationArchive $archive
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
