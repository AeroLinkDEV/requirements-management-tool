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
    Invoke-BootstrapExpectingRefusal -Mode HomeCanonical -Fixture $fixture -ExpectedFragment 'local-only commits'
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
    Invoke-BootstrapExpectingRefusal -Mode HomeCanonical -Fixture $fixture -ExpectedFragment 'local-only commits'
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

    # ---------------------------------------------------------------- BOOTSTRAP RE-ENTRY
    # C15. a valid fast-forward that modifies the bootstrap implementation reruns the updated launcher
    #      exactly once, in a fresh process, with the mode arguments carried over.
    $fixture = New-FixtureRepository
    $launcherRelPath = 'product\scripts\Start-AeroLink.ps1'
    $launcherPath = Join-Path $fixture.WorkPath $launcherRelPath
    New-Item -ItemType Directory -Path (Split-Path -Parent $launcherPath) -Force | Out-Null
    Set-Content -LiteralPath $launcherPath -Value 'old launcher' -Encoding ASCII
    $null = Invoke-FixtureGit -GitArguments @('add', '-A') -Repository $fixture.WorkPath
    $null = Invoke-FixtureGit -GitArguments @('commit', '-m', 'old launcher') -Repository $fixture.WorkPath
    $null = Invoke-FixtureGit -GitArguments @('push', 'origin', 'main') -Repository $fixture.WorkPath
    Push-RemoteCommit -Fixture $fixture -FileName 'product/scripts/Start-AeroLink.ps1' -Content 'updated launcher'
    $markerPath = Join-Path $fixture.FixtureRoot 'reentry-marker.txt'
    $childScript = Join-Path $fixture.FixtureRoot 'launcher.ps1'
    Set-Content -LiteralPath $childScript -Encoding ASCII -Value @'
Set-Content -Path "$env:AEROLINK_TEST_MARKER_PATH" -Value "marker=$env:AEROLINK_BOOTSTRAP_REENTRY args=$args"
exit 7
'@
    $env:AEROLINK_TEST_MARKER_PATH = $markerPath
    try {
        $result = Invoke-AeroLinkSourceBootstrap -Mode Development -RepositoryRoot $fixture.WorkPath `
            -CurrentScriptPath $childScript -ScriptArguments @('-Mode', 'Test Mode') `
            -LauncherFiles @($launcherRelPath)
        Assert-True ($result.Action -eq 'Reentered') "C15: expected Reentered, got '$($result.Action)'."
        Assert-True ($result.ExitCode -eq 7) "C15: the child exit code was not propagated (got $($result.ExitCode))."
        Assert-True (Test-Path -LiteralPath $markerPath -PathType Leaf) 'C15: the re-entered bootstrap never ran.'
        if (Test-Path -LiteralPath $markerPath -PathType Leaf) {
            $marker = Get-Content -LiteralPath $markerPath -Raw
            Assert-True ($marker -match 'marker=1') 'C15: the child did not carry the one-shot re-entry marker.'
            Assert-True ($marker -match 'args=-Mode Test Mode') 'C15: the child did not receive the original arguments.'
        }
        Assert-True ((Get-FixtureRemoteMain $fixture) -eq (Get-FixtureHead $fixture)) 'C15: the fast-forward did not reach origin/main.'
    }
    finally {
        Remove-Item -Path 'Env:AEROLINK_TEST_MARKER_PATH' -ErrorAction SilentlyContinue
    }

    # C16. a re-entry marker already present must never loop.
    $fixture = New-FixtureRepository
    Push-RemoteCommit -Fixture $fixture -FileName 'docs/remote.txt' -Content 'newer'
    $env:AEROLINK_BOOTSTRAP_REENTRY = '1'
    try {
        $result = Invoke-BootstrapQuiet -Mode Development -Fixture $fixture
        Assert-True ($result.Action -eq 'ReentryInProgress') "C16: expected ReentryInProgress, got '$($result.Action)'."
    }
    finally {
        Remove-Item -Path 'Env:AEROLINK_BOOTSTRAP_REENTRY' -ErrorAction SilentlyContinue
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
