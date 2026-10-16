#Requires -Version 5.1
<#
    .SYNOPSIS
        Read-only, credential-less check of an AeroLink site server installation and its host (#1614 1.10).

    .DESCRIPTION
        Reports, for one installation (or both), each as PASS, WARN or FAIL:
          - the host is domain-joined;
          - both services exist, run as their declared virtual accounts (by SID), are running, and the API's is
            delayed-automatic and depends on the cluster's;
          - the HTTPS certificate: present in LocalMachine\My, chain valid, the public name in its subject
            alternative names, days to expiry, private key readable by the API's account;
          - the firewall rule: the API port, inbound, domain profile only; no rule opens the PostgreSQL port;
          - the API port is owned by the API service's process;
          - PostgreSQL: listens on 127.0.0.1 only, on the declared port, and pg_hba admits loopback SCRAM only;
          - the key ring folder: protected, writable by the API's account, nothing for Users;
          - the newest verified backup's age;
          - free disk space on the installation's drive;
          - the installed package: the retained signed package verifies against the installed trusted certificate,
            and every file of current\ matches its signed manifest;
          - HTTP/1.1-only is set when Windows sign-in is on (#1615);
          - the "Log on as a service" right admits NT SERVICE\ALL SERVICES or the services (error 1069).

        Reads files, CIM, the certificate store, the firewall and the TCP table. It connects to no database, reads
        no secret and changes nothing. Run elevated for the checks that read pgdata and the security policy;
        unelevated, those are reported as not checked and the final qualification is explicitly INCOMPLETE.

        Exit code: 0 when nothing failed, 1 otherwise.
#>
[CmdletBinding()]
param(
    [ValidateSet('Production', 'Development')][string[]]$Name = @('Production', 'Development'),
    [string]$MachineRoot = 'C:\AeroLink',
    [int]$MinimumFreeGigabytes = 20,
    [int]$CertificateWarningDays = 30,
    [int]$BackupWarningHours = 30
)

$ErrorActionPreference = 'Stop'
foreach ($module in 'AeroLinkInstallation', 'AeroLinkSiteInstallation', 'AeroLinkSiteServer', 'AeroLinkReleasePackage', 'AeroLinkNativeRunner') {
    Import-Module (Join-Path $PSScriptRoot "$module.psm1") -Force
}
$results = New-Object System.Collections.Generic.List[object]
$script:qualificationIncomplete = $false
function Report([string]$Scope, [string]$Check, [ValidateSet('PASS', 'WARN', 'FAIL', 'SKIP')][string]$State, [string]$Detail) {
    $script:results.Add([pscustomobject]@{ Scope = $Scope; Check = $Check; State = $State; Detail = $Detail })
    if ($State -eq 'SKIP') { $script:qualificationIncomplete = $true }
    $colour = @{ PASS = 'Green'; WARN = 'Yellow'; FAIL = 'Red'; SKIP = 'DarkGray' }[$State]
    Write-Host ("  {0,-4}  {1,-22} {2}" -f $State, $Check, $Detail) -ForegroundColor $colour
}
function Try-Check([string]$Scope, [string]$Check, [scriptblock]$Body) {
    try { & $Body } catch { Report $Scope $Check 'FAIL' $_.Exception.Message }
}
function Test-AeroLinkHostServiceLogonRight {
    <# SeDenyServiceLogonRight wins even when the allow right names the account. #>
    [CmdletBinding()]
    param(
        [AllowNull()][string[]]$AllowHolders,
        [AllowNull()][string[]]$DenyHolders,
        [Parameter(Mandatory)][string]$ServiceSid,
        [string]$ServiceAccount
    )
    $allow = Test-AeroLinkServiceLogonRight -Holders $AllowHolders -ServiceSid $ServiceSid -ServiceAccount $ServiceAccount
    $applicable = @($ServiceSid, $ServiceAccount, 'S-1-5-80-0', 'NT SERVICE\ALL SERVICES') |
        Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } |
        ForEach-Object { ([string]$_).ToUpperInvariant() }
    $denied = @($DenyHolders | Where-Object { $applicable -contains ([string]$_).ToUpperInvariant() })
    if ($denied.Count -gt 0) {
        return [pscustomobject]@{
            Allowed = $false
            Detail = "SeDenyServiceLogonRight overrides the allow right for $($denied -join ', '); the service would fail to start with error 1069."
        }
    }
    return $allow
}
$elevated = (New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

Write-Host 'Host' -ForegroundColor Cyan
Try-Check 'Host' 'Domain' {
    $computer = Get-CimInstance -ClassName Win32_ComputerSystem
    if ($computer.PartOfDomain) { Report 'Host' 'Domain' 'PASS' "joined to $($computer.Domain)" } else { Report 'Host' 'Domain' 'FAIL' 'not domain-joined: Kerberos sign-in and AD lookups need it' }
}
$userRights = $null
$script:userRights = $null
Try-Check 'Host' 'Service logon right' {
    if (-not $elevated) { Report 'Host' 'Service logon right' 'SKIP' 'run elevated to read the security policy'; return }
    $inf = Join-Path ([IO.Path]::GetTempPath()) ('aerolink-rights-' + [guid]::NewGuid().ToString('N') + '.inf')
    try {
        $run = Invoke-AeroLinkNativeCommand -FilePath (Join-Path $env:SystemRoot 'System32\secedit.exe') -ArgumentList @('/export', '/areas', 'USER_RIGHTS', '/cfg', $inf) `
            -StandardOutput "$inf.out" -StandardError "$inf.err" -TimeoutSeconds 60 -StepName 'secedit /export'
        if ($run.ExitCode -ne 0) { throw "secedit /export failed: $($run.Detail)" }
        $rightsText = [IO.File]::ReadAllText($inf, [Text.Encoding]::Unicode)
        $script:userRights = [pscustomobject]@{
            Allow = Get-AeroLinkUserRightSids -Text $rightsText -Right 'SeServiceLogonRight'
            Deny  = Get-AeroLinkUserRightSids -Text $rightsText -Right 'SeDenyServiceLogonRight'
        }
        $allowText = if ($null -eq $script:userRights.Allow) { 'default (NT SERVICE\ALL SERVICES)' } else { $script:userRights.Allow -join ', ' }
        $denyText = if ($null -eq $script:userRights.Deny) { 'none' } else { $script:userRights.Deny -join ', ' }
        Report 'Host' 'Service logon right' 'PASS' "allow: $allowText; deny: $denyText (deny precedence checked per service)"
    }
    finally { Remove-Item -LiteralPath $inf, "$inf.out", "$inf.err" -Force -ErrorAction SilentlyContinue }
}
$userRights = $script:userRights

foreach ($installationName in $Name) {
    $identity = Get-AeroLinkServerIdentity -Name $installationName -MachineRoot $MachineRoot
    $scope = $installationName
    Write-Host "AeroLink $installationName ($($identity.InstallationRoot))" -ForegroundColor Cyan
    $site = $null
    $script:site = $null
    Try-Check $scope 'Declaration' {
        $script:site = Get-AeroLinkSiteDeclaration -InstallationRoot $identity.InstallationRoot
        if (-not $script:site) { Report $scope 'Declaration' 'SKIP' 'not installed'; return }
        Report $scope 'Declaration' 'PASS' "$($script:site.Classification), API $($script:site.ApiPort), PostgreSQL $($script:site.PostgresPort), $($script:site.PublicHostName)"
    }
    $site = $script:site
    if (-not $site) { continue }
    $layout = Get-AeroLinkServerLayout -InstallationRoot $site.InstallationRoot
    $paths = Get-AeroLinkInstallationPaths -ProductRoot $site.ProgramRoot -InstallationRoot $site.InstallationRoot

    $apiService = $null
    $pgService = $null
    $script:apiService = $null
    $script:pgService = $null
    foreach ($pair in @(@($site.ServiceName, $site.RunAsSid), @($site.PostgresServiceName, $site.PostgresRunAsSid))) {
        Try-Check $scope "Service $($pair[0])" {
            $service = Get-CimInstance -ClassName Win32_Service -Filter "Name='$($pair[0])'"
            if (-not $service) { Report $scope "Service $($pair[0])" 'FAIL' 'not installed'; return }
            if ($pair[0] -eq $site.ServiceName) { $script:apiService = $service }
            if ($pair[0] -eq $site.PostgresServiceName) { $script:pgService = $service }
            $sid = (New-Object Security.Principal.NTAccount([string]$service.StartName)).Translate([Security.Principal.SecurityIdentifier]).Value
            $allowHolders = if ($userRights) { $userRights.Allow } else { $null }
            $denyHolders = if ($userRights) { $userRights.Deny } else { $null }
            $logon = Test-AeroLinkHostServiceLogonRight -AllowHolders $allowHolders -DenyHolders $denyHolders -ServiceSid $sid -ServiceAccount ([string]$service.StartName)
            if ($sid -ne $pair[1]) { Report $scope "Service $($pair[0])" 'FAIL' "runs as $($service.StartName) ($sid), declared $($pair[1])" }
            elseif (-not $logon.Allowed) { Report $scope "Service $($pair[0])" 'FAIL' $logon.Detail }
            elseif ($service.State -ne 'Running') { Report $scope "Service $($pair[0])" 'FAIL' "$($service.State) (start mode $($service.StartMode)); the installation is not ready" }
            else { Report $scope "Service $($pair[0])" 'PASS' "running as $($service.StartName)" }
        }
    }
    $apiService = $script:apiService
    $pgService = $script:pgService
    Try-Check $scope 'API service shape' {
        if (-not $apiService) { return }
        $key = Get-ItemProperty -LiteralPath "HKLM:\SYSTEM\CurrentControlSet\Services\$($site.ServiceName)"
        $depends = @($key.DependOnService)
        $delayed = $key.PSObject.Properties['DelayedAutostart'] -and [int]$key.DelayedAutostart -eq 1
        $environment = @(if ($key.PSObject.Properties['Environment']) { $key.Environment })
        $secretInEnvironment = @($environment | Where-Object { $_ -match '(?i)password|secret' })
        $requiredEnvironment = @(
            "DataProtection__KeyRingPath=$($site.KeyRingPath)"
            'DataProtection__ApplicationName=AeroLink'
            'DataProtection__ProtectKeysWith=DpapiLocalMachine')
        $missingEnvironment = @($requiredEnvironment | Where-Object { $expected = $_; -not @($environment | Where-Object { $_ -ieq $expected }) })
        if ($depends -notcontains $site.PostgresServiceName -or -not $delayed -or $apiService.StartMode -ne 'Auto') { Report $scope 'API service shape' 'FAIL' "delayed-auto=$delayed, start=$($apiService.StartMode), depends on: $($depends -join ', ')" }
        elseif ($secretInEnvironment.Count -gt 0) { Report $scope 'API service shape' 'FAIL' 'the service environment carries a secret' }
        elseif ($missingEnvironment.Count -gt 0) { Report $scope 'API service shape' 'FAIL' "missing installation-owned Data Protection settings: $($missingEnvironment -join ', ')" }
        elseif ([string]$apiService.PathName -ne ('"' + $layout.ApiExecutable + '"')) { Report $scope 'API service shape' 'FAIL' "runs $($apiService.PathName), not the current package" }
        else { Report $scope 'API service shape' 'PASS' 'delayed-automatic, depends on PostgreSQL, runs current\app, no secret in its environment' }
    }

    $settings = $null
    $script:settings = $null
    Try-Check $scope 'Certificate' {
        $script:settings = Get-Content -LiteralPath $layout.SiteServerSettings -Raw | ConvertFrom-Json
        $thumbprint = [string]$script:settings.certificate.thumbprint
        $store = New-Object Security.Cryptography.X509Certificates.X509Store('My', [Security.Cryptography.X509Certificates.StoreLocation]::LocalMachine)
        $store.Open([Security.Cryptography.X509Certificates.OpenFlags]'ReadOnly, OpenExistingOnly')
        try { $found = $store.Certificates.Find([Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint, $thumbprint, $false) } finally { $store.Close() }
        if ($found.Count -eq 0) { Report $scope 'Certificate' 'FAIL' "$thumbprint is not in LocalMachine\My"; return }
        $certificate = $found[0]
        $names = Get-AeroLinkCertificateDnsNames -Certificate $certificate
        $chain = New-Object Security.Cryptography.X509Certificates.X509Chain($true)
        $chainOk = $chain.Build($certificate)
        $days = [int]($certificate.NotAfter - (Get-Date)).TotalDays
        $keyAclProblems = New-Object System.Collections.Generic.List[string]
        try {
            $keyPath = Get-AeroLinkCertificatePrivateKeyFile -Certificate $certificate
            $keyAcl = Get-Acl -LiteralPath $keyPath
            $keyRules = @($keyAcl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]))
            $apiSid = $site.RunAsSid.ToUpperInvariant()
            $apiAllow = @($keyRules | Where-Object {
                $_.IdentityReference.Value.ToUpperInvariant() -eq $apiSid -and
                $_.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and
                (([int]$_.FileSystemRights -band [int][Security.AccessControl.FileSystemRights]::Read) -ne 0)
            })
            $apiDeny = @($keyRules | Where-Object { $_.IdentityReference.Value.ToUpperInvariant() -eq $apiSid -and $_.AccessControlType -eq [Security.AccessControl.AccessControlType]::Deny })
            $broadReaders = @($keyRules | Where-Object {
                $_.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and
                $_.IdentityReference.Value.ToUpperInvariant() -in @('S-1-1-0', 'S-1-5-11', 'S-1-5-32-545', 'S-1-5-80-0') -and
                (([int]$_.FileSystemRights -band [int][Security.AccessControl.FileSystemRights]::Read) -ne 0)
            })
            if (-not $keyAcl.AreAccessRulesProtected) { $keyAclProblems.Add('private-key ACL inherits permissions') }
            if ($apiAllow.Count -eq 0) { $keyAclProblems.Add("private-key ACL has no explicit read grant for $($site.RunAsAccount)") }
            if ($apiDeny.Count -gt 0) { $keyAclProblems.Add("private-key ACL denies $($site.RunAsAccount)") }
            if ($broadReaders.Count -gt 0) { $keyAclProblems.Add('private-key ACL grants read access to a broad principal') }
        } catch { $keyAclProblems.Add($_.Exception.Message) }
        if (-not (Test-AeroLinkHostNameCovered -HostName $site.PublicHostName -DnsNames $names)) { Report $scope 'Certificate' 'FAIL' "$thumbprint does not name $($site.PublicHostName) in its SAN ($($names -join ', '))" }
        elseif (-not $chainOk) { Report $scope 'Certificate' 'FAIL' "$thumbprint does not chain: $(@($chain.ChainStatus | ForEach-Object { $_.Status }) -join ', ')" }
        elseif ($keyAclProblems.Count -gt 0) { Report $scope 'Certificate' 'FAIL' "private-key ACL for $thumbprint is not ready: $($keyAclProblems -join '; ')" }
        elseif ($days -lt $CertificateWarningDays) { Report $scope 'Certificate' 'WARN' "$thumbprint expires in $days day(s); renew it" }
        else { Report $scope 'Certificate' 'PASS' "$thumbprint covers $($site.PublicHostName), chains, $days day(s) left, key readable by the API account" }
    }
    $settings = $script:settings
    Try-Check $scope 'HTTP/1.1 for sign-in' {
        $signIn = @(if ($apiService) { (Get-ItemProperty -LiteralPath "HKLM:\SYSTEM\CurrentControlSet\Services\$($site.ServiceName)").Environment } ) | Where-Object { $_ -match '^(?i)Identity__Windows\w*__Enabled=true$' }
        if ($signIn.Count -eq 0) { Report $scope 'HTTP/1.1 for sign-in' 'SKIP' 'Windows sign-in is not configured (#1615)' }
        elseif ($settings -and $settings.http1Only -eq $true) { Report $scope 'HTTP/1.1 for sign-in' 'PASS' 'Windows sign-in on, HTTP/1.1 only' }
        else { Report $scope 'HTTP/1.1 for sign-in' 'FAIL' 'Windows sign-in is on but http1Only is not set: Negotiate fails over HTTP/2' }
    }

    Try-Check $scope 'Firewall' {
        $rule = Get-NetFirewallRule -Name $identity.FirewallRuleName -ErrorAction SilentlyContinue
        if (-not $rule) { Report $scope 'Firewall' 'FAIL' "no rule $($identity.FirewallRuleName)"; return }
        $port = $rule | Get-NetFirewallPortFilter
        $pgOpen = @(Get-NetFirewallPortFilter -All | Where-Object { $_.LocalPort -contains [string]$site.PostgresPort } | Get-NetFirewallRule | Where-Object { $_.Enabled -eq 'True' -and $_.Direction -eq 'Inbound' -and $_.Action -eq 'Allow' })
        if ([string]$rule.Profile -ne 'Domain' -or $port.LocalPort -ne [string]$site.ApiPort -or $rule.Enabled -ne 'True') { Report $scope 'Firewall' 'FAIL' "profile $($rule.Profile), port $($port.LocalPort), enabled $($rule.Enabled)" }
        elseif ($pgOpen.Count -gt 0) { Report $scope 'Firewall' 'FAIL' "PostgreSQL port $($site.PostgresPort) is opened by: $(@($pgOpen | ForEach-Object { $_.Name }) -join ', ')" }
        else { Report $scope 'Firewall' 'PASS' "TCP $($site.ApiPort) inbound, domain profile only; PostgreSQL port closed" }
    }
    Try-Check $scope 'API port owner' {
        $listeners = @(Get-NetTCPConnection -State Listen -LocalPort $site.ApiPort -ErrorAction SilentlyContinue)
        if ($listeners.Count -eq 0) { Report $scope 'API port owner' 'FAIL' "nothing listens on $($site.ApiPort); the HTTPS API is not ready"; return }
        $owners = @($listeners | ForEach-Object { $_.OwningProcess } | Sort-Object -Unique)
        if ($apiService -and $owners.Count -eq 1 -and $owners[0] -eq $apiService.ProcessId) { Report $scope 'API port owner' 'PASS' "$($site.ApiPort) is the API service's (pid $($owners[0]))" }
        else { Report $scope 'API port owner' 'FAIL' "$($site.ApiPort) is held by pid(s) $($owners -join ', '), not the API service" }
    }

    Try-Check $scope 'PostgreSQL' {
        if (-not $elevated) { Report $scope 'PostgreSQL' 'SKIP' 'run elevated to read pgdata'; return }
        $hba = ConvertFrom-AeroLinkPgHba -Text ([IO.File]::ReadAllText((Join-Path $paths.PostgresData 'pg_hba.conf')))
        $conf = [IO.File]::ReadAllText((Join-Path $paths.PostgresData 'postgresql.conf'))
        $listen = Get-AeroLinkPostgresSetting -Text $conf -Name 'listen_addresses'
        $pgPort = Get-AeroLinkPostgresSetting -Text $conf -Name 'port'
        $listeners = @(Get-NetTCPConnection -State Listen -LocalPort $site.PostgresPort -ErrorAction SilentlyContinue)
        $nonLoopback = @($listeners | Where-Object { $_.LocalAddress -ne '127.0.0.1' })
        $loopback = @($listeners | Where-Object { $_.LocalAddress -eq '127.0.0.1' })
        $dataRoot = [IO.Path]::GetFullPath($paths.PostgresData).TrimEnd('\')
        $ownedListeners = @()
        foreach ($listener in $loopback) {
            $process = Get-CimInstance Win32_Process -Filter "ProcessId=$($listener.OwningProcess)" -ErrorAction SilentlyContinue
            if ($process -and [string]$process.CommandLine -match [regex]::Escape($dataRoot)) { $ownedListeners += $listener }
        }
        if (-not $hba.Passed) { Report $scope 'PostgreSQL' 'FAIL' "pg_hba is not loopback SCRAM only: $(@($hba.WeakRules + $hba.NonLoopbackRules | ForEach-Object { $_.Line } | Sort-Object -Unique) -join ' | ')" }
        elseif ($listen -ne '127.0.0.1' -or [string]$pgPort -ne [string]$site.PostgresPort) { Report $scope 'PostgreSQL' 'FAIL' "listen_addresses '$listen', port $pgPort; expected 127.0.0.1:$($site.PostgresPort)" }
        elseif ($loopback.Count -eq 0) { Report $scope 'PostgreSQL' 'FAIL' "nothing listens on 127.0.0.1:$($site.PostgresPort); PostgreSQL is not ready" }
        elseif ($nonLoopback.Count -gt 0) { Report $scope 'PostgreSQL' 'FAIL' "PostgreSQL port $($site.PostgresPort) also listens on $(@($nonLoopback | ForEach-Object { $_.LocalAddress }) -join ', ')" }
        elseif ($ownedListeners.Count -eq 0) { Report $scope 'PostgreSQL' 'FAIL' "127.0.0.1:$($site.PostgresPort) is not owned by a process for $dataRoot" }
        else { Report $scope 'PostgreSQL' 'PASS' "127.0.0.1:$($site.PostgresPort), scram-sha-256 only, no trust" }
    }
    Try-Check $scope 'Key ring' {
        $access = Get-AeroLinkDirectoryAccessBySid -Path $site.KeyRingPath
        $apiWrite = @($access.Rules | Where-Object { $_.Sid -eq $site.RunAsSid -and $_.Allow -and $_.CanWrite })
        $broad = @($access.Rules | Where-Object { $_.Allow -and $_.Sid -in @('S-1-5-32-545', 'S-1-5-11', 'S-1-1-0') })
        if (-not $access.Protected -or $apiWrite.Count -eq 0 -or $broad.Count -gt 0) { Report $scope 'Key ring' 'FAIL' "protected=$($access.Protected), API write=$($apiWrite.Count -gt 0), broad grants=$($broad.Count)" }
        else { Report $scope 'Key ring' 'PASS' 'protected, writable by the API account only' }
    }
    Try-Check $scope 'Backup age' {
        $archives = @(Get-ChildItem -LiteralPath $paths.Backups -Filter 'aerolink-*.zip' -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending)
        if ($archives.Count -eq 0) { Report $scope 'Backup age' 'WARN' "no backup in $($paths.Backups) yet"; return }
        $logPath = Join-Path $paths.Logs 'scheduled-backup.log'
        $verificationLines = if (Test-Path -LiteralPath $logPath -PathType Leaf) { @(Get-Content -LiteralPath $logPath -ErrorAction Stop) } else { @() }
        $verified = $null
        foreach ($archive in $archives) {
            $sidecarPath = "$($archive.FullName).sha256"
            if (-not (Test-Path -LiteralPath $sidecarPath -PathType Leaf)) { continue }
            $sidecar = (Get-Content -LiteralPath $sidecarPath -Raw).Trim()
            if ($sidecar -notmatch '^\s*(?<hash>[0-9a-fA-F]{64})\s+(?<name>\S+)\s*$') { continue }
            $sidecarHash = $Matches.hash
            $sidecarName = $Matches.name
            if ($sidecarName -ne $archive.Name) { continue }
            $actualHash = (Get-FileHash -LiteralPath $archive.FullName -Algorithm SHA256).Hash
            if (-not [string]::Equals($sidecarHash, $actualHash, [StringComparison]::OrdinalIgnoreCase)) { continue }
            $verifiedAt = $null
            foreach ($line in $verificationLines) {
                if ($line -match '^\[(?<when>[^\]]+)\]\s+Scheduled backup verified successfully:\s+(?<path>.+)$') {
                    try { $when = [DateTime]::Parse($Matches.when) } catch { continue }
                    if ([IO.Path]::GetFullPath($Matches.path) -eq $archive.FullName -and $when -ge $archive.LastWriteTime.AddMinutes(-1)) { $verifiedAt = $when; break }
                }
            }
            if ($null -ne $verifiedAt) { $verified = [pscustomobject]@{ Archive = $archive; VerifiedAt = $verifiedAt }; break }
        }
        if (-not $verified) { Report $scope 'Backup age' 'FAIL' "no backup has both a matching SHA-256 sidecar and scheduled verification evidence in $logPath"; return }
        $hours = [int]((Get-Date) - $verified.Archive.LastWriteTime).TotalHours
        Report $scope 'Backup age' $(if ($hours -le $BackupWarningHours) { 'PASS' } else { 'FAIL' }) "$($verified.Archive.Name), $hours hour(s) old; verified $($verified.VerifiedAt.ToString('o'))"
    }
    Try-Check $scope 'Disk' {
        $drive = [IO.DriveInfo]::new([IO.Path]::GetPathRoot($site.InstallationRoot))
        $free = [Math]::Round($drive.AvailableFreeSpace / 1GB, 1)
        Report $scope 'Disk' $(if ($free -ge $MinimumFreeGigabytes) { 'PASS' } else { 'WARN' }) "$free GB free on $($drive.Name)"
    }
    Try-Check $scope 'Package' {
        $current = (Get-Item -LiteralPath $layout.Current).Target
        $manifest = Get-Content -LiteralPath (Join-Path $layout.Current 'manifest.json') -Raw | ConvertFrom-Json
        $packageName = "AeroLink-$($manifest.version)+$($manifest.sourceSha).zip"
        $package = Get-Item -LiteralPath (Join-Path $layout.Packages $packageName) -ErrorAction SilentlyContinue
        if (-not $package -or $package.PSIsContainer) { Report $scope 'Package' 'FAIL' "the exact signed package $packageName is not retained in $($layout.Packages)"; return }
        $allowUnverifiedDevelopment = $installationName -eq 'Development'
        $verified = Test-AeroLinkReleasePackage -Package $package.FullName -TrustedCertificate $layout.TrustedCertificate -AllowUnverifiedDevelopment:$allowUnverifiedDevelopment
        $stream = $null; $archive = $null; $signedManifest = $null
        try {
            $archive = [IO.Compression.ZipFile]::OpenRead($package.FullName)
            $entry = $archive.GetEntry('manifest.json')
            if (-not $entry) { throw 'The retained package has no manifest.json entry.' }
            $stream = New-Object IO.StreamReader($entry.Open(), (New-Object Text.UTF8Encoding($false)))
            $signedManifest = $stream.ReadToEnd() | ConvertFrom-Json
        }
        finally {
            if ($stream) { $stream.Dispose() }
            if ($archive) { $archive.Dispose() }
        }
        $manifestProblems = New-Object System.Collections.Generic.List[string]
        foreach ($property in @('formatVersion', 'product', 'version', 'sourceSha', 'sourcePosture', 'latestMigration')) {
            if ([string]$manifest.$property -ne [string]$signedManifest.$property) { $manifestProblems.Add("manifest $property differs from the signed package") }
        }
        $signedEntries = @{}
        foreach ($file in @($signedManifest.files)) { $signedEntries[[string]$file.path] = "$(([string]$file.sha256).ToLowerInvariant())|$([long]$file.length)" }
        $installedEntries = @{}
        foreach ($file in @($manifest.files)) { $installedEntries[[string]$file.path] = "$(([string]$file.sha256).ToLowerInvariant())|$([long]$file.length)" }
        foreach ($path in @($signedEntries.Keys)) { if (-not $installedEntries.ContainsKey($path) -or $installedEntries[$path] -ne $signedEntries[$path]) { $manifestProblems.Add("installed manifest does not match signed entry $path") } }
        foreach ($path in @($installedEntries.Keys)) { if (-not $signedEntries.ContainsKey($path)) { $manifestProblems.Add("installed manifest has extra entry $path") } }
        $changed = New-Object System.Collections.Generic.List[string]
        foreach ($file in @($signedManifest.files)) {
            $installed = Join-Path $layout.Current ([string]$file.path)
            if (-not (Test-Path -LiteralPath $installed -PathType Leaf) -or (Get-FileHash -LiteralPath $installed -Algorithm SHA256).Hash.ToLowerInvariant() -ne ([string]$file.sha256).ToLowerInvariant()) { $changed.Add([string]$file.path) }
        }
        $currentRoot = (Resolve-Path -LiteralPath $layout.Current).Path.TrimEnd('\')
        $expectedFiles = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
        [void]$expectedFiles.Add('manifest.json')
        foreach ($path in @($signedEntries.Keys)) { [void]$expectedFiles.Add($path.Replace('/', '\')) }
        $actualFiles = @(Get-ChildItem -LiteralPath $currentRoot -Recurse -File -Force | ForEach-Object { $_.FullName.Substring($currentRoot.Length + 1) })
        $extraFiles = @($actualFiles | Where-Object { -not $expectedFiles.Contains($_) })
        if ($extraFiles.Count -gt 0) { $manifestProblems.Add("current has extra file(s): $(@($extraFiles | Select-Object -First 5) -join ', ')") }
        if ($manifestProblems.Count -gt 0) { Report $scope 'Package' 'FAIL' ($manifestProblems -join '; ') }
        elseif ($changed.Count -gt 0) { Report $scope 'Package' 'FAIL' "$($changed.Count) installed file(s) differ from the signed manifest: $(@($changed | Select-Object -First 5) -join ', ')" }
        else { Report $scope 'Package' 'PASS' "$($manifest.version) ($($manifest.sourceSha)) verifies; exact retained package and current\ match the signed manifest" }
    }
}

$failed = @($results | Where-Object State -eq 'FAIL').Count
$skipped = @($results | Where-Object State -eq 'SKIP').Count
$qualification = if ($failed) { 'FAILED' } elseif ($script:qualificationIncomplete) { 'INCOMPLETE - rerun elevated before treating the host as qualified' } else { 'COMPLETE' }
Write-Host ("{0} check(s): {1} failed, {2} warning(s), {3} skipped." -f $results.Count, $failed, @($results | Where-Object State -eq 'WARN').Count, $skipped) -ForegroundColor $(if ($failed) { 'Red' } elseif ($script:qualificationIncomplete) { 'Yellow' } else { 'Green' })
Write-Host "Qualification: $qualification" -ForegroundColor $(if ($failed) { 'Red' } elseif ($script:qualificationIncomplete) { 'Yellow' } else { 'Green' })
$results
exit $(if ($failed) { 1 } else { 0 })
