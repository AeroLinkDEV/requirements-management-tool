[CmdletBinding()]
param(
 [Parameter(Mandatory)][string]$BackupArchive,
 [string]$TargetDatabase='aerolink_restore_validation',
 [string]$EvidenceTarget,
 [switch]$AllowProductionRestore,
 [string]$Confirmation
)
$ErrorActionPreference='Stop'
$productRoot=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Import-Module (Join-Path $PSScriptRoot 'AeroLinkEvidenceStore.psm1') -Force
if($TargetDatabase -notmatch '^[a-zA-Z][a-zA-Z0-9_]{0,62}$'){throw 'The target database name is unsafe.'}
$production=$TargetDatabase -eq 'aerolink'
if($production -and (-not $AllowProductionRestore -or $Confirmation -ne 'RESTORE-AEROLINK')){throw 'Production restore requires -AllowProductionRestore and -Confirmation RESTORE-AEROLINK.'}
if(-not $production -and $TargetDatabase -notmatch '(restore|validation|test)'){throw 'Isolated restore database names must contain restore, validation, or test.'}
& (Join-Path $PSScriptRoot 'Verify-AeroLinkBackup.ps1') -BackupArchive $BackupArchive|Out-Host
if($production){& (Join-Path $PSScriptRoot 'Backup-AeroLink.ps1') -RetentionDays 30;& (Join-Path $PSScriptRoot 'Stop-AeroLink.ps1')}
& (Join-Path $PSScriptRoot 'Start-Postgres.ps1')
$bin=Join-Path $productRoot '.local\postgresql\pgsql\bin';$archive=(Resolve-Path -LiteralPath $BackupArchive).Path
$restoreRoot=Join-Path $productRoot '.local\restore-work';New-Item -ItemType Directory -Path $restoreRoot -Force|Out-Null;$temporary=Join-Path $restoreRoot ([Guid]::NewGuid().ToString('N'));New-Item -ItemType Directory -Path $temporary|Out-Null
try{
 Expand-Archive -LiteralPath $archive -DestinationPath $temporary
 $manifest=ConvertFrom-Json -InputObject (Get-Content -LiteralPath (Join-Path $temporary 'manifest.json') -Raw)
 $archiveInventoryObject=ConvertFrom-Json -InputObject (Get-Content -LiteralPath (Join-Path $temporary ([string]$manifest.AttachmentInventory)) -Raw);$archiveInventory=@($archiveInventoryObject|ForEach-Object{$_})
 $exists=& (Join-Path $bin 'psql.exe') -h 127.0.0.1 -p 54329 -U postgres -d postgres -tAc "SELECT 1 FROM pg_database WHERE datname='$TargetDatabase'"
 if($exists -eq '1'){
  if($production){& (Join-Path $bin 'pg_restore.exe') -h 127.0.0.1 -p 54329 -U postgres -d $TargetDatabase --clean --if-exists --no-owner (Join-Path $temporary 'aerolink-postgresql.dump')}
  else{& (Join-Path $bin 'dropdb.exe') -h 127.0.0.1 -p 54329 -U postgres --if-exists $TargetDatabase;& (Join-Path $bin 'createdb.exe') -h 127.0.0.1 -p 54329 -U postgres $TargetDatabase;& (Join-Path $bin 'pg_restore.exe') -h 127.0.0.1 -p 54329 -U postgres -d $TargetDatabase --no-owner (Join-Path $temporary 'aerolink-postgresql.dump')}
 }else{& (Join-Path $bin 'createdb.exe') -h 127.0.0.1 -p 54329 -U postgres $TargetDatabase;& (Join-Path $bin 'pg_restore.exe') -h 127.0.0.1 -p 54329 -U postgres -d $TargetDatabase --no-owner (Join-Path $temporary 'aerolink-postgresql.dump')}
 if($LASTEXITCODE -ne 0){throw 'pg_restore did not complete successfully.'}
 $count=& (Join-Path $bin 'psql.exe') -h 127.0.0.1 -p 54329 -U postgres -d $TargetDatabase -tAc 'SELECT COUNT(*) FROM "Programs"' 2>$null
 if($LASTEXITCODE -ne 0){$count=& (Join-Path $bin 'psql.exe') -h 127.0.0.1 -p 54329 -U postgres -d $TargetDatabase -tAc 'SELECT COUNT(*) FROM programs'}
 if(-not $EvidenceTarget){$EvidenceTarget=if($production){Get-AeroLinkEvidenceRoot -ProductRoot $productRoot}else{Join-Path $productRoot ".local\restore-validation\$TargetDatabase\evidence"}}
 $evidenceSource=Join-Path $temporary 'evidence'
 $restoredInventory=@(Get-AeroLinkAttachmentInventory -Psql (Join-Path $bin 'psql.exe') -Database $TargetDatabase)
 if((ConvertTo-Json -InputObject @($archiveInventory) -Depth 4 -Compress) -ne (ConvertTo-Json -InputObject @($restoredInventory) -Depth 4 -Compress)){throw 'The restored database attachment inventory does not match the signed backup inventory.'}
 Assert-AeroLinkStorageLifecycleHealthy -Psql (Join-Path $bin 'psql.exe') -Database $TargetDatabase
 if($restoredInventory.Count -gt 0 -and -not(Test-Path -LiteralPath $evidenceSource)){throw 'The archive contains attachment rows but no evidence directory.'}
 $sourceValidation=Test-AeroLinkAttachmentInventory -Inventory $restoredInventory -EvidenceRoot $evidenceSource
 if(Test-Path -LiteralPath $evidenceSource){
  $resolvedTarget=[IO.Path]::GetFullPath($EvidenceTarget)
  $validationRoot=[IO.Path]::GetFullPath((Join-Path $productRoot '.local\restore-validation'))
  if(-not $production -and -not $resolvedTarget.StartsWith($validationRoot,[StringComparison]::OrdinalIgnoreCase)){throw 'The isolated evidence target must remain under product\.local\restore-validation.'}
  if($production){
   $parent=Split-Path $resolvedTarget -Parent;New-Item -ItemType Directory -Path $parent -Force|Out-Null
   $incoming=Join-Path $parent ('.restore-incoming-'+[Guid]::NewGuid().ToString('N'));Copy-Item -LiteralPath $evidenceSource -Destination $incoming -Recurse -Force
   if(Test-Path -LiteralPath $resolvedTarget){$retained=Join-Path $parent ('evidence-pre-restore-'+(Get-Date -Format 'yyyyMMdd-HHmmss'));Move-Item -LiteralPath $resolvedTarget -Destination $retained}
   Move-Item -LiteralPath $incoming -Destination $resolvedTarget
  }else{
   if(Test-Path -LiteralPath $resolvedTarget){Remove-Item -LiteralPath $resolvedTarget -Recurse -Force}
   New-Item -ItemType Directory -Path $resolvedTarget -Force|Out-Null;Copy-Item -Path (Join-Path $evidenceSource '*') -Destination $resolvedTarget -Recurse -Force
  }
 }
 $targetValidation=Test-AeroLinkAttachmentInventory -Inventory $restoredInventory -EvidenceRoot ([IO.Path]::GetFullPath($EvidenceTarget))
 Write-Host "Restore verified in database '$TargetDatabase': $count Program record(s), $($targetValidation.ReferencedAttachments) attachment row(s), $($targetValidation.ReferencedObjects) object(s), $($targetValidation.VerifiedBytes) byte(s). Evidence root: $([IO.Path]::GetFullPath($EvidenceTarget))" -ForegroundColor Green
}finally{if(Test-Path -LiteralPath $temporary){Remove-Item -LiteralPath $temporary -Recurse -Force}}
if($production){& (Join-Path $PSScriptRoot 'Start-AeroLink.ps1') -DoNotOpenBrowser}
