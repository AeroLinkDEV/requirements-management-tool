#Requires -Version 5.1
<#
    Pieces of a site server installation (#1614 Phase 1, items 1.3-1.5) for the installer to call.

    A site installation is one of the two AeroLink installations on the company host (D8):
    C:\AeroLink\AeroLink Production and C:\AeroLink\AeroLink Development. Each has its own ports, PostgreSQL
    cluster, evidence root and key ring, and declares all of that in the `site` block of its instance.json
    (Get-AeroLinkSiteDeclaration in AeroLinkInstallation.psm1 reads it).

    Run-as identities (D2, revised 15 Oct). Each installation runs as its own virtual service accounts:
      NT SERVICE\AeroLink-Production             the API
      NT SERVICE\AeroLink-Production-PostgreSQL  its PostgreSQL cluster
    PostgreSQL has its own account rather than sharing the API's. The API needs no file access to pgdata, only a
    database connection, and PostgreSQL needs none to the key ring, connector key, evidence or protected secrets.
    With separate accounts a compromise of either process reaches only its own files, and Development can reach
    nothing of Production's. A virtual account still acts as the computer account on the network, so the reasons
    D2 chose NETWORK SERVICE (Kerberos, AD lookups, CA enrolment) still hold.

    Their SIDs are computed from the service names (Get-AeroLinkServiceSid), so folders and secrets are granted
    before the services exist; a grant takes effect when the service runs as the account. The PostgreSQL service is
    created by Register-AeroLinkSitePostgresService; the API service belongs to boot supervision (#1614 1.8) and
    must be named exactly as declared.

    This module never touches an installation without a site declaration. HOME, laptop and demo installations,
    their PostgreSQL on 54329 and their per-user state are out of its reach by construction.

    Every permission is set and read by SID. Nothing here parses icacls, schtasks, netsh or sc output, and no
    task is registered with schtasks /RU, which hangs on a hidden password prompt for a service account.

    Order of use by an installer, all elevated:
      1. Set-AeroLinkInstanceConfig -Classification SiteProduction|SiteDevelopment
      2. Set-AeroLinkSiteDeclaration      ports, service accounts, database role, deployment and instance ids
      3. Set-AeroLinkSiteFolderAcl        owner Administrators; program read-only; data writable by its account only
      4. Initialize-AeroLinkSitePostgres  scram-sha-256 cluster on 127.0.0.1, non-superuser owner role
      5. Register-AeroLinkSitePostgresService
    and, at every API start, as the API's account:
      Get-AeroLinkSiteRuntimeEnvironment -IncludeConnectionSecret
#>

Set-StrictMode -Version Latest

Import-Module (Join-Path $PSScriptRoot 'AeroLinkInstallation.psm1')
Import-Module (Join-Path $PSScriptRoot 'AeroLinkProtectedConfig.psm1')
Import-Module (Join-Path $PSScriptRoot 'AeroLinkNativeRunner.psm1')

$script:SystemSid = 'S-1-5-18'
$script:AdministratorsSid = 'S-1-5-32-544'
$script:UsersSid = 'S-1-5-32-545'

function Test-AeroLinkElevatedAdministrator {
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Read-AeroLinkInstanceDocument {
    param([Parameter(Mandatory)][string]$ConfigPath)
    $document = [ordered]@{}
    if (Test-Path -LiteralPath $ConfigPath -PathType Leaf) {
        try { $current = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json }
        catch { throw "The AeroLink instance declaration at $ConfigPath is malformed: $($_.Exception.Message)" }
        foreach ($property in $current.PSObject.Properties) { $document[$property.Name] = $property.Value }
    }
    return $document
}

function New-AeroLinkSiteDeploymentId {
    $bytes = New-Object byte[] 8
    $generator = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $generator.GetBytes($bytes) } finally { $generator.Dispose() }
    return 'aerolink-site-' + ([BitConverter]::ToString($bytes)).Replace('-', '').ToLowerInvariant()
}

function Get-AeroLinkDefaultSiteServiceName {
    # "C:\AeroLink\AeroLink Production" -> AeroLink-Production (D2 revised).
    param([Parameter(Mandatory)][string]$InstallationRoot)
    $leaf = (Split-Path -Leaf ([IO.Path]::GetFullPath($InstallationRoot).TrimEnd('\'))) -replace '^AeroLink[\s_-]*', ''
    $leaf = $leaf -replace '[^A-Za-z0-9]', ''
    if (-not $leaf) { throw "No service name can be derived from '$InstallationRoot'; pass -ServiceName." }
    return "AeroLink-$leaf"
}

function Assert-AeroLinkUniqueSiteInstanceId {
    <#
        .SYNOPSIS Refuses a site declaration that reuses the other canonical site's instance id.
        .DESCRIPTION
            Production and Development share a host name. Their site cookie names and Windows-link
            cookie names are derived from instanceId, so copying one instance.json into the other must
            fail before the declaration is written. Arbitrary disposable roots have no sibling site and
            retain the existing qualification path.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$InstallationRoot,
        [Parameter(Mandatory)][string]$InstanceId
    )
    if ([string]::IsNullOrWhiteSpace($InstanceId)) { throw 'A site declaration requires a non-empty instanceId.' }
    $targetGuid = [guid]::Empty
    if (-not [guid]::TryParse($InstanceId.Trim(), [ref]$targetGuid)) {
        throw "The site instanceId '$InstanceId' is not a valid GUID; refusing to write a declaration or derive cookie names from it."
    }
    $root = [IO.Path]::GetFullPath($InstallationRoot).TrimEnd('\', '/')
    $parent = Split-Path -Parent $root
    foreach ($siblingName in @('AeroLink Production', 'AeroLink Development')) {
        $sibling = [IO.Path]::GetFullPath((Join-Path $parent $siblingName)).TrimEnd('\', '/')
        if ([string]::Equals($sibling, $root, [StringComparison]::OrdinalIgnoreCase)) { continue }
        $configPath = Join-Path $sibling 'instance.json'
        if (-not (Test-Path -LiteralPath $configPath -PathType Leaf)) { continue }
        try { $document = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json }
        catch { throw "The sibling site declaration at $configPath is malformed: $($_.Exception.Message)" }
        $classification = if ($document.PSObject.Properties['classification']) { [string]$document.classification } else { '' }
        $otherId = if ($document.PSObject.Properties['instanceId']) { [string]$document.instanceId } else { '' }
        if ((Test-AeroLinkSiteClassification $classification) -and -not [string]::IsNullOrWhiteSpace($otherId)) {
            $otherGuid = [guid]::Empty
            if (-not [guid]::TryParse($otherId.Trim(), [ref]$otherGuid)) {
                throw "The sibling site declaration at $configPath has an invalid instanceId '$otherId'; refusing to write a declaration while a declared site identity cannot be verified."
            }
            if ($otherGuid -eq $targetGuid) {
                throw "The site instanceId '$InstanceId' is already used by $classification at '$sibling'. Production and Development must have distinct persisted instance ids and cookie namespaces; copy the declaration only after assigning a new id."
            }
        }
    }
}

function Set-AeroLinkSiteDeclaration {
    <#
        .SYNOPSIS Declares, idempotently, a site installation's ports, service accounts, role and stable ids.
        .DESCRIPTION
            The installation must already be classified SiteProduction or SiteDevelopment. A port another
            process is listening on is refused, unless it is the port this installation already declares (its
            own service may be the listener).

            The API port declared here is the single source of that port: the site launcher binds it, and Stop,
            Restore and Diagnostics find the API there.

            Two ids are minted once and never changed: the connector deployment id (every enrolled Word connector
            is bound to it) and the installation's instanceId (notifications and runtime identity are bound to
            it). Minting the instanceId here means nothing has to write instance.json later, when the
            installation root is read-only to the service account.

            -RunAsSid and -PostgresRunAsSid override the SIDs computed from the service names. They exist for
            disposable qualification by a non-elevated user and are never needed on the host.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$InstallationRoot,
        [Parameter(Mandatory)][ValidateRange(1024, 65535)][int]$ApiPort,
        [Parameter(Mandatory)][ValidateRange(1024, 65535)][int]$PostgresPort,
        [ValidatePattern('^[A-Za-z0-9_-]{3,80}$')][string]$ServiceName,
        [ValidatePattern('^[A-Za-z0-9_-]{3,80}$')][string]$PostgresServiceName,
        [string]$RunAsSid,
        [string]$PostgresRunAsSid,
        # The full host name users browse to (D3). Diagnostics probes https://<it>:<ApiPort>.
        [string]$PublicHostName,
        [ValidatePattern('^[a-z_][a-z0-9_]{2,62}$')][string]$DatabaseRole = 'aerolink_app',
        [string]$ConnectorDeploymentId,
        # Outside the installation. Defaults to %ProgramData%\AeroLink\notification-authority\<installation hash>.
        [string]$NotificationAuthorityRoot
    )
    $root = [IO.Path]::GetFullPath($InstallationRoot)
    if (-not (Test-Path -LiteralPath $root -PathType Container)) { throw "The site installation root does not exist: $root" }
    $configPath = Join-Path $root 'instance.json'
    $document = Read-AeroLinkInstanceDocument -ConfigPath $configPath
    $classification = if ($document.Contains('classification')) { [string]$document['classification'] } else { '' }
    if (-not (Test-AeroLinkSiteClassification $classification)) {
        throw "Declare this installation SiteProduction or SiteDevelopment before giving it a site block; it is '$classification'."
    }
    $existing = Get-AeroLinkSiteDeclaration -InstallationRoot $root
    $existingRaw = if ($existing) { $document['site'] } else { $null }
    foreach ($pair in @(@($ApiPort, 'the AeroLink API', $(if ($existing) { $existing.ApiPort } else { 0 })),
                        @($PostgresPort, 'AeroLink PostgreSQL', $(if ($existing) { $existing.PostgresPort } else { 0 })))) {
        if ($pair[0] -ne $pair[2]) { Assert-AeroLinkPortAvailable -Port $pair[0] -Purpose $pair[1] }
    }
    if ($existing -and $existing.ConnectorDeploymentId) {
        if ($ConnectorDeploymentId -and $ConnectorDeploymentId -ne $existing.ConnectorDeploymentId) {
            throw "This installation's connector deployment id is already '$($existing.ConnectorDeploymentId)'. Changing it would orphan every enrolled Word connector; it is never replaced here."
        }
        $ConnectorDeploymentId = $existing.ConnectorDeploymentId
    }
    if (-not $ConnectorDeploymentId) { $ConnectorDeploymentId = New-AeroLinkSiteDeploymentId }
    if (-not $ServiceName) { $ServiceName = if ($existing) { $existing.ServiceName } else { Get-AeroLinkDefaultSiteServiceName -InstallationRoot $root } }
    if (-not $PostgresServiceName) { $PostgresServiceName = if ($existing) { $existing.PostgresServiceName } else { "$ServiceName-PostgreSQL" } }
    $site = [ordered]@{
        schemaVersion         = 1
        apiPort               = $ApiPort
        postgresPort          = $PostgresPort
        serviceName           = $ServiceName
        postgresServiceName   = $PostgresServiceName
        databaseRole          = $DatabaseRole
        connectorDeploymentId = $ConnectorDeploymentId
    }
    # Optional values persist across re-declaration unless they are passed again.
    $optional = [ordered]@{ runAsSid = $RunAsSid; postgresRunAsSid = $PostgresRunAsSid; publicHostName = $PublicHostName; notificationAuthorityRoot = $NotificationAuthorityRoot }
    foreach ($name in @($optional.Keys)) {
        $value = $optional[$name]
        if ([string]::IsNullOrWhiteSpace($value) -and $existingRaw -and $existingRaw.PSObject.Properties[$name]) { $value = [string]$existingRaw.$name }
        if ([string]::IsNullOrWhiteSpace($value)) { continue }
        if ($name -eq 'notificationAuthorityRoot') { $value = [IO.Path]::GetFullPath($value) }
        $site[$name] = $value
    }
    $document['site'] = $site
    if (-not $document.Contains('instanceId') -or [string]::IsNullOrWhiteSpace([string]$document['instanceId'])) {
        $document['instanceId'] = [guid]::NewGuid().ToString('D')
    }
    Assert-AeroLinkUniqueSiteInstanceId -InstallationRoot $root -InstanceId ([string]$document['instanceId'])
    $document['updatedAtUtc'] = (Get-Date).ToUniversalTime().ToString('o')
    $temporary = "$configPath.$([guid]::NewGuid().ToString('N')).tmp"
    try {
        [pscustomobject]$document | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $temporary -Encoding UTF8
        # Validate before replacing, so a bad value never reaches the live declaration.
        $null = Get-Content -LiteralPath $temporary -Raw | ConvertFrom-Json
        Move-Item -LiteralPath $temporary -Destination $configPath -Force
    }
    finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } }
    # Read back through the one validator, so a declaration this writes is one every reader accepts.
    return Get-AeroLinkSiteDeclaration -InstallationRoot $root
}

function ConvertTo-AeroLinkFileSystemRights {
    param([Parameter(Mandatory)][ValidateSet('Read', 'Modify', 'FullControl')][string]$Access)
    switch ($Access) {
        'Read' { return [Security.AccessControl.FileSystemRights]::ReadAndExecute }
        'Modify' { return [Security.AccessControl.FileSystemRights]::Modify }
        default { return [Security.AccessControl.FileSystemRights]::FullControl }
    }
}

function Get-AeroLinkDirectorySecurity {
    param([Parameter(Mandatory)][IO.DirectoryInfo]$Directory, [Parameter(Mandatory)][Security.AccessControl.AccessControlSections]$Sections)
    if ($PSVersionTable.PSEdition -eq 'Desktop') { return $Directory.GetAccessControl($Sections) }
    return [IO.FileSystemAclExtensions]::GetAccessControl($Directory, $Sections)
}

function Set-AeroLinkDirectoryAccessBySid {
    <#
        .SYNOPSIS Replaces a directory's DACL with exactly the given SID grants, inherited by everything below it.
        .DESCRIPTION
            Inheritance from the parent is cut and every existing explicit entry is removed, so the result is the
            grant list and nothing else. Grants are { Sid; Access = Read | Modify | FullControl }.

            Ownership matters as much as the DACL: an owner keeps READ_CONTROL and WRITE_DAC whatever the DACL
            says, so a folder somebody else pre-created (C:\ and ProgramData let ordinary users create folders)
            would let them re-grant themselves. An existing folder owned by anyone but Administrators, SYSTEM or
            the installing user is refused, and an elevated installer makes Administrators the owner.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][object[]]$Grants
    )
    $full = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $full -PathType Container)) { New-Item -ItemType Directory -Path $full -Force | Out-Null }
    $directory = [IO.DirectoryInfo]::new($full)
    $installerSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $owner = (Get-AeroLinkDirectorySecurity -Directory $directory -Sections Owner).GetOwner([Security.Principal.SecurityIdentifier]).Value
    if ($owner -notin @($script:AdministratorsSid, $script:SystemSid, $installerSid)) {
        throw "$full is owned by $owner, not by Administrators, SYSTEM or the installing user. Somebody else created it; refusing to secure a folder whose owner could re-grant itself access. Remove or re-own it deliberately first."
    }
    $security = New-Object Security.AccessControl.DirectorySecurity
    $security.SetAccessRuleProtection($true, $false)
    foreach ($grant in $Grants) {
        $sid = New-Object Security.Principal.SecurityIdentifier([string]$grant.Sid)
        $rule = New-Object Security.AccessControl.FileSystemAccessRule($sid, (ConvertTo-AeroLinkFileSystemRights $grant.Access),
            ([Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'), [Security.AccessControl.PropagationFlags]::None,
            [Security.AccessControl.AccessControlType]::Allow)
        $security.AddAccessRule($rule)
    }
    # An elevated administrator may make Administrators the owner without any extra privilege. A non-elevated
    # caller (disposable qualification) cannot, and remains the owner, which the check above already accepts.
    if (Test-AeroLinkElevatedAdministrator) { $security.SetOwner((New-Object Security.Principal.SecurityIdentifier($script:AdministratorsSid))) }
    if ($PSVersionTable.PSEdition -eq 'Desktop') { $directory.SetAccessControl($security) }
    else { [IO.FileSystemAclExtensions]::SetAccessControl($directory, $security) }
}

function Get-AeroLinkDirectoryAccessBySid {
    <#
        .SYNOPSIS A directory's owner and DACL as SIDs and rights, read from the security descriptor - never from icacls text.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path)
    # Read with READ_CONTROL alone, so an owner can inspect a folder it has deliberately been locked out of.
    $directory = [IO.DirectoryInfo]::new($Path)
    $acl = Get-AeroLinkDirectorySecurity -Directory $directory -Sections ([Security.AccessControl.AccessControlSections]'Access, Owner')
    # Any right that changes content, structure or permissions counts as write.
    $writeMask = [Security.AccessControl.FileSystemRights]'WriteData, AppendData, WriteExtendedAttributes, WriteAttributes, DeleteSubdirectoriesAndFiles, Delete, ChangePermissions, TakeOwnership'
    $genericWrite = 0x40000000 -bor 0x10000000   # GENERIC_WRITE, GENERIC_ALL
    return [pscustomobject]@{
        Path      = $Path
        Owner     = $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value
        Protected = $acl.AreAccessRulesProtected
        Rules     = @($acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]) | ForEach-Object {
            [pscustomobject]@{
                Sid       = $_.IdentityReference.Value
                Rights    = $_.FileSystemRights
                Allow     = ($_.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow)
                Inherited = $_.IsInherited
                CanWrite  = ((([int]$_.FileSystemRights) -band ([int]$writeMask -bor $genericWrite)) -ne 0)
            }
        })
    }
}

