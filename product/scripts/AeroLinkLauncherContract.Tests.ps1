#Requires -Version 5.1
<#
    Contract coverage for the double-clickable launchers in the repository root.

    These files are the only part of AeroLink some people ever run directly, and they are the part with
    no compiler and no test framework behind them. Two defects shipped in them on the same day:

      * A PowerShell 7 parent left the 7.x module directories in front of PSModulePath, Windows
        PowerShell bound Microsoft.PowerShell.Utility out of the wrong tree, and the remote-demo
        launchers stopped with an error naming a configuration file that was perfectly valid.
      * Three launchers ended in `pause` with nothing carrying the exit code, so the pause succeeded and
        the launcher reported success. A backup verification that found no archive at all exited zero.

    Neither is visible to any suite that compiles or executes product code, which is why both reached
    main. This reads the launchers as text and asserts the properties that were violated.

    Nothing here starts a service, touches PostgreSQL, writes evidence, or runs a launcher.
#>
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$failures = [System.Collections.Generic.List[string]]::new()

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { $script:failures.Add($Message) }
}

# The launchers, plus the shared body the three START_* files delegate to. launch.cmd names no
# powershell.exe of its own from the callers' point of view, and looking only for the obvious call is how
# it gets missed.
$launchers = @(Get-ChildItem -LiteralPath $root -Filter '*.bat' -File | Sort-Object Name)
Assert-True ($launchers.Count -gt 0) 'No root launchers were found. This contract would pass by testing nothing.'
$shared = Join-Path $root 'product\scripts\launch.cmd'
Assert-True (Test-Path -LiteralPath $shared -PathType Leaf) 'product\scripts\launch.cmd is missing; the START_* launchers delegate to it.'

$targets = @()
$targets += $launchers | ForEach-Object { [pscustomobject]@{ Name = $_.Name; Path = $_.FullName } }
if (Test-Path -LiteralPath $shared -PathType Leaf) {
    $targets += [pscustomobject]@{ Name = 'product\scripts\launch.cmd'; Path = $shared }
}

foreach ($target in $targets) {
    $text = [System.IO.File]::ReadAllText($target.Path)
    $name = $target.Name

    # A .bat is a Windows artifact and a bare LF in one is a corruption waiting to change behaviour.
    Assert-True ($text -match "`r`n") "$name must use CRLF line endings."
    Assert-True (-not ($text -match "(?<!`r)`n")) "$name contains a bare LF line ending."

    $invokesPowerShell = $text -match 'powershell\.exe'
    if (-not $invokesPowerShell) { continue }

    # PSModulePath must be cleared before Windows PowerShell is started, or a PowerShell 7 parent decides
    # which modules it binds. Position matters: clearing it after the call would be decorative.
    $clearIndex = $text.IndexOf('set "PSModulePath="')
    $callIndex = $text.IndexOf('powershell.exe')
    Assert-True ($clearIndex -ge 0) "$name invokes powershell.exe without clearing PSModulePath first."
    if ($clearIndex -ge 0) {
        Assert-True ($clearIndex -lt $callIndex) "$name clears PSModulePath after invoking powershell.exe, which is too late."
    }

    # Something has to carry the result out. Without this a failing launcher returns whatever the last
    # thing it did returned, and `pause` always succeeds.
    Assert-True ($text -match '(?m)^\s*exit /b') "$name never runs `exit /b`, so a failure cannot reach whatever called it."
    Assert-True ($text -match '%ERRORLEVEL%|errorlevel|%RESULT%') "$name does not read an exit code, so it cannot report one."
}

# Every launcher must name a script that exists. A rename that misses one of these fails at the moment
# somebody double-clicks it, which is the worst possible time to discover it.
foreach ($target in $targets) {
    $text = [System.IO.File]::ReadAllText($target.Path)
    foreach ($match in [regex]::Matches($text, '-File\s+"([^"]+)"')) {
        $referenced = $match.Groups[1].Value
        if ($referenced -match '%AEROLINK_SCRIPT%') { continue }  # resolved from the caller, checked below
        $resolved = $referenced -replace '%~dp0', "$root\"
        Assert-True (Test-Path -LiteralPath $resolved -PathType Leaf) "$($target.Name) names a script that does not exist: $referenced"
    }
}

