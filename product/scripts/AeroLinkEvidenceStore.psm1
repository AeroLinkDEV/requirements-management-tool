Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'AeroLinkBackupArchive.psm1')

function Invoke-AeroLinkEvidenceSql {
    param([string]$Psql, [string]$Database, [int]$Port, [string]$Sql, [string[]]$OutputArguments = @())
    # Windows PowerShell's native stdin pipeline can prepend a BOM. Use an explicitly BOM-free SQL
    # file so PostgreSQL receives the same query under PS 5.1 and PS 7, including quoted identifiers.
    $path = Join-Path ([IO.Path]::GetTempPath()) ('aerolink-evidence-query-' + [guid]::NewGuid().ToString('N') + '.sql')
    try {
        [IO.File]::WriteAllText($path, $Sql, (New-Object Text.UTF8Encoding($false)))
        $result = & $Psql -h 127.0.0.1 -p $Port -U postgres -d $Database -v ON_ERROR_STOP=1 @OutputArguments -f $path
        if ($LASTEXITCODE -ne 0) { throw "Could not query controlled storage in database '$Database'." }
        return $result
    } finally { if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force } }
}

function Get-AeroLinkEvidenceRoot {
    param([Parameter(Mandatory)][string]$ProductRoot)
    if (-not [string]::IsNullOrWhiteSpace($env:Evidence__Root)) { return [IO.Path]::GetFullPath($env:Evidence__Root) }
    $apiRoot = Join-Path $ProductRoot 'src\AeroLink.Api'
    $root = $null
    foreach ($name in @('appsettings.json', $(if ($env:ASPNETCORE_ENVIRONMENT) { "appsettings.$($env:ASPNETCORE_ENVIRONMENT).json" }))) {
        if (-not $name) { continue }; $path = Join-Path $apiRoot $name
        if (Test-Path -LiteralPath $path) {
            $settings = ConvertFrom-Json -InputObject (Get-Content -LiteralPath $path -Raw)
            $evidenceProperty = $settings.PSObject.Properties['Evidence']
            if ($evidenceProperty -and $evidenceProperty.Value) {
                $rootProperty = $evidenceProperty.Value.PSObject.Properties['Root']
                if ($rootProperty -and -not [string]::IsNullOrWhiteSpace([string]$rootProperty.Value)) { $root = [string]$rootProperty.Value }
            }
        }
    }
    if ([string]::IsNullOrWhiteSpace($root)) { $root = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'AeroLink\evidence' }
    if (-not [IO.Path]::IsPathRooted($root)) { $root = Join-Path $apiRoot $root }
    return [IO.Path]::GetFullPath($root)
}

function Get-AeroLinkEvidenceSqlText {
    <#
      One normalized string for captured SQL output. An empty pipeline/function result is the case that produced
      InvokeMethodOnNull in the #1055 first-start crash: it is neither a literal $null (which casts to '') nor a
      value a caller may pass to a string method. Absent output becomes '' here, and every caller validates it.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Psql,[Parameter(Mandatory)][string]$Database,[int]$Port=54329,
        [Parameter(Mandatory)][string]$Sql,[string[]]$OutputArguments=@())
    $raw = Invoke-AeroLinkEvidenceSql -Psql $Psql -Database $Database -Port $Port -Sql $Sql -OutputArguments $OutputArguments
    if ($null -eq $raw) { return '' }
    $lines = @(@($raw) | ForEach-Object { if ($null -eq $_) { '' } else { [string]$_ } })
    if ($lines.Count -eq 0) { return '' }
    return ($lines -join "`n")
}

$script:AttachmentsMigrationId = '20260713003618_AddEnterpriseHardeningControl'
$script:DocumentLifecycleMigrationId = '20260806032704_AddManagedDocumentationCenter'
$script:AtomicStorageMigrationId = '20260812172807_AddManagedDocumentAtomicStorage'
$script:CoreRelationNames = @('candidate_baselines','programs','projects','software_releases','system_change_requests','baseline_scr_selections','audit_events','requirement_changes','review_cycles','approval_steps','controlled_attachments','managed_document_revisions','managed_document_storage_operations')