function Get-AeroLinkSiteFolderPlan {
    <#
        .SYNOPSIS Which folders a site installation owns and who may do what in each.
        .DESCRIPTION
            - The installation root and program folder: read-only to the API account.
            - The PostgreSQL binaries: read and execute for Users. They are not secret, and initdb and pg_ctl run
              under a restricted token in which Administrators is deny-only, so an installer could not otherwise
              execute them (#1636 review P1-1).
            - pgdata: writable by the PostgreSQL account and Administrators only. The API has no file access to it.
            - Data folders are writable by the API account and Administrators only; the Data Protection key ring also grants SYSTEM full control.
            - The notification authority, outside the installation: read-only to the API account. An
              administrator provisions it; the application never writes it.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$InstallationRoot)
    $site = Get-AeroLinkSiteDeclaration -InstallationRoot $InstallationRoot
    if (-not $site) { throw "$InstallationRoot has no site declaration; folder permissions are only set for a site installation." }
    $paths = Get-AeroLinkInstallationPaths -ProductRoot $site.ProgramRoot -InstallationRoot $site.InstallationRoot
    $admin = @{ Sid = $script:AdministratorsSid; Access = 'FullControl' }
    $system = @{ Sid = $script:SystemSid; Access = 'FullControl' }
    $readOnly = @($admin, $system, @{ Sid = $site.RunAsSid; Access = 'Read' })
    $binaries = @($admin, $system, @{ Sid = $script:UsersSid; Access = 'Read' })
    $cluster = @($admin, @{ Sid = $site.PostgresRunAsSid; Access = 'Modify' })
    $writable = @($admin, @{ Sid = $site.RunAsSid; Access = 'Modify' })
    $keyRingWritable = @($admin, $system, @{ Sid = $site.RunAsSid; Access = 'Modify' })
    $plan = New-Object System.Collections.Generic.List[object]
    foreach ($path in @($site.InstallationRoot, $site.ProgramRoot, $site.NotificationAuthorityRoot)) {
        $plan.Add([pscustomobject]@{ Path = $path; Kind = 'ReadOnly'; Grants = $readOnly })
    }
    $plan.Add([pscustomobject]@{ Path = $paths.PostgresHome; Kind = 'Binaries'; Grants = $binaries })
    $plan.Add([pscustomobject]@{ Path = $paths.PostgresData; Kind = 'Cluster'; Grants = $cluster })
    foreach ($path in @($site.EvidenceRoot, $site.KeyRingPath, $site.ConnectorKeyDirectory, $paths.Logs, $paths.Backups,
                        $paths.BackupVerification, $paths.RestoreWork, $paths.RestoreValidation, $paths.BootstrapState,
                        $paths.DocumentConnector, $paths.UpgradeState, $paths.SnapshotInbox)) {
        $grants = if ($path -eq $site.KeyRingPath) { $keyRingWritable } else { $writable }
        $plan.Add([pscustomobject]@{ Path = $path; Kind = 'Data'; Grants = $grants })
    }
    return $plan.ToArray()
}

