#Requires -Version 5.1
<#
    The site server installer, its Windows service and the host check (#1614 1.7, 1.8, 1.10).

    One installation per name on the company host (D8): C:\AeroLink\AeroLink Production and
    C:\AeroLink\AeroLink Development. Each has:

      <root>\program\versions\<version>\     a verified release package (#1635): app\, scripts\, manifest.json
      <root>\program\current                a junction to the running version
      <root>\program\packages\               the signed packages as installed, kept for the host check
      <root>\trust\release-signing.cer      the release certificate the installer trusts; never the package's copy
      <root>\instance.json, site-server.json the declaration (#1636) and the HTTPS endpoint (#1642)

    and two Windows services, each running as its own virtual account (D2, revised):

      AeroLink-<Name>-PostgreSQL            the cluster (#1636 Register-AeroLinkSitePostgresService)
      AeroLink-<Name>                       the API: current\app\AeroLink.Api.exe, a Windows service through
                                            UseWindowsService, delayed-automatic, depending on the cluster,
                                            restarted by the Service Control Manager on failure

    The functions here split into pure plans (tested non-elevated: root locks, layout, the service definition, the
    host-check parsers) and the elevated steps that apply them. Nothing here parses localised schtasks, netsh, sc
    or icacls output: services are created and read through CIM, permissions by SID, sc.exe is judged only by its
    exit code, and the one policy read (secedit /export) is the documented INF format, not display text.
#>

Set-StrictMode -Version Latest

Import-Module (Join-Path $PSScriptRoot 'AeroLinkInstallation.psm1')
Import-Module (Join-Path $PSScriptRoot 'AeroLinkNativeRunner.psm1')

$script:AdministratorsSid = 'S-1-5-32-544'
$script:SystemSid = 'S-1-5-18'
$script:UsersSid = 'S-1-5-32-545'
$script:AllServicesSid = 'S-1-5-80-0'          # NT SERVICE\ALL SERVICES

function Get-AeroLinkServerIdentity {
    <#
        .SYNOPSIS The fixed names of one site installation (D8): root, classification, label and service names.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][ValidateSet('Production', 'Development')][string]$Name,
        [string]$MachineRoot = 'C:\AeroLink'
    )
    return [pscustomobject]@{
        Name                = $Name
        InstallationRoot    = Join-Path ([IO.Path]::GetFullPath($MachineRoot)) "AeroLink $Name"
        Classification      = "Site$Name"
        Label               = "SITE $($Name.ToUpperInvariant())"
        ServiceName         = "AeroLink-$Name"
        PostgresServiceName = "AeroLink-$Name-PostgreSQL"
        FirewallRuleName    = "AeroLink-$Name-HTTPS"
    }
}

function Get-AeroLinkServerRootPlan {
    <#
        .SYNOPSIS The two machine roots the installer creates and locks first: owner Administrators, no write for Users.
        .DESCRIPTION
            C:\AeroLink and %ProgramData%\AeroLink are created before anything inside them. Both C:\ and ProgramData
            let an ordinary user create a folder, and an owner keeps WRITE_DAC whatever the DACL says, so a folder an
            ordinary user pre-created would let them re-grant themselves everything below it. Each root is therefore
            owned by Administrators, readable by Users, and writable only by Administrators and SYSTEM;
            Set-AeroLinkDirectoryAccessBySid refuses a root somebody else already owns.

            The folders below keep their own protected permissions (#1636 folder plan, the protected-config store,
            the notification authority), so these grants do not reach them.
    #>
    [CmdletBinding()]
    param([string]$MachineRoot = 'C:\AeroLink', [string]$ProgramDataRoot)
    if (-not $ProgramDataRoot) { $ProgramDataRoot = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)) 'AeroLink' }
    $grants = @(
        @{ Sid = $script:AdministratorsSid; Access = 'FullControl' },
        @{ Sid = $script:SystemSid; Access = 'FullControl' },
        @{ Sid = $script:UsersSid; Access = 'Read' })
    return @(
        [pscustomobject]@{ Path = [IO.Path]::GetFullPath($MachineRoot); Grants = $grants },
        [pscustomobject]@{ Path = [IO.Path]::GetFullPath($ProgramDataRoot); Grants = $grants })
}

