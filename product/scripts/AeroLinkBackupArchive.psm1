#Requires -Version 5.1
$ErrorActionPreference = 'Stop'

function Add-AeroLinkCompressionAssemblies {
    Add-Type -AssemblyName System.IO.Compression -ErrorAction Stop
    Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction Stop
}

function Get-AeroLinkBackupFileInventory {
    param([Parameter(Mandatory)][string]$StagingRoot)
    $root = [IO.Path]::GetFullPath($StagingRoot).TrimEnd('\', '/')
    if (-not (Test-Path -LiteralPath $root -PathType Container)) { throw "The backup staging root is missing: $root" }
    $prefixLength = $root.Length + 1
    return @(Get-ChildItem -LiteralPath $root -File -Recurse | ForEach-Object {
        [pscustomobject]@{
            Path = $_.FullName.Substring($prefixLength).Replace('\', '/')
            Size = $_.Length
            Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    })
}

function Compress-AeroLinkBackupArchive {
    param(
        [Parameter(Mandatory)][string]$SourceDirectory,
        [Parameter(Mandatory)][string]$DestinationArchive
    )
    Add-AeroLinkCompressionAssemblies
    $sourceRoot = [IO.Path]::GetFullPath($SourceDirectory).TrimEnd('\', '/')
    if (-not (Test-Path -LiteralPath $sourceRoot -PathType Container)) { throw "The backup staging root is missing: $sourceRoot" }
    $destination = [IO.Path]::GetFullPath($DestinationArchive)
    $destinationParent = Split-Path -Parent $destination
    if (-not (Test-Path -LiteralPath $destinationParent -PathType Container)) { throw "The archive destination directory is missing: $destinationParent" }
    if (Test-Path -LiteralPath $destination) { throw "The backup archive destination already exists: $destination" }
    $stream = [IO.File]::Open($destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $zip = New-Object IO.Compression.ZipArchive($stream, [IO.Compression.ZipArchiveMode]::Create, $false)
        try {
            foreach ($directory in (Get-ChildItem -LiteralPath $sourceRoot -Directory -Recurse | Sort-Object FullName)) {
                $relative = $directory.FullName.Substring($sourceRoot.Length + 1).Replace('\', '/')
                [void]$zip.CreateEntry("$relative/")
            }
            foreach ($file in (Get-ChildItem -LiteralPath $sourceRoot -File -Recurse | Sort-Object FullName)) {
                $relative = $file.FullName.Substring($sourceRoot.Length + 1).Replace('\', '/')
                $entry = $zip.CreateEntry($relative, [IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = $file.LastWriteTime
                $source = [IO.File]::OpenRead($file.FullName)
                try {
                    $target = $entry.Open()
                    try { $source.CopyTo($target) } finally { $target.Dispose() }
                }
                finally { $source.Dispose() }
            }
        }
        finally { $zip.Dispose() }
    }
    finally { $stream.Dispose() }
}

function Expand-AeroLinkBackupArchive {
    param(
        [Parameter(Mandatory)][string]$ArchivePath,
        [Parameter(Mandatory)][string]$DestinationDirectory
    )
    Add-AeroLinkCompressionAssemblies
    $archive = [IO.Path]::GetFullPath($ArchivePath)
    if (-not (Test-Path -LiteralPath $archive -PathType Leaf)) { throw "The backup archive is missing: $archive" }
    $destinationRoot = [IO.Path]::GetFullPath($DestinationDirectory)
    New-Item -ItemType Directory -Path $destinationRoot -Force | Out-Null
    $destinationRoot = [IO.Path]::GetFullPath($destinationRoot).TrimEnd('\', '/')
    $prefix = $destinationRoot + [IO.Path]::DirectorySeparatorChar
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        foreach ($zipEntry in $zip.Entries) {
            $entryPath = [string]$zipEntry.FullName
            if ($entryPath.Length -eq 0) { continue }
            if ([IO.Path]::IsPathRooted($entryPath) -or (@($entryPath -split '[\\/]') -contains '..')) { throw "Unsafe archive path: $entryPath" }
            $target = [IO.Path]::GetFullPath((Join-Path $destinationRoot ($entryPath.Replace('/', [IO.Path]::DirectorySeparatorChar))))
            if (-not $target.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw "Unsafe archive path: $entryPath" }
            if ($entryPath.EndsWith('/') -or $entryPath.EndsWith('\')) {
                if (-not (Test-Path -LiteralPath $target -PathType Container)) { New-Item -ItemType Directory -Path $target -Force | Out-Null }
                continue
            }
            $parent = Split-Path -Parent $target
            if (-not (Test-Path -LiteralPath $parent -PathType Container)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
            [IO.Compression.ZipFileExtensions]::ExtractToFile($zipEntry, $target, $true)
        }
    }
    finally { $zip.Dispose() }
}

Export-ModuleMember -Function @(
    'Get-AeroLinkBackupFileInventory',
    'Compress-AeroLinkBackupArchive',
    'Expand-AeroLinkBackupArchive'
)
