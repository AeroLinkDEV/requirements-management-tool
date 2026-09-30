#Requires -Version 5.1
<#
    Stores, reports or removes the Esri World Imagery API key for the FMS Test Bench's out-the-window view (DEC-151).

      Set     (default) prompts for the key with hidden input and stores it encrypted (Windows DPAPI) with an ACL for
              this account, SYSTEM and Administrators only. The running API picks it up on its next imagery request.
      Status  says whether a key is stored, and its fingerprint and date. Never the key.
      Remove  deletes the stored key; the view falls back to USGS imagery and relief.

    The key is never taken as an argument, so it cannot reach a command line, a task definition or shell history.
#>
[CmdletBinding()]
param([ValidateSet('Set', 'Status', 'Remove')][string]$Action = 'Set')

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'AeroLinkProtectedConfig.psm1') -Force

switch ($Action) {
    'Set' {
        Write-Host 'Paste the Esri API key (ArcGIS Location Platform, Basemaps privilege) and press Enter. Input is hidden.'
        $receipt = Set-AeroLinkProtectedImageryKey
        Write-Host 'Esri imagery key saved.' -ForegroundColor Green
        Write-Host "  Protected file: $($receipt.Path)"
        Write-Host "  Fingerprint: $($receipt.Fingerprint)"
        Write-Host '  The key is encrypted with Windows DPAPI and withheld from this receipt. No restart is needed.'
    }
    'Status' {
        $descriptor = Get-AeroLinkProtectedImageryDescriptor
        if ($descriptor.Configured) { Write-Host "Esri imagery key stored ($($descriptor.Path)), fingerprint $($descriptor.Fingerprint), updated $($descriptor.UpdatedAtUtc)." }
        else { Write-Host "No Esri imagery key is stored ($($descriptor.Path)). Outside the USGS coverage the view draws relief." }
    }
    'Remove' {
        $result = Remove-AeroLinkProtectedImageryKey
        Write-Host $(if ($result.Removed) { "Esri imagery key removed ($($result.Path))." } else { "No Esri imagery key was stored ($($result.Path))." })
    }
}
