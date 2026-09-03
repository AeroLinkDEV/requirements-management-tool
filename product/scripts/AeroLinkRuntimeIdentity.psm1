#Requires -Version 5.1
<#
    One authority for "is the AeroLink already listening on this port the one I am about to start?".

    `/health/ready` answers a different question: it proves this process can reach a database. It says nothing
    about which source revision the process was built from or which launcher mode it belongs to, and #816
    showed exactly what that costs — a healthy API from an older revision survived a repository update while
    the client moved on, and the launcher happily declared success.

    Identity here is deliberately three things, all of which must agree before a process is reused:

      1. OWNERSHIP  - the listener is an AeroLink process this repository launched, judged from its command
                      line. An unrecognized process is never stopped and never reused; it is a refusal.
      2. MODE       - LOCAL-DEV, HOME-PRODUCTION, and so on. A production API answering while development was
                      requested is a mode mismatch, not a lucky reuse.
      3. SOURCE     - the exact source identity. For a clean tree that is the commit SHA. For a dirty tree a
                      SHA proves nothing about the bytes on disk, so a conservative worktree fingerprint over
                      the changed and untracked files is folded in; when that cannot be computed cheaply the
                      answer is "unfingerprintable", which restarts rather than pretending.

    Nothing here reads or emits a secret. The runtime identity surface it consumes (`/health/identity`) is
    non-secret by construction: source SHA, mode, instance label, database NAME, start time.
#>

Set-StrictMode -Version Latest

# Above this many changed/untracked files a dirty worktree is not fingerprinted. The cost is a restart, which
# is correct behaviour; the alternative is minutes of hashing on every launch.
$script:AeroLinkDirtyFileLimit = 2000

function Get-AeroLinkSourceFingerprint {
    <#
        .SYNOPSIS The exact source identity of a checkout, honest about dirt.
        .DESCRIPTION
            Returns Sha, IsDirty, and Identity. Identity is the string a runtime must report to be considered
            the same source. For a clean tree it is the commit SHA, so nothing changes for the ordinary case.
            For a dirty tree it is "<sha>+worktree:<hash>", where the hash covers every changed and untracked
            non-ignored path together with its content, so editing a file invalidates a running process. When
            the tree is too dirty to fingerprint cheaply, Identity is "<sha>+worktree:unfingerprintable",
            which never matches a recorded identity and therefore always restarts.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$RepositoryRoot)

    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $sha = (& git -C $RepositoryRoot rev-parse HEAD 2>$null | Select-Object -First 1)
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($sha)) {
            return [pscustomobject]@{ Sha = $null; IsDirty = $null; Identity = $null; Detail = 'The source is not a Git working tree; its identity cannot be established.' }
        }
        $sha = $sha.Trim()
        $status = @(& git -C $RepositoryRoot status --porcelain 2>$null | Where-Object { $_ -and $_.Trim() })
        if ($LASTEXITCODE -ne 0) {
            return [pscustomobject]@{ Sha = $sha; IsDirty = $null; Identity = "$sha+worktree:unfingerprintable"; Detail = 'The working-tree state could not be read.' }
        }
    }
    finally { $ErrorActionPreference = $previous }

    if ($status.Count -eq 0) {
        return [pscustomobject]@{ Sha = $sha; IsDirty = $false; Identity = $sha; Detail = "Clean checkout at $($sha.Substring(0,8))." }
    }
    if ($status.Count -gt $script:AeroLinkDirtyFileLimit) {
        return [pscustomobject]@{ Sha = $sha; IsDirty = $true; Identity = "$sha+worktree:unfingerprintable"; Detail = "$($status.Count) changed paths is beyond the bounded fingerprint limit; the runtime will be restarted rather than assumed equivalent." }
    }

    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        $builder = New-Object System.Text.StringBuilder
        foreach ($entry in ($status | Sort-Object)) {
            [void]$builder.AppendLine($entry)
            # Porcelain v1: two status characters, a space, then the path (renames use " -> ").
            $path = $entry.Substring(3)
            if ($path -match ' -> ') { $path = ($path -split ' -> ')[-1] }
            $path = $path.Trim('"')
            $full = Join-Path $RepositoryRoot $path
            if (Test-Path -LiteralPath $full -PathType Leaf) {
                [void]$builder.AppendLine((Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash)
            }
            elseif (Test-Path -LiteralPath $full -PathType Container) {
                # An untracked directory is reported as one entry; fold its file list and contents in.
                foreach ($child in (Get-ChildItem -LiteralPath $full -File -Recurse -ErrorAction SilentlyContinue | Sort-Object FullName)) {
                    [void]$builder.AppendLine($child.FullName.Substring($RepositoryRoot.Length))
                    [void]$builder.AppendLine((Get-FileHash -LiteralPath $child.FullName -Algorithm SHA256).Hash)
                }
            }
            else { [void]$builder.AppendLine('absent') }
        }
        $digest = [BitConverter]::ToString($sha256.ComputeHash([Text.Encoding]::UTF8.GetBytes($builder.ToString()))).Replace('-', '').ToLowerInvariant()
    }
    finally { $sha256.Dispose() }

    return [pscustomobject]@{
        Sha      = $sha
        IsDirty  = $true
        Identity = "$sha+worktree:$($digest.Substring(0, 16))"
        Detail   = "Checkout at $($sha.Substring(0,8)) with $($status.Count) local change(s); the worktree fingerprint is part of its identity."
    }
}

