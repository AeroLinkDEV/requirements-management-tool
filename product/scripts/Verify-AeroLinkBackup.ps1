[CmdletBinding()]
param([string]$BackupArchive)

$ErrorActionPreference='Stop'
$productRoot=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$backupRoot=Join-Path $productRoot '.local\backups'
if(-not $BackupArchive){$BackupArchive=(Get-ChildItem -LiteralPath $backupRoot -Filter 'aerolink-*.zip' -File | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName}
if(-not $BackupArchive -or -not (Test-Path -LiteralPath $BackupArchive)){throw 'No AeroLink backup archive was found.'}
$archive=(Resolve-Path -LiteralPath $BackupArchive).Path
if([IO.Path]::GetExtension($archive) -ne '.zip'){throw 'The selected backup is not a ZIP archive.'}
$sidecar="$archive.sha256";if(-not(Test-Path -LiteralPath $sidecar)){throw 'The archive SHA-256 sidecar is missing.'}
$expected=((Get-Content -LiteralPath $sidecar -Raw).Trim() -split '\s+')[0].ToLowerInvariant();$actual=(Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
if($expected -ne $actual){throw "Backup archive hash mismatch. Expected $expected; calculated $actual."}
$verificationRoot=Join-Path $productRoot '.local\backup-verification';New-Item -ItemType Directory -Path $verificationRoot -Force|Out-Null
$temporary=Join-Path $verificationRoot ([Guid]::NewGuid().ToString('N'));New-Item -ItemType Directory -Path $temporary|Out-Null
try{
 Add-Type -AssemblyName System.IO.Compression.FileSystem
 $zip=[IO.Compression.ZipFile]::OpenRead($archive)
 try{
  foreach($zipEntry in $zip.Entries){
   $entryPath=[string]$zipEntry.FullName
   if([IO.Path]::IsPathRooted($entryPath)-or $entryPath -split '[\\/]' -contains '..'){throw "Unsafe archive path: $entryPath"}
  }
 }finally{$zip.Dispose()}
 Expand-Archive -LiteralPath $archive -DestinationPath $temporary
 $manifestPath=Join-Path $temporary 'manifest.json';if(-not(Test-Path -LiteralPath $manifestPath)){throw 'The backup manifest is missing.'}
 $manifestObject=ConvertFrom-Json -InputObject (Get-Content -LiteralPath $manifestPath -Raw)
 if([int]$manifestObject.FormatVersion -ne 2){throw 'The backup manifest format is unsupported.'}
 $manifest=@($manifestObject.Files|ForEach-Object{$_});if($manifest.Count -eq 0){throw 'The backup file manifest is empty.'}
 foreach($entry in $manifest){$relative=[string]$entry.Path;if([IO.Path]::IsPathRooted($relative)-or $relative -split '[\\/]' -contains '..'){throw "Unsafe manifest path: $relative"};$candidate=[IO.Path]::GetFullPath((Join-Path $temporary $relative));$tempPrefix=[IO.Path]::GetFullPath($temporary)+[IO.Path]::DirectorySeparatorChar;if(-not $candidate.StartsWith($tempPrefix,[StringComparison]::OrdinalIgnoreCase)){throw "Manifest path escapes the archive: $relative"};if(-not(Test-Path -LiteralPath $candidate -PathType Leaf)){throw "Manifest file is missing: $relative"};if((Get-Item -LiteralPath $candidate).Length -ne [long]$entry.Size){throw "Manifest size mismatch: $relative"};$hash=(Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash.ToLowerInvariant();if($hash -ne ([string]$entry.Sha256).ToLowerInvariant()){throw "Manifest hash mismatch: $relative"}}
 if(-not(Test-Path -LiteralPath (Join-Path $temporary 'aerolink-postgresql.dump'))){throw 'The PostgreSQL dump is missing.'}
 $inventoryPath=Join-Path $temporary ([string]$manifestObject.AttachmentInventory);if(-not(Test-Path -LiteralPath $inventoryPath)){throw 'The controlled-attachment inventory is missing.'}
 $inventoryObject=ConvertFrom-Json -InputObject (Get-Content -LiteralPath $inventoryPath -Raw);$inventory=@($inventoryObject|ForEach-Object{$_})
 Import-Module (Join-Path $PSScriptRoot 'AeroLinkEvidenceStore.psm1') -Force
 $verified=Test-AeroLinkAttachmentInventory -Inventory $inventory -EvidenceRoot (Join-Path $temporary ([string]$manifestObject.Storage.ArchiveRoot))
 if($verified.ReferencedObjects -ne [int]$manifestObject.Storage.ObjectCount -or $verified.ReferencedAttachments -ne [int]$manifestObject.Storage.AttachmentCount -or $verified.VerifiedBytes -ne [long]$manifestObject.Storage.ReferencedBytes -or $verified.UnreferencedObjects.Count -ne [int]$manifestObject.Storage.UnreferencedObjectCount){throw 'Backup storage counts do not match the verified attachment inventory.'}
 [pscustomobject]@{Valid=$true;Archive=$archive;ArchiveSha256=$actual;ManifestEntries=$manifest.Count;ReferencedAttachments=$verified.ReferencedAttachments;ReferencedObjects=$verified.ReferencedObjects;ReferencedBytes=$verified.VerifiedBytes;UnreferencedObjects=$verified.UnreferencedObjects;VerifiedAt=(Get-Date).ToUniversalTime().ToString('o')}
}finally{if(Test-Path -LiteralPath $temporary){Remove-Item -LiteralPath $temporary -Recurse -Force}}
