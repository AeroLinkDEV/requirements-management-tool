#Requires -Version 5.1
<##
    Installation-scoped protected settings for the read-only GitLab connector.

    The token is never represented in a command line, task definition, repository file, transition spool,
    or operator receipt.  Only the process which is about to create the API process resolves the token into
    a child environment.  The persisted record contains non-secret connector metadata and a Windows DPAPI
    LocalMachine ciphertext.  The file and its parent directory have an explicit DACL for the configured
    launcher account, SYSTEM and Administrators, with inheritance disabled.
##>

Set-StrictMode -Version Latest

$script:ProtectedConfigSchemaVersion = 1
$script:ProtectedConfigFileName = 'gitlab.json'
$script:ProtectedConfigDirectoryName = 'AeroLink\protected-config'
$script:ProtectedConfigEntropyLabel = 'AeroLink protected GitLab connector v1'

Add-Type -AssemblyName System.Security

function Get-AeroLinkProtectedConfigOwnerSid {
    $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    if ($null -eq $identity -or $null -eq $identity.User) { throw 'The current Windows identity could not be established.' }
    return $identity.User.Value
}

function Resolve-AeroLinkProtectedInstallationRoot {
    param([Parameter(Mandatory)][string]$InstallationRoot)
    if ([string]::IsNullOrWhiteSpace($InstallationRoot) -or -not [IO.Path]::IsPathRooted($InstallationRoot)) {
        throw 'The protected GitLab configuration requires an absolute installation root.'
    }
    return [IO.Path]::GetFullPath($InstallationRoot).TrimEnd('\')
}

function Get-AeroLinkProtectedInstallationHash {
    param([Parameter(Mandatory)][string]$InstallationRoot)
    $canonical = (Resolve-AeroLinkProtectedInstallationRoot $InstallationRoot).ToLowerInvariant()
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($canonical)))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
}

function Get-AeroLinkProtectedConfigPath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$InstallationRoot,
        [string]$RootOverride
    )
    $root = if ([string]::IsNullOrWhiteSpace($RootOverride)) {
        Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)) $script:ProtectedConfigDirectoryName
    } else {
        if (-not [IO.Path]::IsPathRooted($RootOverride)) { throw 'The protected configuration root override must be absolute.' }
        [IO.Path]::GetFullPath($RootOverride)
    }
    return Join-Path (Join-Path $root (Get-AeroLinkProtectedInstallationHash $InstallationRoot)) $script:ProtectedConfigFileName
}

function Get-AeroLinkProtectedConfigEntropy {
    param([Parameter(Mandatory)][string]$InstallationRoot)
    $value = (Resolve-AeroLinkProtectedInstallationRoot $InstallationRoot) + '|' + $script:ProtectedConfigEntropyLabel
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($value)) }
    finally { $sha.Dispose() }
}

function Assert-AeroLinkProtectedHttpsOrigin {
    param([Parameter(Mandatory)][string]$BaseUrl)
    $uri = $null
    if (-not [Uri]::TryCreate($BaseUrl.Trim(), [UriKind]::Absolute, [ref]$uri) -or
        $uri.Scheme -ne 'https' -or [string]::IsNullOrWhiteSpace($uri.Host) -or
        -not [string]::IsNullOrEmpty($uri.UserInfo) -or -not [string]::IsNullOrEmpty($uri.Query) -or
        -not [string]::IsNullOrEmpty($uri.Fragment)) {
        throw 'ProjectGitLab BaseUrl must be an absolute HTTPS origin without credentials, query, or fragment.'
    }
    return $uri.GetLeftPart([UriPartial]::Authority).TrimEnd('/') + $uri.AbsolutePath.TrimEnd('/')
}

function Assert-AeroLinkProtectedGuid {
    param([Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][string]$Value)
    $parsed = [Guid]::Empty
    if (-not [Guid]::TryParse($Value, [ref]$parsed) -or $parsed -eq [Guid]::Empty) { throw "$Name must be a non-empty GUID." }
    return $parsed.ToString('D')
}