function Get-AeroLinkDatabaseSchemaState {
    <#
      .SYNOPSIS Classify what schema this database actually has, fail-closed, from its own migration history.
      .DESCRIPTION
        Presence of a migration-history TABLE is not presence of applied migrations, and the selected tables have
        DIFFERENT introduction points in the real migration history:

          controlled_attachments                20260713003618_AddEnterpriseHardeningControl
          managed_document_revisions            20260806032704_AddManagedDocumentationCenter
            (with the ReleaseCandidate*/Released* attachment columns the health query reads)
          managed_document_storage_operations   20260812172807_AddManagedDocumentAtomicStorage

        A supported schema between those points therefore has real controlled-attachment references while having
        no atomic-storage bookkeeping at all; treating that as "no storage, no inventory" silently skipped
        reference-to-evidence verification (Astra R2-1). Capabilities are derived from BOTH the catalogue and the
        history, and every contradiction is refused: a table without its migration, a migration without its
        table, capabilities out of their historical order, and relations without any history.

        States:
          Fresh           no migration history and no known AeroLink relation - a genuine first start
          FreshSchema     history exists with zero applied migrations and no known AeroLink relation
          PreStorage      supported older schema: attachments may exist, atomic-storage bookkeeping does not yet
          Supported       history with applied migrations, programs and the capability tables present
          PartialOrCorrupt any other combination (relations without history, history without its tables, ...)

        An answer that is absent, malformed, non-binary or unreadable throws a named failure. Callers must not
        treat a query failure, a partial schema, or an unknown state as an empty database.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Psql,[Parameter(Mandatory)][string]$Database,[int]$Port=54329)
    # Two probes, deliberately. PostgreSQL resolves table references when a statement is analysed, so a
    # CASE-guarded `SELECT count(*) FROM "__EFMigrationsHistory"` fails on a database where that table does not
    # exist yet - which is exactly the first-start case. Probe 1 asks the catalogue only; probe 2 runs the
    # counts only when the history table exists.
    $coreList = (@($script:CoreRelationNames | ForEach-Object { "'" + $_ + "'" }) -join ',')
    $presenceSql = @'
SELECT
 (CASE WHEN to_regclass('public."__EFMigrationsHistory"') IS NULL THEN '0' ELSE '1' END) || ',' ||
 (CASE WHEN to_regclass('public.programs') IS NULL THEN '0' ELSE '1' END) || ',' ||
 (CASE WHEN to_regclass('public.controlled_attachments') IS NULL THEN '0' ELSE '1' END) || ',' ||
 (CASE WHEN to_regclass('public.managed_document_revisions') IS NULL THEN '0' ELSE '1' END) || ',' ||
 (CASE WHEN to_regclass('public.managed_document_storage_operations') IS NULL THEN '0' ELSE '1' END) || ',' ||
 (SELECT count(*)::text FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
   WHERE n.nspname = 'public' AND c.relkind = 'r' AND c.relname IN (CORE_LIST))
