#Requires -Version 5.1
<#
    Mode-aware source-posture bootstrap shared by the AeroLink launchers.

    AeroLink has two launcher modes that intentionally disagree about how strict the source posture must be:

      * HomeCanonical  - START_AEROLINK_PRODUCTION.bat (and, through it, the remote-demo stack). The canonical
        HOME database may only be exercised by a clean, known `main`. Anything else is refused before any
        product process, build, or PostgreSQL start, and Git is never mutated to make an unsafe posture go
        away.
      * Development    - START_AEROLINK.bat. Deliberate local work (feature branches, dirt, local-only commits)
        is preserved exactly; the launcher only fast-forwards a clean `main` and never polices the checkout
        into canonical-main shape.

    This module characterizes the repository BEFORE mutating anything (never `git pull` first and diagnose
    from the failure), permits exactly one kind of automatic mutation - a strictly fast-forward update of a
    clean `main` with no local-only commits - and continues legibly when GitHub is unreachable.

    Two bounded auxiliary behaviors live here because a fast-forward can change the files that are running:
    a one-shot re-entry mechanism (rerun the launch from the updated files, exactly once, marker-guarded), and
    a package-lock.json fingerprint so `npm ci` runs only when client dependency inputs actually changed.

    Every test-facing seam is a parameter: repositories, state directories, and the dependency refresh command
    are injected, so the contract suites use disposable Git fixtures and temporary state and never touch the
    persistent AeroLink database or product evidence.
#>

# The one branch an automatic update may ever move, in either mode.
$script:AeroLinkBootstrapMainBranch = 'main'

function Invoke-AeroLinkBootstrapGit {
    <#
        .SYNOPSIS Runs git in a repository and throws with the captured output when it fails.
    #>
    param(
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [Parameter(Mandatory)][string[]]$GitArguments
    )
    # Under Windows PowerShell 5.1 with a Stop preference, redirected native stderr becomes error records and
    # aborts the call. The exit code is the authority here, so the preference is relaxed around the call.
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & git -C $RepositoryRoot @GitArguments 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }
    if ($exitCode -ne 0) {
        throw "git $($GitArguments -join ' ') failed in $RepositoryRoot : $((($output | ForEach-Object { "$_" }) -join ' ').Trim())"
    }
    return ($output | ForEach-Object { "$_" }) -join "`n"
}

function Invoke-AeroLinkBootstrapGitQuiet {
    <#
        .SYNOPSIS Probing git call: returns the output, or $null when the command fails. Never throws.
    #>
    param(
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [Parameter(Mandatory)][string[]]$GitArguments
    )
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & git -C $RepositoryRoot @GitArguments 2>$null
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }
    if ($exitCode -ne 0) { return $null }
    return ($output | ForEach-Object { "$_" }) -join "`n"
}