function Get-AeroLinkServerLayout {
    <#
        .SYNOPSIS Where one installation keeps its program versions, the current junction, packages and trust.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$InstallationRoot, [string]$Version, [string]$SourceSha)
    $root = [IO.Path]::GetFullPath($InstallationRoot).TrimEnd('\')
    $program = Join-Path $root 'program'
    $current = Join-Path $program 'current'
    $versionDirectory = $null
    if ($Version) {
        if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z][0-9A-Za-z.-]*)?$') {
            throw "Release version '$Version' is not a safe semantic version directory name."
        }
        if ($SourceSha) {
            if ($SourceSha -notmatch '^[0-9a-fA-F]{40}$') {
                throw "Release source SHA '$SourceSha' is not a full 40-character hexadecimal identity."
            }
            $versionDirectory = "$Version+$($SourceSha.ToLowerInvariant())"
        } else { $versionDirectory = $Version }
    } elseif ($SourceSha) {
        throw 'A release source SHA cannot be used without its semantic version.'
    }
    return [pscustomobject]@{
        InstallationRoot   = $root
        ProgramRoot        = $program
        Versions           = Join-Path $program 'versions'
        VersionRoot        = $(if ($versionDirectory) { Join-Path (Join-Path $program 'versions') $versionDirectory } else { $null })
        VersionDirectory   = $versionDirectory
        SourceSha          = $(if ($SourceSha) { $SourceSha.ToLowerInvariant() } else { $null })
        Current            = $current
        Packages           = Join-Path $program 'packages'
        Trust              = Join-Path $root 'trust'
        TrustedCertificate = Join-Path (Join-Path $root 'trust') 'release-signing.cer'
        ApiExecutable      = Join-Path $current 'app\AeroLink.Api.exe'
        Scripts            = Join-Path $current 'scripts'
        SiteServerSettings = Join-Path $root 'site-server.json'
    }
}

function New-AeroLinkApiServiceDefinition {
    <#
        .SYNOPSIS The API's Windows service, as data: what Install-AeroLinkApiService creates and Test-AeroLinkHost checks.
        .DESCRIPTION
            The service runs the API executable itself, which reports to the Service Control Manager through
            UseWindowsService. PowerShell cannot be the service binary: it never answers the SCM, which ends it
            after 30 seconds (error 1053). So the site launcher's composed settings (Start-AeroLinkSite.ps1
            -ValidateOnly, #1642) are written into the service's own environment once, at install, and the one
            secret the API needs - its database password - is never among them: the API reads it at start-up from
            the protected-config store (SiteServer:InstallationRoot), as the service account that alone can.

            Refuses an environment that carries a password or a secret: the service key is readable far more widely
            than the protected store.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Site,
        [Parameter(Mandatory)][string]$CurrentProgram,
        [Parameter(Mandatory)][Collections.IDictionary]$Environment,
        [string]$DisplayName
    )
    $entries = New-Object System.Collections.Generic.List[string]
    foreach ($key in @($Environment.Keys | Sort-Object)) {
        $value = $Environment[$key]
        if ($null -eq $value -or [string]::IsNullOrEmpty([string]$value)) { continue }
        if ([string]$key -match '(?i)password|secret|token' -or [string]$value -match '(?i)(^|;)\s*password\s*=') {
            throw "The service environment may not carry a secret ($key). The API reads its secrets from the protected store."
        }
        if ([string]$key -match '[=\x00]' -or [string]$value -match '\x00') { throw "The service environment entry $key cannot be written to the registry." }
        $entries.Add("$key=$value")
    }
    $executable = Join-Path ([IO.Path]::GetFullPath($CurrentProgram)) 'app\AeroLink.Api.exe'
    return [pscustomobject]@{
        Name                   = $Site.ServiceName
        DisplayName            = $(if ($DisplayName) { $DisplayName } else { "AeroLink $($Site.Classification -replace '^Site', '')" })
        PathName               = '"' + $executable + '"'
        Executable             = $executable
        StartName              = $Site.RunAsAccount
        StartNameSid           = $Site.RunAsSid
        StartMode              = 'Automatic'
        DelayedAutoStart       = $true
        Dependencies           = @($Site.PostgresServiceName)
        # Restart after a minute, three times; the count resets after a day without a failure.
        FailureResetSeconds    = 86400
        FailureActions         = @('restart/60000', 'restart/60000', 'restart/60000')
        Environment            = $entries.ToArray()
    }
}

