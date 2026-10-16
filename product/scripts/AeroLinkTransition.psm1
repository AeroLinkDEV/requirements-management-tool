#Requires -Version 5.1
Set-StrictMode -Version Latest

# A synchronous helper invoked with the call operator runs in this PowerShell process. Keep the
# authenticated owner handle in module scope so that helper can reuse it without asking Windows for a
# second write handle to the same lock file. The file-backed record remains the authority for descendants
# and independently started coordinators.
$script:AeroLinkTransitionLeases = @{}

function Get-AeroLinkTransitionDigest([string]$Value) {
    $hash = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($Value))).Replace('-', '') }
    finally { $hash.Dispose() }
}

function Enter-AeroLinkTransition {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$InstallationRoot,
        [ValidateSet('Preserve', 'KeepReady')][string]$Policy = 'Preserve',
        [string]$OperationId,
        [string]$Phase = 'Transition'
    )
    if ([string]::IsNullOrWhiteSpace($OperationId)) { $OperationId = [guid]::NewGuid().ToString('N') }
    if ([string]::IsNullOrWhiteSpace($Phase)) { throw 'A transition phase is required.' }
    $root = [IO.Path]::GetFullPath($InstallationRoot).TrimEnd('\', '/')
    $directory = Join-Path $root 'bootstrap'
    if (-not (Test-Path -LiteralPath $directory)) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }
    $path = Join-Path $directory 'home-transition.lock'
    $previous = $env:AEROLINK_TRANSITION_LEASE
    $previousJournal = $env:AEROLINK_TRANSITION_JOURNAL

    # Reuse the authenticated outer lease for an in-process nested helper. Do this before opening the
    # lock path: the owner's FileShare.Read handle intentionally refuses a second ReadWrite handle.
    $local = $script:AeroLinkTransitionLeases[$root]
    if ($local -and -not $previous) { throw 'An authenticated transition lease is already active in this process, but the continuation capability is missing.' }
    if ($local -and $previous) {
        try {
            $inherited = $previous | ConvertFrom-Json
            $self = Get-CimInstance Win32_Process -Filter "ProcessId=$PID" -ErrorAction Stop
            if ((Get-AeroLinkTransitionDigest $inherited.token) -ne $local.TokenHash -or
                -not $local.Stream -or -not $local.Stream.CanRead -or
                $local.LeasePath -ine $path -or [string]::IsNullOrWhiteSpace($local.OperationId) -or
                [string]::IsNullOrWhiteSpace($local.Phase) -or $local.ProcessId -ne $PID -or
                -not $self -or -not $self.CreationDate -or
                ([DateTimeOffset]$self.CreationDate).UtcDateTime.Ticks -ne $local.CreatedAtTicks) {
                throw 'The active in-process lease capability or process identity does not match.'
            }
            return [pscustomobject]@{ Stream = $null; WitnessPath = $null; Previous = $previous; PreviousJournal = $previousJournal; Policy = $local.Policy; Root = $root; Owner = $false; Shared = $true; Pending = $null; OperationId = $local.OperationId; OwnerProcessId = $local.ProcessId; OwnerCreatedAt = $local.OwnerCreatedAt; Phase = $local.Phase; LeasePath = $local.LeasePath }
        }
        catch { throw "The active transition lease could not be reused by this helper. $($_.Exception.Message)" }
    }
    $stream = $null
    try { $stream = [IO.File]::Open($path, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::Read) }
    catch [IO.IOException] {
        # The parent retains its open OS lease across a synchronous fresh-process handoff. Only descendants
        # carrying its per-run capability may continue; an unrelated recovery/manual invocation fails promptly.
        try {
            # The outer owner deliberately grants concurrent readers. A same-process nested helper (the site
            # updater calling Backup-AeroLink.ps1) must authenticate and reuse that lease rather than trying a
            # second read/write handle. A descendant gets the same read-only observation before it receives its
            # own continuation witness below.
            $readerStream = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
            $reader = New-Object IO.StreamReader($readerStream)
            try { $active = $reader.ReadToEnd() | ConvertFrom-Json }
            finally { $reader.Dispose() }
            # An unrelated invocation presents no capability at all. Say that, rather than letting a null
            # pipeline into ConvertFrom-Json report "Cannot bind argument to parameter 'InputObject'", which
            # masked the real refusal in the #1055 qualification run.
            if ([string]::IsNullOrWhiteSpace($previous)) { throw 'This invocation presented no continuation capability; only a descendant of the active transition may share its lease.' }
            $inherited = $previous | ConvertFrom-Json
            if ($active.installationRoot -ine $root -or
                (Get-AeroLinkTransitionDigest $inherited.token) -ne $active.tokenHash) { throw 'Lease capability does not match.' }
            if (-not $active.PSObject.Properties['operationId'] -or [string]::IsNullOrWhiteSpace([string]$active.operationId) -or
                -not $active.PSObject.Properties['phase'] -or [string]::IsNullOrWhiteSpace([string]$active.phase) -or
                -not $active.PSObject.Properties['leasePath'] -or [string]$active.leasePath -ine $path) {
                throw 'The active lease is missing its operation, phase or path binding.'
            }
            if ([int]$active.processId -eq $PID) {
                $self = Get-CimInstance Win32_Process -Filter "ProcessId=$PID" -ErrorAction Stop
                if (-not $self -or -not $self.CreationDate -or
                    ([DateTimeOffset]$self.CreationDate).UtcDateTime.Ticks -ne ([DateTimeOffset]$active.createdAt).UtcDateTime.Ticks) {
                    throw 'The active lease process identity changed.'
                }
                return [pscustomobject]@{ Stream = $null; WitnessPath = $null; Previous = $previous; PreviousJournal = $previousJournal; Policy = [string]$active.policy; Root = $root; Owner = $false; Shared = $true; Pending = $null; OperationId = [string]$active.operationId; OwnerProcessId = [int]$active.processId; OwnerCreatedAt = [string]$active.createdAt; Phase = [string]$active.phase; LeasePath = [string]$active.leasePath }
            }
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
            return [pscustomobject]@{ Stream = $witness; WitnessPath = $witnessPath; Previous = $previous; PreviousJournal = $previousJournal; Policy = [string]$active.policy; Root = $root; Owner = $false; Shared = $false; Pending = $null; OperationId = [string]$active.operationId; OwnerProcessId = [int]$active.processId; OwnerCreatedAt = [string]$active.createdAt; Phase = [string]$active.phase; LeasePath = [string]$active.leasePath }
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
            operationId = $OperationId; phase = $Phase; leasePath = $path
        } | ConvertTo-Json -Compress
        $bytes = [Text.Encoding]::UTF8.GetBytes($record)
        $stream.SetLength(0)
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush($true)
        $env:AEROLINK_TRANSITION_LEASE = @{ token = $token } | ConvertTo-Json -Compress
        $env:AEROLINK_TRANSITION_JOURNAL = $journalPath
        $script:AeroLinkTransitionLeases[$root] = [pscustomobject]@{
            TokenHash = Get-AeroLinkTransitionDigest $token
            ProcessId = $PID
            CreatedAtTicks = ([DateTimeOffset]$self.CreationDate).UtcDateTime.Ticks
            OwnerCreatedAt = ([DateTimeOffset]$self.CreationDate).UtcDateTime.ToString('o')
            Stream = $stream
            Policy = $Policy
            OperationId = $OperationId
            Phase = $Phase
            LeasePath = $path
        }
        return [pscustomobject]@{ Stream = $stream; Previous = $previous; PreviousJournal = $previousJournal; Policy = $Policy; Root = $root; Owner = $true; Shared = $false; Pending = $pending; OperationId = $OperationId; OwnerProcessId = $PID; OwnerCreatedAt = ([DateTimeOffset]$self.CreationDate).UtcDateTime.ToString('o'); Phase = $Phase; LeasePath = $path }
    }
    catch { if ($stream) { $stream.Dispose() }; [void]$script:AeroLinkTransitionLeases.Remove($root); throw }
}

