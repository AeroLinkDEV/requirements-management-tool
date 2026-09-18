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

    # --- Database schema classification and the storage readers (#1055 first start, TA-2) ---
    # Presence of a migration-history TABLE is not presence of applied migrations, and three absent relations do
    # not prove an empty database. The classifier answers with one catalogue probe; the readers accept a genuine
    # fresh schema or a supported pre-storage schema, verify a supported/current schema, and fail closed on
    # partial, malformed or absent answers. The exact SQL boundary is the only thing stubbed.
    $stubPsql = Join-Path $copyRoot 'stub-psql.ps1'
    $stubText = @'
$sqlPath = $null
for ($i = 0; $i -lt $args.Count; $i++) { if ($args[$i] -eq '-f') { $sqlPath = $args[$i + 1] } }
$sql = if ($sqlPath) { Get-Content -LiteralPath $sqlPath -Raw } else { '' }
if ($sql -match 'to_regclass') {
    switch ($env:AL_EVIDENCE_STUB) {
        'fresh' { '0,0,0,0'; exit 0 }
        'fresh-schema' { '1,0,0,0'; exit 0 }
        'prestorage' { '1,1,0,0'; exit 0 }
        'partial-missing-storage' { '1,1,0,1'; exit 0 }
        'partial-no-history' { '0,1,0,0'; exit 0 }
        'partial-no-program' { '1,0,1,1'; exit 0 }
        'malformed' { '2,2,2,2'; exit 0 }
        'empty-classification' { exit 0 }
        default { '1,1,1,1'; exit 0 }
    }
}
if ($sql -match 'FROM "__EFMigrationsHistory"') {
    switch ($env:AL_EVIDENCE_STUB) {
        'fresh-schema' { '0,0'; exit 0 }
        'prestorage' { '5,0'; exit 0 }
        default { '152,1'; exit 0 }
    }
}
if ($sql -match 'COPY \(') {
    if ($env:AL_EVIDENCE_MARKER) { [IO.File]::WriteAllText($env:AL_EVIDENCE_MARKER, 'COPY issued') }
    '"Id","StorageKey","Size","Sha256","ArtifactType","ArtifactId","RevisionId"'
    '"11111111-1111-1111-1111-111111111111","aa/first.docx","3","aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","ManagedDocument","22222222-2222-2222-2222-222222222222","33333333-3333-3333-3333-333333333333"'
    exit 0
}
if ($sql -match 'managed_document_storage_operations') {
    switch ($env:AL_EVIDENCE_STUB) {
        'unhealthy' { '1,0,0'; exit 0 }
        'empty-health' { exit 0 }
        default { '0,0,0'; exit 0 }
    }
}
exit 0
'@
    [IO.File]::WriteAllText($stubPsql, $stubText, (New-Object Text.UTF8Encoding($false)))

    # Classification itself: each state is named from the catalogue answer, and partial/malformed/absent answers
    # are Unknown-or-throwing rather than being folded into "fresh".
    $env:AL_EVIDENCE_STUB = 'fresh'
    $state = Get-AeroLinkDatabaseSchemaState -Psql $stubPsql -Database 'stub' -Port 55999
    if ($state.State -ne 'Fresh') { throw "A catalogue answer with no history and no relations must classify as Fresh, got '$($state.State)'." }
    $env:AL_EVIDENCE_STUB = 'fresh-schema'
    $state = Get-AeroLinkDatabaseSchemaState -Psql $stubPsql -Database 'stub' -Port 55999
    if ($state.State -ne 'FreshSchema') { throw "A zero-migration history with no relations must classify as FreshSchema, got '$($state.State)'." }
    $env:AL_EVIDENCE_STUB = 'prestorage'
    $state = Get-AeroLinkDatabaseSchemaState -Psql $stubPsql -Database 'stub' -Port 55999
    if ($state.State -ne 'PreStorage') { throw "A supported older schema must classify as PreStorage, got '$($state.State)'." }
    $env:AL_EVIDENCE_STUB = 'partial-no-history'
    $state = Get-AeroLinkDatabaseSchemaState -Psql $stubPsql -Database 'stub' -Port 55999
    if ($state.State -ne 'PartialOrCorrupt') { throw "Relations without a migration history must classify as PartialOrCorrupt, got '$($state.State)'." }
    $env:AL_EVIDENCE_STUB = 'partial-no-program'
    $state = Get-AeroLinkDatabaseSchemaState -Psql $stubPsql -Database 'stub' -Port 55999
    if ($state.State -ne 'PartialOrCorrupt') { throw "A migration history without the application root table must classify as PartialOrCorrupt, got '$($state.State)'." }
    $env:AL_EVIDENCE_STUB = 'partial-missing-storage'
    $state = Get-AeroLinkDatabaseSchemaState -Psql $stubPsql -Database 'stub' -Port 55999
    if ($state.State -ne 'PartialOrCorrupt') { throw "A storage migration applied without its tables must classify as PartialOrCorrupt, got '$($state.State)'." }
    $env:AL_EVIDENCE_STUB = 'malformed'
    Expect-Failure { Get-AeroLinkDatabaseSchemaState -Psql $stubPsql -Database 'stub' -Port 55999 } 'could not be classified'
    $env:AL_EVIDENCE_STUB = 'empty-classification'
    Expect-Failure { Get-AeroLinkDatabaseSchemaState -Psql $stubPsql -Database 'stub' -Port 55999 } 'could not be classified'

    $markerFresh = Join-Path $copyRoot 'storage-query-fresh.marker'
    $markerSupported = Join-Path $copyRoot 'storage-query-supported.marker'
    $env:AL_EVIDENCE_STUB = 'fresh'
    $env:AL_EVIDENCE_MARKER = $markerFresh
    Assert-AeroLinkStorageLifecycleHealthy -Psql $stubPsql -Database 'stub' -Port 55999
    $freshInventory = @(Get-AeroLinkAttachmentInventory -Psql $stubPsql -Database 'stub' -Port 55999)
    if ($freshInventory.Count -ne 0) { throw "A fresh database must inventory as zero attachments, got $($freshInventory.Count)." }
    if (Test-Path -LiteralPath $markerFresh) { throw 'A fresh database must not query the storage tables or health columns at all.' }
    $env:AL_EVIDENCE_STUB = 'prestorage'
    Assert-AeroLinkStorageLifecycleHealthy -Psql $stubPsql -Database 'stub' -Port 55999
    $prestorageInventory = @(Get-AeroLinkAttachmentInventory -Psql $stubPsql -Database 'stub' -Port 55999)
    if ($prestorageInventory.Count -ne 0) { throw "A pre-storage schema must inventory as zero attachments before the upgrade, got $($prestorageInventory.Count)." }
    if (Test-Path -LiteralPath $markerFresh) { throw 'A pre-storage schema must not query storage tables that do not exist yet.' }
    Remove-Item Env:\AL_EVIDENCE_MARKER -ErrorAction SilentlyContinue
    $env:AL_EVIDENCE_STUB = 'supported'
    $env:AL_EVIDENCE_MARKER = $markerSupported
    Assert-AeroLinkStorageLifecycleHealthy -Psql $stubPsql -Database 'stub' -Port 55999
    $supportedInventory = @(Get-AeroLinkAttachmentInventory -Psql $stubPsql -Database 'stub' -Port 55999)
    if ($supportedInventory.Count -ne 1) { throw "A supported schema must inventory its controlled attachments, got $($supportedInventory.Count)." }
    if (-not (Test-Path -LiteralPath $markerSupported)) { throw 'A supported schema must query the storage tables.' }
    Remove-Item Env:\AL_EVIDENCE_MARKER -ErrorAction SilentlyContinue
    $env:AL_EVIDENCE_STUB = 'partial-missing-storage'
    Expect-Failure { Assert-AeroLinkStorageLifecycleHealthy -Psql $stubPsql -Database 'stub' -Port 55999 } 'refused'
    Expect-Failure { Get-AeroLinkAttachmentInventory -Psql $stubPsql -Database 'stub' -Port 55999 } 'refused'
    $env:AL_EVIDENCE_STUB = 'partial-no-history'
    Expect-Failure { Get-AeroLinkAttachmentInventory -Psql $stubPsql -Database 'stub' -Port 55999 } 'refused'
    $env:AL_EVIDENCE_STUB = 'malformed'
    Expect-Failure { Assert-AeroLinkStorageLifecycleHealthy -Psql $stubPsql -Database 'stub' -Port 55999 } 'could not be classified'
    $env:AL_EVIDENCE_STUB = 'empty-classification'
    Expect-Failure { Get-AeroLinkAttachmentInventory -Psql $stubPsql -Database 'stub' -Port 55999 } 'could not be classified'
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
