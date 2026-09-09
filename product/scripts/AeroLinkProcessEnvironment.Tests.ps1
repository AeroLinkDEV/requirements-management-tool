[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
if (-not (Get-Module -Name AeroLinkProcessEnvironment)) {
    Import-Module (Join-Path $PSScriptRoot 'AeroLinkProcessEnvironment.psm1')
}
$download = Join-Path $PSScriptRoot 'Test-AeroLinkRestoredDownloads.ps1'
$probeName = 'AEROLINK_981_ROUNDTRIP_PROBE'
$settingsNames = @(
    'ASPNETCORE_ENVIRONMENT',
    'ASPNETCORE_URLS',
    'ConnectionStrings__AeroLink',
    'Evidence__Root',
    'RestoreValidation__ReadOnly',
    'RestoreValidation__Token',
    'DemoData__Enabled',
    'Identity__SeedDemoAccounts',
    'Identity__CookieSecure'
)

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Remove-ProcessVariable {
    param([string]$Name)
    $path = "Env:\$Name"
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -ErrorAction Stop }
}

function Set-ProcessState {
    param([string]$Name, [ValidateSet('Absent', 'Empty', 'Populated')][string]$State)
    Remove-ProcessVariable $Name
    if ($State -eq 'Empty') { Set-Item -LiteralPath "Env:\$Name" -Value '' }
    if ($State -eq 'Populated') { Set-Item -LiteralPath "Env:\$Name" -Value 'original-value' }
}

function Assert-ProcessState {
    param(
        [string]$Name,
        [ValidateSet('Absent', 'Empty', 'Populated')][string]$State,
        [string]$Context
    )
    $value = [Environment]::GetEnvironmentVariable($Name, 'Process')
    if ($State -eq 'Absent') {
        Assert-True ($null -eq $value) "$Context left $Name present when it was originally absent."
        return
    }
    if ($State -eq 'Empty') {
        Assert-True ($null -ne $value -and $value -eq '') "$Context did not restore $Name as present-empty."
        return
    }
    Assert-True ($value -eq 'original-value') "$Context restored $Name as '$value' instead of its populated value."
}

function Invoke-RoundTrip {
    param(
        [string]$State,
        [bool]$Fail
    )
    Set-ProcessState $probeName $State
    $before = Get-AeroLinkProcessEnvironmentSnapshot -Name @($probeName)
    try {
        Set-Item -LiteralPath "Env:\$probeName" -Value 'temporary-value'
        if ($Fail) { throw 'Injected validation failure.' }
    }
    catch {
        if (-not $Fail -or $_.Exception.Message -ne 'Injected validation failure.') { throw }
    }
    finally {
        Restore-AeroLinkProcessEnvironmentSnapshot -Snapshot $before
    }
    Assert-ProcessState $probeName $State $(if ($Fail) { 'Failure restoration' } else { 'Success restoration' })
}

# This script is also invoked in-process by AeroLinkRestoreContract.Tests.ps1. Preserve the caller's
# actual environment, not the synthetic states this test intentionally installs.
$callerSnapshot = Get-AeroLinkProcessEnvironmentSnapshot -Name @($settingsNames + $probeName)
try {
    $emptyStateSupported = $false
    Set-ProcessState $probeName 'Empty'
    $emptyValue = [Environment]::GetEnvironmentVariable($probeName, 'Process')
    if ($null -ne $emptyValue -and $emptyValue -eq '') { $emptyStateSupported = $true }
    Remove-ProcessVariable $probeName

    foreach ($fail in @($false, $true)) {
        Invoke-RoundTrip 'Absent' $fail
        Invoke-RoundTrip 'Populated' $fail
        if ($emptyStateSupported) { Invoke-RoundTrip 'Empty' $fail }
    }

    # Exercise the real validator's finally path with an executable that exits immediately. The expected
    # readiness failure must still restore every process setting it temporarily overrides in this process.
    foreach ($name in $settingsNames) { Remove-ProcessVariable $name }
    Set-ProcessState 'ConnectionStrings__AeroLink' 'Populated'
    Set-ProcessState 'RestoreValidation__Token' 'Populated'
    if ($emptyStateSupported) { Set-ProcessState 'Evidence__Root' 'Empty' }
    else { Set-ProcessState 'Evidence__Root' 'Populated' }
    $original = Get-AeroLinkProcessEnvironmentSnapshot -Name $settingsNames
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try { $apiPort = ([Net.IPEndPoint]$listener.LocalEndpoint).Port } finally { $listener.Stop() }
    $root = Join-Path ([IO.Path]::GetTempPath()) ('aerolink-981-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $root -Force | Out-Null
    try {
        $expectedFailure = $false
        try {
            & $download -Database 'aerolink_981_validation' -EvidenceRoot $root -AttachmentInventory @() `
                -PostgresPort 59999 -ApiPort $apiPort -LogRoot $root `
                -ApiExecutable (Join-Path $env:WINDIR 'System32\where.exe')
        }
        catch {
            if ($_.Exception.Message -notlike '*did not become ready*') { throw }
            $expectedFailure = $true
        }
        Assert-True $expectedFailure 'The immediate-exit API executable did not produce the expected readiness failure.'
        foreach ($name in $settingsNames) {
            $before = $original[$name]
            $state = if (-not $before.Present) { 'Absent' } elseif ($before.Value -eq '') { 'Empty' } else { 'Populated' }
            Assert-ProcessState $name $state 'Validator failure restoration'
        }
    }
    finally {
        Restore-AeroLinkProcessEnvironmentSnapshot -Snapshot $original
        if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
    }

    [pscustomobject]@{
        Passed = $true
        Engine = $PSVersionTable.PSVersion.ToString()
        EmptyStateSupported = $emptyStateSupported
        ValidatorFailureRestoredAllSettings = $true
        CallerEnvironmentRestored = $true
    }
}
finally {
    Restore-AeroLinkProcessEnvironmentSnapshot -Snapshot $callerSnapshot
}
$global:LASTEXITCODE = 0
