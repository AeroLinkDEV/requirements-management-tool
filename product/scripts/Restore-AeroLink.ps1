[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BackupArchive,
    [string]$TargetDatabase = 'aerolink_restore_validation',
    [string]$EvidenceTarget,
    # Defaults to this installation's declared PostgreSQL port (Get-AeroLinkServiceEndpoints).
    [int]$PostgresPort = 0,
    [string]$PostgresBin,
    [int]$ValidationApiPort = 5091,
    [switch]$DisposableQualification,
    # Restore the archive's own state and validate the archive itself, but do NOT stand up current AeroLink
    # against the result.
    #
    # The read-only validation host refuses a database with pending migrations ("The restored database schema
    # does not match this AeroLink validation build"), which is correct when the restore is the end of the
    # story. It is circular when the restore exists so that current code can then MIGRATE the copy: a laptop
    # several migrations behind could never reach its upgrade, because the restore setting it up demanded the
    # schema the upgrade was going to produce. Callers that migrate the clone afterwards pass this and run the
    # current-code validation themselves, once the copy is genuinely current.
    #
    # Ignored for a production restore, which always validates.
    [switch]$SkipCurrentCodeValidation,
    [switch]$AllowProductionRestore,
    [string]$Confirmation,
    [ValidateSet('','BeforeDatabaseRestore','AfterDatabaseRestore','AfterEvidenceCopy','AfterPreActivationValidation','AfterOriginalDatabaseRename','AfterDatabaseActivation','AfterEvidenceActivation','AfterActivationValidation','BeforeRestart','AfterRestart')]
    [string]$FaultInjection = '',
    # A site server installation (#1614 1.9), named explicitly. Omitted, the installation resolves from this
    # checkout exactly as before. A production restore on a site must run from that installation's own program.
    [string]$InstallationRoot,
    # Qualification only: where the site's protected secrets live, instead of the machine store.
    [string]$ProtectedConfigRoot,
    # A site production restore refuses an archive taken from an installation of another classification (D8: test
    # data never mixes with production). Phase 3 restores HOME's archive into SiteProduction on purpose, with this.
    [switch]$AllowArchiveFromOtherClassification
)