function Test-AeroLinkServiceSidMatchesAccount {
    param([Parameter(Mandatory)]$Site)
    return ($Site.RunAsSid -eq (Get-AeroLinkServiceSid -ServiceName $Site.ServiceName))
}

function Install-AeroLinkApiService {
    <#
        .SYNOPSIS Creates or updates the API's Windows service from a definition. Elevated.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Definition, [Parameter(Mandatory)][string]$LogDirectory)
    $name = $Definition.Name
    $existing = Get-CimInstance -ClassName Win32_Service -Filter "Name='$name'"
    if (-not $existing) {
        $created = Invoke-CimMethod -ClassName Win32_Service -MethodName Create -Arguments @{
            Name = $name; DisplayName = $Definition.DisplayName; PathName = $Definition.PathName; ServiceType = [byte]16
            ErrorControl = [byte]1; StartMode = 'Automatic'; DesktopInteract = $false; StartName = $Definition.StartName
            StartPassword = $null; ServiceDependencies = [string[]]$Definition.Dependencies
        }
        if ([int]$created.ReturnValue -ne 0) { throw "Win32_Service.Create did not create $name (return $($created.ReturnValue))." }
    }
    else {
        $changed = Invoke-CimMethod -InputObject $existing -MethodName Change -Arguments @{
            DisplayName = $Definition.DisplayName; PathName = $Definition.PathName; StartMode = 'Automatic'
            StartName = $Definition.StartName; StartPassword = $null; ServiceDependencies = [string[]]$Definition.Dependencies
        }
        if ([int]$changed.ReturnValue -ne 0) { throw "Win32_Service.Change did not update $name (return $($changed.ReturnValue))." }
    }
    $key = "HKLM:\SYSTEM\CurrentControlSet\Services\$name"
    Set-ItemProperty -LiteralPath $key -Name 'DelayedAutostart' -Value 1 -Type DWord
    New-ItemProperty -LiteralPath $key -Name 'Environment' -Value ([string[]]$Definition.Environment) -PropertyType MultiString -Force | Out-Null
    $sc = Join-Path $env:SystemRoot 'System32\sc.exe'
    New-Item -ItemType Directory -Path $LogDirectory -Force | Out-Null
    foreach ($step in @(
        @{ Name = 'service SID type'; Arguments = @('sidtype', $name, 'unrestricted') },
        @{ Name = 'failure actions'; Arguments = @('failure', $name, "reset=", "$($Definition.FailureResetSeconds)", 'actions=', ($Definition.FailureActions -join '/')) },
        @{ Name = 'failure actions on non-crash exit'; Arguments = @('failureflag', $name, '1') })) {
        $run = Invoke-AeroLinkNativeCommand -FilePath $sc -ArgumentList $step.Arguments -StandardOutput (Join-Path $LogDirectory "api-service-$($step.Name -replace '\W+','-').stdout.log") `
            -StandardError (Join-Path $LogDirectory "api-service-$($step.Name -replace '\W+','-').stderr.log") -TimeoutSeconds 30 -StepName "sc $($step.Name)"
        if ($run.ExitCode -ne 0) { throw "The $($step.Name) of $name could not be set: $($run.Detail)" }
    }
    $service = Get-CimInstance -ClassName Win32_Service -Filter "Name='$name'"
    if (-not $service -or -not [string]::Equals([string]$service.StartName, $Definition.StartName, [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals([string]$service.PathName, $Definition.PathName, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$name is not configured as defined (runs as '$($service.StartName)', binary '$($service.PathName)')."
    }
    return $service
}

function Get-AeroLinkCertificatePrivateKeyFile {
    <#
        .SYNOPSIS The file holding a machine certificate's private key, so its ACL can name the API's account.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][Security.Cryptography.X509Certificates.X509Certificate2]$Certificate)
    $machineKeys = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)) 'Microsoft\Crypto'
    $rsa = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($Certificate)
    $key = if ($rsa) { $rsa } else { [Security.Cryptography.X509Certificates.ECDsaCertificateExtensions]::GetECDsaPrivateKey($Certificate) }
    if (-not $key) { throw "Certificate $($Certificate.Thumbprint) has no RSA or ECDSA private key this process can open." }
    $unique = $null
    if ($key -is [Security.Cryptography.RSACng] -or $key -is [Security.Cryptography.ECDsaCng]) { $unique = $key.Key.UniqueName; $folders = @('Keys', 'RSA\MachineKeys') }
    elseif ($key -is [Security.Cryptography.RSACryptoServiceProvider]) { $unique = $key.CspKeyContainerInfo.UniqueKeyContainerName; $folders = @('RSA\MachineKeys') }
    if (-not $unique) { throw "The private key of certificate $($Certificate.Thumbprint) is not in a machine key file this installer recognises." }
    foreach ($folder in $folders) {
        $candidate = Join-Path (Join-Path $machineKeys $folder) $unique
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
    }
    throw "The private key file of certificate $($Certificate.Thumbprint) ($unique) was not found under $machineKeys."
}

function Grant-AeroLinkCertificateKeyRead {
    <#
        .SYNOPSIS Lets the API's virtual account read the HTTPS certificate's private key. Elevated.
        .DESCRIPTION
            A CA-issued machine certificate's key is readable by SYSTEM and Administrators only; without this grant
            the API starts, logs that it is listening, and fails every TLS handshake. (The API refuses that at
            start-up too, #1642, so a missed grant is a refusal, not a silent outage.)
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Thumbprint, [Parameter(Mandatory)][string]$Sid, [string]$StoreName = 'My')
    $store = New-Object Security.Cryptography.X509Certificates.X509Store($StoreName, [Security.Cryptography.X509Certificates.StoreLocation]::LocalMachine)
    $store.Open([Security.Cryptography.X509Certificates.OpenFlags]'ReadOnly, OpenExistingOnly')
    try {
        $found = $store.Certificates.Find([Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint, ($Thumbprint -replace '\s', ''), $false)
        if ($found.Count -eq 0) { throw "No certificate with thumbprint $Thumbprint is in LocalMachine\$StoreName." }
        $file = Get-AeroLinkCertificatePrivateKeyFile -Certificate $found[0]
    }
    finally { $store.Close() }
    return Set-AeroLinkCertificatePrivateKeyAcl -Path $file -Sid $Sid
}

function Set-AeroLinkCertificatePrivateKeyAcl {
    <#
        .SYNOPSIS Replaces a machine certificate private-key file DACL with the installation boundary.
        .DESCRIPTION
            Certificate enrollment can leave a key readable by Users or another service. Adding the API SID to
            that DACL is not sufficient: the old readers would remain. This helper removes inherited and explicit
            access, grants only the API read access and SYSTEM/Administrators full control, and reads the DACL back
            before returning. It accepts a path so the boundary can be qualified against a disposable file without
            mutating the machine certificate store; the installer reaches it through Grant-AeroLinkCertificateKeyRead.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Sid)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "The certificate private-key file does not exist: $Path" }
    try { $apiSid = [Security.Principal.SecurityIdentifier]::new($Sid).Value }
    catch { throw "The certificate private-key reader must be a SID; it was '$Sid'." }

    $security = Get-Acl -LiteralPath $Path
    $security.SetAccessRuleProtection($true, $false)
    foreach ($existing in @($security.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]))) {
        [void]$security.RemoveAccessRuleSpecific($existing)
    }
    foreach ($grant in @(
        @{ Sid = $apiSid; Access = [Security.AccessControl.FileSystemRights]::Read }
        @{ Sid = $script:SystemSid; Access = [Security.AccessControl.FileSystemRights]::FullControl }
        @{ Sid = $script:AdministratorsSid; Access = [Security.AccessControl.FileSystemRights]::FullControl }
    )) {
        $rule = [Security.AccessControl.FileSystemAccessRule]::new(
            [Security.Principal.SecurityIdentifier]::new($grant.Sid), $grant.Access,
            [Security.AccessControl.InheritanceFlags]::None, [Security.AccessControl.PropagationFlags]::None,
            [Security.AccessControl.AccessControlType]::Allow)
        [void]$security.AddAccessRule($rule)
    }
    # Persist the DACL section only. Reusing Set-Acl with a descriptor that carries an audit section asks for
    # SeSecurityPrivilege on some machine-key files, although this operation needs only WRITE_DAC.
    $access = [Security.AccessControl.AccessControlSections]::Access
    $dacl = [Security.AccessControl.FileSecurity]::new()
    $dacl.SetSecurityDescriptorSddlForm($security.GetSecurityDescriptorSddlForm($access), $access)
    if ($PSVersionTable.PSEdition -eq 'Desktop') { [IO.File]::SetAccessControl($Path, $dacl) }
    else { [IO.FileSystemAclExtensions]::SetAccessControl([IO.FileInfo]::new($Path), $dacl) }

    $observed = Get-Acl -LiteralPath $Path
    $actual = @($observed.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]) | ForEach-Object {
        '{0}|{1}|{2}|{3}|{4}|{5}' -f $_.IdentityReference.Value, [int]$_.FileSystemRights,
            [int]$_.AccessControlType, [int]$_.InheritanceFlags, [int]$_.PropagationFlags, $_.IsInherited
    } | Sort-Object) -join ';'
    $expected = @(
        "${apiSid}|$([int]([Security.AccessControl.FileSystemRights]::Read -bor [Security.AccessControl.FileSystemRights]::Synchronize))|$([int][Security.AccessControl.AccessControlType]::Allow)|0|0|False"
        "$($script:SystemSid)|$([int][Security.AccessControl.FileSystemRights]::FullControl)|$([int][Security.AccessControl.AccessControlType]::Allow)|0|0|False"
        "$($script:AdministratorsSid)|$([int][Security.AccessControl.FileSystemRights]::FullControl)|$([int][Security.AccessControl.AccessControlType]::Allow)|0|0|False"
    ) | Sort-Object
    $expected = $expected -join ';'
    if (-not $observed.AreAccessRulesProtected -or $actual -ne $expected) {
        throw "Certificate private-key ACL readback failed for $Path. Expected '$expected', observed '$actual'."
    }
    return $Path
}

function Set-AeroLinkApiFirewallRule {
    <#
        .SYNOPSIS One inbound rule for the API's HTTPS port, domain profile only. PostgreSQL ports are never opened.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$RuleName, [Parameter(Mandatory)][ValidateRange(1, 65535)][int]$Port, [Parameter(Mandatory)][string]$DisplayName)
    Get-NetFirewallRule -Name $RuleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    return New-NetFirewallRule -Name $RuleName -DisplayName $DisplayName -Direction Inbound -Protocol TCP -LocalPort $Port `
        -Profile Domain -Action Allow -Enabled True
}

# --- Host check parsers (pure) ---------------------------------------------------------------------------------

function ConvertFrom-AeroLinkPgHba {
    <#
        .SYNOPSIS The effective rules of a pg_hba.conf and what a site requires of them.
        .DESCRIPTION
            A site cluster admits loopback SCRAM only (#1614 1.4). Reported: every rule, any trust/password/md5/ident
            rule, any rule not bound to 127.0.0.1/32, and whether the file passes.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Text)
    $rules = New-Object System.Collections.Generic.List[object]
    foreach ($raw in ($Text -split "`r?`n")) {
        $line = ($raw -replace '#.*$', '').Trim()
        if (-not $line) { continue }
        $fields = @($line -split '\s+')
        $type = $fields[0]
        if ($type -eq 'local') { $address = $null; $method = $fields[3] }
        elseif ($fields.Count -ge 5 -and $fields[4] -match '^\d+\.\d+\.\d+\.\d+$') {
            # Address and mask as two fields: 127.0.0.1 255.255.255.255 is 127.0.0.1/32.
            $address = $(if ($fields[4] -eq '255.255.255.255') { "$($fields[3])/32" } else { "$($fields[3]) $($fields[4])" })
            $method = $(if ($fields.Count -ge 6) { $fields[5] } else { $null })
        }
        elseif ($fields.Count -ge 5) { $address = $fields[3]; $method = $fields[4] }
        else { $address = $null; $method = $null }
        $rules.Add([pscustomobject]@{ Type = $type; Database = $fields[1]; User = $fields[2]; Address = $address; Method = $method; Line = $line })
    }
    $weak = @($rules | Where-Object { $_.Method -notin @('scram-sha-256', 'reject') })
    $notLoopback = @($rules | Where-Object { $_.Method -ne 'reject' -and ($_.Type -eq 'local' -or $_.Address -notin @('127.0.0.1/32')) })
    return [pscustomobject]@{ Rules = $rules.ToArray(); WeakRules = $weak; NonLoopbackRules = $notLoopback; Passed = ($rules.Count -gt 0 -and $weak.Count -eq 0 -and $notLoopback.Count -eq 0) }
}

function Get-AeroLinkPostgresSetting {
    <#
        .SYNOPSIS The effective value of one setting in a postgresql.conf (the last uncommented assignment wins).
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Text, [Parameter(Mandatory)][string]$Name)
    $value = $null
    foreach ($raw in ($Text -split "`r?`n")) {
        $line = $raw.Trim()
        $escaped = [regex]::Escape($Name)
        if ($line -match "^(?i)$escaped\s*=?\s*'([^']*)'") { $value = $Matches[1] }
        elseif ($line -match "^(?i)$escaped\s*=?\s*([^\s#']+)") { $value = $Matches[1] }
    }
    return $value
}

