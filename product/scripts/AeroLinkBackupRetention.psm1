Set-StrictMode -Version Latest

function Assert-AeroLinkBackupPath {
    param([string]$Root, [string]$Path)
    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd('\','/')
    $full = [IO.Path]::GetFullPath($Path)
    if ($full -ne $rootPath -and -not $full.StartsWith($rootPath + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Backup path escapes its owned root: $full"
    }
    $cursor = $full
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            if ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Backup retention refuses a reparse point: $cursor"
            }
        }
        $parent = [IO.Path]::GetDirectoryName($cursor)
        if ($parent -eq $cursor) { break }; $cursor = $parent
    }
}

function Enter-AeroLinkBackupLock {
    param([Parameter(Mandatory)][string]$BackupRoot)
    Assert-AeroLinkBackupPath $BackupRoot $BackupRoot
    [void][IO.Directory]::CreateDirectory($BackupRoot)
    $path = Join-Path $BackupRoot '.backup.lock'
    Assert-AeroLinkBackupPath $BackupRoot $path
    try { return [IO.File]::Open($path, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None) }
    catch { throw "Another backup or retention operation owns '$BackupRoot'. Retry after it completes." }
}

function Get-AeroLinkBackupRetentionPlan {
    param([Parameter(Mandatory)][string]$BackupRoot, [ValidateRange(1,15)][int]$RetentionDays = 15, [datetime]$Now = (Get-Date))
    Assert-AeroLinkBackupPath $BackupRoot $BackupRoot
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    # Walk explicitly: never follow junctions into another installation or qualification tree.
    $pending = New-Object 'System.Collections.Generic.Queue[string]'
    $pending.Enqueue([IO.Path]::GetFullPath($BackupRoot))
    $archives = @()
    while ($pending.Count) {
        $directory = $pending.Dequeue()
        foreach ($item in Get-ChildItem -LiteralPath $directory -Force) {
            Assert-AeroLinkBackupPath $BackupRoot $item.FullName
            if ($item.PSIsContainer) { $pending.Enqueue($item.FullName); continue }
            if ($item.Name -notmatch '^aerolink-\d{8}-\d{6}\.zip$') { continue }
            $sidecar = $item.FullName + '.sha256'
            if (-not (Test-Path -LiteralPath $sidecar -PathType Leaf)) { throw "Backup sidecar missing: $sidecar" }
            Assert-AeroLinkBackupPath $BackupRoot $sidecar
            $zip = [IO.Compression.ZipFile]::OpenRead($item.FullName)
            try {
                $entry = $zip.GetEntry('manifest.json')
                if (-not $entry) { throw "Backup manifest missing: $($item.FullName)" }
                $reader = New-Object IO.StreamReader($entry.Open())
                try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
                if ($manifest.FormatVersion -ne 2 -or $manifest.Database.Name -notmatch '^[a-zA-Z][a-zA-Z0-9_]{0,62}$') { throw 'Unrecognized backup identity.' }
                $created = ([DateTimeOffset]$manifest.CreatedAtUtc).LocalDateTime
                if ($created -gt $Now.AddMinutes(5)) { throw "Future-dated backup requires operator review: $($item.FullName)" }
                $archives += [pscustomobject]@{ Path=$item.FullName; Sidecar=$sidecar; Database=[string]$manifest.Database.Name; Created=$created; Bytes=$item.Length; ModifiedUtc=$item.LastWriteTimeUtc; SidecarText=(Get-Content -LiteralPath $sidecar -Raw); Action='Delete'; Reason='Older than retention window' }
            } finally { $zip.Dispose() }
        }
    }
    # Fifteen calendar days including today: at most fifteen daily points per database.
    $cutoff = $Now.Date.AddDays(1 - $RetentionDays)
    foreach ($group in ($archives | Where-Object Created -ge $cutoff | Group-Object { $_.Database + '|' + $_.Created.ToString('yyyy-MM-dd') })) {
        $ordered = @($group.Group | Sort-Object Created,Path -Descending)
        $ordered[0].Action = 'Keep'; $ordered[0].Reason = 'Latest daily restore point'
        foreach ($extra in ($ordered | Select-Object -Skip 1)) { $extra.Reason = 'Additional same-day restore point' }
    }
    return $archives
}

function Invoke-AeroLinkBackupRetentionPlan {
    param([Parameter(Mandatory)][string]$BackupRoot, [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Plan)
    # Verify ALL survivors before deleting anything. A bad replacement must never remove the previous point.
    foreach ($entry in ($Plan | Where-Object Action -eq 'Keep')) {
        Assert-AeroLinkBackupPath $BackupRoot $entry.Path
        Assert-AeroLinkBackupPath $BackupRoot $entry.Sidecar
        $expected = ($entry.SidecarText.Trim() -split '\s+')[0]
        if ($expected -notmatch '^[a-fA-F0-9]{64}$' -or (Get-FileHash -LiteralPath $entry.Path -Algorithm SHA256).Hash -ne $expected) { throw "Retained backup checksum failed: $($entry.Path)" }
    }
    foreach ($entry in ($Plan | Where-Object Action -eq 'Delete')) {
        if (-not @($Plan | Where-Object { $_.Action -eq 'Keep' -and $_.Database -eq $entry.Database }).Count) { throw "No current recovery point for $($entry.Database); retention refused." }
    }
    # Check the whole plan for drift before the first delete, then recheck each target at action time.
    foreach ($entry in $Plan) {
        Assert-AeroLinkBackupPath $BackupRoot $entry.Path
        Assert-AeroLinkBackupPath $BackupRoot $entry.Sidecar
        $current = Get-Item -LiteralPath $entry.Path
        if ($current.Length -ne $entry.Bytes -or $current.LastWriteTimeUtc -ne $entry.ModifiedUtc -or (Get-Content -LiteralPath $entry.Sidecar -Raw) -cne $entry.SidecarText) { throw "Backup changed during retention: $($entry.Path)" }
    }
    foreach ($entry in ($Plan | Where-Object Action -eq 'Delete')) {
        Assert-AeroLinkBackupPath $BackupRoot $entry.Path
        Assert-AeroLinkBackupPath $BackupRoot $entry.Sidecar
        $current = Get-Item -LiteralPath $entry.Path
        if ($current.Length -ne $entry.Bytes -or $current.LastWriteTimeUtc -ne $entry.ModifiedUtc) { throw "Backup changed before deletion: $($entry.Path)" }
        Remove-Item -LiteralPath $entry.Path -Force -ErrorAction Stop
        Remove-Item -LiteralPath $entry.Sidecar -Force -ErrorAction Stop
    }
}
Export-ModuleMember -Function Enter-AeroLinkBackupLock,Get-AeroLinkBackupRetentionPlan,Invoke-AeroLinkBackupRetentionPlan
