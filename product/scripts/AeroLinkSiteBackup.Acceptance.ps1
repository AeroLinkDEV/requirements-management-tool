#Requires -Version 5.1
<#
    Site backup and restore acceptance (#1614 1.9), on a disposable site cluster. Run by hand; not a CI suite.

    What it proves, end to end, through the real scripts:
      1. a site installation is declared and its cluster initialised exactly as the installer will
         (Set-AeroLinkSiteDeclaration, Initialize-AeroLinkSitePostgres): SCRAM only, a non-superuser owner role;
      2. data written AS that role, including a pg_trgm index (AeroLink's migrations create that extension),
         is backed up by Backup-AeroLink.ps1 -InstallationRoot, as the application role, password from the
         protected store, into <root>\backups;
      3. Verify-AeroLinkBackup.ps1 accepts the archive;
      4. Restore-AeroLink.ps1 -InstallationRoot restores it into an isolated database, which ends owned by the
         application role (not postgres), with every relation the role's, the rows intact, and writable by it.

    Everything lives under one temporary root on a free loopback port below the dynamic range, never 54329. The
    persistent HOME cluster, product\.local and the machine protected-config store are never touched: the
    secrets go to a temporary store (-ProtectedConfigRoot), readable by the current user, who stands in for
    both service accounts and the maintenance identity. Non-elevated.

    -PostgresBin names a PostgreSQL 18 bin folder (with lib and share beside it).
#>
[CmdletBinding()]
param([Parameter(Mandatory)][string]$PostgresBin, [switch]$KeepOnFailure)

$ErrorActionPreference = 'Stop'
$PostgresBin = [IO.Path]::GetFullPath($PostgresBin)
Import-Module (Join-Path $PSScriptRoot 'AeroLinkInstallation.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'AeroLinkSiteInstallation.psm1') -Force
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

$me = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$work = Join-Path ([IO.Path]::GetTempPath()) ('aerolink-site-backup-' + [guid]::NewGuid().ToString('N'))
$root = Join-Path $work 'AeroLink Production'
$secrets = Join-Path $work 'protected'
$authority = Join-Path $work 'notification-authority'
New-Item -ItemType Directory -Path $root, $secrets -Force | Out-Null
$started = $false
$data = Join-Path $root 'pgdata'
$passed = $false
try {
    @{ classification = 'SiteProduction'; label = 'SITE PRODUCTION (ACCEPTANCE)' } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'instance.json') -Encoding UTF8
    $apiPort = Get-FreeLoopbackPort
    $pgPort = Get-FreeLoopbackPort
    while ($pgPort -eq $apiPort) { $pgPort = Get-FreeLoopbackPort }
    $site = Set-AeroLinkSiteDeclaration -InstallationRoot $root -ApiPort $apiPort -PostgresPort $pgPort -RunAsSid $me -PostgresRunAsSid $me `
        -PublicHostName 'aerolink-acceptance.example.test' -NotificationAuthorityRoot $authority
    Write-Host "Site declared: API $apiPort, PostgreSQL $pgPort, role $($site.DatabaseRole)." -ForegroundColor Cyan

    $init = Initialize-AeroLinkSitePostgres -InstallationRoot $root -PostgresBin $PostgresBin -ProtectedConfigRoot $secrets -MaintenanceSid $me
    Assert-True ($init.DatabaseOwner -eq $site.DatabaseRole -and $init.Authentication -eq 'scram-sha-256') 'The site cluster is initialised: SCRAM only, aerolink owned by the application role.'

    # Through the native runner with its output in files: the postmaster inherits pg_ctl's handles, so a captured
    # pipe would never close.
    $start = Invoke-AeroLinkNativeCommand -FilePath (Join-Path $PostgresBin 'pg_ctl.exe') `
        -ArgumentList @('-D', $data, '-l', (Join-Path $work 'postgres.log'), '-w', '-t', '120', 'start') `
        -StandardOutput (Join-Path $work 'pg_ctl-start.stdout.log') -StandardError (Join-Path $work 'pg_ctl-start.stderr.log') `
        -TimeoutSeconds 150 -StepName 'pg_ctl start (site acceptance)'
    if ($start.ExitCode -ne 0) { throw "The disposable site cluster did not start: $($start.Detail)" }
    $started = $true

    $app = Get-AeroLinkSitePostgresAccess -InstallationRoot $root -Role Application -ProtectedConfigRoot $secrets
    function Invoke-AppSql([string]$Database, [string]$Sql) {
        $path = Join-Path $work ('q-' + [guid]::NewGuid().ToString('N') + '.sql')
        [IO.File]::WriteAllText($path, $Sql, (New-Object Text.UTF8Encoding($false)))
        try {
            return (Invoke-AeroLinkWithPostgresAccess $app {
                $out = & (Join-Path $PostgresBin 'psql.exe') -X -w -h 127.0.0.1 -p $pgPort -U $app.User -d $Database -v ON_ERROR_STOP=1 -q -tA -f $path
                if ($LASTEXITCODE -ne 0) { throw "SQL as $($app.User) failed in $Database." }
                ($out -join "`n").Trim()
            })
        }
        finally { Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue }
    }

    # Seeded as the application role, as the API will create its schema; pg_trgm is trusted, so its owner may create it.
    [void](Invoke-AppSql 'aerolink' @'
CREATE EXTENSION IF NOT EXISTS pg_trgm;
CREATE TABLE site_backup_probe (id int PRIMARY KEY, note text NOT NULL);
CREATE INDEX site_backup_probe_note ON site_backup_probe USING gin (note gin_trgm_ops);
INSERT INTO site_backup_probe VALUES (1, 'alpha'), (2, 'bravo'), (3, 'charlie');
'@)

    # Without the password the tools must refuse, not prompt: -w everywhere.
    $env:PGPASSWORD = $null
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'   # Windows PowerShell turns the expected stderr line into a terminating error.
    try { & (Join-Path $PostgresBin 'psql.exe') -X -w -h 127.0.0.1 -p $pgPort -U $app.User -d aerolink -tA -c 'SELECT 1' 2>&1 | Out-Null; $refused = $LASTEXITCODE -ne 0 }
    finally { $ErrorActionPreference = $previousPreference }
    Assert-True $refused 'Without its password a site login is refused rather than prompted.'

    $backup = & (Join-Path $PSScriptRoot 'Backup-AeroLink.ps1') -InstallationRoot $root -ProtectedConfigRoot $secrets -PostgresBin $PostgresBin -RetentionDays 15 |
        Where-Object { $_ -is [pscustomobject] -and $_.PSObject.Properties['Archive'] } | Select-Object -Last 1
    Assert-True ($null -ne $backup -and (Test-Path -LiteralPath $backup.Archive -PathType Leaf)) 'Backup-AeroLink.ps1 produced an archive for the site.'
    Assert-True ($backup.BackupRoot -eq (Join-Path $root 'backups')) "The site archive is under <root>\backups: $($backup.BackupRoot)"
    Assert-True ($null -eq $env:PGPASSWORD) 'The backup leaves no password in the environment.'
    $manifest = $null
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($backup.Archive)
    try { $reader = New-Object IO.StreamReader($zip.GetEntry('manifest.json').Open()); try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() } }
    finally { $zip.Dispose() }
    Assert-True ($manifest.Instance.Classification -eq 'SiteProduction') "The archive records the site classification: $($manifest.Instance.Classification)"

    $verified = & (Join-Path $PSScriptRoot 'Verify-AeroLinkBackup.ps1') -BackupArchive $backup.Archive -InstallationRoot $root
    Assert-True ($verified.Valid) 'Verify-AeroLinkBackup.ps1 accepts the site archive.'

    & (Join-Path $PSScriptRoot 'Restore-AeroLink.ps1') -BackupArchive $backup.Archive -TargetDatabase 'aerolink_restore_validation' `
        -InstallationRoot $root -ProtectedConfigRoot $secrets -PostgresBin $PostgresBin -SkipCurrentCodeValidation | Out-Host
    Assert-True ($null -eq $env:PGPASSWORD) 'The restore leaves no password in the environment.'

    $owner = Invoke-AppSql 'aerolink_restore_validation' "SELECT pg_get_userbyid(datdba) FROM pg_database WHERE datname = current_database();"
    Assert-True ($owner -eq $site.DatabaseRole) "The restored database is owned by the application role: $owner"
    $foreign = Invoke-AppSql 'aerolink_restore_validation' "SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'public' AND pg_get_userbyid(c.relowner) <> current_user;"
    Assert-True ($foreign -eq '0') "Every restored relation is owned by the application role ($foreign foreign)."
    $rows = Invoke-AppSql 'aerolink_restore_validation' "SELECT count(*) || '|' || string_agg(note, ',' ORDER BY id) FROM site_backup_probe;"
    Assert-True ($rows -eq '3|alpha,bravo,charlie') "The restored rows are intact: $rows"
    $trigram = Invoke-AppSql 'aerolink_restore_validation' "SELECT count(*) FROM site_backup_probe WHERE note % 'alpah';"
    Assert-True ($trigram -eq '1') 'The pg_trgm extension and its index are restored and usable.'
    $written = Invoke-AppSql 'aerolink_restore_validation' "INSERT INTO site_backup_probe VALUES (4, 'delta'); SELECT count(*) FROM site_backup_probe;"
    Assert-True ($written -eq '4') 'The application role can write to the restored database.'
    $public = Invoke-AppSql 'aerolink_restore_validation' "SELECT has_database_privilege('public', current_database(), 'CONNECT');"
    Assert-True ($public -eq 'f') 'The restored database is closed to PUBLIC, as aerolink is.'

    # Phase 3: a HOME archive, whose objects postgres owns and whose dump was taken as postgres, restored into a
    # site. The archive is made by Backup-AeroLink.ps1's HOME path (no site declaration), pointed at a database
    # in this cluster that the superuser owns; the superuser password reaches it the same way, through PGPASSWORD.
    $super = Get-AeroLinkSitePostgresAccess -InstallationRoot $root -Role Superuser -ProtectedConfigRoot $secrets
    $homeRoot = Join-Path $work 'home-installation'
    New-Item -ItemType Directory -Path $homeRoot, (Join-Path $homeRoot 'evidence') -Force | Out-Null
    $homeSql = Join-Path $work 'home-origin.sql'
    [IO.File]::WriteAllText($homeSql, @'
CREATE EXTENSION IF NOT EXISTS pg_trgm;
CREATE TABLE home_probe (id int PRIMARY KEY, note text NOT NULL);
CREATE INDEX home_probe_note ON home_probe USING gin (note gin_trgm_ops);
GRANT SELECT ON home_probe TO PUBLIC;
INSERT INTO home_probe VALUES (1, 'home-one'), (2, 'home-two');
'@, (New-Object Text.UTF8Encoding($false)))
    Invoke-AeroLinkWithPostgresAccess $super {
        & (Join-Path $PostgresBin 'createdb.exe') -w -h 127.0.0.1 -p $pgPort -U postgres aerolink_home_origin
        if ($LASTEXITCODE -ne 0) { throw 'Could not create the HOME-origin database.' }
        & (Join-Path $PostgresBin 'psql.exe') -X -w -q -h 127.0.0.1 -p $pgPort -U postgres -d aerolink_home_origin -v ON_ERROR_STOP=1 -f $homeSql
        if ($LASTEXITCODE -ne 0) { throw 'Could not seed the HOME-origin database.' }
    }
    $homeBackup = Invoke-AeroLinkWithPostgresAccess $super {
        & (Join-Path $PSScriptRoot 'Backup-AeroLink.ps1') -InstallationRoot $homeRoot -Database aerolink_home_origin -PostgresPort $pgPort `
            -PostgresBin $PostgresBin -EvidenceRoot (Join-Path $homeRoot 'evidence') -PostgresAlreadyRunning -RetentionDays 0 |
            Where-Object { $_ -is [pscustomobject] -and $_.PSObject.Properties['Archive'] } | Select-Object -Last 1
    }
    Assert-True ($null -ne $homeBackup) 'A HOME-format archive was taken as postgres.'
    & (Join-Path $PSScriptRoot 'Restore-AeroLink.ps1') -BackupArchive $homeBackup.Archive -TargetDatabase 'aerolink_restore_home' `
        -InstallationRoot $root -ProtectedConfigRoot $secrets -PostgresBin $PostgresBin -SkipCurrentCodeValidation | Out-Host
    $homeForeign = Invoke-AppSql 'aerolink_restore_home' "SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'public' AND pg_get_userbyid(c.relowner) <> current_user;"
    Assert-True ($homeForeign -eq '0') "Every relation of the restored HOME archive is owned by the application role ($homeForeign foreign)."
    $homeRows = Invoke-AppSql 'aerolink_restore_home' "INSERT INTO home_probe VALUES (3, 'site-three'); SELECT count(*) || '|' || bool_or(note % 'home-oen') FROM home_probe;"
    Assert-True ($homeRows -eq '3|true') "The restored HOME data is readable, searchable by trigram and writable by the application role: $homeRows"
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
