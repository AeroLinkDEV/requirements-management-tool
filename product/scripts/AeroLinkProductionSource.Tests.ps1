#Requires -Version 5.1
<#
    Contract coverage for the dedicated HOME production source (#881, 2026-09-03 amendment).

    The scenario every one of these is really about:

        the HOME PC reboots at 11:52 while the development checkout is on feat/880-slice6-digital-thread-page
        with modified and untracked work, and the demo has to come back anyway.

    Every scenario runs against disposable Git repositories under the machine temp directory - a bare origin,
    a "development" clone and a "production" clone. The remote is made unreachable by pointing origin at a
    path that does not exist, so no test needs the network. No test connects to a database, touches the
    persistent product\.local, starts a product process, or registers a Scheduled Task.
#>
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'AeroLinkProductionSource.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'AeroLinkInstallation.psm1') -Force

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

function Invoke-FixtureGit {
    param([Parameter(Mandatory)][string[]]$GitArguments, [string]$Repository)
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        if ($Repository) { $output = & git -C $Repository @GitArguments 2>&1 } else { $output = & git @GitArguments 2>&1 }
        $exitCode = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $previous }
    $text = (($output | ForEach-Object { "$_" }) -join "`n").Trim()
    if ($exitCode -ne 0) { throw "fixture git $($GitArguments -join ' ') failed: $text" }
    return $text
}

function New-Fixture {
    <#
        A bare origin, a development clone, and an empty slot beside it for the production clone. The layout
        mirrors HOME: two checkouts of one repository, side by side, neither inside the other.
    #>
    $root = Join-Path ([IO.Path]::GetTempPath()) ("aerolink-prodsource-" + [Guid]::NewGuid().ToString('N'))
    $script:fixtures.Add($root)
    New-Item -ItemType Directory -Path $root | Out-Null
    $origin = Join-Path $root 'origin.git'
    $development = Join-Path $root 'Requirements Management Tool'
    $production = Join-Path $root 'AeroLink Production'
    $installation = Join-Path $development 'product\.local'

    $null = Invoke-FixtureGit -GitArguments @('init', '--bare', $origin)
    $null = Invoke-FixtureGit -GitArguments @('symbolic-ref', 'HEAD', 'refs/heads/main') -Repository $origin
    $null = Invoke-FixtureGit -GitArguments @('init', $development)
    $null = Invoke-FixtureGit -GitArguments @('symbolic-ref', 'HEAD', 'refs/heads/main') -Repository $development
    $null = Invoke-FixtureGit -GitArguments @('config', 'user.email', 'prodsource-fixture@example.com') -Repository $development
    $null = Invoke-FixtureGit -GitArguments @('config', 'user.name', 'Production Source Fixture') -Repository $development
    New-Item -ItemType Directory -Path (Join-Path $development 'product\scripts') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $development '.gitignore') -Value 'product/.local/' -Encoding ASCII
    Set-Content -LiteralPath (Join-Path $development 'product\scripts\marker.txt') -Value 'v1' -Encoding ASCII
    $null = Invoke-FixtureGit -GitArguments @('add', '-A') -Repository $development
    $null = Invoke-FixtureGit -GitArguments @('commit', '-m', 'initial') -Repository $development
    $null = Invoke-FixtureGit -GitArguments @('remote', 'add', 'origin', $origin) -Repository $development
    $null = Invoke-FixtureGit -GitArguments @('push', '-u', 'origin', 'main') -Repository $development

    # The canonical persistent installation, which lives with the development checkout on the real machine.
    New-Item -ItemType Directory -Path (Join-Path $installation 'pgdata') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $installation 'pgdata\PG_VERSION') -Value '18' -Encoding ASCII

    return [pscustomobject]@{
        Root = $root; Origin = $origin; Development = $development
        Production = $production; Installation = $installation
    }
}