function Get-AeroLinkUserRightSids {
    <#
        .SYNOPSIS The accounts holding one user right in a secedit /export INF (e.g. SeServiceLogonRight).
        .DESCRIPTION
            Returns $null when the right is not listed, which means no policy defines it and Windows' default
            applies (NT SERVICE\ALL SERVICES may log on as a service). Entries are SIDs (*S-1-...) or account names.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Text, [Parameter(Mandatory)][string]$Right)
    foreach ($raw in ($Text -split "`r?`n")) {
        if ($raw -match "^\s*$([regex]::Escape($Right))\s*=\s*(.*)$") {
            return @($Matches[1].Split(',') | ForEach-Object { $_.Trim().TrimStart('*') } | Where-Object { $_ })
        }
    }
    return $null
}

function Test-AeroLinkServiceLogonRight {
    <#
        .SYNOPSIS Whether a virtual service account may log on as a service under the effective policy (error 1069 risk).
    #>
    [CmdletBinding()]
    param([AllowNull()][string[]]$Holders, [Parameter(Mandatory)][string]$ServiceSid, [string]$ServiceAccount)
    if ($null -eq $Holders) { return [pscustomobject]@{ Allowed = $true; Detail = 'No policy defines SeServiceLogonRight; the Windows default lets NT SERVICE\ALL SERVICES log on as a service.' } }
    $names = @($Holders | ForEach-Object { $_.ToUpperInvariant() })
    $allowed = $names -contains $script:AllServicesSid.ToUpperInvariant() -or $names -contains $ServiceSid.ToUpperInvariant() -or
        $names -contains 'NT SERVICE\ALL SERVICES' -or ($ServiceAccount -and $names -contains $ServiceAccount.ToUpperInvariant())
    $detail = if ($allowed) { 'SeServiceLogonRight includes the service account or NT SERVICE\ALL SERVICES.' } else { "SeServiceLogonRight is set by policy and names neither NT SERVICE\ALL SERVICES nor $ServiceAccount; the service would fail to start with error 1069. Holders: $($Holders -join ', ')" }
    return [pscustomobject]@{ Allowed = $allowed; Detail = $detail }
}