function Set-AeroLinkSiteFolderAcl {
    <#
        .SYNOPSIS Creates a site installation's folders and sets their owner and permissions by SID. Idempotent.
        .DESCRIPTION
            The parent of the installation root (C:\AeroLink) is the installer's to create and lock (#1614 1.7).
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$InstallationRoot)
    $plan = Get-AeroLinkSiteFolderPlan -InstallationRoot $InstallationRoot
    # Create everything first, then set the deepest folders first and the installation root last: every folder
    # is protected, so order does not change the result, but the caller must still be able to reach a child
    # while its parent is being narrowed.
    foreach ($entry in $plan) { if (-not (Test-Path -LiteralPath $entry.Path -PathType Container)) { New-Item -ItemType Directory -Path $entry.Path -Force | Out-Null } }
    foreach ($entry in @($plan | Sort-Object { $_.Path.Length } -Descending)) { Set-AeroLinkDirectoryAccessBySid -Path $entry.Path -Grants $entry.Grants }
    return $plan
}

function New-AeroLinkSitePassword {
    # 32 random bytes, base64url: ASCII only, so SASLprep leaves it unchanged and no SQL or shell quoting applies.
    # Built straight into a SecureString, so the password never crosses a PowerShell parameter binding, which
    # module logging (event 4103) would record.
    $bytes = New-Object byte[] 32
    $generator = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $generator.GetBytes($bytes) } finally { $generator.Dispose() }
    $secure = New-Object Security.SecureString
    try { foreach ($character in [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_').ToCharArray()) { $secure.AppendChar($character) } }
    finally { [Array]::Clear($bytes, 0, $bytes.Length) }
    $secure.MakeReadOnly()
    return $secure
}

function Use-AeroLinkPlainSecret {
    # Hands a SecureString's plaintext to a script block for the shortest possible time, then frees the copy.
    param([Parameter(Mandatory)][Security.SecureString]$Secret, [Parameter(Mandatory)][scriptblock]$Action)
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Secret)
    try { return (& $Action ([Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr))) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}

function ConvertTo-AeroLinkScramVerifier {
    <#
        .SYNOPSIS The SCRAM-SHA-256 verifier PostgreSQL stores for a password (RFC 5802/7677), computed here.
        .DESCRIPTION
            CREATE ROLE ... PASSWORD accepts a verifier in place of the password, so the application password
            never reaches the server, a SQL file or a server log - only this one-way verifier does.
    #>
    param([Parameter(Mandatory)][Security.SecureString]$Password, [int]$Iterations = 4096)
    $salt = New-Object byte[] 16
    $generator = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $generator.GetBytes($salt) } finally { $generator.Dispose() }
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Password)
    try { [byte[]]$passwordBytes = [Text.Encoding]::UTF8.GetBytes([Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
    $kdf = New-Object Security.Cryptography.Rfc2898DeriveBytes($passwordBytes, $salt, $Iterations, [Security.Cryptography.HashAlgorithmName]::SHA256)
    try { $salted = $kdf.GetBytes(32) } finally { $kdf.Dispose(); [Array]::Clear($passwordBytes, 0, $passwordBytes.Length) }
    $hmac = New-Object Security.Cryptography.HMACSHA256(, $salted)
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $clientKey = $hmac.ComputeHash([Text.Encoding]::ASCII.GetBytes('Client Key'))
        $storedKey = $sha.ComputeHash($clientKey)
        $serverKey = $hmac.ComputeHash([Text.Encoding]::ASCII.GetBytes('Server Key'))
    }
    finally { $hmac.Dispose(); $sha.Dispose(); [Array]::Clear($salted, 0, $salted.Length) }
    return 'SCRAM-SHA-256$' + $Iterations + ':' + [Convert]::ToBase64String($salt) + '$' + [Convert]::ToBase64String($storedKey) + ':' + [Convert]::ToBase64String($serverKey)
}

function New-AeroLinkPrivateDirectory {
    # A working directory only the current user (and Administrators) can open, for the transient superuser
    # password file initdb insists on and the bootstrap logs.
    $path = Join-Path ([IO.Path]::GetTempPath()) ('aerolink-site-pg-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $path | Out-Null
    Set-AeroLinkDirectoryAccessBySid -Path $path -Grants @(
        @{ Sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value; Access = 'FullControl' },
        @{ Sid = $script:AdministratorsSid; Access = 'FullControl' })
    return $path
}

function Invoke-AeroLinkSitePsql {
    param(
        [Parameter(Mandatory)][string]$Bin, [Parameter(Mandatory)][int]$Port, [Parameter(Mandatory)][string]$User,
        [Parameter(Mandatory)][string]$Database, [Parameter(Mandatory)][string]$WorkDirectory, [Parameter(Mandatory)][string]$StepName,
        [string]$Sql, [string]$SqlFile, [Security.SecureString]$Password
    )
    $previous = $env:PGPASSWORD
    try {
        # The password reaches psql through its own environment, never its command line; -w makes a missing or
        # wrong one a refusal instead of a prompt.
        if ($null -eq $Password) { Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue }
        else { Use-AeroLinkPlainSecret -Secret $Password -Action { param($plain) $env:PGPASSWORD = $plain } }
        $arguments = @('-X', '-w', '-h', '127.0.0.1', '-p', "$Port", '-U', $User, '-d', $Database, '-v', 'ON_ERROR_STOP=1', '-tA', '-q')
        if ($SqlFile) { $arguments += @('-f', $SqlFile) } else { $arguments += @('-c', $Sql) }
        $stem = Join-Path $WorkDirectory ($StepName -replace '[^A-Za-z0-9]+', '-')
        return Save-AeroLinkStepOutput (Invoke-AeroLinkNativeCommand -FilePath (Join-Path $Bin 'psql.exe') -ArgumentList $arguments `
            -StandardOutput "$stem.stdout.log" -StandardError "$stem.stderr.log" -TimeoutSeconds 60 -StepName $StepName -CaptureOutput)
    }
    finally { if ($null -eq $previous) { Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue } else { $env:PGPASSWORD = $previous } }
}

function Save-AeroLinkStepOutput {
    # The runner holds captured output in memory only; write it to the log paths it names, so a failure's kept
    # logs contain what the tool said. None of these tools prints a password.
    param([Parameter(Mandatory)]$Run)
    foreach ($pair in @(@('StdOutPath', 'StdOutText'), @('StdErrPath', 'StdErrText'))) {
        if ($Run.PSObject.Properties[$pair[0]] -and $Run.($pair[0]) -and $Run.PSObject.Properties[$pair[1]] -and $null -ne $Run.($pair[1])) {
            try { [IO.File]::WriteAllText([string]$Run.($pair[0]), [string]$Run.($pair[1]), (New-Object Text.UTF8Encoding($false))) } catch { }
        }
    }
    return $Run
}

function Get-AeroLinkStepFailure {
    # The runner's detail plus the tool's own error text.
    param([Parameter(Mandatory)]$Run)
    $text = if ($Run.PSObject.Properties['StdErrText'] -and $Run.StdErrText) { ' ' + ([string]$Run.StdErrText).Trim() } else { '' }
    return "$($Run.Detail)$text"
}

function Initialize-AeroLinkSitePostgres {
    <#
        .SYNOPSIS Initialises a site installation's PostgreSQL cluster, hardened (#1614 1.4).
        .DESCRIPTION
            - initdb with scram-sha-256 for every connection, never trust;
            - listens on 127.0.0.1 only, on the declared port, with pg_hba admitting loopback SCRAM only;
            - a non-superuser application role (the declared databaseRole) owns the aerolink database, and
              PUBLIC may connect to no other database;
            - both passwords are generated here and stored in the protected-config store: the application
              password readable by the API's account, the superuser password by Administrators only;
            - the data directory ends writable only by the PostgreSQL account and Administrators.

            Refuses a cluster that already exists (it never re-initialises), an installation without a site
            declaration (so HOME's cluster is out of reach), and a declared port something already listens on.
            The cluster is started only briefly, under the installer's account, to create the role, then
            stopped; Register-AeroLinkSitePostgresService runs it under the PostgreSQL account afterwards.

            On failure the bootstrap logs (never the password file, which is removed first) are kept under
            <root>\logs\site-postgres-init-<time>.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$InstallationRoot,
        # Defaults to the installation's own binaries; a qualification run may name another copy.
        [string]$PostgresBin,
        [string]$ProtectedConfigRoot,
        # Who may read the superuser (maintenance) password. Administrators unless a named maintenance identity is given.
        [string]$MaintenanceSid = $script:AdministratorsSid,
        [int]$TimeoutSeconds = 120
    )
    $site = Get-AeroLinkSiteDeclaration -InstallationRoot $InstallationRoot
    if (-not $site) { throw "$InstallationRoot has no site declaration. Only a site installation's cluster is initialised here." }
    # Qualification declarations may override both SIDs, but a production cluster bootstrap writes installation
    # folders and protected secrets. Establish both real virtual-account bindings before the first ACL write.
    Assert-AeroLinkSiteServiceSid -Site $site -Role Api | Out-Null
    Assert-AeroLinkSiteServiceSid -Site $site -Role Postgres | Out-Null
    $paths = Get-AeroLinkInstallationPaths -ProductRoot $site.ProgramRoot -InstallationRoot $site.InstallationRoot
    $bin = if ($PostgresBin) { [IO.Path]::GetFullPath($PostgresBin) } else { $paths.PostgresBin }
    $data = $paths.PostgresData
    $port = $site.PostgresPort
    foreach ($tool in @('initdb.exe', 'pg_ctl.exe', 'psql.exe')) {
        if (-not (Test-Path -LiteralPath (Join-Path $bin $tool) -PathType Leaf)) { throw "PostgreSQL tool $tool was not found under $bin." }
    }
    if (Test-Path -LiteralPath (Join-Path $data 'PG_VERSION') -PathType Leaf) {
        throw "A PostgreSQL cluster already exists at $data. A site cluster is initialised once and never re-initialised."
    }
    if ((Test-Path -LiteralPath $data) -and @(Get-ChildItem -LiteralPath $data -Force).Count -gt 0) {
        throw "$data is not empty. Refusing to initialise a cluster over existing files."
    }
    Assert-AeroLinkPortAvailable -Port $port -Purpose "this site installation's PostgreSQL"

    $installerSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $work = New-AeroLinkPrivateDirectory
    $started = $false
    $failed = $true
    try {
        # The installer needs its own SID on the data directory while it bootstraps: initdb and pg_ctl drop the
        # Administrators group from their token, so an Administrators-only grant would lock them out.
        $bootstrapGrants = @(@{ Sid = $script:AdministratorsSid; Access = 'FullControl' }, @{ Sid = $site.PostgresRunAsSid; Access = 'Modify' })
        if ($installerSid -ne $site.PostgresRunAsSid) { $bootstrapGrants += @{ Sid = $installerSid; Access = 'FullControl' } }
        Set-AeroLinkDirectoryAccessBySid -Path $data -Grants $bootstrapGrants

        $superuserPassword = New-AeroLinkSitePassword
        $applicationPassword = New-AeroLinkSitePassword
        # Stored before the cluster exists: a cluster whose superuser password was lost is unrecoverable, a
        # stored password for a cluster that failed to initialise is merely unused.
        $null = Set-AeroLinkProtectedSiteSecret -InstallationRoot $site.InstallationRoot -Name 'postgres-superuser' -ReaderSid $MaintenanceSid `
            -Secret $superuserPassword -RootOverride $ProtectedConfigRoot
        $null = Set-AeroLinkProtectedSiteSecret -InstallationRoot $site.InstallationRoot -Name 'postgres-application' -ReaderSid $site.RunAsSid `
            -Secret $applicationPassword -RootOverride $ProtectedConfigRoot

        $pwFile = Join-Path $work 'superuser.pw'
        Use-AeroLinkPlainSecret -Secret $superuserPassword -Action { param($plain) [IO.File]::WriteAllText($pwFile, $plain, (New-Object Text.UTF8Encoding($false))) }
        try {
            # A leaf process, so its output is captured; the working directory is the private one, because initdb's
            # restricted re-execution inherits it and must not depend on the program folder or installation root.
            $init = Save-AeroLinkStepOutput (Invoke-AeroLinkNativeCommand -FilePath (Join-Path $bin 'initdb.exe') `
                -ArgumentList @('-D', $data, '-U', 'postgres', '--pwfile', $pwFile, '-A', 'scram-sha-256', '--encoding=UTF8', '--no-locale') `
                -StandardOutput (Join-Path $work 'initdb.stdout.log') -StandardError (Join-Path $work 'initdb.stderr.log') `
                -WorkingDirectory $work -TimeoutSeconds 300 -StepName 'initdb (site)' -CaptureOutput)
        }
        finally { Remove-Item -LiteralPath $pwFile -Force -ErrorAction SilentlyContinue }
        if ($init.ExitCode -ne 0) { throw "initdb failed: $(Get-AeroLinkStepFailure $init)" }

        # Loopback SCRAM only. initdb also writes ::1 and replication lines; this file replaces them.
        $hba = "# AeroLink site installation (#1614): loopback only, SCRAM password authentication for every connection.`r`n" +
               "# TYPE  DATABASE  USER  ADDRESS       METHOD`r`nhost    all       all   127.0.0.1/32  scram-sha-256`r`n"
        [IO.File]::WriteAllText((Join-Path $data 'pg_hba.conf'), $hba, (New-Object Text.UTF8Encoding($false)))
        $settings = "`r`n# AeroLink site installation (#1614). Later lines win over initdb's defaults above.`r`n" +
                    "listen_addresses = '127.0.0.1'`r`nport = $port`r`npassword_encryption = 'scram-sha-256'`r`n" +
                    "logging_collector = on`r`nlog_directory = 'log'`r`n"
        [IO.File]::AppendAllText((Join-Path $data 'postgresql.conf'), $settings, (New-Object Text.UTF8Encoding($false)))

        $start = Invoke-AeroLinkNativeCommand -FilePath (Join-Path $bin 'pg_ctl.exe') `
            -ArgumentList @('-D', $data, '-l', (Join-Path $work 'bootstrap-postgres.log'), '-w', '-t', "$TimeoutSeconds", 'start') `
            -StandardOutput (Join-Path $work 'pg_ctl-start.stdout.log') -StandardError (Join-Path $work 'pg_ctl-start.stderr.log') `
            -TimeoutSeconds ($TimeoutSeconds + 30) -StepName 'pg_ctl start (site bootstrap)'
        # Not captured: the postmaster would inherit the pipe. Its own output is in bootstrap-postgres.log, which is kept.
        if ($start.ExitCode -ne 0) { throw "The new site cluster did not start for bootstrap: $($start.Detail) See bootstrap-postgres.log." }
        $started = $true

        $role = $site.DatabaseRole
        $verifier = ConvertTo-AeroLinkScramVerifier -Password $applicationPassword
        $sqlFile = Join-Path $work 'bootstrap.sql'
        $sql = @(
            "CREATE ROLE $role LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD '$verifier';",
            "CREATE DATABASE aerolink OWNER $role ENCODING 'UTF8' TEMPLATE template0;",
            'REVOKE ALL ON DATABASE aerolink FROM PUBLIC;',
            'REVOKE CONNECT, TEMPORARY ON DATABASE postgres FROM PUBLIC;',
            'REVOKE CONNECT, TEMPORARY ON DATABASE template1 FROM PUBLIC;',
            "GRANT CONNECT, TEMPORARY ON DATABASE aerolink TO $role;"
        ) -join "`n"
        [IO.File]::WriteAllText($sqlFile, $sql, (New-Object Text.UTF8Encoding($false)))
        $bootstrap = Invoke-AeroLinkSitePsql -Bin $bin -Port $port -User 'postgres' -Database 'postgres' -WorkDirectory $work `
            -StepName 'create application role' -SqlFile $sqlFile -Password $superuserPassword
        Remove-Item -LiteralPath $sqlFile -Force -ErrorAction SilentlyContinue
        if ($bootstrap.ExitCode -ne 0) { throw "The application role could not be created: $(Get-AeroLinkStepFailure $bootstrap)" }

        # Verify against the running cluster rather than trusting the configuration just written.
        $noPassword = Invoke-AeroLinkSitePsql -Bin $bin -Port $port -User 'postgres' -Database 'postgres' -WorkDirectory $work `
            -StepName 'superuser without password' -Sql 'SELECT 1'
        if ($noPassword.ExitCode -eq 0) { throw 'The site cluster accepted a superuser login without a password; refusing to leave it in service.' }
        $wrong = $applicationPassword.Copy(); $wrong.InsertAt(0, 'x')
        $wrongPassword = Invoke-AeroLinkSitePsql -Bin $bin -Port $port -User $role -Database 'aerolink' -WorkDirectory $work `
            -StepName 'application role wrong password' -Sql 'SELECT 1' -Password $wrong
        $wrong.Dispose()
        if ($wrongPassword.ExitCode -eq 0) { throw 'The site cluster accepted a wrong application password; refusing to leave it in service.' }
        $probe = Invoke-AeroLinkSitePsql -Bin $bin -Port $port -User $role -Database 'aerolink' -WorkDirectory $work `
            -StepName 'application role login' -Password $applicationPassword `
            -Sql "SELECT current_user || '|' || r.rolsuper || '|' || pg_get_userbyid(d.datdba) || '|' || current_setting('listen_addresses') || '|' || current_setting('port') || '|' || current_setting('password_encryption') FROM pg_roles r, pg_database d WHERE r.rolname = current_user AND d.datname = 'aerolink'"
        if ($probe.ExitCode -ne 0) { throw "The application role could not log in to aerolink: $(Get-AeroLinkStepFailure $probe)" }
        $answer = (Get-AeroLinkNativeOutputLine $probe.StdOutText)
        $expected = "$role|false|$role|127.0.0.1|$port|scram-sha-256"
        if ([string]$answer -ne $expected) { throw "The site cluster's role and listener are not as initialised: '$answer', expected '$expected'." }
        # Every rule, every column, and no rule PostgreSQL could not parse (string_agg alone would skip NULLs).
        $hbaRules = Invoke-AeroLinkSitePsql -Bin $bin -Port $port -User 'postgres' -Database 'postgres' -WorkDirectory $work `
            -StepName 'authentication rules' -Password $superuserPassword `
            -Sql "SELECT count(*) FILTER (WHERE error IS NOT NULL) || '|' || count(*) || '|' || coalesce(string_agg(concat_ws(' ', coalesce(type, '-'), coalesce(array_to_string(database, ','), '-'), coalesce(array_to_string(user_name, ','), '-'), coalesce(address, '-'), coalesce(netmask, '-'), coalesce(auth_method, '-')), ';' ORDER BY line_number), '') FROM pg_hba_file_rules"
        $hbaAnswer = Get-AeroLinkNativeOutputLine $hbaRules.StdOutText
        $hbaExpected = '0|1|host all all 127.0.0.1 255.255.255.255 scram-sha-256'
        if ($hbaRules.ExitCode -ne 0 -or [string]$hbaAnswer -ne $hbaExpected) { throw "The site cluster's authentication rules are not loopback SCRAM only: '$hbaAnswer', expected '$hbaExpected'. $(Get-AeroLinkStepFailure $hbaRules)" }
        $failed = $false
    }
    finally {
        if ($started) {
            $stop = Invoke-AeroLinkNativeCommand -FilePath (Join-Path $bin 'pg_ctl.exe') -ArgumentList @('-D', $data, '-m', 'fast', '-w', '-t', '60', 'stop') `
                -StandardOutput (Join-Path $work 'pg_ctl-stop.stdout.log') -StandardError (Join-Path $work 'pg_ctl-stop.stderr.log') `
                -TimeoutSeconds 90 -StepName 'pg_ctl stop (site bootstrap)' -CaptureOutput
            $stop = Save-AeroLinkStepOutput $stop
            if ($stop.ExitCode -ne 0) { Write-Warning "The bootstrap postmaster did not stop cleanly: $(Get-AeroLinkStepFailure $stop)" }
        }
        if ($failed) {
            # Keep the evidence: the thrown detail names log files that would otherwise be deleted with $work.
            $kept = Join-Path $paths.Logs ('site-postgres-init-' + (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ'))
            try {
                New-Item -ItemType Directory -Path $kept -Force | Out-Null
                Get-ChildItem -LiteralPath $work -Filter '*.log' -File | Copy-Item -Destination $kept
                Write-Warning "Site PostgreSQL initialisation failed; its logs are kept in $kept"
            }
            catch { Write-Warning "Site PostgreSQL initialisation failed and its logs could not be kept in ${kept}: $($_.Exception.Message). They remain in $work." ; $work = $null }
        }
        if ($work) { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
    }
    # Bootstrap is over: the data directory is the PostgreSQL account's and Administrators' alone.
    Set-AeroLinkDirectoryAccessBySid -Path $data -Grants @(@{ Sid = $script:AdministratorsSid; Access = 'FullControl' }, @{ Sid = $site.PostgresRunAsSid; Access = 'Modify' })
    return [pscustomobject]@{
        InstallationRoot          = $site.InstallationRoot
        DataDirectory             = $data
        Port                      = $port
        ListenAddresses           = '127.0.0.1'
        Authentication            = 'scram-sha-256'
        ApplicationRole           = $site.DatabaseRole
        ApplicationRoleSuperuser  = $false
        DatabaseOwner             = $site.DatabaseRole
        PasswordlessLoginRefused  = $true
        WrongPasswordRefused      = $true
        PostgresRunAsAccount      = $site.PostgresRunAsAccount
        PostgresRunAsSid          = $site.PostgresRunAsSid
        ApplicationSecretReader   = $site.RunAsSid
        ApplicationSecretPath     = Get-AeroLinkProtectedSiteSecretPath -InstallationRoot $site.InstallationRoot -Name 'postgres-application' -RootOverride $ProtectedConfigRoot
        SuperuserSecretPath       = Get-AeroLinkProtectedSiteSecretPath -InstallationRoot $site.InstallationRoot -Name 'postgres-superuser' -RootOverride $ProtectedConfigRoot
    }
}

function Register-AeroLinkSitePostgresService {
    <#
        .SYNOPSIS Registers the site cluster as a Windows service running as its own virtual account.
        .DESCRIPTION
            The service is named exactly as the declared postgresServiceName and runs as NT SERVICE\<that name>,
            the account whose SID the data directory was granted to. A virtual account has no password.

            pg_ctl register is used first. If it does not create the service, the same service is created through
            CIM (Win32_Service.Create, StartName = the virtual account), with pg_ctl's own runservice command line.
            Either way the service SID type is set to unrestricted and the result is read back from CIM.
            Never schtasks /RU. Requires an elevated administrator; refuses an uninitialised cluster and a
            declaration whose PostgreSQL SID was overridden (an override is not a virtual account).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$InstallationRoot,
        [string]$PostgresBin,
        [switch]$Start
    )
    $site = Get-AeroLinkSiteDeclaration -InstallationRoot $InstallationRoot
    if (-not $site) { throw "$InstallationRoot has no site declaration. Only a site installation's cluster is registered as a service." }
    if (-not (Test-AeroLinkElevatedAdministrator)) { throw 'Registering a Windows service requires an elevated administrator.' }
    $serviceName = $site.PostgresServiceName
    if ($site.PostgresRunAsSid -ne (Get-AeroLinkServiceSid -ServiceName $serviceName)) {
        throw "The declared PostgreSQL SID $($site.PostgresRunAsSid) is an override, not NT SERVICE\$serviceName. Remove postgresRunAsSid from the site declaration before registering the service."
    }
    $paths = Get-AeroLinkInstallationPaths -ProductRoot $site.ProgramRoot -InstallationRoot $site.InstallationRoot
    $bin = if ($PostgresBin) { [IO.Path]::GetFullPath($PostgresBin) } else { $paths.PostgresBin }
    if (-not (Test-Path -LiteralPath (Join-Path $paths.PostgresData 'PG_VERSION') -PathType Leaf)) {
        throw "No site cluster exists at $($paths.PostgresData). Run Initialize-AeroLinkSitePostgres first."
    }
    if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) { throw "A service named $serviceName already exists; it is not replaced here." }
    $account = $site.PostgresRunAsAccount
    $logs = $paths.Logs
    New-Item -ItemType Directory -Path $logs -Force | Out-Null
    $register = Invoke-AeroLinkNativeCommand -FilePath (Join-Path $bin 'pg_ctl.exe') `
        -ArgumentList @('register', '-N', $serviceName, '-U', $account, '-D', $paths.PostgresData, '-S', 'auto', '-w', '-o', "-p $($site.PostgresPort)") `
        -StandardOutput (Join-Path $logs 'postgres-register.stdout.log') -StandardError (Join-Path $logs 'postgres-register.stderr.log') `
        -TimeoutSeconds 60 -StepName 'pg_ctl register'
    $method = 'pg_ctl register'
    if ($register.ExitCode -ne 0 -or -not (Get-Service -Name $serviceName -ErrorAction SilentlyContinue)) {
        if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) { throw "pg_ctl register failed after creating $serviceName; inspect it before retrying: $($register.Detail)" }
        $command = '"' + (Join-Path $bin 'pg_ctl.exe') + '" runservice -N "' + $serviceName + '" -D "' + $paths.PostgresData + '" -w -o "-p ' + $site.PostgresPort + '"'
        $created = Invoke-CimMethod -ClassName Win32_Service -MethodName Create -Arguments @{
            Name = $serviceName; DisplayName = $serviceName; PathName = $command; ServiceType = [byte]16; ErrorControl = [byte]1
            StartMode = 'Automatic'; DesktopInteract = $false; StartName = $account; StartPassword = $null
        }
        if ([int]$created.ReturnValue -ne 0) { throw "Neither pg_ctl register ($($register.Detail)) nor Win32_Service.Create (return $($created.ReturnValue)) created $serviceName." }
        $method = 'Win32_Service.Create'
    }
    # A virtual account needs the service SID; set it explicitly and judge only by the exit code.
    $sidType = Invoke-AeroLinkNativeCommand -FilePath (Join-Path $env:SystemRoot 'System32\sc.exe') -ArgumentList @('sidtype', $serviceName, 'unrestricted') `
        -StandardOutput (Join-Path $logs 'postgres-sidtype.stdout.log') -StandardError (Join-Path $logs 'postgres-sidtype.stderr.log') -TimeoutSeconds 30 -StepName 'service SID type'
    if ($sidType.ExitCode -ne 0) { throw "The service SID type of $serviceName could not be set: $($sidType.Detail)" }
    $service = Get-CimInstance -ClassName Win32_Service -Filter "Name='$serviceName'"
    if (-not $service -or -not [string]::Equals([string]$service.StartName, $account, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$serviceName does not run as $account (it runs as '$($service.StartName)')."
    }
    if ($Start) {
        Assert-AeroLinkPortAvailable -Port $site.PostgresPort -Purpose "this site installation's PostgreSQL"
        Start-Service -Name $serviceName
    }
    return [pscustomobject]@{ ServiceName = $serviceName; Account = $account; RunAsSid = $site.PostgresRunAsSid; Method = $method; DataDirectory = $paths.PostgresData; Port = $site.PostgresPort }
}

function Get-AeroLinkSiteRuntimeEnvironment {
    <#
        .SYNOPSIS The installation-owned settings the API process is started with on a site installation (#1614 1.5).
        .DESCRIPTION
            Every piece of state that would otherwise fall back to the run-as account's profile or the machine
            name is named explicitly: the Data Protection key ring (protected with DPAPI-LocalMachine, under the
            fixed application name AeroLink), the evidence root, the connector signing key and deployment id,
            the notification authority root and the instance id. Run as the API's account with
            -IncludeConnectionSecret to receive a connection string that carries the application password from
            the protected store; without it the connection string carries no password.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$InstallationRoot,
        [switch]$IncludeConnectionSecret,
        [string]$ProtectedConfigRoot
    )
    $site = Get-AeroLinkSiteDeclaration -InstallationRoot $InstallationRoot
    if (-not $site) { throw "$InstallationRoot has no site declaration." }
    $connection = "Host=127.0.0.1;Port=$($site.PostgresPort);Database=aerolink;Username=$($site.DatabaseRole)"
    if ($IncludeConnectionSecret) {
        $password = Get-AeroLinkProtectedSiteSecret -InstallationRoot $site.InstallationRoot -Name 'postgres-application' -RootOverride $ProtectedConfigRoot
        $connection += ";Password=$password"
    }
    $environment = [ordered]@{
        Database__Provider                  = 'PostgreSql'
        ConnectionStrings__AeroLink         = $connection
        DataProtection__KeyRingPath         = $site.KeyRingPath
        DataProtection__ApplicationName     = 'AeroLink'
        DataProtection__ProtectKeysWith     = 'DpapiLocalMachine'
        Evidence__Root                      = $site.EvidenceRoot
        Connector__SigningKeyPath           = $site.ConnectorSigningKeyPath
        Connector__DeploymentId             = $site.ConnectorDeploymentId
        AEROLINK_NOTIFICATION_AUTHORITY_ROOT = $site.NotificationAuthorityRoot
    }
    $instance = Get-Content -LiteralPath $site.ConfigPath -Raw | ConvertFrom-Json
    if ($instance.PSObject.Properties['instanceId'] -and -not [string]::IsNullOrWhiteSpace([string]$instance.instanceId)) {
        $environment['Instance__InstanceId'] = [string]$instance.instanceId
    }
    return $environment
}

function Get-AeroLinkSitePostgresAccess {
    <#
        .SYNOPSIS The login a site backup or restore uses, with its password from the protected store (#1614 1.9).
        .DESCRIPTION
            Application: the declared non-superuser role that owns aerolink. pg_dump, the attachment inventory, and
            pg_restore into a database it owns all run as this role, so a restored database's objects belong to
            it, as the cluster created them. It is the only login the scheduled backup has: the password is
            readable by the API's service account, which is what the backup task runs as.

            Superuser: postgres. Its password is readable by Administrators (or the maintenance identity named at
            initialisation). A restore logs in only as postgres and creates every object as the application role
            through pg_restore --role; an operator's backup uses it too (Get-AeroLinkSiteBackupAccess).

            The two passwords have different readers and no real process holds both (#1614 1.4), so nothing may
            ever need both.

            The password is returned as a SecureString and reaches a PostgreSQL tool only through PGPASSWORD in
            that tool's environment (Invoke-AeroLinkWithPostgresAccess), never its command line.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$InstallationRoot,
        [ValidateSet('Application', 'Superuser')][string]$Role = 'Application',
        [string]$ProtectedConfigRoot
    )
    $site = Get-AeroLinkSiteDeclaration -InstallationRoot $InstallationRoot
    if (-not $site) { throw "$InstallationRoot has no site declaration; it has no site PostgreSQL credentials." }
    $name = if ($Role -eq 'Superuser') { 'postgres-superuser' } else { 'postgres-application' }
    $secure = New-Object Security.SecureString
    $plain = Get-AeroLinkProtectedSiteSecret -InstallationRoot $site.InstallationRoot -Name $name -RootOverride $ProtectedConfigRoot
    try { foreach ($character in $plain.ToCharArray()) { $secure.AppendChar($character) } }
    finally { $plain = $null }
    $secure.MakeReadOnly()
    return [pscustomobject]@{
        Role            = $Role
        User            = $(if ($Role -eq 'Superuser') { 'postgres' } else { $site.DatabaseRole })
        ApplicationRole = $site.DatabaseRole
        Port            = $site.PostgresPort
        Password        = $secure
    }
}

function Test-AeroLinkTokenHoldsSid {
    param([Parameter(Mandatory)][string]$Sid)
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    return $principal.IsInRole((New-Object Security.Principal.SecurityIdentifier($Sid)))
}

function Get-AeroLinkSiteBackupAccess {
    <#
        .SYNOPSIS The login a site backup uses, chosen by who is running it (#1614 1.9).
        .DESCRIPTION
            The installation's API service account (the scheduled task) holds only the application password, so it
            backs up as the application role. Anyone else - an elevated administrator taking a manual, pre-restore or
            updater backup - is the maintenance identity and backs up as postgres with the superuser password. A
            process that is neither cannot read either secret and is refused, naming both routes.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$InstallationRoot, [string]$ProtectedConfigRoot)
    $site = Get-AeroLinkSiteDeclaration -InstallationRoot $InstallationRoot
    if (-not $site) { throw "$InstallationRoot has no site declaration; it has no site PostgreSQL credentials." }
    $role = if (Test-AeroLinkTokenHoldsSid $site.RunAsSid) { 'Application' } else { 'Superuser' }
    try { return Get-AeroLinkSitePostgresAccess -InstallationRoot $site.InstallationRoot -Role $role -ProtectedConfigRoot $ProtectedConfigRoot }
    catch {
        throw "A site backup runs as the installation's service account ($($site.RunAsAccount)), with the application password, or as its maintenance identity (an elevated administrator unless initialisation named another), with the superuser password. This process was taken for the $(if ($role -eq 'Application') { 'service account' } else { 'maintenance identity' }) and could not read that password: $($_.Exception.Message)"
    }
}

function Set-AeroLinkSiteEvidenceAccess {
    <#
        .SYNOPSIS Re-applies the site evidence folder's permissions after a restore moved a new folder into place.
        .DESCRIPTION
            A restore builds the incoming evidence beside the target, inside the installation root, so the new folder
            inherits the root's permissions - read-only to the API account - and a same-volume move keeps them. Left
            alone, the API could not write an attachment after a production restore. This applies the evidence
            entry of Get-AeroLinkSiteFolderPlan again (protected, inherited by every file below) and proves the API
            account can write.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$InstallationRoot, [Parameter(Mandatory)][string]$Path)
    $site = Get-AeroLinkSiteDeclaration -InstallationRoot $InstallationRoot
    if (-not $site) { throw "$InstallationRoot has no site declaration." }
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    $entry = @(Get-AeroLinkSiteFolderPlan -InstallationRoot $site.InstallationRoot | Where-Object { [string]::Equals(([IO.Path]::GetFullPath($_.Path)).TrimEnd('\'), $full, [StringComparison]::OrdinalIgnoreCase) })
    if ($entry.Count -ne 1 -or $entry[0].Kind -ne 'Data') { throw "$full is not the site's evidence folder; refusing to change its permissions." }
    Set-AeroLinkDirectoryAccessBySid -Path $full -Grants $entry[0].Grants
    $access = Get-AeroLinkDirectoryAccessBySid -Path $full
    $writable = @($access.Rules | Where-Object { $_.Sid -eq $site.RunAsSid -and $_.Allow -and $_.CanWrite })
    if (-not $access.Protected -or $writable.Count -eq 0) { throw "The restored evidence folder $full is not writable by $($site.RunAsAccount) after its permissions were re-applied." }
    return $access
}

function New-AeroLinkSiteBackupTaskDefinition {
    <#
        .SYNOPSIS The daily verified backup of a site installation, as a scheduled task definition (#1614 1.9, D7).
        .DESCRIPTION
            Runs Invoke-AeroLinkScheduledBackup.ps1 -InstallationRoot <root> as the installation's own API service
            account (NT SERVICE\AeroLink-<name>, logon type ServiceAccount: no password, no interactive user). That
            account can read the application password, read the evidence root and write backups and logs, and
            nothing else. Backups stay on the host for DEC-130's 15 days (D7: local only).

            Only builds the definition; Register-AeroLinkSiteBackupTask registers it, elevated, through the Task
            Scheduler API (never schtasks /RU, which hangs on a hidden password prompt for a service account).
            The installer (#1614 1.7) calls Register; ScriptRoot is the installed program's scripts folder.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$InstallationRoot,
        [string]$ScriptRoot = $PSScriptRoot,
        [datetime]$At = ([datetime]::Today.AddHours(2)),
        [ValidateRange(1, 15)][int]$RetentionDays = 15
    )
    $site = Get-AeroLinkSiteDeclaration -InstallationRoot $InstallationRoot
    if (-not $site) { throw "$InstallationRoot has no site declaration; a site backup task is only defined for a site installation." }
    $script = Join-Path ([IO.Path]::GetFullPath($ScriptRoot)) 'Invoke-AeroLinkScheduledBackup.ps1'
    $powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    # No trailing backslash inside the quotes: Windows PowerShell would read \" as an escaped quote and swallow the
    # following arguments into the path.
    $rootArgument = ([string]$site.InstallationRoot).TrimEnd('\')
    $arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$script`" -InstallationRoot `"$rootArgument`" -RetentionDays $RetentionDays"
    return [pscustomobject]@{
        TaskName    = "AeroLink backup ($($site.ServiceName))"
        TaskPath    = '\AeroLink\'
        Description = "Daily verified backup of the AeroLink site installation at $($site.InstallationRoot) (#1614 1.9)."
        Action      = New-ScheduledTaskAction -Execute $powershell -Argument $arguments -WorkingDirectory $site.InstallationRoot
        Trigger     = New-ScheduledTaskTrigger -Daily -At $At
        Principal   = New-ScheduledTaskPrincipal -UserId $site.RunAsAccount -LogonType ServiceAccount -RunLevel Limited
        Settings    = New-ScheduledTaskSettingsSet -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Hours 4)
    }
}

function Register-AeroLinkSiteBackupTask {
    <# .SYNOPSIS Registers (or replaces) the site backup task. Elevated; called by the installer (#1614 1.7). #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$InstallationRoot, [string]$ScriptRoot = $PSScriptRoot, [datetime]$At = ([datetime]::Today.AddHours(2)))
    $site = Get-AeroLinkSiteDeclaration -InstallationRoot $InstallationRoot
    if (-not $site) { throw "$InstallationRoot has no site declaration; a site backup task is only registered for a site installation." }
    # The task runs as NT SERVICE\<serviceName>. A runAsSid override (disposable qualification only) would make the
    # declaration name an account the task does not run as, and the application password's reader with it.
    if ($site.RunAsSid -ne (Get-AeroLinkServiceSid -ServiceName $site.ServiceName)) {
        throw "The declared API SID $($site.RunAsSid) is an override, not NT SERVICE\$($site.ServiceName). Remove runAsSid from the site declaration before registering the backup task."
    }
    $runner = Join-Path ([IO.Path]::GetFullPath($ScriptRoot)) 'Invoke-AeroLinkScheduledBackup.ps1'
    if (-not (Test-Path -LiteralPath $runner -PathType Leaf)) {
        throw "The site backup task runner does not exist at '$runner'; refusing before Task Scheduler registration."
    }
    if (-not (Test-AeroLinkElevatedAdministrator)) { throw 'Registering the site backup task requires an elevated administrator.' }
    $definition = New-AeroLinkSiteBackupTaskDefinition -InstallationRoot $InstallationRoot -ScriptRoot $ScriptRoot -At $At
    $task = New-ScheduledTask -Action $definition.Action -Trigger $definition.Trigger -Principal $definition.Principal `
        -Settings $definition.Settings -Description $definition.Description
    return Register-ScheduledTask -TaskName $definition.TaskName -TaskPath $definition.TaskPath -InputObject $task -Force
}

Export-ModuleMember -Function @(
    'Get-AeroLinkSitePostgresAccess',
    'Get-AeroLinkSiteBackupAccess',
    'Set-AeroLinkSiteEvidenceAccess',
    'New-AeroLinkSiteBackupTaskDefinition',
    'Register-AeroLinkSiteBackupTask',
    'Set-AeroLinkSiteDeclaration',
    'Set-AeroLinkDirectoryAccessBySid',
    'Get-AeroLinkDirectoryAccessBySid',
    'Get-AeroLinkSiteFolderPlan',
    'Set-AeroLinkSiteFolderAcl',
    'Initialize-AeroLinkSitePostgres',
    'Register-AeroLinkSitePostgresService',
    'Get-AeroLinkSiteRuntimeEnvironment',
    'Assert-AeroLinkUniqueSiteInstanceId'
)