# The indirect callers: each sets AEROLINK_SCRIPT and lets launch.cmd run it.
foreach ($launcher in $launchers) {
    $text = [System.IO.File]::ReadAllText($launcher.FullName)
    $match = [regex]::Match($text, 'set\s+"AEROLINK_SCRIPT=([^"]+)"')
    if (-not $match.Success) { continue }
    $script = Join-Path $root (Join-Path 'product\scripts' $match.Groups[1].Value)
    Assert-True (Test-Path -LiteralPath $script -PathType Leaf) "$($launcher.Name) sets AEROLINK_SCRIPT to a script that does not exist: $($match.Groups[1].Value)"
    Assert-True ($text -match 'call\s+"%~dp0product\\scripts\\launch\.cmd"') "$($launcher.Name) sets AEROLINK_SCRIPT but never calls launch.cmd."
}

# The control-plane generation rule: every implementation file loaded BEFORE a source advance is either part
# of the re-entry fingerprint, or an update to it leaves the old version driving the rest of the launch. Two
# modules were missing - the production-source module that gates delegation and canonicality, and the
# remote-demo module the pre-advance hook imports - so an update touching either did not force re-entry.
$productionLauncher = [System.IO.File]::ReadAllText((Join-Path $root 'product\scripts\Start-AeroLinkProduction.ps1'))
$launcherFilesBlock = if ($productionLauncher -match '(?s)-LauncherFiles\s*@\((.*?)\n\s*\)') { $Matches[1] } else { '' }
Assert-True ([bool]$launcherFilesBlock) 'The production launcher must declare the launcher files its re-entry fingerprint covers.'
foreach ($module in @(
        'Start-AeroLinkProduction.ps1', 'AeroLinkPrerequisites.ps1', 'AeroLinkLaunch.ps1',
        'AeroLinkNativeRunner.psm1', 'AeroLinkBootstrap.psm1', 'AeroLinkInstallation.psm1',
        'AeroLinkRuntimeIdentity.psm1', 'AeroLinkUpgrade.psm1',
        'AeroLinkProductionSource.psm1', 'AeroLinkRemoteDemo.psm1')) {
    Assert-True ($launcherFilesBlock -match [regex]::Escape($module)) `
        "The re-entry fingerprint omits $module, which is loaded before the source advance - an update to it would leave the old version driving the launch."
}

# Nested dependency imports must not remove commands the launcher already imported into
# its own scope. Windows PowerShell 5.1 does exactly that when a module uses -Force while
# importing a dependency that the caller already loaded. The merged #881 launcher failed
# on HOME before startup because importing AeroLinkUpgrade removed
# Get-AeroLinkInstallationPaths; importing AeroLinkProductionSource would likewise remove
# the bootstrap commands needed later in the same launcher.
$scriptsRoot = Join-Path $root 'product\scripts'
Import-Module (Join-Path $scriptsRoot 'AeroLinkBootstrap.psm1') -Force
Import-Module (Join-Path $scriptsRoot 'AeroLinkInstallation.psm1') -Force
Import-Module (Join-Path $scriptsRoot 'AeroLinkRuntimeIdentity.psm1') -Force
Import-Module (Join-Path $scriptsRoot 'AeroLinkUpgrade.psm1') -Force
Assert-True ([bool](Get-Command Get-AeroLinkInstallationPaths -ErrorAction SilentlyContinue)) `
    'Importing AeroLinkUpgrade must not remove caller-visible AeroLinkInstallation commands.'
Import-Module (Join-Path $scriptsRoot 'AeroLinkProductionSource.psm1') -Force
Assert-True ([bool](Get-Command Get-AeroLinkInstallationPaths -ErrorAction SilentlyContinue)) `
    'Importing AeroLinkProductionSource must not remove caller-visible AeroLinkInstallation commands.'
Assert-True ([bool](Get-Command Get-AeroLinkBootstrapScriptArguments -ErrorAction SilentlyContinue)) `
    'Importing AeroLinkProductionSource must not remove caller-visible AeroLinkBootstrap commands.'

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Host "FAIL: $_" -ForegroundColor Red }
    Write-Host "Root launcher contract FAILED ($($failures.Count) failure(s))." -ForegroundColor Red
    exit 1
}

Write-Host "Root launcher contract passed ($($targets.Count) launcher(s) checked)." -ForegroundColor Green
exit 0
