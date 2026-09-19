$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'AeroLinkEvidenceStore.psm1') -Force
$productRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$copyRoot = Join-Path ([IO.Path]::GetTempPath()) ('AeroLink long-path copy ' + [Guid]::NewGuid().ToString('N'))
$root = Join-Path ([IO.Path]::GetTempPath()) ("AeroLink evidence Ω spaces " + [Guid]::NewGuid().ToString('N'))

function Expect-Failure([scriptblock]$Action, [string]$Pattern) {
    try { & $Action; throw "Expected failure matching '$Pattern'." }
    catch { if ($_.Exception.Message -notlike "*$Pattern*") { throw } }
}

function Invoke-RobocopyChecked([string[]]$Arguments, [string]$FailureMessage) {
    & robocopy.exe @Arguments | Out-Null
    $exitCode = $LASTEXITCODE
    $global:LASTEXITCODE = 0
    if ($exitCode -ge 8) { throw "$FailureMessage (robocopy exit code $exitCode)." }
}

function Remove-LongPathTree([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $emptyRoot = Join-Path ([IO.Path]::GetTempPath()) ('AeroLink empty mirror ' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $emptyRoot -Force | Out-Null
    try {
        Invoke-RobocopyChecked @($emptyRoot, $Path, '/MIR', '/R:1', '/W:1', '/NFL', '/NDL', '/NJH', '/NJS', '/NP') 'Could not clean the long-path evidence fixture'
    }
    finally {
        if (Test-Path -LiteralPath $emptyRoot) { Remove-Item -LiteralPath $emptyRoot -Recurse -Force }
    }
    if (Test-Path -LiteralPath $Path) { Remove-Item -LiteralPath $Path -Recurse -Force }
}

try {
    New-Item -ItemType Directory -Path (Join-Path $root 'aa') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $root 'bb') -Force | Out-Null
    $bytes = [Text.Encoding]::UTF8.GetBytes('exact controlled evidence')
    [IO.File]::WriteAllBytes((Join-Path $root 'aa\first.docx'), $bytes)
    [IO.File]::WriteAllBytes((Join-Path $root 'bb\duplicate.docx'), $bytes)
    [IO.File]::WriteAllText((Join-Path $root 'orphan.bin'), 'unreferenced')
    $hash = (Get-FileHash -LiteralPath (Join-Path $root 'aa\first.docx') -Algorithm SHA256).Hash.ToLowerInvariant()
    $inventory = @(
        [pscustomobject]@{ Id=[Guid]::NewGuid(); StorageKey='aa/first.docx'; Size=$bytes.Length; Sha256=$hash; ArtifactType='ManagedDocument'; ArtifactId=[Guid]::NewGuid(); RevisionId=[Guid]::NewGuid() },
        [pscustomobject]@{ Id=[Guid]::NewGuid(); StorageKey='bb/duplicate.docx'; Size=$bytes.Length; Sha256=$hash; ArtifactType='ManagedDocument'; ArtifactId=[Guid]::NewGuid(); RevisionId=[Guid]::NewGuid() }
    )
    $result = Test-AeroLinkAttachmentInventory -Inventory $inventory -EvidenceRoot $root
    if ($result.ReferencedAttachments -ne 2 -or $result.ReferencedObjects -ne 2 -or $result.UnreferencedObjects.Count -ne 1) { throw 'Inventory counts did not preserve duplicate hashes and report the orphan separately.' }

    # Keep this a genuine > MAX_PATH regression, but construct and inspect it through robocopy rather than
    # System.IO. Windows PowerShell 5.1/.NET Framework can reject extended-length System.IO paths when the
    # machine-wide LongPathsEnabled policy is disabled, even though robocopy itself supports long paths.
    $segment = 'segment-' + ('x' * 52)
    $longRelativeDirectory = "$segment\$segment\$segment"
    $longRelative = "$longRelativeDirectory\retained-evidence.docx"
    $longSource = Join-Path $copyRoot 'source'
    $longDestination = Join-Path $copyRoot 'destination'
    $longSourceLeaf = Join-Path $longSource $longRelativeDirectory
    $longPath = Join-Path $longSource $longRelative
    if ($longPath.Length -le 260) { throw "The long-path fixture was not actually longer than MAX_PATH: $($longPath.Length) characters." }

    $seedRoot = Join-Path $copyRoot 'seed'
    New-Item -ItemType Directory -Path $seedRoot -Force | Out-Null
    [IO.File]::WriteAllBytes((Join-Path $seedRoot 'retained-evidence.docx'), $bytes)
    Invoke-RobocopyChecked @($seedRoot, $longSourceLeaf, 'retained-evidence.docx', '/R:1', '/W:1', '/NFL', '/NDL', '/NJH', '/NJS', '/NP') 'Could not create the genuine long-path evidence fixture'

    Copy-AeroLinkEvidenceTree -Source $longSource -Destination $longDestination

    # Read the copied object back through robocopy into a shallow path so the assertion does not rely on
    # System.IO long-path opt-in. This still proves that the destination contains the exact >260-character
    # evidence object and that its bytes survived the production copy helper.
    $verifyRoot = Join-Path $copyRoot 'verify'
    New-Item -ItemType Directory -Path $verifyRoot -Force | Out-Null
    $longDestinationLeaf = Join-Path $longDestination $longRelativeDirectory
    $longDestinationPath = Join-Path $longDestination $longRelative
    if ($longDestinationPath.Length -le 260) { throw 'The copied evidence path was not actually longer than MAX_PATH.' }
    Invoke-RobocopyChecked @($longDestinationLeaf, $verifyRoot, 'retained-evidence.docx', '/R:1', '/W:1', '/NFL', '/NDL', '/NJH', '/NJS', '/NP') 'The supported long-path evidence copy did not preserve the object'
    $verifiedPath = Join-Path $verifyRoot 'retained-evidence.docx'
    if (-not [IO.File]::Exists($verifiedPath)) { throw 'The supported long-path evidence copy did not preserve the object.' }
    $verifiedHash = (Get-FileHash -LiteralPath $verifiedPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($verifiedHash -ne $hash) { throw 'The supported long-path evidence copy changed the object bytes.' }

    $missing = @($inventory | ForEach-Object { $_.PSObject.Copy() }); $missing[0].StorageKey = 'aa/missing.docx'
    Expect-Failure { Test-AeroLinkAttachmentInventory -Inventory $missing -EvidenceRoot $root } 'missing'
    $wrongSize = @($inventory | ForEach-Object { $_.PSObject.Copy() }); $wrongSize[0].Size = $bytes.Length + 1
    Expect-Failure { Test-AeroLinkAttachmentInventory -Inventory $wrongSize -EvidenceRoot $root } 'size mismatch'
    $wrongHash = @($inventory | ForEach-Object { $_.PSObject.Copy() }); $wrongHash[0].Sha256 = ('0' * 64)
    Expect-Failure { Test-AeroLinkAttachmentInventory -Inventory $wrongHash -EvidenceRoot $root } 'hash mismatch'
    $unsafe = @($inventory | ForEach-Object { $_.PSObject.Copy() }); $unsafe[0].StorageKey = '../escape.docx'
    Expect-Failure { Test-AeroLinkAttachmentInventory -Inventory $unsafe -EvidenceRoot $root } 'Unsafe attachment storage key'

    # --- Database schema classification and the storage readers (#1055 first start, TA-2, Astra R2-1) ---
    # Presence of a migration-history TABLE is not presence of applied migrations, and the capability tables have
    # DIFFERENT introduction points: controlled_attachments (20260713003618) precedes atomic-storage bookkeeping
    # (20260812172807). A supported older schema can therefore hold real attachment references. Capabilities are
    # derived from the catalogue AND the history, contradictions are refused, and the readers act on the
    # capability - not on a state name. The exact SQL boundary is the only thing stubbed.
    $stubPsql = Join-Path $copyRoot 'stub-psql.ps1'
    $stubText = @'
$sqlPath = $null
for ($i = 0; $i -lt $args.Count; $i++) { if ($args[$i] -eq '-f') { $sqlPath = $args[$i + 1] } }
$sql = if ($sqlPath) { Get-Content -LiteralPath $sqlPath -Raw } else { '' }
if ($sql -match 'to_regclass') {
    switch ($env:AL_EVIDENCE_STUB) {
        'fresh' { '0,0,0,0,0,0'; exit 0 }
        'fresh-schema' { '1,0,0,0,0,0'; exit 0 }
        'prestorage-no-attachments' { '1,1,0,0,0,1'; exit 0 }
        'older-attachments' { '1,1,1,0,0,2'; exit 0 }
        'older-with-lifecycle' { '1,1,1,1,0,3'; exit 0 }
        'corrupt-storageops-without-migration' { '1,1,1,1,1,4'; exit 0 }
        'corrupt-attachments-without-migration' { '1,1,1,0,0,2'; exit 0 }
        'corrupt-zero-history-with-counts' { '1,1,0,0,0,1'; exit 0 }
        'corrupt-relations-without-history' { '0,1,0,0,0,1'; exit 0 }
        'malformed-catalogue' { '2,2,2,2,2,2'; exit 0 }
        'empty-classification' { exit 0 }
        default { '1,1,1,1,1,4'; exit 0 }
    }
}
if ($sql -match 'FROM "__EFMigrationsHistory"') {
    switch ($env:AL_EVIDENCE_STUB) {
        'fresh-schema' { '0,0,0,0'; exit 0 }
        'prestorage-no-attachments' { '2,0,0,0'; exit 0 }
        'older-attachments' { '30,1,0,0'; exit 0 }
        'older-with-lifecycle' { '40,1,1,0'; exit 0 }
        'corrupt-storageops-without-migration' { '30,1,1,0'; exit 0 }
        'corrupt-attachments-without-migration' { '30,0,0,0'; exit 0 }
        'corrupt-zero-history-with-counts' { '0,1,0,0'; exit 0 }
        'malformed-counts' { '30,1'; exit 0 }
        default { '152,1,1,1'; exit 0 }
    }
}
if ($sql -match 'COPY \(') {
    if ($env:AL_EVIDENCE_INVENTORY_MARKER) { [IO.File]::WriteAllText($env:AL_EVIDENCE_INVENTORY_MARKER, 'COPY issued') }
    '"Id","StorageKey","Size","Sha256","ArtifactType","ArtifactId","RevisionId"'
    '"11111111-1111-1111-1111-111111111111","aa/first.docx","3","aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","ManagedDocument","22222222-2222-2222-2222-222222222222","33333333-3333-3333-3333-333333333333"'
    exit 0
}
if ($sql -notmatch 'to_regclass' -and $sql -match 'managed_document_storage_operations') {
    if ($env:AL_EVIDENCE_HEALTH_MARKER) { [IO.File]::WriteAllText($env:AL_EVIDENCE_HEALTH_MARKER, 'health query issued') }
    switch ($env:AL_EVIDENCE_STUB) {
        'unhealthy' { '1,0,0'; exit 0 }
        'empty-health' { exit 0 }
        default { '0,0,0'; exit 0 }
    }
}
exit 0
'@
    [IO.File]::WriteAllText($stubPsql, $stubText, (New-Object Text.UTF8Encoding($false)))

    # Classification itself: each state is named from the catalogue answer and the migration history, and
    # partial/malformed/absent answers are throwing rather than being folded into "fresh".
    $classify = { param($mode) $env:AL_EVIDENCE_STUB = $mode; Get-AeroLinkDatabaseSchemaState -Psql $stubPsql -Database 'stub' -Port 55999 }
    if ((& $classify 'fresh').State -ne 'Fresh') { throw 'A catalogue answer with no history and no known relation must classify as Fresh.' }
    if ((& $classify 'fresh-schema').State -ne 'FreshSchema') { throw 'A zero-migration history with no known relation must classify as FreshSchema.' }
    $prestorageNoAttachments = & $classify 'prestorage-no-attachments'
    if ($prestorageNoAttachments.State -ne 'PreStorage' -or $prestorageNoAttachments.AttachmentsPresent) { throw "A schema predating the attachment migration must classify as PreStorage without attachments, got '$($prestorageNoAttachments.State)'." }
    $older = & $classify 'older-attachments'
    if ($older.State -ne 'PreStorage' -or -not $older.AttachmentsPresent -or $older.StorageOperationsPresent) { throw "A supported older schema with attachments must classify as PreStorage with attachments present, got '$($older.State)'." }
    $olderLifecycle = & $classify 'older-with-lifecycle'
    if ($olderLifecycle.State -ne 'PreStorage' -or -not $olderLifecycle.DocumentRevisionsPresent) { throw "A schema with managed-document revisions but no atomic storage must classify as PreStorage, got '$($olderLifecycle.State)'." }
    if ((& $classify 'supported').State -ne 'Supported') { throw 'A current schema must classify as Supported.' }
    foreach ($corrupt in @('corrupt-storageops-without-migration','corrupt-attachments-without-migration','corrupt-zero-history-with-counts','corrupt-relations-without-history')) {
        $state = & $classify $corrupt
        if ($state.State -ne 'PartialOrCorrupt') { throw "The contradictory schema '$corrupt' must classify as PartialOrCorrupt, got '$($state.State)'." }
    }
    Expect-Failure { $env:AL_EVIDENCE_STUB = 'malformed-catalogue'; Get-AeroLinkDatabaseSchemaState -Psql $stubPsql -Database 'stub' -Port 55999 } 'could not be classified'
    Expect-Failure { $env:AL_EVIDENCE_STUB = 'malformed-counts'; Get-AeroLinkDatabaseSchemaState -Psql $stubPsql -Database 'stub' -Port 55999 } 'could not be classified'
    Expect-Failure { $env:AL_EVIDENCE_STUB = 'empty-classification'; Get-AeroLinkDatabaseSchemaState -Psql $stubPsql -Database 'stub' -Port 55999 } 'could not be classified'

    $markerInventory = Join-Path $copyRoot 'inventory-query.marker'
    $markerHealth = Join-Path $copyRoot 'health-query.marker'
    $resetMarkers = { Remove-Item -LiteralPath $markerInventory,$markerHealth -Force -ErrorAction SilentlyContinue }
    $env:AL_EVIDENCE_INVENTORY_MARKER = $markerInventory
    $env:AL_EVIDENCE_HEALTH_MARKER = $markerHealth
    $env:AL_EVIDENCE_STUB = 'fresh'
    & $resetMarkers
    Assert-AeroLinkStorageLifecycleHealthy -Psql $stubPsql -Database 'stub' -Port 55999
    $freshInventory = @(Get-AeroLinkAttachmentInventory -Psql $stubPsql -Database 'stub' -Port 55999)
    if ($freshInventory.Count -ne 0) { throw "A fresh database must inventory as zero attachments, got $($freshInventory.Count)." }
    if ((Test-Path -LiteralPath $markerInventory) -or (Test-Path -LiteralPath $markerHealth)) { throw 'A fresh database must not query the capability tables at all.' }
    $env:AL_EVIDENCE_STUB = 'prestorage-no-attachments'
    & $resetMarkers
    $prestorageInventory = @(Get-AeroLinkAttachmentInventory -Psql $stubPsql -Database 'stub' -Port 55999)
    if ($prestorageInventory.Count -ne 0 -or (Test-Path -LiteralPath $markerInventory)) { throw 'A schema predating the attachment migration must not query controlled_attachments.' }

    # R2-1 regression: a supported older schema HAS the attachment table, so its references must be inventoried
    # (COPY issued) even though atomic-storage bookkeeping does not exist yet, and health stays inapplicable.
    $env:AL_EVIDENCE_STUB = 'older-attachments'
    & $resetMarkers
    Assert-AeroLinkStorageLifecycleHealthy -Psql $stubPsql -Database 'stub' -Port 55999
    $olderInventory = @(Get-AeroLinkAttachmentInventory -Psql $stubPsql -Database 'stub' -Port 55999)
    if ($olderInventory.Count -ne 1) { throw "An older schema with attachments must inventory its controlled attachment, got $($olderInventory.Count)." }
    if (-not (Test-Path -LiteralPath $markerInventory)) { throw 'An older schema with attachments must issue the controlled_attachments COPY.' }
    if (Test-Path -LiteralPath $markerHealth) { throw 'A schema without atomic-storage bookkeeping must not run the lifecycle health query.' }
    $env:AL_EVIDENCE_STUB = 'older-with-lifecycle'
    & $resetMarkers
    Assert-AeroLinkStorageLifecycleHealthy -Psql $stubPsql -Database 'stub' -Port 55999
    $lifecycleInventory = @(Get-AeroLinkAttachmentInventory -Psql $stubPsql -Database 'stub' -Port 55999)
    if ($lifecycleInventory.Count -ne 1) { throw "A lifecycle-era older schema must inventory its controlled attachment, got $($lifecycleInventory.Count)." }
    if (Test-Path -LiteralPath $markerHealth) { throw 'A schema without atomic-storage bookkeeping must not run the lifecycle health query.' }

    $env:AL_EVIDENCE_STUB = 'supported'
    & $resetMarkers
    Assert-AeroLinkStorageLifecycleHealthy -Psql $stubPsql -Database 'stub' -Port 55999
    $supportedInventory = @(Get-AeroLinkAttachmentInventory -Psql $stubPsql -Database 'stub' -Port 55999)
    if ($supportedInventory.Count -ne 1) { throw "A supported schema must inventory its controlled attachments, got $($supportedInventory.Count)." }
    if (-not (Test-Path -LiteralPath $markerHealth)) { throw 'A supported schema must run the lifecycle health query.' }
    & $resetMarkers
    $env:AL_EVIDENCE_STUB = 'corrupt-storageops-without-migration'
    Expect-Failure { Assert-AeroLinkStorageLifecycleHealthy -Psql $stubPsql -Database 'stub' -Port 55999 } 'refused'
    Expect-Failure { Get-AeroLinkAttachmentInventory -Psql $stubPsql -Database 'stub' -Port 55999 } 'refused'
    $env:AL_EVIDENCE_STUB = 'corrupt-attachments-without-migration'
    Expect-Failure { Get-AeroLinkAttachmentInventory -Psql $stubPsql -Database 'stub' -Port 55999 } 'refused'
    $env:AL_EVIDENCE_STUB = 'corrupt-zero-history-with-counts'
    Expect-Failure { Get-AeroLinkAttachmentInventory -Psql $stubPsql -Database 'stub' -Port 55999 } 'refused'
    $env:AL_EVIDENCE_STUB = 'corrupt-relations-without-history'
    Expect-Failure { Get-AeroLinkAttachmentInventory -Psql $stubPsql -Database 'stub' -Port 55999 } 'refused'
    $env:AL_EVIDENCE_STUB = 'malformed-catalogue'
    Expect-Failure { Assert-AeroLinkStorageLifecycleHealthy -Psql $stubPsql -Database 'stub' -Port 55999 } 'could not be classified'
    $env:AL_EVIDENCE_STUB = 'empty-classification'
    Expect-Failure { Get-AeroLinkAttachmentInventory -Psql $stubPsql -Database 'stub' -Port 55999 } 'could not be classified'
    Remove-Item Env:\AL_EVIDENCE_INVENTORY_MARKER,Env:\AL_EVIDENCE_HEALTH_MARKER -ErrorAction SilentlyContinue
    $env:AL_EVIDENCE_STUB = 'unhealthy'
    Expect-Failure { Assert-AeroLinkStorageLifecycleHealthy -Psql $stubPsql -Database 'stub' -Port 55999 } 'not backup/restore ready'
    $env:AL_EVIDENCE_STUB = 'empty-health'
    Expect-Failure { Assert-AeroLinkStorageLifecycleHealthy -Psql $stubPsql -Database 'stub' -Port 55999 } 'Could not evaluate managed-document storage health'
    Remove-Item Env:\AL_EVIDENCE_STUB -ErrorAction SilentlyContinue
    Remove-Item Env:\AL_EVIDENCE_MARKER -ErrorAction SilentlyContinue

    $env:Evidence__Root = $root
    if ((Get-AeroLinkEvidenceRoot -ProductRoot $productRoot) -ne [IO.Path]::GetFullPath($root)) { throw 'Evidence__Root did not take precedence.' }
    Remove-Item Env:\Evidence__Root
    $default = Get-AeroLinkEvidenceRoot -ProductRoot $productRoot
    if ([string]::IsNullOrWhiteSpace($default) -or -not [IO.Path]::IsPathRooted($default)) { throw 'The default evidence root was not resolved canonically.' }
    [pscustomobject]@{ Passed=$true; ReferencedAttachments=$result.ReferencedAttachments; ReferencedObjects=$result.ReferencedObjects; UnreferencedObjects=$result.UnreferencedObjects.Count; CustomRoot=$root; DefaultRoot=$default }
    $global:LASTEXITCODE = 0
}
finally {
    Remove-Item Env:\Evidence__Root -ErrorAction SilentlyContinue
    Remove-Item Env:\AL_EVIDENCE_STUB -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
    Remove-LongPathTree $copyRoot
}
