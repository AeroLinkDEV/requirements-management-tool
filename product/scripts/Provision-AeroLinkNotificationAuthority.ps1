[CmdletBinding()]
param(
    [string]$ProductRoot = (Split-Path $PSScriptRoot -Parent),
    [Parameter(Mandatory)][string]$PolicyFile,
    [ValidateSet('Disabled', 'ControlledTest', 'Live')][string]$MaximumMode = 'Disabled',
    [guid]$ExpectedSendGeneration = [guid]::Empty
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'AeroLinkInstallation.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'AeroLinkNotificationAuthority.psm1') -Force
$instance = Get-AeroLinkInstanceConfig -ProductRoot $ProductRoot -Mode Development
if ([string]::IsNullOrWhiteSpace([string]$instance.InstanceId)) { throw 'An existing installation identity is required.' }
$inputPath = [IO.Path]::GetFullPath($PolicyFile)
$inputFile = Get-Item -LiteralPath $inputPath
if ($inputFile.Length -gt 32768 -or ($inputFile.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Private policy input is oversized or indirect.' }
$requested = [IO.File]::ReadAllText($inputPath) | ConvertFrom-Json
if ($requested -isnot [pscustomobject]) { throw 'Private policy input must be a JSON object.' }
$allowed = @('relayHosts', 'senders', 'recipientDomains', 'recipientAddresses', 'diagnosticTarget', 'baseUrl', 'allowManagedCredentials', 'trustAnchorsPem')
foreach ($property in $requested.PSObject.Properties.Name) {
    if ($property -notin $allowed) { throw 'Private policy input contains an unsupported field. Credentials belong in protected settings.' }
}
foreach ($arrayName in @('relayHosts', 'senders', 'recipientDomains', 'recipientAddresses', 'trustAnchorsPem')) {
    if ($requested.PSObject.Properties.Name -contains $arrayName) {
        if ($requested.$arrayName -isnot [array]) { throw 'Policy lists must be JSON arrays of strings.' }
        foreach ($member in $requested.$arrayName) {
            if ($member -isnot [string]) { throw 'Policy lists must contain only JSON strings.' }
        }
    }
}
foreach ($stringName in @('diagnosticTarget', 'baseUrl')) {
    if (($requested.PSObject.Properties.Name -contains $stringName) -and $requested.$stringName -isnot [string]) {
        throw 'Policy origin and diagnostic target must be JSON strings.'
    }
}
function PrivateValue([string]$name, $fallback) {
    if ($requested.PSObject.Properties.Name -contains $name) { return $requested.$name }
    return $fallback
}
$relayHosts = @(PrivateValue 'relayHosts' @())
$senders = @(PrivateValue 'senders' @())
$recipientDomains = @(PrivateValue 'recipientDomains' @())
$recipientAddresses = @(PrivateValue 'recipientAddresses' @())
$diagnosticTarget = [string](PrivateValue 'diagnosticTarget' '')
$baseUrl = [string](PrivateValue 'baseUrl' '')
$trustAnchors = @(PrivateValue 'trustAnchorsPem' @())
$managedCredentials = PrivateValue 'allowManagedCredentials' $false
if ($managedCredentials -isnot [bool]) { throw 'allowManagedCredentials must be a JSON boolean.' }
foreach ($hostName in @($relayHosts) + @($recipientDomains)) {
    if ([string]::IsNullOrWhiteSpace([string]$hostName) -or [uri]::CheckHostName([string]$hostName) -eq [UriHostNameType]::Unknown) { throw 'Policy hosts and recipient domains must be explicit host names or addresses.' }
}
foreach ($mailbox in @($senders) + @($recipientAddresses) + @(if ($diagnosticTarget) { $diagnosticTarget })) {
    $parsedMailbox = [Net.Mail.MailAddress]::new([string]$mailbox)
    if ($parsedMailbox.Address -ne [string]$mailbox) { throw 'Policy mailboxes must be exact addresses without display names.' }
}
if ($MaximumMode -ne 'Disabled') {
    $origin = $null
    if ($relayHosts.Count -eq 0 -or $senders.Count -eq 0 -or -not $diagnosticTarget -or
        -not [uri]::TryCreate($baseUrl, [UriKind]::Absolute, [ref]$origin) -or $origin.Scheme -ne 'https' -or
        $origin.AbsolutePath -ne '/' -or $origin.UserInfo -or $origin.Query -or $origin.Fragment -or
        ($MaximumMode -eq 'Live' -and $origin.IsLoopback)) { throw 'External policy requires approved relay, sender, diagnostic address and a clean HTTPS origin.' }
}
foreach ($pem in $trustAnchors) {
    $encoded = ([string]$pem).Replace('-----BEGIN CERTIFICATE-----', '').Replace('-----END CERTIFICATE-----', '') -replace '\s', ''
    $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new([Convert]::FromBase64String($encoded))
    try {
        $constraints = @($certificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.19' })
        if ($constraints.Count -ne 1 -or -not $constraints[0].CertificateAuthority) { throw 'Approved custom trust anchors must be certificate authorities.' }
    } finally { $certificate.Dispose() }
}
$path = Get-AeroLinkNotificationAuthorityPath -InstanceId $instance.InstanceId
$witness = $path + '.generation'
if (Test-Path -LiteralPath $witness) {
    if ((Get-Item -LiteralPath $witness).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Generation witness cannot be indirect.' }
    $current = [guid]::Empty
    if (-not [guid]::TryParse([IO.File]::ReadAllText($witness).Trim(), [ref]$current) -or $current -ne $ExpectedSendGeneration) { throw 'Current send generation differs from the explicitly expected generation.' }
} elseif ($ExpectedSendGeneration -ne [guid]::Empty) { throw 'Expected generation is unavailable; inspect the independent authority before provisioning.' }

# Always create a new generation. An archived input can never select or revive its old generation.
# Witness-first durable revocation also leaves interrupted provisioning safely Disabled.
$revocation = Revoke-AeroLinkNotificationSendGeneration -ProductRoot $ProductRoot
$policy = [IO.File]::ReadAllText($path) | ConvertFrom-Json
$policy.maximumMode = switch ($MaximumMode) { 'ControlledTest' { 2 } 'Live' { 3 } default { 0 } }
$policy.relayHosts = $relayHosts; $policy.senders = $senders
$policy.recipientDomains = $recipientDomains; $policy.recipientAddresses = $recipientAddresses
$policy.diagnosticTarget = $diagnosticTarget; $policy.baseUrl = $baseUrl
$policy.allowManagedCredentials = $managedCredentials
$policy.trustAnchorsPem = $trustAnchors
$root = Split-Path $path -Parent
$temporary = Join-Path $root ('.provision-' + [guid]::NewGuid().ToString('N') + '.tmp')
$backup = Join-Path $root ('.provision-backup-' + [guid]::NewGuid().ToString('N') + '.tmp')
try {
    $bytes = [Text.Encoding]::UTF8.GetBytes(($policy | ConvertTo-Json -Depth 8 -Compress))
    if ($bytes.Length -gt 32768) { throw 'Resolved private policy exceeds the bounded authority size.' }
    $stream = [IO.FileStream]::new($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None, 4096, [IO.FileOptions]::WriteThrough)
    try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
    $security = Get-Acl -LiteralPath $path
    Set-AeroLinkNotificationAccessRules -Path $temporary -Security $security
    [IO.File]::Replace($temporary, $path, $backup)
    Set-AeroLinkNotificationAccessRules -Path $path -Security $security
    $readback = [IO.File]::ReadAllText($path) | ConvertFrom-Json
    if ([string]$readback.sendGeneration -ne [string]$revocation.SendGeneration -or
        [IO.File]::ReadAllText($witness).Trim() -ne [string]$readback.sendGeneration) { throw 'Provisioned generation readback failed.' }
    [pscustomobject]@{ State = 'Provisioned'; MaximumMode = $MaximumMode; SendGeneration = [string]$readback.sendGeneration; Admission = 'ExplicitActivationRequired' }
} finally {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
    if (Test-Path -LiteralPath $backup) { Remove-Item -LiteralPath $backup -Force }
}
