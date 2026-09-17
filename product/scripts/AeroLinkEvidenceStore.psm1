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

function Get-AeroLinkControlledStoragePresence {
    <#
      Which controlled-storage objects exist in this database, answered from the catalogue before any table is
      queried. A genuine first start has no AeroLink schema at all; a database with an applied migration history
      and a missing storage table is an anomaly. Callers decide what an absent object means, and every answer
      that is not the expected three flags fails closed.
    #>
    param([Parameter(Mandatory)][string]$Psql,[Parameter(Mandatory)][string]$Database,[int]$Port=54329)
    $sql = @'
SELECT (to_regclass('public.managed_document_storage_operations') IS NOT NULL)::int::text || ',' || (to_regclass('public.controlled_attachments') IS NOT NULL)::int::text || ',' || (to_regclass('public."__EFMigrationsHistory"') IS NOT NULL)::int::text
'@
    $raw = ([string](Invoke-AeroLinkEvidenceSql -Psql $Psql -Database $Database -Port $Port -Sql $sql -OutputArguments @('-tA'))).Trim()
    if ($raw -notmatch '^\d+,\d+,\d+$') { throw "Could not inspect the controlled-storage schema of database '$Database'." }
    $parts = $raw.Split(',')
    return [pscustomobject]@{ ManagedDocumentStorage = ($parts[0] -eq '1'); Attachments = ($parts[1] -eq '1'); MigrationHistory = ($parts[2] -eq '1') }
}

function Get-AeroLinkAttachmentInventory {
    param([Parameter(Mandatory)][string]$Psql,[Parameter(Mandatory)][string]$Database,[int]$Port=54329)
    # A database with no applied schema has no controlled attachments yet, and the verified backup of a first
    # start must not query a table that does not exist (#1055). A database WITH a migration history but no
    # attachment table is still an anomaly and still fails closed.
    $presence = Get-AeroLinkControlledStoragePresence -Psql $Psql -Database $Database -Port $Port
    if (-not $presence.Attachments) {
        if (-not $presence.MigrationHistory) {
            Write-Host "No AeroLink schema has been applied to '$Database' yet (first start): there are no controlled attachments to inventory."
            return @()
        }
        throw "The controlled-attachment table is missing from '$Database' even though it has an applied migration history."
    }
    $sql = 'COPY (SELECT "Id", "StorageKey", "Size", lower("Sha256") AS "Sha256", "ArtifactType", "ArtifactId", "RevisionId" FROM controlled_attachments ORDER BY "StorageKey", "Id") TO STDOUT WITH (FORMAT CSV, HEADER TRUE)'
    $csv = Invoke-AeroLinkEvidenceSql -Psql $Psql -Database $Database -Port $Port -Sql $sql
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
    # A database that has never had the AeroLink schema applied - a genuine first start - legitimately has none
    # of the controlled-document tables, so there is nothing that could be unhealthy. Measured in the #1055 INT
    # first start: the launcher's verified-backup step refused an empty cluster, and the whole first Start
    # failed ("Could not query controlled storage") before the schema it was about to create existed. Ask the
    # catalogue first; a database WITH an applied migration history but a missing storage table is still an
    # anomaly and still fails closed, as does any query that does not answer.
    $presence = Get-AeroLinkControlledStoragePresence -Psql $Psql -Database $Database -Port $Port
    if (-not $presence.ManagedDocumentStorage) {
        if (-not $presence.MigrationHistory) {
            Write-Host "No AeroLink schema has been applied to '$Database' yet (first start): there is no controlled-document storage to verify."
            return
        }
        throw "The managed-document storage tables are missing from '$Database' even though it has an applied migration history."
    }
    $sql = @'
SELECT
 (SELECT count(*) FROM managed_document_storage_operations WHERE "State" IN ('Pending','RepairRequired')) AS pending,
 (SELECT count(*) FROM managed_document_revisions WHERE ("ReleaseCandidateDocxAttachmentId" IS NULL) <> ("ReleaseCandidatePdfAttachmentId" IS NULL)) AS partial_candidates,
 (SELECT count(*) FROM managed_document_revisions WHERE "State" = 'Released' AND (("ReleasedDocxAttachmentId" IS NULL) OR ("ReleasedPdfAttachmentId" IS NULL))) AS incomplete_releases;
'@
    $raw = Invoke-AeroLinkEvidenceSql -Psql $Psql -Database $Database -Port $Port -Sql $sql -OutputArguments @('-tA', '-F', ',')
    # ([string]$null) is $null in Windows PowerShell 5.1; an empty answer must be a named contract failure,
    # never InvokeMethodOnNull (#1055 TA-2 class).
    $value = if ($null -eq $raw) { '' } else { ([string]$raw).Trim() }
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

Export-ModuleMember -Function Get-AeroLinkEvidenceRoot,Get-AeroLinkAttachmentInventory,Test-AeroLinkAttachmentInventory,Assert-AeroLinkStorageLifecycleHealthy,Copy-AeroLinkEvidenceTree
