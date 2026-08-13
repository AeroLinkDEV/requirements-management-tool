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
if($restore.Contains("if (-not `$DisposableQualification) { & (Join-Path `$PSScriptRoot 'Stop-AeroLink.ps1') }")){throw 'Rollback still stops PostgreSQL before its compensating database renames.'}
if(-not $restore.Contains('Stop-AeroLinkApplicationProcesses')){throw 'Rollback does not stop the application processes independently of PostgreSQL.'}
if(-not $restore.Contains('if ($command -notlike "*$productRoot*") { continue }')){throw 'Rollback does not preserve unrelated listeners while recovering the database pair.'}
if(-not $restore.Contains("Disposable restore qualification is forbidden on the persistent AeroLink PostgreSQL port 54329.")){throw 'Disposable restore qualification is not fenced from the persistent database.'}
if(-not $download.Contains('X-AeroLink-Restore-Validation') -or -not $program.Contains('restore_validation_read_only') -or -not $program.Contains('typeof(IHostedService)')){throw 'The isolated API-download validation token/read-only boundary is incomplete.'}
if($download.Contains("Start-Process -FilePath 'dotnet'")){throw 'Restore validation still tracks a dotnet-run parent instead of the API listener process.'}
if(-not $download.Contains('AeroLink.Api.exe') -or -not $download.Contains('remained in use after process cleanup')){throw 'Restore validation does not launch the built API directly and prove its port is released.'}
if($download.IndexOf('finally {', $download.IndexOf('finally {') + 1) -lt 0 -or -not $download.Contains('Production rollback/restart must never inherit')){throw 'Restore validation does not restore its parent environment in a nested cleanup finally.'}
[pscustomobject]@{Passed=$true;ShadowDatabase=$true;ReversibleActivation=$true;ReadOnlyApiDownloads=$true;PersistentPortFence=$true}
$global:LASTEXITCODE=0
