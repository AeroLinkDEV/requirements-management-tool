[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$productRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Import-Module (Join-Path $PSScriptRoot 'AeroLinkRuntimeIdentity.psm1')
Import-Module (Join-Path $PSScriptRoot 'AeroLinkInstallation.psm1')
Import-Module (Join-Path $PSScriptRoot 'AeroLinkTransition.psm1')
$installation = Get-AeroLinkInstallationPaths -ProductRoot $productRoot
$lease = Enter-AeroLinkTransition -InstallationRoot $installation.InstallationRoot
try {
    foreach ($service in @(
        @{ Port = 5173; Directory = (Join-Path $productRoot 'client') },
        @{ Port = 5080; Directory = (Join-Path $productRoot 'src\AeroLink.Api') }
    )) {
        $result = Stop-AeroLinkOwnedListener -Port $service.Port -OwnershipFragments @($service.Directory)
        Write-Host $result.Detail
    }
    & (Join-Path $PSScriptRoot 'Stop-Postgres.ps1')
}
finally { Exit-AeroLinkTransition -Lease $lease }