function Get-AeroLinkRepositoryPosture {
    <#
        .SYNOPSIS Characterizes a Git working tree without mutating anything, reachable or not.
        .DESCRIPTION
            Branch, HEAD, tracked/untracked dirt, and the relationship of the current HEAD to the cached
            refs/remotes/origin/main. Reaching the remote is a separate, explicit step (see
            Sync-AeroLinkRemoteRefs); posture must be knowable offline so refusals never depend on the network.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [string]$RemoteName = 'origin'
    )

    $inside = Invoke-AeroLinkBootstrapGitQuiet -RepositoryRoot $RepositoryRoot -GitArguments @('rev-parse', '--is-inside-work-tree')
    if ($inside -ne 'true') {
        return [pscustomobject]@{
            RepositoryRoot      = $RepositoryRoot
            IsGitRepository     = $false
            Branch              = $null
            IsDetachedHead      = $false
            HeadSha             = $null
            ShortSha            = $null
            HasTrackedChanges   = $false
            UntrackedFileCount  = 0
            HasRemote           = $false
            RemoteMainSha       = $null
            ShortRemoteMainSha  = $null
            AheadOfRemoteMain   = $null
            BehindRemoteMain    = $null
            Relationship        = 'Unknown'
        }
    }

    $headSha = (Invoke-AeroLinkBootstrapGit -RepositoryRoot $RepositoryRoot -GitArguments @('rev-parse', 'HEAD')).Trim()
    $branchRef = Invoke-AeroLinkBootstrapGitQuiet -RepositoryRoot $RepositoryRoot -GitArguments @('symbolic-ref', '-q', 'HEAD')
    $isDetached = [string]::IsNullOrEmpty($branchRef)
    $branch = if ($isDetached) { 'HEAD' } else { ($branchRef.Trim() -replace '^refs/heads/', '') }

    $statusRaw = Invoke-AeroLinkBootstrapGit -RepositoryRoot $RepositoryRoot -GitArguments @('status', '--porcelain')
    $statusEntries = @(($statusRaw -split "`r?`n") | Where-Object { $_ -and $_.Trim() })
    # Untracked files are local work; a fast-forward never deletes them. Only tracked modifications are dirt.
    $trackedChanges = @($statusEntries | Where-Object { -not $_.StartsWith('??') })
    $untrackedCount = @($statusEntries | Where-Object { $_.StartsWith('??') }).Count

    $remoteUrl = Invoke-AeroLinkBootstrapGitQuiet -RepositoryRoot $RepositoryRoot -GitArguments @('remote', 'get-url', $RemoteName)
    $remoteMainSha = Invoke-AeroLinkBootstrapGitQuiet -RepositoryRoot $RepositoryRoot -GitArguments @('rev-parse', '--verify', '--quiet', "refs/remotes/$RemoteName/main")

    $ahead = $null
    $behind = $null
    $relationship = 'Unknown'
    if ($remoteMainSha) {
        $ahead = [int]((Invoke-AeroLinkBootstrapGit -RepositoryRoot $RepositoryRoot -GitArguments @('rev-list', '--count', "refs/remotes/$RemoteName/main..HEAD")).Trim())
        $behind = [int]((Invoke-AeroLinkBootstrapGit -RepositoryRoot $RepositoryRoot -GitArguments @('rev-list', '--count', "HEAD..refs/remotes/$RemoteName/main")).Trim())
        if ($ahead -eq 0 -and $behind -eq 0) { $relationship = 'Equal' }
        elseif ($ahead -gt 0 -and $behind -eq 0) { $relationship = 'Ahead' }
        elseif ($ahead -eq 0 -and $behind -gt 0) { $relationship = 'Behind' }
        else { $relationship = 'Diverged' }
    }

    return [pscustomobject]@{
        RepositoryRoot      = $RepositoryRoot
        IsGitRepository     = $true
        Branch              = $branch
        IsDetachedHead      = $isDetached
        HeadSha             = $headSha
        ShortSha            = $headSha.Substring(0, [Math]::Min(8, $headSha.Length))
        HasTrackedChanges   = ($trackedChanges.Count -gt 0)
        UntrackedFileCount  = $untrackedCount
        HasRemote           = (-not [string]::IsNullOrEmpty($remoteUrl))
        RemoteMainSha       = $remoteMainSha
        ShortRemoteMainSha  = if ($remoteMainSha) { $remoteMainSha.Substring(0, [Math]::Min(8, $remoteMainSha.Length)) } else { $null }
        AheadOfRemoteMain   = $ahead
        BehindRemoteMain    = $behind
        Relationship        = $relationship
    }
}

