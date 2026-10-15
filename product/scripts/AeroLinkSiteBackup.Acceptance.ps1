#Requires -Version 5.1
<#
    Site backup and restore acceptance (#1614 1.9), on a disposable site cluster. Run by hand; not a CI suite.

    On the host the two PostgreSQL passwords have different readers and no process holds both (#1614 1.4):
      postgres-application  readable by the API's service account, NT SERVICE\AeroLink-<name>
      postgres-superuser    readable by the maintenance identity, an elevated administrator
    This acceptance keeps them apart too. After setup the secrets live in two separate protected stores, each
    holding ONE secret, and each step is given only the store its real caller would have:

      service store    application password only   the scheduled backup, run by the API's service account
      operator store   superuser password only     the drill, the Phase 3 restore and an operator's backup

    A step that reached for the other password would find it missing and fail, so this cannot pass with the two
    identities collapsed into one. Which caller a step is taken for follows the declaration: while the site's
    runAsSid is the current user, this process IS the service account; once it is the real virtual account's SID,
    which this process does not hold, it is the operator.

    What it proves, through the real scripts:
      1. the cluster is initialised exactly as the installer will (Set-AeroLinkSiteDeclaration,
         Initialize-AeroLinkSitePostgres): SCRAM only, a non-superuser owner role;
      2. the service backup runs from a RELEASE PACKAGE layout (#1614 1.6: app\, scripts\, manifest.json; no
         src\, no git) as the application role, into <root>\backups, recording the package's SHA and migration;
      3. Verify-AeroLinkBackup.ps1 accepts it;
      4. the drill restores it as postgres --role=<app>: every relation, function, type and schema the restore
         created belongs to the application role, the rows and the pg_trgm index are intact, the role can write;
      5. an operator's backup runs as postgres with only the superuser password;
      6. Phase 3: a HOME-format archive, taken as postgres from a postgres-owned database, restores into the site
         owned by the application role;
      7. after a restore moves evidence into place, Set-AeroLinkSiteEvidenceAccess gives the folder and its files
         back to the API account.

    Everything lives under one temporary root on a free loopback port below the dynamic range, never 54329. The
    persistent HOME cluster, product\.local and the machine protected-config store are never touched. Non-elevated.

    -PostgresBin names a PostgreSQL 18 bin folder (with lib and share beside it).
#>
[CmdletBinding()]
param([Parameter(Mandatory)][string]$PostgresBin, [switch]$KeepOnFailure)

$ErrorActionPreference = 'Stop'
$PostgresBin = [IO.Path]::GetFullPath($PostgresBin)
Import-Module (Join-Path $PSScriptRoot 'AeroLinkInstallation.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'AeroLinkSiteInstallation.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'AeroLinkProtectedConfig.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'AeroLinkNativeRunner.psm1') -Force

$failures = [System.Collections.Generic.List[string]]::new()
function Assert-True([bool]$Condition, [string]$Message) {
    if ($Condition) { Write-Host "  PASS  $Message" -ForegroundColor Green } else { Write-Host "  FAIL  $Message" -ForegroundColor Red; $script:failures.Add($Message) }
}
function Get-FreeLoopbackPort {
    for ($attempt = 0; $attempt -lt 200; $attempt++) {
        $candidate = Get-Random -Minimum 20000 -Maximum 40000
        if ($candidate -eq 54329) { continue }
        $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $candidate)
        try { $listener.Start(); return $candidate } catch [Net.Sockets.SocketException] { continue } finally { $listener.Stop() }
    }
    throw 'No free loopback port was found.'
}
function Get-ArchiveManifest([string]$Archive) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($Archive)
    try { $reader = New-Object IO.StreamReader($zip.GetEntry('manifest.json').Open()); try { return ($reader.ReadToEnd() | ConvertFrom-Json) } finally { $reader.Dispose() } }
    finally { $zip.Dispose() }
}
function Get-ArchiveResult { process { if ($_ -is [pscustomobject] -and $_.PSObject.Properties['Archive']) { $_ } } }
function Set-DeclaredRunAs([string]$Sid) {
    # Who the API's service account is. $null: the real virtual account, derived from the service name.
    $document = Get-Content -LiteralPath $instancePath -Raw | ConvertFrom-Json
    if ($document.site.PSObject.Properties['runAsSid']) { $document.site.PSObject.Properties.Remove('runAsSid') }
    if ($Sid) { $document.site | Add-Member -NotePropertyName runAsSid -NotePropertyValue $Sid }
    $document | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $instancePath -Encoding UTF8
}

$me = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$work = Join-Path ([IO.Path]::GetTempPath()) ('aerolink-site-backup-' + [guid]::NewGuid().ToString('N'))
$root = Join-Path $work 'AeroLink Production'
$instancePath = Join-Path $root 'instance.json'
$setupStore = Join-Path $work 'protected-setup'
$serviceStore = Join-Path $work 'protected-service'
$operatorStore = Join-Path $work 'protected-operator'
$authority = Join-Path $work 'notification-authority'
$package = Join-Path $work 'package'
New-Item -ItemType Directory -Path $root, $setupStore, $serviceStore, $operatorStore -Force | Out-Null
$started = $false
$data = Join-Path $root 'pgdata'
$passed = $false
try {
    @{ classification = 'SiteProduction'; label = 'SITE PRODUCTION (ACCEPTANCE)' } | ConvertTo-Json | Set-Content -LiteralPath $instancePath -Encoding UTF8
    $apiPort = Get-FreeLoopbackPort
    $pgPort = Get-FreeLoopbackPort
    while ($pgPort -eq $apiPort) { $pgPort = Get-FreeLoopbackPort }
    # Setup only: the installer's own run, as one identity. The cluster's data directory is granted to this user.
    $site = Set-AeroLinkSiteDeclaration -InstallationRoot $root -ApiPort $apiPort -PostgresPort $pgPort -RunAsSid $me -PostgresRunAsSid $me `
        -PublicHostName 'aerolink-acceptance.example.test' -NotificationAuthorityRoot $authority
    $init = Initialize-AeroLinkSitePostgres -InstallationRoot $root -PostgresBin $PostgresBin -ProtectedConfigRoot $setupStore -MaintenanceSid $me
    Assert-True ($init.DatabaseOwner -eq $site.DatabaseRole -and $init.Authentication -eq 'scram-sha-256') 'The site cluster is initialised: SCRAM only, aerolink owned by the application role.'

    # Split the secrets: one store per caller, ONE secret in each, then remove the setup store holding both.
    $app = Get-AeroLinkSitePostgresAccess -InstallationRoot $root -Role Application -ProtectedConfigRoot $setupStore
    $super = Get-AeroLinkSitePostgresAccess -InstallationRoot $root -Role Superuser -ProtectedConfigRoot $setupStore
    $null = Set-AeroLinkProtectedSiteSecret -InstallationRoot $root -Name 'postgres-application' -Secret $app.Password -ReaderSid $me -RootOverride $serviceStore
    $null = Set-AeroLinkProtectedSiteSecret -InstallationRoot $root -Name 'postgres-superuser' -Secret $super.Password -ReaderSid $me -RootOverride $operatorStore
    Remove-Item -LiteralPath $setupStore -Recurse -Force
    foreach ($pair in @(@($serviceStore, 'Superuser'), @($operatorStore, 'Application'))) {
        $missing = $false
        try { $null = Get-AeroLinkSitePostgresAccess -InstallationRoot $root -Role $pair[1] -ProtectedConfigRoot $pair[0] } catch { $missing = $true }
        Assert-True $missing "The $(Split-Path $pair[0] -Leaf) store cannot give the $($pair[1].ToLowerInvariant()) password."
    }

    # Through the native runner with its output in files: the postmaster inherits pg_ctl's handles, so a captured
    # pipe would never close.
    $start = Invoke-AeroLinkNativeCommand -FilePath (Join-Path $PostgresBin 'pg_ctl.exe') `
        -ArgumentList @('-D', $data, '-l', (Join-Path $work 'postgres.log'), '-w', '-t', '120', 'start') `
        -StandardOutput (Join-Path $work 'pg_ctl-start.stdout.log') -StandardError (Join-Path $work 'pg_ctl-start.stderr.log') `
        -TimeoutSeconds 150 -StepName 'pg_ctl start (site acceptance)'
    if ($start.ExitCode -ne 0) { throw "The disposable site cluster did not start: $($start.Detail)" }
    $started = $true

    # The acceptance's own checks may use either login; the scripts under test get one store each.
    function Invoke-Sql($Access, [string]$Database, [string]$Sql) {
        $path = Join-Path $work ('q-' + [guid]::NewGuid().ToString('N') + '.sql')
        [IO.File]::WriteAllText($path, $Sql, (New-Object Text.UTF8Encoding($false)))
        try {
            return (Invoke-AeroLinkWithPostgresAccess $Access {
                $out = & (Join-Path $PostgresBin 'psql.exe') -X -w -h 127.0.0.1 -p $pgPort -U $Access.User -d $Database -v ON_ERROR_STOP=1 -q -tA -f $path
                if ($LASTEXITCODE -ne 0) { throw "SQL as $($Access.User) failed in $Database." }
                ($out -join "`n").Trim()
            })
        }
        finally { Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue }
    }
    $ownershipSql = @"
WITH app AS (SELECT oid FROM pg_roles WHERE rolname = '$($site.DatabaseRole)'),
ext AS (SELECT classid, objid FROM pg_depend WHERE deptype = 'e'),
ns AS (SELECT oid, nspname, nspowner FROM pg_namespace WHERE nspname NOT IN ('pg_catalog','information_schema') AND nspname NOT LIKE 'pg_toast%' AND nspname NOT LIKE 'pg_temp%')
SELECT (SELECT count(*) FROM pg_class c JOIN ns ON ns.oid = c.relnamespace WHERE c.relowner <> (SELECT oid FROM app) AND NOT EXISTS (SELECT 1 FROM ext WHERE ext.classid = 'pg_class'::regclass AND ext.objid = c.oid))
     + (SELECT count(*) FROM pg_proc p JOIN ns ON ns.oid = p.pronamespace WHERE p.proowner <> (SELECT oid FROM app) AND NOT EXISTS (SELECT 1 FROM ext WHERE ext.classid = 'pg_proc'::regclass AND ext.objid = p.oid))
     + (SELECT count(*) FROM ns WHERE ns.nspowner <> (SELECT oid FROM app) AND NOT (ns.nspname = 'public' AND pg_get_userbyid(ns.nspowner) = 'pg_database_owner'))
     || '|' || (SELECT count(*) FROM pg_class c JOIN ns ON ns.oid = c.relnamespace WHERE c.relowner = (SELECT oid FROM app) AND c.relkind IN ('r','i'))
"@

    # Seeded as the application role, as the API will create its schema; pg_trgm is trusted, so its owner may create it.
    [void](Invoke-Sql $app 'aerolink' @'
CREATE EXTENSION IF NOT EXISTS pg_trgm;
CREATE TABLE site_backup_probe (id int PRIMARY KEY, note text NOT NULL);
CREATE INDEX site_backup_probe_note ON site_backup_probe USING gin (note gin_trgm_ops);
CREATE FUNCTION site_backup_probe_count() RETURNS bigint LANGUAGE sql AS 'SELECT count(*) FROM site_backup_probe';
INSERT INTO site_backup_probe VALUES (1, 'alpha'), (2, 'bravo'), (3, 'charlie');
'@)

    # Without its password a tool must refuse, not prompt: -w everywhere.
    $env:PGPASSWORD = $null
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'   # Windows PowerShell turns the expected stderr line into a terminating error.
    try { & (Join-Path $PostgresBin 'psql.exe') -X -w -h 127.0.0.1 -p $pgPort -U $app.User -d aerolink -tA -c 'SELECT 1' 2>&1 | Out-Null; $refused = $LASTEXITCODE -ne 0 }
    finally { $ErrorActionPreference = $previousPreference }
    Assert-True $refused 'Without its password a site login is refused rather than prompted.'

    # --- The scheduled backup: the service account, from a release package, with the application password only ---
    # A release package (#1614 1.6, #1635): manifest.json, app\ and scripts\, and no src\ or git.
    $packageScripts = Join-Path $package 'scripts'
    $packageApp = Join-Path $package 'app'
    New-Item -ItemType Directory -Path $packageScripts, $packageApp -Force | Out-Null
    Get-ChildItem -LiteralPath $PSScriptRoot -File | Where-Object { $_.Extension -in @('.ps1', '.psm1', '.psd1', '.sql') -and $_.Name -notlike '*.Tests.ps1' -and $_.Name -notlike '*.Acceptance.ps1' } |
        ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $packageScripts }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot '..\src\AeroLink.Api\appsettings.json') -Destination $packageApp
    Set-Content -LiteralPath (Join-Path $packageApp 'AeroLink.Api.exe') -Value '' -Encoding ASCII
    $packageSha = 'abcdef0123456789abcdef0123456789abcdef01'
    @{ schemaVersion = 1; version = '0.0.0-acceptance'; sourceSha = $packageSha; latestMigration = '20991231000000_AcceptanceMigration' } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $package 'manifest.json') -Encoding UTF8
    $serviceBackup = & (Join-Path $packageScripts 'Backup-AeroLink.ps1') -InstallationRoot $root -ProtectedConfigRoot $serviceStore -PostgresBin $PostgresBin -RetentionDays 15 | Get-ArchiveResult | Select-Object -Last 1
    Assert-True ($null -ne $serviceBackup -and (Test-Path -LiteralPath $serviceBackup.Archive -PathType Leaf)) 'The service account backs up the site from a release package, with the application password only.'
    Assert-True ($serviceBackup.BackupRoot -eq (Join-Path $root 'backups')) "The archive is under <root>\backups: $($serviceBackup.BackupRoot)"
    Assert-True ($null -eq $env:PGPASSWORD) 'The backup leaves no password in the environment.'
    $serviceManifest = Get-ArchiveManifest $serviceBackup.Archive
    Assert-True ($serviceManifest.Instance.Classification -eq 'SiteProduction' -and $serviceManifest.Application.SourceSha -eq $packageSha -and $serviceManifest.Application.SchemaVersion -eq '20991231000000_AcceptanceMigration') `
        "The archive records the site and the package's own SHA and migration: $($serviceManifest.Instance.Classification) $($serviceManifest.Application.SourceSha) $($serviceManifest.Application.SchemaVersion)"

    $verified = & (Join-Path $packageScripts 'Verify-AeroLinkBackup.ps1') -BackupArchive $serviceBackup.Archive -InstallationRoot $root
    Assert-True ($verified.Valid) 'Verify-AeroLinkBackup.ps1 accepts the site archive.'

    # --- From here this process is the operator: the service account is the real virtual account, not this user ---
    Set-DeclaredRunAs $null
    $declared = Get-AeroLinkSiteDeclaration -InstallationRoot $root
    Assert-True ($declared.RunAsSid -ne $me) "The site's service account is now $($declared.RunAsAccount), which this process does not hold."

    $refusedWrongStore = $false
    try { & (Join-Path $PSScriptRoot 'Backup-AeroLink.ps1') -InstallationRoot $root -ProtectedConfigRoot $serviceStore -PostgresBin $PostgresBin -RetentionDays 15 | Out-Null }
    catch { $refusedWrongStore = $_.Exception.Message -match 'maintenance identity' }
    Assert-True $refusedWrongStore 'An operator without the superuser password is refused, naming both routes.'

    & (Join-Path $PSScriptRoot 'Restore-AeroLink.ps1') -BackupArchive $serviceBackup.Archive -TargetDatabase 'aerolink_restore_validation' `
        -InstallationRoot $root -ProtectedConfigRoot $operatorStore -PostgresBin $PostgresBin -SkipCurrentCodeValidation | Out-Host
    Assert-True ($null -eq $env:PGPASSWORD) 'The restore leaves no password in the environment.'
    $owner = Invoke-Sql $super 'aerolink_restore_validation' "SELECT pg_get_userbyid(datdba) FROM pg_database WHERE datname = current_database();"
    Assert-True ($owner -eq $site.DatabaseRole) "The drill restored, with the superuser password only, into a database owned by the application role: $owner"
    $ownership = Invoke-Sql $super 'aerolink_restore_validation' $ownershipSql
    Assert-True ($ownership -eq '0|3') "Every restored relation, function and schema belongs to the application role (foreign|owned: $ownership)."
    $rows = Invoke-Sql $app 'aerolink_restore_validation' "SELECT site_backup_probe_count() || '|' || string_agg(note, ',' ORDER BY id) || '|' || bool_or(note % 'alpah') FROM site_backup_probe;"
    Assert-True ($rows -eq '3|alpha,bravo,charlie|true') "The rows, the function and the pg_trgm index are restored: $rows"
    $written = Invoke-Sql $app 'aerolink_restore_validation' "INSERT INTO site_backup_probe VALUES (4, 'delta'); SELECT count(*) FROM site_backup_probe;"
    Assert-True ($written -eq '4') 'The application role can write to the restored tables.'
    $public = Invoke-Sql $super 'aerolink_restore_validation' "SELECT has_database_privilege('public', current_database(), 'CONNECT');"
    Assert-True ($public -eq 'f') 'The restored database is closed to PUBLIC, as aerolink is.'

    $operatorBackup = & (Join-Path $PSScriptRoot 'Backup-AeroLink.ps1') -InstallationRoot $root -ProtectedConfigRoot $operatorStore -PostgresBin $PostgresBin -RetentionDays 15 | Get-ArchiveResult | Select-Object -Last 1
    Assert-True ($null -ne $operatorBackup) "An operator's backup runs as postgres with the superuser password only."

    # --- Phase 3: a HOME archive, objects owned by postgres, taken as postgres, restored into the site ---
    $homeRoot = Join-Path $work 'home-installation'
    New-Item -ItemType Directory -Path $homeRoot, (Join-Path $homeRoot 'evidence') -Force | Out-Null
    [void](Invoke-Sql $super 'postgres' 'CREATE DATABASE aerolink_home_origin;')
    [void](Invoke-Sql $super 'aerolink_home_origin' @'
CREATE EXTENSION IF NOT EXISTS pg_trgm;
CREATE TABLE home_probe (id int PRIMARY KEY, note text NOT NULL);
CREATE INDEX home_probe_note ON home_probe USING gin (note gin_trgm_ops);
GRANT SELECT ON home_probe TO PUBLIC;
INSERT INTO home_probe VALUES (1, 'home-one'), (2, 'home-two');
'@)
    # Backup's HOME path (no site declaration), against a disposable HOME-shaped installation named by the
    # qualification override, with the superuser password in PGPASSWORD for this one call.
    $previousOverride = $env:AEROLINK_INSTALLATION_ROOT
    try {
        $env:AEROLINK_INSTALLATION_ROOT = $homeRoot
        $homeBackup = Invoke-AeroLinkWithPostgresAccess $super {
            & (Join-Path $PSScriptRoot 'Backup-AeroLink.ps1') -Database aerolink_home_origin -PostgresPort $pgPort `
                -PostgresBin $PostgresBin -EvidenceRoot (Join-Path $homeRoot 'evidence') -PostgresAlreadyRunning -RetentionDays 0 | Get-ArchiveResult | Select-Object -Last 1
        }
    }
    finally { $env:AEROLINK_INSTALLATION_ROOT = $previousOverride }
    Assert-True ($null -ne $homeBackup) 'A HOME-format archive was taken as postgres.'
    & (Join-Path $PSScriptRoot 'Restore-AeroLink.ps1') -BackupArchive $homeBackup.Archive -TargetDatabase 'aerolink_restore_home' `
        -InstallationRoot $root -ProtectedConfigRoot $operatorStore -PostgresBin $PostgresBin -SkipCurrentCodeValidation | Out-Host
    $homeOwnership = Invoke-Sql $super 'aerolink_restore_home' $ownershipSql
    Assert-True ($homeOwnership -eq '0|3') "Every object of the restored HOME archive belongs to the application role (foreign|owned: $homeOwnership)."
    $homeRows = Invoke-Sql $app 'aerolink_restore_home' "INSERT INTO home_probe VALUES (3, 'site-three'); SELECT count(*) || '|' || bool_or(note % 'home-oen') FROM home_probe;"
    Assert-True ($homeRows -eq '3|true') "The restored HOME data is readable, searchable by trigram and writable by the application role: $homeRows"

    # --- Evidence permissions after a restore moves a folder into place (the production activation path) ---
    # The incoming folder is built inside the installation root, inheriting its permissions, then moved into place.
    Set-DeclaredRunAs $me   # the API account must be a SID whose access this non-elevated run can still exercise
    $evidence = Join-Path $root 'evidence'
    Set-AeroLinkDirectoryAccessBySid -Path $root -Grants @(@{ Sid = $me; Access = 'FullControl' }, @{ Sid = 'S-1-5-32-545'; Access = 'Read' })
    $incoming = Join-Path $root '.restore-incoming-acceptance'
    New-Item -ItemType Directory -Path (Join-Path $incoming 'objects') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $incoming 'objects\restored.bin') -Value 'restored' -Encoding ASCII
    if (Test-Path -LiteralPath $evidence) { Remove-Item -LiteralPath $evidence -Recurse -Force }
    Move-Item -LiteralPath $incoming -Destination $evidence
    $before = Get-AeroLinkDirectoryAccessBySid -Path $evidence
    Assert-True (-not $before.Protected) 'Before the fix, the moved evidence folder carries the installation root''s inherited permissions.'
    $after = Set-AeroLinkSiteEvidenceAccess -InstallationRoot $root -Path $evidence
    $fileRules = @((Get-Acl -LiteralPath (Join-Path $evidence 'objects\restored.bin')).Access | Where-Object {
        $_.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value -eq $me -and ($_.FileSystemRights -band [Security.AccessControl.FileSystemRights]::Modify) -eq [Security.AccessControl.FileSystemRights]::Modify })
    $usersOnFile = @((Get-Acl -LiteralPath (Join-Path $evidence 'objects\restored.bin')).Access | Where-Object { $_.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value -eq 'S-1-5-32-545' })
    Assert-True ($after.Protected -and $fileRules.Count -gt 0 -and $usersOnFile.Count -eq 0) `
        'After a restore, the evidence folder is protected again and every file in it is writable by the API account, with nothing inherited from the root.'
    $passed = $failures.Count -eq 0
}
finally {
    if ($started) { & (Join-Path $PostgresBin 'pg_ctl.exe') -D $data -m fast -w -t 60 stop }
    if ($passed -or -not $KeepOnFailure) { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
    else { Write-Host "Kept for inspection: $work" -ForegroundColor Yellow }
}
if ($failures.Count -gt 0) { Write-Host "Site backup acceptance FAILED ($($failures.Count))." -ForegroundColor Red; exit 1 }
Write-Host 'Site backup acceptance passed.' -ForegroundColor Green
exit 0
