Set-StrictMode -Version Latest

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

function Get-AeroLinkAttachmentInventory {
    param([Parameter(Mandatory)][string]$Psql,[Parameter(Mandatory)][string]$Database,[int]$Port=54329)
    $sql = 'COPY (SELECT "Id", "StorageKey", "Size", lower("Sha256") AS "Sha256", "ArtifactType", "ArtifactId", "RevisionId" FROM controlled_attachments ORDER BY "StorageKey", "Id") TO STDOUT WITH (FORMAT CSV, HEADER TRUE)'
    $csv = $sql | & $Psql -h 127.0.0.1 -p $Port -U postgres -d $Database -v ON_ERROR_STOP=1 -f -
    if ($LASTEXITCODE -ne 0) { throw "Could not read the controlled-attachment inventory from database '$Database'." }
    return @($csv | ConvertFrom-Csv)
}

function Test-AeroLinkAttachmentInventory {
    param([Parameter(Mandatory)][object[]]$Inventory,[Parameter(Mandatory)][string]$EvidenceRoot)
    $root = [IO.Path]::GetFullPath($EvidenceRoot); $prefix = $root + [IO.Path]::DirectorySeparatorChar
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $verifiedBytes = [long]0
    foreach ($entry in $Inventory) {
        $key = ([string]$entry.StorageKey).Replace('/', [IO.Path]::DirectorySeparatorChar)
        if ([string]::IsNullOrWhiteSpace($key) -or [IO.Path]::IsPathRooted($key) -or $key -split '[\\/]' -contains '..') { throw "Unsafe attachment storage key: $($entry.StorageKey)" }
        $path = [IO.Path]::GetFullPath((Join-Path $root $key))
        if (-not $path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw "Attachment storage key escapes the evidence root: $($entry.StorageKey)" }
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Referenced evidence object is missing: $($entry.StorageKey) (attachment $($entry.Id))" }
        $size = (Get-Item -LiteralPath $path).Length; if ($size -ne [long]$entry.Size) { throw "Referenced evidence size mismatch: $($entry.StorageKey); expected $($entry.Size), found $size" }
        $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant(); if ($hash -ne ([string]$entry.Sha256).ToLowerInvariant()) { throw "Referenced evidence hash mismatch: $($entry.StorageKey); expected $($entry.Sha256), found $hash" }
        [void]$seen.Add([string]$entry.StorageKey); $verifiedBytes += $size
    }
    $allObjects = if (Test-Path -LiteralPath $root) { @(Get-ChildItem -LiteralPath $root -File -Recurse | ForEach-Object { $_.FullName.Substring($root.Length).TrimStart([char[]]@('\','/')).Replace('\','/') }) } else { @() }
    $unreferenced = @($allObjects | Where-Object { -not $seen.Contains($_) })
    return [pscustomobject]@{ ReferencedObjects=$seen.Count; ReferencedAttachments=$Inventory.Count; VerifiedBytes=$verifiedBytes; UnreferencedObjects=$unreferenced }
}

function Assert-AeroLinkStorageLifecycleHealthy {
    param([Parameter(Mandatory)][string]$Psql,[Parameter(Mandatory)][string]$Database,[int]$Port=54329)
    $sql = @'
SELECT
 (SELECT count(*) FROM managed_document_storage_operations WHERE "State" IN ('Pending','RepairRequired')) AS pending,
 (SELECT count(*) FROM managed_document_revisions WHERE ("ReleaseCandidateDocxAttachmentId" IS NULL) <> ("ReleaseCandidatePdfAttachmentId" IS NULL)) AS partial_candidates,
 (SELECT count(*) FROM managed_document_revisions WHERE "State" = 'Released' AND (("ReleasedDocxAttachmentId" IS NULL) OR ("ReleasedPdfAttachmentId" IS NULL))) AS incomplete_releases;
'@
    $raw = $sql | & $Psql -h 127.0.0.1 -p $Port -U postgres -d $Database -v ON_ERROR_STOP=1 -tA -F ',' -f -
    if ($LASTEXITCODE -ne 0) { throw "Could not evaluate managed-document storage health in database '$Database'." }
    $value = ([string]$raw).Trim()
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