'@
    $presenceSql = $presenceSql.Replace('CORE_LIST', $coreList)
    $presenceText = (Get-AeroLinkEvidenceSqlText -Psql $Psql -Database $Database -Port $Port -Sql $presenceSql -OutputArguments @('-tA')).Trim()
    if ($presenceText -notmatch '^[01],[01],[01],[01],[01],[0-9]+$') {
        throw "The schema of database '$Database' could not be classified: the catalogue probe answered '$presenceText'."
    }
    $presence = $presenceText.Split(',')
    $historyPresent = $presence[0] -eq '1'
    $programPresent = $presence[1] -eq '1'
    $attachmentsPresent = $presence[2] -eq '1'
    $documentRevisionsPresent = $presence[3] -eq '1'
    $storageOperationsPresent = $presence[4] -eq '1'
    $coreRelationCount = [int]$presence[5]
    $historyCount = 0
    $attachmentsMigrationApplied = $false
    $documentLifecycleMigrationApplied = $false
    $atomicStorageMigrationApplied = $false
    if ($historyPresent) {
        $countSql = @'
SELECT (SELECT count(*)::text FROM "__EFMigrationsHistory") || ',' ||
 (SELECT count(*)::text FROM "__EFMigrationsHistory" WHERE "MigrationId" = 'ATTACHMENTS_MIGRATION') || ',' ||
 (SELECT count(*)::text FROM "__EFMigrationsHistory" WHERE "MigrationId" = 'LIFECYCLE_MIGRATION') || ',' ||
 (SELECT count(*)::text FROM "__EFMigrationsHistory" WHERE "MigrationId" = 'ATOMIC_MIGRATION')
'@
        $countSql = $countSql.Replace('ATTACHMENTS_MIGRATION', $script:AttachmentsMigrationId).Replace('LIFECYCLE_MIGRATION', $script:DocumentLifecycleMigrationId).Replace('ATOMIC_MIGRATION', $script:AtomicStorageMigrationId)
        $countText = (Get-AeroLinkEvidenceSqlText -Psql $Psql -Database $Database -Port $Port -Sql $countSql -OutputArguments @('-tA')).Trim()
        if ($countText -notmatch '^[0-9]+,[0-9]+,[0-9]+,[0-9]+$') {
            throw "The schema of database '$Database' could not be classified: the migration-history probe answered '$countText'."
        }
        $countParts = $countText.Split(',')
        $historyCount = [int]$countParts[0]
        $attachmentsMigrationApplied = [int]$countParts[1] -gt 0
        $documentLifecycleMigrationApplied = [int]$countParts[2] -gt 0
        $atomicStorageMigrationApplied = [int]$countParts[3] -gt 0
    }
    $capabilityTablePresent = $attachmentsPresent -or $documentRevisionsPresent -or $storageOperationsPresent
    $state = 'PartialOrCorrupt'; $detail = ''
    if (-not $historyPresent) {
        if ($coreRelationCount -eq 0) { $state = 'Fresh'; $detail = 'no migration history and no known AeroLink relations' }
        else { $detail = "no migration history, but $coreRelationCount known AeroLink relation(s) exist" }
    }
    elseif ($historyCount -eq 0) {
        if ($attachmentsMigrationApplied -or $documentLifecycleMigrationApplied -or $atomicStorageMigrationApplied -or $capabilityTablePresent) {
            $detail = 'a zero-migration history is contradicted by applied-migration counts or capability tables'
        }
        elseif ($coreRelationCount -eq 0) { $state = 'FreshSchema'; $detail = 'a migration history exists with no applied migration and no known AeroLink relations' }
        else { $detail = "a zero-migration history exists but $coreRelationCount known AeroLink relation(s) exist" }
    }
    elseif ($atomicStorageMigrationApplied -and -not $documentLifecycleMigrationApplied) {
        $detail = 'atomic-storage bookkeeping is recorded without the managed-document lifecycle migration it depends on'
    }
    elseif ($documentLifecycleMigrationApplied -and -not $attachmentsMigrationApplied) {
        $detail = 'the managed-document lifecycle migration is recorded without the earlier attachment migration'
    }
    elseif ($attachmentsPresent -ne $attachmentsMigrationApplied) {
        $detail = "the attachment table's presence ($attachmentsPresent) does not match its migration history ($attachmentsMigrationApplied)"
    }
    elseif ($documentRevisionsPresent -ne $documentLifecycleMigrationApplied) {
        $detail = "the managed-document revision table's presence ($documentRevisionsPresent) does not match its migration history ($documentLifecycleMigrationApplied)"
    }
    elseif ($storageOperationsPresent -ne $atomicStorageMigrationApplied) {
        $detail = "the atomic-storage operations table's presence ($storageOperationsPresent) does not match its migration history ($atomicStorageMigrationApplied)"
    }
    elseif (-not $programPresent) {
        $detail = "the migration history records $historyCount applied migration(s) but the application root table (programs) is absent"
    }
    elseif (-not $atomicStorageMigrationApplied) {
        $state = 'PreStorage'; $detail = "the migration history records $historyCount applied migration(s) and predates the atomic-storage migration; attachment inventory still applies"
    }
    else {
        $state = 'Supported'; $detail = "the migration history records $historyCount applied migration(s) including the atomic-storage migration"
    }
    return [pscustomobject]@{ State = $state; Detail = $detail; HistoryPresent = $historyPresent; HistoryCount = $historyCount
        ProgramPresent = $programPresent; CoreRelationCount = $coreRelationCount
        AttachmentsPresent = $attachmentsPresent; AttachmentsExpected = $attachmentsMigrationApplied
        DocumentRevisionsPresent = $documentRevisionsPresent; DocumentLifecycleExpected = $documentLifecycleMigrationApplied
        StorageOperationsPresent = $storageOperationsPresent; AtomicStorageExpected = $atomicStorageMigrationApplied
        StorageMigrationApplied = $atomicStorageMigrationApplied }
}