function Push-RemoteCommit {
    param([Parameter(Mandatory)]$Fixture, [Parameter(Mandatory)][string]$Content)
    $pusher = Join-Path $Fixture.Root 'pusher'
    if (Test-Path -LiteralPath $pusher) { Remove-Item -LiteralPath $pusher -Recurse -Force }
    $null = Invoke-FixtureGit -GitArguments @('clone', $Fixture.Origin, $pusher)
    $null = Invoke-FixtureGit -GitArguments @('config', 'user.email', 'prodsource-fixture@example.com') -Repository $pusher
    $null = Invoke-FixtureGit -GitArguments @('config', 'user.name', 'Production Source Fixture') -Repository $pusher
    Set-Content -LiteralPath (Join-Path $pusher 'product\scripts\marker.txt') -Value $Content -Encoding ASCII
    $null = Invoke-FixtureGit -GitArguments @('add', '-A') -Repository $pusher
    $null = Invoke-FixtureGit -GitArguments @('commit', '-m', "remote $Content") -Repository $pusher
    $null = Invoke-FixtureGit -GitArguments @('push', 'origin', 'main') -Repository $pusher
    return Invoke-FixtureGit -GitArguments @('rev-parse', 'HEAD') -Repository $pusher
}

function New-ProductionSource {
    param([Parameter(Mandatory)]$Fixture)
    return Initialize-AeroLinkProductionSource -SourceRoot $Fixture.Production `
        -InstallationRoot $Fixture.Installation -OriginUrl $Fixture.Origin
}

function Get-DevelopmentSnapshot {
    <#
        Everything about the development checkout production must not disturb: branch, HEAD, the full status
        including untracked files, and the bytes of the working files.
    #>
    param([Parameter(Mandatory)]$Fixture)
    $files = @{}
    foreach ($file in (Get-ChildItem -LiteralPath $Fixture.Development -File -Recurse -Force |
            Where-Object { $_.FullName -notlike '*\.git\*' })) {
        $files[$file.FullName] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    }
    return [pscustomobject]@{
        Branch = (Invoke-FixtureGit -GitArguments @('rev-parse', '--abbrev-ref', 'HEAD') -Repository $Fixture.Development)
        Head   = (Invoke-FixtureGit -GitArguments @('rev-parse', 'HEAD') -Repository $Fixture.Development)
        Status = (Invoke-FixtureGit -GitArguments @('status', '--porcelain') -Repository $Fixture.Development)
        Stash  = (Invoke-FixtureGit -GitArguments @('stash', 'list') -Repository $Fixture.Development)
        Files  = $files
    }
}

function Assert-DevelopmentUnchanged {
    param([Parameter(Mandatory)]$Fixture, [Parameter(Mandatory)]$Before, [Parameter(Mandatory)][string]$Scenario)
    $after = Get-DevelopmentSnapshot -Fixture $Fixture
    Assert-True ($after.Branch -eq $Before.Branch) "$Scenario the development branch must be untouched (was $($Before.Branch), now $($after.Branch))."
    Assert-True ($after.Head -eq $Before.Head) "$Scenario the development HEAD must be untouched."
    Assert-True ($after.Status -eq $Before.Status) "$Scenario the development working tree, including untracked files, must be byte-identical."
    Assert-True ($after.Stash -eq $Before.Stash) "$Scenario nothing may be stashed in the development checkout."
    foreach ($path in $Before.Files.Keys) {
        Assert-True ((Test-Path -LiteralPath $path) -and (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -eq $Before.Files[$path]) `
            "$Scenario developer file $path must be byte-identical."
    }
    foreach ($path in $after.Files.Keys) {
        Assert-True ($Before.Files.ContainsKey($path)) "$Scenario production must not add files to the development checkout ($path)."
    }
}

