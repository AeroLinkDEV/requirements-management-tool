#Requires -Version 5.1
# Executes the production controller with disposable dependency adapters. No API, tunnel, Git source or
# database is changed. Native process/lease and real Git generation behavior have separate contract suites.
$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) ('aerolink-924-production-' + [guid]::NewGuid().ToString('N'))
$scripts = Join-Path $root 'product\scripts'
New-Item -ItemType Directory -Path $scripts -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $root 'product\client') -Force | Out-Null
$source = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Start-AeroLinkProduction.ps1') -Raw
# Replace dependency loading only. All production branching, ordering, completion and catch/finally code is
# the actual source under test, including the function definitions within that script.
$source = [regex]::Replace($source, '(?m)^[ \t]*Import-Module[^\r\n]*\r?\n', '')
$source = [regex]::Replace($source, '(?m)^\. \(Join-Path \$PSScriptRoot[^\r\n]*\r?\n', '')
$controller = Join-Path $scripts 'Start-AeroLinkProduction.ps1'
Set-Content -LiteralPath $controller -Value $source -Encoding UTF8
$driver = Join-Path $root 'driver.ps1'
@'
param($Controller, $CasePath, $Events)
$ErrorActionPreference = 'Stop'
$case = Get-Content -LiteralPath $CasePath -Raw | ConvertFrom-Json
$script:started = $false
function Event($Name) {
    Add-Content -LiteralPath $Events -Value $Name
    if ($case.Failure -eq $Name) { throw "Injected $Name failure" }
}
function Get-AeroLinkInstallationPaths {
    $local = Join-Path (Split-Path $CasePath -Parent) 'local'
    [pscustomobject]@{ InstallationRoot=$local; Logs=(Join-Path $local 'logs'); BootstrapState=(Join-Path $local 'bootstrap') }
}
function Assert-AeroLinkRunningFromProductionSource { [pscustomobject]@{ DelegateTo=$null } }
function Enter-AeroLinkTransition { Event 'Lease'; [pscustomobject]@{ Owner=$true; Policy='Preserve' } }
function Exit-AeroLinkTransition { Event 'ReleaseLease' }
function Get-AeroLinkRemoteDemoConfigPath { $CasePath }
function Get-AeroLinkRemoteDemoConfig { [pscustomobject]@{ PublicUrl='https://example.invalid'; LocalApiBaseUri='http://127.0.0.1:5080' } }
function New-AeroLinkProductionObligation {
    param($SourceRoot)
    [pscustomobject]@{ SourceRoot=$SourceRoot; PriorTunnel=$case.Tunnel; PriorRuntime=$true; TeardownBegan=$false; Discharged=$false; PublicOrigin='https://example.invalid' }
}
function Save-AeroLinkProductionObligation {}
function Stop-AeroLinkProductionTransition {
    param($Obligation)
    if ($case.Tunnel) { Add-Content -LiteralPath $Events -Value 'TunnelStop'; $Obligation.TeardownBegan=$true; Event 'AfterTunnelStop' }
    Event 'ApiStop'; $Obligation.TeardownBegan=$true
}
function Get-AeroLinkBootstrapScriptArguments { @() }
function Invoke-AeroLinkSourceBootstrap {
    param($PreAdvanceAction)
    Event 'SourceCheck'
    if ($case.Advance) { & $PreAdvanceAction; Event 'SourceAdvance'; $script:started=$false }
    if ($case.Failure -eq 'Handoff') { return [pscustomobject]@{ Action='Reentered'; ExitCode=1 } }
    [pscustomobject]@{ Action='Current'; ExitCode=0 }
}
function Get-AeroLinkSourceFingerprint { [pscustomobject]@{ Sha=('a' * 40); Identity=('a' * 40) } }
function Get-AeroLinkInstanceConfig {
    [pscustomobject]@{ InstanceId='instance'; Label='TEST'; Classification='HomeCanonical'; SnapshotSourceLabel=$null; SnapshotSourceSha=$null; SnapshotCreatedAtUtc=$null; SnapshotActivatedAtUtc=$null }
}
function Resolve-AeroLinkRuntimeDisposition {
    Event 'RuntimeProof'
    $disposition = if ($script:started) { 'Reuse' } elseif ($case.Advance) { 'Free' } else { $case.Runtime }
    [pscustomobject]@{ Disposition=$disposition; ProcessId=1234; Detail=$disposition }
}
function Test-AeroLinkRemoteDemoNotificationOriginProof { [pscustomobject]@{ Valid=$case.Tunnel } }
function Resolve-AeroLinkDotnet { 'Invoke-FakeApiBuild' }
function Assert-AeroLinkNode { Event 'Prerequisites' }
function Assert-AeroLinkPostgres { Event 'Postgres' }
function Get-AeroLinkUpgradeAnalysis {
    Event 'Upgrade'
    if ($case.PendingUpgrade) { return [pscustomobject]@{ Status='upgrade-required'; Analysis=[pscustomobject]@{ pendingEfMigrations=@('fixture'); pendingSemanticUpgrades=@() } } }
    [pscustomobject]@{ Status='current' }
}
function Invoke-AeroLinkCloneValidatedUpgrade {
    foreach ($boundary in @('Backup','RestoreClone','UpgradeClone','ValidateClone','UpgradeReal','AfterUpgrade')) { Event $boundary }
    [pscustomobject]@{ Applied=$true; Detail='Disposable upgrade adapter completed' }
}
function Update-AeroLinkClientDependencies { Event 'Dependencies' }
function npm.cmd { Event 'ClientBuild'; $global:LASTEXITCODE=0 }
function Invoke-FakeApiBuild { Event 'ApiBuild'; $global:LASTEXITCODE=0 }
function Start-AeroLinkService {
    param($Environment)
    Event 'ApiStart'
    if ($case.Tunnel -and $Environment.Notifications__BaseUrl -ne 'https://example.invalid') { throw 'Wrong startup origin' }
    $script:started=$true
}
function Get-AeroLinkPortOwner { [pscustomobject]@{ Found=$true; Ambiguous=$false; ProcessId=1234; CommandLine='api'; ExecutablePath='api.exe'; StartedAt='2026-01-01T00:00:00Z' } }
function Test-AeroLinkProcessOwnership { $true }
function Grant-AeroLinkCreatedProcessAccess {}
function Set-AeroLinkRemoteDemoNotificationOriginProof { Event 'OriginProof' }
function Invoke-WebRequest { Event 'BuiltClientProof'; [pscustomobject]@{ Content='/assets/index-test.js' } }
function Start-AeroLinkRemoteDemo { Event 'TunnelRestore'; [pscustomobject]@{ Ready=$true } }
function Get-AeroLinkRemoteDemoNgrokProcess { [pscustomobject]@{ Owned=@(); Mismatched=@() } }
function Get-AeroLinkProductionSourcePosture { [pscustomobject]@{ Canonical=$true; Posture=[pscustomobject]@{ HeadSha=('a' * 40) } } }
function Invoke-AeroLinkBootstrapReentry { Event 'Compensate'; return 0 }
& $Controller -DoNotOpenBrowser
'@ | Set-Content -LiteralPath $driver -Encoding UTF8
$failures = [Collections.Generic.List[string]]::new()
try {
    $cases = @()
    foreach ($tunnel in @($false, $true)) {
        foreach ($runtime in @('Reuse', 'RestartStale', 'RestartModeMismatch', 'RestartUnready', 'RestartUnidentified', 'Free')) {
            $cases += @{ Tunnel=$tunnel; Runtime=$runtime; Advance=$false; Failure='' }
        }
        $cases += @{ Tunnel=$tunnel; Runtime='Reuse'; Advance=$true; Failure='' }
    }
    foreach ($failure in @('AfterTunnelStop', 'SourceAdvance', 'Handoff', 'Prerequisites', 'Postgres', 'Upgrade', 'Dependencies', 'ClientBuild', 'ApiBuild', 'ApiStart', 'RuntimeProof', 'BuiltClientProof', 'OriginProof', 'TunnelRestore')) {
        $cases += @{ Tunnel=$true; Runtime='RestartStale'; Advance=$true; Failure=$failure }
    }
    foreach ($failure in @('', 'Backup', 'RestoreClone', 'UpgradeClone', 'ValidateClone', 'UpgradeReal', 'AfterUpgrade', 'ApiBuild')) {
        $cases += @{ Tunnel=$true; Runtime='RestartStale'; Advance=$true; Failure=$failure; PendingUpgrade=$true }
    }
    $index = 0
    foreach ($case in $cases) {
        $index++
        $casePath = Join-Path $root "case-$index.json"
        $eventsPath = Join-Path $root "events-$index.txt"
        $log = Join-Path $root "output-$index.txt"
        $case | ConvertTo-Json | Set-Content -LiteralPath $casePath -Encoding UTF8
        $previousPreference = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $driver -Controller $controller -CasePath $casePath -Events $eventsPath *> $log
            $code = $LASTEXITCODE
        } finally { $ErrorActionPreference = $previousPreference }
        $events = @(Get-Content -LiteralPath $eventsPath)
        $description = "case $index ON=$($case.Tunnel) runtime=$($case.Runtime) advance=$($case.Advance) failure=$($case.Failure)"
        if (($code -eq 0) -ne (-not $case.Failure)) { $failures.Add("$description exit $code; $(Get-Content $log -Tail 4)") }
        if ($events[-1] -ne 'ReleaseLease') { $failures.Add("$description did not release its lease") }
        if (-not $case.Failure -and $case.Runtime -eq 'Reuse' -and -not $case.Advance) {
            if (@($events | Where-Object { $_ -in @('Postgres','ClientBuild','ApiBuild','ApiStart','ApiStop','TunnelStop','Upgrade') }).Count) { $failures.Add("$description disturbed exact reuse") }
        }
        if (-not $case.Tunnel -and $events -contains 'TunnelRestore') { $failures.Add("$description published an OFF tunnel") }
        if ($case.Tunnel -and $events -contains 'ApiStop' -and [array]::IndexOf($events,'TunnelStop') -gt [array]::IndexOf($events,'ApiStop')) { $failures.Add("$description stopped API before tunnel") }
        if ($case.Failure -and $events -contains 'TunnelStop' -and $events -notcontains 'Compensate') { $failures.Add("$description lost its compensation obligation") }
        if ($case.Tunnel -and $events -contains 'ApiStart' -and -not $case.Failure -and
            [array]::IndexOf($events,'OriginProof') -gt [array]::IndexOf($events,'TunnelRestore')) { $failures.Add("$description restored before new-process origin proof") }
    }
    if ($failures.Count) { $failures | ForEach-Object { Write-Host "FAIL: $_" }; throw "Production transition contracts failed. Evidence: $root" }
    Write-Host "Production transition controller contracts passed ($index disposable dependency scenarios)."
}
finally {
    if (-not $failures.Count) {
        $resolved = [IO.Path]::GetFullPath($root)
        if (-not $resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected fixture cleanup path.' }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