function Assert-AeroLinkProtectedRemoteProjectId {
    param([Parameter(Mandatory)][string]$Value)
    if ($Value -notmatch '^[1-9][0-9]*$') { throw 'SyntheticDemoRemoteProjectId must be a positive decimal GitLab project id.' }
    return $Value
}

function Protect-AeroLinkProtectedToken {
    param([Parameter(Mandatory)][byte[]]$Plaintext, [Parameter(Mandatory)][string]$InstallationRoot)
    $protected = [Security.Cryptography.ProtectedData]::Protect($Plaintext, (Get-AeroLinkProtectedConfigEntropy $InstallationRoot), [Security.Cryptography.DataProtectionScope]::LocalMachine)
    try { return [Convert]::ToBase64String($protected) }
    finally { [Array]::Clear($protected, 0, $protected.Length) }
}

function Unprotect-AeroLinkProtectedToken {
    param([Parameter(Mandatory)][string]$Ciphertext, [Parameter(Mandatory)][string]$InstallationRoot)
    try { $protected = [Convert]::FromBase64String($Ciphertext) }
    catch { throw 'The protected GitLab credential is not valid ciphertext.' }
    try { return [Security.Cryptography.ProtectedData]::Unprotect($protected, (Get-AeroLinkProtectedConfigEntropy $InstallationRoot), [Security.Cryptography.DataProtectionScope]::LocalMachine) }
    catch { throw 'The protected GitLab credential could not be decrypted by this Windows installation.' }
    finally { if ($protected) { [Array]::Clear($protected, 0, $protected.Length) } }
}

function Get-AeroLinkProtectedAclSid {
    param([Parameter(Mandatory)]$IdentityReference)
    try {
        if ($IdentityReference -is [Security.Principal.SecurityIdentifier]) { return $IdentityReference.Value }
        return $IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value
    } catch { return $null }
}

