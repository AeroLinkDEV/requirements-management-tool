[CmdletBinding()]
param(
    [ValidateRange(1, 15)]
    [int]$RetentionDays = 15
)

$ErrorActionPreference = 'Stop'
$productRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Import-Module (Join-Path $PSScriptRoot 'AeroLinkInstallation.psm1') -Force
$installation = Get-AeroLinkInstallationPaths -ProductRoot $productRoot
$logRoot = $installation.Logs
$backupRoot = $installation.Backups
$logPath = Join-Path $logRoot 'scheduled-backup.log'
$backupScript = Join-Path $PSScriptRoot 'Backup-AeroLink.ps1'
$verifyScript = Join-Path $PSScriptRoot 'Verify-AeroLinkBackup.ps1'

New-Item -ItemType Directory -Path $logRoot -Force | Out-Null
$startedAt = Get-Date
Add-Content -LiteralPath $logPath -Encoding UTF8 -Value (
    "[{0}] Scheduled backup started (retention: {1} days)." -f $startedAt.ToString('o'), $RetentionDays)

try {
    $backupOutput = & $backupScript -RetentionDays $RetentionDays 2>&1
    if ($backupOutput) { $backupOutput | Out-String | Add-Content -LiteralPath $logPath -Encoding UTF8 }

    $capture = @($backupOutput | Where-Object { $_ -is [pscustomobject] -and $_.PSObject.Properties['Archive'] }) | Select-Object -Last 1
    $archive = if ($capture) { Get-Item -LiteralPath $capture.Archive } else { $null }
    if (-not $archive -or $archive.LastWriteTime -lt $startedAt.AddMinutes(-1)) {
        throw 'The scheduled backup did not produce a new AeroLink archive.'
    }

    $verification = & $verifyScript -BackupArchive $archive.FullName
    $verification | Format-List | Out-String | Add-Content -LiteralPath $logPath -Encoding UTF8
    Add-Content -LiteralPath $logPath -Encoding UTF8 -Value (
        "[{0}] Scheduled backup verified successfully: {1}" -f (Get-Date).ToString('o'), $archive.FullName)
    $verification
}
catch {
    Add-Content -LiteralPath $logPath -Encoding UTF8 -Value (
        "[{0}] Scheduled backup failed: {1}" -f (Get-Date).ToString('o'), $_.Exception.Message)
    throw
}

