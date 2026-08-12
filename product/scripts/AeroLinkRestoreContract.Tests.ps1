$ErrorActionPreference = 'Stop'
$restore = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Restore-AeroLink.ps1') -Raw
$download = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Test-AeroLinkRestoredDownloads.ps1') -Raw
$program = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\src\AeroLink.Api\Program.cs') -Raw
foreach ($path in @('Backup-AeroLink.ps1','Restore-AeroLink.ps1','Test-AeroLinkRestoredDownloads.ps1','AeroLinkRestoreQualification.Tests.ps1')) {
    $errors=$null;$tokens=$null
    [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot $path),[ref]$tokens,[ref]$errors)|Out-Null
    if($errors.Count -gt 0){throw "$path has a PowerShell parse error: $($errors[0].Message)"}
}
foreach ($required in @('aerolink_restore_stage_','Rename-Database ''aerolink'' $oldDatabase','AfterEvidenceActivation','Test-RestoredApi ''aerolink''','$activationPassed = $true')) {
    if(-not $restore.Contains($required)){throw "Restore activation contract is missing: $required"}
}
foreach ($rollbackRequired in @('$originalDatabaseRenamed = $true','AfterOriginalDatabaseRename','if ($databaseActivated) { Rename-Database ''aerolink'' $failedDatabase }','Rename-Database $oldDatabase ''aerolink''','SELECT COUNT(*) FROM programs;')) {
    if(-not $restore.Contains($rollbackRequired)){throw "Restore rollback/query contract is missing: $rollbackRequired"}
}
if(-not $restore.Contains("Disposable restore qualification is forbidden on the persistent AeroLink PostgreSQL port 54329.")){throw 'Disposable restore qualification is not fenced from the persistent database.'}
if(-not $download.Contains('X-AeroLink-Restore-Validation') -or -not $program.Contains('restore_validation_read_only') -or -not $program.Contains('typeof(IHostedService)')){throw 'The isolated API-download validation token/read-only boundary is incomplete.'}
[pscustomobject]@{Passed=$true;ShadowDatabase=$true;ReversibleActivation=$true;ReadOnlyApiDownloads=$true;PersistentPortFence=$true}
$global:LASTEXITCODE=0
