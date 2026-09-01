#Requires -Version 5.1
<#
    Contract coverage for the mode-aware launcher source-posture bootstrap (issue #881 slice 1A).

    Every scenario runs against disposable Git repositories created under the machine temp directory: a bare
    "origin" and a work clone with its own user identity. The remote is made unreachable by pointing origin at
    a path that does not exist, so no test needs the network. No test connects to any database, touches the
    persistent AeroLink PostgreSQL instance, writes to the real product\.local operator state, or runs a real
    launcher against the real repository.
#>
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'AeroLinkBootstrap.psm1') -Force

$failures = [System.Collections.Generic.List[string]]::new()
$fixtures = [System.Collections.Generic.List[string]]::new()

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { $script:failures.Add($Message) }
}

function Invoke-FixtureGit {
    <#
        Fixture git runner. Under Windows PowerShell 5.1 a Stop preference turns redirected native stderr into
        error records, and git writes progress to stderr; the exit code is the authority, so the preference is
        relaxed around the call exactly as the module itself does.
    #>
    param(
        [Parameter(Mandatory)][string[]]$GitArguments,
        [string]$Repository
    )
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        if ($Repository) { $output = & git -C $Repository @GitArguments 2>&1 }
        else { $output = & git @GitArguments 2>&1 }
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }
    $text = (($output | ForEach-Object { "$_" }) -join "`n").Trim()
    if ($exitCode -ne 0) { throw "fixture git $($GitArguments -join ' ') failed: $text" }
    return $text
}

function New-FixtureRepository {
    $fixtureRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("aerolink-bootstrap-" + [Guid]::NewGuid().ToString('N'))
    $script:fixtures.Add($fixtureRoot)
    $originPath = Join-Path $fixtureRoot 'origin.git'
    $workPath = Join-Path $fixtureRoot 'work'
    New-Item -ItemType Directory -Path $fixtureRoot | Out-Null
    $null = Invoke-FixtureGit -GitArguments @('init', '--bare', $originPath)
    $null = Invoke-FixtureGit -GitArguments @('symbolic-ref', 'HEAD', 'refs/heads/main') -Repository $originPath
    $null = Invoke-FixtureGit -GitArguments @('init', $workPath)
    $null = Invoke-FixtureGit -GitArguments @('symbolic-ref', 'HEAD', 'refs/heads/main') -Repository $workPath
    $null = Invoke-FixtureGit -GitArguments @('config', 'user.email', 'bootstrap-fixture@example.com') -Repository $workPath
    $null = Invoke-FixtureGit -GitArguments @('config', 'user.name', 'Bootstrap Fixture') -Repository $workPath
    Set-Content -LiteralPath (Join-Path $workPath 'README.md') -Value 'fixture' -Encoding ASCII
    $null = Invoke-FixtureGit -GitArguments @('add', '-A') -Repository $workPath
    $null = Invoke-FixtureGit -GitArguments @('commit', '-m', 'initial') -Repository $workPath
    $null = Invoke-FixtureGit -GitArguments @('remote', 'add', 'origin', $originPath) -Repository $workPath
    $null = Invoke-FixtureGit -GitArguments @('push', '-u', 'origin', 'main') -Repository $workPath
    return [pscustomobject]@{ FixtureRoot = $fixtureRoot; OriginPath = $originPath; WorkPath = $workPath }
}

function New-PusherClone {
    param([Parameter(Mandatory)]$Fixture)
    $pusherPath = Join-Path $Fixture.FixtureRoot 'pusher'
    # A fixture may push to its remote more than once; the disposable pusher clone is recreated each time.
    if (Test-Path -LiteralPath $pusherPath) { Remove-Item -LiteralPath $pusherPath -Recurse -Force }
    $null = Invoke-FixtureGit -GitArguments @('clone', $Fixture.OriginPath, $pusherPath)
    $null = Invoke-FixtureGit -GitArguments @('config', 'user.email', 'bootstrap-fixture@example.com') -Repository $pusherPath
    $null = Invoke-FixtureGit -GitArguments @('config', 'user.name', 'Bootstrap Fixture') -Repository $pusherPath
    return $pusherPath
}

