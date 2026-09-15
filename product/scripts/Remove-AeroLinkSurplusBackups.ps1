[CmdletBinding()]
param([Parameter(Mandatory)][string]$BackupRoot, [ValidateRange(1,15)][int]$RetentionDays=15, [switch]$Apply)
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'AeroLinkBackupRetention.psm1') -Force
$lock=Enter-AeroLinkBackupLock -BackupRoot $BackupRoot
try {
    $plan=@(Get-AeroLinkBackupRetentionPlan -BackupRoot $BackupRoot -RetentionDays $RetentionDays)
    if($Apply){Invoke-AeroLinkBackupRetentionPlan -BackupRoot $BackupRoot -Plan $plan}
    $plan | Select-Object Path,Database,Created,Bytes,Action,Reason
} finally {$lock.Dispose()}