function Get-AeroLinkCertificateDnsNames {
    <#
        .SYNOPSIS The DNS names in a certificate's subject alternative name extension (never the CN).
        .DESCRIPTION
            Read from the extension's DER bytes, so the result does not depend on the display language the way
            X509Extension.Format does.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][Security.Cryptography.X509Certificates.X509Certificate2]$Certificate)
    $extension = $Certificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.17' } | Select-Object -First 1
    if (-not $extension) { return @() }
    return @(ConvertFrom-AeroLinkSanDer -Bytes $extension.RawData)
}

function ConvertFrom-AeroLinkSanDer {
    # GeneralNames ::= SEQUENCE OF GeneralName; dNSName is [2] IA5String (context tag 0x82).
    param([Parameter(Mandatory)][byte[]]$Bytes)
    function Read-Length([byte[]]$B, [ref]$Index) {
        $first = $B[$Index.Value]; $Index.Value++
        if ($first -lt 0x80) { return [int]$first }
        $count = $first -band 0x7F; $length = 0
        for ($i = 0; $i -lt $count; $i++) { $length = ($length -shl 8) -bor $B[$Index.Value]; $Index.Value++ }
        return $length
    }
    $index = 0
    if ($Bytes[$index] -ne 0x30) { throw 'The subject alternative name extension is not a DER sequence.' }
    $index++
    $end = (Read-Length $Bytes ([ref]$index)) + $index
    $names = New-Object System.Collections.Generic.List[string]
    while ($index -lt $end) {
        $tag = $Bytes[$index]; $index++
        $length = Read-Length $Bytes ([ref]$index)
        if ($tag -eq 0x82) { $names.Add([Text.Encoding]::ASCII.GetString($Bytes, $index, $length)) }
        $index += $length
    }
    return $names.ToArray()
}

