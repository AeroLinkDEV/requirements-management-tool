#Requires -Version 5.1
Set-StrictMode -Version Latest

function Get-AeroLinkTransitionDigest([string]$Value) {
    $hash = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($Value))).Replace('-', '') }
    finally { $hash.Dispose() }
}

function Enter-AeroLinkTransition {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$InstallationRoot,
        [ValidateSet('Preserve', 'KeepReady')][string]$Policy = 'Preserve'
    )
    $root = [IO.Path]::GetFullPath($InstallationRoot).TrimEnd('\', '/')
    $directory = Join-Path $root 'bootstrap'
    if (-not (Test-Path -LiteralPath $directory)) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }
    $path = Join-Path $directory 'home-transition.lock'
    $previous = $env:AEROLINK_TRANSITION_LEASE
    $previousJournal = $env:AEROLINK_TRANSITION_JOURNAL
    $stream = $null
    try { $stream = [IO.File]::Open($path, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::Read) }
    catch [IO.IOException] {
        # The parent retains its open OS lease across a synchronous fresh-process handoff. Only descendants
        # carrying its per-run capability may continue; an unrelated recovery/manual invocation fails promptly.
        try {
            $readerStream = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
            $reader = New-Object IO.StreamReader($readerStream)
            try { $active = $reader.ReadToEnd() | ConvertFrom-Json }
            finally { $reader.Dispose() }
            $inherited = $previous | ConvertFrom-Json
            if ($active.installationRoot -ine $root -or
                (Get-AeroLinkTransitionDigest $inherited.token) -ne $active.tokenHash) { throw 'Lease capability does not match.' }
            $ancestor = $PID
            $found = $false
            for ($depth = 0; $depth -lt 32 -and $ancestor -gt 0; $depth++) {
                $process = Get-CimInstance Win32_Process -Filter "ProcessId=$ancestor" -ErrorAction Stop
                if (-not $process) { break }
                if ($ancestor -eq [int]$active.processId) {
                    $found = ([DateTimeOffset]$process.CreationDate).UtcDateTime.Ticks -eq ([DateTimeOffset]$active.createdAt).UtcDateTime.Ticks
                    break
                }
                $ancestor = [int]$process.ParentProcessId
            }
            if (-not $found) { throw 'The lease owner is not a live ancestor with the recorded start identity.' }
            # A descendant retains its own OS witness. If the parent is interrupted, a new coordinator
            # must still refuse while this child is executing the transition.
            $witnessPath = Join-Path $directory ('home-transition-' + [guid]::NewGuid().ToString('N') + '.active')
            $witness = [IO.File]::Open($witnessPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::Read)
            return [pscustomobject]@{ Stream = $witness; WitnessPath = $witnessPath; Previous = $previous; PreviousJournal = $previousJournal; Policy = [string]$active.policy; Root = $root; Owner = $false; Pending = $null }
        }
        catch { throw "Another HOME transition owns this installation, or its continuation cannot be authenticated. Retry after that invocation finishes. $($_.Exception.Message)" }
    }
    try {
        foreach ($witnessFile in @(Get-ChildItem -LiteralPath $directory -Filter 'home-transition-*.active' -File)) {
            try { $probe = [IO.File]::Open($witnessFile.FullName, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::Read) }
            catch { throw 'An interrupted coordinator still has a live continuation. Retry after that child finishes; nothing was started.' }
            $probe.Dispose()
            Remove-Item -LiteralPath $witnessFile.FullName -Force
        }
        $journalPath = Join-Path $directory 'home-transition-intent.json'
        $pending = $null
        if (Test-Path -LiteralPath $journalPath -PathType Leaf) {
            $journal = Get-Content -LiteralPath $journalPath -Raw | ConvertFrom-Json
            if ($journal.InstallationRoot -ine $root) { throw 'Interrupted transition installation binding is invalid.' }
            if (-not $journal.Obligation.Discharged -and $journal.Obligation.Stage -in @('Quiescing','Quiesced')) { $pending = $journal.Obligation }
        }
        $token = [guid]::NewGuid().ToString('N') + [guid]::NewGuid().ToString('N')
        $self = Get-CimInstance Win32_Process -Filter "ProcessId=$PID" -ErrorAction Stop
        $record = @{
            installationRoot = $root; processId = $PID
            createdAt = ([DateTimeOffset]$self.CreationDate).UtcDateTime.ToString('o')
            tokenHash = Get-AeroLinkTransitionDigest $token; policy = $Policy
        } | ConvertTo-Json -Compress
        $bytes = [Text.Encoding]::UTF8.GetBytes($record)
        $stream.SetLength(0)
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush($true)
        $env:AEROLINK_TRANSITION_LEASE = @{ token = $token } | ConvertTo-Json -Compress
        $env:AEROLINK_TRANSITION_JOURNAL = $journalPath
        return [pscustomobject]@{ Stream = $stream; Previous = $previous; PreviousJournal = $previousJournal; Policy = $Policy; Root = $root; Owner = $true; Pending = $pending }
    }
    catch { $stream.Dispose(); throw }
}

function Exit-AeroLinkTransition {
    param($Lease)
    if (-not $Lease) { return }
    if ($Lease.Owner) {
        $env:AEROLINK_TRANSITION_LEASE = $Lease.Previous
        $env:AEROLINK_TRANSITION_JOURNAL = $Lease.PreviousJournal
        $Lease.Stream.Dispose()
    } else {
        $Lease.Stream.Dispose()
        Remove-Item -LiteralPath $Lease.WitnessPath -Force
    }
}

Export-ModuleMember -Function Enter-AeroLinkTransition, Exit-AeroLinkTransition
