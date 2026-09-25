#requires -Version 5.1
<#
.SYNOPSIS
  PROPOSED, Astra-review-gated recovery of the canonical AeroLink checkout after the #1040 rehearsal incident.

.DESCRIPTION
  DO NOT RUN -Mode Recover or -Mode Rollback before Astra's actual Checkpoint-1 recovery approval.
  -Mode Plan (the default) is read-only: it evaluates every precondition and prints what Recover would do.

  Recover restores only the pre-incident local state:
    refs/heads/main  b6309fb0 (incident)  ->  77b96857 (last established pre-incident local main)
  It first preserves everything: a standalone all-refs bundle, the local config file, ref/reflog/status
  snapshots, and create-only archive refs under refs/incident/1040/ (never pushed by a default push).
  Separate opt-in switches (each needing its own explicit approval) handle the other decisions:
    -UnsetRehearsalIdentity   remove ONLY the repository-local user.name/user.email rehearsal overrides.
                              It does not guess or set any previous local value.
    -ArchiveDrillA2           move drillA2 to refs/incident/1040/drillA2-bb8a6622 (no deletion of commits).
    -FastForwardToRemoteMain  a SEPARATE update from the restored pre-incident main to current origin/main,
                              fast-forward only. Undoing the incident does not require this.

  Every Git command targets the verified repository with an explicit `git -C <path>`; nothing depends on the
  current directory. Every precondition failure aborts before the first write. Ref moves use
  compare-and-swap (update-ref with an expected old value) or `reset --keep`, which refuses to discard local
  modifications. Nothing is pushed, fetched (except -FastForwardToRemoteMain), stashed, cleaned or rebased.

  Rollback returns main to the preserved incident commit and re-applies exactly what Recover changed, using
  the result.json that Recover wrote in -EvidenceDir.
#>
[CmdletBinding()]
param(
    [ValidateSet('Plan', 'Recover', 'Rollback')]
    [string]$Mode = 'Plan',
    [string]$Repo = 'C:\Sean Project\Requirements Management Tool',
    [string]$EvidenceDir = '',
    [switch]$UnsetRehearsalIdentity,
    [switch]$ArchiveDrillA2,
    [switch]$FastForwardToRemoteMain
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$env:GIT_OPTIONAL_LOCKS = '0'
$env:GIT_TERMINAL_PROMPT = '0'

$X = [ordered]@{
    PreMain       = '77b96857868f35926cd439371af159f8af2230e2'
    PreTree       = 'd00fdd25367314188d28fa28863d13c0e8aac5c9'
    PreWorkflow   = 'd0e1776748026f59975dfc05eeb806163daaeb73'
    Revert        = 'a35b260b799cbfbea436ed5f63ff3c6ae1f23555'
    IncidentMain  = 'b6309fb01d46afe17e7aaa3e34fc0bc38be6e726'
    IncidentTree  = '6db158346d6e454e84e747b3424e5dce70e86dfe'
    DrillA2       = 'bb8a6622a08742cc75f9879efb53ce8aa331b2fe'
    Zero          = '0000000000000000000000000000000000000000'
    ArchiveMain   = 'refs/incident/1040/local-main-b6309fb0'
    ArchiveDrill  = 'refs/incident/1040/drillA2-bb8a6622'
    RehearsalName = 'rehearsal'
    RehearsalMail = 'rehearsal@invalid'
}

$sep = [System.IO.Path]::DirectorySeparatorChar
function ConvertTo-NormalPath([string]$p) {
    return [System.IO.Path]::GetFullPath(($p -replace '/', $sep)).TrimEnd($sep)
}

function Invoke-Git([string[]]$GitArgs) {
    $saved = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $lines = & git -C $script:RepoFull @GitArgs 2>&1 | ForEach-Object { "$_" }
        return [pscustomobject]@{ Exit = $LASTEXITCODE; Lines = @($lines); Text = (@($lines) -join "`n").Trim() }
    } finally { $ErrorActionPreference = $saved }
}