function Get-AeroLinkRuntimeIdentity {
    <#
        .SYNOPSIS Reads the non-secret runtime identity a running AeroLink publishes, or $null.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$BaseUri,
        [int]$TimeoutSec = 3
    )
    try {
        $response = Invoke-RestMethod -Uri ($BaseUri.TrimEnd('/') + '/health/identity') -TimeoutSec $TimeoutSec -UseBasicParsing
        return $response
    }
    catch { return $null }
}

function Get-AeroLinkPortOwner {
    <#
        .SYNOPSIS The single process listening on a port, with its command line, or a described absence.
        .DESCRIPTION
            Ambiguity fails closed: more than one listener means ownership cannot be attributed, and nothing
            is stopped on a guess.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][int]$Port)
    $listeners = @(Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue)
    if ($listeners.Count -eq 0) {
        return [pscustomobject]@{ Found = $false; Ambiguous = $false; ProcessId = $null; CommandLine = $null; ExecutablePath = $null; Detail = "Nothing is listening on port $Port." }
    }
    $owners = @($listeners | Select-Object -ExpandProperty OwningProcess -Unique)
    if ($owners.Count -ne 1) {
        return [pscustomobject]@{ Found = $true; Ambiguous = $true; ProcessId = $null; CommandLine = $null; ExecutablePath = $null; Detail = "Port $Port has $($owners.Count) distinct listening owners; ownership cannot be attributed." }
    }
    $process = Get-CimInstance Win32_Process -Filter "ProcessId=$($owners[0])" -ErrorAction SilentlyContinue
    return [pscustomobject]@{
        Found          = $true
        Ambiguous      = $false
        ProcessId      = [int]$owners[0]
        CommandLine    = if ($process) { [string]$process.CommandLine } else { $null }
        ExecutablePath = if ($process) { [string]$process.ExecutablePath } else { $null }
        Detail         = "Port $Port is held by PID $($owners[0])."
    }
}

function Test-AeroLinkProcessOwnership {
    <#
        .SYNOPSIS Whether a command line is recognizably an AeroLink process this repository launched.
    #>
    [CmdletBinding()]
    param(
        [AllowNull()][string]$CommandLine,
        [AllowNull()][string]$ExecutablePath,
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$OwnershipFragments
    )
    if ([string]::IsNullOrWhiteSpace($CommandLine) -and [string]::IsNullOrWhiteSpace($ExecutablePath)) { return $false }
    $candidate = "$ExecutablePath $CommandLine"
    foreach ($fragment in $OwnershipFragments) {
        if ([string]::IsNullOrWhiteSpace($fragment)) { continue }
        if ($candidate -like "*$fragment*") { return $true }
    }
    return $false
}

