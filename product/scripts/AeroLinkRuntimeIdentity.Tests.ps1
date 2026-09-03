#Requires -Version 5.1
<#
    Contract coverage for runtime identity and process ownership (#881).

    The defect these encode: `/health/ready` returning 200 was treated as "reuse this process". It proves the
    process can reach a database and nothing about which source it was built from - which is how a healthy
    API from an older revision survived a repository update in #816 while the client moved on.

    Every scenario drives the decision through injected probes. No test starts a process, binds a port, kills
    anything, or reaches a database.
#>
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'AeroLinkRuntimeIdentity.psm1') -Force

$failures = [System.Collections.Generic.List[string]]::new()
$fixtures = [System.Collections.Generic.List[string]]::new()

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { $script:failures.Add($Message) }
}
function Assert-Throws([scriptblock]$Action, [string]$Pattern, [string]$Message) {
    try { & $Action | Out-Null }
    catch {
        if ($_.Exception.Message -match $Pattern) { return }
        $script:failures.Add("$Message (threw, but not matching '$Pattern': $($_.Exception.Message))")
        return
    }
    $script:failures.Add("$Message (nothing was thrown)")
}

$ownership = @('AeroLink.Api', 'C:\Sean Project\AeroLink Production\product\src\AeroLink.Api\AeroLink.Api.csproj')
$currentSha = 'a1b2c3d4e5f60718293a4b5c6d7e8f9012345678'
$olderSha = '0f1e2d3c4b5a69788796a5b4c3d2e1f098765432'

function New-Owner {
    param([int]$ProcessId = 4242, [string]$CommandLine = 'dotnet run --project "C:\Sean Project\AeroLink Production\product\src\AeroLink.Api\AeroLink.Api.csproj"')
    return { param($Port) [pscustomobject]@{ Found = $true; Ambiguous = $false; ProcessId = $ProcessId; CommandLine = $CommandLine; ExecutablePath = 'C:\Program Files\dotnet\dotnet.exe'; Detail = "Port $Port is held by PID $ProcessId." } }.GetNewClosure()
}
function New-Identity {
    param([string]$Sha, [string]$Mode)
    return { param($BaseUri) [pscustomobject]@{ sourceIdentity = $Sha; sourceShortSha = $Sha.Substring(0, 8); mode = $Mode } }.GetNewClosure()
}
$alwaysReady = { param($BaseUri) $true }

function Get-Disposition {
    param($PortOwnerProbe, $RuntimeProbe, $ReadyProbe = $alwaysReady, [string]$ExpectedMode = 'HOME-PRODUCTION', [string]$ExpectedIdentity = $currentSha)
    return Resolve-AeroLinkRuntimeDisposition -Port 5080 -BaseUri 'http://127.0.0.1:5080' `
        -ExpectedMode $ExpectedMode -ExpectedSourceIdentity $ExpectedIdentity -OwnershipFragments $ownership `
        -PortOwnerProbe $PortOwnerProbe -RuntimeProbe $RuntimeProbe -ReadyProbe $ReadyProbe
}

