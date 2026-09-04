#Requires -Version 5.1
<#
    Declares what this AeroLink installation IS, so nobody has to guess from a window title.

    #881 G says an operator must never mistake WORK-LAPTOP LOCAL for HOME CANONICAL. The enum existed and
    the badge rendered it, but nothing in the ordinary laptop path ever established it: a laptop stayed
    LOCAL DEVELOPMENT / Undeclared indefinitely, which is exactly the state in which the snapshot-import
    guard has nothing to read. Two things now establish a classification - configuring the HOME production
    source declares HOME CANONICAL, and accepting a HOME snapshot declares WORK-LAPTOP LOCAL - and this is
    the explicit action for every other case, including a laptop that never imports a snapshot.

    Never inferred from the hostname. A machine name is not a fact about a database, and a guess here is the
    kind that gets a change request typed into the wrong installation.

    Status and Preview change nothing at all, including on disk.
#>
[CmdletBinding()]
param(
    [ValidateSet('Status', 'Preview', 'Declare')]
    [string]$Action = 'Status',

    # What this installation is. The label is what an operator reads on the badge; the classification is what
    # the guards read.
    [ValidateSet('WorkLaptopLocal', 'HomeCanonical', 'LocalDemo')]
    [string]$Classification,

    # Optional display label. Defaults to the conventional one for the classification.
    [string]$Label,

    # Reclassifying an installation that already declares something is a deliberate act, not a correction
    # somebody stumbles into: it moves which destructive guards apply to real data.
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'AeroLinkInstallation.psm1') -Force

$productRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$installation = Get-AeroLinkInstallationPaths -ProductRoot $productRoot
$current = Get-AeroLinkInstanceConfig -ProductRoot $productRoot -Mode Development

$defaultLabels = @{
    WorkLaptopLocal = 'WORK-LAPTOP LOCAL'
    HomeCanonical   = 'HOME CANONICAL'
    LocalDemo       = 'LOCAL DEMO'
}

Write-Host 'AeroLink instance declaration' -ForegroundColor Cyan
Write-Host ''
Write-Host "Installation:   $($installation.InstallationRoot)"
Write-Host "Declaration:    $($current.ConfigPath)"
Write-Host "Currently:      $($current.Label) ($($current.Classification))"
if ($current.InstanceId) { Write-Host "Instance id:    $($current.InstanceId)" }

if ($Action -eq 'Status') {
    Write-Host ''
    if ($current.Classification -eq 'Undeclared') {
        Write-Host 'This installation has not declared what it is.' -ForegroundColor Yellow
        Write-Host 'Declare it so the badge is honest and the destructive-import guards have something to read:' -ForegroundColor Yellow
        Write-Host '      DECLARE_AEROLINK_INSTANCE.bat Declare WorkLaptopLocal' -ForegroundColor Gray
    }
    Write-Host 'Nothing was changed.' -ForegroundColor Green
    exit 0
}

if (-not $Classification) {
    throw 'Declaring an instance requires -Classification: WorkLaptopLocal, HomeCanonical, or LocalDemo. Nothing was changed.'
}
$resolvedLabel = if ($Label) { $Label } else { $defaultLabels[$Classification] }

Write-Host ''
Write-Host "Would declare:  $resolvedLabel ($Classification)"
Write-Host ''
Write-Host 'What this changes:'
switch ($Classification) {
    'HomeCanonical' {
        Write-Host '  * The HOME-to-laptop snapshot import will REFUSE to replace this database.'
        Write-Host '  * The development launcher will REFUSE to run against this installation.'
    }
    'WorkLaptopLocal' {
        Write-Host '  * This installation may accept a HOME snapshot over its database.'
        Write-Host '  * A backup taken here is recorded as WORK-LAPTOP LOCAL, so it cannot pose as a HOME snapshot.'
    }
    'LocalDemo' {
        Write-Host '  * This installation is neither canonical nor a laptop mirror; it makes no provenance claim.'
    }
}
Write-Host '  * No database, evidence, attachment or backup is touched either way.'

if ($Action -eq 'Preview') {
    Write-Host ''
    Write-Host 'Preview only. Nothing was changed.' -ForegroundColor Green
    Write-Host 'To apply it:' -ForegroundColor DarkGray
    Write-Host "      DECLARE_AEROLINK_INSTANCE.bat Declare $Classification" -ForegroundColor Gray
    exit 0
}

if ($current.Classification -ne 'Undeclared' -and $current.Classification -ne $Classification -and -not $Force) {
    throw "This installation is already declared $($current.Label) ($($current.Classification)). Reclassifying it changes which destructive guards apply to real data, so it needs -Force. Nothing was changed."
}

$result = Set-AeroLinkInstanceConfig -ProductRoot $productRoot -Label $resolvedLabel -Classification $Classification
$result = Get-AeroLinkInstanceConfig -ProductRoot $productRoot -Mode Development -EnsureInstanceId
Write-Host ''
Write-Host "Instance declared: $($result.Label) ($($result.Classification))." -ForegroundColor Green
Write-Host "Instance id:       $($result.InstanceId)" -ForegroundColor DarkGray
Write-Host 'No persistent data was changed.' -ForegroundColor Green
exit 0
