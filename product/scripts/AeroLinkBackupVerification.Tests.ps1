$ErrorActionPreference='Stop'
$root=Join-Path ([IO.Path]::GetTempPath()) ('aerolink-relocated-backup-'+[Guid]::NewGuid().ToString('N'))
$staging=Join-Path $root 'source';$relocated=Join-Path $root 'relocated backup Ω';New-Item -ItemType Directory -Path (Join-Path $staging 'evidence\ab') -Force|Out-Null;New-Item -ItemType Directory -Path $relocated -Force|Out-Null
try{
 $object=Join-Path $staging 'evidence\ab\object.docx';[IO.File]::WriteAllText($object,'controlled backup object');$hash=(Get-FileHash -LiteralPath $object -Algorithm SHA256).Hash.ToLowerInvariant();$size=(Get-Item -LiteralPath $object).Length
 [IO.File]::WriteAllText((Join-Path $staging 'aerolink-postgresql.dump'),'disposable dump fixture')
 $inventory=@([pscustomobject]@{Id=[Guid]::NewGuid();StorageKey='ab/object.docx';Size=$size;Sha256=$hash;ArtifactType='ManagedDocument';ArtifactId=[Guid]::NewGuid();RevisionId=[Guid]::NewGuid()})
 ConvertTo-Json -InputObject $inventory -Depth 4|Set-Content -LiteralPath (Join-Path $staging 'attachment-inventory.json') -Encoding UTF8
 $files=@(Get-ChildItem -LiteralPath $staging -File -Recurse|ForEach-Object{[pscustomobject]@{Path=$_.FullName.Substring($staging.Length+1);Size=$_.Length;Sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}})
 $manifest=[ordered]@{FormatVersion=2;CreatedAtUtc=(Get-Date).ToUniversalTime().ToString('o');Application=@{SourceSha='test';SchemaVersion='test'};Database=@{Name='test';Dump='aerolink-postgresql.dump'};Storage=@{Scheme='filesystem-v1';SourceRoot='fixture';ArchiveRoot='evidence';ObjectCount=1;AttachmentCount=1;ReferencedBytes=$size;UnreferencedObjectCount=0;UnreferencedObjects=@()};AttachmentInventory='attachment-inventory.json';Files=$files}
 $manifest|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $staging 'manifest.json') -Encoding UTF8
 $archive=Join-Path $relocated 'portable backup.zip';Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $archive
 $archiveHash=(Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant();"$archiveHash  portable backup.zip"|Set-Content -LiteralPath "$archive.sha256" -Encoding ASCII
 $verified=& (Join-Path $PSScriptRoot 'Verify-AeroLinkBackup.ps1') -BackupArchive $archive
 if(-not $verified.Valid -or $verified.ReferencedAttachments -ne 1 -or $verified.ReferencedObjects -ne 1){throw 'A relocated valid archive did not verify.'}
 Add-Content -LiteralPath $archive -Value 'corruption' -NoNewline
 try{& (Join-Path $PSScriptRoot 'Verify-AeroLinkBackup.ps1') -BackupArchive $archive|Out-Null;throw 'A corrupted relocated archive was accepted.'}catch{if($_.Exception.Message -notlike '*hash mismatch*'){throw}}
 [pscustomobject]@{Passed=$true;RelocatedArchive=$archive;ReferencedAttachments=$verified.ReferencedAttachments;CorruptionRejected=$true}
 $global:LASTEXITCODE=0
}finally{if(Test-Path -LiteralPath $root){Remove-Item -LiteralPath $root -Recurse -Force}}
