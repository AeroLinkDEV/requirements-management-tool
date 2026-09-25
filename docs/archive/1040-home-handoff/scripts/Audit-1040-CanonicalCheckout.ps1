#requires -Version 5.1
<#
.SYNOPSIS
  READ-ONLY audit of the canonical AeroLink checkout after the issue #1040 rehearsal incident.

.DESCRIPTION
  Prepared by cloud Claude for Astra/Sean (issue #1040 takeover). Nothing here was executed in the cloud;
  the cloud cannot reach this machine.

  This script changes NO repository, ref, reflog, index, worktree, stash, hook or Git configuration. It
  runs only Git read commands with optional locks disabled (so `status` cannot refresh the index), plus
  file hashing. It writes only into a NEW output directory that must be outside every repository and must
  not already exist. It performs no network access (no fetch, no ls-remote).

  Output is for Astra's Checkpoint-1 recovery review. Remote URLs have embedded credentials masked;
  still review the output before sharing it publicly.
#>
[CmdletBinding()]
param(
    [string]$Repo = 'C:\Sean Project\Requirements Management Tool',
    [string]$OutDir = ('C:\Sean Project\RMT-1040-glm-evidence\canonical-audit-' +
        (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ')),
    [string]$IncidentBundle = 'C:\Sean Project\RMT-1040-glm-evidence\handoff-1040-1098-2026-09-25T0140Z\critical-evidence\incident-bundle\accidental-commits.bundle'
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$env:GIT_OPTIONAL_LOCKS = '0'        # never let status/diff rewrite the index
$env:GIT_TERMINAL_PROMPT = '0'

# ---- Guard: exact repository identity, output location, nothing pre-existing ---------------------------
if (-not (Test-Path -LiteralPath $Repo -PathType Container)) { throw "Repository path not found: $Repo" }
$sep = [System.IO.Path]::DirectorySeparatorChar
function ConvertTo-NormalPath([string]$p) {
    return [System.IO.Path]::GetFullPath(($p -replace '/', $sep)).TrimEnd($sep)
}
$repoFull = ConvertTo-NormalPath (Resolve-Path -LiteralPath $Repo).ProviderPath
if (Test-Path -LiteralPath $OutDir) { throw "Refusing to reuse an existing output directory: $OutDir" }
$outFull = ConvertTo-NormalPath $OutDir
if ($outFull.StartsWith($repoFull + $sep, [StringComparison]::OrdinalIgnoreCase) -or
    $outFull.Equals($repoFull, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Output directory must be outside the repository.'
}

function Get-GitLines([string]$Dir, [string[]]$GitArgs) {
    $saved = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'   # PS 5.1 would otherwise turn native stderr into a terminating error
    try {
        $lines = & git -C $Dir @GitArgs 2>&1 | ForEach-Object { "$_" }
        return [pscustomobject]@{ Exit = $LASTEXITCODE; Lines = @($lines) }
    } finally { $ErrorActionPreference = $saved }
}

$top = Get-GitLines $repoFull @('rev-parse', '--show-toplevel')
if ($top.Exit -ne 0) { throw "Not a Git work tree: $repoFull" }
$topNorm = ConvertTo-NormalPath $top.Lines[0]
if (-not $topNorm.Equals($repoFull, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Path resolves to a different work tree ($topNorm); refusing."
}

New-Item -ItemType Directory -Path $outFull | Out-Null
$commandLog = Join-Path $outFull 'commands.txt'

function Mask([string]$s) { return ($s -replace '://[^/@\s]+@', '://***@') }

function Save-Git([string]$Name, [string[]]$GitArgs, [string]$Dir = $repoFull) {
    $r = Get-GitLines $Dir $GitArgs
    $file = Join-Path $outFull ("$Name.txt")
    ($r.Lines | ForEach-Object { Mask $_ }) | Set-Content -LiteralPath $file -Encoding UTF8
    Add-Content -LiteralPath $commandLog -Encoding UTF8 -Value (
        "[{0}] exit={1} git -C `"{2}`" {3}" -f $Name, $r.Exit, $Dir, ($GitArgs -join ' '))
    return $r
}

$expected = [ordered]@{
    PreIncidentMain     = '77b96857868f35926cd439371af159f8af2230e2'
    PreIncidentTree     = 'd00fdd25367314188d28fa28863d13c0e8aac5c9'
    AccidentalRevert    = 'a35b260b799cbfbea436ed5f63ff3c6ae1f23555'
    AccidentalDrill     = 'b6309fb01d46afe17e7aaa3e34fc0bc38be6e726'
    IncidentTree        = '6db158346d6e454e84e747b3424e5dce70e86dfe'
    DrillA2Revert       = '5978b673cbaa7f1683ff8aba7332e18ccb5cdbc0'
    DrillA2             = 'bb8a6622a08742cc75f9879efb53ce8aa331b2fe'
    RemoteMainAtHandoff = '0dddb0291420ee3630314bba8b9a5a3e6d367e95'
    FeatureHead1066     = '027c985e82d1d1b764a2f24eac6e6c2ed4ec2c71'
    RequesterHead1098   = '23bb25d987639ada3356a7003fa0380ba08e5042'
}
$expected | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $outFull 'expected-identities.json') -Encoding UTF8

# ---- 1. Identity, HEAD and status ------------------------------------------------------------------------
Save-Git '01-git-version'        @('version') | Out-Null
Save-Git '02-toplevel'           @('rev-parse', '--show-toplevel', '--git-dir', '--git-common-dir') | Out-Null
Save-Git '03-head'               @('rev-parse', 'HEAD', 'HEAD^{tree}') | Out-Null
Save-Git '04-head-symbolic'      @('symbolic-ref', '-q', 'HEAD') | Out-Null
Save-Git '05-status'             @('--no-optional-locks', 'status', '--porcelain=v2', '--branch', '--untracked-files=all') | Out-Null
Save-Git '06-stash'              @('stash', 'list', '--date=iso') | Out-Null

# In-progress operations and locks (file existence only)
$commonDir = ConvertTo-NormalPath (Get-GitLines $repoFull @('rev-parse', '--path-format=absolute', '--git-common-dir')).Lines[0]
$gitDir    = ConvertTo-NormalPath (Get-GitLines $repoFull @('rev-parse', '--path-format=absolute', '--git-dir')).Lines[0]
$markers = 'MERGE_HEAD', 'CHERRY_PICK_HEAD', 'REVERT_HEAD', 'BISECT_LOG', 'rebase-merge', 'rebase-apply', 'index.lock', 'HEAD.lock'
$markers | ForEach-Object {
    '{0}={1}' -f $_, (Test-Path -LiteralPath (Join-Path $gitDir $_))
} | Set-Content -LiteralPath (Join-Path $outFull '07-in-progress-markers.txt') -Encoding UTF8
Get-ChildItem -LiteralPath (Join-Path $commonDir 'refs') -Recurse -Filter '*.lock' -ErrorAction SilentlyContinue |
    ForEach-Object { $_.FullName } | Set-Content -LiteralPath (Join-Path $outFull '07b-ref-locks.txt') -Encoding UTF8

# ---- 2. Every ref, and the incident relationships ------------------------------------------------------
Save-Git '10-all-refs' @('for-each-ref', '--format=%(refname) %(objectname) %(objecttype) %(committerdate:iso-strict) %(authorname) <%(authoremail)>') | Out-Null
Save-Git '11-branches-verbose' @('branch', '--all', '-vv', '--no-abbrev') | Out-Null
foreach ($sha in $expected.Values) {
    Save-Git ("12-object-" + $sha.Substring(0, 8)) @('cat-file', '-t', $sha) | Out-Null
}
Save-Git '13-main-tree'              @('rev-parse', 'refs/heads/main', 'refs/heads/main^{tree}') | Out-Null
Save-Git '14-main-beyond-preincident' @('log', '--format=%H %P | %an <%ae> | %cd | %s', '--date=iso', "$($expected.PreIncidentMain)..refs/heads/main") | Out-Null
Save-Git '15-preincident-ancestor-of-main' @('merge-base', '--is-ancestor', $expected.PreIncidentMain, 'refs/heads/main') | Out-Null
Save-Git '16-diffstat-preincident-to-main' @('diff', '--stat', $expected.PreIncidentMain, 'refs/heads/main') | Out-Null
Save-Git '17-drillA2' @('log', '--format=%H %P | %an <%ae> | %cd | %s', '--date=iso', "$($expected.PreIncidentMain)..refs/heads/drillA2") | Out-Null
foreach ($sha in $expected.AccidentalRevert, $expected.AccidentalDrill, $expected.DrillA2Revert, $expected.DrillA2) {
    Save-Git ("18-refs-containing-" + $sha.Substring(0, 8)) @('for-each-ref', '--contains', $sha, '--format=%(refname) %(objectname)') | Out-Null
}
# Anything committed anywhere since the rehearsal started, and anything authored by the rehearsal identity.
Save-Git '19-commits-since-incident' @('log', '--all', '--since=2026-09-24T20:30:00-04:00', '--format=%H | %D | %an <%ae> | %cn <%ce> | %cd | %s', '--date=iso') | Out-Null
Save-Git '19b-rehearsal-authored'    @('log', '--all', '--author=rehearsal@invalid', '--format=%H | %D | %cd | %s', '--date=iso') | Out-Null
Save-Git '19c-rehearsal-committed'   @('log', '--all', '--committer=rehearsal@invalid', '--format=%H | %D | %cd | %s', '--date=iso') | Out-Null

# ---- 3. Reflogs (HEAD and every local branch) ----------------------------------------------------------
Save-Git '20-reflog-HEAD' @('reflog', 'show', '--date=iso', '-n', '120', 'HEAD') | Out-Null
$branches = (Get-GitLines $repoFull @('for-each-ref', '--format=%(refname)', 'refs/heads')).Lines
$i = 0
foreach ($b in $branches) {
    $i++
    $safe = ($b -replace '[^A-Za-z0-9._-]', '_')
    Save-Git ('21-reflog-{0:D3}-{1}' -f $i, $safe) @('reflog', 'show', '--date=iso', '-n', '60', $b) | Out-Null
}

# ---- 4. Configuration and its origins -------------------------------------------------------------------
Save-Git '30-config-all-with-origin' @('config', '--list', '--show-origin', '--show-scope') | Out-Null
Save-Git '31-config-local'           @('config', '--local', '--list', '--show-origin') | Out-Null
Save-Git '32-user-name-all-origins'  @('config', '--show-origin', '--show-scope', '--get-all', 'user.name') | Out-Null
Save-Git '33-user-email-all-origins' @('config', '--show-origin', '--show-scope', '--get-all', 'user.email') | Out-Null
Save-Git '34-worktree-config-flag'   @('config', '--get', 'extensions.worktreeConfig') | Out-Null
Save-Git '35-hooks-path'             @('config', '--show-origin', '--get', 'core.hooksPath') | Out-Null
Save-Git '36-remotes'                @('remote', '-v') | Out-Null
$localConfig = Join-Path $commonDir 'config'
if (Test-Path -LiteralPath $localConfig) {
    $h = Get-FileHash -LiteralPath $localConfig -Algorithm SHA256
    $item = Get-Item -LiteralPath $localConfig
    "{0}  {1}  lastWriteUtc={2:o}  length={3}" -f $h.Hash, $localConfig, $item.LastWriteTimeUtc, $item.Length |
        Set-Content -LiteralPath (Join-Path $outFull '37-local-config-file-hash.txt') -Encoding UTF8
}
$hooksDir = Join-Path $commonDir 'hooks'
if (Test-Path -LiteralPath $hooksDir) {
    Get-ChildItem -LiteralPath $hooksDir -File | Where-Object { $_.Name -notlike '*.sample' } | ForEach-Object {
        '{0}  {1}  lastWriteUtc={2:o}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash, $_.Name, $_.LastWriteTimeUtc
    } | Set-Content -LiteralPath (Join-Path $outFull '38-active-hooks.txt') -Encoding UTF8
}

# ---- 5. Worktrees and their ownership/state -------------------------------------------------------------
$wt = Save-Git '40-worktrees' @('worktree', 'list', '--porcelain')
$paths = $wt.Lines | Where-Object { $_ -like 'worktree *' } | ForEach-Object { ConvertTo-NormalPath $_.Substring(9) }
$j = 0
foreach ($p in $paths) {
    $j++
    if (-not (Test-Path -LiteralPath $p -PathType Container)) {
        "missing: $p" | Add-Content -LiteralPath (Join-Path $outFull '41-worktree-missing.txt') -Encoding UTF8
        continue
    }
    Save-Git ('42-wt{0:D2}-head' -f $j)   @('rev-parse', '--show-toplevel', 'HEAD') $p | Out-Null
    Save-Git ('42-wt{0:D2}-branch' -f $j) @('symbolic-ref', '-q', 'HEAD') $p | Out-Null
    Save-Git ('42-wt{0:D2}-status' -f $j) @('--no-optional-locks', 'status', '--porcelain=v2', '--branch', '--untracked-files=all') $p | Out-Null
}

# ---- 6. Evidence cross-checks ----------------------------------------------------------------------------
$wf = Join-Path (Join-Path (Join-Path $repoFull '.github') 'workflows') 'request-full-ci.yml'
if (Test-Path -LiteralPath $wf) {
    Save-Git '50-workflow-blob-in-worktree' @('hash-object', '--', '.github/workflows/request-full-ci.yml') | Out-Null
    Save-Git '51-workflow-blob-preincident' @('rev-parse', "$($expected.PreIncidentMain):.github/workflows/request-full-ci.yml") | Out-Null
}
if (Test-Path -LiteralPath $IncidentBundle) {
    (Get-FileHash -LiteralPath $IncidentBundle -Algorithm SHA256).Hash |
        Set-Content -LiteralPath (Join-Path $outFull '52-incident-bundle-sha256.txt') -Encoding UTF8
    Save-Git '53-incident-bundle-verify' @('bundle', 'verify', $IncidentBundle) | Out-Null
} else {
    "not found: $IncidentBundle" | Set-Content -LiteralPath (Join-Path $outFull '52-incident-bundle-sha256.txt') -Encoding UTF8
}

# ---- 7. Integrity of this audit output --------------------------------------------------------------------
Get-ChildItem -LiteralPath $outFull -File | Where-Object { $_.Name -ne 'SHA256SUMS.txt' } | Sort-Object Name | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
} | Set-Content -LiteralPath (Join-Path $outFull 'SHA256SUMS.txt') -Encoding UTF8

Write-Host "Read-only audit written to: $outFull"
Write-Host 'No repository, ref, index, worktree or configuration was modified.'