function Resolve-AeroLinkRuntimeDisposition {
    <#
        .SYNOPSIS What a launcher should do about whatever is already on its port.
        .DESCRIPTION
            The whole reuse decision in one place, so the development launcher, the production launcher and
            remote-demo recovery cannot disagree. Outcomes:

              Free                 nothing is listening; start.
              Reuse                owned, right mode, right source, ready; leave it alone.
              RestartStale         owned AeroLink from a different source identity; stop it and start ours.
              RestartModeMismatch  owned AeroLink belonging to another launcher mode; stop it and start ours.
              RestartUnready       owned AeroLink of the right identity that is not ready; stop it and start ours.
              RestartUnidentified  owned AeroLink that publishes no identity (an older build); stop and start.
              Refuse               something we do not own, or ambiguous ownership. Never stopped.

            The probes are injectable so contract tests can drive every branch without a live API.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][int]$Port,
        [Parameter(Mandatory)][string]$BaseUri,
        [Parameter(Mandatory)][string]$ExpectedMode,
        [Parameter(Mandatory)][AllowNull()][string]$ExpectedSourceIdentity,
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$OwnershipFragments,
        [scriptblock]$PortOwnerProbe,
        [scriptblock]$RuntimeProbe,
        [scriptblock]$ReadyProbe
    )
    $owner = if ($PortOwnerProbe) { & $PortOwnerProbe $Port } else { Get-AeroLinkPortOwner -Port $Port }

    if (-not $owner.Found) {
        return [pscustomobject]@{ Disposition = 'Free'; ProcessId = $null; Detail = $owner.Detail }
    }
    if ($owner.Ambiguous) {
        return [pscustomobject]@{ Disposition = 'Refuse'; ProcessId = $null; Detail = "$($owner.Detail) AeroLink will not stop a process it cannot attribute." }
    }
    if (-not (Test-AeroLinkProcessOwnership -CommandLine $owner.CommandLine -ExecutablePath $owner.ExecutablePath -OwnershipFragments $OwnershipFragments)) {
        return [pscustomobject]@{ Disposition = 'Refuse'; ProcessId = $owner.ProcessId; Detail = "Port $Port is occupied by another application (PID $($owner.ProcessId)). AeroLink never stops a process it does not own. Close it and run this launcher again." }
    }

    $identity = if ($RuntimeProbe) { & $RuntimeProbe $BaseUri } else { Get-AeroLinkRuntimeIdentity -BaseUri $BaseUri }
    if ($null -eq $identity) {
        return [pscustomobject]@{ Disposition = 'RestartUnidentified'; ProcessId = $owner.ProcessId; Detail = "The AeroLink process on port $Port (PID $($owner.ProcessId)) publishes no runtime identity, so it cannot be proven to match this source. It will be restarted." }
    }

    $runtimeMode = [string]$identity.mode
    if (-not [string]::Equals($runtimeMode, $ExpectedMode, [StringComparison]::OrdinalIgnoreCase)) {
        return [pscustomobject]@{ Disposition = 'RestartModeMismatch'; ProcessId = $owner.ProcessId; Detail = "The AeroLink process on port $Port is running in $runtimeMode mode; $ExpectedMode was requested. It will be restarted in the requested mode." }
    }

    $runtimeIdentity = [string]$identity.sourceIdentity
    if ([string]::IsNullOrWhiteSpace($ExpectedSourceIdentity) -or [string]::IsNullOrWhiteSpace($runtimeIdentity) -or $runtimeIdentity -ne $ExpectedSourceIdentity) {
        $runtimeShort = if ([string]::IsNullOrWhiteSpace($runtimeIdentity)) { 'unknown' } else { $runtimeIdentity.Substring(0, [Math]::Min(12, $runtimeIdentity.Length)) }
        $expectedShort = if ([string]::IsNullOrWhiteSpace($ExpectedSourceIdentity)) { 'unknown' } else { $ExpectedSourceIdentity.Substring(0, [Math]::Min(12, $ExpectedSourceIdentity.Length)) }
        return [pscustomobject]@{ Disposition = 'RestartStale'; ProcessId = $owner.ProcessId; Detail = "The AeroLink process on port $Port is running source $runtimeShort; this launcher runs $expectedShort. A healthy API from another revision is stale, and it will be restarted." }
    }

    $ready = if ($ReadyProbe) { [bool](& $ReadyProbe $BaseUri) } else { Test-AeroLinkReadyEndpoint -BaseUri $BaseUri }
    if (-not $ready) {
        return [pscustomobject]@{ Disposition = 'RestartUnready'; ProcessId = $owner.ProcessId; Detail = "The AeroLink process on port $Port matches this source and mode but is not reporting ready. It will be restarted." }
    }

    return [pscustomobject]@{ Disposition = 'Reuse'; ProcessId = $owner.ProcessId; Detail = "The AeroLink process on port $Port (PID $($owner.ProcessId)) already runs this exact source in $ExpectedMode mode and is ready." }
}