function Sync-AeroLinkRemoteRefs {
    <#
        .SYNOPSIS Fetches remote-tracking refs with a hard timeout. Returns $true only when the fetch succeeded.
        .DESCRIPTION
            A fetch moves remote-tracking refs only; it is the one network operation the bootstrap performs,
            and it is bounded because an unattended double-click launch must never hang on a dead network or
            a credential prompt. GIT_TERMINAL_PROMPT is disabled for the child process.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [string]$RemoteName = 'origin',
        [int]$TimeoutSeconds = 45
    )
    $previousPromptSetting = $env:GIT_TERMINAL_PROMPT
    $env:GIT_TERMINAL_PROMPT = '0'
    try {
        # System.Diagnostics.Process, not Start-Process: under Windows PowerShell 5.1 a Start-Process
        # PassThru object does not reliably expose ExitCode for redirected children, and the bootstrap must
        # know the real exit code rather than guessing.
        $startInfo = New-Object System.Diagnostics.ProcessStartInfo
        $startInfo.FileName = 'git.exe'
        $startInfo.Arguments = "-C `"$RepositoryRoot`" fetch $RemoteName --prune"
        $startInfo.WorkingDirectory = $RepositoryRoot
        $startInfo.UseShellExecute = $false
        $startInfo.RedirectStandardOutput = $true
        $startInfo.RedirectStandardError = $true
        $startInfo.CreateNoWindow = $true
        $process = [System.Diagnostics.Process]::Start($startInfo)
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            try { $process.Kill() } catch { }
            return $false
        }
        $process.WaitForExit()
        $null = $stdoutTask.Result
        $null = $stderrTask.Result
        return ($process.ExitCode -eq 0)
    }
    finally {
        $env:GIT_TERMINAL_PROMPT = $previousPromptSetting
    }
}

function Get-AeroLinkBootstrapScriptArguments {
    <#
        .SYNOPSIS Rebuilds a launcher's own bound parameters as a re-entry argument list.
        .DESCRIPTION
            The re-entry child must receive the exact mode and arguments the operator gave. Switches are
            carried only when present; value parameters are carried as name/value pairs.
    #>
    param($BoundParameters)
    $arguments = @()
    if ($BoundParameters) {
        foreach ($entry in $BoundParameters.GetEnumerator()) {
            if ($entry.Value -is [System.Management.Automation.SwitchParameter]) {
                if ($entry.Value.IsPresent) { $arguments += "-$($entry.Key)" }
            }
            else {
                $arguments += "-$($entry.Key)"
                $arguments += [string]$entry.Value
            }
        }
    }
    return ,$arguments
}
function Get-AeroLinkBootstrapFileSet {
    <#
        .SYNOPSIS Hashes the launcher implementation files whose change must trigger a bounded re-entry.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [AllowEmptyCollection()][Parameter(Mandatory)][string[]]$LauncherFiles
    )
    $entries = @()
    foreach ($relativePath in $LauncherFiles) {
        $path = Join-Path $RepositoryRoot $relativePath
        $exists = (Test-Path -LiteralPath $path -PathType Leaf)
        $hash = $null
        if ($exists) { $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
        $entries += [pscustomobject]@{ RelativePath = $relativePath; Path = $path; Exists = $exists; Hash = $hash }
    }
    return ,$entries
}

function Compare-AeroLinkBootstrapFileSet {
    <#
        .SYNOPSIS Returns the relative paths whose existence or content hash differs between two file sets.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Before,
        [Parameter(Mandatory)]$After
    )
    $changed = @()
    foreach ($beforeEntry in $Before) {
        $afterEntry = $After | Where-Object { $_.RelativePath -eq $beforeEntry.RelativePath } | Select-Object -First 1
        if ($null -eq $afterEntry) { $changed += $beforeEntry.RelativePath; continue }
        if ($beforeEntry.Exists -ne $afterEntry.Exists) { $changed += $beforeEntry.RelativePath; continue }
        if ($beforeEntry.Exists -and $beforeEntry.Hash -ne $afterEntry.Hash) { $changed += $beforeEntry.RelativePath }
    }
    return @($changed)
}

function Invoke-AeroLinkBootstrapReentry {
    <#
        .SYNOPSIS Restarts the launch from the updated launcher files, in a fresh process, exactly once.
        .DESCRIPTION
            The child process carries AEROLINK_BOOTSTRAP_REENTRY, which makes the bootstrap skip its update
            path entirely, so a defect here cannot loop. The parent waits for the child and returns its exit
            code; the caller exits with it so launch.cmd reports the true outcome.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$CurrentScriptPath,
        [AllowEmptyCollection()][string[]]$ScriptArguments = @()
    )
    $hostExecutable = (Get-Process -Id $PID).Path
    $quotedArguments = @()
    foreach ($argument in $ScriptArguments) {
        if ($argument -match '\s') { $quotedArguments += ('"' + ($argument -replace '"', '\"') + '"') }
        else { $quotedArguments += $argument }
    }
    $argumentLine = "-NoProfile -ExecutionPolicy Bypass -File `"$CurrentScriptPath`""
    if ($quotedArguments.Count -gt 0) { $argumentLine += ' ' + ($quotedArguments -join ' ') }

    Write-Host ''
    Write-Host 'The launcher implementation itself was just updated by the safe update. Restarting the launch from the updated files...' -ForegroundColor Yellow

    $env:AEROLINK_BOOTSTRAP_REENTRY = '1'
    try {
        $child = Start-Process -FilePath $hostExecutable -ArgumentList $argumentLine -NoNewWindow -PassThru -Wait
        return $child.ExitCode
    }
    finally {
        Remove-Item -Path 'Env:AEROLINK_BOOTSTRAP_REENTRY' -ErrorAction SilentlyContinue
    }
}