function Invoke-GitChecked([string[]]$GitArgs) {
    $r = Invoke-Git $GitArgs
    Write-Host ("  git -C `"{0}`" {1}  -> exit {2}" -f $script:RepoFull, ($GitArgs -join ' '), $r.Exit)
    if ($r.Exit -ne 0) { throw "Git command failed (exit $($r.Exit)): git $($GitArgs -join ' ')`n$($r.Text)" }
    return $r
}

function Get-Rev([string]$Rev) {
    $r = Invoke-Git @('rev-parse', '--verify', '--quiet', $Rev)
    if ($r.Exit -ne 0) { return $null }
    return $r.Lines[0]
}

function Get-LocalConfig([string]$Key) {
    $r = Invoke-Git @('config', '--local', '--get-all', $Key)
    if ($r.Exit -eq 1) { return @() }
    if ($r.Exit -ne 0) { throw "Cannot read local $Key" }
    return @($r.Lines)
}

# ---- Repository identity (common to every mode) --------------------------------------------------------
if (-not (Test-Path -LiteralPath $Repo -PathType Container)) { throw "Repository path not found: $Repo" }
$script:RepoFull = ConvertTo-NormalPath (Resolve-Path -LiteralPath $Repo).ProviderPath
$top = Invoke-Git @('rev-parse', '--show-toplevel')
if ($top.Exit -ne 0 -or -not (ConvertTo-NormalPath $top.Lines[0]).Equals($script:RepoFull, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing: $Repo is not itself the top of a Git work tree."
}
$gitDir = ConvertTo-NormalPath (Invoke-GitChecked @('rev-parse', '--path-format=absolute', '--git-dir')).Lines[0]
$commonDir = ConvertTo-NormalPath (Invoke-GitChecked @('rev-parse', '--path-format=absolute', '--git-common-dir')).Lines[0]

function Get-StateFailures([string]$ExpectedMain, [string]$ExpectedTree, [bool]$ForRecover) {
    $f = New-Object System.Collections.Generic.List[string]
    if (-not $gitDir.Equals($commonDir, [StringComparison]::OrdinalIgnoreCase)) { $f.Add('not the main worktree (linked worktree detected)') }
    $sym = Invoke-Git @('symbolic-ref', '-q', 'HEAD')
    if ($sym.Exit -ne 0 -or $sym.Lines[0] -ne 'refs/heads/main') { $f.Add("HEAD is not refs/heads/main ($($sym.Text))") }
    $main = Get-Rev 'refs/heads/main'
    if ($main -ne $ExpectedMain) { $f.Add("refs/heads/main is $main, expected $ExpectedMain") }
    $tree = Get-Rev 'refs/heads/main^{tree}'
    if ($tree -ne $ExpectedTree) { $f.Add("main tree is $tree, expected $ExpectedTree") }
    $st = Invoke-Git @('--no-optional-locks', 'status', '--porcelain=v1', '--untracked-files=all')
    if ($st.Exit -ne 0) { $f.Add('git status failed') }
    else {
        $dirty = @($st.Lines | Where-Object { $_ -ne '' })
        if ($dirty.Count -gt 0) { $f.Add("working tree is not clean ($($dirty.Count) entries); preserve that work first") }
    }
    foreach ($m in 'MERGE_HEAD', 'CHERRY_PICK_HEAD', 'REVERT_HEAD', 'BISECT_LOG', 'rebase-merge', 'rebase-apply', 'index.lock', 'HEAD.lock') {
        if (Test-Path -LiteralPath (Join-Path $gitDir $m)) { $f.Add("in-progress/lock marker present: $m") }
    }
    $wt = Invoke-Git @('worktree', 'list', '--porcelain')
    $current = $null
    foreach ($line in $wt.Lines) {
        if ($line -like 'worktree *') { $current = ConvertTo-NormalPath $line.Substring(9) }
        elseif ($line -eq 'branch refs/heads/main' -and -not $current.Equals($script:RepoFull, [StringComparison]::OrdinalIgnoreCase)) {
            $f.Add("main is also checked out in another worktree: $current")
        }
    }
    if ($ForRecover) {
        $between = Invoke-Git @('rev-list', "$($X.PreMain)..refs/heads/main")
        if (($between.Lines -join ',') -ne "$($X.IncidentMain),$($X.Revert)") {
            $f.Add("commits beyond pre-incident main are not exactly the two accidental commits: $($between.Lines -join ',')")
        }
        $drill = Get-Rev 'refs/heads/drillA2'
        if ($drill -ne $X.DrillA2) { $f.Add("refs/heads/drillA2 is $drill, expected $($X.DrillA2)") }
        foreach ($a in $X.ArchiveMain, $X.ArchiveDrill) { if (Get-Rev $a) { $f.Add("archive ref already exists: $a") } }
        if ($UnsetRehearsalIdentity) {
            $n = @(Get-LocalConfig 'user.name'); $e = @(Get-LocalConfig 'user.email')
            if (($n -join '|') -ne $X.RehearsalName -or ($e -join '|') -ne $X.RehearsalMail) {
                $f.Add("local identity is '$($n -join '|')' <$($e -join '|')>, not exactly the rehearsal values; identity step refused")
            }
        }
    }
    return ,$f     # comma: return the List itself, never an unrolled $null
}

function Write-Plan {
    Write-Host ''
    Write-Host 'Recover would run, in order (each against the verified repository path only):'
    Write-Host "  1. create NEW evidence directory outside the repository: $EvidenceDir"
    Write-Host '  2. git bundle create <evidence>/canonical-all-refs-pre-recovery.bundle --all ; git bundle verify'
    Write-Host '     copy <common-dir>/config ; snapshot for-each-ref, reflogs (HEAD, main, drillA2), status, stash list'
    Write-Host "  3. git update-ref -m <msg> $($X.ArchiveMain) $($X.IncidentMain) $($X.Zero)   (create-only)"
    Write-Host "     git update-ref -m <msg> $($X.ArchiveDrill) $($X.DrillA2) $($X.Zero)"
    Write-Host '  4. re-check every precondition'
    Write-Host "  5. git reset --keep $($X.PreMain)        (HEAD on main; refuses to discard local changes)"
    Write-Host "  6. verify HEAD=$($X.PreMain), tree=$($X.PreTree), clean status, workflow blob=$($X.PreWorkflow), other branches unchanged"
    if ($UnsetRehearsalIdentity) { Write-Host '  7. git config --local --unset user.name ; git config --local --unset user.email ; record effective identity + origin' }
    if ($ArchiveDrillA2) { Write-Host "  8. git update-ref -m <msg> -d refs/heads/drillA2 $($X.DrillA2)   (commits stay reachable from $($X.ArchiveDrill))" }
    if ($FastForwardToRemoteMain) { Write-Host '  9. git fetch origin +refs/heads/main:refs/remotes/origin/main ; require ancestry ; git merge --ff-only refs/remotes/origin/main' }
}

function Save-Snapshot([string]$Dir, [string]$Prefix) {
    $items = [ordered]@{
        'refs'          = @('for-each-ref', '--format=%(refname) %(objectname)')
        'status'        = @('--no-optional-locks', 'status', '--porcelain=v2', '--branch', '--untracked-files=all')
        'reflog-HEAD'   = @('reflog', 'show', '--date=iso', '-n', '120', 'HEAD')
        'reflog-main'   = @('reflog', 'show', '--date=iso', '-n', '60', 'refs/heads/main')
        'reflog-drillA2'= @('reflog', 'show', '--date=iso', '-n', '60', 'refs/heads/drillA2')
        'stash'         = @('stash', 'list', '--date=iso')
        'config-local'  = @('config', '--local', '--list', '--show-origin')
        'identity'      = @('config', '--show-origin', '--show-scope', '--get-all', 'user.name')
        'identity-mail' = @('config', '--show-origin', '--show-scope', '--get-all', 'user.email')
    }
    foreach ($k in $items.Keys) {
        $r = Invoke-Git $items[$k]
        ($r.Lines | ForEach-Object { $_ -replace '://[^/@\s]+@', '://***@' }) |
            Set-Content -LiteralPath (Join-Path $Dir "$Prefix-$k.txt") -Encoding UTF8
    }
}

function Write-Sums([string]$Dir) {
    Get-ChildItem -LiteralPath $Dir -File | Where-Object { $_.Name -ne 'SHA256SUMS.txt' } | Sort-Object Name | ForEach-Object {
        '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
    } | Set-Content -LiteralPath (Join-Path $Dir 'SHA256SUMS.txt') -Encoding UTF8
}

Write-Host "Repository (verified work-tree top): $script:RepoFull"
Write-Host "Mode: $Mode"

# =========================================================================================================
if ($Mode -eq 'Plan' -or $Mode -eq 'Recover') {
    $fail = Get-StateFailures $X.IncidentMain $X.IncidentTree $true
    if ($fail.Count -gt 0) {
        Write-Host 'PRECONDITIONS NOT MET - nothing was changed:'
        $fail | ForEach-Object { Write-Host "  - $_" }
        throw 'Refusing: the checkout is not in the exact reviewed incident state.'
    }
    Write-Host 'All preconditions hold (exact reviewed incident state).'
    if ($Mode -eq 'Plan') { Write-Plan; Write-Host ''; Write-Host 'Plan mode: nothing was written or changed.'; return }

    if ([string]::IsNullOrWhiteSpace($EvidenceDir)) { throw 'Recover requires -EvidenceDir (a NEW directory outside the repository).' }
    if (Test-Path -LiteralPath $EvidenceDir) { throw "Refusing to reuse existing evidence directory: $EvidenceDir" }
    $evid = ConvertTo-NormalPath $EvidenceDir
    if ($evid.StartsWith($script:RepoFull + $sep, [StringComparison]::OrdinalIgnoreCase)) { throw 'Evidence directory must be outside the repository.' }
    New-Item -ItemType Directory -Path $evid | Out-Null
    $result = [ordered]@{ startedUtc = (Get-Date).ToUniversalTime().ToString('o'); repo = $script:RepoFull
        mainBefore = $X.IncidentMain; identityUnset = $false; drillA2Archived = $false; fastForwardedTo = $null }

    # ---- Preservation (all before the first ref move) ----
    Write-Host 'Preserving current state...'
    Save-Snapshot $evid 'before'
    $bundle = Join-Path $evid 'canonical-all-refs-pre-recovery.bundle'
    Invoke-GitChecked @('bundle', 'create', $bundle, '--all') | Out-Null
    Invoke-GitChecked @('bundle', 'verify', $bundle) | Out-Null
    (Invoke-GitChecked @('bundle', 'list-heads', $bundle)).Lines | Set-Content -LiteralPath (Join-Path $evid 'bundle-heads.txt') -Encoding UTF8
    Copy-Item -LiteralPath (Join-Path $commonDir 'config') -Destination (Join-Path $evid 'config.pre-recovery')
    Invoke-GitChecked @('update-ref', '-m', '#1040 incident preservation (Astra-reviewed recovery)', $X.ArchiveMain, $X.IncidentMain, $X.Zero) | Out-Null
    Invoke-GitChecked @('update-ref', '-m', '#1040 incident preservation (Astra-reviewed recovery)', $X.ArchiveDrill, $X.DrillA2, $X.Zero) | Out-Null
    if ((Get-Rev $X.ArchiveMain) -ne $X.IncidentMain -or (Get-Rev $X.ArchiveDrill) -ne $X.DrillA2) { throw 'Archive refs did not verify; stopping before any ref move.' }
    Write-Sums $evid
    $headsBefore = (Invoke-Git @('for-each-ref', '--format=%(refname) %(objectname)', 'refs/heads')).Lines

    # ---- Re-check immediately before the move, then restore pre-incident main ----
    $again = Get-StateFailures $X.IncidentMain $X.IncidentTree $false
    if ($again.Count -gt 0) { $again | ForEach-Object { Write-Host "  - $_" }; throw 'State changed after preservation; stopping before any ref move. Preservation is retained.' }
    Invoke-GitChecked @('reset', '--keep', $X.PreMain) | Out-Null

    $post = Get-StateFailures $X.PreMain $X.PreTree $false
    $wfBlob = (Invoke-Git @('hash-object', '--', '.github/workflows/request-full-ci.yml')).Lines[0]
    if ($wfBlob -ne $X.PreWorkflow) { $post.Add("request-full-ci.yml blob $wfBlob, expected $($X.PreWorkflow)") }
    $headsAfter = (Invoke-Git @('for-each-ref', '--format=%(refname) %(objectname)', 'refs/heads')).Lines
    $otherBefore = $headsBefore | Where-Object { $_ -notlike 'refs/heads/main *' }
    $otherAfter = $headsAfter | Where-Object { $_ -notlike 'refs/heads/main *' }
    if (($otherBefore -join "`n") -ne ($otherAfter -join "`n")) { $post.Add('a branch other than main changed during recovery') }
    if ($post.Count -gt 0) {
        $post | ForEach-Object { Write-Host "  - $_" }
        throw "Post-recovery verification FAILED. Do not continue. Report to Astra; rollback: -Mode Rollback -EvidenceDir `"$evid`""
    }
    Write-Host "Verified: main=$($X.PreMain) tree=$($X.PreTree), clean, workflow blob restored, other branches unchanged."

    # ---- Optional, separately approved steps ----
    if ($UnsetRehearsalIdentity) {
        Invoke-GitChecked @('config', '--local', '--unset', 'user.name') | Out-Null
        Invoke-GitChecked @('config', '--local', '--unset', 'user.email') | Out-Null
        if (@(Get-LocalConfig 'user.name').Count -ne 0 -or @(Get-LocalConfig 'user.email').Count -ne 0) { throw 'Local identity still present after unset.' }
        $result.identityUnset = $true
        $eff = Invoke-Git @('config', '--show-origin', '--show-scope', '--get', 'user.name')
        Write-Host "Effective user.name now: $($eff.Text)  (empty = none configured; Sean chooses explicitly, nothing guessed)"
    }
    if ($ArchiveDrillA2) {
        $wt = (Invoke-Git @('worktree', 'list', '--porcelain')).Lines
        if ($wt -contains 'branch refs/heads/drillA2') { throw 'drillA2 is checked out in a worktree; not archived.' }
        Invoke-GitChecked @('update-ref', '-m', '#1040 archive drillA2 (preserved at archive ref)', '-d', 'refs/heads/drillA2', $X.DrillA2) | Out-Null
        if ((Get-Rev 'refs/heads/drillA2') -or (Get-Rev $X.ArchiveDrill) -ne $X.DrillA2) { throw 'drillA2 archive did not verify.' }
        $result.drillA2Archived = $true
    }
    if ($FastForwardToRemoteMain) {
        Invoke-GitChecked @('fetch', 'origin', '+refs/heads/main:refs/remotes/origin/main') | Out-Null
        $remote = Get-Rev 'refs/remotes/origin/main'
        if ((Invoke-Git @('merge-base', '--is-ancestor', $X.PreMain, $remote)).Exit -ne 0) { throw "origin/main $remote does not contain pre-incident main; not updated." }
        Invoke-GitChecked @('merge', '--ff-only', 'refs/remotes/origin/main') | Out-Null
        if ((Get-Rev 'HEAD') -ne $remote -or (Get-StateFailures $remote (Get-Rev "$remote^{tree}") $false).Count -gt 0) { throw 'Fast-forward verification failed.' }
        $result.fastForwardedTo = $remote
        Write-Host "Fast-forwarded main to current origin/main $remote (a separate update, not part of undoing the incident)."
    }

    Save-Snapshot $evid 'after'
    $result.finishedUtc = (Get-Date).ToUniversalTime().ToString('o')
    $result.mainAfter = Get-Rev 'refs/heads/main'
    $result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evid 'result.json') -Encoding UTF8
    Write-Sums $evid
    Write-Host "Recovery complete. Evidence: $evid"
    return
}

# =========================================================================================================
if ($Mode -eq 'Rollback') {
    if ([string]::IsNullOrWhiteSpace($EvidenceDir) -or -not (Test-Path -LiteralPath (Join-Path $EvidenceDir 'result.json'))) {
        throw 'Rollback requires the -EvidenceDir that Recover wrote (with result.json).'
    }
    $evid = ConvertTo-NormalPath $EvidenceDir
    $prior = Get-Content -LiteralPath (Join-Path $evid 'result.json') -Raw | ConvertFrom-Json
    $mainNow = Get-Rev 'refs/heads/main'
    $expectedNow = if ($prior.fastForwardedTo) { $prior.fastForwardedTo } else { $X.PreMain }
    $fail = Get-StateFailures $expectedNow (Get-Rev "$expectedNow^{tree}") $false
    if ((Get-Rev $X.ArchiveMain) -ne $X.IncidentMain) { $fail.Add("archive ref $($X.ArchiveMain) missing or moved") }
    if ($fail.Count -gt 0) {
        $fail | ForEach-Object { Write-Host "  - $_" }
        throw "Refusing rollback: main is $mainNow, expected $expectedNow in a clean state. Restore from the bundle manually with review."
    }
    Save-Snapshot $evid 'before-rollback'
    Invoke-GitChecked @('reset', '--keep', $X.ArchiveMain) | Out-Null
    if ($prior.identityUnset) {
        if (@(Get-LocalConfig 'user.name').Count -ne 0 -or @(Get-LocalConfig 'user.email').Count -ne 0) { throw 'A local identity exists now; not overwriting it.' }
        Invoke-GitChecked @('config', '--local', 'user.name', $X.RehearsalName) | Out-Null
        Invoke-GitChecked @('config', '--local', 'user.email', $X.RehearsalMail) | Out-Null
    }
    if ($prior.drillA2Archived) {
        Invoke-GitChecked @('update-ref', '-m', '#1040 rollback: restore drillA2', 'refs/heads/drillA2', $X.DrillA2, $X.Zero) | Out-Null
    }
    $post = Get-StateFailures $X.IncidentMain $X.IncidentTree $false
    if ($post.Count -gt 0) { $post | ForEach-Object { Write-Host "  - $_" }; throw 'Rollback verification failed; report to Astra.' }
    Save-Snapshot $evid 'after-rollback'
    Write-Sums $evid
    Write-Host "Rolled back to the preserved incident state (main=$($X.IncidentMain)). Archive refs are retained."
}
