#Requires -Version 5.1
# Real continuation CLI, topology restoration, module imports and OS leases. The nested production
# launcher is replaced only after its imports: no API, database, Git update or tunnel is started.
$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) ('aerolink-1030-import-' + [guid]::NewGuid().ToString('N'))
$scripts = Join-Path $root 'product\scripts'
New-Item -ItemType Directory -Path $scripts -Force | Out-Null
Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.psm1' | Copy-Item -Destination $scripts
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'AeroLinkRemoteDemo.ps1') -Destination $scripts
# Read the actual launcher's imports in order, including their Force flags. This deliberately retains
# the module reload that the controller-adapter suite strips, and runs it in the real restoration scope.
$launcher = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Start-AeroLinkProduction.ps1') -Raw
$imports = [regex]::Matches($launcher, '(?m)^[ \t]*Import-Module[^\r\n]*') | ForEach-Object { $_.Value }
$nested = @'
param([switch]$DoNotOpenBrowser)
$ErrorActionPreference = 'Stop'
'@ + "`r`n" + ($imports -join "`r`n") + "`r`n" + @'
$lease = Enter-AeroLinkTransition -InstallationRoot $env:AEROLINK_INSTALLATION_ROOT
try {
    Add-Content -LiteralPath $env:AEROLINK_1030_EVENTS -Value 'RuntimeOnly'
    if ($env:AEROLINK_1030_FAILURE -eq '1') { throw 'Injected runtime restoration failure' }
}
finally { Exit-AeroLinkTransition -Lease $lease }
'@
Set-Content -LiteralPath (Join-Path $scripts 'Start-AeroLinkProduction.ps1') -Value $nested -Encoding UTF8
$driver = Join-Path $root 'driver.ps1'
@'
param($Root, $Failure, $Handoff)
$ErrorActionPreference = 'Stop'
$env:LOCALAPPDATA = Join-Path $Root 'profile'
$env:AEROLINK_INSTALLATION_ROOT = Join-Path $Root ('installation-' + $Failure + '-' + $Handoff)
$env:AEROLINK_1030_EVENTS = Join-Path $Root ('events-' + $Failure + '-' + $Handoff)
$env:AEROLINK_1030_FAILURE = $Failure
$env:AEROLINK_TRANSITION_LEASE = $null
$env:AEROLINK_TRANSITION_JOURNAL = $null
New-Item -ItemType Directory -Path $env:AEROLINK_INSTALLATION_ROOT -Force | Out-Null
$configDir = Join-Path $env:LOCALAPPDATA 'AeroLink\RemoteDemo'
New-Item -ItemType Directory -Path $configDir -Force | Out-Null
$escapedRoot = $Root.Replace("'", "''")
"@{ NgrokExecutable='unused.exe'; PublicUrl='https://example.invalid'; TrafficPolicyPath='unused.yml'; AeroLinkRoot='$escapedRoot' }" |
    Set-Content -LiteralPath (Join-Path $configDir 'remote-demo.config.psd1') -Encoding UTF8
$directory = Join-Path $env:AEROLINK_INSTALLATION_ROOT 'bootstrap'
if ($Handoff -eq '1') {
    Import-Module (Join-Path $Root 'product\scripts\AeroLinkRemoteDemo.psm1')
    Import-Module (Join-Path $Root 'product\scripts\AeroLinkTransition.psm1')
    $owner = Enter-AeroLinkTransition -InstallationRoot $env:AEROLINK_INSTALLATION_ROOT
    try {
        $result = 0
        try {
            Invoke-AeroLinkRemoteDemoHandoff -Config (Get-AeroLinkRemoteDemoConfig) -PreserveServiceState `
                -Topology ([pscustomobject]@{ TunnelRunning=$false; RuntimeRunning=$true }) -HeadSha ('a' * 40)
        } catch {
            if ($Failure -ne '1' -or $_.Exception.Message -notmatch 'exit code 1') { throw }
            $result = 1
        }
        if (@(Get-ChildItem -LiteralPath $directory -Filter '*.active').Count) { throw 'Fresh-process continuation left a witness behind' }
        $blocked = $false
        try { $probe = [IO.File]::Open((Join-Path $directory 'home-transition.lock'), [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None); $probe.Dispose() }
        catch [IO.IOException] { $blocked = $true }
        if (-not $blocked) { throw 'Child released its coordinator lease' }
    } finally { Exit-AeroLinkTransition -Lease $owner }
} else {
    $env:AEROLINK_TRANSITION_CONTINUATION = @{ sourceRoot=$Root; keepReady=$false; priorTunnel=$false; priorRuntime=$true } | ConvertTo-Json -Compress
    & (Join-Path $Root 'product\scripts\AeroLinkRemoteDemo.ps1') -Action Continue
    $result = $LASTEXITCODE
}
# Prove cleanup BEFORE process exit could implicitly release abandoned OS handles.
if ($env:AEROLINK_TRANSITION_LEASE -or $env:AEROLINK_TRANSITION_JOURNAL) { throw 'Continuation left lease environment behind' }
if (@(Get-ChildItem -LiteralPath $directory -Filter '*.active').Count) { throw 'Continuation left an owned witness behind' }
$probe = [IO.File]::Open((Join-Path $directory 'home-transition.lock'), [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
$probe.Dispose()
if (@(Get-Content -LiteralPath $env:AEROLINK_1030_EVENTS).Count -ne 1) { throw 'Expected one runtime-only restoration' }
$expected = if ($Failure -eq '1') { 1 } else { 0 }
if ($result -ne $expected) { throw "Continuation exit $result; expected $expected" }
Write-Host 'LEASE_RELEASED_BEFORE_EXIT'
'@ | Set-Content -LiteralPath $driver -Encoding UTF8
$passed = $false
try {
    foreach ($failure in @('0', '1')) {
      foreach ($handoff in @('0', '1')) {
        $log = Join-Path $root ('output-' + $failure + '-' + $handoff + '.txt')
        $priorPreference = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $driver -Root $root -Failure $failure -Handoff $handoff *> $log
            $code = $LASTEXITCODE
        } finally { $ErrorActionPreference = $priorPreference }
        $output = Get-Content -LiteralPath $log -Raw
        if ($code -ne 0 -or $output -notmatch 'LEASE_RELEASED_BEFORE_EXIT') { throw "Nested import scenario $failure failed. Evidence: $log`n$output" }
        if ($failure -eq '0' -and $output -notmatch 'AEROLINK TRANSITION CONTINUED') { throw 'Successful continuation was not reported' }
        if ($failure -eq '1' -and ($output -notmatch 'AEROLINK TRANSITION CONTINUATION FAILED' -or $output -notmatch 'Injected runtime restoration failure')) { throw 'Restoration failure was not reported truthfully' }
      }
    }
    $passed = $true
    Write-Host 'Transition nested import contracts passed (4 direct/fresh-process success/failure scenarios; real CLI and lease cleanup).'
}
finally {
    if ($passed) {
        $resolved = [IO.Path]::GetFullPath($root)
        if (-not $resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected fixture cleanup path' }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
