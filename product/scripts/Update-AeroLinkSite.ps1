#Requires -Version 5.1
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][string]$InstallationRoot,
    [Parameter(Mandatory)][string]$Package,
    [Parameter(Mandatory)][string]$TrustedCertificate,
    [string]$ProtectedConfigRoot,
    [switch]$AllowUnverifiedDevelopment
)

$ErrorActionPreference = 'Stop'
$productRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Import-Module (Join-Path $PSScriptRoot 'AeroLinkSiteUpdater.psm1') -Force
Invoke-AeroLinkSiteUpdate -ProductRoot $productRoot -InstallationRoot $InstallationRoot -Package $Package `
    -TrustedCertificate $TrustedCertificate -ProtectedConfigRoot $ProtectedConfigRoot `
    -AllowUnverifiedDevelopment:$AllowUnverifiedDevelopment -WhatIf:$WhatIfPreference