function Get-AeroLinkAttachmentInventory {
    param([Parameter(Mandatory)][string]$Psql,[Parameter(Mandatory)][string]$Database,[int]$Port=54329)
    # A database with no applied schema, or one that genuinely predates the attachment migration, has no
    # controlled attachments to inventory; the verified backup of a first start must not query a table that does
    # not exist yet (#1055). The CAPABILITY decides, not the state name: a supported older schema that has the
    # attachment table is inventoried even when atomic-storage bookkeeping does not exist yet (Astra R2-1), and
    # anything partial, malformed or unreadable fails closed in the classifier.
    $schema = Get-AeroLinkDatabaseSchemaState -Psql $Psql -Database $Database -Port $Port
    if ($schema.State -eq 'PartialOrCorrupt') { throw "The controlled-attachment inventory refused database '$Database': $($schema.Detail)." }
    if (-not $schema.AttachmentsPresent) {
        if ($schema.State -in @('Fresh','FreshSchema')) {
            Write-Host "No AeroLink schema has been applied to '$Database' yet (first start): there are no controlled attachments to inventory."
            return @()
        }
        if ($schema.State -eq 'PreStorage') {
            Write-Host "Database '$Database' predates the controlled-attachment migration: there are no controlled attachments to inventory before the upgrade."
            return @()
        }
        throw "The controlled-attachment inventory refused database '$Database': $($schema.Detail)."
    }
    $sql = 'COPY (SELECT "Id", "StorageKey", "Size", lower("Sha256") AS "Sha256", "ArtifactType", "ArtifactId", "RevisionId" FROM controlled_attachments ORDER BY "StorageKey", "Id") TO STDOUT WITH (FORMAT CSV, HEADER TRUE)'
    $csv = Get-AeroLinkEvidenceSqlText -Psql $Psql -Database $Database -Port $Port -Sql $sql
    return @($csv | ConvertFrom-Csv)
}

