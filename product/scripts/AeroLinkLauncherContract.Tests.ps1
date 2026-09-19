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

# ---------------------------------------------------------------------------------------------------------
# The launcher prerequisite boundary (#1055, S4 OFF). Measured: a launcher re-entered after its own source
# advanced reached "[0/4] Checking prerequisites..." with NO resolver in the session, so
# `Resolve-AeroLinkDotnet` was "not recognized" and an otherwise-successful runtime restoration was reported
# as a failed transition. Both launchers must re-establish the prerequisite helpers from their own directory
# immediately before the call, and that guard is executed here in a fresh process on THIS host, with a negative
# control proving the unguarded call is exactly the measured failure.
# ---------------------------------------------------------------------------------------------------------
$reestablishment = @(
    ". (Join-Path `$PSScriptRoot 'AeroLinkPrerequisites.ps1')",
    ". (Join-Path `$PSScriptRoot 'AeroLinkLaunch.ps1')"
)
foreach ($launcherName in @('Start-AeroLinkProduction.ps1', 'Start-AeroLink.ps1')) {
    $launcherText = Get-Content -LiteralPath (Join-Path $scriptsRoot $launcherName) -Raw
    # The LAST occurrence: the top-of-file dot-sources are the first, the post-import re-establishment is the one
    # the body depends on.
    $reestablishAt = $launcherText.LastIndexOf($reestablishment[0])
    $launchAt = $launcherText.LastIndexOf($reestablishment[1])
    $callAt = $launcherText.IndexOf('$dotnet = Resolve-AeroLinkDotnet')
    # ...and in particular after the LAST import that precedes the body's prerequisite step, which is the
    # boundary the measured failure crossed.
    $importsBeforeCall = if ($callAt -gt 0) { [regex]::Matches($launcherText.Substring(0, $callAt), 'Import-Module ') } else { @() }
    $lastImportAt = if ($importsBeforeCall.Count) { $importsBeforeCall[$importsBeforeCall.Count - 1].Index } else { -1 }
    Assert-True ($reestablishAt -ge 0 -and $launchAt -gt $reestablishAt) "$launcherName must re-establish AeroLinkPrerequisites and AeroLinkLaunch before its body."
    Assert-True ($reestablishAt -gt $lastImportAt) "$launcherName must re-establish the helpers AFTER the last Import-Module that precedes the prerequisite step, not only at the top."
    Assert-True ($callAt -gt $reestablishAt) "$launcherName must re-establish the helpers before resolving dotnet."
}
$boundaryProbe = Join-Path ([IO.Path]::GetTempPath()) ('al1055-prereq-probe-' + [guid]::NewGuid().ToString('N').Substring(0, 8) + '.ps1')
[IO.File]::WriteAllText($boundaryProbe, @'
param([string]$Scripts, [string]$Guard)
$ErrorActionPreference = 'Stop'
# The launcher's own structure: helpers dot-sourced at the top, then its Import-Module sequence.
. (Join-Path $Scripts 'AeroLinkPrerequisites.ps1')
. (Join-Path $Scripts 'AeroLinkLaunch.ps1')
Import-Module (Join-Path $Scripts 'AeroLinkBootstrap.psm1') -Force
Import-Module (Join-Path $Scripts 'AeroLinkInstallation.psm1') -Force
Import-Module (Join-Path $Scripts 'AeroLinkRuntimeIdentity.psm1') -Force
Import-Module (Join-Path $Scripts 'AeroLinkUpgrade.psm1') -Force
Import-Module (Join-Path $Scripts 'AeroLinkTransition.psm1')
Import-Module (Join-Path $Scripts 'AeroLinkRemoteDemo.psm1') -Force
Import-Module (Join-Path $Scripts 'AeroLinkProcessControl.psm1')
# The measured failure: the body reached its prerequisite step with the helpers absent from the session.
Remove-Item function:Resolve-AeroLinkDotnet, function:Assert-AeroLinkNode, function:Assert-AeroLinkPostgres -ErrorAction SilentlyContinue
$negative = $false
try { $null = Assert-AeroLinkPostgres -ProductRoot $Scripts } catch { $negative = $_.Exception.Message -like '*not recognized*' }
$guardOk = $false
try {
    Invoke-Expression $Guard
    $guardOk = [bool](Get-Command Resolve-AeroLinkDotnet -ErrorAction SilentlyContinue) -and
               [bool](Get-Command Assert-AeroLinkNode -ErrorAction SilentlyContinue) -and
               [bool](Get-Command Assert-AeroLinkPostgres -ErrorAction SilentlyContinue)
} catch { }
if (-not $negative) { 'NEGATIVE-CONTROL-MISSING' } elseif (-not $guardOk) { 'GUARD-FAILED' } else { 'OK' }
'@, (New-Object Text.UTF8Encoding($false)))
try {
    $guardForProbe = ($reestablishment -join "`r`n").Replace('$PSScriptRoot', ("'" + $scriptsRoot + "'"))
    foreach ($hostInfo in @(
        [pscustomobject]@{ name = 'Windows PowerShell'; exe = (Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe') },
        [pscustomobject]@{ name = 'PowerShell 7'; exe = 'pwsh.exe' }
    )) {
        $probeOut = & $hostInfo.exe -NoProfile -ExecutionPolicy Bypass -File $boundaryProbe -Scripts $scriptsRoot -Guard $guardForProbe 2>&1
        $verdict = (@($probeOut) | ForEach-Object { "$_" } | Where-Object { $_ -in @('OK', 'GUARD-FAILED', 'NEGATIVE-CONTROL-MISSING') } | Select-Object -Last 1)
        Assert-True ($verdict -eq 'OK') "The prerequisite guard did not restore the resolver under $($hostInfo.name) (verdict: $verdict)."
    }
}
finally { Remove-Item -LiteralPath $boundaryProbe -Force -ErrorAction SilentlyContinue }

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Host "FAIL: $_" -ForegroundColor Red }
    Write-Host "Root launcher contract FAILED ($($failures.Count) failure(s))." -ForegroundColor Red
    exit 1
}

Write-Host "Root launcher contract passed ($($targets.Count) launcher(s) checked)." -ForegroundColor Green
exit 0
