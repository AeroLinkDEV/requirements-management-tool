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

function Read-AeroLinkGitUtf8 {
    <#
        .SYNOPSIS Runs git and decodes its standard output as UTF-8, whichever PowerShell host is running.
        .DESCRIPTION
            Both supported hosts decode a native command's output through a console encoding, and they do not
            agree about it. Git emits pathnames as UTF-8 bytes, so the decoding has to be ours or a
            non-ASCII pathname arrives corrupted. StandardOutputEncoding makes that explicit and local.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [Parameter(Mandatory)][string[]]$GitArguments
    )
    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = 'git.exe'
    $startInfo.WorkingDirectory = $RepositoryRoot
    # Arguments as one quoted string, not ArgumentList: that collection is .NET Core only, and the supported
    # launcher chain includes Windows PowerShell 5.1 on .NET Framework.
    $quoted = @("-C", "`"$RepositoryRoot`"") + $GitArguments
    $startInfo.Arguments = $quoted -join ' '
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.StandardOutputEncoding = [Text.Encoding]::UTF8
    $startInfo.StandardErrorEncoding = [Text.Encoding]::UTF8
    $process = [System.Diagnostics.Process]::Start($startInfo)
    $stdout = $process.StandardOutput.ReadToEnd()
    $null = $process.StandardError.ReadToEnd()
    $process.WaitForExit()
    return [pscustomobject]@{ ExitCode = $process.ExitCode; StdOut = $stdout }
}

function Split-AeroLinkPorcelainZ {
    <#
        .SYNOPSIS Splits `git status --porcelain=v1 -z` output into entries with verbatim pathnames.
        .DESCRIPTION
            The -z format is NUL-terminated and never quotes or escapes a pathname, which is the whole reason
            for using it: a file called `réservé.tsx` or one with a space, a quote or a backslash comes back
            exactly as it is on disk. Renames and copies (R/C) emit a SECOND NUL-terminated field holding the
            original path immediately after the entry, so that field is consumed and kept — both paths belong
            to the fingerprint, and mistaking the origin path for the next entry would desynchronise the
            whole parse.
    #>
    [CmdletBinding()]
    param([AllowNull()][string]$Raw)
    if ([string]::IsNullOrEmpty($Raw)) { return @() }
    $fields = $Raw -split "`0"
    $entries = @()
    for ($index = 0; $index -lt $fields.Count; $index++) {
        $field = $fields[$index]
        if ([string]::IsNullOrEmpty($field)) { continue }
        # "XY <path>": two status characters and a space.
        if ($field.Length -lt 4) { continue }
        $status = $field.Substring(0, 2)
        $path = $field.Substring(3)
        if ($status[0] -eq 'R' -or $status[0] -eq 'C') {
            $index++
            $origin = if ($index -lt $fields.Count) { $fields[$index] } else { '' }
            $entries += [pscustomobject]@{ Status = $status; Path = $path; OriginPath = $origin }
            continue
        }
        $entries += [pscustomobject]@{ Status = $status; Path = $path; OriginPath = $null }
    }
    return ,$entries
}

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
        # Read through the same explicit process call as the status below, rather than `& git | Select-Object
        # -First 1`. Piping a native command into Select-Object -First stops the pipeline early, and under
        # PowerShell 7 that can terminate git before it exits cleanly and leave a non-zero $LASTEXITCODE — so
        # a perfectly good working tree intermittently reported "not a Git working tree" and lost its
        # identity, which forces a needless restart at best.
        $shaRun = Read-AeroLinkGitUtf8 -RepositoryRoot $RepositoryRoot -GitArguments @('rev-parse', 'HEAD')
        $sha = ($shaRun.StdOut -split "`r?`n" | Where-Object { $_ -and $_.Trim() } | Select-Object -First 1)
        if ($shaRun.ExitCode -ne 0 -or [string]::IsNullOrWhiteSpace($sha)) {
            return [pscustomobject]@{ Sha = $null; IsDirty = $null; Reusable = $false; Identity = $null; Detail = 'The source is not a Git working tree; its identity cannot be established.' }
        }
        $sha = $sha.Trim()
        # -z: NUL-separated, and pathnames are emitted verbatim rather than C-quoted. With plain --porcelain
        # a path containing a space, a quote, or any non-ASCII character comes back quoted and escaped, and
        # the naive unquoting below it could not resolve the file — so a changed file contributed only its
        # status text and later edits to it did not change the fingerprint. That is a stale process surviving
        # an edit, which is the exact failure this function exists to prevent.
        # Read git's bytes and decode them as UTF-8 ourselves rather than letting the host do it. Git writes
        # pathnames as UTF-8; Windows PowerShell 5.1 decodes native output through a code page, and the two
        # hosts disagree about how that is configured, so `réservé.tsx` arrived mangled on at least one of
        # them. A mangled name fails Test-Path, contributes only its status text, and its edits then leave
        # the source identity unchanged — a stale process surviving an edit.
        $statusRun = Read-AeroLinkGitUtf8 -RepositoryRoot $RepositoryRoot -GitArguments @('status', '--porcelain=v1', '-z')
        $statusRaw = $statusRun.StdOut
        $LASTEXITCODE = $statusRun.ExitCode
        if ($LASTEXITCODE -ne 0) {
            return [pscustomobject]@{ Sha = $sha; IsDirty = $null; Reusable = $false; Identity = $null; Detail = 'The working-tree state could not be read, so this source cannot be proven equivalent to a running process.' }
        }
        $status = @(Split-AeroLinkPorcelainZ -Raw $statusRaw)
    }
    finally { $ErrorActionPreference = $previous }

    if ($status.Count -eq 0) {
        return [pscustomobject]@{ Sha = $sha; IsDirty = $false; Reusable = $true; Identity = $sha; Detail = "Clean checkout at $($sha.Substring(0,8))." }
    }
    if ($status.Count -gt $script:AeroLinkDirtyFileLimit) {
        # Not an identity. A string like "<sha>+worktree:unfingerprintable" is stable, so two consecutive
        # launches in this state produced the same value and the second one REUSED a process whose bytes were
        # never established. Unknown source must be unreusable, not consistently unknown.
        return [pscustomobject]@{ Sha = $sha; IsDirty = $true; Reusable = $false; Identity = $null; Detail = "$($status.Count) changed paths is beyond the bounded fingerprint limit; the runtime will be restarted rather than assumed equivalent." }
    }

    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        $builder = New-Object System.Text.StringBuilder
        foreach ($entry in ($status | Sort-Object -Property Status, Path)) {
            [void]$builder.AppendLine("$($entry.Status) $($entry.Path)")
            if ($entry.OriginPath) { [void]$builder.AppendLine("from $($entry.OriginPath)") }
            # The path is verbatim from -z: no quoting to strip, no escapes to decode, so a file whose name
            # contains a space, a quote or a non-ASCII character resolves like any other.
            $full = Join-Path $RepositoryRoot $entry.Path
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
        Reusable = $true
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

            So does an unreadable connection table. `-ErrorAction SilentlyContinue` turned a denied or
            unavailable TCP enumeration into an empty result, which every caller then read as "the port is
            free" - so a source transition could rewrite a working tree while an old production API was still
            executing out of it, and topology could conclude "nothing was running" about a machine it had
            simply failed to look at. Not knowing is not the same as nothing, and it is the one answer that
            must stop a transition rather than let it proceed.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][int]$Port)
    try { $listeners = @(Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction Stop) }
    catch {
        # ONE condition converts to an empty list: the cmdlet reporting that no connection matched. Everything
        # else - access denied, a CIM transport failure, a broken provider - is unknown, and unknown is not
        # free.
        #
        # A typed `catch [CimJobException]` was wrong here and worse than no catch at all: PowerShell selects
        # a typed catch ahead of a later general one, and Get-NetTCPConnection raises that same type for
        # genuine provider and access failures as it does for "nothing matched". So every enumeration failure
        # was being converted into "the port is free", which is the exact fail-open this was written to close.
        # The condition is identified by the error's own category and identifier, not by its exception type.
        $noMatch = ($_.CategoryInfo.Category -eq 'ObjectNotFound') -or
            ($_.FullyQualifiedErrorId -match '(?i)NoMatching|ObjectNotFound') -or
            ($_.Exception.Message -match '(?i)no matching .* objects? found')
        if (-not $noMatch) {
            throw "AeroLink could not read the TCP connection table to determine what is listening on port ${Port}: $($_.Exception.Message). Nothing was concluded and nothing was stopped - an unreadable port table means unknown, never free."
        }
        $listeners = @()
    }
    if ($listeners.Count -eq 0) {
        return [pscustomobject]@{ Found = $false; Ambiguous = $false; Attributable = $false; ProcessId = $null; CommandLine = $null; ExecutablePath = $null; Detail = "Nothing is listening on port $Port." }
    }
    $owners = @($listeners | Select-Object -ExpandProperty OwningProcess -Unique)
    if ($owners.Count -ne 1) {
        return [pscustomobject]@{ Found = $true; Ambiguous = $true; Attributable = $false; ProcessId = $null; CommandLine = $null; ExecutablePath = $null; Detail = "Port $Port has $($owners.Count) distinct listening owners; ownership cannot be attributed." }
    }
    # A listener whose process cannot be read is not the same thing as a listener owned by somebody else.
    # The process may have exited between these two calls (re-running a launcher seconds after a stop), or
    # belong to another user. Both still fail closed - nothing is stopped on a guess - but the operator is
    # told which of the two it is, instead of being sent to close an application that may not exist.
    $process = Get-CimInstance Win32_Process -Filter "ProcessId=$($owners[0])" -ErrorAction SilentlyContinue
    $attributable = [bool]$process
    return [pscustomobject]@{
        Found          = $true
        Ambiguous      = $false
        Attributable   = $attributable
        ProcessId      = [int]$owners[0]
        CommandLine    = if ($process) { [string]$process.CommandLine } else { $null }
        ExecutablePath = if ($process) { [string]$process.ExecutablePath } else { $null }
        Detail         = if ($attributable) { "Port $Port is held by PID $($owners[0])." }
                         else { "Port $Port is held by PID $($owners[0]), whose command line could not be read; it may have just exited or belong to another user." }
    }
}