function Set-AeroLinkTransitionPhase {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Lease,
        [Parameter(Mandatory)][string]$Phase
    )
    if (-not $Lease.Owner) { throw 'Only the transition owner may advance the transition phase.' }
    if ([string]::IsNullOrWhiteSpace($Phase)) { throw 'A transition phase is required.' }
    $root = [IO.Path]::GetFullPath([string]$Lease.Root).TrimEnd('\', '/')
    $local = $script:AeroLinkTransitionLeases[$root]
    if (-not $local -or $local.Stream -ne $Lease.Stream -or $local.LeasePath -ine [string]$Lease.LeasePath -or
        $local.ProcessId -ne $PID -or [string]$local.OperationId -ne [string]$Lease.OperationId) {
        throw 'The transition owner binding is no longer active.'
    }
    $record = @{
        installationRoot = $root; processId = $local.ProcessId; createdAt = $local.OwnerCreatedAt
        tokenHash = $local.TokenHash; policy = $local.Policy; operationId = $local.OperationId
        phase = $Phase; leasePath = $local.LeasePath
    } | ConvertTo-Json -Compress
    $bytes = [Text.Encoding]::UTF8.GetBytes($record)
    $Lease.Stream.SetLength(0)
    $Lease.Stream.Write($bytes, 0, $bytes.Length)
    $Lease.Stream.Flush($true)
    $local.Phase = $Phase
    $Lease.Phase = $Phase
}

function Exit-AeroLinkTransition {
    param($Lease)
    if (-not $Lease) { return }
    if ($Lease.PSObject.Properties['Shared'] -and $Lease.Shared) {
        return
    } elseif ($Lease.Owner) {
        [void]$script:AeroLinkTransitionLeases.Remove($Lease.Root)
        $env:AEROLINK_TRANSITION_LEASE = $Lease.Previous
        $env:AEROLINK_TRANSITION_JOURNAL = $Lease.PreviousJournal
        $Lease.Stream.Dispose()
    } else {
        $Lease.Stream.Dispose()
        Remove-Item -LiteralPath $Lease.WitnessPath -Force
    }
}

Export-ModuleMember -Function Enter-AeroLinkTransition, Exit-AeroLinkTransition, Set-AeroLinkTransitionPhase