function Test-AeroLinkAttachmentInventory {
    param([Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Inventory,[Parameter(Mandatory)][string]$EvidenceRoot)
    $root = [IO.Path]::GetFullPath($EvidenceRoot); $prefix = $root + [IO.Path]::DirectorySeparatorChar
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $verifiedBytes = [long]0
    foreach ($entry in $Inventory) {
        $key = ([string]$entry.StorageKey).Replace('/', [IO.Path]::DirectorySeparatorChar)
        if ([string]::IsNullOrWhiteSpace($key) -or [IO.Path]::IsPathRooted($key) -or $key -split '[\\/]' -contains '..') { throw "Unsafe attachment storage key: $($entry.StorageKey)" }
        $path = [IO.Path]::GetFullPath((Join-Path $root $key))
        if (-not $path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw "Attachment storage key escapes the evidence root: $($entry.StorageKey)" }
        $path = ConvertTo-AeroLinkArchiveIoPath $path
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Referenced evidence object is missing: $($entry.StorageKey) (attachment $($entry.Id))" }
        $size = (Get-Item -LiteralPath $path).Length; if ($size -ne [long]$entry.Size) { throw "Referenced evidence size mismatch: $($entry.StorageKey); expected $($entry.Size), found $size" }
        $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant(); if ($hash -ne ([string]$entry.Sha256).ToLowerInvariant()) { throw "Referenced evidence hash mismatch: $($entry.StorageKey); expected $($entry.Sha256), found $hash" }
        [void]$seen.Add([string]$entry.StorageKey); $verifiedBytes += $size
    }
    $ioRoot = ConvertTo-AeroLinkArchiveIoPath $root
    $allObjects = if (Test-Path -LiteralPath $ioRoot) { @(Get-ChildItem -LiteralPath $ioRoot -File -Recurse | ForEach-Object { $_.FullName.Substring($ioRoot.Length).TrimStart([char[]]@('\','/')).Replace('\','/') }) } else { @() }
    $unreferenced = @($allObjects | Where-Object { -not $seen.Contains($_) })
    return [pscustomobject]@{ ReferencedObjects=$seen.Count; ReferencedAttachments=$Inventory.Count; VerifiedBytes=$verifiedBytes; UnreferencedObjects=$unreferenced }
}

function Assert-AeroLinkStorageLifecycleHealthy {
    param([Parameter(Mandatory)][string]$Psql,[Parameter(Mandatory)][string]$Database,[int]$Port=54329)
    # A database that has never had the AeroLink schema applied - a genuine first start - has no controlled
    # storage to verify; so does a supported older schema that predates the ATOMIC-STORAGE bookkeeping (its
    # attachment inventory is still taken by the reader above). Measured in the #1055 INT first start: the
    # launcher's verified-backup step refused an empty cluster before the schema it was about to create existed.
    # Everything partial, malformed or unreadable fails closed in the classifier.
    $schema = Get-AeroLinkDatabaseSchemaState -Psql $Psql -Database $Database -Port $Port
    if ($schema.State -eq 'PartialOrCorrupt') { throw "The controlled-document storage check refused database '$Database': $($schema.Detail)." }
    if (-not $schema.StorageOperationsPresent) {
        if ($schema.State -in @('Fresh','FreshSchema')) {
            Write-Host "No AeroLink schema has been applied to '$Database' yet (first start): there is no controlled-document storage to verify."
            return
        }
        if ($schema.State -eq 'PreStorage') {
            Write-Host "Database '$Database' predates the atomic-storage migration: storage lifecycle health is inapplicable before the upgrade."
            return
        }
        throw "The controlled-document storage check refused database '$Database': $($schema.Detail)."
    }
    $sql = @'
SELECT
 (SELECT count(*) FROM managed_document_storage_operations WHERE "State" IN ('Pending','RepairRequired')) AS pending,
 (SELECT count(*) FROM managed_document_revisions WHERE ("ReleaseCandidateDocxAttachmentId" IS NULL) <> ("ReleaseCandidatePdfAttachmentId" IS NULL)) AS partial_candidates,
 (SELECT count(*) FROM managed_document_revisions WHERE "State" = 'Released' AND (("ReleasedDocxAttachmentId" IS NULL) OR ("ReleasedPdfAttachmentId" IS NULL))) AS incomplete_releases;
'@
    $value = (Get-AeroLinkEvidenceSqlText -Psql $Psql -Database $Database -Port $Port -Sql $sql -OutputArguments @('-tA', '-F', ',')).Trim()
    if ($value -notmatch '^\d+,\d+,\d+$') { throw "Could not evaluate managed-document storage health in database '$Database'." }
    $parts = $value.Split(','); if ([int]$parts[0] -ne 0 -or [int]$parts[1] -ne 0 -or [int]$parts[2] -ne 0) { throw "Managed-document storage is not backup/restore ready: pending=$($parts[0]), partialCandidates=$($parts[1]), incompleteReleases=$($parts[2])." }
}

function Copy-AeroLinkEvidenceTree {
    param([Parameter(Mandatory)][string]$Source,[Parameter(Mandatory)][string]$Destination)
    $sourcePath=[IO.Path]::GetFullPath($Source);$destinationPath=[IO.Path]::GetFullPath($Destination)
    if(-not(Test-Path -LiteralPath $sourcePath -PathType Container)){throw "Evidence source directory is missing: $sourcePath"}
    New-Item -ItemType Directory -Path $destinationPath -Force|Out-Null
    & robocopy.exe $sourcePath $destinationPath /E /COPY:DAT /DCOPY:DAT /R:1 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
    if($LASTEXITCODE -ge 8){throw "Evidence copy failed with robocopy exit code $LASTEXITCODE."}
    $global:LASTEXITCODE=0
}

Export-ModuleMember -Function Get-AeroLinkEvidenceRoot,Get-AeroLinkDatabaseSchemaState,Get-AeroLinkEvidenceSqlText,Get-AeroLinkAttachmentInventory,Test-AeroLinkAttachmentInventory,Assert-AeroLinkStorageLifecycleHealthy,Copy-AeroLinkEvidenceTree