function Test-AeroLinkProcessOwnership {
    <#
        .SYNOPSIS Whether a process is one THIS checkout launched. Every fragment must appear.
        .DESCRIPTION
            This used to return true if ANY fragment matched, and the callers passed a generic
            'AeroLink.Api' alongside the checkout-specific project path. A second checkout's API therefore
            satisfied the generic fragment and became eligible to be stopped — precisely the "do not assume
            every AeroLink-looking process belongs to production" case the 2026-09-03 amendment names. Every
            fragment must now match, and callers pass fragments that are specific to one checkout.

            The fragment to pass is the project or client DIRECTORY, not the .csproj path. Measured on this
            machine, the process that actually holds port 5080 is the apphost, not `dotnet run`:

                C:\...\product\src\AeroLink.Api\bin\Debug\net10.0\AeroLink.Api.exe  --urls http://127.0.0.1:5097

            Its command line contains the project directory and never the .csproj path, so requiring the
            .csproj would match nothing and refuse every launch. Vite is the same shape — its command line
            carries the client root through node_modules.

            Matching is literal and case-insensitive rather than -like: a path is not a wildcard pattern, and
            a directory containing [ or ] would otherwise be silently mis-compared.
    #>
    [CmdletBinding()]
    param(
        [AllowNull()][string]$CommandLine,
        [AllowNull()][string]$ExecutablePath,
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$OwnershipFragments
    )
    if ([string]::IsNullOrWhiteSpace($CommandLine) -and [string]::IsNullOrWhiteSpace($ExecutablePath)) { return $false }
    $required = @($OwnershipFragments | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    # No fragment means no way to establish ownership. Never an implicit yes.
    if ($required.Count -eq 0) { return $false }
    $candidate = "$ExecutablePath $CommandLine"
    foreach ($fragment in $required) {
        if ($candidate.IndexOf($fragment, [StringComparison]::OrdinalIgnoreCase) -lt 0) { return $false }
    }
    return $true
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
        # Null or empty is the "this source cannot be proven" signal, and it must bind rather than throw:
        # Get-AeroLinkSourceFingerprint returns a null Identity when the worktree state is unreadable or
        # beyond the bounded limit, and PowerShell converts that null to an empty string on the way in.
        [Parameter(Mandatory)][AllowNull()][AllowEmptyString()][string]$ExpectedSourceIdentity,
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
        $detail = if ($owner.Attributable) {
            "Port $Port is occupied by another application (PID $($owner.ProcessId)). AeroLink never stops a process it does not own. Close it and run this launcher again."
        }
        else {
            "Port $Port is held by PID $($owner.ProcessId), but its command line could not be read, so AeroLink cannot tell whether it owns it. The process may have just exited, or may belong to another user. Nothing was stopped; check the port and run this launcher again."
        }
        return [pscustomobject]@{ Disposition = 'Refuse'; ProcessId = $owner.ProcessId; Detail = $detail }
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
        if ($owner.Attributable) {
            throw "Port $Port is occupied by another application (PID $($owner.ProcessId)). Close it and run this launcher again."
        }
        throw "Port $Port is held by PID $($owner.ProcessId), but its command line could not be read, so AeroLink cannot tell whether it owns it. Nothing was stopped."
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
    # Exported so the porcelain parser can be exercised directly rather than only through a fingerprint:
    # rename pairing and verbatim pathnames are exactly the parts that were wrong before.
    'Read-AeroLinkGitUtf8',
    'Split-AeroLinkPorcelainZ',
    'Get-AeroLinkSourceFingerprint',
    'Get-AeroLinkRuntimeIdentity',
    'Get-AeroLinkPortOwner',
    'Test-AeroLinkProcessOwnership',
    'Test-AeroLinkReadyEndpoint',
    'Resolve-AeroLinkRuntimeDisposition',
    'Stop-AeroLinkOwnedListener'
)