try {
    # --- Nothing listening: start ---
    $free = { param($Port) [pscustomobject]@{ Found = $false; Ambiguous = $false; ProcessId = $null; CommandLine = $null; ExecutablePath = $null; Detail = "Nothing is listening on port $Port." } }
    Assert-True ((Get-Disposition -PortOwnerProbe $free -RuntimeProbe (New-Identity $currentSha 'HOME-PRODUCTION')).Disposition -eq 'Free') `
        'An empty port must be Free.'

    # --- Matching owner, mode, source and readiness: the ONLY case that may be reused ---
    $reuse = Get-Disposition -PortOwnerProbe (New-Owner) -RuntimeProbe (New-Identity $currentSha 'HOME-PRODUCTION')
    Assert-True ($reuse.Disposition -eq 'Reuse') 'A matching, ready, owned process in the requested mode may be reused.'

    # --- Healthy but older source: STALE. This is the #816 defect. ---
    $stale = Get-Disposition -PortOwnerProbe (New-Owner) -RuntimeProbe (New-Identity $olderSha 'HOME-PRODUCTION')
    Assert-True ($stale.Disposition -eq 'RestartStale') 'A healthy AeroLink from another revision must be restarted, not reused.'
    Assert-True ($stale.Detail -match 'stale') 'The stale diagnostic must say so in the operator''s terms.'

    # --- Mode mismatch: a production API answering a development request is not a lucky reuse ---
    $mismatch = Get-Disposition -PortOwnerProbe (New-Owner) -RuntimeProbe (New-Identity $currentSha 'LOCAL-DEV')
    Assert-True ($mismatch.Disposition -eq 'RestartModeMismatch') 'A process in another launcher mode must be restarted in the requested mode.'

    # --- Owned but publishes no identity (an older build): cannot be proven, so restart ---
    $unidentified = Get-Disposition -PortOwnerProbe (New-Owner) -RuntimeProbe { param($BaseUri) $null }
    Assert-True ($unidentified.Disposition -eq 'RestartUnidentified') 'A process that publishes no identity cannot be proven current and must be restarted.'

    # --- Matching but not ready: restart rather than wait on it ---
    $unready = Get-Disposition -PortOwnerProbe (New-Owner) -RuntimeProbe (New-Identity $currentSha 'HOME-PRODUCTION') -ReadyProbe { param($BaseUri) $false }
    Assert-True ($unready.Disposition -eq 'RestartUnready') 'A matching process that is not ready must be restarted.'

    # --- An unrelated process on 5080 is REFUSED and never stopped ---
    $foreign = { param($Port) [pscustomobject]@{ Found = $true; Ambiguous = $false; ProcessId = 9001; CommandLine = 'C:\Tools\somebody-elses-server.exe --port 5080'; ExecutablePath = 'C:\Tools\somebody-elses-server.exe'; Detail = "Port $Port is held by PID 9001." } }
    $refusal = Get-Disposition -PortOwnerProbe $foreign -RuntimeProbe (New-Identity $currentSha 'HOME-PRODUCTION')
    Assert-True ($refusal.Disposition -eq 'Refuse') 'An unrelated process on an AeroLink port must be a refusal.'
    Assert-True ($refusal.Detail -match '9001') 'The refusal must name the PID so the operator can find it.'
    Assert-True ($refusal.Detail -match 'never stops a process it does not own') 'The refusal must say plainly that AeroLink will not kill it.'

    $stopped = [System.Collections.Generic.List[int]]::new()
    Assert-Throws { Stop-AeroLinkOwnedListener -Port 5080 -OwnershipFragments $ownership -PortOwnerProbe $foreign -Stopper { param($ProcessId) $stopped.Add($ProcessId) } } `
        'occupied by another application' 'Stopping an unowned listener must throw.'
    Assert-True ($stopped.Count -eq 0) 'An unowned process must never be stopped, not even after the refusal.'

    # --- Ambiguous ownership fails closed rather than guessing ---
    $ambiguous = { param($Port) [pscustomobject]@{ Found = $true; Ambiguous = $true; ProcessId = $null; CommandLine = $null; ExecutablePath = $null; Detail = "Port $Port has 2 distinct listening owners; ownership cannot be attributed." } }
    Assert-True ((Get-Disposition -PortOwnerProbe $ambiguous -RuntimeProbe (New-Identity $currentSha 'HOME-PRODUCTION')).Disposition -eq 'Refuse') `
        'Ambiguous port ownership must refuse rather than pick a process to stop.'

    # --- An owned stale process IS stopped, once, by PID ---
    $stopped.Clear()
    $stop = Stop-AeroLinkOwnedListener -Port 5080 -OwnershipFragments $ownership -PortOwnerProbe (New-Owner) -Stopper { param($ProcessId) $stopped.Add($ProcessId) }
    Assert-True ($stop.Stopped -and $stopped.Count -eq 1 -and $stopped[0] -eq 4242) 'An owned stale process must be stopped exactly once, by PID.'

    # --- A test API on an alternate port is a different question and is never consulted here ---
    $testPortOwner = { param($Port) if ($Port -eq 5082) { throw "Port 5082 must not be inspected by a 5080 decision." } ; & (New-Owner) $Port }
    $null = Get-Disposition -PortOwnerProbe $testPortOwner -RuntimeProbe (New-Identity $currentSha 'HOME-PRODUCTION')

    # --- Ownership matching is by command line and executable, and is not fooled by a similar name ---
    Assert-True (Test-AeroLinkProcessOwnership -CommandLine 'dotnet run --project "...\AeroLink.Api.csproj"' -ExecutablePath 'dotnet.exe' -OwnershipFragments $ownership) `
        'An AeroLink API command line is recognized.'
    Assert-True (-not (Test-AeroLinkProcessOwnership -CommandLine 'node aerolink-api-mock.js' -ExecutablePath 'node.exe' -OwnershipFragments $ownership)) `
        'A process merely mentioning AeroLink in a different form is not owned.'
    Assert-True (-not (Test-AeroLinkProcessOwnership -CommandLine $null -ExecutablePath $null -OwnershipFragments $ownership)) `
        'A process whose command line cannot be read is not owned.'

    # =====================================================================================================
    # Source fingerprint: honest about dirt.
    # =====================================================================================================
    $repo = Join-Path ([IO.Path]::GetTempPath()) ("aerolink-fingerprint-" + [Guid]::NewGuid().ToString('N'))
    $fixtures.Add($repo)
    New-Item -ItemType Directory -Path $repo | Out-Null
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    & git -C $repo init --quiet 2>&1 | Out-Null
    & git -C $repo config user.email 'fingerprint@example.com' 2>&1 | Out-Null
    & git -C $repo config user.name 'Fingerprint Fixture' 2>&1 | Out-Null
    Set-Content -LiteralPath (Join-Path $repo 'file.txt') -Value 'one' -Encoding ASCII
    & git -C $repo add -A 2>&1 | Out-Null
    & git -C $repo commit -m initial --quiet 2>&1 | Out-Null
    $ErrorActionPreference = $previous

    $clean = Get-AeroLinkSourceFingerprint -RepositoryRoot $repo
    Assert-True (-not $clean.IsDirty) 'A clean checkout reports itself clean.'
    Assert-True ($clean.Identity -eq $clean.Sha) 'A clean checkout identity is exactly its commit SHA, so nothing changes for the ordinary case.'

    Set-Content -LiteralPath (Join-Path $repo 'file.txt') -Value 'two' -Encoding ASCII
    $dirty = Get-AeroLinkSourceFingerprint -RepositoryRoot $repo
    Assert-True ($dirty.IsDirty) 'A modified checkout reports itself dirty.'
    Assert-True ($dirty.Sha -eq $clean.Sha) 'Editing a file does not move HEAD...'
    Assert-True ($dirty.Identity -ne $clean.Identity) '...but it MUST change the source identity, or a stale process survives an edit.'
    Assert-True ($dirty.Identity -match '\+worktree:') 'A dirty identity is visibly a worktree fingerprint, not a bare SHA.'

    Set-Content -LiteralPath (Join-Path $repo 'file.txt') -Value 'three' -Encoding ASCII
    $dirtyAgain = Get-AeroLinkSourceFingerprint -RepositoryRoot $repo
    Assert-True ($dirtyAgain.Identity -ne $dirty.Identity) 'Different uncommitted content must produce a different identity.'

    Set-Content -LiteralPath (Join-Path $repo 'file.txt') -Value 'two' -Encoding ASCII
    Assert-True ((Get-AeroLinkSourceFingerprint -RepositoryRoot $repo).Identity -eq $dirty.Identity) `
        'The same uncommitted content must produce the same identity, so an unchanged dirty tree does not restart on every launch.'

    Set-Content -LiteralPath (Join-Path $repo 'untracked.tsx') -Value 'new source' -Encoding ASCII
    Assert-True ((Get-AeroLinkSourceFingerprint -RepositoryRoot $repo).Identity -ne $dirty.Identity) `
        'An untracked source file must change the identity: it can enter a build.'

    Assert-True ((Get-AeroLinkSourceFingerprint -RepositoryRoot ([IO.Path]::GetTempPath())).Identity -eq $null) `
        'A directory that is not a Git working tree has no source identity, and must not be given one.'

    # A dirty identity can never equal a bare SHA, so a runtime that reports only a SHA is always restarted.
    $dirtyDecision = Get-Disposition -PortOwnerProbe (New-Owner) -RuntimeProbe (New-Identity $currentSha 'LOCAL-DEV') `
        -ExpectedMode 'LOCAL-DEV' -ExpectedIdentity "$currentSha+worktree:0123456789abcdef"
    Assert-True ($dirtyDecision.Disposition -eq 'RestartStale') `
        'A launcher running a dirty tree must not reuse a process that reports only the commit SHA.'
}
finally {
    foreach ($fixture in $fixtures) {
        if (Test-Path -LiteralPath $fixture) { Remove-Item -LiteralPath $fixture -Recurse -Force -ErrorAction SilentlyContinue }
    }
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Host "FAIL: $_" -ForegroundColor Red }
    Write-Host "AeroLink runtime-identity contract FAILED ($($failures.Count) failure(s))." -ForegroundColor Red
    exit 1
}
Write-Host 'AeroLink runtime-identity contract passed.' -ForegroundColor Green
exit 0
