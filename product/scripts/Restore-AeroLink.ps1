[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BackupArchive,
    [string]$TargetDatabase = 'aerolink_restore_validation',
    [string]$EvidenceTarget,
    [int]$PostgresPort = 54329,
    [string]$PostgresBin,
    [int]$ValidationApiPort = 5091,
    [switch]$DisposableQualification,
    [switch]$AllowProductionRestore,
    [string]$Confirmation,
    [ValidateSet('','BeforeDatabaseRestore','AfterDatabaseRestore','AfterEvidenceCopy','AfterPreActivationValidation','AfterOriginalDatabaseRename','AfterDatabaseActivation','AfterEvidenceActivation','AfterActivationValidation','BeforeRestart','AfterRestart')]
    [string]$FaultInjection = ''
)

$ErrorActionPreference = 'Stop'
$productRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Import-Module (Join-Path $PSScriptRoot 'AeroLinkEvidenceStore.psm1') -Force
if ($TargetDatabase -notmatch '^[a-zA-Z][a-zA-Z0-9_]{0,62}$') { throw 'The target database name is unsafe.' }
$production = $TargetDatabase -eq 'aerolink'
if ($production -and (-not $AllowProductionRestore -or $Confirmation -ne 'RESTORE-AEROLINK')) { throw 'Production restore requires -AllowProductionRestore and -Confirmation RESTORE-AEROLINK.' }
if (-not $production -and $TargetDatabase -notmatch '(restore|validation|test)') { throw 'Isolated restore database names must contain restore, validation, or test.' }
if ($production -and -not $DisposableQualification -and $PostgresPort -ne 54329) { throw 'Production restore is restricted to the configured AeroLink PostgreSQL port 54329.' }
if ($DisposableQualification -and $PostgresPort -eq 54329) { throw 'Disposable restore qualification is forbidden on the persistent AeroLink PostgreSQL port 54329.' }