function Test-AeroLinkHostNameCovered {
    <# Whether a host name is covered by a SAN DNS name list (one left-most wildcard label allowed). #>
    param([Parameter(Mandatory)][string]$HostName, [AllowEmptyCollection()][string[]]$DnsNames)
    $target = $HostName.TrimEnd('.').ToLowerInvariant()
    foreach ($name in $DnsNames) {
        $candidate = $name.TrimEnd('.').ToLowerInvariant()
        if ($candidate -eq $target) { return $true }
        if ($candidate.StartsWith('*.') -and $target.Contains('.') -and $target.Substring($target.IndexOf('.')) -eq $candidate.Substring(1)) { return $true }
    }
    return $false
}

Export-ModuleMember -Function @(
    'Get-AeroLinkServerIdentity',
    'Get-AeroLinkServerRootPlan',
    'Get-AeroLinkServerLayout',
    'New-AeroLinkApiServiceDefinition',
    'Test-AeroLinkServiceSidMatchesAccount',
    'Install-AeroLinkApiService',
    'Get-AeroLinkCertificatePrivateKeyFile',
    'Grant-AeroLinkCertificateKeyRead',
    'Set-AeroLinkCertificatePrivateKeyAcl',
    'Set-AeroLinkApiFirewallRule',
    'ConvertFrom-AeroLinkPgHba',
    'Get-AeroLinkPostgresSetting',
    'Get-AeroLinkUserRightSids',
    'Test-AeroLinkServiceLogonRight',
    'Get-AeroLinkCertificateDnsNames',
    'ConvertFrom-AeroLinkSanDer',
    'Test-AeroLinkHostNameCovered'
)
