[CmdletBinding()]
param([Parameter(Mandatory)][string]$BackupRoot, [ValidateRange(1,15)][int]$RetentionDays=15, [switch]$Apply,
    [string]$InstallationRoot)
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'AeroLinkBackupRetention.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'AeroLinkInstallation.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'AeroLinkTransition.psm1') -Force
$productRoot=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$backupPath=[IO.Path]::GetFullPath($BackupRoot).TrimEnd('\','/')
$association=$null
if ($PSBoundParameters.ContainsKey('InstallationRoot')) {
    if ([string]::IsNullOrWhiteSpace($InstallationRoot) -or $InstallationRoot -notmatch '^(?:[A-Za-z]:[\\/]|[\\/]{2}[^\\/]+[\\/][^\\/]+(?:[\\/]|$))' -or
        -not (Test-Path -LiteralPath $InstallationRoot -PathType Container)) {
        throw 'An explicit InstallationRoot must name an existing absolute installation directory.'
    }
    $association=Get-AeroLinkInstallationPaths -ProductRoot $productRoot -InstallationRoot $InstallationRoot
} else {
    $installation=$null
    try { $installation=Get-AeroLinkInstallationPaths -ProductRoot $productRoot }
    catch {
        $associationError=$_
        # A failed lookup is not a successful nonmatch. Known intended canonical paths still
        # fail closed; unrelated archive maintenance remains available with an honest boundary.
        $intendedRoots=@((Join-Path $productRoot '.local'))
        if ($env:AEROLINK_INSTALLATION_ROOT -and [IO.Path]::IsPathRooted($env:AEROLINK_INSTALLATION_ROOT)) {
            $intendedRoots+=$env:AEROLINK_INSTALLATION_ROOT
        }
        $pointerPath=Join-Path $productRoot '.local\installation.json'
        if (Test-Path -LiteralPath $pointerPath -PathType Leaf) {
            try {
                $pointer=Get-Content -LiteralPath $pointerPath -Raw | ConvertFrom-Json
                if ($pointer.installationRoot -and [IO.Path]::IsPathRooted($pointer.installationRoot)) { $intendedRoots+=$pointer.installationRoot }
            } catch { }
        }
        foreach ($intendedRoot in $intendedRoots) {
            try { $intendedBackups=[IO.Path]::GetFullPath((Join-Path $intendedRoot 'backups')).TrimEnd('\','/') }
            catch { continue }
            if ([string]::Equals($backupPath,$intendedBackups,[StringComparison]::OrdinalIgnoreCase)) { throw $associationError }
        }
        Write-Warning 'Installation association could not be resolved; this unrelated archive root uses only its backup-root lock.' -WarningAction Continue
    }
    if ($installation -and [string]::Equals($backupPath,[IO.Path]::GetFullPath($installation.Backups).TrimEnd('\','/'),[StringComparison]::OrdinalIgnoreCase)) {
        $association=$installation
    }
}
# Do not create a lease through a linked archive/installation ancestor. The root lock retains
# its own complete path checks too; this check precedes the new installation-side operation.
foreach ($path in @($backupPath, $(if ($association) { $association.InstallationRoot }))) {
    $cursor=$path
    while ($cursor) {
        if ((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Backup retention refuses a reparse point: $cursor"
        }
        $parent=[IO.Path]::GetDirectoryName($cursor)
        if ($parent -eq $cursor) { break }
        $cursor=$parent
    }
}
$transitionLease=$null
$lock=$null
$operationError=$null
try {
    if ($association) {
        $transitionLease=Enter-AeroLinkTransition -InstallationRoot $association.InstallationRoot -Policy Preserve
        if ($transitionLease.Pending) { throw 'An interrupted production transition requires recovery before installation backup retention.' }
    }
    $lock=Enter-AeroLinkBackupLock -BackupRoot $BackupRoot
    $plan=@(Get-AeroLinkBackupRetentionPlan -BackupRoot $BackupRoot -RetentionDays $RetentionDays)
    if($Apply){Invoke-AeroLinkBackupRetentionPlan -BackupRoot $BackupRoot -Plan $plan}
    $plan | Select-Object Path,Database,Created,Bytes,Action,Reason
} catch {
    $operationError=$_
    throw
} finally {
    try {
        if ($lock) { $lock.Dispose() }
    } catch {
        if ($operationError) { Write-Warning "Backup-root lock cleanup also failed: $($_.Exception.Message)" -WarningAction Continue }
        else { $operationError=$_; throw }
    } finally {
        try { Exit-AeroLinkTransition -Lease $transitionLease }
        catch {
            if ($operationError) { Write-Warning "Installation lease cleanup also failed: $($_.Exception.Message)" -WarningAction Continue }
            else { throw }
        }
    }
}