$ErrorActionPreference = 'Stop'
$productRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Import-Module (Join-Path $PSScriptRoot 'AeroLinkEvidenceStore.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'AeroLinkBackupArchive.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'AeroLinkInstallation.psm1') -Force
# The installation's own ports. The guards below protect the persistent database, so they compare against the
# declared port, never a disposable qualification override.
$restoreInstallationRoot = if ($InstallationRoot) { [IO.Path]::GetFullPath($InstallationRoot) } else { Get-AeroLinkInstallationRoot -ProductRoot $productRoot }
# A site cluster requires SCRAM passwords (#1614 1.4). A site restore runs elevated, as the maintenance identity,
# and logs in only as postgres: the application password is readable by the API's service account alone, and no
# process holds both. Every object is still created by, and owned by, the application role: pg_restore --role
# switches to it before creating anything. The password reaches each tool only through PGPASSWORD, with -w.
# HOME keeps postgres over trust, unchanged.
$site = Get-AeroLinkSiteDeclaration -InstallationRoot $restoreInstallationRoot
if ($InstallationRoot -and -not $site) { throw "-InstallationRoot names a site server installation; $restoreInstallationRoot has no site declaration. Run without it for this checkout's own installation." }
$suAccess = $null; $suUser = 'postgres'; $appRole = $null
if ($site) {
    Import-Module (Join-Path $PSScriptRoot 'AeroLinkSiteInstallation.psm1') -Force
    try { $suAccess = Get-AeroLinkSitePostgresAccess -InstallationRoot $site.InstallationRoot -Role Superuser -ProtectedConfigRoot $ProtectedConfigRoot }
    catch { throw "A site restore runs as the installation's maintenance identity (an elevated administrator unless initialisation named another), which holds the superuser password; this process could not read it: $($_.Exception.Message)" }
    $suUser = $suAccess.User
    $appRole = $site.DatabaseRole
}
function Assert-SiteApiStopped {
    # A site's API is a service under its own account that this script neither stops nor starts (#1614 1.8).
    if ($site -and @(Get-NetTCPConnection -State Listen -LocalPort $site.ApiPort -ErrorAction SilentlyContinue).Count -gt 0) {
        throw "Stop the site's AeroLink service before a production restore: port $($site.ApiPort) is listening."
    }
}
if ($site) { Assert-AeroLinkSiteServiceSid -Site $site -Role Api | Out-Null }
$serviceEndpoints = Get-AeroLinkServiceEndpoints -InstallationRoot $restoreInstallationRoot
$persistentPostgresPort = $serviceEndpoints.DeclaredPostgresPort
if (-not $PostgresPort) { $PostgresPort = $persistentPostgresPort }
if ($TargetDatabase -notmatch '^[a-zA-Z][a-zA-Z0-9_]{0,62}$') { throw 'The target database name is unsafe.' }
$production = $TargetDatabase -eq 'aerolink'
if ($production -and (-not $AllowProductionRestore -or $Confirmation -ne 'RESTORE-AEROLINK')) { throw 'Production restore requires -AllowProductionRestore and -Confirmation RESTORE-AEROLINK.' }
if (-not $production -and $TargetDatabase -notmatch '(restore|validation|test)') { throw 'Isolated restore database names must contain restore, validation, or test.' }
if ($production -and -not $DisposableQualification -and $PostgresPort -ne $persistentPostgresPort) { throw "Production restore is restricted to the configured AeroLink PostgreSQL port $persistentPostgresPort." }
if ($DisposableQualification -and $PostgresPort -eq $persistentPostgresPort) { throw "Disposable restore qualification is forbidden on the persistent AeroLink PostgreSQL port $persistentPostgresPort." }
if ($site -and $production -and -not $DisposableQualification) {
    # The notification revocation and the pre-restore backup resolve the installation from this program, so it
    # must be the site's own; and AeroLink must already be stopped, because a site's API is a service under its
    # own account that this script neither stops nor starts (boot supervision, #1614 1.8).
    $programInstallation = Get-AeroLinkInstallationRoot -ProductRoot $productRoot
    if (-not [string]::Equals($programInstallation, $site.InstallationRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "A production restore of the site at $($site.InstallationRoot) must run from that installation's own program; this one belongs to $programInstallation. Nothing was changed."
    }
    Assert-SiteApiStopped
}

function Invoke-Fault([string]$Phase) { if ($FaultInjection -eq $Phase) { throw "Injected restore fault at $Phase." } }
function Invoke-Psql([string]$Database, [string]$Sql) {
    return (Invoke-AeroLinkWithPostgresAccess $suAccess {
        $output = & (Join-Path $bin 'psql.exe') -w -h 127.0.0.1 -p $PostgresPort -U $suUser -d $Database -v ON_ERROR_STOP=1 -tA -c $Sql
        if ($LASTEXITCODE -ne 0) { throw "PostgreSQL command failed against database '$Database'." }
        $output
    })
}
function Remove-Database([string]$Database) {
    [void](Invoke-Psql 'postgres' "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '$Database' AND pid <> pg_backend_pid();")
    $code = Invoke-AeroLinkWithPostgresAccess $suAccess { & (Join-Path $bin 'dropdb.exe') -w -h 127.0.0.1 -p $PostgresPort -U $suUser --if-exists $Database | Out-Host; $LASTEXITCODE }
    if ($code -ne 0) { throw "Could not remove disposable database '$Database'." }
}
function New-RestoredDatabase([string]$Database, [string]$Dump) {
    Remove-Database $Database
    if (-not $site) {
        & (Join-Path $bin 'createdb.exe') -w -h 127.0.0.1 -p $PostgresPort -U postgres $Database
        if ($LASTEXITCODE -ne 0) { throw "Could not create restore database '$Database'." }
        & (Join-Path $bin 'pg_restore.exe') -w -h 127.0.0.1 -p $PostgresPort -U postgres -d $Database --no-owner $Dump
        if ($LASTEXITCODE -ne 0) { throw 'pg_restore did not complete successfully.' }
        return
    }
    # A site: the database is created owned by the application role and closed to PUBLIC, exactly as
    # Initialize-AeroLinkSitePostgres creates aerolink. pg_restore logs in as postgres and switches to the
    # application role (--role) before creating anything; --no-owner then makes every object the role's own,
    # whatever role owned it in the source (postgres, for a HOME archive), and --no-privileges drops grants naming
    # roles this cluster does not have.
    $code = Invoke-AeroLinkWithPostgresAccess $suAccess { & (Join-Path $bin 'createdb.exe') -w -h 127.0.0.1 -p $PostgresPort -U $suUser -O $appRole -E UTF8 -T template0 $Database | Out-Host; $LASTEXITCODE }
    if ($code -ne 0) { throw "Could not create restore database '$Database'." }
    [void](Invoke-Psql 'postgres' "REVOKE ALL ON DATABASE `"$Database`" FROM PUBLIC; GRANT CONNECT, TEMPORARY ON DATABASE `"$Database`" TO `"$appRole`";")
    $code = Invoke-AeroLinkWithPostgresAccess $suAccess { & (Join-Path $bin 'pg_restore.exe') -w -h 127.0.0.1 -p $PostgresPort -U $suUser "--role=$appRole" -d $Database --no-owner --no-privileges $Dump | Out-Host; $LASTEXITCODE }
    if ($code -ne 0) { throw 'pg_restore did not complete successfully.' }
    Assert-RestoredOwnership $Database
}
function Assert-RestoredOwnership([string]$Database) {
    # Prove it rather than trust the flags. Every relation, function, type and schema in the restored database must
    # belong to the application role, except what PostgreSQL itself owns by rule: the members of an extension
    # (a trusted extension's objects belong to the bootstrap superuser), the array types of those members, and
    # public, which belongs to pg_database_owner - that is, to whoever owns the database.
    $sql = @"
WITH app AS (SELECT oid FROM pg_roles WHERE rolname = '$appRole'),
ext AS (SELECT classid, objid FROM pg_depend WHERE deptype = 'e'),
user_ns AS (SELECT oid, nspname, nspowner FROM pg_namespace WHERE nspname NOT IN ('pg_catalog','information_schema') AND nspname NOT LIKE 'pg_toast%' AND nspname NOT LIKE 'pg_temp%')
SELECT
 (SELECT count(*) FROM pg_class c JOIN user_ns n ON n.oid = c.relnamespace WHERE c.relowner <> (SELECT oid FROM app)
   AND NOT EXISTS (SELECT 1 FROM ext WHERE ext.classid = 'pg_class'::regclass AND ext.objid = c.oid)) || ',' ||
 (SELECT count(*) FROM pg_proc p JOIN user_ns n ON n.oid = p.pronamespace WHERE p.proowner <> (SELECT oid FROM app)
   AND NOT EXISTS (SELECT 1 FROM ext WHERE ext.classid = 'pg_proc'::regclass AND ext.objid = p.oid)) || ',' ||
 (SELECT count(*) FROM pg_type t JOIN user_ns n ON n.oid = t.typnamespace WHERE t.typowner <> (SELECT oid FROM app)
   AND NOT EXISTS (SELECT 1 FROM ext WHERE ext.classid = 'pg_type'::regclass AND (ext.objid = t.oid OR ext.objid = t.typelem))) || ',' ||
 (SELECT count(*) FROM user_ns n WHERE n.nspowner <> (SELECT oid FROM app) AND NOT (n.nspname = 'public' AND pg_get_userbyid(n.nspowner) = 'pg_database_owner'))
"@
    $answer = ([string](Invoke-Psql $Database $sql)).Trim()
    if ($answer -ne '0,0,0,0') { throw "The restored database '$Database' has objects not owned by $appRole (relations,functions,types,schemas: $answer)." }
    # And the role can write, as the role: a table of its own, inserted into, inside a transaction thrown away.
    [void](Invoke-Psql $Database "BEGIN; SET LOCAL ROLE `"$appRole`"; CREATE TABLE aerolink_restore_write_probe (id int); INSERT INTO aerolink_restore_write_probe VALUES (1); ROLLBACK;")
}
function Rename-Database([string]$From, [string]$To) {
    [void](Invoke-Psql 'postgres' "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '$From' AND pid <> pg_backend_pid();")
    [void](Invoke-Psql 'postgres' "ALTER DATABASE `"$From`" RENAME TO `"$To`";")
}
function Stop-AeroLinkApplicationProcesses {
    foreach ($port in 5173, $serviceEndpoints.DeclaredApiPort) {
        foreach ($listener in @(Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue)) {
            $process = Get-CimInstance Win32_Process -Filter "ProcessId=$($listener.OwningProcess)" -ErrorAction SilentlyContinue
            $command = "$($process.ExecutablePath) $($process.CommandLine)"
            # An unrelated listener is outside this restore's authority. In normal production the
            # pre-activation stop already removed AeroLink; never let another worktree/application
            # prevent the database pair from being rolled back.
            if ($command -notlike "*$productRoot*") { continue }
            Stop-Process -Id $listener.OwningProcess -Force
        }
    }
}
function Start-AeroLinkAfterRestore {
    <#
        Restarts AeroLink through the launcher that is allowed to run THIS installation.

        The development launcher now refuses an installation declared HOME CANONICAL, which is right - but
        restore called it unconditionally, both after a successful activation and again on the rollback path.
        On a declared HOME installation that turned a good restore into a failure, and then prevented the
        compensating restart of the original database as well. A guard that breaks recovery is worse than the
        gap it closes.
    #>
    if ($site) {
        # A site's API is a Windows service under its own account (boot supervision, #1614 1.8); restore never runs
        # a HOME launcher against a site's database.
        Write-Host "Start the site's AeroLink service ($($site.ServiceName)) to bring AeroLink back." -ForegroundColor Yellow
        return
    }
    $instance = Get-AeroLinkInstanceConfig -ProductRoot $productRoot -Mode Development
    if ($instance.Classification -eq 'HomeCanonical') {
        & (Join-Path $PSScriptRoot 'Start-AeroLinkProduction.ps1') -DoNotOpenBrowser
    }
    else {
        & (Join-Path $PSScriptRoot 'Start-AeroLink.ps1') -DoNotOpenBrowser
    }
}

function Test-RestoredApi([string]$Database, [string]$Root, [object[]]$Inventory, [int]$Port) {
    $managed = @($Inventory | Where-Object { [string]$_.ArtifactType -eq 'ManagedDocument' })
    if ($managed.Count -eq 0) { return [pscustomobject]@{ Passed=$true; ManagedDocumentDownloads=0; DownloadedBytes=0 } }
    # Release, named explicitly: this path builds Release and must validate with the build it produced, not
    # with whichever configuration happens to have output on disk.
    # The read-only validation API connects as postgres, as on HOME; on a site Npgsql takes the superuser password
    # from PGPASSWORD. It runs this program's own API: the Release build of a checkout, or a package's app\.
    return (Invoke-AeroLinkWithPostgresAccess $suAccess { & (Join-Path $PSScriptRoot 'Test-AeroLinkRestoredDownloads.ps1') -Database $Database -EvidenceRoot $Root `
        -AttachmentInventory $Inventory -PostgresPort $PostgresPort -ApiPort $Port -LogRoot (Join-Path $temporary 'validation-logs') -DatabaseUser $suUser `
        -ApiExecutable (Get-AeroLinkProgramLayout -ProductRoot $productRoot).ApiExecutable })
}

& (Join-Path $PSScriptRoot 'Verify-AeroLinkBackup.ps1') -BackupArchive $BackupArchive -InstallationRoot $(if ($site) { $site.InstallationRoot } else { '' }) | Out-Host
if ($site) {
    # Which installation the archive came from, decided from its own (just verified) manifest before anything is
    # revoked, backed up or renamed, so a wrong archive is refused while nothing has changed.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archiveZip = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $BackupArchive).Path)
    try {
        $manifestEntry = $archiveZip.GetEntry('manifest.json')
        if (-not $manifestEntry) { throw 'The archive has no manifest.json.' }
        $manifestReader = New-Object IO.StreamReader($manifestEntry.Open())
        try { $archiveManifest = $manifestReader.ReadToEnd() | ConvertFrom-Json } finally { $manifestReader.Dispose() }
    }
    finally { $archiveZip.Dispose() }
    $archiveInstance = if ($archiveManifest.PSObject.Properties['Instance'] -and $archiveManifest.Instance) { $archiveManifest.Instance } else { $null }
    $archiveClassification = if ($archiveInstance) { [string]$archiveInstance.Classification } else { 'unrecorded' }
    Write-Host "Archive taken from: $archiveClassification$(if ($archiveInstance) { " ($([string]$archiveInstance.Label))" })." -ForegroundColor Cyan
    if ($production -and $archiveClassification -ne $site.Classification -and -not $AllowArchiveFromOtherClassification) {
        throw "This archive was taken from a $archiveClassification installation, not $($site.Classification). D8 keeps test and production data apart; to restore HOME's archive into the site on purpose (Phase 3), add -AllowArchiveFromOtherClassification. Nothing was changed."
    }
}
if ($production) {
    # Revocation survives both successful restoration and compensating database/configuration rollback.
    # Never include this protected external authority in the archive or retained installation pair.
    Import-Module (Join-Path $PSScriptRoot 'AeroLinkNotificationAuthority.psm1') -Force
    $notificationRevocation = Revoke-AeroLinkNotificationSendGeneration -ProductRoot $productRoot
    Write-Host "External notifications: $($notificationRevocation.State). Explicit installation admission is required after recovery."
}
if ($production -and -not $DisposableQualification) {
    # A recoverable point is captured while the original database and evidence set are still active.
    if ($site) {
        & (Join-Path $PSScriptRoot 'Backup-AeroLink.ps1') -RetentionDays 15 -InstallationRoot $site.InstallationRoot -ProtectedConfigRoot $ProtectedConfigRoot
    }
    else {
        & (Join-Path $PSScriptRoot 'Backup-AeroLink.ps1') -RetentionDays 15
        & (Join-Path $PSScriptRoot 'Stop-AeroLink.ps1')
    }
}
# A site cluster is a service under its own account (#1614 1.4); restore neither starts nor stops it.
if ($PostgresPort -eq $persistentPostgresPort -and -not $site) { & (Join-Path $PSScriptRoot 'Start-Postgres.ps1') }

# Imported again, deliberately: a production restore's notification revocation force-reimports this module inside
# AeroLinkNotificationAuthority, which removes this script's copy of its commands.
Import-Module (Join-Path $PSScriptRoot 'AeroLinkInstallation.psm1') -Force
$installation = Get-AeroLinkInstallationPaths -ProductRoot $productRoot -InstallationRoot $restoreInstallationRoot
if (-not $PostgresBin) { $PostgresBin = $installation.PostgresBin }
$bin = [IO.Path]::GetFullPath($PostgresBin)
$archive = (Resolve-Path -LiteralPath $BackupArchive).Path
$restoreRoot = $installation.RestoreWork; New-Item -ItemType Directory -Path $restoreRoot -Force | Out-Null
$temporary = Join-Path $restoreRoot ([Guid]::NewGuid().ToString('N')); New-Item -ItemType Directory -Path $temporary | Out-Null
$token = [Guid]::NewGuid().ToString('N').Substring(0, 12)
$restoreDatabase = if ($production) { "aerolink_restore_stage_$token" } else { $TargetDatabase }
$oldDatabase = "aerolink_pre_restore_$token"
$failedDatabase = "aerolink_failed_restore_$token"
$resolvedTarget = $null; $incoming = $null; $retained = $null
$originalDatabaseRenamed = $false; $databaseActivated = $false; $evidenceActivated = $false; $activationPassed = $false

try {
    Expand-AeroLinkBackupArchive -ArchivePath $archive -DestinationDirectory $temporary
    $manifest = ConvertFrom-Json -InputObject (Get-Content -LiteralPath (Join-Path $temporary 'manifest.json') -Raw)
    $archiveInventoryObject = ConvertFrom-Json -InputObject (Get-Content -LiteralPath (Join-Path $temporary ([string]$manifest.AttachmentInventory)) -Raw)
    $archiveInventory = @($archiveInventoryObject | ForEach-Object { $_ })
    $dump = Join-Path $temporary ([string]$manifest.Database.Dump)
    Invoke-Fault 'BeforeDatabaseRestore'
    New-RestoredDatabase $restoreDatabase $dump
    Invoke-Fault 'AfterDatabaseRestore'

    $restoredInventory = @(Invoke-AeroLinkWithPostgresAccess $suAccess { Get-AeroLinkAttachmentInventory -Psql (Join-Path $bin 'psql.exe') -Database $restoreDatabase -Port $PostgresPort -User $suUser })
    if ((ConvertTo-Json -InputObject @($archiveInventory) -Depth 4 -Compress) -ne (ConvertTo-Json -InputObject @($restoredInventory) -Depth 4 -Compress)) { throw 'The restored database attachment inventory does not match the signed backup inventory.' }
    Invoke-AeroLinkWithPostgresAccess $suAccess { Assert-AeroLinkStorageLifecycleHealthy -Psql (Join-Path $bin 'psql.exe') -Database $restoreDatabase -Port $PostgresPort -User $suUser }
    $evidenceSource = Join-Path $temporary ([string]$manifest.Storage.ArchiveRoot)
    if ($restoredInventory.Count -gt 0 -and -not (Test-Path -LiteralPath $evidenceSource)) { throw 'The archive contains attachment rows but no evidence directory.' }
    [void](Test-AeroLinkAttachmentInventory -Inventory $restoredInventory -EvidenceRoot $evidenceSource)

    if (-not $EvidenceTarget) { $EvidenceTarget = if ($production) { Get-AeroLinkEvidenceRoot -ProductRoot $productRoot -InstallationRoot $installation.InstallationRoot } else { Join-Path $installation.RestoreValidation "$TargetDatabase\evidence" } }
    $resolvedTarget = [IO.Path]::GetFullPath($EvidenceTarget)
    $validationRoot = [IO.Path]::GetFullPath($installation.RestoreValidation) + [IO.Path]::DirectorySeparatorChar
    if (-not $production -and -not ($resolvedTarget + [IO.Path]::DirectorySeparatorChar).StartsWith($validationRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'The isolated evidence target must remain under the installation restore-validation root.' }
    $parent = Split-Path $resolvedTarget -Parent; New-Item -ItemType Directory -Path $parent -Force | Out-Null
    $incoming = Join-Path $parent ('.restore-incoming-' + $token)
    if (Test-Path -LiteralPath $incoming) { Remove-Item -LiteralPath (ConvertTo-AeroLinkArchiveIoPath $incoming) -Recurse -Force }
    New-Item -ItemType Directory -Path $incoming -Force | Out-Null
    if (Test-Path -LiteralPath $evidenceSource) { Copy-AeroLinkEvidenceTree -Source $evidenceSource -Destination $incoming }
    [void](Test-AeroLinkAttachmentInventory -Inventory $restoredInventory -EvidenceRoot $incoming)
    Invoke-Fault 'AfterEvidenceCopy'
    # Archive integrity, inventory and evidence have all been proved above regardless. What is deferred is
    # only standing up CURRENT code against a copy that is deliberately not current yet.
    $deferCurrentCode = $SkipCurrentCodeValidation -and -not $production
    $deferredResult = [pscustomobject]@{ Passed = $true; ManagedDocumentDownloads = 0; DownloadedBytes = 0; CurrentCodeValidationDeferred = $true }
    $preActivationDownloads = if ($deferCurrentCode) { $deferredResult } else { Test-RestoredApi $restoreDatabase $incoming $restoredInventory $ValidationApiPort }
    Invoke-Fault 'AfterPreActivationValidation'

    if (-not $production) {
        if (Test-Path -LiteralPath $resolvedTarget) { Remove-Item -LiteralPath (ConvertTo-AeroLinkArchiveIoPath $resolvedTarget) -Recurse -Force }
        Move-Item -LiteralPath $incoming -Destination $resolvedTarget; $incoming = $null
        [void](Test-AeroLinkAttachmentInventory -Inventory $restoredInventory -EvidenceRoot $resolvedTarget)
        $finalDownloads = if ($deferCurrentCode) { $deferredResult } else { Test-RestoredApi $restoreDatabase $resolvedTarget $restoredInventory ($ValidationApiPort + 1) }
        $activationPassed = $true
    }
    else {
        Assert-SiteApiStopped
        Rename-Database 'aerolink' $oldDatabase; $originalDatabaseRenamed = $true
        Invoke-Fault 'AfterOriginalDatabaseRename'
        Rename-Database $restoreDatabase 'aerolink'; $databaseActivated = $true
        Invoke-Fault 'AfterDatabaseActivation'
        if (Test-Path -LiteralPath $resolvedTarget) { $retained = Join-Path $parent ("evidence-pre-restore-$token"); Move-Item -LiteralPath $resolvedTarget -Destination $retained }
        Move-Item -LiteralPath $incoming -Destination $resolvedTarget; $incoming = $null; $evidenceActivated = $true
        # The incoming folder inherited the installation root's permissions (read-only to the API) and the move
        # kept them; give the site's evidence folder its own permissions back, and prove the API can write.
        if ($site) { [void](Set-AeroLinkSiteEvidenceAccess -InstallationRoot $site.InstallationRoot -Path $resolvedTarget) }
        Invoke-Fault 'AfterEvidenceActivation'
        $activatedInventory = @(Invoke-AeroLinkWithPostgresAccess $suAccess { Get-AeroLinkAttachmentInventory -Psql (Join-Path $bin 'psql.exe') -Database 'aerolink' -Port $PostgresPort -User $suUser })
        [void](Test-AeroLinkAttachmentInventory -Inventory $activatedInventory -EvidenceRoot $resolvedTarget)
        Invoke-AeroLinkWithPostgresAccess $suAccess { Assert-AeroLinkStorageLifecycleHealthy -Psql (Join-Path $bin 'psql.exe') -Database 'aerolink' -Port $PostgresPort -User $suUser }
        $finalDownloads = Test-RestoredApi 'aerolink' $resolvedTarget $activatedInventory ($ValidationApiPort + 1)
        Invoke-Fault 'AfterActivationValidation'
        Invoke-Fault 'BeforeRestart'
        if (-not $DisposableQualification) { Start-AeroLinkAfterRestore }
        Invoke-Fault 'AfterRestart'
        $activationPassed = $true
    }

    # The summary must not assume a migrated schema. Under -SkipCurrentCodeValidation the clone is deliberately
    # pre-migration: an absent Program table is "not yet present", not a count and not a swallowed error
    # (#1055 first start). When current-code validation was NOT deferred, the table is required, so its absence
    # is a named failure rather than a quiet summary.
    $schemaState = Invoke-AeroLinkWithPostgresAccess $suAccess { Get-AeroLinkDatabaseSchemaState -Psql (Join-Path $bin 'psql.exe') -Database $TargetDatabase -Port $PostgresPort -User $suUser }
    if ($schemaState.ProgramPresent) { $countText = (Invoke-Psql $TargetDatabase 'SELECT COUNT(*) FROM programs;').Trim() + ' Program record(s)' }
    elseif ($deferCurrentCode) { $countText = 'Program table not present yet (pre-migration restore; current-code validation deferred)' }
    else { throw "The restore summary expected the Program table in database '$TargetDatabase', but it is absent and current-code validation was not deferred." }
    $verified = Test-AeroLinkAttachmentInventory -Inventory $restoredInventory -EvidenceRoot $resolvedTarget
    Write-Host "Restore verified in database '$TargetDatabase': $countText, $($verified.ReferencedAttachments) attachment row(s), $($verified.ReferencedObjects) object(s), $($verified.VerifiedBytes) byte(s), $($finalDownloads.ManagedDocumentDownloads) API download(s). Evidence root: $resolvedTarget" -ForegroundColor Green
    if ($production) { Write-Host "Rollback retained as database '$oldDatabase'$(if($retained){" and evidence '$retained'"})." -ForegroundColor Yellow }
}
catch {
    $failure = $_
    $originalPairAvailable = $production -and -not $originalDatabaseRenamed
    if ($production -and $originalDatabaseRenamed -and -not $activationPassed) {
        try {
            # PostgreSQL must remain available for the compensating database renames. A site's API was refused
            # unless already stopped, and is a service this script does not own.
            if (-not $site) { Stop-AeroLinkApplicationProcesses }
            else { try { Assert-SiteApiStopped } catch { Write-Warning "$($_.Exception.Message) Rolling the database and evidence back regardless; restart the service afterwards." } }
            if ($evidenceActivated -and (Test-Path -LiteralPath $resolvedTarget)) { Move-Item -LiteralPath $resolvedTarget -Destination (Join-Path (Split-Path $resolvedTarget -Parent) ("evidence-failed-restore-$token")) }
            if ($retained -and (Test-Path -LiteralPath $retained)) { Move-Item -LiteralPath $retained -Destination $resolvedTarget }
            if ($databaseActivated) { Rename-Database 'aerolink' $failedDatabase }
            Rename-Database $oldDatabase 'aerolink'
            $originalPairAvailable = $true
        }
        catch { throw "Restore failed: $($failure.Exception.Message). Automatic rollback also failed: $($_.Exception.Message). AeroLink was not restarted." }
    }
    if ($originalPairAvailable -and -not $DisposableQualification) {
        try { Start-AeroLinkAfterRestore }
        catch { throw "Restore failed: $($failure.Exception.Message). The original database/evidence pair was retained but AeroLink restart failed: $($_.Exception.Message)." }
    }
    throw $failure
}
finally {
    if ($incoming -and (Test-Path -LiteralPath $incoming)) { Remove-Item -LiteralPath (ConvertTo-AeroLinkArchiveIoPath $incoming) -Recurse -Force }
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath (ConvertTo-AeroLinkArchiveIoPath $temporary) -Recurse -Force }
    if ($production -and -not $databaseActivated) {
        try { Remove-Database $restoreDatabase } catch { }
    }
}