function Set-AeroLinkProtectedDirectoryAcl {
    param([Parameter(Mandatory)][string]$DirectoryPath, [Parameter(Mandatory)][string]$OwnerSid)
    $security = Get-Acl -LiteralPath $DirectoryPath
    $security.SetAccessRuleProtection($true, $false)
    foreach ($sid in @($OwnerSid, 'S-1-5-18', 'S-1-5-32-544')) {
        $rule = New-Object Security.AccessControl.FileSystemAccessRule(
            (New-Object Security.Principal.SecurityIdentifier($sid)), 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
        $security.SetAccessRule($rule)
    }
    Set-Acl -LiteralPath $DirectoryPath -AclObject $security
}

function Set-AeroLinkProtectedFileAcl {
    param([Parameter(Mandatory)][string]$FilePath, [Parameter(Mandatory)][string]$OwnerSid)
    $security = Get-Acl -LiteralPath $FilePath
    $security.SetAccessRuleProtection($true, $false)
    foreach ($sid in @($OwnerSid, 'S-1-5-18', 'S-1-5-32-544')) {
        $rule = New-Object Security.AccessControl.FileSystemAccessRule(
            (New-Object Security.Principal.SecurityIdentifier($sid)), 'FullControl', 'None', 'None', 'Allow')
        $security.AddAccessRule($rule)
    }
    Set-Acl -LiteralPath $FilePath -AclObject $security
}

function Assert-AeroLinkProtectedAcl {
    param([Parameter(Mandatory)][string]$FilePath, [Parameter(Mandatory)][string]$OwnerSid)
    $acl = Get-Acl -LiteralPath $FilePath
    if (-not $acl.AreAccessRulesProtected) { throw 'The protected GitLab configuration inherits permissions; refusing to read it.' }
    $allowed = @($OwnerSid, 'S-1-5-18', 'S-1-5-32-544')
    $seen = @{}
    $rules = @($acl.Access)
    foreach ($rule in $rules) {
        $sid = Get-AeroLinkProtectedAclSid $rule.IdentityReference
        if ($rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow -or $sid -notin $allowed -or
            (($rule.FileSystemRights -band [Security.AccessControl.FileSystemRights]::FullControl) -ne [Security.AccessControl.FileSystemRights]::FullControl)) {
            throw 'The protected GitLab configuration has an unexpected ACL entry; refusing to read it.'
        }
        $seen[$sid] = $true
    }
    foreach ($sid in $allowed) {
        if (-not $seen.ContainsKey($sid)) { throw 'The protected GitLab configuration is missing a required FullControl ACL principal.' }
    }
}

function Assert-AeroLinkProtectedDirectoryAcl {
    param([Parameter(Mandatory)][string]$DirectoryPath, [Parameter(Mandatory)][string]$OwnerSid)
    $acl = Get-Acl -LiteralPath $DirectoryPath
    if (-not $acl.AreAccessRulesProtected) { throw 'The protected GitLab configuration directory inherits permissions; refusing to use it.' }
    $allowed = @($OwnerSid, 'S-1-5-18', 'S-1-5-32-544')
    $seen = @{}
    $rules = @($acl.Access)
    foreach ($rule in $rules) {
        $sid = Get-AeroLinkProtectedAclSid $rule.IdentityReference
        if ($rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow -or $sid -notin $allowed -or
            (($rule.FileSystemRights -band [Security.AccessControl.FileSystemRights]::FullControl) -ne [Security.AccessControl.FileSystemRights]::FullControl)) {
            throw 'The protected GitLab configuration directory has an unexpected ACL entry; refusing to use it.'
        }
        $seen[$sid] = $true
    }
    foreach ($sid in $allowed) {
        if (-not $seen.ContainsKey($sid)) { throw 'The protected GitLab configuration directory is missing a required FullControl ACL principal.' }
    }
}

function Read-AeroLinkProtectedRecord {
    param([Parameter(Mandatory)][string]$ConfigPath, [Parameter(Mandatory)][string]$InstallationRoot, [switch]$DecryptToken)
    if (-not (Test-Path -LiteralPath $ConfigPath -PathType Leaf)) { return $null }
    $ownerSid = Get-AeroLinkProtectedConfigOwnerSid
    Assert-AeroLinkProtectedDirectoryAcl -DirectoryPath (Split-Path -Parent $ConfigPath) -OwnerSid $ownerSid
    Assert-AeroLinkProtectedAcl -FilePath $ConfigPath -OwnerSid $ownerSid
    try { $record = Get-Content -LiteralPath $ConfigPath -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop }
    catch { throw 'The protected GitLab configuration is malformed; refusing to use it.' }
    if ($null -eq $record -or [int]$record.schemaVersion -ne $script:ProtectedConfigSchemaVersion -or
        [string]$record.installationRootHash -ne (Get-AeroLinkProtectedInstallationHash $InstallationRoot) -or
        [string]$record.ownerSid -ne $ownerSid -or [string]::IsNullOrWhiteSpace([string]$record.protectedToken) -or
        [string]$record.configurationFingerprint -notmatch '^[0-9a-fA-F]{64}$') {
        throw 'The protected GitLab configuration identity or schema is unexpected; refusing to use it.'
    }
    $encoded = $null
    try {
        $encoded = [Convert]::FromBase64String([string]$record.protectedToken)
        if ($encoded.Length -eq 0) { throw 'empty' }
    }
    catch { throw 'The protected GitLab credential is not valid ciphertext.' }
    finally { if ($encoded) { [Array]::Clear($encoded, 0, $encoded.Length) } }
    $baseUrl = Assert-AeroLinkProtectedHttpsOrigin ([string]$record.baseUrl)
    $syntheticProject = [string]$record.syntheticDemoProjectId
    $syntheticRemote = [string]$record.syntheticDemoRemoteProjectId
    if ([string]::IsNullOrWhiteSpace($syntheticProject) -xor [string]::IsNullOrWhiteSpace($syntheticRemote)) { throw 'Synthetic GitLab display metadata must be configured as a pair.' }
    if ($syntheticProject) { $syntheticProject = Assert-AeroLinkProtectedGuid 'SyntheticDemoProjectId' $syntheticProject; $syntheticRemote = Assert-AeroLinkProtectedRemoteProjectId $syntheticRemote }
    $scope = $record.releasedSyntheticSourceSupplementScope
    $scopeValues = @('programId','projectId','releaseId','baselineId','campaignId') | ForEach-Object { [string]$scope.$_ }
    $scopeConfigured = @($scopeValues | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }).Count
    if ($scopeConfigured -ne 0 -and $scopeConfigured -ne 5) { throw 'ReleasedSyntheticSourceSupplementScope must be empty or contain all five exact GUIDs.' }
    if ($scopeConfigured -eq 5) {
        $scope = [ordered]@{ programId = Assert-AeroLinkProtectedGuid 'ProgramId' $scopeValues[0]; projectId = Assert-AeroLinkProtectedGuid 'ProjectId' $scopeValues[1]; releaseId = Assert-AeroLinkProtectedGuid 'ReleaseId' $scopeValues[2]; baselineId = Assert-AeroLinkProtectedGuid 'BaselineId' $scopeValues[3]; campaignId = Assert-AeroLinkProtectedGuid 'CampaignId' $scopeValues[4] }
    } else { $scope = [ordered]@{ programId = ''; projectId = ''; releaseId = ''; baselineId = ''; campaignId = '' } }
    $token = $null
    if ($DecryptToken) {
        $plain = Unprotect-AeroLinkProtectedToken ([string]$record.protectedToken) $InstallationRoot
        try { $token = [Text.Encoding]::UTF8.GetString($plain) }
        finally { [Array]::Clear($plain, 0, $plain.Length) }
        if ([string]::IsNullOrWhiteSpace($token)) { throw 'The protected GitLab credential is empty; refusing to use it.' }
    }
    return [pscustomobject]@{ Path = $ConfigPath; OwnerSid = $ownerSid; BaseUrl = $baseUrl; Token = $token; SyntheticDemoProjectId = $syntheticProject; SyntheticDemoRemoteProjectId = $syntheticRemote; Scope = $scope; Fingerprint = [string]$record.configurationFingerprint }
}