try {
    # =====================================================================================================
    # 1. THE EXACT 2026-09-03 REGRESSION
    #    Development on a dirty feature branch with untracked source. Production recovers anyway, from its
    #    own canonical clone, and touches nothing of the developer's.
    # =====================================================================================================
    $fixture = New-Fixture
    $null = Invoke-FixtureGit -GitArguments @('checkout', '-b', 'feat/880-slice6-digital-thread-page') -Repository $fixture.Development
    Set-Content -LiteralPath (Join-Path $fixture.Development 'product\scripts\marker.txt') -Value 'work in progress' -Encoding ASCII
    Set-Content -LiteralPath (Join-Path $fixture.Development 'product\scripts\NewCanvas.tsx') -Value 'untracked source' -Encoding ASCII
    $devBefore = Get-DevelopmentSnapshot -Fixture $fixture

    $created = New-ProductionSource -Fixture $fixture
    Assert-True ($created.Cloned) 'The dedicated production source must be created by cloning origin.'
    Assert-True ($created.Canonical) "The freshly created production source must be canonical; it reported: $($created.Reason)"
    Assert-DevelopmentUnchanged -Fixture $fixture -Before $devBefore -Scenario '2026-09-03 regression:'

    $posture = Get-AeroLinkProductionSourcePosture -SourceRoot $fixture.Production
    Assert-True ($posture.Dedicated) 'The production source must declare itself dedicated.'
    Assert-True ($posture.Canonical) 'The production source must be canonical while the development checkout is a dirty feature branch.'
    Assert-True ($posture.Posture.Branch -eq 'main') 'The production clone tracks main independently of the development checkout.'

    # 2. Source is separated; DATA is not.
    $productionPaths = Get-AeroLinkInstallationPaths -ProductRoot (Join-Path $fixture.Production 'product')
    Assert-True ($productionPaths.InstallationRoot -eq [IO.Path]::GetFullPath($fixture.Installation)) `
        'The production source must resolve to the canonical installation, not to one of its own.'
    Assert-True (Test-Path -LiteralPath (Join-Path $productionPaths.PostgresData 'PG_VERSION')) `
        'The production source must address the existing canonical cluster; a missing PG_VERSION would mean a second, empty installation.'

    # 3. The development checkout is still free to use main. This is the worktree constraint the amendment
    #    called out, and the reason this is a clone rather than a worktree.
    $null = Invoke-FixtureGit -GitArguments @('stash', '--include-untracked') -Repository $fixture.Development
    $null = Invoke-FixtureGit -GitArguments @('checkout', 'main') -Repository $fixture.Development
    Assert-True ((Invoke-FixtureGit -GitArguments @('rev-parse', '--abbrev-ref', 'HEAD') -Repository $fixture.Development) -eq 'main') `
        'The production-source design must not prevent the development checkout from checking out main.'
    Assert-True ((Get-AeroLinkProductionSourcePosture -SourceRoot $fixture.Production).Canonical) `
        'Production stays canonical while the development checkout is on main.'
    $null = Invoke-FixtureGit -GitArguments @('checkout', '-b', 'feat/another') -Repository $fixture.Development
    Assert-True ((Get-AeroLinkProductionSourcePosture -SourceRoot $fixture.Production).Canonical) `
        'Development switching main -> feature must not affect the production source.'

    # =====================================================================================================
    # 4. Production behind origin/main advances by strict fast-forward and revalidates.
    # =====================================================================================================
    $fixture = New-Fixture
    $null = New-ProductionSource -Fixture $fixture
    $remoteSha = Push-RemoteCommit -Fixture $fixture -Content 'v2'
    $devBefore = Get-DevelopmentSnapshot -Fixture $fixture
    $update = Update-AeroLinkProductionSource -SourceRoot $fixture.Production
    Assert-True ($update.Action -eq 'Updated') "A production source behind origin/main must advance; it reported $($update.Action): $($update.Reason)"
    Assert-True ($update.HeadSha -eq $remoteSha) 'The production source must land on the exact origin/main revision.'
    Assert-True ($update.Canonical) 'The advanced production source must revalidate as canonical.'
    Assert-DevelopmentUnchanged -Fixture $fixture -Before $devBefore -Scenario 'main advance:'

    # Already current is a no-op, and says so.
    $again = Update-AeroLinkProductionSource -SourceRoot $fixture.Production
    Assert-True ($again.Action -eq 'AlreadyCurrent') 'A second reconciliation with no remote movement must do nothing.'
    Assert-True ($again.HeadSha -eq $remoteSha) 'A no-op reconciliation leaves the source where it was.'

    # =====================================================================================================
    # 5. GitHub unavailable: a previously verified clean cached main runs, and says it is unverified.
    # =====================================================================================================
    $null = Invoke-FixtureGit -GitArguments @('remote', 'set-url', 'origin', (Join-Path $fixture.Root 'no-such-remote')) -Repository $fixture.Production
    $offline = Update-AeroLinkProductionSource -SourceRoot $fixture.Production
    Assert-True ($offline.Action -eq 'CachedCanonical') "An unreachable remote must still allow a verified cached main; it reported $($offline.Action)."
    Assert-True ($offline.Canonical) 'Cached canonical source is usable.'
    Assert-True ($offline.RemoteReachable -eq $false) 'The offline result must record that the remote was not reached.'
    Assert-True ($offline.Reason -match 'could not be verified') 'The offline diagnostic must say the latest remote revision was not verified, not claim to be current.'

    # =====================================================================================================
    # 6. Unexpected state in the production source fails closed, and mutates nothing.
    # =====================================================================================================
    foreach ($case in @(
        @{ Name = 'tracked modification'; Setup = { Set-Content -LiteralPath (Join-Path $fixture.Production 'product\scripts\marker.txt') -Value 'tampered' -Encoding ASCII } }
        @{ Name = 'untracked source';     Setup = { Set-Content -LiteralPath (Join-Path $fixture.Production 'product\scripts\Sneaky.tsx') -Value 'x' -Encoding ASCII } }
        @{ Name = 'local-only commit';    Setup = {
                Set-Content -LiteralPath (Join-Path $fixture.Production 'product\scripts\marker.txt') -Value 'local' -Encoding ASCII
                $null = Invoke-FixtureGit -GitArguments @('config', 'user.email', 'x@example.com') -Repository $fixture.Production
                $null = Invoke-FixtureGit -GitArguments @('config', 'user.name', 'X') -Repository $fixture.Production
                $null = Invoke-FixtureGit -GitArguments @('commit', '-am', 'local only') -Repository $fixture.Production
            } }
        @{ Name = 'feature branch';       Setup = { $null = Invoke-FixtureGit -GitArguments @('checkout', '-b', 'feat/production-tinkering') -Repository $fixture.Production } }
    )) {
        $dirtyFixture = New-Fixture
        $null = New-ProductionSource -Fixture $dirtyFixture
        $fixture = $dirtyFixture
        & $case.Setup
        $headBefore = Invoke-FixtureGit -GitArguments @('rev-parse', 'HEAD') -Repository $dirtyFixture.Production
        $statusBefore = Invoke-FixtureGit -GitArguments @('status', '--porcelain') -Repository $dirtyFixture.Production

        $result = Update-AeroLinkProductionSource -SourceRoot $dirtyFixture.Production
        Assert-True ($result.Action -eq 'Refused' -and -not $result.Canonical) `
            "A production source with a $($case.Name) must be refused, not repaired; it reported $($result.Action)."
        Assert-True ((Invoke-FixtureGit -GitArguments @('rev-parse', 'HEAD') -Repository $dirtyFixture.Production) -eq $headBefore) `
            "A refused $($case.Name) must leave HEAD exactly as found."
        Assert-True ((Invoke-FixtureGit -GitArguments @('status', '--porcelain') -Repository $dirtyFixture.Production) -eq $statusBefore) `
            "A refused $($case.Name) must leave the working tree exactly as found - no stash, reset, or clean."
        Assert-True ((Invoke-FixtureGit -GitArguments @('stash', 'list') -Repository $dirtyFixture.Production) -eq '') `
            "A refused $($case.Name) must never stash."
        Assert-Throws { Assert-AeroLinkDedicatedProductionSource -SourceRoot $dirtyFixture.Production } 'not canonical' `
            "Production start must refuse a source with a $($case.Name)."
    }

    # =====================================================================================================
    # 7. Pointing production or recovery at the DEVELOPMENT checkout is a contract failure.
    #    This is the mutation the whole amendment is about: it must not be quietly possible.
    # =====================================================================================================
    $fixture = New-Fixture
    $null = New-ProductionSource -Fixture $fixture
    Assert-Throws { Assert-AeroLinkDedicatedProductionSource -SourceRoot $fixture.Development } 'not a dedicated AeroLink production source' `
        'Aiming production or recovery at the development checkout must be refused even when that checkout is clean main.'
    Assert-Throws { Update-AeroLinkProductionSource -SourceRoot $fixture.Development } 'not a dedicated AeroLink production source' `
        'Updating the development checkout through the production path must be refused.'
    $developmentPosture = Get-AeroLinkProductionSourcePosture -SourceRoot $fixture.Development
    Assert-True (-not $developmentPosture.Dedicated) 'The development checkout must never report itself as dedicated production source.'

    # =====================================================================================================
    # 8. Initialization refusals: never nest source inside data, never invent an installation.
    # =====================================================================================================
    $fixture = New-Fixture
    Assert-Throws { Initialize-AeroLinkProductionSource -SourceRoot (Join-Path $fixture.Installation 'production') -InstallationRoot $fixture.Installation -OriginUrl $fixture.Origin } `
        'must not nest' 'A production source inside the persistent installation must be refused.'
    Assert-Throws { Initialize-AeroLinkProductionSource -SourceRoot $fixture.Production -InstallationRoot (Join-Path $fixture.Root 'no-installation-here') -OriginUrl $fixture.Origin } `
        'will not initialize a second installation' 'Pointing a new production source at a missing installation must be refused.'
    $occupied = Join-Path $fixture.Root 'occupied'
    New-Item -ItemType Directory -Path $occupied -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $occupied 'somebody-elses-work.txt') -Value 'x' -Encoding ASCII
    Assert-Throws { Initialize-AeroLinkProductionSource -SourceRoot $occupied -InstallationRoot $fixture.Installation -OriginUrl $fixture.Origin } `
        'exists and is not empty' 'Cloning into a non-empty directory that is not a repository must be refused rather than merged into.'

    # Initialization is idempotent: running it again confirms rather than re-clones.
    $first = New-ProductionSource -Fixture $fixture
    $second = New-ProductionSource -Fixture $fixture
    Assert-True ($first.Cloned -and -not $second.Cloned) 'Re-running initialization must confirm the existing production source rather than clone again.'
    Assert-True ($second.Canonical) 'A confirmed production source is still canonical.'

    # =====================================================================================================
    # 9. "Dedicated" is a binding to a repository AND an installation, not a file that happens to exist.
    #
    # Existence alone was too weak for what it authorises: a checkout of some other repository could be
    # blessed and then judged canonical against ITS own origin/main, and a source could stay canonical while
    # its data pointer was moved to a different installation.
    # =====================================================================================================

    # An existing checkout of a DIFFERENT repository is refused rather than adopted.
    $wrongOriginFixture = New-Fixture
    $strangerOrigin = Join-Path $wrongOriginFixture.Root 'stranger.git'
    $null = Invoke-FixtureGit -GitArguments @('init', '--bare', $strangerOrigin)
    $null = Invoke-FixtureGit -GitArguments @('symbolic-ref', 'HEAD', 'refs/heads/main') -Repository $strangerOrigin
    $null = Invoke-FixtureGit -GitArguments @('clone', $strangerOrigin, $wrongOriginFixture.Production)
    Assert-Throws { Initialize-AeroLinkProductionSource -SourceRoot $wrongOriginFixture.Production -InstallationRoot $wrongOriginFixture.Installation -OriginUrl $wrongOriginFixture.Origin } `
        'Refusing to adopt' 'A checkout of another repository must not be adopted as the dedicated production source.'
    Assert-True (-not (Test-Path -LiteralPath (Get-AeroLinkProductionSourceMarkerPath -SourceRoot $wrongOriginFixture.Production) -PathType Leaf)) `
        'A refused adoption must not leave a dedicated marker behind.'

    # A marker that asserts nothing, or records no binding, is not a dedicated source.
    $markerFixture = New-Fixture
    $null = New-ProductionSource -Fixture $markerFixture
    $markerPath = Get-AeroLinkProductionSourceMarkerPath -SourceRoot $markerFixture.Production
    Assert-True ((Get-AeroLinkProductionSourcePosture -SourceRoot $markerFixture.Production).Dedicated) 'A properly created production source is dedicated.'

    '{ "dedicatedProductionSource": false }' | Set-Content -LiteralPath $markerPath -Encoding UTF8
    Assert-True (-not (Get-AeroLinkProductionSourcePosture -SourceRoot $markerFixture.Production).Dedicated) `
        'A marker that does not assert a dedicated production source must not be treated as one.'

    '{ "dedicatedProductionSource": true }' | Set-Content -LiteralPath $markerPath -Encoding UTF8
    Assert-True (-not (Get-AeroLinkProductionSourcePosture -SourceRoot $markerFixture.Production).Dedicated) `
        'A marker recording no repository or installation binding must not be treated as dedicated.'

    'not json at all' | Set-Content -LiteralPath $markerPath -Encoding UTF8
    Assert-True (-not (Get-AeroLinkProductionSourcePosture -SourceRoot $markerFixture.Production).Dedicated) `
        'A malformed marker must not be treated as dedicated.'
    Assert-Throws { Assert-AeroLinkDedicatedProductionSource -SourceRoot $markerFixture.Production } 'malformed' `
        'Production start must refuse a source whose marker cannot be read.'

    # The installation binding: canonical source, but repointed at a different installation.
    $movedFixture = New-Fixture
    $null = New-ProductionSource -Fixture $movedFixture
    Assert-True ((Get-AeroLinkProductionSourcePosture -SourceRoot $movedFixture.Production).Dedicated) 'Baseline: the fresh production source is dedicated.'
    $otherInstallation = Join-Path $movedFixture.Root 'other-installation'
    New-Item -ItemType Directory -Path $otherInstallation -Force | Out-Null
    Set-AeroLinkInstallationPointer -ProductRoot (Join-Path $movedFixture.Production 'product') -InstallationRoot $otherInstallation | Out-Null
    $movedPosture = Get-AeroLinkProductionSourcePosture -SourceRoot $movedFixture.Production
    Assert-True ($movedPosture.Canonical) 'Repointing the installation does not make the SOURCE uncanonical...'
    Assert-True (-not $movedPosture.Dedicated) '...but it must break the dedicated binding: source identity and data identity are one binding.'
    Assert-Throws { Assert-AeroLinkDedicatedProductionSource -SourceRoot $movedFixture.Production } 'installation' `
        'Production start must refuse a source whose data pointer no longer matches what it was bound to.'

    # =====================================================================================================
    # 10. Deciding and acting are separable, so a caller can stop its runtime in between.
    #
    # Fetching writes only remote-tracking refs, which no running process reads. The fast-forward rewrites
    # the working tree the production runtime executes out of. A caller with a runtime up must be able to
    # learn that an update is due WITHOUT the tree moving under that process.
    # =====================================================================================================
    $phaseFixture = New-Fixture
    $null = New-ProductionSource -Fixture $phaseFixture
    $beforeSha = (Get-AeroLinkProductionSourcePosture -SourceRoot $phaseFixture.Production).Posture.HeadSha
    $target = Push-RemoteCommit -Fixture $phaseFixture -Content 'phase-v2'

    $inspect = Update-AeroLinkProductionSource -SourceRoot $phaseFixture.Production -InspectOnly
    Assert-True ($inspect.Action -eq 'UpdateAvailable') "Inspection must report an available update; it reported $($inspect.Action): $($inspect.Reason)"
    Assert-True ($inspect.TargetSha -eq $target) 'Inspection must name the revision the advance will land on.'
    Assert-True ($inspect.HeadSha -eq $beforeSha) 'Inspection must report the source still at its current revision...'
    Assert-True ((Get-AeroLinkProductionSourcePosture -SourceRoot $phaseFixture.Production).Posture.HeadSha -eq $beforeSha) `
        '...and must actually leave the working tree there. This is the whole point of the phase split.'

    # The gap between the phases is closed: a push landing in between cannot silently redirect the advance.
    $moved = Push-RemoteCommit -Fixture $phaseFixture -Content 'phase-v3'
    Assert-True ($moved -ne $target) 'Fixture sanity: origin/main moved again between inspection and advance.'
    $stale = Update-AeroLinkProductionSource -SourceRoot $phaseFixture.Production -AdvanceToSha $target
    Assert-True ($stale.Action -eq 'Refused') 'An advance decided against a revision that is no longer origin/main must be refused.'
    Assert-True ($stale.Reason -match 'between inspection and advance') 'The refusal must say why, so the next pass is understood to re-decide.'
    Assert-True ((Get-AeroLinkProductionSourcePosture -SourceRoot $phaseFixture.Production).Posture.HeadSha -eq $beforeSha) `
        'A refused advance must not have moved the working tree.'

    # Re-decide, then advance: the ordinary two-phase pass completes.
    $inspect2 = Update-AeroLinkProductionSource -SourceRoot $phaseFixture.Production -InspectOnly
    $advance = Update-AeroLinkProductionSource -SourceRoot $phaseFixture.Production -AdvanceToSha $inspect2.TargetSha
    Assert-True ($advance.Action -eq 'Updated' -and $advance.HeadSha -eq $moved) 'A re-decided advance must land on the current origin/main.'
    Assert-DevelopmentUnchanged -Fixture $phaseFixture -Before (Get-DevelopmentSnapshot -Fixture $phaseFixture) -Scenario 'two-phase advance:'

    Assert-Throws { Update-AeroLinkProductionSource -SourceRoot $phaseFixture.Production -InspectOnly -AdvanceToSha $moved } 'contradiction' `
        'Asking to inspect and to advance in one call is a caller error, not a silent choice of one of them.'

    # =====================================================================================================
    # 11. HOME production runs from the dedicated source, not from whichever checkout the launcher lives in.
    #
    # The canonical-main gate refuses a dirty or feature-branch development checkout, so this is not about
    # unreviewed code. It is about which working tree the long-lived process executes out of: a development
    # checkout momentarily on clean main passes every gate and is then one `git checkout` away from having
    # its assemblies replaced under a running production API.
    # =====================================================================================================
    $launchFixture = New-Fixture
    $null = New-ProductionSource -Fixture $launchFixture
    $declared = { [pscustomobject]@{ SourceRoot = $launchFixture.Production; ConfigPath = 'C:\config\production-source.psd1'; RemoteName = 'origin' } }

    $fromProduction = Assert-AeroLinkRunningFromProductionSource -RepositoryRoot $launchFixture.Production -ConfigReader $declared
    Assert-True ($fromProduction.Checked) 'Starting production FROM the dedicated production source is exactly right.'

    # Sitting at the configured path is a claim, not a proof. The marker, the remote and the installation
    # pointer are what substantiate it, and each of those can break while the path stays correct.
    $brokenBinding = Get-AeroLinkProductionSourceMarkerPath -SourceRoot $launchFixture.Production
    'not json at all' | Set-Content -LiteralPath $brokenBinding -Encoding UTF8
    Assert-Throws { Assert-AeroLinkRunningFromProductionSource -RepositoryRoot $launchFixture.Production -ConfigReader $declared } `
        'malformed' 'A checkout at the configured path whose dedicated binding has broken must still be refused.'
    $null = New-ProductionSource -Fixture $launchFixture
    Assert-True (Assert-AeroLinkRunningFromProductionSource -RepositoryRoot $launchFixture.Production -ConfigReader $declared).Checked `
        'Repairing the binding makes the source usable again.'

    # Starting from the development checkout is a redirection, not a refusal. The stable root BAT is the
    # documented HOME production entry point and is referenced by shortcuts and tasks this repository cannot
    # enumerate, so it has to keep working - by delegating to the dedicated source, not by running here.
    $fromDevelopment = Assert-AeroLinkRunningFromProductionSource -RepositoryRoot $launchFixture.Development -ConfigReader $declared
    Assert-True (-not $fromDevelopment.Checked) 'The development checkout is not the dedicated production source.'
    Assert-True ($fromDevelopment.DelegateTo -eq $launchFixture.Production) 'It must name the dedicated source to delegate to, so the stable entry point keeps working.'
    Assert-True ($fromDevelopment.Reason -match [regex]::Escape($launchFixture.Production)) 'The reason must name the production source.'

    # A trailing separator is the same directory, not a different one.
    $trailing = Assert-AeroLinkRunningFromProductionSource -RepositoryRoot ($launchFixture.Production + '\') -ConfigReader $declared
    Assert-True ($trailing.Checked -and -not $trailing.DelegateTo) 'A trailing separator must not turn the production source into a stranger.'

    # No dedicated source configured is the ordinary state on a laptop and on any machine set up before #881.
    # Refusing there would remove the ability to run production rather than protect it. Absence is decided by
    # the file not existing, not by an exception.
    $missingConfig = Join-Path $launchFixture.Root 'no-such-production-source.psd1'
    $unconfigured = Assert-AeroLinkRunningFromProductionSource -RepositoryRoot $launchFixture.Development -ConfigPath $missingConfig
    Assert-True (-not $unconfigured.Checked -and -not $unconfigured.DelegateTo) 'With no dedicated source configured, production must start here rather than be redirected.'

    # A configuration that is PRESENT but unusable is the opposite case. Swallowing it would disable the
    # guard exactly when something is already wrong, and let production run from the development checkout.
    $malformedConfig = Join-Path $launchFixture.Root 'malformed-production-source.psd1'
    'this is not a data file' | Set-Content -LiteralPath $malformedConfig -Encoding UTF8
    Assert-Throws { Assert-AeroLinkRunningFromProductionSource -RepositoryRoot $launchFixture.Development -ConfigPath $malformedConfig } `
        'malformed' 'A malformed production-source configuration must fail closed, not read as "not configured".'

    $unknownKeyConfig = Join-Path $launchFixture.Root 'unknown-key-production-source.psd1'
    "@{ SourceRoot = 'C:\\AeroLink Production'; NgrokAuthToken = 'secret' }" | Set-Content -LiteralPath $unknownKeyConfig -Encoding UTF8
    Assert-Throws { Assert-AeroLinkRunningFromProductionSource -RepositoryRoot $launchFixture.Development -ConfigPath $unknownKeyConfig } `
        'unknown key' 'A configuration carrying an unexpected key must fail closed rather than be partially honoured.'

    $relativeConfig = Join-Path $launchFixture.Root 'relative-production-source.psd1'
    "@{ SourceRoot = 'AeroLink Production' }" | Set-Content -LiteralPath $relativeConfig -Encoding UTF8
    Assert-Throws { Assert-AeroLinkRunningFromProductionSource -RepositoryRoot $launchFixture.Development -ConfigPath $relativeConfig } `
        'absolute path' 'A configuration whose source root is not an absolute path must fail closed.'

    # Trivial spelling differences in a remote URL are not different repositories.
    Assert-True (Test-AeroLinkSameRemote -Left 'https://github.com/o/r.git' -Right 'https://github.com/o/r') 'A trailing .git is the same repository.'
    Assert-True (Test-AeroLinkSameRemote -Left 'https://github.com/o/r/' -Right 'https://github.com/o/r') 'A trailing slash is the same repository.'
    Assert-True (-not (Test-AeroLinkSameRemote -Left 'https://github.com/o/r' -Right 'https://github.com/o/other')) 'A different repository is a different repository.'
    Assert-True (-not (Test-AeroLinkSameRemote -Left '' -Right 'https://github.com/o/r')) 'An unknown remote is never a match.'
}
finally {
    foreach ($fixture in $fixtures) {
        if (Test-Path -LiteralPath $fixture) { Remove-Item -LiteralPath $fixture -Recurse -Force -ErrorAction SilentlyContinue }
    }
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Host "FAIL: $_" -ForegroundColor Red }
    Write-Host "AeroLink production-source contract FAILED ($($failures.Count) failure(s))." -ForegroundColor Red
    exit 1
}
Write-Host 'AeroLink production-source contract passed.' -ForegroundColor Green
exit 0
