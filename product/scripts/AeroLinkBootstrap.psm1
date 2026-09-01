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
    clean `main` with no local-only commits and no non-ignored untracked files in HOME canonical mode - and
    continues legibly when GitHub is unreachable. Untracked source is potentially executable source (an
    untracked .ts/.tsx or SDK-style C# file can enter a build), so HOME canonical refuses it; development
    mode preserves and runs with it.

    Two bounded auxiliary behaviors live here because a fast-forward can change the files that are running:
    a one-shot re-entry mechanism (rerun the launch from the updated files, exactly once, carrying an expected
    source SHA), and a package-lock.json fingerprint so `npm ci` runs only when client dependency inputs
    actually changed. Re-entry skips the network/update cycle but never skips mode-policy validation: the
    child re-runs the full policy for its mode and verifies the source identity the parent verified, then
    consumes the one-shot markers so a stale marker can never bypass a later launch.

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
            The child process carries two one-shot environment markers: AEROLINK_BOOTSTRAP_REENTRY, which lets
            the child's bootstrap skip the network/update cycle, and AEROLINK_BOOTSTRAP_EXPECTED_SHA, the exact
            source identity the parent verified. The markers are loop prevention and source identity only —
            they are never authority to skip mode-policy validation, and the child consumes both immediately.
            The parent waits for the child and returns its exit code; the caller exits with it so launch.cmd
            reports the true outcome.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$CurrentScriptPath,
        [Parameter(Mandatory)][string]$ExpectedSha,
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
    $env:AEROLINK_BOOTSTRAP_EXPECTED_SHA = $ExpectedSha
    try {
        $child = Start-Process -FilePath $hostExecutable -ArgumentList $argumentLine -NoNewWindow -PassThru -Wait
        return $child.ExitCode
    }
    finally {
        Remove-Item -Path 'Env:AEROLINK_BOOTSTRAP_REENTRY', 'Env:AEROLINK_BOOTSTRAP_EXPECTED_SHA' -ErrorAction SilentlyContinue
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

function Write-AeroLinkProductionRefusal {
    <#
        .SYNOPSIS Prints the production refusal block and throws so the launcher exits before any product start.
    #>
    param(
        [Parameter(Mandatory)][string]$Reason,
        [string[]]$AdditionalLines = @()
    )
    Write-Host 'AEROLINK PRODUCTION START REFUSED' -ForegroundColor Red
    Write-Host $Reason -ForegroundColor Red
    foreach ($line in $AdditionalLines) { Write-Host $line -ForegroundColor Red }
    Write-Host 'No Git files or database state were changed.' -ForegroundColor Red
    throw "Production launch refused: $Reason"
}

function Assert-AeroLinkHomeCanonicalSourcePolicy {
    <#
        .SYNOPSIS Validates a posture against the HOME canonical source invariant, without touching anything.
        .DESCRIPTION
            The invariant: a Git working tree on a non-detached `main`, zero tracked modifications, zero
            non-ignored untracked files (untracked source is potentially executable source and is not
            attested by merged main), and no commits ahead of or diverged from the last-known origin/main.
            When an expected SHA is supplied, HEAD must match it exactly. Used at startup, offline, on
            re-entry, and again after any fast-forward, because the source can change between those moments.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Posture,
        [string]$ExpectedSha,
        [ValidateSet('startup', 'offline', 're-entry', 'post-update')][string]$Context = 'startup'
    )
    if (-not $Posture.IsGitRepository) {
        throw "AeroLink cannot characterize its source: $($Posture.RepositoryRoot) is not a Git working tree. Launch refused; nothing was changed."
    }
    if ($Posture.IsDetachedHead) {
        throw "AeroLink cannot characterize its source: the repository is in detached HEAD state at $($Posture.ShortSha). Check out a branch, then launch again. Nothing was changed."
    }
    if ($Posture.Branch -ne $script:AeroLinkBootstrapMainBranch) {
        Write-AeroLinkProductionRefusal -Reason "Repository is on $($Posture.Branch), not canonical main."
    }
    if ($Posture.HasTrackedChanges) {
        Write-AeroLinkProductionRefusal -Reason 'The working tree has uncommitted modifications to tracked files.'
    }
    if ($Posture.UntrackedFileCount -gt 0) {
        $noun = if ($Posture.UntrackedFileCount -eq 1) { 'file' } else { 'files' }
        Write-AeroLinkProductionRefusal -Reason "The repository contains $($Posture.UntrackedFileCount) untracked local $noun." `
            -AdditionalLines @('Canonical HOME AeroLink only runs from a clean merged main.')
    }
    if (-not [string]::IsNullOrEmpty($ExpectedSha) -and $Posture.HeadSha -ne $ExpectedSha) {
        throw "AeroLink source identity mismatch ($Context): HEAD is $($Posture.ShortSha) but the verified source is $($ExpectedSha.Substring(0, [Math]::Min(8, $ExpectedSha.Length))). The source changed during startup and must be inspected; no automatic action was taken."
    }
    if ($Posture.Relationship -eq 'Diverged') {
        Write-AeroLinkProductionRefusal -Reason 'Local main has diverged from origin/main.'
    }
    if ($null -ne $Posture.AheadOfRemoteMain -and $Posture.AheadOfRemoteMain -gt 0) {
        $offlineNote = if ($Context -eq 'offline') { ', and GitHub is unavailable to verify otherwise' } else { '' }
        Write-AeroLinkProductionRefusal -Reason "Local main contains commits that are not on origin/main$offlineNote."
    }
}

function Invoke-AeroLinkSourceBootstrap {
    <#
        .SYNOPSIS Applies the mode-specific source policy, then updates and re-enters when permitted.
        .DESCRIPTION
            Decision order is deliberately posture-first: characterize, decide by mode, and only then touch
            anything. The only permitted automatic Git mutation is a strictly fast-forward update of a clean
            `main` with no local-only commits and no untracked files in HOME canonical mode, performed after
            the fetch so preconditions are re-derived from the refreshed state. Refusals happen before any
            product process, build, or PostgreSQL start, and never mutate Git.

            Re-entry (the fresh-process restart after a launcher-changing fast-forward) skips the network and
            update cycle but NEVER skips mode-policy validation: the child still runs the full policy for its
            mode, plus an exact expected-SHA identity check against the SHA the parent verified, and consumes
            the one-shot markers so a stale marker can never bypass a later launch.

            -FastForwardObserver is a deterministic diagnostic/test seam invoked between the fast-forward and
            the post-update revalidation, and -FailedFetchObserver between a failed fetch and the refreshed
            posture, so contract tests can prove that source appearing in those windows fails closed instead
            of racing.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][ValidateSet('Development', 'HomeCanonical')][string]$Mode,
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [Parameter(Mandatory)][string]$CurrentScriptPath,
        [AllowEmptyCollection()][string[]]$ScriptArguments = @(),
        [AllowEmptyCollection()][string[]]$LauncherFiles = @(),
        [int]$FetchTimeoutSeconds = 45,
        [scriptblock]$FastForwardObserver,
        [scriptblock]$FailedFetchObserver
    )

    $isHomeCanonical = ($Mode -eq 'HomeCanonical')

    # Re-entry: the parent has already performed the update and verified the source. Skip the network and
    # update cycle, but re-run the full mode policy against the actual tree, consume the markers, and refuse
    # closed on any mismatch. The marker is loop prevention and identity, never validation authority — and it
    # is never sufficient on its own: re-entry is valid ONLY with a well-formed expected SHA that HEAD matches.
    if ($env:AEROLINK_BOOTSTRAP_REENTRY) {
        $expectedShaFromParent = $env:AEROLINK_BOOTSTRAP_EXPECTED_SHA
        Remove-Item -Path 'Env:AEROLINK_BOOTSTRAP_REENTRY', 'Env:AEROLINK_BOOTSTRAP_EXPECTED_SHA' -ErrorAction SilentlyContinue

        if ([string]::IsNullOrWhiteSpace($expectedShaFromParent)) {
            throw 'AeroLink re-entry source identity is incomplete: expected SHA was not provided. Launch refused; nothing was changed.'
        }
        if ($expectedShaFromParent -notmatch '^[0-9a-fA-F]{40}$') {
            throw 'AeroLink re-entry source identity is malformed: the expected SHA is not a full 40-character hexadecimal commit identity. Launch refused; nothing was changed.'
        }

        $posture = Get-AeroLinkRepositoryPosture -RepositoryRoot $RepositoryRoot
        if (-not $isHomeCanonical) {
            if (-not $posture.IsGitRepository) {
                throw "AeroLink cannot characterize its source: $RepositoryRoot is not a Git working tree. Launch refused; nothing was changed."
            }
            if ($posture.IsDetachedHead) {
                throw "AeroLink cannot characterize its source: the repository is in detached HEAD state at $($posture.ShortSha). Check out a branch, then launch again. Nothing was changed."
            }
            if ($null -ne $expectedShaFromParent -and $posture.HeadSha -ne $expectedShaFromParent) {
                throw "AeroLink source identity mismatch (re-entry): HEAD is $($posture.ShortSha) but the updated launcher expected $($expectedShaFromParent.Substring(0, [Math]::Min(8, $expectedShaFromParent.Length))). The source changed during startup and must be inspected; no automatic action was taken."
            }
            Write-Host "Source bootstrap re-entry: development source validated at $($posture.Branch) @ $($posture.ShortSha); no further fetch or update." -ForegroundColor DarkGray
            return [pscustomobject]@{
                Action = 'ReentryValidated'; HeadSha = $posture.HeadSha; UpdatedToSha = $null
                RemoteReachable = $null; Reason = 'Re-entry validated the local source posture; the update cycle was skipped.'
            }
        }
        Assert-AeroLinkHomeCanonicalSourcePolicy -Posture $posture -ExpectedSha $expectedShaFromParent -Context 're-entry'
        Write-Host "Source bootstrap re-entry: HOME canonical source revalidated at main @ $($posture.ShortSha); no further fetch or update." -ForegroundColor DarkGray
        return [pscustomobject]@{
            Action = 'ReentryValidated'; HeadSha = $posture.HeadSha; UpdatedToSha = $null
            RemoteReachable = $null; Reason = 'Re-entry revalidated the HOME canonical source posture; the update cycle was skipped.'
        }
    }

    $posture = Get-AeroLinkRepositoryPosture -RepositoryRoot $RepositoryRoot

    if (-not $posture.IsGitRepository) {
        throw "AeroLink cannot characterize its source: $RepositoryRoot is not a Git working tree. Launch refused; nothing was changed."
    }
    if ($posture.IsDetachedHead) {
        throw "AeroLink cannot characterize its source: the repository is in detached HEAD state at $($posture.ShortSha). Check out a branch, then launch again. Nothing was changed."
    }

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
        Write-AeroLinkProductionRefusal -Reason "Repository is on $($posture.Branch), not canonical main."
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
        Write-AeroLinkProductionRefusal -Reason 'The working tree has uncommitted modifications to tracked files.'
    }

    if ($isHomeCanonical -and $posture.UntrackedFileCount -gt 0) {
        # Untracked source is potentially executable source (an untracked .ts/.tsx or SDK-style C# file can
        # enter a build), and it is not attested by merged main. Never delete, stash, or modify it: refuse.
        $noun = if ($posture.UntrackedFileCount -eq 1) { 'file' } else { 'files' }
        Write-AeroLinkProductionRefusal -Reason "The repository contains $($posture.UntrackedFileCount) untracked local $noun." `
            -AdditionalLines @('Canonical HOME AeroLink only runs from a clean merged main.')
    }

    if (-not $isHomeCanonical -and $posture.UntrackedFileCount -gt 0) {
        Write-Host "Note: $($posture.UntrackedFileCount) untracked local file(s) present. They are preserved and never deleted." -ForegroundColor DarkGray
    }

    if (-not $posture.HasRemote) {
        throw "AeroLink cannot characterize its source: no 'origin' remote is configured in $RepositoryRoot. Launch refused; nothing was changed."
    }

    # The source identity this startup transaction inspected. HOME canonical requires the repository to sit
    # exactly here when the fetch fails; a concurrent clean HEAD move is a moved precondition, not a safe one.
    $preFetchHeadSha = $posture.HeadSha

    $reached = Sync-AeroLinkRemoteRefs -RepositoryRoot $RepositoryRoot -TimeoutSeconds $FetchTimeoutSeconds
    # Deterministic diagnostic/test seam: source that another process creates while the fetch is failing can
    # be simulated here, so the contract tests can prove the failed-fetch window is still fully mode-aware.
    if (-not $reached -and $FailedFetchObserver) { & $FailedFetchObserver $RepositoryRoot }
    # The fetch may have moved remote-tracking refs; the policy is decided from the refreshed posture, so a
    # precondition that "moved" is simply re-evaluated, never broadened.
    $posture = Get-AeroLinkRepositoryPosture -RepositoryRoot $RepositoryRoot
    # Every message from here on describes the actual current checkout, never the pre-fetch one.
    $preFetchShortSha = $shortSha
    $shortSha = $posture.ShortSha

    if (-not $reached) {
        if ($isHomeCanonical) {
            # HOME canonical during a failed/offline fetch: the inspected source identity must still be the
            # one on disk. A clean HEAD move (for example another process resetting main backwards) escapes
            # the dirt/untracked/branch checks, so the identity itself is pinned; it is never adopted,
            # rewound, or re-fetched — the repository is left exactly as found and the launch is refused.
            if ($posture.HeadSha -ne $preFetchHeadSha) {
                Write-AeroLinkProductionRefusal -Reason "The source revision changed while AeroLink was checking for updates. Expected $preFetchShortSha; found $($posture.ShortSha)." `
                    -AdditionalLines @('Canonical HOME AeroLink requires a stable source revision during startup.')
            }
            if ($posture.HasTrackedChanges) {
                Write-AeroLinkProductionRefusal -Reason 'The working tree changed while AeroLink was checking for updates.' `
                    -AdditionalLines @('Canonical HOME AeroLink only runs from a clean merged main.')
            }
            if ($null -eq $posture.RemoteMainSha) {
                Write-AeroLinkProductionRefusal -Reason "GitHub is unavailable and no cached origin/main exists, so the canonical source posture cannot be verified for main @ $shortSha."
            }
            Assert-AeroLinkHomeCanonicalSourcePolicy -Posture $posture -Context 'offline'
            Write-Host "GitHub unavailable. Running cached clean main @ $shortSha. Latest remote revision could not be verified." -ForegroundColor Yellow
            return [pscustomobject]@{
                Action = 'ContinuedOfflineCachedMain'; HeadSha = $posture.HeadSha; UpdatedToSha = $null
                RemoteReachable = $false; Reason = 'GitHub is unavailable; the cached clean main is explicitly not verified against the remote.'
            }
        }
        if ($posture.HasTrackedChanges) {
            # Development: local work that appeared during the fetch window is preserved exactly, untouched.
            Write-Host "Development checkout: main @ $($posture.ShortSha) with local modifications."
            Write-Host 'Automatic main update skipped. Your uncommitted changes were left byte-for-byte unchanged.'
            return [pscustomobject]@{
                Action = 'LocalChangesPreserved'; HeadSha = $posture.HeadSha; UpdatedToSha = $null
                RemoteReachable = $false; Reason = 'The working tree has tracked modifications, so no update was attempted.'
            }
        }
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
        Write-AeroLinkProductionRefusal -Reason 'The working tree has uncommitted modifications to tracked files.'
    }

    if ($isHomeCanonical) {
        # The refreshed posture is the last word before the relationship decision: anything that appeared
        # while fetching is refused here, exactly as it would have been before the fetch.
        Assert-AeroLinkHomeCanonicalSourcePolicy -Posture $posture -Context 'startup'
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
            Write-AeroLinkProductionRefusal -Reason 'Local main contains commits that are not on origin/main.'
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
            Write-AeroLinkProductionRefusal -Reason 'Local main has diverged from origin/main.'
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

            # Deterministic diagnostic/test seam: anything another process does in the real window between
            # the merge and the revalidation below can be simulated here without sleeps or races.
            if ($FastForwardObserver) { & $FastForwardObserver $RepositoryRoot }

            # Full revalidation after the update, before anything else may consume the new tree: the source
            # must still satisfy the complete mode policy, not merely sit at the expected commit.
            $updated = Get-AeroLinkRepositoryPosture -RepositoryRoot $RepositoryRoot
            if (-not $updated.IsGitRepository -or $updated.IsDetachedHead) {
                throw 'The repository state changed during startup and can no longer be characterized. Inspect the repository; no automatic action was taken.'
            }
            if ($updated.HeadSha -ne $posture.RemoteMainSha) {
                throw "The source changed during startup: HEAD is $($updated.ShortSha) but the verified update target was $($posture.ShortRemoteMainSha). Inspect the repository; no automatic action was taken."
            }
            if ($isHomeCanonical) {
                Assert-AeroLinkHomeCanonicalSourcePolicy -Posture $updated -ExpectedSha $posture.RemoteMainSha -Context 'post-update'
            }
            elseif ($updated.HasTrackedChanges) {
                Write-Host 'Note: local modifications appeared during the update. They are preserved and nothing was merged, rebased, or reset.' -ForegroundColor DarkGray
            }
            Write-Host "Updated safely to $($updated.ShortSha)" -ForegroundColor Green

            if ($LauncherFiles.Count -gt 0) {
                $afterFiles = Get-AeroLinkBootstrapFileSet -RepositoryRoot $RepositoryRoot -LauncherFiles $LauncherFiles
                $changedFiles = @(Compare-AeroLinkBootstrapFileSet -Before $beforeFiles -After $afterFiles)
                if ($changedFiles.Count -gt 0) {
                    Write-Host "Updated launcher files: $($changedFiles -join ', ')" -ForegroundColor DarkGray
                    $exitCode = Invoke-AeroLinkBootstrapReentry -CurrentScriptPath $CurrentScriptPath -ExpectedSha $updated.HeadSha -ScriptArguments $ScriptArguments
                    return [pscustomobject]@{
                        Action = 'Reentered'; HeadSha = $updated.HeadSha; UpdatedToSha = $updated.ShortSha
                        RemoteReachable = $true; Reason = 'The updated launcher implementation ran in a fresh process.'; ExitCode = $exitCode
                    }
                }
            }
            return [pscustomobject]@{
                Action = 'Updated'; HeadSha = $updated.HeadSha; UpdatedToSha = $updated.ShortSha
                RemoteReachable = $true; Reason = 'Strictly fast-forwarded to origin/main and the result revalidated.'
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