function Get-AeroLinkProtectedGitLabDescriptor {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$InstallationRoot, [string]$RootOverride)
    $path = Get-AeroLinkProtectedConfigPath -InstallationRoot $InstallationRoot -RootOverride $RootOverride
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return [pscustomobject]@{ Configured = $false; Path = $path; Fingerprint = 'unconfigured' } }
    $record = Read-AeroLinkProtectedRecord -ConfigPath $path -InstallationRoot $InstallationRoot
    return [pscustomobject]@{ Configured = $true; Path = $path; Fingerprint = $record.Fingerprint; BaseUrl = $record.BaseUrl; SyntheticDemoProjectId = $record.SyntheticDemoProjectId; SyntheticDemoRemoteProjectId = $record.SyntheticDemoRemoteProjectId; Scope = $record.Scope }
}

function Get-AeroLinkProtectedGitLabRuntimeEnvironment {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$InstallationRoot, [Parameter(Mandatory)][string]$ConfigPath)
    $record = Read-AeroLinkProtectedRecord -ConfigPath $ConfigPath -InstallationRoot $InstallationRoot -DecryptToken
    if ($null -eq $record) { throw 'The protected GitLab configuration disappeared before the API process was created.' }
    $environment = [ordered]@{
        ProjectGitLab__BaseUrl = $record.BaseUrl
        ProjectGitLab__ReadAccessToken = $record.Token
        Runtime__GitLabConfigFingerprint = $record.Fingerprint
    }
    if ($record.SyntheticDemoProjectId) {
        $environment['ProjectGitLab__SyntheticDemoProjectId'] = $record.SyntheticDemoProjectId
        $environment['ProjectGitLab__SyntheticDemoRemoteProjectId'] = $record.SyntheticDemoRemoteProjectId
    }
    foreach ($name in @('ProgramId','ProjectId','ReleaseId','BaselineId','CampaignId')) {
        $environment["ProjectGitLab__ReleasedSyntheticSourceSupplementScope__${name}"] = $record.Scope.($name.Substring(0,1).ToLowerInvariant() + $name.Substring(1))
    }
    return [pscustomobject]@{ Environment = $environment; Fingerprint = $record.Fingerprint; Path = $record.Path }
}

