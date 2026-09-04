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

# The project DIRECTORY, which is what a real listener's command line carries. Measured from a live process
# on this machine: the holder of the API port is the apphost at
# <project dir>\bin\Debug\net10.0\AeroLink.Api.exe, so the .csproj path never appears in it and requiring
# the .csproj would refuse every launch instead of tightening anything.
$ownership = @('C:\Sean Project\AeroLink Production\product\src\AeroLink.Api')
$otherCheckout = 'C:\Sean Project\Requirements Management Tool\product\src\AeroLink.Api'
$currentSha = 'a1b2c3d4e5f60718293a4b5c6d7e8f9012345678'
$olderSha = '0f1e2d3c4b5a69788796a5b4c3d2e1f098765432'

function New-Owner {
    # The real shape, copied from a live listener rather than imagined.
    param(
        [int]$ProcessId = 4242,
        [string]$CommandLine = '"C:\Sean Project\AeroLink Production\product\src\AeroLink.Api\bin\Debug\net10.0\AeroLink.Api.exe"  --urls http://127.0.0.1:5080',
        [string]$ExecutablePath = 'C:\Sean Project\AeroLink Production\product\src\AeroLink.Api\bin\Debug\net10.0\AeroLink.Api.exe'
    )
    return { param($Port) [pscustomobject]@{ Found = $true; Ambiguous = $false; Attributable = $true; ProcessId = $ProcessId; CommandLine = $CommandLine; ExecutablePath = $ExecutablePath; Detail = "Port $Port is held by PID $ProcessId." } }.GetNewClosure()
}
function New-Identity {
    param([string]$Sha, [string]$Mode)
    return { param($BaseUri) [pscustomobject]@{ sourceIdentity = $Sha; sourceShortSha = $Sha.Substring(0, 8); mode = $Mode } }.GetNewClosure()
}
$alwaysReady = { param($BaseUri) $true }

function Get-Disposition {
    param($PortOwnerProbe, $RuntimeProbe, $ReadyProbe = $alwaysReady, [string]$ExpectedMode = 'HOME-PRODUCTION', [AllowNull()][AllowEmptyString()][string]$ExpectedIdentity = $currentSha)
    return Resolve-AeroLinkRuntimeDisposition -Port 5080 -BaseUri 'http://127.0.0.1:5080' `
        -ExpectedMode $ExpectedMode -ExpectedSourceIdentity $ExpectedIdentity -OwnershipFragments $ownership `
        -PortOwnerProbe $PortOwnerProbe -RuntimeProbe $RuntimeProbe -ReadyProbe $ReadyProbe
}

