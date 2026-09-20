#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ManifestPath,
    [Parameter(Mandatory)][string]$JournalPath,
    [Parameter(Mandatory)][string]$ApiBaseUrl,
    [switch]$Apply,
    [switch]$ResumePending,
    [int]$ScanLimit = 10000,
    [PSCredential]$Credential
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'AeroLinkResumableDemo.psm1') -Force

# Preview performs a live read-only preflight, so it uses the same secure in-memory session as Apply.
# The PSCredential never reaches the manifest, journal, or console output.
$client = $null
try {
    if ($null -eq $Credential) { $Credential = Get-Credential -Message 'AeroLink demo API credential (kept in memory; never recorded)' }
    $client = New-AeroLinkResumableClient -ApiBaseUrl $ApiBaseUrl -Credential $Credential -Login
    $result = Invoke-AeroLinkResumableDemo -ManifestPath $ManifestPath -JournalPath $JournalPath -ApiBaseUrl $ApiBaseUrl -Apply:$Apply -ResumePending:$ResumePending -ScanLimit $ScanLimit -ApiClient $client
    $result | ConvertTo-Json -Depth 20
}
finally {
    if ($null -ne $client) { Close-AeroLinkResumableClient $client }
}