function Invoke-Fault([string]$Phase) { if ($FaultInjection -eq $Phase) { throw "Injected restore fault at $Phase." } }
function Invoke-Psql([string]$Database, [string]$Sql) {
    $output = & (Join-Path $bin 'psql.exe') -h 127.0.0.1 -p $PostgresPort -U postgres -d $Database -v ON_ERROR_STOP=1 -tA -c $Sql
    if ($LASTEXITCODE -ne 0) { throw "PostgreSQL command failed against database '$Database'." }
    return $output
}
function Remove-Database([string]$Database) {
    [void](Invoke-Psql 'postgres' "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '$Database' AND pid <> pg_backend_pid();")
    & (Join-Path $bin 'dropdb.exe') -h 127.0.0.1 -p $PostgresPort -U postgres --if-exists $Database
    if ($LASTEXITCODE -ne 0) { throw "Could not remove disposable database '$Database'." }
}
function New-RestoredDatabase([string]$Database, [string]$Dump) {
    Remove-Database $Database
    & (Join-Path $bin 'createdb.exe') -h 127.0.0.1 -p $PostgresPort -U postgres $Database
    if ($LASTEXITCODE -ne 0) { throw "Could not create restore database '$Database'." }
    & (Join-Path $bin 'pg_restore.exe') -h 127.0.0.1 -p $PostgresPort -U postgres -d $Database --no-owner $Dump
    if ($LASTEXITCODE -ne 0) { throw 'pg_restore did not complete successfully.' }
}
function Rename-Database([string]$From, [string]$To) {
    [void](Invoke-Psql 'postgres' "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '$From' AND pid <> pg_backend_pid();")
    [void](Invoke-Psql 'postgres' "ALTER DATABASE `"$From`" RENAME TO `"$To`";")
}
function Stop-AeroLinkApplicationProcesses {
    foreach ($port in 5173,5080) {
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
function Test-RestoredApi([string]$Database, [string]$Root, [object[]]$Inventory, [int]$Port) {
    $managed = @($Inventory | Where-Object { [string]$_.ArtifactType -eq 'ManagedDocument' })
    if ($managed.Count -eq 0) { return [pscustomobject]@{ Passed=$true; ManagedDocumentDownloads=0; DownloadedBytes=0 } }
    return & (Join-Path $PSScriptRoot 'Test-AeroLinkRestoredDownloads.ps1') -Database $Database -EvidenceRoot $Root `
        -AttachmentInventory $Inventory -PostgresPort $PostgresPort -ApiPort $Port -LogRoot (Join-Path $temporary 'validation-logs')
}

& (Join-Path $PSScriptRoot 'Verify-AeroLinkBackup.ps1') -BackupArchive $BackupArchive | Out-Host
if ($production -and -not $DisposableQualification) {
    # A recoverable point is captured while the original database and evidence set are still active.
    & (Join-Path $PSScriptRoot 'Backup-AeroLink.ps1') -RetentionDays 30
    & (Join-Path $PSScriptRoot 'Stop-AeroLink.ps1')
}
if ($PostgresPort -eq 54329) { & (Join-Path $PSScriptRoot 'Start-Postgres.ps1') }

if (-not $PostgresBin) { $PostgresBin = Join-Path $productRoot '.local\postgresql\pgsql\bin' }
$bin = [IO.Path]::GetFullPath($PostgresBin)
$archive = (Resolve-Path -LiteralPath $BackupArchive).Path
$restoreRoot = Join-Path $productRoot '.local\restore-work'; New-Item -ItemType Directory -Path $restoreRoot -Force | Out-Null
$temporary = Join-Path $restoreRoot ([Guid]::NewGuid().ToString('N')); New-Item -ItemType Directory -Path $temporary | Out-Null
$token = [Guid]::NewGuid().ToString('N').Substring(0, 12)
$restoreDatabase = if ($production) { "aerolink_restore_stage_$token" } else { $TargetDatabase }
$oldDatabase = "aerolink_pre_restore_$token"
$failedDatabase = "aerolink_failed_restore_$token"
$resolvedTarget = $null; $incoming = $null; $retained = $null
$originalDatabaseRenamed = $false; $databaseActivated = $false; $evidenceActivated = $false; $activationPassed = $false

try {
    Expand-Archive -LiteralPath $archive -DestinationPath $temporary
    $manifest = ConvertFrom-Json -InputObject (Get-Content -LiteralPath (Join-Path $temporary 'manifest.json') -Raw)
    $archiveInventoryObject = ConvertFrom-Json -InputObject (Get-Content -LiteralPath (Join-Path $temporary ([string]$manifest.AttachmentInventory)) -Raw)
    $archiveInventory = @($archiveInventoryObject | ForEach-Object { $_ })
    $dump = Join-Path $temporary ([string]$manifest.Database.Dump)
    Invoke-Fault 'BeforeDatabaseRestore'
    New-RestoredDatabase $restoreDatabase $dump
    Invoke-Fault 'AfterDatabaseRestore'

    $restoredInventory = @(Get-AeroLinkAttachmentInventory -Psql (Join-Path $bin 'psql.exe') -Database $restoreDatabase -Port $PostgresPort)
    if ((ConvertTo-Json -InputObject @($archiveInventory) -Depth 4 -Compress) -ne (ConvertTo-Json -InputObject @($restoredInventory) -Depth 4 -Compress)) { throw 'The restored database attachment inventory does not match the signed backup inventory.' }
    Assert-AeroLinkStorageLifecycleHealthy -Psql (Join-Path $bin 'psql.exe') -Database $restoreDatabase -Port $PostgresPort
    $evidenceSource = Join-Path $temporary ([string]$manifest.Storage.ArchiveRoot)
    if ($restoredInventory.Count -gt 0 -and -not (Test-Path -LiteralPath $evidenceSource)) { throw 'The archive contains attachment rows but no evidence directory.' }
    [void](Test-AeroLinkAttachmentInventory -Inventory $restoredInventory -EvidenceRoot $evidenceSource)

    if (-not $EvidenceTarget) { $EvidenceTarget = if ($production) { Get-AeroLinkEvidenceRoot -ProductRoot $productRoot } else { Join-Path $productRoot ".local\restore-validation\$TargetDatabase\evidence" } }
    $resolvedTarget = [IO.Path]::GetFullPath($EvidenceTarget)
    $validationRoot = [IO.Path]::GetFullPath((Join-Path $productRoot '.local\restore-validation')) + [IO.Path]::DirectorySeparatorChar
    if (-not $production -and -not ($resolvedTarget + [IO.Path]::DirectorySeparatorChar).StartsWith($validationRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'The isolated evidence target must remain under product\.local\restore-validation.' }
    $parent = Split-Path $resolvedTarget -Parent; New-Item -ItemType Directory -Path $parent -Force | Out-Null
    $incoming = Join-Path $parent ('.restore-incoming-' + $token)
    if (Test-Path -LiteralPath $incoming) { Remove-Item -LiteralPath $incoming -Recurse -Force }
    New-Item -ItemType Directory -Path $incoming -Force | Out-Null
    if (Test-Path -LiteralPath $evidenceSource) { Copy-AeroLinkEvidenceTree -Source $evidenceSource -Destination $incoming }
    [void](Test-AeroLinkAttachmentInventory -Inventory $restoredInventory -EvidenceRoot $incoming)
    Invoke-Fault 'AfterEvidenceCopy'
    $preActivationDownloads = Test-RestoredApi $restoreDatabase $incoming $restoredInventory $ValidationApiPort
    Invoke-Fault 'AfterPreActivationValidation'

    if (-not $production) {
        if (Test-Path -LiteralPath $resolvedTarget) { Remove-Item -LiteralPath $resolvedTarget -Recurse -Force }
        Move-Item -LiteralPath $incoming -Destination $resolvedTarget; $incoming = $null
        [void](Test-AeroLinkAttachmentInventory -Inventory $restoredInventory -EvidenceRoot $resolvedTarget)
        $finalDownloads = Test-RestoredApi $restoreDatabase $resolvedTarget $restoredInventory ($ValidationApiPort + 1)
        $activationPassed = $true
    }
    else {
        Rename-Database 'aerolink' $oldDatabase; $originalDatabaseRenamed = $true
        Invoke-Fault 'AfterOriginalDatabaseRename'
        Rename-Database $restoreDatabase 'aerolink'; $databaseActivated = $true
        Invoke-Fault 'AfterDatabaseActivation'
        if (Test-Path -LiteralPath $resolvedTarget) { $retained = Join-Path $parent ("evidence-pre-restore-$token"); Move-Item -LiteralPath $resolvedTarget -Destination $retained }
        Move-Item -LiteralPath $incoming -Destination $resolvedTarget; $incoming = $null; $evidenceActivated = $true
        Invoke-Fault 'AfterEvidenceActivation'
        $activatedInventory = @(Get-AeroLinkAttachmentInventory -Psql (Join-Path $bin 'psql.exe') -Database 'aerolink' -Port $PostgresPort)
        [void](Test-AeroLinkAttachmentInventory -Inventory $activatedInventory -EvidenceRoot $resolvedTarget)
        Assert-AeroLinkStorageLifecycleHealthy -Psql (Join-Path $bin 'psql.exe') -Database 'aerolink' -Port $PostgresPort
        $finalDownloads = Test-RestoredApi 'aerolink' $resolvedTarget $activatedInventory ($ValidationApiPort + 1)
        Invoke-Fault 'AfterActivationValidation'
        Invoke-Fault 'BeforeRestart'
        if (-not $DisposableQualification) { & (Join-Path $PSScriptRoot 'Start-AeroLink.ps1') -DoNotOpenBrowser }
        Invoke-Fault 'AfterRestart'
        $activationPassed = $true
    }

    $count = (Invoke-Psql $TargetDatabase 'SELECT COUNT(*) FROM programs;').Trim()
    $verified = Test-AeroLinkAttachmentInventory -Inventory $restoredInventory -EvidenceRoot $resolvedTarget
    Write-Host "Restore verified in database '$TargetDatabase': $count Program record(s), $($verified.ReferencedAttachments) attachment row(s), $($verified.ReferencedObjects) object(s), $($verified.VerifiedBytes) byte(s), $($finalDownloads.ManagedDocumentDownloads) API download(s). Evidence root: $resolvedTarget" -ForegroundColor Green
    if ($production) { Write-Host "Rollback retained as database '$oldDatabase'$(if($retained){" and evidence '$retained'"})." -ForegroundColor Yellow }
}
catch {
    $failure = $_
    $originalPairAvailable = $production -and -not $originalDatabaseRenamed
    if ($production -and $originalDatabaseRenamed -and -not $activationPassed) {
        try {
            # PostgreSQL must remain available for the compensating database renames.
            Stop-AeroLinkApplicationProcesses
            if ($evidenceActivated -and (Test-Path -LiteralPath $resolvedTarget)) { Move-Item -LiteralPath $resolvedTarget -Destination (Join-Path (Split-Path $resolvedTarget -Parent) ("evidence-failed-restore-$token")) }
            if ($retained -and (Test-Path -LiteralPath $retained)) { Move-Item -LiteralPath $retained -Destination $resolvedTarget }
            if ($databaseActivated) { Rename-Database 'aerolink' $failedDatabase }
            Rename-Database $oldDatabase 'aerolink'
            $originalPairAvailable = $true
        }
        catch { throw "Restore failed: $($failure.Exception.Message). Automatic rollback also failed: $($_.Exception.Message). AeroLink was not restarted." }
    }
    if ($originalPairAvailable -and -not $DisposableQualification) {
        try { & (Join-Path $PSScriptRoot 'Start-AeroLink.ps1') -DoNotOpenBrowser }
        catch { throw "Restore failed: $($failure.Exception.Message). The original database/evidence pair was retained but AeroLink restart failed: $($_.Exception.Message)." }
    }
    throw $failure
}
finally {
    if ($incoming -and (Test-Path -LiteralPath $incoming)) { Remove-Item -LiteralPath $incoming -Recurse -Force }
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Recurse -Force }
    if ($production -and -not $databaseActivated) {
        try { Remove-Database $restoreDatabase } catch { }
    }
}