try {
    # --- Nothing listening: start ---
    $free = { param($Port) [pscustomobject]@{ Found = $false; Ambiguous = $false; Attributable = $false; ProcessId = $null; CommandLine = $null; ExecutablePath = $null; Detail = "Nothing is listening on port $Port." } }
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
    $foreign = { param($Port) [pscustomobject]@{ Found = $true; Ambiguous = $false; Attributable = $true; ProcessId = 9001; CommandLine = 'C:\Tools\somebody-elses-server.exe --port 5080'; ExecutablePath = 'C:\Tools\somebody-elses-server.exe'; Detail = "Port $Port is held by PID 9001." } }
    $refusal = Get-Disposition -PortOwnerProbe $foreign -RuntimeProbe (New-Identity $currentSha 'HOME-PRODUCTION')
    Assert-True ($refusal.Disposition -eq 'Refuse') 'An unrelated process on an AeroLink port must be a refusal.'
    Assert-True ($refusal.Detail -match '9001') 'The refusal must name the PID so the operator can find it.'
    Assert-True ($refusal.Detail -match 'never stops a process it does not own') 'The refusal must say plainly that AeroLink will not kill it.'

    $stopped = [System.Collections.Generic.List[int]]::new()
    Assert-Throws { Stop-AeroLinkOwnedListener -Port 5080 -OwnershipFragments $ownership -PortOwnerProbe $foreign -Stopper { param($ProcessId) $stopped.Add($ProcessId) } } `
        'occupied by another application' 'Stopping an unowned listener must throw.'
    Assert-True ($stopped.Count -eq 0) 'An unowned process must never be stopped, not even after the refusal.'

    # --- A listener whose process cannot be read is unattributable, not somebody else's ---
    #
    # The process may have exited between reading the listener table and reading the process (re-running a
    # launcher seconds after a stop), or belong to another user. Still a refusal - nothing is stopped on a
    # guess - but telling the operator to "close it" sends them after something that may not exist.
    $unreadable = { param($Port) [pscustomobject]@{ Found = $true; Ambiguous = $false; Attributable = $false; ProcessId = 7777; CommandLine = $null; ExecutablePath = $null; Detail = "Port $Port is held by PID 7777, whose command line could not be read." } }
    $unreadableRefusal = Get-Disposition -PortOwnerProbe $unreadable -RuntimeProbe (New-Identity $currentSha 'HOME-PRODUCTION')
    Assert-True ($unreadableRefusal.Disposition -eq 'Refuse') 'An unreadable listener must still fail closed.'
    Assert-True ($unreadableRefusal.Detail -match 'could not be read') 'The refusal must say the command line could not be read...'
    Assert-True ($unreadableRefusal.Detail -notmatch 'occupied by another application') '...and must not claim another application owns the port.'
    $stopped.Clear()
    Assert-Throws { Stop-AeroLinkOwnedListener -Port 5080 -OwnershipFragments $ownership -PortOwnerProbe $unreadable -Stopper { param($ProcessId) $stopped.Add($ProcessId) } } `
        'could not be read' 'Stopping an unattributable listener must throw with the honest reason.'
    Assert-True ($stopped.Count -eq 0) 'An unattributable process must never be stopped.'

    # --- Ambiguous ownership fails closed rather than guessing ---
    $ambiguous = { param($Port) [pscustomobject]@{ Found = $true; Ambiguous = $true; Attributable = $false; ProcessId = $null; CommandLine = $null; ExecutablePath = $null; Detail = "Port $Port has 2 distinct listening owners; ownership cannot be attributed." } }
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
    Assert-True (Test-AeroLinkProcessOwnership -CommandLine "dotnet run --project `"$($ownership[0])\AeroLink.Api.csproj`"" -ExecutablePath 'dotnet.exe' -OwnershipFragments $ownership) `
        'This checkout''s AeroLink API command line is recognized.'
    Assert-True (-not (Test-AeroLinkProcessOwnership -CommandLine 'node aerolink-api-mock.js' -ExecutablePath 'node.exe' -OwnershipFragments $ownership)) `
        'A process merely mentioning AeroLink in a different form is not owned.'

    # The dangerous near-match, and the reason ownership is no longer an OR over fragments: ANOTHER
    # CHECKOUT'S AeroLink. It is unmistakably an AeroLink API, and it is unmistakably not ours to stop.
    $otherCheckoutCommand = "`"$otherCheckout\bin\Debug\net10.0\AeroLink.Api.exe`"  --urls http://127.0.0.1:5080"
    Assert-True (-not (Test-AeroLinkProcessOwnership -CommandLine $otherCheckoutCommand -ExecutablePath "$otherCheckout\bin\Debug\net10.0\AeroLink.Api.exe" -OwnershipFragments $ownership)) `
        'Another checkout''s AeroLink API must not be owned by this checkout.'

    $otherCheckoutOwner = New-Owner -ProcessId 5150 -CommandLine $otherCheckoutCommand -ExecutablePath "$otherCheckout\bin\Debug\net10.0\AeroLink.Api.exe"
    $otherCheckoutRefusal = Get-Disposition -PortOwnerProbe $otherCheckoutOwner -RuntimeProbe (New-Identity $currentSha 'HOME-PRODUCTION')
    Assert-True ($otherCheckoutRefusal.Disposition -eq 'Refuse') `
        'Another checkout''s AeroLink on our port must be refused, not stopped as though it were ours.'
    $stoppedOther = [System.Collections.Generic.List[int]]::new()
    Assert-Throws { Stop-AeroLinkOwnedListener -Port 5080 -OwnershipFragments $ownership -PortOwnerProbe $otherCheckoutOwner -Stopper { param($ProcessId) $stoppedOther.Add($ProcessId) } } `
        'occupied by another application' 'Stopping another checkout''s AeroLink must throw.'
    Assert-True ($stoppedOther.Count -eq 0) 'Another checkout''s AeroLink must never be stopped.'

    # Every fragment must match, so an empty fragment set can never be an implicit yes.
    Assert-True (-not (Test-AeroLinkProcessOwnership -CommandLine $otherCheckoutCommand -ExecutablePath 'x' -OwnershipFragments @())) `
        'No ownership fragment means ownership cannot be established, never that it is granted.'
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

    # --- A pathname Git would C-quote must still change the identity ---
    #
    # With plain --porcelain, a name containing a space, a quote or a non-ASCII character comes back quoted
    # and escaped; the old parser trimmed quotes but decoded nothing, failed to resolve the file, and folded
    # in only the status text. Editing that file then left the identity unchanged, which is a stale process
    # surviving an edit. --porcelain=v1 -z emits the name verbatim.
    $awkwardName = 'r' + [char]0x00E9 + 'serv' + [char]0x00E9 + ' notes.tsx'
    $awkward = Join-Path $repo $awkwardName
    Set-Content -LiteralPath $awkward -Value 'one' -Encoding UTF8
    $awkwardFirst = Get-AeroLinkSourceFingerprint -RepositoryRoot $repo
    Set-Content -LiteralPath $awkward -Value 'two' -Encoding UTF8
    $awkwardSecond = Get-AeroLinkSourceFingerprint -RepositoryRoot $repo
    Assert-True ($awkwardFirst.Identity -ne $awkwardSecond.Identity) `
        "Editing a file whose pathname Git would quote must change the source identity (before='$($awkwardFirst.Identity)' after='$($awkwardSecond.Identity)' detail='$($awkwardSecond.Detail)')."
    Remove-Item -LiteralPath $awkward -Force

    # --- A rename carries both paths, and the parse stays in step ---
    Set-Content -LiteralPath (Join-Path $repo 'renamed.txt') -Value 'content' -Encoding ASCII
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    & git -C $repo add -A 2>&1 | Out-Null
    & git -C $repo commit -m 'add renamed' --quiet 2>&1 | Out-Null
    & git -C $repo mv 'renamed.txt' 'renamed-now.txt' 2>&1 | Out-Null
    $ErrorActionPreference = $previousPreference
    $renameFingerprint = Get-AeroLinkSourceFingerprint -RepositoryRoot $repo
    Assert-True (-not [string]::IsNullOrWhiteSpace($renameFingerprint.Identity)) 'A rename must still produce a source identity rather than derailing the parse.'
    Assert-True ($renameFingerprint.Reusable -eq $true) 'A bounded dirty tree with a rename is still fingerprintable.'

    # --- Unknown source must be UNREUSABLE, not consistently unknown ---
    #
    # The old code returned the stable string "<sha>+worktree:unfingerprintable" here. Two consecutive
    # launches in that state therefore produced the same expected identity, the process published it, and the
    # second launch REUSED a process whose bytes had never been established.
    $overLimit = [pscustomobject]@{ Sha = $currentSha; IsDirty = $true; Reusable = $false; Identity = $null }
    $firstLaunch = Get-Disposition -PortOwnerProbe (New-Owner) `
        -RuntimeProbe (New-Identity $currentSha 'LOCAL-DEV') -ExpectedMode 'LOCAL-DEV' -ExpectedIdentity $overLimit.Identity
    Assert-True ($firstLaunch.Disposition -eq 'RestartStale') 'An unprovable source must not reuse a running process.'
    # The second consecutive launch, against a process started by the first one under the same condition.
    $secondLaunch = Get-Disposition -PortOwnerProbe (New-Owner) `
        -RuntimeProbe { param($BaseUri) [pscustomobject]@{ sourceIdentity = ''; sourceShortSha = ''; mode = 'LOCAL-DEV' } } `
        -ExpectedMode 'LOCAL-DEV' -ExpectedIdentity $overLimit.Identity
    Assert-True ($secondLaunch.Disposition -eq 'RestartStale') `
        'Two consecutive launches with unprovable source must BOTH restart; unknown must never become a stable reusable identity.'
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