function Test-AeroLinkReadyEndpoint {
    <#
        .SYNOPSIS True only when /health/ready reports ready with a connected database.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$BaseUri,
        [int]$TimeoutSec = 3
    )
    try {
        $ready = Invoke-RestMethod -Uri ($BaseUri.TrimEnd('/') + '/health/ready') -TimeoutSec $TimeoutSec -UseBasicParsing
        return ($ready.status -eq 'ready' -and $ready.database -eq 'connected')
    }
    catch { return $false }
}

function Stop-AeroLinkOwnedListener {
    <#
        .SYNOPSIS Stops the listener on a port only when it is positively identified as AeroLink-owned.
        .DESCRIPTION
            The refusal is the feature. An unrelated process on 5080 is somebody's work, and a launcher that
            kills it to free a port has done more damage than the failure it was avoiding.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][int]$Port,
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$OwnershipFragments,
        [scriptblock]$PortOwnerProbe,
        [scriptblock]$Stopper
    )
    $owner = if ($PortOwnerProbe) { & $PortOwnerProbe $Port } else { Get-AeroLinkPortOwner -Port $Port }
    if (-not $owner.Found) { return [pscustomobject]@{ Stopped = $false; ProcessId = $null; Detail = $owner.Detail } }
    if ($owner.Ambiguous) { throw "$($owner.Detail) AeroLink will not stop a process it cannot attribute." }
    if (-not (Test-AeroLinkProcessOwnership -CommandLine $owner.CommandLine -ExecutablePath $owner.ExecutablePath -OwnershipFragments $OwnershipFragments)) {
        throw "Port $Port is occupied by another application (PID $($owner.ProcessId)). Close it and run this launcher again."
    }
    if ($Stopper) { & $Stopper $owner.ProcessId }
    else {
        Stop-Process -Id $owner.ProcessId -Force
        # Long enough that a start does not race a not-quite-dead listener, which fails in a way nothing here
        # would explain.
        Start-Sleep -Milliseconds 800
    }
    return [pscustomobject]@{ Stopped = $true; ProcessId = $owner.ProcessId; Detail = "Stopped the AeroLink-owned process on port $Port (PID $($owner.ProcessId))." }
}

Export-ModuleMember -Function @(
    'Get-AeroLinkSourceFingerprint',
    'Get-AeroLinkRuntimeIdentity',
    'Get-AeroLinkPortOwner',
    'Test-AeroLinkProcessOwnership',
    'Test-AeroLinkReadyEndpoint',
    'Resolve-AeroLinkRuntimeDisposition',
    'Stop-AeroLinkOwnedListener'
)
