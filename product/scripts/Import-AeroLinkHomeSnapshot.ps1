#Requires -Version 5.1
<#
    Explicit, one-way HOME -> work-laptop snapshot refresh.

    This is not replication. There is no bidirectional sync, no merge of two PostgreSQL histories, and no
    step in ordinary startup that reaches for it: START_AEROLINK.bat must never silently replace the laptop's
    database with HOME's, and HOME being unreachable must never stop the laptop launching. The operator asks
    for this, or it does not happen.

    The transport is deliberately a seam rather than a service. A supported AeroLink backup archive is
    produced on HOME by BACKUP_AEROLINK.bat and carried to the laptop by whatever means the operator already
    trusts for controlled data - a drive, a corporate file share, the protected remote-demo download. Building
    a public snapshot endpoint to make this "complete" would introduce a far larger security design than the
    problem justifies, and #881 says so.

    The order below is the safety property, and every step is the supported script that already owns it:

        verify the incoming archive          Verify-AeroLinkBackup.ps1 (hash, manifest, evidence inventory)
        back up the laptop as it is now      Backup-AeroLink.ps1       (the recoverable point, taken first)
        restore incoming to a staging copy   Restore-AeroLink.ps1      (isolated database and evidence)
        upgrade staging with THIS build      maintenance upgrade       (the same authorities startup runs)
        prove staging is then current        maintenance analyze
        activate, explicitly                 Restore-AeroLink.ps1      (its own rollback contract)

    Anything that fails before activation leaves the laptop database exactly as it was, because nothing has
    touched it yet.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SnapshotArchive,
    [ValidateSet('Preview', 'Import')][string]$Action = 'Preview',
    # Activation replaces the laptop database. It is spelled out rather than implied by -Import.
    [string]$Confirmation,
    [string]$SnapshotSourceLabel = 'HOME CANONICAL',
    [int]$PostgresPort = 54329
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'AeroLinkInstallation.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'AeroLinkUpgrade.psm1') -Force

$productRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$installation = Get-AeroLinkInstallationPaths -ProductRoot $productRoot
$instance = Get-AeroLinkInstanceConfig -ProductRoot $productRoot -Mode Development

# The one direction that is supported. Restoring a snapshot onto the canonical HOME installation would
# overwrite the very database every snapshot is taken from.
if ($instance.Classification -eq 'HomeCanonical') {
    throw 'This installation is declared HOME CANONICAL. A HOME snapshot refresh is one-way onto a work-laptop installation and will not be applied here.'
}

if (-not (Test-Path -LiteralPath $SnapshotArchive -PathType Leaf)) {
    throw "The snapshot archive was not found: $SnapshotArchive"
}
$SnapshotArchive = (Resolve-Path -LiteralPath $SnapshotArchive).Path

Write-Host 'AeroLink - refresh this laptop from a HOME snapshot' -ForegroundColor Cyan
Write-Host ''
Write-Host "Snapshot archive:  $SnapshotArchive"
Write-Host "This installation: $($instance.Label) ($($instance.Classification))"
Write-Host "Local database:    $($installation.PostgresData)"
Write-Host ''
Write-Host 'Activation REPLACES this laptop''s AeroLink database and evidence with the snapshot.' -ForegroundColor Yellow
Write-Host 'Records created on this laptop that are not in the snapshot will not survive it.' -ForegroundColor Yellow
Write-Host 'The current laptop state is backed up first, and the snapshot is validated before anything' -ForegroundColor Yellow
Write-Host 'is replaced.' -ForegroundColor Yellow
Write-Host ''

Write-Host '[1/6] Verifying the incoming snapshot...' -ForegroundColor Cyan
$verification = & (Join-Path $PSScriptRoot 'Verify-AeroLinkBackup.ps1') -BackupArchive $SnapshotArchive
Write-Host "      Archive verified: $($verification.ReferencedAttachments) attachment(s), $($verification.ReferencedObjects) evidence object(s)." -ForegroundColor Green

$manifest = $null
$temporary = Join-Path $installation.RestoreWork ('snapshot-manifest-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporary -Force | Out-Null
try {
    Import-Module (Join-Path $PSScriptRoot 'AeroLinkBackupArchive.psm1') -Force
    Expand-AeroLinkBackupArchive -ArchivePath $SnapshotArchive -DestinationDirectory $temporary
    $manifest = Get-Content -LiteralPath (Join-Path $temporary 'manifest.json') -Raw | ConvertFrom-Json
}
finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Recurse -Force } }
Write-Host "      Snapshot source revision: $($manifest.Application.SourceSha)"
Write-Host "      Snapshot created:         $($manifest.CreatedAtUtc)"