function Push-RemoteCommit {
    param([Parameter(Mandatory)]$Fixture, [Parameter(Mandatory)][string]$FileName, [Parameter(Mandatory)][string]$Content)
    $pusher = New-PusherClone -Fixture $Fixture
    $target = Join-Path $pusher $FileName
    $parent = Split-Path -Parent $target
    if (-not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
    Set-Content -LiteralPath $target -Value $Content -Encoding ASCII
    $null = Invoke-FixtureGit -GitArguments @('add', '-A') -Repository $pusher
    $null = Invoke-FixtureGit -GitArguments @('commit', '-m', "remote: $FileName") -Repository $pusher
    $null = Invoke-FixtureGit -GitArguments @('push', 'origin', 'main') -Repository $pusher
}

function Disconnect-Remote {
    param([Parameter(Mandatory)]$Fixture)
    $null = Invoke-FixtureGit -GitArguments @('remote', 'set-url', 'origin', (Join-Path $Fixture.FixtureRoot 'no-such-remote')) -Repository $Fixture.WorkPath
}

function Get-FixtureHead {
    param([Parameter(Mandatory)]$Fixture)
    return Invoke-FixtureGit -GitArguments @('rev-parse', 'HEAD') -Repository $Fixture.WorkPath
}

function Get-FixtureBranch {
    param([Parameter(Mandatory)]$Fixture)
    return Invoke-FixtureGit -GitArguments @('rev-parse', '--abbrev-ref', 'HEAD') -Repository $Fixture.WorkPath
}

function Get-FixtureRemoteMain {
    param([Parameter(Mandatory)]$Fixture)
    return Invoke-FixtureGit -GitArguments @('rev-parse', 'origin/main') -Repository $Fixture.WorkPath
}

function Get-OriginMain {
    <#
        Reads the main tip from the bare origin itself. Remote-tracking refs inside the work clone only move
        on fetch, so assertions about what the remote holds must read the origin repository directly.
    #>
    param([Parameter(Mandatory)]$Fixture)
    return Invoke-FixtureGit -GitArguments @('rev-parse', 'main') -Repository $Fixture.OriginPath
}

function Get-FixtureStatus {
    param([Parameter(Mandatory)]$Fixture)
    return Invoke-FixtureGit -GitArguments @('status', '--porcelain') -Repository $Fixture.WorkPath
}

function Invoke-BootstrapQuiet {
    param([Parameter(Mandatory)][string]$Mode, [Parameter(Mandatory)]$Fixture)
    return Invoke-AeroLinkSourceBootstrap -Mode $Mode -RepositoryRoot $Fixture.WorkPath `
        -CurrentScriptPath (Join-Path $Fixture.FixtureRoot 'launcher.ps1') -LauncherFiles @()
}

function Invoke-BootstrapExpectingRefusal {
    param([Parameter(Mandatory)][string]$Mode, [Parameter(Mandatory)]$Fixture, [Parameter(Mandatory)][string]$ExpectedFragment)
    try {
        Invoke-BootstrapQuiet -Mode $Mode -Fixture $Fixture | Out-Null
    }
    catch {
        $message = "$($_.Exception.Message)"
        if ($message -notmatch [regex]::Escape($ExpectedFragment)) {
            $script:failures.Add("$Mode refusal message did not name the expected diagnosis. Expected fragment: '$ExpectedFragment'; got: '$message'")
        }
        return
    }
    $script:failures.Add("$Mode launch should have been refused for this posture, but the bootstrap continued.")
}

try {
    # ---------------------------------------------------------------- DEVELOPMENT MODE
    # A1. clean main + remote equal: no source mutation, proceeds.
    $fixture = New-FixtureRepository
    $beforeHead = Get-FixtureHead $fixture
    $result = Invoke-BootstrapQuiet -Mode Development -Fixture $fixture
    Assert-True ($result.Action -eq 'AlreadyCurrent') "A1: expected AlreadyCurrent, got '$($result.Action)'."
    Assert-True ((Get-FixtureHead $fixture) -eq $beforeHead) 'A1: HEAD moved on an already-current repository.'

    # A2. clean main behind + strict ff possible: updates to origin/main.
    $fixture = New-FixtureRepository
    $beforeHead = Get-FixtureHead $fixture
    Push-RemoteCommit -Fixture $fixture -FileName 'docs/remote.txt' -Content 'newer'
    $result = Invoke-BootstrapQuiet -Mode Development -Fixture $fixture
    Assert-True ($result.Action -eq 'Updated') "A2: expected Updated, got '$($result.Action)'."
    Assert-True ((Get-FixtureHead $fixture) -eq (Get-FixtureRemoteMain $fixture)) 'A2: HEAD is not origin/main after the fast-forward.'
    Assert-True ((Get-FixtureHead $fixture) -ne $beforeHead) 'A2: the fast-forward did not move HEAD.'

    # A3. clean main + remote unavailable: no Git mutation; cached SHA continues the launch.
    $fixture = New-FixtureRepository
    Disconnect-Remote -Fixture $fixture
    $beforeHead = Get-FixtureHead $fixture
    $result = Invoke-BootstrapQuiet -Mode Development -Fixture $fixture
    Assert-True ($result.Action -eq 'ContinuedOffline') "A3: expected ContinuedOffline, got '$($result.Action)'."
    Assert-True ($result.RemoteReachable -eq $false) 'A3: offline run reported the remote as reachable.'
    Assert-True ($result.HeadSha -eq $beforeHead) 'A3: HEAD changed while the remote was unavailable.'
    Assert-True ((Get-FixtureStatus $fixture).Length -eq 0) 'A3: the offline attempt dirtied the worktree.'

    # A4. deliberate feature branch: preserved, no main switch, no merge/rebase.
    $fixture = New-FixtureRepository
    Push-RemoteCommit -Fixture $fixture -FileName 'docs/remote.txt' -Content 'newer'
    $null = Invoke-FixtureGit -GitArguments @('checkout', '-b', 'feature/operator-work') -Repository $fixture.WorkPath
    Set-Content -LiteralPath (Join-Path $fixture.WorkPath 'feature.txt') -Value 'deliberate' -Encoding ASCII
    $null = Invoke-FixtureGit -GitArguments @('add', '-A') -Repository $fixture.WorkPath
    $null = Invoke-FixtureGit -GitArguments @('commit', '-m', 'feature work') -Repository $fixture.WorkPath
    $featureHead = Get-FixtureHead $fixture
    $result = Invoke-BootstrapQuiet -Mode Development -Fixture $fixture
    Assert-True ($result.Action -eq 'FeatureBranchPreserved') "A4: expected FeatureBranchPreserved, got '$($result.Action)'."
    Assert-True ((Get-FixtureBranch $fixture) -eq 'feature/operator-work') 'A4: the deliberate branch was switched.'
    Assert-True ((Get-FixtureHead $fixture) -eq $featureHead) 'A4: the feature branch HEAD moved.'

    # A5. dirty main: modifications preserved byte-for-byte, no update, launch continues.
    $fixture = New-FixtureRepository
    Push-RemoteCommit -Fixture $fixture -FileName 'docs/remote.txt' -Content 'newer'
    $dirtyFile = Join-Path $fixture.WorkPath 'README.md'
    Set-Content -LiteralPath $dirtyFile -Value 'local uncommitted work' -Encoding ASCII
    $beforeHead = Get-FixtureHead $fixture
    $result = Invoke-BootstrapQuiet -Mode Development -Fixture $fixture
    Assert-True ($result.Action -eq 'LocalChangesPreserved') "A5: expected LocalChangesPreserved, got '$($result.Action)'."
    Assert-True ((Get-Content -LiteralPath $dirtyFile -Raw) -match 'local uncommitted work') 'A5: the local modification was not preserved.'
    Assert-True ((Get-FixtureHead $fixture) -eq $beforeHead) 'A5: HEAD moved while the worktree was dirty.'

    # A6. main with a local-only commit while the remote is also ahead: preserved, no rewrite.
    $fixture = New-FixtureRepository
    Push-RemoteCommit -Fixture $fixture -FileName 'docs/remote.txt' -Content 'newer'
    Set-Content -LiteralPath (Join-Path $fixture.WorkPath 'local.txt') -Value 'local commit' -Encoding ASCII
    $null = Invoke-FixtureGit -GitArguments @('add', '-A') -Repository $fixture.WorkPath
    $null = Invoke-FixtureGit -GitArguments @('commit', '-m', 'local-only commit') -Repository $fixture.WorkPath
    $localHead = Get-FixtureHead $fixture
    $result = Invoke-BootstrapQuiet -Mode Development -Fixture $fixture
    Assert-True ($result.Action -eq 'DivergencePreserved') "A6: expected DivergencePreserved for a diverged main, got '$($result.Action)'."
    Assert-True ((Get-FixtureHead $fixture) -eq $localHead) 'A6: the local-only commit did not survive.'
    Assert-True ((Test-Path -LiteralPath (Join-Path $fixture.WorkPath 'local.txt') -PathType Leaf)) 'A6: the local-only commit content vanished.'

    # A6b. local-only commit with an up-to-date remote must also be preserved, not silently rewound.
    $fixture = New-FixtureRepository
    Set-Content -LiteralPath (Join-Path $fixture.WorkPath 'local.txt') -Value 'local commit' -Encoding ASCII
    $null = Invoke-FixtureGit -GitArguments @('add', '-A') -Repository $fixture.WorkPath
    $null = Invoke-FixtureGit -GitArguments @('commit', '-m', 'local-only commit') -Repository $fixture.WorkPath
    $localHead = Get-FixtureHead $fixture
    $result = Invoke-BootstrapQuiet -Mode Development -Fixture $fixture
    Assert-True ($result.Action -eq 'LocalCommitsPreserved') "A6b: expected LocalCommitsPreserved, got '$($result.Action)'."
    Assert-True ((Get-FixtureHead $fixture) -eq $localHead) 'A6b: the ahead-of-remote commit did not survive.'

    # A7. diverged main: no merge/rebase/reset; the divergence survives.
    $fixture = New-FixtureRepository
    Push-RemoteCommit -Fixture $fixture -FileName 'docs/remote.txt' -Content 'newer'
    Set-Content -LiteralPath (Join-Path $fixture.WorkPath 'local.txt') -Value 'local commit' -Encoding ASCII
    $null = Invoke-FixtureGit -GitArguments @('add', '-A') -Repository $fixture.WorkPath
    $null = Invoke-FixtureGit -GitArguments @('commit', '-m', 'local-only commit') -Repository $fixture.WorkPath
    $localHead = Get-FixtureHead $fixture
    $result = Invoke-BootstrapQuiet -Mode Development -Fixture $fixture
    Assert-True ($result.Action -eq 'DivergencePreserved') "A7: expected DivergencePreserved, got '$($result.Action)'."
    Assert-True ((Get-FixtureHead $fixture) -eq $localHead) 'A7: the diverged main was rewritten.'
    Assert-True ((Get-FixtureRemoteMain $fixture) -ne (Get-FixtureHead $fixture)) 'A7: origin/main was moved onto the local commit.'

    # ---------------------------------------------------------------- HOME PRODUCTION MODE
    # B8. clean current main: permitted.
    $fixture = New-FixtureRepository
    $beforeHead = Get-FixtureHead $fixture
    $result = Invoke-BootstrapQuiet -Mode HomeCanonical -Fixture $fixture
    Assert-True ($result.Action -eq 'AlreadyCurrent') "B8: expected AlreadyCurrent, got '$($result.Action)'."
    Assert-True ((Get-FixtureHead $fixture) -eq $beforeHead) 'B8: HEAD moved on an already-current canonical repository.'

    # B9. clean main behind + strict ff: updates and is permitted.
    $fixture = New-FixtureRepository
    $beforeHead = Get-FixtureHead $fixture
    Push-RemoteCommit -Fixture $fixture -FileName 'docs/remote.txt' -Content 'newer'
    $result = Invoke-BootstrapQuiet -Mode HomeCanonical -Fixture $fixture
    Assert-True ($result.Action -eq 'Updated') "B9: expected Updated, got '$($result.Action)'."
    Assert-True ((Get-FixtureHead $fixture) -eq (Get-FixtureRemoteMain $fixture)) 'B9: HEAD is not origin/main after the canonical fast-forward.'
    Assert-True ((Get-FixtureHead $fixture) -ne $beforeHead) 'B9: the canonical fast-forward did not move HEAD.'

    # B10. clean main + remote unavailable: explicit cached-main run with the exact local SHA.
    $fixture = New-FixtureRepository
    Disconnect-Remote -Fixture $fixture
    $beforeHead = Get-FixtureHead $fixture
    $result = Invoke-BootstrapQuiet -Mode HomeCanonical -Fixture $fixture
    Assert-True ($result.Action -eq 'ContinuedOfflineCachedMain') "B10: expected ContinuedOfflineCachedMain, got '$($result.Action)'."
    Assert-True ($result.HeadSha -eq $beforeHead) 'B10: the cached-main run changed HEAD.'
    Assert-True ($result.RemoteReachable -eq $false) 'B10: a cached-main run reported the remote as reachable.'

    # B10b. offline main with local-only commits: refused, because the posture cannot be verified.
    $fixture = New-FixtureRepository
    Disconnect-Remote -Fixture $fixture
    Set-Content -LiteralPath (Join-Path $fixture.WorkPath 'local.txt') -Value 'local commit' -Encoding ASCII
    $null = Invoke-FixtureGit -GitArguments @('add', '-A') -Repository $fixture.WorkPath
    $null = Invoke-FixtureGit -GitArguments @('commit', '-m', 'local-only commit') -Repository $fixture.WorkPath
    $beforeHead = Get-FixtureHead $fixture
    Invoke-BootstrapExpectingRefusal -Mode HomeCanonical -Fixture $fixture -ExpectedFragment 'commits that are not on origin/main'
    Assert-True ((Get-FixtureHead $fixture) -eq $beforeHead) 'B10b: the refusal moved HEAD.'

    # B11. feature branch: refused before any product startup; branch untouched.
    $fixture = New-FixtureRepository
    Push-RemoteCommit -Fixture $fixture -FileName 'docs/remote.txt' -Content 'newer'
    $null = Invoke-FixtureGit -GitArguments @('checkout', '-b', 'feature/someone-else') -Repository $fixture.WorkPath
    $beforeHead = Get-FixtureHead $fixture
    Invoke-BootstrapExpectingRefusal -Mode HomeCanonical -Fixture $fixture -ExpectedFragment 'not canonical main'
    Assert-True ((Get-FixtureBranch $fixture) -eq 'feature/someone-else') 'B11: the refusal switched the branch.'
    Assert-True ((Get-FixtureHead $fixture) -eq $beforeHead) 'B11: the refusal moved the branch HEAD.'

    # B12. dirty main: refused; files untouched.
    $fixture = New-FixtureRepository
    $dirtyFile = Join-Path $fixture.WorkPath 'README.md'
    Set-Content -LiteralPath $dirtyFile -Value 'local uncommitted work' -Encoding ASCII
    $beforeHead = Get-FixtureHead $fixture
    Invoke-BootstrapExpectingRefusal -Mode HomeCanonical -Fixture $fixture -ExpectedFragment 'uncommitted modifications'
    Assert-True ((Get-Content -LiteralPath $dirtyFile -Raw) -match 'local uncommitted work') 'B12: the refusal altered the local modification.'
    Assert-True ((Get-FixtureHead $fixture) -eq $beforeHead) 'B12: the refusal moved HEAD.'

    # B13. local-only main commit: refused; commit untouched.
    $fixture = New-FixtureRepository
    Set-Content -LiteralPath (Join-Path $fixture.WorkPath 'local.txt') -Value 'local commit' -Encoding ASCII
    $null = Invoke-FixtureGit -GitArguments @('add', '-A') -Repository $fixture.WorkPath
    $null = Invoke-FixtureGit -GitArguments @('commit', '-m', 'local-only commit') -Repository $fixture.WorkPath
    $beforeHead = Get-FixtureHead $fixture
    Invoke-BootstrapExpectingRefusal -Mode HomeCanonical -Fixture $fixture -ExpectedFragment 'commits that are not on origin/main'
    Assert-True ((Get-FixtureHead $fixture) -eq $beforeHead) 'B13: the refusal discarded the local commit.'
    Assert-True ((Test-Path -LiteralPath (Join-Path $fixture.WorkPath 'local.txt') -PathType Leaf)) 'B13: the local commit content vanished.'

    # B14. diverged main: refused; history untouched.
    $fixture = New-FixtureRepository
    Push-RemoteCommit -Fixture $fixture -FileName 'docs/remote.txt' -Content 'newer'
    Set-Content -LiteralPath (Join-Path $fixture.WorkPath 'local.txt') -Value 'local commit' -Encoding ASCII
    $null = Invoke-FixtureGit -GitArguments @('add', '-A') -Repository $fixture.WorkPath
    $null = Invoke-FixtureGit -GitArguments @('commit', '-m', 'local-only commit') -Repository $fixture.WorkPath
    $beforeHead = Get-FixtureHead $fixture
    $remoteBefore = Get-OriginMain $fixture
    Invoke-BootstrapExpectingRefusal -Mode HomeCanonical -Fixture $fixture -ExpectedFragment 'diverged'
    Assert-True ((Get-FixtureHead $fixture) -eq $beforeHead) 'B14: the refusal rewrote local history.'
    Assert-True ((Get-OriginMain $fixture) -eq $remoteBefore) 'B14: the refusal moved origin/main.'

    # ---------------------------------------------------------------- HOME UNTRACKED SOURCE (P1)
    # B15. HOME canonical + untracked source: REFUSED; the file survives byte-for-byte; HEAD unchanged.
    $fixture = New-FixtureRepository
    $untrackedSource = Join-Path $fixture.WorkPath 'operator-notes.txt'
    Set-Content -LiteralPath $untrackedSource -Value 'keep me' -Encoding ASCII
    $beforeHead = Get-FixtureHead $fixture
    Invoke-BootstrapExpectingRefusal -Mode HomeCanonical -Fixture $fixture -ExpectedFragment 'untracked local file'
    Assert-True ((Get-Content -LiteralPath $untrackedSource -Raw) -match 'keep me') 'B15: the refusal deleted or altered the untracked file.'
    Assert-True ((Get-FixtureHead $fixture) -eq $beforeHead) 'B15: the refusal moved HEAD.'
    Assert-True ((Test-Path -LiteralPath $untrackedSource -PathType Leaf)) 'B15: the untracked file vanished.'

    # B16. HOME canonical + untracked source + remote unavailable: still refused, not a cached-main run.
    $fixture = New-FixtureRepository
    Disconnect-Remote -Fixture $fixture
    Set-Content -LiteralPath (Join-Path $fixture.WorkPath 'untracked.ts') -Value 'unattested source' -Encoding ASCII
    Invoke-BootstrapExpectingRefusal -Mode HomeCanonical -Fixture $fixture -ExpectedFragment 'untracked local file'

    # ---------------------------------------------------------------- POST-UPDATE REVALIDATION (P2)
    # B17. a tracked modification appearing between the fast-forward and the revalidation fails HOME closed.
    #      The observer seam simulates the concurrent-agent window deterministically: no sleeps, no races.
    $fixture = New-FixtureRepository
    Push-RemoteCommit -Fixture $fixture -FileName 'docs/remote.txt' -Content 'newer'
    $raceObserver = {
        param([string]$ObservedRepositoryRoot)
        Set-Content -LiteralPath (Join-Path $ObservedRepositoryRoot 'README.md') -Value 'race edit' -Encoding ASCII
    }
    $beforeHead = Get-FixtureHead $fixture
    try {
        Invoke-AeroLinkSourceBootstrap -Mode HomeCanonical -RepositoryRoot $fixture.WorkPath `
            -CurrentScriptPath (Join-Path $fixture.FixtureRoot 'launcher.ps1') -LauncherFiles @() `
            -FastForwardObserver $raceObserver | Out-Null
        $script:failures.Add('B17: HOME canonical continued after source appeared between the update and its revalidation.')
    }
    catch {
        $message = "$($_.Exception.Message)"
        if ($message -notmatch 'uncommitted modifications') {
            $script:failures.Add("B17: the post-update refusal did not name the moved precondition; got: '$message'")
        }
    }
    Assert-True ((Get-Content -LiteralPath (Join-Path $fixture.WorkPath 'README.md') -Raw) -match 'race edit') 'B17: the fail-closed path altered or reverted the concurrent edit.'
    Assert-True ((Get-FixtureHead $fixture) -eq (Get-FixtureRemoteMain $fixture)) 'B17: the fail-closed path moved HEAD away from the verified update.'
    Assert-True ((Get-FixtureHead $fixture) -ne $beforeHead) 'B17: the fixture did not actually fast-forward before the race.'

    # B18. untracked work appearing in the same window fails HOME closed the same way.
    $fixture = New-FixtureRepository
    Push-RemoteCommit -Fixture $fixture -FileName 'docs/remote.txt' -Content 'newer'
    $raceObserver = {
        param([string]$ObservedRepositoryRoot)
        Set-Content -LiteralPath (Join-Path $ObservedRepositoryRoot 'race-untracked.ts') -Value 'unattested' -Encoding ASCII
    }
    try {
        Invoke-AeroLinkSourceBootstrap -Mode HomeCanonical -RepositoryRoot $fixture.WorkPath `
            -CurrentScriptPath (Join-Path $fixture.FixtureRoot 'launcher.ps1') -LauncherFiles @() `
            -FastForwardObserver $raceObserver | Out-Null
        $script:failures.Add('B18: HOME canonical continued after untracked source appeared between the update and its revalidation.')
    }
    catch {
        $message = "$($_.Exception.Message)"
        if ($message -notmatch 'untracked local file') {
            $script:failures.Add("B18: the post-update refusal did not name the untracked source; got: '$message'")
        }
    }
    Assert-True ((Test-Path -LiteralPath (Join-Path $fixture.WorkPath 'race-untracked.ts') -PathType Leaf)) 'B18: the fail-closed path deleted the concurrent untracked file.'

    # B19. development mode treats the same window permissively: the update stands, the work is preserved.
    $fixture = New-FixtureRepository
    Push-RemoteCommit -Fixture $fixture -FileName 'docs/remote.txt' -Content 'newer'
    $raceObserver = {
        param([string]$ObservedRepositoryRoot)
        Set-Content -LiteralPath (Join-Path $ObservedRepositoryRoot 'README.md') -Value 'race edit' -Encoding ASCII
    }
    $result = Invoke-AeroLinkSourceBootstrap -Mode Development -RepositoryRoot $fixture.WorkPath `
        -CurrentScriptPath (Join-Path $fixture.FixtureRoot 'launcher.ps1') -LauncherFiles @() `
        -FastForwardObserver $raceObserver
    Assert-True ($result.Action -eq 'Updated') "B19: expected Updated for development with a concurrent edit, got '$($result.Action)'."
    Assert-True ((Get-Content -LiteralPath (Join-Path $fixture.WorkPath 'README.md') -Raw) -match 'race edit') 'B19: the concurrent development edit was not preserved.'

    # ---------------------------------------------------------------- FAILED-FETCH WINDOW (P1)
    # B20. source appearing while the fetch is failing is decided mode-aware, never "preserved and continued"
    #      into HOME production. The -FailedFetchObserver seam simulates the window deterministically: the
    #      remote is disconnected so the fetch fails, and the observer dirties the tree during that failure.
    # B20a. HOME: tracked source appears during the failed fetch: REFUSED, file and HEAD untouched.
    $fixture = New-FixtureRepository
    Push-RemoteCommit -Fixture $fixture -FileName 'docs/remote.txt' -Content 'newer'
    Disconnect-Remote -Fixture $fixture
    $beforeHead = Get-FixtureHead $fixture
    $fetchObserver = {
        param([string]$ObservedRepositoryRoot)
        Set-Content -LiteralPath (Join-Path $ObservedRepositoryRoot 'README.md') -Value 'race edit during failed fetch' -Encoding ASCII
    }
    try {
        Invoke-AeroLinkSourceBootstrap -Mode HomeCanonical -RepositoryRoot $fixture.WorkPath `
            -CurrentScriptPath (Join-Path $fixture.FixtureRoot 'launcher.ps1') -LauncherFiles @() `
            -FailedFetchObserver $fetchObserver | Out-Null
        $script:failures.Add('B20a: HOME canonical continued after tracked source appeared during a failed fetch.')
    }
    catch {
        $message = "$($_.Exception.Message)"
        if ($message -notmatch 'changed while AeroLink was checking for updates') {
            $script:failures.Add("B20a: the failed-fetch refusal did not name the moved precondition; got: '$message'")
        }
    }
    Assert-True ((Get-Content -LiteralPath (Join-Path $fixture.WorkPath 'README.md') -Raw) -match 'race edit during failed fetch') 'B20a: the refusal altered the concurrent edit.'
    Assert-True ((Get-FixtureHead $fixture) -eq $beforeHead) 'B20a: the refusal moved HEAD.'

    # B20b. DEVELOPMENT: the same window preserves the work and continues locally.
    $fixture = New-FixtureRepository
    Push-RemoteCommit -Fixture $fixture -FileName 'docs/remote.txt' -Content 'newer'
    Disconnect-Remote -Fixture $fixture
    $beforeHead = Get-FixtureHead $fixture
    $fetchObserver = {
        param([string]$ObservedRepositoryRoot)
        Set-Content -LiteralPath (Join-Path $ObservedRepositoryRoot 'README.md') -Value 'race edit during failed fetch' -Encoding ASCII
    }
    $result = Invoke-AeroLinkSourceBootstrap -Mode Development -RepositoryRoot $fixture.WorkPath `
        -CurrentScriptPath (Join-Path $fixture.FixtureRoot 'launcher.ps1') -LauncherFiles @() `
        -FailedFetchObserver $fetchObserver
    Assert-True ($result.Action -eq 'LocalChangesPreserved') "B20b: expected LocalChangesPreserved, got '$($result.Action)'."
    Assert-True ((Get-Content -LiteralPath (Join-Path $fixture.WorkPath 'README.md') -Raw) -match 'race edit during failed fetch') 'B20b: the concurrent development edit was not preserved.'
    Assert-True ((Get-FixtureHead $fixture) -eq $beforeHead) 'B20b: HEAD moved during the failed-fetch window.'

    # B20c. HOME: untracked source appearing during the failed fetch is refused by the same offline invariant.
    $fixture = New-FixtureRepository
    Push-RemoteCommit -Fixture $fixture -FileName 'docs/remote.txt' -Content 'newer'
    Disconnect-Remote -Fixture $fixture
    $fetchObserver = {
        param([string]$ObservedRepositoryRoot)
        Set-Content -LiteralPath (Join-Path $ObservedRepositoryRoot 'race-untracked.ts') -Value 'unattested' -Encoding ASCII
    }
    try {
        Invoke-AeroLinkSourceBootstrap -Mode HomeCanonical -RepositoryRoot $fixture.WorkPath `
            -CurrentScriptPath (Join-Path $fixture.FixtureRoot 'launcher.ps1') -LauncherFiles @() `
            -FailedFetchObserver $fetchObserver | Out-Null
        $script:failures.Add('B20c: HOME canonical continued after untracked source appeared during a failed fetch.')
    }
    catch {
        $message = "$($_.Exception.Message)"
        if ($message -notmatch 'untracked local file') {
            $script:failures.Add("B20c: the failed-fetch refusal did not name the untracked source; got: '$message'")
        }
    }
    Assert-True ((Test-Path -LiteralPath (Join-Path $fixture.WorkPath 'race-untracked.ts') -PathType Leaf)) 'B20c: the refusal deleted the concurrent untracked file.'

    # B21/B22. a CLEAN HEAD move during the failed-fetch window: the dirt/untracked/branch checks cannot see
    # it, so HOME pins the exact pre-fetch source identity. The fixture moves HEAD to an ANCESTOR commit so
    # the refreshed posture would otherwise classify as Behind and pass the offline invariant.
    # B21. HOME: clean HEAD move during failed fetch: REFUSED; the moved checkout is preserved, never rewound.
    $fixture = New-FixtureRepository
    Set-Content -LiteralPath (Join-Path $fixture.WorkPath 'second.txt') -Value 'second commit' -Encoding ASCII
    $null = Invoke-FixtureGit -GitArguments @('add', '-A') -Repository $fixture.WorkPath
    $null = Invoke-FixtureGit -GitArguments @('commit', '-m', 'second') -Repository $fixture.WorkPath
    $null = Invoke-FixtureGit -GitArguments @('push', 'origin', 'main') -Repository $fixture.WorkPath
    $headBeforeFetch = Get-FixtureHead $fixture
    $ancestorSha = Invoke-FixtureGit -GitArguments @('rev-list', '--max-parents=0', 'HEAD') -Repository $fixture.WorkPath
    Assert-True ($ancestorSha -ne $headBeforeFetch) 'B21: fixture setup failed to produce an ancestor commit.'
    Disconnect-Remote -Fixture $fixture
    $fetchObserver = {
        param([string]$ObservedRepositoryRoot)
        # Another process cleanly moves main backwards; no dirt, no untracked files, relationship Behind.
        $null = Invoke-FixtureGit -GitArguments @('reset', '--hard', $env:AEROLINK_TEST_ANCESTOR_SHA) -Repository $ObservedRepositoryRoot
    }
    $env:AEROLINK_TEST_ANCESTOR_SHA = $ancestorSha
    try {
        Invoke-AeroLinkSourceBootstrap -Mode HomeCanonical -RepositoryRoot $fixture.WorkPath `
            -CurrentScriptPath (Join-Path $fixture.FixtureRoot 'launcher.ps1') -LauncherFiles @() `
            -FailedFetchObserver $fetchObserver | Out-Null
        $script:failures.Add('B21: HOME canonical continued after a clean HEAD move during a failed fetch.')
    }
    catch {
        $message = "$($_.Exception.Message)"
        if ($message -notmatch 'source revision changed while AeroLink was checking for updates') {
            $script:failures.Add("B21: the refusal did not name the moved source revision; got: '$message'")
        }
        if ($message -notmatch [regex]::Escape($headBeforeFetch.Substring(0, 8)) -or $message -notmatch [regex]::Escape($ancestorSha.Substring(0, 8))) {
            $script:failures.Add("B21: the refusal did not state expected-vs-actual identity; got: '$message'")
        }
    }
    finally {
        Remove-Item -Path 'Env:AEROLINK_TEST_ANCESTOR_SHA' -ErrorAction SilentlyContinue
    }
    Assert-True ((Get-FixtureHead $fixture) -eq $ancestorSha) 'B21: the moved checkout was not preserved (an automatic rollback/reset occurred).'
    Assert-True ((Get-FixtureBranch $fixture) -eq 'main') 'B21: the refusal left the repository off main.'
    Assert-True ((Get-FixtureStatus $fixture).Length -eq 0) 'B21: the refusal left the worktree dirty.'
    Assert-True ((Get-OriginMain $fixture) -eq $headBeforeFetch) 'B21: the refusal moved the remote.'

    # B22. DEVELOPMENT: the same clean HEAD move is preserved and reported honestly at the actual SHA.
    $fixture = New-FixtureRepository
    Set-Content -LiteralPath (Join-Path $fixture.WorkPath 'second.txt') -Value 'second commit' -Encoding ASCII
    $null = Invoke-FixtureGit -GitArguments @('add', '-A') -Repository $fixture.WorkPath
    $null = Invoke-FixtureGit -GitArguments @('commit', '-m', 'second') -Repository $fixture.WorkPath
    $null = Invoke-FixtureGit -GitArguments @('push', 'origin', 'main') -Repository $fixture.WorkPath
    $headBeforeFetch = Get-FixtureHead $fixture
    $ancestorSha = Invoke-FixtureGit -GitArguments @('rev-list', '--max-parents=0', 'HEAD') -Repository $fixture.WorkPath
    Disconnect-Remote -Fixture $fixture
    $fetchObserver = {
        param([string]$ObservedRepositoryRoot)
        $null = Invoke-FixtureGit -GitArguments @('reset', '--hard', $env:AEROLINK_TEST_ANCESTOR_SHA) -Repository $ObservedRepositoryRoot
    }
    $env:AEROLINK_TEST_ANCESTOR_SHA = $ancestorSha
    try {
        # 6>&1 captures the operator diagnostics so the printed SHA can be proven to be the refreshed one.
        $captured = Invoke-AeroLinkSourceBootstrap -Mode Development -RepositoryRoot $fixture.WorkPath `
            -CurrentScriptPath (Join-Path $fixture.FixtureRoot 'launcher.ps1') -LauncherFiles @() `
            -FailedFetchObserver $fetchObserver 6>&1
        $messages = @($captured | ForEach-Object { "$_" })
        $result = $messages | Where-Object { $_ -match '^\s*$' -eq $false -and $_ -match 'GitHub unavailable' } | Select-Object -First 1
        $returned = $captured | Where-Object { $_ -is [pscustomobject] -and $_.PSObject.Properties['Action'] } | Select-Object -First 1
        Assert-True ($null -ne $returned -and $returned.Action -eq 'ContinuedOffline') "B22: expected ContinuedOffline, got '$($returned.Action)'."
        Assert-True ($null -ne $returned -and $returned.HeadSha -eq $ancestorSha) 'B22: the result did not report the actual refreshed HEAD.'
        Assert-True ((Get-FixtureHead $fixture) -eq $ancestorSha) 'B22: the moved checkout was not preserved.'
        Assert-True ((Get-FixtureStatus $fixture).Length -eq 0) 'B22: the worktree was left dirty.'
        Assert-True ($null -ne $result -and $result -match "local main @ $($ancestorSha.Substring(0, 8))") "B22: the offline diagnostic did not report the actual refreshed SHA; got: '$result'"
        Assert-True ($null -eq ($messages | Where-Object { $_ -match "local main @ $($headBeforeFetch.Substring(0, 8))" })) 'B22: a stale pre-fetch SHA survived into the offline diagnostic.'
    }
    finally {
        Remove-Item -Path 'Env:AEROLINK_TEST_ANCESTOR_SHA' -ErrorAction SilentlyContinue
    }

    # ---------------------------------------------------------------- BOOTSTRAP RE-ENTRY
    # C15. a valid fast-forward that modifies the bootstrap implementation reruns the updated launcher
    #      exactly once, in a fresh process, validating the expected source identity and carrying the mode
    #      arguments and exit code over. (That the re-entered child performs no further fetch or update is
    #      proven behaviorally in C17f, where the remote is strictly ahead and must not be adopted.)
    $fixture = New-FixtureRepository
    $launcherRelPath = 'product\scripts\Start-AeroLink.ps1'
    $launcherPath = Join-Path $fixture.WorkPath $launcherRelPath
    New-Item -ItemType Directory -Path (Split-Path -Parent $launcherPath) -Force | Out-Null
    Set-Content -LiteralPath $launcherPath -Value 'old launcher' -Encoding ASCII
    $null = Invoke-FixtureGit -GitArguments @('add', '-A') -Repository $fixture.WorkPath
    $null = Invoke-FixtureGit -GitArguments @('commit', '-m', 'old launcher') -Repository $fixture.WorkPath
    $null = Invoke-FixtureGit -GitArguments @('push', 'origin', 'main') -Repository $fixture.WorkPath
    Push-RemoteCommit -Fixture $fixture -FileName 'product/scripts/Start-AeroLink.ps1' -Content 'updated launcher'
    $expectedSha = Get-OriginMain $fixture
    $markerPath = Join-Path $fixture.FixtureRoot 'reentry-marker.txt'
    $childScript = Join-Path $fixture.FixtureRoot 'launcher.ps1'
    Set-Content -LiteralPath $childScript -Encoding ASCII -Value @'
Set-Content -Path "$env:AEROLINK_TEST_MARKER_PATH" -Value "reentry=$env:AEROLINK_BOOTSTRAP_REENTRY expected=$env:AEROLINK_BOOTSTRAP_EXPECTED_SHA args=$args head=$(git -C "$env:AEROLINK_TEST_WORK_PATH" rev-parse HEAD)"
exit 7
'@
    $env:AEROLINK_TEST_MARKER_PATH = $markerPath
    $env:AEROLINK_TEST_WORK_PATH = $fixture.WorkPath
    try {
        $result = Invoke-AeroLinkSourceBootstrap -Mode Development -RepositoryRoot $fixture.WorkPath `
            -CurrentScriptPath $childScript -ScriptArguments @('-Mode', 'Test Mode') `
            -LauncherFiles @($launcherRelPath)
        Assert-True ($result.Action -eq 'Reentered') "C15: expected Reentered, got '$($result.Action)'."
        Assert-True ($result.ExitCode -eq 7) "C15: the child exit code was not propagated (got $($result.ExitCode))."
        Assert-True ($result.UpdatedToSha -eq $expectedSha.Substring(0, 8)) 'C15: the update did not land on the verified source identity.'
        Assert-True (Test-Path -LiteralPath $markerPath -PathType Leaf) 'C15: the re-entered bootstrap never ran.'
        if (Test-Path -LiteralPath $markerPath -PathType Leaf) {
            $marker = Get-Content -LiteralPath $markerPath -Raw
            # The fixture child is the launcher BODY only; a real launcher's bootstrap call consumes the
            # one-shot markers before this body runs, which C16 proves in-process. What C15 proves here is
            # that the parent carried the loop marker AND the verified source identity into the child.
            Assert-True ($marker -match 'reentry=1') 'C15: the child did not carry the one-shot re-entry marker.'
            Assert-True ($marker -match "expected=$expectedSha") 'C15: the child did not carry the verified expected source identity.'
            Assert-True ($marker -match 'args=-Mode Test Mode') 'C15: the child did not receive the original arguments.'
            Assert-True ($marker -match "head=$expectedSha") 'C15: the child did not run the exact verified source.'
        }
        Assert-True ((Get-FixtureHead $fixture) -eq $expectedSha) 'C15: the fast-forward did not reach the verified source identity.'
    }
    finally {
        Remove-Item -Path 'Env:AEROLINK_TEST_MARKER_PATH', 'Env:AEROLINK_TEST_WORK_PATH' -ErrorAction SilentlyContinue
    }

    # C16. a re-entry marker with a matching expected SHA skips the update cycle but never skips validation;
    #      the markers are consumed so nothing later can inherit a bypass.
    $fixture = New-FixtureRepository
    Push-RemoteCommit -Fixture $fixture -FileName 'docs/remote.txt' -Content 'newer'
    $headBefore = Get-FixtureHead $fixture
    $env:AEROLINK_BOOTSTRAP_REENTRY = '1'
    $env:AEROLINK_BOOTSTRAP_EXPECTED_SHA = $headBefore
    try {
        $result = Invoke-BootstrapQuiet -Mode Development -Fixture $fixture
        Assert-True ($result.Action -eq 'ReentryValidated') "C16: expected ReentryValidated, got '$($result.Action)'."
        Assert-True ($env:AEROLINK_BOOTSTRAP_REENTRY -eq $null) 'C16: the one-shot re-entry marker was not consumed.'
    }
    finally {
        Remove-Item -Path 'Env:AEROLINK_BOOTSTRAP_REENTRY', 'Env:AEROLINK_BOOTSTRAP_EXPECTED_SHA' -ErrorAction SilentlyContinue
    }

    # C16b. a re-entry marker without an expected SHA is never a generic local-run authority, in development
    #       mode either: the parent-created re-entry contract is exact source identity.
    $fixture = New-FixtureRepository
    $env:AEROLINK_BOOTSTRAP_REENTRY = '1'
    try {
        Invoke-BootstrapExpectingRefusal -Mode Development -Fixture $fixture -ExpectedFragment 're-entry source identity is incomplete'
    }
    finally {
        Remove-Item -Path 'Env:AEROLINK_BOOTSTRAP_REENTRY', 'Env:AEROLINK_BOOTSTRAP_EXPECTED_SHA' -ErrorAction SilentlyContinue
    }

    # C19. the expected-SHA marker itself is part of the re-entry authority boundary: missing, blank, or
    #      malformed identities fail closed even when the rest of the HOME posture is perfectly clean.
    # C19a. HOME: marker present, expected SHA missing.
    $fixture = New-FixtureRepository
    $env:AEROLINK_BOOTSTRAP_REENTRY = '1'
    try {
        Invoke-BootstrapExpectingRefusal -Mode HomeCanonical -Fixture $fixture -ExpectedFragment 're-entry source identity is incomplete'
    }
    finally {
        Remove-Item -Path 'Env:AEROLINK_BOOTSTRAP_REENTRY', 'Env:AEROLINK_BOOTSTRAP_EXPECTED_SHA' -ErrorAction SilentlyContinue
    }
    # C19b. HOME: marker present, expected SHA blank.
    $fixture = New-FixtureRepository
    $env:AEROLINK_BOOTSTRAP_REENTRY = '1'
    $env:AEROLINK_BOOTSTRAP_EXPECTED_SHA = '   '
    try {
        Invoke-BootstrapExpectingRefusal -Mode HomeCanonical -Fixture $fixture -ExpectedFragment 're-entry source identity is incomplete'
    }
    finally {
        Remove-Item -Path 'Env:AEROLINK_BOOTSTRAP_REENTRY', 'Env:AEROLINK_BOOTSTRAP_EXPECTED_SHA' -ErrorAction SilentlyContinue
    }
    # C19c. HOME: marker present, expected SHA malformed.
    $fixture = New-FixtureRepository
    $env:AEROLINK_BOOTSTRAP_REENTRY = '1'
    $env:AEROLINK_BOOTSTRAP_EXPECTED_SHA = 'not-a-commit-sha'
    try {
        Invoke-BootstrapExpectingRefusal -Mode HomeCanonical -Fixture $fixture -ExpectedFragment 're-entry source identity is malformed'
    }
    finally {
        Remove-Item -Path 'Env:AEROLINK_BOOTSTRAP_REENTRY', 'Env:AEROLINK_BOOTSTRAP_EXPECTED_SHA' -ErrorAction SilentlyContinue
    }

    # C17. re-entry validates the full HOME canonical policy even with the marker present. Each unsafe
    #      posture must still be refused, and the legitimate posture must validate without any fetch.
    # C17a. feature branch + marker: refused.
    $fixture = New-FixtureRepository
    $null = Invoke-FixtureGit -GitArguments @('checkout', '-b', 'feature/reentry-bypass') -Repository $fixture.WorkPath
    $env:AEROLINK_BOOTSTRAP_REENTRY = '1'
    $env:AEROLINK_BOOTSTRAP_EXPECTED_SHA = Get-FixtureHead $fixture
    try {
        Invoke-BootstrapExpectingRefusal -Mode HomeCanonical -Fixture $fixture -ExpectedFragment 'not canonical main'
        Assert-True ((Get-FixtureBranch $fixture) -eq 'feature/reentry-bypass') 'C17a: the refusal switched the branch.'
    }
    finally {
        Remove-Item -Path 'Env:AEROLINK_BOOTSTRAP_REENTRY', 'Env:AEROLINK_BOOTSTRAP_EXPECTED_SHA' -ErrorAction SilentlyContinue
    }
    # C17b. dirty tracked tree + marker: refused, file untouched.
    $fixture = New-FixtureRepository
    Set-Content -LiteralPath (Join-Path $fixture.WorkPath 'README.md') -Value 'dirty during reentry' -Encoding ASCII
    $env:AEROLINK_BOOTSTRAP_REENTRY = '1'
    $env:AEROLINK_BOOTSTRAP_EXPECTED_SHA = Get-FixtureHead $fixture
    try {
        Invoke-BootstrapExpectingRefusal -Mode HomeCanonical -Fixture $fixture -ExpectedFragment 'uncommitted modifications'
        Assert-True ((Get-Content -LiteralPath (Join-Path $fixture.WorkPath 'README.md') -Raw) -match 'dirty during reentry') 'C17b: the refusal altered the local modification.'
    }
    finally {
        Remove-Item -Path 'Env:AEROLINK_BOOTSTRAP_REENTRY', 'Env:AEROLINK_BOOTSTRAP_EXPECTED_SHA' -ErrorAction SilentlyContinue
    }
    # C17c. untracked source + marker: refused, file untouched.
    $fixture = New-FixtureRepository
    Set-Content -LiteralPath (Join-Path $fixture.WorkPath 'untracked.ts') -Value 'unattested source' -Encoding ASCII
    $env:AEROLINK_BOOTSTRAP_REENTRY = '1'
    $env:AEROLINK_BOOTSTRAP_EXPECTED_SHA = Get-FixtureHead $fixture
    try {
        Invoke-BootstrapExpectingRefusal -Mode HomeCanonical -Fixture $fixture -ExpectedFragment 'untracked local file'
        Assert-True ((Get-Content -LiteralPath (Join-Path $fixture.WorkPath 'untracked.ts') -Raw) -match 'unattested source') 'C17c: the refusal altered the untracked file.'
    }
    finally {
        Remove-Item -Path 'Env:AEROLINK_BOOTSTRAP_REENTRY', 'Env:AEROLINK_BOOTSTRAP_EXPECTED_SHA' -ErrorAction SilentlyContinue
    }
    # C17d. detached HEAD + marker: refused.
    $fixture = New-FixtureRepository
    $null = Invoke-FixtureGit -GitArguments @('checkout', '--detach') -Repository $fixture.WorkPath
    $env:AEROLINK_BOOTSTRAP_REENTRY = '1'
    $env:AEROLINK_BOOTSTRAP_EXPECTED_SHA = Get-FixtureHead $fixture
    try {
        Invoke-BootstrapExpectingRefusal -Mode HomeCanonical -Fixture $fixture -ExpectedFragment 'detached HEAD'
    }
    finally {
        Remove-Item -Path 'Env:AEROLINK_BOOTSTRAP_REENTRY', 'Env:AEROLINK_BOOTSTRAP_EXPECTED_SHA' -ErrorAction SilentlyContinue
    }
    # C17e. expected-SHA mismatch + marker: refused even on an otherwise clean main.
    $fixture = New-FixtureRepository
    $env:AEROLINK_BOOTSTRAP_REENTRY = '1'
    $env:AEROLINK_BOOTSTRAP_EXPECTED_SHA = '0000000000000000000000000000000000000000'
    try {
        Invoke-BootstrapExpectingRefusal -Mode HomeCanonical -Fixture $fixture -ExpectedFragment 'source identity mismatch'
    }
    finally {
        Remove-Item -Path 'Env:AEROLINK_BOOTSTRAP_REENTRY', 'Env:AEROLINK_BOOTSTRAP_EXPECTED_SHA' -ErrorAction SilentlyContinue
    }
    # C17f. clean main + matching expected SHA + marker: validates without fetching, even though the remote
    #       is strictly ahead; the re-entered run must not adopt it.
    $fixture = New-FixtureRepository
    Push-RemoteCommit -Fixture $fixture -FileName 'docs/remote.txt' -Content 'newer'
    $headBefore = Get-FixtureHead $fixture
    $env:AEROLINK_BOOTSTRAP_REENTRY = '1'
    $env:AEROLINK_BOOTSTRAP_EXPECTED_SHA = $headBefore
    try {
        $result = Invoke-BootstrapQuiet -Mode HomeCanonical -Fixture $fixture
        Assert-True ($result.Action -eq 'ReentryValidated') "C17f: expected ReentryValidated, got '$($result.Action)'."
        Assert-True ((Get-FixtureHead $fixture) -eq $headBefore) 'C17f: the re-entered run moved HEAD.'
        Assert-True ((Get-OriginMain $fixture) -ne $headBefore) 'C17f: fixture setup failed to leave the remote ahead.'
    }
    finally {
        Remove-Item -Path 'Env:AEROLINK_BOOTSTRAP_REENTRY', 'Env:AEROLINK_BOOTSTRAP_EXPECTED_SHA' -ErrorAction SilentlyContinue
    }

    # C18. the re-entry identity covers transitive launcher dependencies: a remote commit that changes ONLY
    #      product\scripts\AeroLinkNativeRunner.psm1 (already loaded in memory before the bootstrap runs,
    #      imported by AeroLinkLaunch.ps1) must still trigger re-entry rather than continue half-old/half-new.
    $fixture = New-FixtureRepository
    $runnerRelPath = 'product\scripts\AeroLinkNativeRunner.psm1'
    $runnerPath = Join-Path $fixture.WorkPath $runnerRelPath
    New-Item -ItemType Directory -Path (Split-Path -Parent $runnerPath) -Force | Out-Null
    Set-Content -LiteralPath $runnerPath -Value 'old runner' -Encoding ASCII
    $null = Invoke-FixtureGit -GitArguments @('add', '-A') -Repository $fixture.WorkPath
    $null = Invoke-FixtureGit -GitArguments @('commit', '-m', 'old runner') -Repository $fixture.WorkPath
    $null = Invoke-FixtureGit -GitArguments @('push', 'origin', 'main') -Repository $fixture.WorkPath
    Push-RemoteCommit -Fixture $fixture -FileName 'product/scripts/AeroLinkNativeRunner.psm1' -Content 'updated runner'
    $markerPath = Join-Path $fixture.FixtureRoot 'reentry-marker.txt'
    $childScript = Join-Path $fixture.FixtureRoot 'launcher.ps1'
    Set-Content -LiteralPath $childScript -Encoding ASCII -Value @'
Set-Content -Path "$env:AEROLINK_TEST_MARKER_PATH" -Value "ran"
exit 0
'@
    $env:AEROLINK_TEST_MARKER_PATH = $markerPath
    try {
        $result = Invoke-AeroLinkSourceBootstrap -Mode Development -RepositoryRoot $fixture.WorkPath `
            -CurrentScriptPath $childScript -LauncherFiles @($runnerRelPath)
        Assert-True ($result.Action -eq 'Reentered') "C18: a NativeRunner-only change must trigger re-entry, got '$($result.Action)'."
        Assert-True (Test-Path -LiteralPath $markerPath -PathType Leaf) 'C18: the re-entered bootstrap never ran.'
    }
    finally {
        Remove-Item -Path 'Env:AEROLINK_TEST_MARKER_PATH' -ErrorAction SilentlyContinue
    }

    # ---------------------------------------------------------------- DEPENDENCIES
    $depRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("aerolink-bootstrap-deps-" + [Guid]::NewGuid().ToString('N'))
    $script:fixtures.Add($depRoot)
    $clientRoot = Join-Path $depRoot 'client'
    $stateDir = Join-Path $depRoot 'state'
    $counterPath = Join-Path $depRoot 'refresh-count.txt'
    New-Item -ItemType Directory -Path $clientRoot | Out-Null
    Set-Content -LiteralPath (Join-Path $clientRoot 'package-lock.json') -Value '{ "lockfile": 1 }' -Encoding ASCII
    $env:AEROLINK_TEST_DEP_COUNTER = $counterPath
    $countingRefresh = {
        param([string]$TargetClientRoot)
        Add-Content -Path $env:AEROLINK_TEST_DEP_COUNTER -Value 'refresh'
        New-Item -ItemType Directory -Path (Join-Path $TargetClientRoot 'node_modules') -Force | Out-Null
    }
    try {
        # D20. a successful refresh persists the fingerprint, and only after success.
        $result = Update-AeroLinkClientDependencies -ClientRoot $clientRoot -StateDirectory $stateDir -RefreshCommand $countingRefresh
        Assert-True ($result.Refreshed) 'D20: the first preparation did not refresh.'
        Assert-True ((Get-Content -LiteralPath $counterPath -Raw).Trim() -eq 'refresh') 'D20: the refresh did not run exactly once.'
        $stamp = Get-Content -LiteralPath (Join-Path $stateDir 'client-dependencies.json') -Raw | ConvertFrom-Json
        $expectedHash = (Get-FileHash -LiteralPath (Join-Path $clientRoot 'package-lock.json') -Algorithm SHA256).Hash
        Assert-True ($stamp.lockfileSha256 -eq $expectedHash) 'D20: the stamp does not record the current lockfile fingerprint.'

        # D17. lockfile unchanged: no npm ci.
        $result = Update-AeroLinkClientDependencies -ClientRoot $clientRoot -StateDirectory $stateDir -RefreshCommand $countingRefresh
        Assert-True (-not $result.Refreshed) 'D17: an unchanged lockfile triggered a refresh.'
        Assert-True ((Get-Content -LiteralPath $counterPath).Count -eq 1) 'D17: the unchanged-lockfile path ran the refresh command.'

        # D18. lockfile changed: refresh requested exactly once.
        Set-Content -LiteralPath (Join-Path $clientRoot 'package-lock.json') -Value '{ "lockfile": 2 }' -Encoding ASCII
        $result = Update-AeroLinkClientDependencies -ClientRoot $clientRoot -StateDirectory $stateDir -RefreshCommand $countingRefresh
        Assert-True ($result.Refreshed) 'D18: a changed lockfile did not refresh.'
        Assert-True ((Get-Content -LiteralPath $counterPath).Count -eq 2) 'D18: the changed lockfile did not run the refresh exactly once.'

        # D19. a failed refresh is visible and never records the new fingerprint as prepared.
        Set-Content -LiteralPath (Join-Path $clientRoot 'package-lock.json') -Value '{ "lockfile": 3 }' -Encoding ASCII
        $failingRefresh = { throw 'npm ci failed with exit code 1.' }
        $refused = $false
        try {
            Update-AeroLinkClientDependencies -ClientRoot $clientRoot -StateDirectory $stateDir -RefreshCommand $failingRefresh | Out-Null
        }
        catch {
            $refused = "$($_.Exception.Message)" -match 'fingerprint was NOT updated'
        }
        Assert-True ($refused) 'D19: the failed refresh did not surface a fingerprint-not-updated failure.'
        $stamp = Get-Content -LiteralPath (Join-Path $stateDir 'client-dependencies.json') -Raw | ConvertFrom-Json
        Assert-True ($stamp.lockfileSha256 -ne (Get-FileHash -LiteralPath (Join-Path $clientRoot 'package-lock.json') -Algorithm SHA256).Hash) 'D19: the failed refresh recorded the new fingerprint as prepared.'
        Assert-True ((Get-Content -LiteralPath $counterPath).Count -eq 2) 'D19: the failing refresh path ran a refresh it should not have.'
    }
    finally {
        Remove-Item -Path 'Env:AEROLINK_TEST_DEP_COUNTER' -ErrorAction SilentlyContinue
    }

    # ---------------------------------------------------------------- SAFETY
    # E21. unknown/unexpected posture: detached HEAD and a non-repository directory both fail closed.
    $fixture = New-FixtureRepository
    $null = Invoke-FixtureGit -GitArguments @('checkout', '--detach') -Repository $fixture.WorkPath
    Invoke-BootstrapExpectingRefusal -Mode Development -Fixture $fixture -ExpectedFragment 'detached HEAD'
    Invoke-BootstrapExpectingRefusal -Mode HomeCanonical -Fixture $fixture -ExpectedFragment 'detached HEAD'
    $notARepo = Join-Path ([System.IO.Path]::GetTempPath()) ("aerolink-bootstrap-norepo-" + [Guid]::NewGuid().ToString('N'))
    $script:fixtures.Add($notARepo)
    New-Item -ItemType Directory -Path $notARepo | Out-Null
    try {
        Invoke-AeroLinkSourceBootstrap -Mode Development -RepositoryRoot $notARepo -CurrentScriptPath (Join-Path $notARepo 'launcher.ps1') | Out-Null
        $script:failures.Add('E21: a non-repository directory did not fail closed.')
    }
    catch { }

    # E21b. a repository without an origin remote cannot be characterized and fails closed in both modes.
    $fixture = New-FixtureRepository
    $null = Invoke-FixtureGit -GitArguments @('remote', 'remove', 'origin') -Repository $fixture.WorkPath
    Invoke-BootstrapExpectingRefusal -Mode Development -Fixture $fixture -ExpectedFragment "'origin' remote"
    Invoke-BootstrapExpectingRefusal -Mode HomeCanonical -Fixture $fixture -ExpectedFragment "'origin' remote"

    # E22. unrelated untracked local work is never deleted, including across a successful update.
    $fixture = New-FixtureRepository
    Push-RemoteCommit -Fixture $fixture -FileName 'docs/remote.txt' -Content 'newer'
    $untracked = Join-Path $fixture.WorkPath 'operator-notes.txt'
    Set-Content -LiteralPath $untracked -Value 'keep me' -Encoding ASCII
    $result = Invoke-BootstrapQuiet -Mode Development -Fixture $fixture
    Assert-True ($result.Action -eq 'Updated') "E22: expected Updated with untracked work present, got '$($result.Action)'."
    Assert-True ((Get-Content -LiteralPath $untracked -Raw) -match 'keep me') 'E22: the untracked file was deleted or altered by the update.'

    # E23/E24. static safety contract: the bootstrap never approaches the persistent database or evidence
    # roots, never mutates history, and every mutable location it uses is injected, not resolved from the
    # real checkout.
    $moduleText = [System.IO.File]::ReadAllText((Join-Path $PSScriptRoot 'AeroLinkBootstrap.psm1'))
    Assert-True (-not ($moduleText -match '54329')) 'E23: the bootstrap module must never reference the persistent PostgreSQL port.'
    Assert-True (-not ($moduleText -match 'Start-Postgres|Setup-Postgres|Stop-Postgres')) 'E23: the bootstrap module must never invoke PostgreSQL scripts.'
    Assert-True (-not ($moduleText -match '\.local\\logs')) 'E24: the bootstrap module must not write into the product logs area itself.'
    Assert-True ($moduleText -match '\$StateDirectory') 'E24: the dependency stamp location must be an injected parameter, not a hardcoded product path.'
    # The one automatic Git mutation this module may ever perform is the strict fast-forward merge; any other
    # history-mutating git verb as a literal argument is a contract violation.
    Assert-True ($moduleText -match "'merge', '--ff-only', 'origin/main'") 'E22/E24: the only permitted automatic Git mutation is the strict ff-only merge of origin/main.'
    Assert-True (-not ($moduleText -match "'reset'|'rebase'|'stash'|'pull'|'checkout',\s*'-f'|'checkout',\s*'--force'")) 'E22/E24: the bootstrap must never stash, rebase, hard-reset, force-checkout, or use a fallback pull.'
    # Re-entry safety: the marker must never skip policy validation, and the parent must pass the verified
    # source identity into the child.
    Assert-True ($moduleText -match 'Assert-AeroLinkHomeCanonicalSourcePolicy -Posture \$posture -ExpectedSha \$expectedShaFromParent -Context ''re-entry''') 'E21/E24: re-entry must run the full HOME canonical policy, not just skip the update.'
    Assert-True ($moduleText -match '-ExpectedSha \$updated\.HeadSha') 'E21/E24: the parent must carry the verified source identity into the re-entry.'
    # Self-update identity: every launcher implementation file already loaded into memory before the
    # bootstrap (directly or transitively) must be part of the re-entry identity of BOTH launchers.
    foreach ($launcherName in @('Start-AeroLink.ps1', 'Start-AeroLinkProduction.ps1')) {
        $launcherText = [System.IO.File]::ReadAllText((Join-Path $PSScriptRoot $launcherName))
        foreach ($required in @(
            'product\scripts\launch.cmd',
            'product\scripts\AeroLinkPrerequisites.ps1',
            'product\scripts\AeroLinkLaunch.ps1',
            'product\scripts\AeroLinkNativeRunner.psm1',
            'product\scripts\AeroLinkBootstrap.psm1'
        )) {
            Assert-True ($launcherText -match [regex]::Escape("'$required'")) "E24: $launcherName must include '$required' in its re-entry identity."
        }
    }
    # The audit behind the identity: AeroLinkLaunch.ps1 (dot-sourced by both launchers before the bootstrap)
    # imports AeroLinkNativeRunner.psm1, which is why it is already in memory before a fast-forward.
    $launchText = [System.IO.File]::ReadAllText((Join-Path $PSScriptRoot 'AeroLinkLaunch.ps1'))
    Assert-True ($launchText -match 'AeroLinkNativeRunner\.psm1') 'E24: the import-chain audit is stale: AeroLinkLaunch.ps1 no longer imports AeroLinkNativeRunner.psm1.'
}
finally {
    foreach ($fixtureRoot in $fixtures) {
        Remove-Item -LiteralPath $fixtureRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Host "FAIL: $_" -ForegroundColor Red }
    Write-Host "Launcher source-posture bootstrap contract FAILED ($($failures.Count) failure(s))." -ForegroundColor Red
    exit 1
}

Write-Host 'Launcher source-posture bootstrap contract passed.' -ForegroundColor Green
exit 0
