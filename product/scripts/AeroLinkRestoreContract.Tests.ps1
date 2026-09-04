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
if(-not $download.Contains('$apiExecutable') -or -not $download.Contains('remained in use after process cleanup')){throw 'Restore validation does not launch the built API directly and prove its port is released.'}
# The build to validate with is named by the caller and never chosen here. Preferring whichever configuration
# had output on disk let an established installation validate an upgraded clone with a stale Release binary
# from its previous production run; a binary predating the read-only boundary would ignore these settings and
# start the ordinary mutating host, with its outbound workers, over copied production data.
if(-not $download.Contains('[Parameter(Mandatory)][string]$ApiExecutable')){throw 'Restore validation still selects its own API build instead of requiring the caller to name the current one.'}
if($download.Contains('bin\Release') -or $download.Contains('bin\Debug')){throw 'Restore validation must not know about build configurations; the caller names the executable.'}
if(-not $restore.Contains('-ApiExecutable')){throw 'Restore does not name the build it validates with.'}
# Authentication endpoint availability, proved before the real database is mutated. The read-only middleware
# short-circuits every non-health route BEFORE endpoint routing, so an absent /api/auth/login answered 403
# exactly as a present one did - the 403 proves the boundary, not the route. /health/routes reads the built
# EndpointDataSource: it reaches routing, invokes nothing, and fails if this build has lost the auth routes.
if(-not $program.Contains('/health/routes')){throw 'The build does not expose a read-only route-presence proof, so authentication endpoint availability cannot be established inside the validation boundary.'}
foreach($route in @('/api/auth/login','/api/auth/me','/api/auth/logout')){
    if(-not $download.Contains($route)){throw "Isolated validation does not require the authentication route $route to be present."}
}
if(-not $download.Contains('does not declare the required authentication routes')){throw 'Isolated validation does not fail when the required authentication routes are missing.'}
if($download.IndexOf('finally {', $download.IndexOf('finally {') + 1) -lt 0 -or -not $download.Contains('Production rollback/restart must never inherit')){throw 'Restore validation does not restore its parent environment in a nested cleanup finally.'}
[pscustomobject]@{Passed=$true;ShadowDatabase=$true;ReversibleActivation=$true;ReadOnlyApiDownloads=$true;PersistentPortFence=$true}
$global:LASTEXITCODE=0