if ($Action -eq 'Preview') {
    Write-Host ''
    Write-Host 'Preview only. Nothing was backed up, restored, upgraded, or replaced.' -ForegroundColor Green
    Write-Host 'To apply it:' -ForegroundColor DarkGray
    Write-Host '      REFRESH_AEROLINK_FROM_HOME.bat "<archive>" Import REFRESH-FROM-HOME' -ForegroundColor Gray
    exit 0
}
if ($Confirmation -ne 'REFRESH-FROM-HOME') {
    throw 'Activation requires -Confirmation REFRESH-FROM-HOME. Nothing was changed.'
}

Write-Host '[2/6] Backing up this laptop as it is now...' -ForegroundColor Cyan
& (Join-Path $PSScriptRoot 'Backup-AeroLink.ps1') -PostgresAlreadyRunning | Out-Host
$laptopBackup = (Get-ChildItem -LiteralPath $installation.Backups -Filter 'aerolink-*.zip' -File |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
if (-not $laptopBackup) { throw 'The pre-refresh backup of this laptop did not produce an archive. Nothing was changed.' }
Write-Host "      Laptop backup retained: $laptopBackup" -ForegroundColor Green

$token = [Guid]::NewGuid().ToString('N').Substring(0, 10)
$stagingDatabase = "aerolink_snapshot_validation_$token"
$stagingConnection = "Host=127.0.0.1;Port=$PostgresPort;Database=$stagingDatabase;Username=postgres"
try {
    Write-Host '[3/6] Restoring the snapshot to an isolated staging copy...' -ForegroundColor Cyan
    & (Join-Path $PSScriptRoot 'Restore-AeroLink.ps1') -BackupArchive $SnapshotArchive -TargetDatabase $stagingDatabase -PostgresPort $PostgresPort | Out-Host

    Write-Host '[4/6] Upgrading the staging copy with this build...' -ForegroundColor Cyan
    $stagingUpgrade = Invoke-AeroLinkMaintenanceCommand -ProductRoot $productRoot -Arguments @('maintenance', 'upgrade', '--apply') -ConnectionString $stagingConnection
    if ($stagingUpgrade.ExitCode -ne 0) {
        throw "The snapshot could not be upgraded by this build, so it was NOT activated and this laptop's database is unchanged. $($stagingUpgrade.StdOut)"
    }

    Write-Host '[5/6] Proving the staging copy is current...' -ForegroundColor Cyan
    $stagingAnalysis = Get-AeroLinkUpgradeAnalysis -ProductRoot $productRoot -ConnectionString $stagingConnection
    if ($stagingAnalysis.Status -ne 'current') {
        if ($stagingAnalysis.Analysis -and @($stagingAnalysis.Analysis.conflicts).Count -gt 0) {
            Write-AeroLinkUpgradeConflictReport -Analysis $stagingAnalysis.Analysis
        }
        throw "The staged snapshot is not current under this build ($($stagingAnalysis.Status)), so it was NOT activated and this laptop's database is unchanged."
    }
    Write-Host '      Staging copy validated.' -ForegroundColor Green
}
finally {
    try { Remove-AeroLinkSnapshotStagingDatabase -ProductRoot $productRoot -Database $stagingDatabase -PostgresPort $PostgresPort }
    catch { Write-Host "      The staging copy could not be removed: $($_.Exception.Message)" -ForegroundColor Yellow }
}

Write-Host '[6/6] Activating the snapshot on this laptop...' -ForegroundColor Cyan
# Activation goes through the supported production restore, which stops AeroLink, keeps the previous
# database under a retained name, validates the activated pair, and rolls back on failure. Reimplementing
# any of that here would be a second, untested activation path for controlled data.
& (Join-Path $PSScriptRoot 'Restore-AeroLink.ps1') -BackupArchive $SnapshotArchive -TargetDatabase 'aerolink' `
    -PostgresPort $PostgresPort -AllowProductionRestore -Confirmation 'RESTORE-AEROLINK' | Out-Host

Set-AeroLinkInstanceConfig -ProductRoot $productRoot -Snapshot @{
    sourceLabel    = $SnapshotSourceLabel
    sourceSha      = [string]$manifest.Application.SourceSha
    createdAtUtc   = [string]$manifest.CreatedAtUtc
    activatedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
} | Out-Null

Write-Host ''
Write-Host 'The HOME snapshot is active on this laptop.' -ForegroundColor Green
Write-Host "Snapshot source revision: $($manifest.Application.SourceSha)"
Write-Host "Snapshot created:         $($manifest.CreatedAtUtc)"
Write-Host "Previous laptop state:    $laptopBackup"
exit 0