function Set-AeroLinkProtectedGitLabConfig {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$InstallationRoot,
        [Parameter(Mandatory)][string]$BaseUrl,
        [securestring]$ReadAccessToken,
        [string]$SyntheticDemoProjectId = '', [string]$SyntheticDemoRemoteProjectId = '',
        [string]$ProgramId = '', [string]$ProjectId = '', [string]$ReleaseId = '', [string]$BaselineId = '', [string]$CampaignId = '',
        [string]$RootOverride
    )
    $installationRoot = Resolve-AeroLinkProtectedInstallationRoot $InstallationRoot
    if ($null -eq $ReadAccessToken) { $ReadAccessToken = Read-Host 'GitLab read access token (input is hidden)' -AsSecureString }
    $baseUrl = Assert-AeroLinkProtectedHttpsOrigin $BaseUrl
    $syntheticPair = @(@($SyntheticDemoProjectId, $SyntheticDemoRemoteProjectId) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($syntheticPair.Count -ne 0 -and $syntheticPair.Count -ne 2) { throw 'Synthetic GitLab display metadata must be supplied as both project ids or omitted.' }
    if ($syntheticPair.Count -eq 2) { $SyntheticDemoProjectId = Assert-AeroLinkProtectedGuid 'SyntheticDemoProjectId' $SyntheticDemoProjectId; $SyntheticDemoRemoteProjectId = Assert-AeroLinkProtectedRemoteProjectId $SyntheticDemoRemoteProjectId }
    $scopeValues = @($ProgramId,$ProjectId,$ReleaseId,$BaselineId,$CampaignId)
    $scopeCount = @($scopeValues | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }).Count
    if ($scopeCount -ne 0 -and $scopeCount -ne 5) { throw 'ReleasedSyntheticSourceSupplementScope must be empty or contain all five exact GUIDs.' }
    if ($scopeCount -eq 5) {
        $ProgramId = Assert-AeroLinkProtectedGuid 'ProgramId' $ProgramId; $ProjectId = Assert-AeroLinkProtectedGuid 'ProjectId' $ProjectId; $ReleaseId = Assert-AeroLinkProtectedGuid 'ReleaseId' $ReleaseId; $BaselineId = Assert-AeroLinkProtectedGuid 'BaselineId' $BaselineId; $CampaignId = Assert-AeroLinkProtectedGuid 'CampaignId' $CampaignId
    } else { $ProgramId = ''; $ProjectId = ''; $ReleaseId = ''; $BaselineId = ''; $CampaignId = '' }
    $ownerSid = Get-AeroLinkProtectedConfigOwnerSid
    $path = Get-AeroLinkProtectedConfigPath -InstallationRoot $installationRoot -RootOverride $RootOverride
    if ($ReadAccessToken.Length -le 0) { throw 'The GitLab read access token was empty; refusing to write configuration.' }
    $directory = Split-Path -Parent $path
    if (Test-Path -LiteralPath $path -PathType Leaf) {
        # Configuration replacement is additive and idempotent, but never repairs an already-invalid record.
        # An operator must resolve ownership, ACL or ciphertext corruption explicitly before trying again.
        $null = Read-AeroLinkProtectedRecord -ConfigPath $path -InstallationRoot $installationRoot
    }
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($ReadAccessToken)
    try {
        $plainText = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
        if ([string]::IsNullOrWhiteSpace($plainText)) { throw 'The GitLab read access token was empty; refusing to write configuration.' }
        $plain = [Text.Encoding]::UTF8.GetBytes($plainText)
        try { $ciphertext = Protect-AeroLinkProtectedToken -Plaintext $plain -InstallationRoot $installationRoot }
        finally { [Array]::Clear($plain, 0, $plain.Length) }
    }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr); $plainText = $null }
    if ([string]::IsNullOrWhiteSpace($ciphertext)) { throw 'The GitLab read access token was empty; refusing to write configuration.' }
    $directoryExists = Test-Path -LiteralPath $directory -PathType Container
    if ($directoryExists) {
        # Existing configuration was validated above; a missing file still requires the same strict parent check.
        Assert-AeroLinkProtectedDirectoryAcl -DirectoryPath $directory -OwnerSid $ownerSid
    } else {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
        Set-AeroLinkProtectedDirectoryAcl -DirectoryPath $directory -OwnerSid $ownerSid
    }
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $generation = [Guid]::NewGuid().ToString('N')
        $fingerprintInput = "$generation|$baseUrl|$SyntheticDemoProjectId|$SyntheticDemoRemoteProjectId|$ProgramId|$ProjectId|$ReleaseId|$BaselineId|$CampaignId|$ownerSid"
        $fingerprint = ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($fingerprintInput)))).Replace('-', '').ToLowerInvariant()
    } finally { $sha.Dispose() }
    $record = [ordered]@{ schemaVersion = $script:ProtectedConfigSchemaVersion; installationRootHash = Get-AeroLinkProtectedInstallationHash $installationRoot; ownerSid = $ownerSid; baseUrl = $baseUrl; protectedToken = $ciphertext; syntheticDemoProjectId = $SyntheticDemoProjectId; syntheticDemoRemoteProjectId = $SyntheticDemoRemoteProjectId; releasedSyntheticSourceSupplementScope = [ordered]@{ programId = $ProgramId; projectId = $ProjectId; releaseId = $ReleaseId; baselineId = $BaselineId; campaignId = $CampaignId }; configurationFingerprint = $fingerprint; updatedAtUtc = (Get-Date).ToUniversalTime().ToString('o') }
    $tempPath = Join-Path $directory ('.gitlab.' + [guid]::NewGuid().ToString('N') + '.tmp')
    $backupPath = Join-Path $directory ('.gitlab.' + [guid]::NewGuid().ToString('N') + '.bak')
    try {
        [IO.File]::WriteAllText($tempPath, ($record | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding($false)))
        Set-AeroLinkProtectedFileAcl -FilePath $tempPath -OwnerSid $ownerSid
        if (Test-Path -LiteralPath $path -PathType Leaf) { [IO.File]::Replace($tempPath, $path, $backupPath, $true) } else { [IO.File]::Move($tempPath, $path) }
    }
    finally {
        if (Test-Path -LiteralPath $tempPath -PathType Leaf) { Remove-Item -LiteralPath $tempPath -Force -ErrorAction SilentlyContinue }
        if (Test-Path -LiteralPath $backupPath -PathType Leaf) { Remove-Item -LiteralPath $backupPath -Force -ErrorAction SilentlyContinue }
    }
    return [pscustomobject]@{ Configured = $true; Path = $path; Fingerprint = $fingerprint; OwnerSid = $ownerSid; BaseUrl = $baseUrl; SyntheticDemoProjectId = $SyntheticDemoProjectId; SyntheticDemoRemoteProjectId = $SyntheticDemoRemoteProjectId; ScopeConfigured = ($scopeCount -eq 5) }
}

Export-ModuleMember -Function Get-AeroLinkProtectedConfigPath, Get-AeroLinkProtectedGitLabDescriptor, Get-AeroLinkProtectedGitLabRuntimeEnvironment, Set-AeroLinkProtectedGitLabConfig