function Update-AeroLinkClientDependencies {
    <#
        .SYNOPSIS Runs the deterministic client dependency refresh only when dependency inputs changed.
        .DESCRIPTION
            The prepared fingerprint is the SHA-256 of product/client/package-lock.json, recorded under the
            operator state directory only after a successful refresh. A stamp that matches the current
            lockfile with node_modules present means nothing to do; anything else runs the refresh (npm ci
            by default, injectable for tests). A failed refresh propagates and never records the stamp, so
            the next launch will retry rather than falsely believe dependencies were prepared.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ClientRoot,
        [Parameter(Mandatory)][string]$StateDirectory,
        [scriptblock]$RefreshCommand
    )
    $lockfilePath = Join-Path $ClientRoot 'package-lock.json'
    if (-not (Test-Path -LiteralPath $lockfilePath -PathType Leaf)) {
        throw "package-lock.json was not found at $lockfilePath. Client dependency inputs cannot be characterized."
    }
    $fingerprint = (Get-FileHash -LiteralPath $lockfilePath -Algorithm SHA256).Hash

    $stampPath = Join-Path $StateDirectory 'client-dependencies.json'
    $stamp = $null
    if (Test-Path -LiteralPath $stampPath -PathType Leaf) {
        try { $stamp = Get-Content -LiteralPath $stampPath -Raw | ConvertFrom-Json } catch { $stamp = $null }
    }

    if ($stamp -and $stamp.lockfileSha256 -eq $fingerprint -and (Test-Path -LiteralPath (Join-Path $ClientRoot 'node_modules') -PathType Container)) {
        Write-Host '      Client dependencies already prepared (package-lock.json unchanged).' -ForegroundColor DarkGray
        return [pscustomobject]@{ Refreshed = $false; Fingerprint = $fingerprint }
    }

    if ($stamp -and $stamp.lockfileSha256 -ne $fingerprint) {
        Write-Host '      package-lock.json changed; refreshing client dependencies (npm ci)...' -ForegroundColor Yellow
    }
    else {
        Write-Host '      Preparing client dependencies (npm ci)...' -ForegroundColor Yellow
    }

    $refresh = $RefreshCommand
    if (-not $refresh) {
        $refresh = {
            param([string]$TargetClientRoot)
            Push-Location $TargetClientRoot
            try {
                & npm.cmd ci
                if ($LASTEXITCODE -ne 0) { throw "npm ci failed with exit code $LASTEXITCODE." }
            }
            finally { Pop-Location }
        }
    }

    try {
        & $refresh $ClientRoot
    }
    catch {
        throw "Client dependency refresh failed; the prepared-dependency fingerprint was NOT updated. $($_.Exception.Message)"
    }

    if (-not (Test-Path -LiteralPath $StateDirectory -PathType Container)) {
        New-Item -ItemType Directory -Path $StateDirectory -Force | Out-Null
    }
    [pscustomobject]@{
        lockfileSha256 = $fingerprint
        preparedAtUtc  = (Get-Date).ToUniversalTime().ToString('o')
    } | ConvertTo-Json | Set-Content -LiteralPath $stampPath -Encoding UTF8

    return [pscustomobject]@{ Refreshed = $true; Fingerprint = $fingerprint }
}

