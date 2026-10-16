[CmdletBinding()]
param(
    [string]$BackupArchive,
    [string]$TargetDatabase = 'aerolink_restore_validation',
    [string]$EvidenceTarget,
    # Defaults to this installation's declared PostgreSQL port (Get-AeroLinkServiceEndpoints).
    [int]$PostgresPort = 0,
    [string]$PostgresBin,
    [int]$ValidationApiPort = 5091,
    [switch]$DisposableQualification,
    # A continuity export owns a fresh restore database. When supplied, an existing target is a refusal rather
    # than a disposable object to replace; this prevents a caller-controlled or stale restore from becoming the
    # export's database authority.
    [switch]$RequireFreshTarget,
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
    # HOME continuity only: write a one-time, source-owned proof after the verified disposable restore and
    # attachment/current-code checks complete. The maintenance reader binds this proof to the archive and live
    # database before it reads any protected values.
    [string]$ContinuityReceiptPath,
    # HOME continuity export: an operation-owned journal. The journal is created by the maintenance host,
    # updated by this restore authority, and retained as a redacted recovery receipt after cleanup.
    [string]$ContinuityOperationJournalPath,
    [string]$ContinuityOperationId,
    [string]$ContinuityApiAssemblyPath,
    [string]$ContinuityApiAssemblySha256,
    [switch]$RetainContinuityRestore,
    [switch]$CleanupContinuityOperation,
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
function Assert-DatabaseAbsent([string]$Database) {
    $answer = ([string](Invoke-Psql 'postgres' "SELECT COUNT(*) FROM pg_database WHERE datname = '$Database';")).Trim()
    if ($answer -ne '0') { throw "The fresh restore target database '$Database' already exists; refusing to replace it." }
}
function New-RestoredDatabase([string]$Database, [string]$Dump) {
    # A continuity restore claims a fresh name by CREATE DATABASE itself. The preflight check remains a
    # useful early refusal, but it is not the mutation boundary: never drop a target that appeared after it.
    if (-not $RequireFreshTarget) { Remove-Database $Database }
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

function Get-ContinuityProcessIdentity([int]$ProcessId) {
    $process = Get-CimInstance Win32_Process -Filter "ProcessId=$ProcessId" -ErrorAction Stop
    if (-not $process -or [string]::IsNullOrWhiteSpace([string]$process.ExecutablePath)) {
        throw "The PostgreSQL listener process identity could not be read."
    }
    $started = (Get-Process -Id $ProcessId -ErrorAction Stop).StartTime.ToUniversalTime().ToString('o')
    $image = [IO.Path]::GetFullPath([string]$process.ExecutablePath)
    if (-not (Test-Path -LiteralPath $image -PathType Leaf)) { throw "The PostgreSQL listener image is missing." }
    [ordered]@{
        ProcessId = $ProcessId
        ProcessStartedUtc = $started
        ImagePath = $image
        ImageSha256 = (Get-FileHash -LiteralPath $image -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

function Get-ContinuityToolIdentities {
    $tools = [ordered]@{}
    foreach ($name in 'createdb.exe','pg_restore.exe','psql.exe','dropdb.exe') {
        $path = [IO.Path]::GetFullPath((Join-Path $bin $name))
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "The continuity PostgreSQL tool '$name' is missing." }
        $version = ((& $path --version 2>&1 | Out-String).Trim())
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($version)) { throw "The continuity PostgreSQL tool '$name' did not report a version." }
        $tools[$name] = [ordered]@{
            Path = $path
            Sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
            Version = $version
        }
    }
    $tools
}

function Get-ContinuityRuntimeIdentity {
    if (-not $ContinuityOperationJournalPath) { return $null }
    $listeners = @(Get-NetTCPConnection -State Listen -LocalAddress '127.0.0.1' -LocalPort $PostgresPort -ErrorAction Stop)
    if ($listeners.Count -ne 1) { throw "The continuity PostgreSQL port must have exactly one loopback listener." }
    $listenerPid = [int]$listeners[0].OwningProcess
    $dataDirectory = ([string](Invoke-Psql 'postgres' "SELECT current_setting('data_directory');")).Trim()
    if ([string]::IsNullOrWhiteSpace($dataDirectory)) { throw 'PostgreSQL did not report its data directory.' }
    $dataDirectory = [IO.Path]::GetFullPath($dataDirectory)
    $postmasterPidPath = Join-Path $dataDirectory 'postmaster.pid'
    if (-not (Test-Path -LiteralPath $postmasterPidPath -PathType Leaf)) { throw 'The PostgreSQL postmaster.pid file is missing.' }
    $pidLines = @(Get-Content -LiteralPath $postmasterPidPath -ErrorAction Stop)
    if ($pidLines.Count -lt 1 -or [int]$pidLines[0] -ne $listenerPid) { throw 'The loopback listener and postmaster.pid identities do not agree.' }
    $identity = Get-ContinuityProcessIdentity $listenerPid
    [ordered]@{
        ListenerAddress = '127.0.0.1'
        ListenerPort = [int]$PostgresPort
        Postmaster = $identity
        DataDirectory = $dataDirectory
        PostmasterPidPath = [IO.Path]::GetFullPath($postmasterPidPath)
        PostmasterPidSha256 = (Get-FileHash -LiteralPath $postmasterPidPath -Algorithm SHA256).Hash.ToLowerInvariant()
        Tools = Get-ContinuityToolIdentities
        CapturedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
    }
}

function Write-ContinuityJournal([string]$Status, [string]$Phase, [hashtable]$Updates = @{}) {
    if (-not $script:continuityJournalState) { return }
    $script:continuityJournalState.Status = $Status
    $script:continuityJournalState.Phase = $Phase
    $script:continuityJournalState.UpdatedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
    foreach ($entry in $Updates.GetEnumerator()) { $script:continuityJournalState[$entry.Key] = $entry.Value }
    $json = $script:continuityJournalState | ConvertTo-Json -Depth 12
    $temporaryJournal = "$ContinuityOperationJournalPath.writing-$([Guid]::NewGuid().ToString('N'))"
    try {
        [IO.File]::WriteAllText($temporaryJournal, $json, [Text.UTF8Encoding]::new($false))
        if ([IO.File]::Exists($ContinuityOperationJournalPath)) { [IO.File]::Replace($temporaryJournal, $ContinuityOperationJournalPath, $null) }
        else { [IO.File]::Move($temporaryJournal, $ContinuityOperationJournalPath) }
    } finally { if (Test-Path -LiteralPath $temporaryJournal) { Remove-Item -LiteralPath $temporaryJournal -Force } }
}

function Assert-ContinuityOwnedPath([string]$Path, [string]$Root, [string]$Description) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return $null }
    $full = [IO.Path]::GetFullPath($Path)
    $prefix = [IO.Path]::GetFullPath($Root).TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
    if (-not $full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw "$Description is outside its declared operation root." }
    if ((Test-Path -LiteralPath $full) -and ((Get-Item -LiteralPath $full).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "$Description is a reparse point." }
    $full
}

function Get-ContinuityDatabaseOid([string]$Database) {
    $value = ([string](Invoke-Psql 'postgres' "SELECT oid::text FROM pg_database WHERE datname = '$Database';")).Trim()
    if ([string]::IsNullOrWhiteSpace($value)) { return $null }
    if ($value -notmatch '^\d+$') { throw "The continuity database identity is malformed." }
    $value
}

function Write-ContinuityDiagnosticReceipt {
    if (-not $script:continuityJournalState) { return }
    $receiptPath = "$ContinuityOperationJournalPath.receipt.json"
    if (Test-Path -LiteralPath $receiptPath) { return $receiptPath }
    $temporaryReceipt = "$receiptPath.writing-$([Guid]::NewGuid().ToString('N'))"
    try {
        [IO.File]::WriteAllText($temporaryReceipt, ($script:continuityJournalState | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
        Move-Item -LiteralPath $temporaryReceipt -Destination $receiptPath
    } finally { if (Test-Path -LiteralPath $temporaryReceipt) { Remove-Item -LiteralPath $temporaryReceipt -Force } }
    $receiptPath
}

function Invoke-ContinuityOwnedCleanup {
    if (-not $script:continuityJournalState) { throw 'The continuity operation journal was not loaded.' }
    $target = [string]$script:continuityJournalState.TargetDatabase
    $createdOid = [string]$script:continuityJournalState.CreatedDatabaseOid
    $createdByOperation = [bool]$script:continuityJournalState.CreatedByOperation
    $phase = [string]$script:continuityJournalState.Phase
    if (-not $createdByOperation -and $phase -in @('database-creation','database-created','evidence-staging','restore-complete')) {
        Write-ContinuityJournal 'recovery-required' 'cleanup-ambiguous' @{ CleanupDisposition = 'The operation reached database mutation without an ownership OID; target and staging were preserved.' }
        throw "The continuity database ownership is ambiguous; recovery is required at $ContinuityOperationJournalPath."
    }
    if ($createdByOperation -and [string]::IsNullOrWhiteSpace($createdOid)) {
        Write-ContinuityJournal 'recovery-required' 'cleanup-ambiguous' @{ CleanupDisposition = 'The operation claimed database creation without recording an OID; target preserved.' }
        throw "The continuity database ownership is ambiguous; recovery is required at $ContinuityOperationJournalPath."
    }
    if ($createdByOperation) {
        $currentOid = Get-ContinuityDatabaseOid $target
        if ($currentOid -and $currentOid -ne $createdOid) {
            Write-ContinuityJournal 'recovery-required' 'cleanup-ambiguous' @{ CleanupDisposition = 'The target database OID changed; target preserved.' }
            throw "The continuity database OID changed; recovery is required at $ContinuityOperationJournalPath."
        }
        if ($currentOid) { Remove-Database $target }
        if (Get-ContinuityDatabaseOid $target) {
            Write-ContinuityJournal 'recovery-required' 'cleanup-failed' @{ CleanupDisposition = 'The owned database remained after drop.' }
            throw "The owned continuity database could not be removed; recovery is required at $ContinuityOperationJournalPath."
        }
    }
    $validationRoot = [IO.Path]::GetFullPath([string]$script:continuityJournalState.RestoreValidationRoot)
    $workRoot = [IO.Path]::GetFullPath([string]$script:continuityJournalState.RestoreWorkRoot)
    foreach ($item in @(
        @{ Path = [string]$script:continuityJournalState.ProofPath; Root = $validationRoot; Description = 'The continuity proof' },
        @{ Path = [string]$script:continuityJournalState.EvidencePath; Root = $validationRoot; Description = 'The continuity evidence' },
        @{ Path = [string]$script:continuityJournalState.IncomingPath; Root = $validationRoot; Description = 'The continuity incoming evidence' },
        @{ Path = [string]$script:continuityJournalState.TemporaryPath; Root = $workRoot; Description = 'The continuity temporary restore' }
    )) {
        $path = Assert-ContinuityOwnedPath $item.Path $item.Root $item.Description
        if ($path -and (Test-Path -LiteralPath $path)) { Remove-Item -LiteralPath (ConvertTo-AeroLinkArchiveIoPath $path) -Recurse -Force }
    }
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
$continuityReceipt = $null
if ($ContinuityReceiptPath) {
    $continuityReceipt = [IO.Path]::GetFullPath($ContinuityReceiptPath)
    $restoreValidationRoot = [IO.Path]::GetFullPath($installation.RestoreValidation) + [IO.Path]::DirectorySeparatorChar
    if (-not ($continuityReceipt.StartsWith($restoreValidationRoot, [StringComparison]::OrdinalIgnoreCase))) {
        throw 'The HOME continuity restore proof must remain under the installation restore-validation root.'
    }
    if (Test-Path -LiteralPath $continuityReceipt) { throw 'The HOME continuity restore proof already exists; proofs are write-once.' }
    $continuityParent = Split-Path -Parent $continuityReceipt
    New-Item -ItemType Directory -Path $continuityParent -Force | Out-Null
}
$bin = [IO.Path]::GetFullPath($PostgresBin)
$script:continuityJournalState = $null
$continuityApiAssembly = $null
if ($ContinuityOperationJournalPath) {
    if ([string]::IsNullOrWhiteSpace($ContinuityOperationId)) { throw 'A continuity operation ID is required when an operation journal is supplied.' }
    $ContinuityOperationJournalPath = [IO.Path]::GetFullPath($ContinuityOperationJournalPath)
    $journalRoot = [IO.Path]::GetFullPath($installation.RestoreValidation) + [IO.Path]::DirectorySeparatorChar
    if (-not $ContinuityOperationJournalPath.StartsWith($journalRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'The continuity operation journal must remain under restore-validation.' }
    if (-not (Test-Path -LiteralPath $ContinuityOperationJournalPath -PathType Leaf)) { throw 'The continuity operation journal does not exist.' }
    $script:continuityJournalState = [ordered]@{}
    $loadedJournal = ConvertFrom-Json -InputObject (Get-Content -LiteralPath $ContinuityOperationJournalPath -Raw)
    foreach ($property in $loadedJournal.PSObject.Properties) { $script:continuityJournalState[$property.Name] = $property.Value }
    if ([string]$script:continuityJournalState.OperationId -ne $ContinuityOperationId) { throw 'The continuity operation journal ID does not match the requested operation.' }
    if ([string]$script:continuityJournalState.Status -in @('cleaned','recovery-required')) { throw 'The continuity operation journal is already terminal.' }
    if (-not $CleanupContinuityOperation -and [string]$script:continuityJournalState.TargetDatabase -ne $TargetDatabase) { throw 'The continuity operation target database does not match its journal.' }
    if (-not $CleanupContinuityOperation) {
        if ([string]::IsNullOrWhiteSpace($ContinuityApiAssemblyPath) -or [string]::IsNullOrWhiteSpace($ContinuityApiAssemblySha256)) { throw 'The continuity API entry assembly identity is required for a fresh operation.' }
        $continuityApiAssembly = [IO.Path]::GetFullPath($ContinuityApiAssemblyPath)
        if (-not (Test-Path -LiteralPath $continuityApiAssembly -PathType Leaf)) { throw 'The continuity API entry assembly is missing.' }
        $actualApiAssemblySha256 = (Get-FileHash -LiteralPath $continuityApiAssembly -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actualApiAssemblySha256 -ne $ContinuityApiAssemblySha256.ToLowerInvariant()) { throw 'The continuity API entry assembly hash does not match the supplied identity.' }
    }
    if ($CleanupContinuityOperation) {
        try {
            $cleanupRuntimeBefore = Get-ContinuityRuntimeIdentity
            Invoke-ContinuityOwnedCleanup
            $cleanupRuntimeAfter = Get-ContinuityRuntimeIdentity
            Write-ContinuityJournal 'cleaned' 'cleanup-complete' @{ CleanupDisposition = 'Only the operation-owned database and staging paths were removed.'; DiagnosticReceiptPath = "$ContinuityOperationJournalPath.receipt.json"; CleanupRuntimeBefore = $cleanupRuntimeBefore; CleanupRuntimeAfter = $cleanupRuntimeAfter }
            [void](Write-ContinuityDiagnosticReceipt)
            exit 0
        }
        catch {
            if ([string]$script:continuityJournalState.Status -ne 'cleaned') {
                try { Write-ContinuityJournal 'recovery-required' 'cleanup-failed' @{ CleanupDisposition = 'The cleanup child failed before it could prove a cleaned terminal state; attended recovery is required.'; Failure = 'Cleanup failed; details are intentionally redacted.' } } catch { }
                try { [void](Write-ContinuityDiagnosticReceipt) } catch { }
            }
            throw "The continuity cleanup requires attended recovery at $ContinuityOperationJournalPath."
        }
    }
    $continuityRuntimeBefore = Get-ContinuityRuntimeIdentity
    Write-ContinuityJournal 'running' 'restore-started' @{
        ChildProcess = [ordered]@{ ProcessId = $PID; ProcessStartedUtc = (Get-Process -Id $PID).StartTime.ToUniversalTime().ToString('o'); ImagePath = [IO.Path]::GetFullPath($PSCommandPath); ImageSha256 = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash.ToLowerInvariant() }
        RuntimeBefore = $continuityRuntimeBefore
        PostgreSqlBinRoot = $bin
        PostgreSqlBinRootSha256 = (Get-FileHash -LiteralPath (Join-Path $bin 'psql.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}
if ([string]::IsNullOrWhiteSpace($BackupArchive)) { throw '-BackupArchive is required for a restore operation.' }
$archive = (Resolve-Path -LiteralPath $BackupArchive).Path
$restoreRoot = $installation.RestoreWork; New-Item -ItemType Directory -Path $restoreRoot -Force | Out-Null
$temporary = Join-Path $restoreRoot ([Guid]::NewGuid().ToString('N')); New-Item -ItemType Directory -Path $temporary | Out-Null
$token = [Guid]::NewGuid().ToString('N').Substring(0, 12)
$restoreDatabase = if ($production) { "aerolink_restore_stage_$token" } else { $TargetDatabase }
$oldDatabase = "aerolink_pre_restore_$token"
$failedDatabase = "aerolink_failed_restore_$token"
$resolvedTarget = $null; $incoming = $null; $retained = $null
$continuityDatabaseCreated = $false; $continuityDatabaseOid = $null
$preserveContinuityRecovery = $false
$originalDatabaseRenamed = $false; $databaseActivated = $false; $evidenceActivated = $false; $activationPassed = $false

try {
    if ($RequireFreshTarget) { Assert-DatabaseAbsent $restoreDatabase }
    if ($ContinuityOperationJournalPath) {
        Write-ContinuityJournal 'running' 'archive-expansion' @{
            TargetDatabase = $restoreDatabase
            RestoreValidationRoot = [IO.Path]::GetFullPath($installation.RestoreValidation)
            RestoreWorkRoot = [IO.Path]::GetFullPath($installation.RestoreWork)
            ProofPath = $continuityReceipt
            ArchivePath = $archive
            ArchiveSha256 = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
            RestoreScriptSha256 = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash.ToLowerInvariant()
            TemporaryPath = $temporary
            PostgresPort = [int]$PostgresPort
        }
    }
    Expand-AeroLinkBackupArchive -ArchivePath $archive -DestinationDirectory $temporary
    $manifest = ConvertFrom-Json -InputObject (Get-Content -LiteralPath (Join-Path $temporary 'manifest.json') -Raw)
    $archiveInventoryObject = ConvertFrom-Json -InputObject (Get-Content -LiteralPath (Join-Path $temporary ([string]$manifest.AttachmentInventory)) -Raw)
    $archiveInventory = @($archiveInventoryObject | ForEach-Object { $_ })
    $dump = Join-Path $temporary ([string]$manifest.Database.Dump)
    Invoke-Fault 'BeforeDatabaseRestore'
    if ($ContinuityOperationJournalPath) { Write-ContinuityJournal 'running' 'database-creation' @{} }
    New-RestoredDatabase $restoreDatabase $dump
    if ($ContinuityOperationJournalPath) {
        $continuityDatabaseCreated = $true
        $continuityDatabaseOid = Get-ContinuityDatabaseOid $restoreDatabase
        if ([string]::IsNullOrWhiteSpace($continuityDatabaseOid)) { throw 'The fresh continuity database was created but its PostgreSQL identity could not be read.' }
        Write-ContinuityJournal 'running' 'database-created' @{ CreatedByOperation = $true; CreatedDatabaseOid = $continuityDatabaseOid }
    }
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
    if ($ContinuityOperationJournalPath -and (Test-Path -LiteralPath $resolvedTarget)) { throw 'The fresh continuity evidence target already exists; refusing to replace it.' }
    $parent = Split-Path $resolvedTarget -Parent; New-Item -ItemType Directory -Path $parent -Force | Out-Null
    $incoming = Join-Path $parent ('.restore-incoming-' + $token)
    if (Test-Path -LiteralPath $incoming) { Remove-Item -LiteralPath (ConvertTo-AeroLinkArchiveIoPath $incoming) -Recurse -Force }
    New-Item -ItemType Directory -Path $incoming -Force | Out-Null
    if ($ContinuityOperationJournalPath) { Write-ContinuityJournal 'running' 'evidence-staging' @{ EvidencePath = $resolvedTarget; IncomingPath = $incoming; EvidenceOwned = $true } }
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
    if ($continuityReceipt) {
        $continuityProof = [ordered]@{
            FormatVersion = 1
            ArchivePath = $archive
            ArchiveSha256 = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
            InstanceId = if ($manifest.Instance -and $manifest.Instance.InstanceId) { [string]$manifest.Instance.InstanceId } else { $null }
            ApplicationSourceSha = if ($manifest.Application -and $manifest.Application.SourceSha) { [string]$manifest.Application.SourceSha } else { $null }
            DatabaseName = [string]$TargetDatabase
            DatabaseOid = $continuityDatabaseOid
            PostgresPort = [int]$PostgresPort
            OperationId = $ContinuityOperationId
            OperationJournalPath = if ($ContinuityOperationJournalPath) { [IO.Path]::GetFullPath($ContinuityOperationJournalPath) } else { $null }
            ApiEntryAssemblyPath = $continuityApiAssembly
            ApiEntryAssemblySha256 = $ContinuityApiAssemblySha256
            InstallationRoot = [IO.Path]::GetFullPath($installation.InstallationRoot)
            RestoreValidationRoot = [IO.Path]::GetFullPath($installation.RestoreValidation)
            CompletedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
            RestoreScriptSha256 = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash.ToLowerInvariant()
            CurrentCodeValidationDeferred = [bool]$deferCurrentCode
            AttachmentInventoryVerified = $true
            PostgresRuntimeBefore = $continuityRuntimeBefore
            PostgresRuntimeAfter = if ($ContinuityOperationJournalPath) { Get-ContinuityRuntimeIdentity } else { $null }
            PostgresToolIdentities = if ($ContinuityOperationJournalPath) { Get-ContinuityToolIdentities } else { $null }
        }
        $json = $continuityProof | ConvertTo-Json -Depth 8
        $proofStream = [IO.File]::Open($continuityReceipt, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try {
            $writer = New-Object IO.StreamWriter($proofStream, [Text.UTF8Encoding]::new($false), 4096, $true)
            try { $writer.Write($json); $writer.Flush() } finally { $writer.Dispose() }
        } finally { $proofStream.Dispose() }
        Write-Host "HOME continuity disposable restore proof: $continuityReceipt" -ForegroundColor Green
    }
    if ($ContinuityOperationJournalPath) {
        $continuityRuntimeAfter = Get-ContinuityRuntimeIdentity
        Write-ContinuityJournal 'completed' 'restore-complete' @{ RuntimeAfter = $continuityRuntimeAfter; ProofPath = $continuityReceipt; EvidencePath = $resolvedTarget; IncomingPath = $incoming; TemporaryPath = $temporary; CreatedDatabaseOid = $continuityDatabaseOid; CreatedByOperation = $continuityDatabaseCreated }
    }
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
    if ($ContinuityOperationJournalPath) {
        try {
            $preserveContinuityRecovery = $true
            Write-ContinuityJournal 'failed' 'restore-failed' @{ Failure = 'The restore authority refused the operation; details are intentionally redacted.'; CreatedDatabaseOid = $continuityDatabaseOid; CreatedByOperation = $continuityDatabaseCreated; EvidencePath = $resolvedTarget; IncomingPath = $incoming; TemporaryPath = $temporary }
            if ($continuityDatabaseCreated) { Invoke-ContinuityOwnedCleanup; Write-ContinuityJournal 'cleaned' 'cleanup-complete' @{ CleanupDisposition = 'Only the operation-owned database and staging paths were removed.'; DiagnosticReceiptPath = "$ContinuityOperationJournalPath.receipt.json" } }
            else { Write-ContinuityJournal 'cleaned' 'cleanup-complete' @{ CleanupDisposition = 'The target was not created by this operation; it was preserved.' } }
            [void](Write-ContinuityDiagnosticReceipt)
            $preserveContinuityRecovery = $false
        }
        catch {
            if ($script:continuityJournalState.Status -ne 'recovery-required') { Write-ContinuityJournal 'recovery-required' 'cleanup-failed' @{ CleanupDisposition = 'Cleanup could not prove exclusive ownership; target and recovery paths were preserved.' } }
            try { [void](Write-ContinuityDiagnosticReceipt) } catch { }
            throw "Restore failed and product-owned cleanup requires attended recovery at $ContinuityOperationJournalPath."
        }
    }
    throw $failure
}
finally {
    if (-not $preserveContinuityRecovery) {
        if ($incoming -and (Test-Path -LiteralPath $incoming)) { Remove-Item -LiteralPath (ConvertTo-AeroLinkArchiveIoPath $incoming) -Recurse -Force }
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath (ConvertTo-AeroLinkArchiveIoPath $temporary) -Recurse -Force }
    }
    if ($production -and -not $databaseActivated) {
        try { Remove-Database $restoreDatabase } catch { }
    }
}
