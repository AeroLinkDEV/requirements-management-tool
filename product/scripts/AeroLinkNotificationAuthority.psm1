Set-StrictMode -Version Latest

function Get-AeroLinkNotificationAuthorityPath {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$InstanceId)
    $parsed = [guid]::Empty
    if (-not [guid]::TryParse($InstanceId, [ref]$parsed)) { throw 'Notification authority requires an existing installation instance identity.' }
    $authorityRoot = $env:AEROLINK_NOTIFICATION_AUTHORITY_ROOT
    if ([string]::IsNullOrWhiteSpace($authorityRoot)) { $authorityRoot = Join-Path $env:LOCALAPPDATA 'AeroLink\notification-authority' }
    $authorityRoot = [IO.Path]::GetFullPath($authorityRoot)
    return Join-Path $authorityRoot ($parsed.ToString('D') + '.json')
}

function Revoke-AeroLinkNotificationSendGeneration {
    <# This authority is deliberately outside database/configuration archives. Rollback never restores it. #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$ProductRoot)
    Import-Module (Join-Path $PSScriptRoot 'AeroLinkInstallation.psm1') -Force
    $installation = Get-AeroLinkInstallationPaths -ProductRoot $ProductRoot
    $instance = Get-AeroLinkInstanceConfig -ProductRoot $ProductRoot -Mode Development
    if ([string]::IsNullOrWhiteSpace([string]$instance.InstanceId)) {
        # A host without an instance identity cannot match any external send authority.
        return [pscustomobject]@{ State = 'DisabledWithoutInstallationIdentity'; SendGeneration = $null }
    }
    $path = Get-AeroLinkNotificationAuthorityPath -InstanceId $instance.InstanceId
    $root = Split-Path $path -Parent
    $installationPrefix = [IO.Path]::GetFullPath($installation.InstallationRoot).TrimEnd('\') + '\'
    if (($root.TrimEnd('\') + '\').StartsWith($installationPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Notification revocation authority must remain outside installation database/configuration backups.'
    }
    if (Test-Path -LiteralPath $root) {
        $item = Get-Item -LiteralPath $root
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Notification authority root cannot be a reparse point.' }
    }
    else { New-Item -ItemType Directory -Path $root -Force | Out-Null }
    $policy = [ordered]@{
        installationId = ([guid]$instance.InstanceId).ToString('D'); hostIdentity = [Environment]::MachineName
        sendGeneration = [guid]::NewGuid().ToString('D'); policyRevision = [guid]::NewGuid().ToString('D')
        maximumMode = 0; relayHosts = @(); senders = @(); recipientDomains = @(); recipientAddresses = @()
        diagnosticTarget = ''; baseUrl = ''; allowManagedCredentials = $false; trustAnchorsPem = @()
    }
    if (Test-Path -LiteralPath $path) {
        $existing = Get-Item -LiteralPath $path
        if ($existing.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Notification authority file cannot be a reparse point.' }
        # Old allowed values are intentionally not restored: commissioning must explicitly permit G2.
    }
    # Advance the independent witness first. An interrupted restore or replacement with an archived
    # policy cannot revive the previous generation. Neither authority file is in configuration archives.
    $witnessPath = $path + '.generation'
    if ((Test-Path -LiteralPath $witnessPath) -and ((Get-Item -LiteralPath $witnessPath).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Notification generation witness cannot be a reparse point.'
    }
    $witnessTemporary = Join-Path $root ('.generation-' + [guid]::NewGuid().ToString('N') + '.tmp')
    $witnessBytes = [Text.Encoding]::UTF8.GetBytes([string]$policy.sendGeneration)
    $witnessBackup = Join-Path $root ('.generation-backup-' + [guid]::NewGuid().ToString('N') + '.tmp')
    try {
        $witnessStream = [IO.FileStream]::new($witnessTemporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None, 4096, [IO.FileOptions]::WriteThrough)
        try { $witnessStream.Write($witnessBytes, 0, $witnessBytes.Length); $witnessStream.Flush($true) } finally { $witnessStream.Dispose() }
        $witnessSecurity = [Security.AccessControl.FileSecurity]::new()
        $witnessSecurity.SetAccessRuleProtection($true, $false)
        foreach ($sid in @([Security.Principal.WindowsIdentity]::GetCurrent().User, [Security.Principal.SecurityIdentifier]::new('S-1-5-18'), [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))) {
            [void]$witnessSecurity.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid, [Security.AccessControl.FileSystemRights]::FullControl, [Security.AccessControl.AccessControlType]::Allow))
        }
        Set-Acl -LiteralPath $witnessTemporary -AclObject $witnessSecurity
        if (Test-Path -LiteralPath $witnessPath) { [IO.File]::Replace($witnessTemporary, $witnessPath, $witnessBackup) }
        else { [IO.File]::Move($witnessTemporary, $witnessPath) }
        Set-Acl -LiteralPath $witnessPath -AclObject $witnessSecurity
        if ([IO.File]::ReadAllText($witnessPath) -ne [string]$policy.sendGeneration) { throw 'Notification generation witness readback failed.' }
    }
    finally {
        if (Test-Path -LiteralPath $witnessTemporary) { Remove-Item -LiteralPath $witnessTemporary -Force }
        if (Test-Path -LiteralPath $witnessBackup) { Remove-Item -LiteralPath $witnessBackup -Force }
    }
    $temporary = Join-Path $root ('.revocation-' + [guid]::NewGuid().ToString('N') + '.tmp')
    $policyBackup = Join-Path $root ('.policy-backup-' + [guid]::NewGuid().ToString('N') + '.tmp')
    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject $policy -Depth 6 -Compress))
        $stream = [IO.FileStream]::new($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None, 4096, [IO.FileOptions]::WriteThrough)
        try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
        $security = [Security.AccessControl.FileSecurity]::new()
        $security.SetAccessRuleProtection($true, $false)
        foreach ($sid in @([Security.Principal.WindowsIdentity]::GetCurrent().User, [Security.Principal.SecurityIdentifier]::new('S-1-5-18'), [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))) {
            $rule = [Security.AccessControl.FileSystemAccessRule]::new($sid, [Security.AccessControl.FileSystemRights]::FullControl, [Security.AccessControl.AccessControlType]::Allow)
            [void]$security.AddAccessRule($rule)
        }
        Set-Acl -LiteralPath $temporary -AclObject $security
        if (Test-Path -LiteralPath $path) { [IO.File]::Replace($temporary, $path, $policyBackup) }
        else { [IO.File]::Move($temporary, $path) }
        Set-Acl -LiteralPath $path -AclObject $security
        $readback = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        if ([string]$readback.sendGeneration -ne [string]$policy.sendGeneration -or [int]$readback.maximumMode -ne 0) { throw 'Notification revocation readback failed.' }
        return [pscustomobject]@{ State = 'DisabledRevoked'; SendGeneration = [string]$policy.sendGeneration }
    }
    finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
        if (Test-Path -LiteralPath $policyBackup) { Remove-Item -LiteralPath $policyBackup -Force }
    }
}

Export-ModuleMember -Function Get-AeroLinkNotificationAuthorityPath, Revoke-AeroLinkNotificationSendGeneration
