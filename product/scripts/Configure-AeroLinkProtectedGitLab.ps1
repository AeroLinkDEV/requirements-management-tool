#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$ProductRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    [Parameter(Mandatory)][string]$BaseUrl,
    [string]$SyntheticDemoProjectId = '',
    [string]$SyntheticDemoRemoteProjectId = '',
    [string]$ProgramId = '',
    [string]$ProjectId = '',
    [string]$ReleaseId = '',
    [string]$BaselineId = '',
    [string]$CampaignId = ''
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'AeroLinkInstallation.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'AeroLinkProtectedConfig.psm1') -Force

$productRoot = (Resolve-Path $ProductRoot).Path
$installation = Get-AeroLinkInstallationPaths -ProductRoot $productRoot
$receipt = Set-AeroLinkProtectedGitLabConfig `
    -InstallationRoot $installation.InstallationRoot `
    -BaseUrl $BaseUrl `
    -SyntheticDemoProjectId $SyntheticDemoProjectId `
    -SyntheticDemoRemoteProjectId $SyntheticDemoRemoteProjectId `
    -ProgramId $ProgramId -ProjectId $ProjectId -ReleaseId $ReleaseId -BaselineId $BaselineId `
    -CampaignId $CampaignId

Write-Host 'Protected GitLab configuration saved.' -ForegroundColor Green
Write-Host "  Installation: $($installation.InstallationRoot)"
Write-Host "  Protected file: $($receipt.Path)"
Write-Host "  Configuration fingerprint: $($receipt.Fingerprint)"
Write-Host "  Synthetic display metadata: $(if ($receipt.SyntheticDemoProjectId) { 'configured' } else { 'not configured' })"
Write-Host "  DEC-131 supplement scope: $(if ($receipt.ScopeConfigured) { 'configured' } else { 'not configured' })"
Write-Host '  Runtime token: encrypted with Windows DPAPI and withheld from this receipt.'
