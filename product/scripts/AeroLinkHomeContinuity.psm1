$ErrorActionPreference = 'Stop'

function Get-AeroLinkHomeContinuityContext {
    <# Read-only admission of the genuine HOME layout for an isolated continuity restore, never Site apply. #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Sha256)

    function Assert-HomePath([string]$Value, [bool]$Directory) {
        if ([string]::IsNullOrWhiteSpace($Value) -or -not [IO.Path]::IsPathRooted($Value)) { throw 'HOME context paths must be absolute.' }
        $full = [IO.Path]::GetFullPath($Value)
        $kind = if ($Directory) { 'Container' } else { 'Leaf' }
        if (-not (Test-Path -LiteralPath $full -PathType $kind)) { throw 'A HOME context path is missing.' }
        for ($current = $full; $current; $current = Split-Path -Parent $current) {
            if ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'HOME context paths must not traverse reparse points.' }
        }
        return $full
    }
    $contextPath = Assert-HomePath $Path $false
    if ($Sha256 -notmatch '^[a-fA-F0-9]{64}$' -or (Get-FileHash -LiteralPath $contextPath -Algorithm SHA256).Hash -ne $Sha256) { throw 'The HOME continuity context hash does not match.' }
    $context = Get-Content -LiteralPath $contextPath -Raw | ConvertFrom-Json
    if ($context.SchemaVersion -ne 1) { throw 'The HOME continuity context version is unsupported.' }
    $root = Assert-HomePath $context.SourceInstallationRoot $true
    $sourceProduct = Assert-HomePath $context.SourceProductRoot $true
    $work = Assert-HomePath $context.WorkRoot $true
    $ring = Assert-HomePath $context.SourceRingDirectory $true
    $sourceEvidence = Assert-HomePath $context.SourceEvidenceRoot $true
    $archive = Assert-HomePath $context.VerifiedBackupArchivePath $false
    if ($work.Length -gt 60) { throw 'HOME continuity requires a deliberately short operation root (at most 60 characters).' }
    $declaration = Assert-HomePath (Join-Path $root 'instance.json') $false
    $instance = Get-Content -LiteralPath $declaration -Raw | ConvertFrom-Json
    $expectedInstance = [Guid]::Parse($context.SourceInstanceId)
    if ($expectedInstance -eq [Guid]::Empty -or $instance.classification -cne 'HomeCanonical' -or [Guid]::Parse($instance.instanceId) -ne $expectedInstance) { throw 'The HOME source instance/classification does not match.' }
    $reader = [Security.Principal.WindowsIdentity]::GetCurrent()
    try { if ($reader.User.Value -ne $context.SourceUserSid) { throw 'HOME continuity must run as the captured original Windows user.' } }
    finally { $reader.Dispose() }
    $pointerPath = Assert-HomePath (Join-Path $sourceProduct '.local\installation.json') $false
    $pointer = Get-Content -LiteralPath $pointerPath -Raw | ConvertFrom-Json
    if (-not [string]::Equals([IO.Path]::GetFullPath($pointer.installationRoot).TrimEnd('\','/'), $root.TrimEnd('\','/'), [StringComparison]::OrdinalIgnoreCase)) { throw 'The genuine HOME source pointer does not match its installation.' }
    $restoreScript = Assert-HomePath $context.RestoreScriptPath $false
    if ((Split-Path -Leaf $restoreScript) -cne 'Restore-AeroLink.ps1' -or (Get-FileHash -LiteralPath $restoreScript -Algorithm SHA256).Hash -ne $context.RestoreScriptSha256) { throw 'The HOME restore authority hash does not match.' }
    foreach ($protected in @($root, $sourceProduct, $ring, $sourceEvidence, (Split-Path -Parent $archive), (Split-Path -Parent (Split-Path -Parent $restoreScript)), (Split-Path -Parent $context.SourceConnectorPath))) {
        $left = $work.TrimEnd('\','/'); $right = [IO.Path]::GetFullPath($protected).TrimEnd('\','/')
        if ([string]::Equals($left, $right, [StringComparison]::OrdinalIgnoreCase) -or $left.StartsWith("$right\", [StringComparison]::OrdinalIgnoreCase) -or $right.StartsWith("$left\", [StringComparison]::OrdinalIgnoreCase)) { throw 'The HOME continuity work root overlaps a protected source, key or tool path.' }
    }
    foreach ($child in @('restore-work','restore-validation','backup-verification')) {
        $childPath = Join-Path $work $child
        if (Test-Path -LiteralPath $childPath) { [void](Assert-HomePath $childPath $true) }
    }
    # This bounded HOME route does not claim general deep-path support (#1667). Preflight derived
    # verification/extraction/evidence paths and raw WinPS journal/receipt atomic temporary names.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $db = 'aerolink_continuity_restore_' + ('x' * 12)
    $validation = Join-Path $work 'restore-validation'
    $journal = Join-Path $validation "$db.operation.json"
    $paths = @($archive, "$archive.sha256", "$journal.receipt.json.writing-$('x' * 32)",
        (Join-Path $validation "$db.continuity.json.writing-$('x' * 32)"))
    $staging = @((Join-Path $work "backup-verification\$('x' * 32)"),
        (Join-Path $work "restore-work\$('x' * 32)"),
        (Join-Path $validation "$db\evidence"),
        (Join-Path $validation "$db\.restore-incoming-$('x' * 12)"),
        (Join-Path $validation "$db\.restore-retained-$('x' * 12)"))
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        $manifestEntry = $zip.GetEntry('manifest.json')
        if (-not $manifestEntry) { throw 'The HOME archive has no manifest.' }
        $manifestReader = [IO.StreamReader]::new($manifestEntry.Open())
        try { $manifest = $manifestReader.ReadToEnd() | ConvertFrom-Json } finally { $manifestReader.Dispose() }
        if ($manifest.FormatVersion -ne 2 -or $manifest.Instance.Classification -cne 'HomeCanonical' -or [Guid]::Parse($manifest.Instance.InstanceId) -ne $expectedInstance) { throw 'The HOME archive does not match its source context.' }
        foreach ($entry in $zip.Entries) {
            foreach ($stage in $staging) {
                if ((Join-Path $stage $entry.FullName).Length -gt 240) { throw 'A derived HOME archive/evidence path exceeds the WinPS 240-character operation budget.' }
            }
        }
    } finally { $zip.Dispose() }
    if (@($paths | Where-Object { $_.Length -gt 240 }).Count) { throw 'A derived HOME journal/receipt/archive path exceeds the WinPS operation budget.' }
    return $context
}

Export-ModuleMember -Function Get-AeroLinkHomeContinuityContext
