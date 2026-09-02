$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'AeroLinkBackupArchive.psm1') -Force
$root=Join-Path ([IO.Path]::GetTempPath()) ('aerolink-relocated-backup-'+[Guid]::NewGuid().ToString('N'))
$verificationRoot=Join-Path $root 'verification';$staging=Join-Path $root 'source';$relocated=Join-Path $root 'relocated backup Ω';New-Item -ItemType Directory -Path (Join-Path $staging 'evidence\ab') -Force|Out-Null;New-Item -ItemType Directory -Path $relocated -Force|Out-Null;New-Item -ItemType Directory -Path $verificationRoot -Force|Out-Null
$verifyScript=Join-Path $PSScriptRoot 'Verify-AeroLinkBackup.ps1'
$verifySource=[IO.File]::ReadAllText($verifyScript)
if(@([regex]::Matches($verifySource,'\[pscustomobject\]@\{Valid=\$true')).Count -ne 1 -or $verifySource -notmatch '\$verificationResult=\[pscustomobject\]@\{Valid=\$true'){throw 'Verification success must be stored in $verificationResult, never emitted before cleanup resolves.'}
$primaryPrecedence=$verifySource.IndexOf('if($verificationError){throw $verificationError}')
$cleanupFailure=$verifySource.IndexOf('cleanup of the temporary verification copy failed')
if($primaryPrecedence -lt 0 -or $cleanupFailure -lt 0 -or $primaryPrecedence -gt $cleanupFailure){throw 'The primary verification failure must precede and outrank the cleanup failure.'}
if($verifySource.IndexOf('if($cleanupError){throw "Backup archive content verification succeeded') -lt 0){throw 'A successful verification with failed cleanup must fail the invocation.'}
if($verifySource -notlike '*Write-Warning "Backup verification cleanup failed; the temporary directory remains at*'){throw 'Cleanup failure must warn with the retained temporary path.'}
try{
 $object=Join-Path $staging 'evidence\ab\object.docx';[IO.File]::WriteAllText($object,'controlled backup object');$hash=(Get-FileHash -LiteralPath $object -Algorithm SHA256).Hash.ToLowerInvariant();$size=(Get-Item -LiteralPath $object).Length
 [IO.File]::WriteAllText((Join-Path $staging 'aerolink-postgresql.dump'),'disposable dump fixture')
 $inventory=@([pscustomobject]@{Id=[Guid]::NewGuid();StorageKey='ab/object.docx';Size=$size;Sha256=$hash;ArtifactType='ManagedDocument';ArtifactId=[Guid]::NewGuid();RevisionId=[Guid]::NewGuid()})
 ConvertTo-Json -InputObject $inventory -Depth 4|Set-Content -LiteralPath (Join-Path $staging 'attachment-inventory.json') -Encoding UTF8
 $files=Get-AeroLinkBackupFileInventory -StagingRoot $staging
 if(@($files|Where-Object{([string]$_.Path) -like '*\*'}).Count -ne 0){throw 'The production file inventory still emits backslash-separated manifest paths.'}
 $manifest=[ordered]@{FormatVersion=2;CreatedAtUtc=(Get-Date).ToUniversalTime().ToString('o');Application=@{SourceSha='test';SchemaVersion='test'};Database=@{Name='test';Dump='aerolink-postgresql.dump'};Storage=@{Scheme='filesystem-v1';SourceRoot='fixture';ArchiveRoot='evidence';ObjectCount=1;AttachmentCount=1;ReferencedBytes=$size;UnreferencedObjectCount=0;UnreferencedObjects=@()};AttachmentInventory='attachment-inventory.json';Files=$files}
 $manifest|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $staging 'manifest.json') -Encoding UTF8
 $archive=Join-Path $relocated 'portable backup.zip';Compress-AeroLinkBackupArchive -SourceDirectory $staging -DestinationArchive $archive
 Add-Type -AssemblyName System.IO.Compression;Add-Type -AssemblyName System.IO.Compression.FileSystem
 $zip=[IO.Compression.ZipFile]::OpenRead($archive)
 try{
  $entries=@($zip.Entries|ForEach-Object{[string]$_.FullName})
  if($entries.Count -lt 4){throw 'The produced archive did not contain the expected entries.'}
  if(@($entries|Where-Object{$_ -like '*\*'}).Count -ne 0){throw 'The produced archive contains backslash-separated ZIP entry names.'}
  if(-not($entries -contains 'evidence/ab/object.docx')){throw 'The nested archive entry is missing or not forward-slash separated.'}
  foreach($entryPath in $entries){if([IO.Path]::IsPathRooted($entryPath)-or(@($entryPath -split '[\\/]') -contains '..')){throw "Unsafe entry in the produced archive: $entryPath"}}
  $entryStream=$zip.GetEntry('evidence/ab/object.docx').Open();$reader=New-Object IO.StreamReader($entryStream);$roundTrip=$reader.ReadToEnd();$reader.Dispose()
  if($roundTrip -ne 'controlled backup object'){throw 'Archive entry content did not survive the round trip.'}
 }finally{$zip.Dispose()}
 $archiveHash=(Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant();"$archiveHash  portable backup.zip"|Set-Content -LiteralPath "$archive.sha256" -Encoding ASCII
 $verified=& $verifyScript -BackupArchive $archive -VerificationRoot $verificationRoot
 if(-not $verified.Valid -or $verified.ReferencedAttachments -ne 1 -or $verified.ReferencedObjects -ne 1){throw 'A relocated valid archive did not verify.'}
 if(@(Get-ChildItem -LiteralPath $verificationRoot -Force).Count -ne 0){throw 'The verification root was not empty after a successful verification.'}
 $ps51=Get-Command powershell.exe -ErrorAction SilentlyContinue
 $ansiEscape=[char]27+'\[[0-9;]*m'
 if($ps51){
  $ps51Verification=((& $ps51.Source -NoProfile -ExecutionPolicy Bypass -File $verifyScript -BackupArchive $archive -VerificationRoot $verificationRoot)|Out-String)-replace $ansiEscape,''
  if($LASTEXITCODE -ne 0 -or $ps51Verification -notmatch 'Valid\s*:\s*True'){throw 'The new-format archive did not verify under Windows PowerShell 5.1.'}
 }
 $pwsh=Get-Command pwsh -ErrorAction SilentlyContinue
 if($pwsh){
  $pwshVerification=((& $pwsh.Source -NoProfile -ExecutionPolicy Bypass -File $verifyScript -BackupArchive $archive -VerificationRoot $verificationRoot)|Out-String)-replace $ansiEscape,''
  if($LASTEXITCODE -ne 0 -or $pwshVerification -notmatch 'Valid\s*:\s*True'){throw 'The new-format archive did not verify under PowerShell 7.'}
 }
 Add-Content -LiteralPath $archive -Value 'corruption' -NoNewline
 try{& $verifyScript -BackupArchive $archive -VerificationRoot $verificationRoot|Out-Null;throw 'A corrupted relocated archive was accepted.'}catch{if($_.Exception.Message -notlike '*hash mismatch*'){throw}}
 if(@(Get-ChildItem -LiteralPath $verificationRoot -Force).Count -ne 0){throw 'The verification root was not empty after rejecting corruption.'}

 $legacyStaging=Join-Path $root 'legacy-source';$legacyOut=Join-Path $root 'legacy out Ω';New-Item -ItemType Directory -Path (Join-Path $legacyStaging 'evidence\ab') -Force|Out-Null;New-Item -ItemType Directory -Path $legacyOut -Force|Out-Null
 $legacyObject=Join-Path $legacyStaging 'evidence\ab\object.docx';[IO.File]::WriteAllText($legacyObject,'legacy archive object');$legacyHash=(Get-FileHash -LiteralPath $legacyObject -Algorithm SHA256).Hash.ToLowerInvariant();$legacySize=(Get-Item -LiteralPath $legacyObject).Length
 [IO.File]::WriteAllText((Join-Path $legacyStaging 'aerolink-postgresql.dump'),'disposable dump fixture')
 $legacyInventory=@([pscustomobject]@{Id=[Guid]::NewGuid();StorageKey='ab/object.docx';Size=$legacySize;Sha256=$legacyHash;ArtifactType='ManagedDocument';ArtifactId=[Guid]::NewGuid();RevisionId=[Guid]::NewGuid()})
 ConvertTo-Json -InputObject $legacyInventory -Depth 4|Set-Content -LiteralPath (Join-Path $legacyStaging 'attachment-inventory.json') -Encoding UTF8
 $legacyFiles=@(Get-ChildItem -LiteralPath $legacyStaging -File -Recurse|ForEach-Object{[pscustomobject]@{Path=$_.FullName.Substring($legacyStaging.Length+1);Size=$_.Length;Sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}})
 $legacyManifest=[ordered]@{FormatVersion=2;CreatedAtUtc=(Get-Date).ToUniversalTime().ToString('o');Application=@{SourceSha='test';SchemaVersion='test'};Database=@{Name='test';Dump='aerolink-postgresql.dump'};Storage=@{Scheme='filesystem-v1';SourceRoot='fixture';ArchiveRoot='evidence';ObjectCount=1;AttachmentCount=1;ReferencedBytes=$legacySize;UnreferencedObjectCount=0;UnreferencedObjects=@()};AttachmentInventory='attachment-inventory.json';Files=$legacyFiles}
 $legacyManifest|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $legacyStaging 'manifest.json') -Encoding UTF8
 $legacyArchive=Join-Path $legacyOut 'legacy backup.zip'
 $legacyZip=[IO.Compression.ZipFile]::Open($legacyArchive,'Create')
 foreach($directory in (Get-ChildItem -LiteralPath $legacyStaging -Directory -Recurse)){[void]$legacyZip.CreateEntry(($directory.FullName.Substring($legacyStaging.Length+1)+'\'))}
 foreach($file in (Get-ChildItem -LiteralPath $legacyStaging -File -Recurse)){$legacyEntry=$legacyZip.CreateEntry($file.FullName.Substring($legacyStaging.Length+1),[IO.Compression.CompressionLevel]::Optimal);$legacySource=[IO.File]::OpenRead($file.FullName);try{$legacyTarget=$legacyEntry.Open();try{$legacySource.CopyTo($legacyTarget)}finally{$legacyTarget.Dispose()}}finally{$legacySource.Dispose()}}
 $legacyZip.Dispose()
 $legacyInspector=[IO.Compression.ZipFile]::OpenRead($legacyArchive)
 try{$legacyEntries=@($legacyInspector.Entries|ForEach-Object{[string]$_.FullName})}finally{$legacyInspector.Dispose()}
 if(@($legacyEntries|Where-Object{$_ -like '*\*'}).Count -lt 1){throw 'The synthetic legacy archive did not reproduce the backslash-entry defect.'}
 $legacyArchiveHash=(Get-FileHash -LiteralPath $legacyArchive -Algorithm SHA256).Hash.ToLowerInvariant();"$legacyArchiveHash  legacy backup.zip"|Set-Content -LiteralPath "$legacyArchive.sha256" -Encoding ASCII
 if($ps51){
  $ps51Legacy=((& $ps51.Source -NoProfile -ExecutionPolicy Bypass -File $verifyScript -BackupArchive $legacyArchive -VerificationRoot $verificationRoot)|Out-String)-replace $ansiEscape,''
  if($LASTEXITCODE -ne 0 -or $ps51Legacy -notmatch 'Valid\s*:\s*True'){throw 'A legacy backslash-separated archive did not verify under Windows PowerShell 5.1.'}
 }
 $legacyVerified=& $verifyScript -BackupArchive $legacyArchive -VerificationRoot $verificationRoot
 if(-not $legacyVerified.Valid){throw 'A legacy backslash-separated archive did not verify in the current host.'}

 $traversalArchive=Join-Path $root 'traversal.zip'
 $traversalZip=[IO.Compression.ZipFile]::Open($traversalArchive,'Create')
 $traversalEntry=$traversalZip.CreateEntry('..\.a893-escape.txt',[IO.Compression.CompressionLevel]::Optimal);$traversalStream=$traversalEntry.Open();$traversalWriter=New-Object IO.StreamWriter($traversalStream);$traversalWriter.Write('escape');$traversalWriter.Dispose()
 $traversalZip.Dispose()
 $traversalHash=(Get-FileHash -LiteralPath $traversalArchive -Algorithm SHA256).Hash.ToLowerInvariant();"$traversalHash  traversal.zip"|Set-Content -LiteralPath "$traversalArchive.sha256" -Encoding ASCII
 try{& $verifyScript -BackupArchive $traversalArchive -VerificationRoot $verificationRoot|Out-Null;throw 'A traversal archive was accepted.'}catch{if($_.Exception.Message -notlike '*Unsafe archive path*'){throw}}
 if(@(Get-ChildItem -LiteralPath $verificationRoot -Force).Count -ne 0){throw 'The verification root was not empty after rejecting a traversal archive.'}

 $malformed=Join-Path $root 'malformed.zip';$validBytes=[IO.File]::ReadAllBytes($archive);[IO.File]::WriteAllBytes($malformed,$validBytes[0..($validBytes.Length-200)])
 $malformedHash=(Get-FileHash -LiteralPath $malformed -Algorithm SHA256).Hash.ToLowerInvariant();"$malformedHash  malformed.zip"|Set-Content -LiteralPath "$malformed.sha256" -Encoding ASCII
 try{& $verifyScript -BackupArchive $malformed -VerificationRoot $verificationRoot|Out-Null;throw 'A malformed archive was accepted.'}catch{if($_.Exception.Message -notmatch '(?i)zip|central directory'){throw "The malformed archive failed for an unexpected reason: $($_.Exception.Message)"}}
 if(@(Get-ChildItem -LiteralPath $verificationRoot -Force).Count -ne 0){throw 'The verification root was not empty after rejecting a malformed archive.'}

 [pscustomobject]@{Passed=$true;RelocatedArchive=$archive;ReferencedAttachments=$verified.ReferencedAttachments;EntryNamesPortable=$true;BackslashEntryCount=0;WindowsPowerShell51Verified=[bool]$ps51;PowerShell7Verified=[bool]$pwsh;LegacyBackslashArchiveVerified=$legacyVerified.Valid;TraversalRejected=$true;MalformedRejected=$true;CorruptionRejected=$true}
 $global:LASTEXITCODE=0
}finally{if(Test-Path -LiteralPath $root){Remove-Item -LiteralPath $root -Recurse -Force};if(Test-Path -LiteralPath $root){throw 'The disposable backup verification root remained after cleanup.'}}