function Invoke-AeroLinkSourceBootstrap {
    <#
        .SYNOPSIS Applies the mode-specific source policy, then updates and re-enters when permitted.
        .DESCRIPTION
            Decision order is deliberately posture-first: characterize, decide by mode, and only then touch
            anything. The only permitted automatic Git mutation is a strictly fast-forward update of a clean
            `main` with no local-only commits, performed after the fetch so preconditions are re-derived from
            the refreshed state. Refusals happen before any product process, build, or PostgreSQL start, and
            never mutate Git.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][ValidateSet('Development', 'HomeCanonical')][string]$Mode,
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [Parameter(Mandatory)][string]$CurrentScriptPath,
        [AllowEmptyCollection()][string[]]$ScriptArguments = @(),
        [AllowEmptyCollection()][string[]]$LauncherFiles = @(),
        [int]$FetchTimeoutSeconds = 45
    )

    if ($env:AEROLINK_BOOTSTRAP_REENTRY) {
        Write-Host 'Source bootstrap: re-entry in progress; the source update step is skipped (bounded one-shot re-entry).' -ForegroundColor DarkGray
        return [pscustomobject]@{
            Action         = 'ReentryInProgress'
            HeadSha        = $null
            UpdatedToSha   = $null
            RemoteReachable = $null
            Reason         = 'The one-shot re-entry marker is present, so no further bootstrap update may run.'
        }
    }

    $posture = Get-AeroLinkRepositoryPosture -RepositoryRoot $RepositoryRoot

    if (-not $posture.IsGitRepository) {
        throw "AeroLink cannot characterize its source: $RepositoryRoot is not a Git working tree. Launch refused; nothing was changed."
    }
    if ($posture.IsDetachedHead) {
        throw "AeroLink cannot characterize its source: the repository is in detached HEAD state at $($posture.ShortSha). Check out a branch, then launch again. Nothing was changed."
    }

    $isHomeCanonical = ($Mode -eq 'HomeCanonical')
    $shortSha = $posture.ShortSha

    if ($posture.Branch -ne $script:AeroLinkBootstrapMainBranch) {
        if (-not $isHomeCanonical) {
            Write-Host "Development checkout: $($posture.Branch) @ $shortSha"
            Write-Host 'Automatic main update skipped. Your branch was left unchanged.'
            return [pscustomobject]@{
                Action = 'FeatureBranchPreserved'; HeadSha = $posture.HeadSha; UpdatedToSha = $null
                RemoteReachable = $null; Reason = "Deliberate branch $($posture.Branch) was left unchanged."
            }
        }
        Write-Host 'AEROLINK PRODUCTION START REFUSED' -ForegroundColor Red
        Write-Host "Repository is on $($posture.Branch), not canonical main." -ForegroundColor Red
        Write-Host 'No Git files or database state were changed.' -ForegroundColor Red
        throw "Production launch refused: the repository is on $($posture.Branch), not canonical main."
    }

    if ($posture.HasTrackedChanges) {
        if (-not $isHomeCanonical) {
            Write-Host "Development checkout: main @ $shortSha with local modifications."
            Write-Host 'Automatic main update skipped. Your uncommitted changes were left byte-for-byte unchanged.'
            return [pscustomobject]@{
                Action = 'LocalChangesPreserved'; HeadSha = $posture.HeadSha; UpdatedToSha = $null
                RemoteReachable = $null; Reason = 'The working tree has tracked modifications, so no update was attempted.'
            }
        }
        Write-Host 'AEROLINK PRODUCTION START REFUSED' -ForegroundColor Red
        Write-Host 'The working tree has uncommitted modifications to tracked files.' -ForegroundColor Red
        Write-Host 'No Git files or database state were changed.' -ForegroundColor Red
        throw 'Production launch refused: the working tree has uncommitted modifications to tracked files.'
    }

    if (-not $posture.HasRemote) {
        throw "AeroLink cannot characterize its source: no 'origin' remote is configured in $RepositoryRoot. Launch refused; nothing was changed."
    }

    $reached = Sync-AeroLinkRemoteRefs -RepositoryRoot $RepositoryRoot -TimeoutSeconds $FetchTimeoutSeconds
    # The fetch may have moved remote-tracking refs; the policy is decided from the refreshed posture, so a
    # precondition that "moved" is simply re-evaluated, never broadened.
    $posture = Get-AeroLinkRepositoryPosture -RepositoryRoot $RepositoryRoot

    if (-not $reached) {
        if ($posture.HasTrackedChanges) {
            # Reached only if the tree became dirty while fetching; the dirty policy above has already spoken.
            return [pscustomobject]@{
                Action = 'LocalChangesPreserved'; HeadSha = $posture.HeadSha; UpdatedToSha = $null
                RemoteReachable = $false; Reason = 'The working tree became dirty during the update attempt; nothing was changed.'
            }
        }
        if (-not $isHomeCanonical) {
            if ($null -eq $posture.RemoteMainSha) {
                Write-Host "GitHub unavailable. Continuing with local main @ $shortSha. The remote revision could not be verified and no upstream main is cached locally."
            }
            else {
                Write-Host "GitHub unavailable. Continuing with local main @ $shortSha. Latest remote revision could not be verified."
                if ($posture.Relationship -eq 'Ahead' -or $posture.Relationship -eq 'Diverged') {
                    Write-Host 'Note: local main is not identical to the last-known origin/main; nothing was merged, rebased, or reset.'
                }
            }
            return [pscustomobject]@{
                Action = 'ContinuedOffline'; HeadSha = $posture.HeadSha; UpdatedToSha = $null
                RemoteReachable = $false; Reason = 'GitHub is unavailable; the local checkout was left unchanged.'
            }
        }
        # HOME canonical offline: acceptable only for a clean, known main with no local-only commits.
        if ($null -eq $posture.RemoteMainSha) {
            Write-Host 'AEROLINK PRODUCTION START REFUSED' -ForegroundColor Red
            Write-Host "GitHub is unavailable and no cached origin/main exists, so the canonical source posture cannot be verified for main @ $shortSha." -ForegroundColor Red
            Write-Host 'No Git files or database state were changed.' -ForegroundColor Red
            throw 'Production launch refused: the canonical source posture cannot be verified offline (no cached origin/main).'
        }
        if ($posture.AheadOfRemoteMain -gt 0) {
            Write-Host 'AEROLINK PRODUCTION START REFUSED' -ForegroundColor Red
            Write-Host 'Local main contains commits that are not on the last-known origin/main, and GitHub is unavailable to verify otherwise.' -ForegroundColor Red
            Write-Host 'No Git files or database state were changed.' -ForegroundColor Red
            throw 'Production launch refused: main has local-only commits.'
        }
        Write-Host "GitHub unavailable. Running cached clean main @ $shortSha. Latest remote revision could not be verified." -ForegroundColor Yellow
        return [pscustomobject]@{
            Action = 'ContinuedOfflineCachedMain'; HeadSha = $posture.HeadSha; UpdatedToSha = $null
            RemoteReachable = $false; Reason = 'GitHub is unavailable; the cached clean main is explicitly not verified against the remote.'
        }
    }

    if ($null -eq $posture.RemoteMainSha) {
        throw "AeroLink cannot characterize its source: origin was reachable but origin/main was not found. Launch refused; nothing was changed."
    }

    if ($posture.HasTrackedChanges) {
        if (-not $isHomeCanonical) {
            Write-Host "Development checkout: main @ $shortSha with local modifications."
            Write-Host 'Automatic main update skipped. Your uncommitted changes were left byte-for-byte unchanged.'
            return [pscustomobject]@{
                Action = 'LocalChangesPreserved'; HeadSha = $posture.HeadSha; UpdatedToSha = $null
                RemoteReachable = $true; Reason = 'The working tree has tracked modifications, so no update was attempted.'
            }
        }
        Write-Host 'AEROLINK PRODUCTION START REFUSED' -ForegroundColor Red
        Write-Host 'The working tree has uncommitted modifications to tracked files.' -ForegroundColor Red
        Write-Host 'No Git files or database state were changed.' -ForegroundColor Red
        throw 'Production launch refused: the working tree has uncommitted modifications to tracked files.'
    }

    if ($posture.UntrackedFileCount -gt 0) {
        Write-Host "Note: $($posture.UntrackedFileCount) untracked local file(s) present. They are preserved and never deleted." -ForegroundColor DarkGray
    }

    switch ($posture.Relationship) {
        'Equal' {
            Write-Host "Source: main @ $shortSha"
            Write-Host 'Status: clean, current with origin/main. No update needed.'
            return [pscustomobject]@{
                Action = 'AlreadyCurrent'; HeadSha = $posture.HeadSha; UpdatedToSha = $null
                RemoteReachable = $true; Reason = 'Local main is current with origin/main.'
            }
        }
        'Ahead' {
            if (-not $isHomeCanonical) {
                Write-Host "Development checkout: main @ $shortSha with local-only commits ahead of origin/main."
                Write-Host 'Automatic main update skipped. Your commits were left unchanged.'
                return [pscustomobject]@{
                    Action = 'LocalCommitsPreserved'; HeadSha = $posture.HeadSha; UpdatedToSha = $null
                    RemoteReachable = $true; Reason = 'Main has local-only commits; no update was attempted.'
                }
            }
            Write-Host 'AEROLINK PRODUCTION START REFUSED' -ForegroundColor Red
            Write-Host 'Local main contains commits that are not on origin/main.' -ForegroundColor Red
            Write-Host 'No Git files or database state were changed.' -ForegroundColor Red
            throw 'Production launch refused: main has local-only commits.'
        }
        'Diverged' {
            if (-not $isHomeCanonical) {
                Write-Host "Development checkout: main @ $shortSha has diverged from origin/main."
                Write-Host 'Automatic main update skipped. Nothing was merged, rebased, or reset.'
                return [pscustomobject]@{
                    Action = 'DivergencePreserved'; HeadSha = $posture.HeadSha; UpdatedToSha = $null
                    RemoteReachable = $true; Reason = 'Main has diverged from origin/main; no update was attempted.'
                }
            }
            Write-Host 'AEROLINK PRODUCTION START REFUSED' -ForegroundColor Red
            Write-Host 'Local main has diverged from origin/main.' -ForegroundColor Red
            Write-Host 'No Git files or database state were changed.' -ForegroundColor Red
            throw 'Production launch refused: main has diverged from origin/main.'
        }
        'Behind' {
            Write-Host "Source: main @ $shortSha"
            Write-Host "origin/main: $($posture.ShortRemoteMainSha)"
            Write-Host 'Status: clean, fast-forwardable'
            $beforeFiles = Get-AeroLinkBootstrapFileSet -RepositoryRoot $RepositoryRoot -LauncherFiles $LauncherFiles
            try {
                Invoke-AeroLinkBootstrapGit -RepositoryRoot $RepositoryRoot -GitArguments @('merge', '--ff-only', 'origin/main') | Out-Null
            }
            catch {
                # A refused merge (for example an untracked file in the way) mutates nothing; say so plainly.
                throw "The safe fast-forward update was refused by Git and nothing was changed: $($_.Exception.Message)"
            }
            $updated = Get-AeroLinkRepositoryPosture -RepositoryRoot $RepositoryRoot
            if (-not $updated.HeadSha -or $updated.HeadSha -ne $posture.RemoteMainSha) {
                throw 'The fast-forward update did not land on origin/main. Refusing to continue; inspect the repository before launching again.'
            }
            Write-Host "Updated safely to $($updated.ShortSha)" -ForegroundColor Green

            if ($LauncherFiles.Count -gt 0) {
                $afterFiles = Get-AeroLinkBootstrapFileSet -RepositoryRoot $RepositoryRoot -LauncherFiles $LauncherFiles
                $changedFiles = @(Compare-AeroLinkBootstrapFileSet -Before $beforeFiles -After $afterFiles)
                if ($changedFiles.Count -gt 0) {
                    Write-Host "Updated launcher files: $($changedFiles -join ', ')" -ForegroundColor DarkGray
                    $exitCode = Invoke-AeroLinkBootstrapReentry -CurrentScriptPath $CurrentScriptPath -ScriptArguments $ScriptArguments
                    return [pscustomobject]@{
                        Action = 'Reentered'; HeadSha = $updated.HeadSha; UpdatedToSha = $updated.ShortSha
                        RemoteReachable = $true; Reason = 'The updated launcher implementation ran in a fresh process.'; ExitCode = $exitCode
                    }
                }
            }
            return [pscustomobject]@{
                Action = 'Updated'; HeadSha = $updated.HeadSha; UpdatedToSha = $updated.ShortSha
                RemoteReachable = $true; Reason = 'Strictly fast-forwarded to origin/main.'
            }
        }
        default {
            throw "AeroLink cannot characterize its source: unexpected repository relationship '$($posture.Relationship)'. Launch refused; nothing was changed."
        }
    }
}

Export-ModuleMember -Function @(
    'Invoke-AeroLinkBootstrapGit',
    'Invoke-AeroLinkBootstrapGitQuiet',
    'Get-AeroLinkRepositoryPosture',
    'Sync-AeroLinkRemoteRefs',
    'Get-AeroLinkBootstrapScriptArguments',
    'Get-AeroLinkBootstrapFileSet',
    'Compare-AeroLinkBootstrapFileSet',
    'Invoke-AeroLinkBootstrapReentry',
    'Update-AeroLinkClientDependencies',
    'Invoke-AeroLinkSourceBootstrap'
)
