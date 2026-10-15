[CmdletBinding()]
param(
    [ValidateRange(0,15)][int]$RetentionDays = 15,
    [string]$Database = 'aerolink',
    # Defaults to this installation's declared PostgreSQL port (Get-AeroLinkServiceEndpoints).
    [int]$PostgresPort = 0,
    [string]$BackupRoot,
    [string]$PostgresBin,
    # The evidence tree belonging to -Database. Defaults to this installation's live evidence root, which is
    # right for the persistent database and wrong for an isolated copy: a staging database restored under
    # restore-validation has its own evidence tree, and archiving the live one beside it would produce an
    # archive whose inventory and objects came from two different databases.
    [string]$EvidenceRoot,
    [switch]$PostgresAlreadyRunning,
    # A site server installation (#1614): its root, named explicitly by the scheduled task, which runs as the
    # installation's service account from the installed program. Omitted, the installation is resolved from this
    # source checkout exactly as before.
    [string]$InstallationRoot,
    # Qualification only: where the site's protected secrets live, instead of the machine store.
    [string]$ProtectedConfigRoot
)

$ErrorActionPreference = 'Stop'
$productRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$repositoryRoot = (Resolve-Path (Join-Path $productRoot '..')).Path
Import-Module (Join-Path $PSScriptRoot 'AeroLinkInstallation.psm1') -Force
$installation = Get-AeroLinkInstallationPaths -ProductRoot $productRoot -InstallationRoot $InstallationRoot
# A site cluster requires SCRAM passwords (#1614 1.4). Everything this backup reads, it reads as the declared
# application role, which owns every object in aerolink; the password comes from the protected store and reaches
# pg_dump and psql only through PGPASSWORD, with -w, never their command lines. HOME keeps postgres over trust.
$site = Get-AeroLinkSiteDeclaration -InstallationRoot $installation.InstallationRoot
$pgAccess = $null
$pgUser = 'postgres'
if ($site) {
    Import-Module (Join-Path $PSScriptRoot 'AeroLinkSiteInstallation.psm1') -Force
    $pgAccess = Get-AeroLinkSitePostgresAccess -InstallationRoot $site.InstallationRoot -Role Application -ProtectedConfigRoot $ProtectedConfigRoot
    $pgUser = $pgAccess.User
}
$declaredPostgresPort = (Get-AeroLinkServiceEndpoints -InstallationRoot $installation.InstallationRoot).DeclaredPostgresPort
if (-not $PostgresPort) { $PostgresPort = $declaredPostgresPort }
Import-Module (Join-Path $PSScriptRoot 'AeroLinkTransition.psm1') -Force
$transitionLease = $null
$backupLock = $null
$operationError = $null
try {
$transitionLease = Enter-AeroLinkTransition -InstallationRoot $installation.InstallationRoot -Policy Preserve
if ($transitionLease.Pending) { throw 'An interrupted production transition requires recovery before this installation can be backed up.' }
if (-not $BackupRoot) { $BackupRoot = $installation.Backups }
$backupRoot = [IO.Path]::GetFullPath($BackupRoot)
$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$staging = Join-Path $backupRoot "aerolink-$timestamp"
$archive = "$staging.zip"
if (-not $PostgresBin) { $PostgresBin = $installation.PostgresBin }
$PostgresBin = [IO.Path]::GetFullPath($PostgresBin)
$pgDump = Join-Path $PostgresBin 'pg_dump.exe'
$storageModule = Join-Path $PSScriptRoot 'AeroLinkEvidenceStore.psm1'
Import-Module $storageModule -Force
Import-Module (Join-Path $PSScriptRoot 'AeroLinkBackupArchive.psm1') -Force
$evidence = if ($EvidenceRoot) { [IO.Path]::GetFullPath($EvidenceRoot) } elseif ($site) { $site.EvidenceRoot } else { Get-AeroLinkEvidenceRoot -ProductRoot $productRoot -InstallationRoot $installation.InstallationRoot }
$instanceIdentity = if ($site) {
    # Read from the declaration itself: the HOME reader's modes do not admit a site installation.
    $declared = Get-Content -LiteralPath $site.ConfigPath -Raw | ConvertFrom-Json
    [pscustomobject]@{ Label = $(if ($declared.PSObject.Properties['label']) { [string]$declared.label } else { $site.Classification }); Classification = $site.Classification }
} else { Get-AeroLinkInstanceConfig -ProductRoot $productRoot }

Import-Module (Join-Path $PSScriptRoot 'AeroLinkNativeRunner.psm1') -Force
# A site cluster is a Windows service under its own account (#1614 1.4); a backup never starts or stops it, and an
# unavailable cluster fails at the first query below.
if (-not $PostgresAlreadyRunning -and -not $site) {
    if ($PostgresPort -ne $declaredPostgresPort -or $Database -ne 'aerolink') { throw 'A non-default backup target requires -PostgresAlreadyRunning and must be isolated qualification infrastructure.' }
    $start = Invoke-AeroLinkChildScript -ScriptPath (Join-Path $PSScriptRoot 'Start-Postgres.ps1') `
        -StandardOutput (Join-Path $installation.Logs 'backup-postgres-start.stdout.log') `
        -StandardError (Join-Path $installation.Logs 'backup-postgres-start.stderr.log') `
        -TimeoutSeconds 420 -StepName 'Start-Postgres.ps1 (backup)'
    if ($start.ExitCode -ne 0) { throw "PostgreSQL is not available for backup: $($start.Detail)" }
}
Import-Module (Join-Path $PSScriptRoot 'AeroLinkBackupRetention.psm1') -Force
$backupLock = Enter-AeroLinkBackupLock -BackupRoot $backupRoot
New-Item -ItemType Directory -Path $staging -Force | Out-Null

try {
    $psqlPath = (Join-Path $PostgresBin 'psql.exe')
    Invoke-AeroLinkWithPostgresAccess $pgAccess { Assert-AeroLinkStorageLifecycleHealthy -Psql $psqlPath -Database $Database -Port $PostgresPort -User $pgUser }
    $inventoryBefore = @(Invoke-AeroLinkWithPostgresAccess $pgAccess { Get-AeroLinkAttachmentInventory -Psql $psqlPath -Database $Database -Port $PostgresPort -User $pgUser })
    $sourceEvidence = Test-AeroLinkAttachmentInventory -Inventory $inventoryBefore -EvidenceRoot $evidence
    $dumpExit = Invoke-AeroLinkWithPostgresAccess $pgAccess { & $pgDump -w -h 127.0.0.1 -p $PostgresPort -U $pgUser -d $Database -Fc -f (Join-Path $staging 'aerolink-postgresql.dump') | Out-Host; $LASTEXITCODE }
    if ($dumpExit -ne 0) { throw 'pg_dump did not complete successfully.' }
    if (Test-Path -LiteralPath $evidence) { Copy-AeroLinkEvidenceTree -Source $evidence -Destination (Join-Path $staging 'evidence') }
    $archivedEvidence = Join-Path $staging 'evidence'
    $archiveEvidence = Test-AeroLinkAttachmentInventory -Inventory $inventoryBefore -EvidenceRoot $archivedEvidence
    $inventoryAfter = @(Invoke-AeroLinkWithPostgresAccess $pgAccess { Get-AeroLinkAttachmentInventory -Psql $psqlPath -Database $Database -Port $PostgresPort -User $pgUser })
    Invoke-AeroLinkWithPostgresAccess $pgAccess { Assert-AeroLinkStorageLifecycleHealthy -Psql $psqlPath -Database $Database -Port $PostgresPort -User $pgUser }
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
        # Which installation these bytes came from, so provenance travels with the archive rather than being
        # asserted by whoever imports it. Without this the HOME->laptop refresh had nothing to check: any
        # valid AeroLink archive could be imported and labelled HOME CANONICAL on the importer's say-so.
        # Non-secret: the operator-declared label and classification, nothing more.
        Instance = [ordered]@{ Label = $instanceIdentity.Label; Classification = $instanceIdentity.Classification }
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
    if ((Test-Path $staging) -and $resolvedStaging.StartsWith($resolvedBackup, [StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath (ConvertTo-AeroLinkArchiveIoPath $staging) -Recurse -Force }
}

if ($RetentionDays -gt 0 -or $Database -eq 'aerolink') {
    $days = if ($RetentionDays -gt 0) { $RetentionDays } else { 15 }
    $retentionPlan = @(Get-AeroLinkBackupRetentionPlan -BackupRoot $backupRoot -RetentionDays $days)
    Invoke-AeroLinkBackupRetentionPlan -BackupRoot $backupRoot -Plan $retentionPlan
}
Write-Host "AeroLink backup complete: $archive" -ForegroundColor Green
Write-Host "SHA-256: $archiveHash"

# The exact artifact this run produced, so a caller can act on THIS archive rather than rediscovering it as
# "the newest aerolink-*.zip in the directory". That guess is wrong whenever a clock skews, a file arrives
# with a future timestamp, or an operator drops an archive in by hand - and in the snapshot path the thing
# being chosen is what gets activated over a live database.
[pscustomobject]@{
    Archive    = $archive
    Sha256     = $archiveHash
    Checksum   = "$archive.sha256"
    BackupRoot = $backupRoot
    Database   = $Database
}
} catch {
    $operationError = $_
    throw
} finally {
    try {
        if ($backupLock) { $backupLock.Dispose() }
    } catch {
        if ($operationError) { Write-Warning "Backup-root lock cleanup also failed: $($_.Exception.Message)" -WarningAction Continue }
        else { $operationError = $_; throw }
    } finally {
        try { Exit-AeroLinkTransition -Lease $transitionLease }
        catch {
            if ($operationError) { Write-Warning "Installation lease cleanup also failed: $($_.Exception.Message)" -WarningAction Continue }
            else { throw }
        }
    }
}
