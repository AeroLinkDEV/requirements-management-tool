#Requires -Version 5.1
<#
    .SYNOPSIS
        Installs or repairs one AeroLink site server installation on the company host (#1614 1.7, 1.8). Elevated.

    .DESCRIPTION
        Idempotent: every step checks what is already there and changes only what is missing. An existing installation
        must already point at the exact signed release and byte-identical tree offered here; a different release is
        refused. Clone-validated upgrades and rollback belong to the A3 updater.

        In order:
          1. lock C:\AeroLink and %ProgramData%\AeroLink: owner Administrators, no write for Users;
          2. declare the installation (SiteProduction or SiteDevelopment, D8) and its ports, public host name and
             virtual accounts (Set-AeroLinkSiteDeclaration, #1636);
          3. verify the package against the offered release certificate before changing <root>\trust, refusing to
             replace a different installed certificate silently; after a deliberate rotation, reverify against the
             installed certificate; extract it into program\versions\<version> and point program\current at it;
             keep the signed package under program\packages for the host check;
          4. write the installation pointer the program resolves its installation from (<program>\.local);
          5. set every folder's owner and permissions by SID (#1636 folder plan);
          6. copy the PostgreSQL binaries (the host is offline: a folder or zip is supplied) and initialise and
             register the cluster as its own service (#1636), once;
          7. write site-server.json: bind address, HTTP/1.1-only, the certificate (#1642);
          8. obtain the HTTPS certificate (a thumbprint already in LocalMachine\My, or an enrolment request to the
             enterprise CA) and let the API's account read its private key;
          9. generate the first-administrator bootstrap secret once, store it for the API's account in the
             protected store, and show it once;
         10. create or update the API's Windows service from the site launcher's composed settings (#1642);
         11. open the API port, domain profile only; PostgreSQL ports are never opened;
         12. register the daily verified backup task (#1649);
         13. start PostgreSQL and then the API, and run the host check.

        Windows sign-in (#1615, D10) is not configured here; production waits for it.

    .PARAMETER Name
        Production or Development (D8). The root is C:\AeroLink\AeroLink <Name>.

    .PARAMETER Package
        The signed release package (AeroLink-<version>+<sha>.zip, with its .sig beside it).

    .PARAMETER TrustedCertificate
        The release-signing certificate (.cer) this host trusts. Installed into <root>\trust on first install.

    .PARAMETER PostgresBinaries
        A PostgreSQL 18 'pgsql' folder (bin, lib, share) or a zip of one. Needed only until the cluster exists.

    .PARAMETER CertificateThumbprint
        The HTTPS certificate, already in LocalMachine\My. Or -RequestCertificateTemplate to enrol one.

    .PARAMETER RemoveBootstrapSecret
        After the first administrator exists: remove the bootstrap secret, restart the API, and change nothing else.
#>
[CmdletBinding(DefaultParameterSetName = 'Install')]
param(
    [Parameter(Mandatory)][ValidateSet('Production', 'Development')][string]$Name,
    [Parameter(ParameterSetName = 'Install', Mandatory)][ValidateRange(1024, 65535)][int]$ApiPort,
    [Parameter(ParameterSetName = 'Install', Mandatory)][ValidateRange(1024, 65535)][int]$PostgresPort,
    [Parameter(ParameterSetName = 'Install', Mandatory)][string]$PublicHostName,
    [Parameter(ParameterSetName = 'Install', Mandatory)][string]$Package,
    [Parameter(ParameterSetName = 'Install', Mandatory)][string]$TrustedCertificate,
    [Parameter(ParameterSetName = 'Install')][string]$PostgresBinaries,
    [Parameter(ParameterSetName = 'Install')][string]$CertificateThumbprint,
    [Parameter(ParameterSetName = 'Install')][string]$RequestCertificateTemplate,
    [Parameter(ParameterSetName = 'Install')][string]$BindAddress = '0.0.0.0',
    [Parameter(ParameterSetName = 'Install')][switch]$Http1Only,
    [Parameter(ParameterSetName = 'Install')][switch]$ReplaceTrustedCertificate,
    [Parameter(ParameterSetName = 'Install')][switch]$NoStart,
    # A -SkipPostureCheck development package (#1635). Accepted only for the Development installation.
    [Parameter(ParameterSetName = 'Install')][switch]$AllowUnverifiedDevelopment,
    [Parameter(ParameterSetName = 'RemoveBootstrap', Mandatory)][switch]$RemoveBootstrapSecret,
    [string]$MachineRoot = 'C:\AeroLink'
)

$ErrorActionPreference = 'Stop'
foreach ($module in 'AeroLinkInstallation', 'AeroLinkSiteInstallation', 'AeroLinkProtectedConfig', 'AeroLinkSiteServer', 'AeroLinkReleasePackage', 'AeroLinkNativeRunner') {
    Import-Module (Join-Path $PSScriptRoot "$module.psm1") -Force
}
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Install-AeroLinkServer.ps1 must run elevated, as an administrator.' }
$identity = Get-AeroLinkServerIdentity -Name $Name -MachineRoot $MachineRoot
$root = $identity.InstallationRoot
function Step([string]$Text) { Write-Host "==> $Text" -ForegroundColor Cyan }

function Get-AeroLinkReleaseTreeFingerprint {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Root)
    $fullRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    $fingerprint = @{}
    foreach ($file in @(Get-ChildItem -LiteralPath $fullRoot -File -Recurse -Force)) {
        $relative = $file.FullName.Substring($fullRoot.Length).Replace('\', '/')
        $fingerprint[$relative] = "$($file.Length)|$((Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant())"
    }
    return $fingerprint
}

function Compare-AeroLinkRetainedRelease {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ExistingRoot,
        [Parameter(Mandatory)][string]$IncomingRoot,
        [Parameter(Mandatory)]$Verified
    )
    try {
        $manifestPath = Join-Path $ExistingRoot 'manifest.json'
        if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { return [pscustomobject]@{ Matches = $false; Reason = 'the retained release has no manifest.json' } }
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        if ([string]$manifest.version -cne [string]$Verified.Version -or [string]$manifest.sourceSha -cne [string]$Verified.SourceSha) {
            return [pscustomobject]@{ Matches = $false; Reason = "the retained manifest identity is $($manifest.version)/$($manifest.sourceSha), offered is $($Verified.Version)/$($Verified.SourceSha)" }
        }
        $existing = Get-AeroLinkReleaseTreeFingerprint -Root $ExistingRoot
        $incoming = Get-AeroLinkReleaseTreeFingerprint -Root $IncomingRoot
        foreach ($path in @($incoming.Keys)) {
            if (-not $existing.ContainsKey($path) -or $existing[$path] -cne $incoming[$path]) {
                return [pscustomobject]@{ Matches = $false; Reason = "retained file '$path' differs from the verified offered release" }
            }
        }
        foreach ($path in @($existing.Keys)) {
            if (-not $incoming.ContainsKey($path)) {
                return [pscustomobject]@{ Matches = $false; Reason = "retained release has extra file '$path'" }
            }
        }
        return [pscustomobject]@{ Matches = $true; Reason = $null }
    }
    catch { return [pscustomobject]@{ Matches = $false; Reason = "the retained release could not be compared: $($_.Exception.Message)" } }
}

function Assert-AeroLinkInstallReleasePreflight {
    [CmdletBinding()]
    param(
        [AllowNull()][string]$CurrentTarget,
        [Parameter(Mandatory)][string]$VersionRoot,
        [string]$LegacyVersionRoot,
        [Parameter(Mandatory)][string]$IncomingRoot,
        [Parameter(Mandatory)]$Verified
    )
    if ([string]$Verified.Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z][0-9A-Za-z.-]*)?$') {
        throw "The verified release version '$($Verified.Version)' is not a safe semantic version directory name."
    }
    if ([string]$Verified.SourceSha -notmatch '^[0-9a-fA-F]{40}$') {
        throw "The verified release source SHA '$($Verified.SourceSha)' is not a full 40-character hexadecimal identity."
    }
    $candidate = [IO.Path]::GetFullPath($VersionRoot).TrimEnd('\')
    $legacy = if ($LegacyVersionRoot) { [IO.Path]::GetFullPath($LegacyVersionRoot).TrimEnd('\') } else { $null }
    $selected = $candidate
    if ($CurrentTarget) {
        $current = [IO.Path]::GetFullPath($CurrentTarget).TrimEnd('\')
        if ([string]::Equals($current, $candidate, [StringComparison]::OrdinalIgnoreCase)) { $selected = $candidate }
        elseif ($legacy -and [string]::Equals($current, $legacy, [StringComparison]::OrdinalIgnoreCase)) { $selected = $legacy }
        else {
            throw "A different release is already current at '$CurrentTarget'. A2 refuses the transition before backup, service stop, junction switch or migration; use the A3 clone-validated updater."
        }
    }
    if (Test-Path -LiteralPath $selected) {
        if (-not (Test-Path -LiteralPath $selected -PathType Container)) {
            throw "The existing release path '$selected' is not a directory; A2 refuses to replace it."
        }
        $retained = Compare-AeroLinkRetainedRelease -ExistingRoot $selected -IncomingRoot $IncomingRoot -Verified $Verified
        if (-not $retained.Matches) {
            throw "The existing release directory '$selected' cannot be reused: $($retained.Reason). A2 refuses the operation before changing the current release."
        }
    }
    return $selected
}

if ($RemoveBootstrapSecret) {
    $removed = Remove-AeroLinkProtectedSiteSecret -InstallationRoot $root -Name 'api-bootstrap'
    Write-Host "Bootstrap secret $(if ($removed.Removed) { 'removed' } else { 'was already absent' })." -ForegroundColor Green
    if (Get-Service -Name $identity.ServiceName -ErrorAction SilentlyContinue) { Restart-Service -Name $identity.ServiceName }
    exit 0
}

Step 'Machine roots: owner Administrators, no write for Users'
foreach ($entry in (Get-AeroLinkServerRootPlan -MachineRoot $MachineRoot)) { Set-AeroLinkDirectoryAccessBySid -Path $entry.Path -Grants $entry.Grants }

Step "Declaration: $($identity.Classification) at $root"
New-Item -ItemType Directory -Path $root -Force | Out-Null
$productLookup = Join-Path $PSScriptRoot '..'
$existing = $null
if (Test-Path -LiteralPath (Join-Path $root 'instance.json') -PathType Leaf) { $existing = Get-Content -LiteralPath (Join-Path $root 'instance.json') -Raw | ConvertFrom-Json }
if ($existing -and $existing.PSObject.Properties['classification'] -and [string]$existing.classification -ne $identity.Classification) {
    throw "$root is declared $($existing.classification), not $($identity.Classification). Nothing was changed."
}
$layout = Get-AeroLinkServerLayout -InstallationRoot $root

Step 'Release: verify against the installed trusted certificate'
New-Item -ItemType Directory -Path $layout.Trust, $layout.Versions, $layout.Packages -Force | Out-Null
$offered = New-Object Security.Cryptography.X509Certificates.X509Certificate2(, [IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $TrustedCertificate).Path))
$incoming = Join-Path $layout.Versions ('.incoming-' + [guid]::NewGuid().ToString('N'))
try {
    if ($AllowUnverifiedDevelopment -and $Name -ne 'Development') { throw 'An unverified development package is never installed as Production.' }
    # Validate the offered package against the certificate the operator supplied before changing the installed trust anchor.
    $verified = Test-AeroLinkReleasePackage -Package $Package -TrustedCertificate $TrustedCertificate -Destination $incoming -AllowUnverifiedDevelopment:$AllowUnverifiedDevelopment
    $versionRoot = (Get-AeroLinkServerLayout -InstallationRoot $root -Version $verified.Version -SourceSha $verified.SourceSha).VersionRoot
    $legacyVersionRoot = (Get-AeroLinkServerLayout -InstallationRoot $root -Version $verified.Version).VersionRoot
    $currentItem = if (Test-Path -LiteralPath $layout.Current) { Get-Item -LiteralPath $layout.Current -ErrorAction Stop } else { $null }
    $previousTarget = if ($currentItem) { [string]$currentItem.Target } else { $null }
    if ($currentItem -and [string]::IsNullOrWhiteSpace($previousTarget)) { throw "The existing current path '$($layout.Current)' is not a readable junction; refusing to replace it." }
    $versionRoot = Assert-AeroLinkInstallReleasePreflight -CurrentTarget $previousTarget -VersionRoot $versionRoot -LegacyVersionRoot $legacyVersionRoot -IncomingRoot $incoming -Verified $verified

    $installed = if (Test-Path -LiteralPath $layout.TrustedCertificate -PathType Leaf) {
        New-Object Security.Cryptography.X509Certificates.X509Certificate2(, [IO.File]::ReadAllBytes($layout.TrustedCertificate))
    } else { $null }
    if ($installed -and $installed.Thumbprint -ne $offered.Thumbprint) {
        if (-not $ReplaceTrustedCertificate) { throw "This installation trusts release certificate $($installed.Thumbprint), not $($offered.Thumbprint). Pass -ReplaceTrustedCertificate only for a deliberate key rotation. Nothing was installed." }
        $oldTrustBytes = [IO.File]::ReadAllBytes($layout.TrustedCertificate)
        try {
            Copy-Item -LiteralPath $TrustedCertificate -Destination $layout.TrustedCertificate -Force
            $reverified = Test-AeroLinkReleasePackage -Package $Package -TrustedCertificate $layout.TrustedCertificate -AllowUnverifiedDevelopment:$AllowUnverifiedDevelopment
            if ($reverified.Version -cne $verified.Version -or $reverified.SourceSha -cne $verified.SourceSha) {
                throw 'The package identity changed between offered-certificate and installed-trust verification.'
            }
        }
        catch {
            [IO.File]::WriteAllBytes($layout.TrustedCertificate, $oldTrustBytes)
            throw
        }
    }
    elseif (-not $installed) { Copy-Item -LiteralPath $TrustedCertificate -Destination $layout.TrustedCertificate -Force }

    if (Test-Path -LiteralPath $versionRoot) { Write-Host "    $($verified.Version) is already installed with matching signed identity and bytes; keeping it." -ForegroundColor DarkGray }
    else { Move-Item -LiteralPath $incoming -Destination $versionRoot }
}
finally { if (Test-Path -LiteralPath $incoming) { Remove-Item -LiteralPath $incoming -Recurse -Force } }
$packageName = Split-Path $Package -Leaf
Copy-Item -LiteralPath $Package -Destination (Join-Path $layout.Packages $packageName) -Force
Copy-Item -LiteralPath "$Package.sig" -Destination (Join-Path $layout.Packages "$packageName.sig") -Force
if (-not $previousTarget) {
    New-Item -ItemType Junction -Path $layout.Current -Target $versionRoot | Out-Null
    Write-Host "    current -> $($verified.Version) ($($verified.SourceSha))"
}
else { Write-Host "    current -> $($verified.Version) ($($verified.SourceSha)); existing exact release retained" }

Step "Declaration: persist $($identity.Classification) only after release preflight"
if (-not $existing -or -not $existing.PSObject.Properties['classification']) {
    Set-AeroLinkInstanceConfig -ProductRoot $productLookup -InstallationRoot $root -Label $identity.Label -Classification $identity.Classification | Out-Null
}
$site = Set-AeroLinkSiteDeclaration -InstallationRoot $root -ApiPort $ApiPort -PostgresPort $PostgresPort -PublicHostName $PublicHostName
if (-not (Test-AeroLinkServiceSidMatchesAccount -Site $site)) { throw "The declaration's runAsSid is an override; a real installation runs as NT SERVICE\$($site.ServiceName). Remove it first." }
Assert-AeroLinkSiteServiceSid -Site $site -Role Postgres | Out-Null

Step 'Installation pointer'
Set-AeroLinkInstallationPointer -ProductRoot $versionRoot -InstallationRoot $root -Note "AeroLink site server $Name (#1614): this program belongs to the installation named here." | Out-Null

Step 'PostgreSQL binaries'
$paths = Get-AeroLinkInstallationPaths -ProductRoot $site.ProgramRoot -InstallationRoot $root
if (-not (Test-Path -LiteralPath (Join-Path $paths.PostgresBin 'postgres.exe') -PathType Leaf)) {
    if (-not $PostgresBinaries) { throw "No PostgreSQL binaries at $($paths.PostgresBin). Supply -PostgresBinaries (a pgsql folder or zip; the host is offline)." }
    $source = (Resolve-Path -LiteralPath $PostgresBinaries).Path
    $target = Join-Path $paths.PostgresHome 'pgsql'
    New-Item -ItemType Directory -Path $paths.PostgresHome -Force | Out-Null
    if ([IO.Path]::GetExtension($source) -eq '.zip') {
        $unpack = Join-Path $paths.PostgresHome ('.unpack-' + [guid]::NewGuid().ToString('N'))
        try {
            Expand-Archive -LiteralPath $source -DestinationPath $unpack
            $pgsql = @(Get-ChildItem -LiteralPath $unpack -Directory -Recurse -Filter 'pgsql' | Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'bin\postgres.exe') }) | Select-Object -First 1
            if (-not $pgsql) { throw "$source contains no pgsql folder with bin\postgres.exe." }
            Move-Item -LiteralPath $pgsql.FullName -Destination $target
        }
        finally { if (Test-Path -LiteralPath $unpack) { Remove-Item -LiteralPath $unpack -Recurse -Force } }
    }
    else { Copy-Item -LiteralPath $source -Destination $target -Recurse }
}

Step 'Folder owners and permissions by SID'
Set-AeroLinkSiteFolderAcl -InstallationRoot $root | Out-Null

Step 'PostgreSQL cluster and service'
if (-not (Test-Path -LiteralPath (Join-Path $paths.PostgresData 'PG_VERSION') -PathType Leaf)) { Initialize-AeroLinkSitePostgres -InstallationRoot $root | Out-Null }
if (-not (Get-Service -Name $site.PostgresServiceName -ErrorAction SilentlyContinue)) { Register-AeroLinkSitePostgresService -InstallationRoot $root | Out-Null }

Step 'HTTPS certificate'
if (-not $CertificateThumbprint) {
    if (-not $RequestCertificateTemplate) {
        $settingsNow = if (Test-Path -LiteralPath $layout.SiteServerSettings) { Get-Content -LiteralPath $layout.SiteServerSettings -Raw | ConvertFrom-Json } else { $null }
        if ($settingsNow -and $settingsNow.certificate -and $settingsNow.certificate.thumbprint) { $CertificateThumbprint = [string]$settingsNow.certificate.thumbprint }
        else { throw 'Name the HTTPS certificate: -CertificateThumbprint (already in LocalMachine\My) or -RequestCertificateTemplate (enrol with the enterprise CA).' }
    }
    else {
        $enrolled = Get-Certificate -Template $RequestCertificateTemplate -DnsName $PublicHostName -CertStoreLocation 'Cert:\LocalMachine\My'
        if ($enrolled.Status -ne 'Issued') { throw "The certificate request was not issued ($($enrolled.Status)); approve it at the CA and rerun with -CertificateThumbprint." }
        $CertificateThumbprint = $enrolled.Certificate.Thumbprint
    }
}
$CertificateThumbprint = $CertificateThumbprint -replace '\s', ''
$keyFile = Grant-AeroLinkCertificateKeyRead -Thumbprint $CertificateThumbprint -Sid $site.RunAsSid
Write-Host "    $CertificateThumbprint; private key readable by $($site.RunAsAccount) ($keyFile)"
[ordered]@{ bindAddress = $BindAddress; http1Only = [bool]$Http1Only; certificate = [ordered]@{ thumbprint = $CertificateThumbprint } } |
    ConvertTo-Json | Set-Content -LiteralPath $layout.SiteServerSettings -Encoding UTF8

Step 'First-administrator bootstrap secret'
$bootstrapPath = Get-AeroLinkProtectedSiteSecretPath -InstallationRoot $root -Name 'api-bootstrap'
if (Test-Path -LiteralPath $bootstrapPath -PathType Leaf) { Write-Host '    Already stored (shown only when it was generated). Remove it with -RemoveBootstrapSecret once the first administrator exists.' -ForegroundColor DarkGray }
else {
    $bytes = New-Object byte[] 32
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    $plain = [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
    [Array]::Clear($bytes, 0, $bytes.Length)
    $secure = New-Object Security.SecureString
    foreach ($character in $plain.ToCharArray()) { $secure.AppendChar($character) }
    $secure.MakeReadOnly()
    $null = Set-AeroLinkProtectedSiteSecret -InstallationRoot $root -Name 'api-bootstrap' -Secret $secure -ReaderSid $site.RunAsSid
    Write-Host ''
    Write-Host "    BOOTSTRAP SECRET (shown once): $plain" -ForegroundColor Yellow
    Write-Host '    Use it once on the activation screen to create the administrator, then run -RemoveBootstrapSecret.' -ForegroundColor Yellow
    Write-Host ''
    $plain = $null
}

Step "API service $($site.ServiceName)"
$launcher = Join-Path $layout.Scripts 'Start-AeroLinkSite.ps1'
if (-not (Test-Path -LiteralPath $launcher -PathType Leaf)) { throw "The installed package has no site launcher ($launcher); it predates #1642 and cannot run a site server." }
$runtime = Get-AeroLinkSiteRuntimeEnvironment -InstallationRoot $root
$previousConnection = $env:ConnectionStrings__AeroLink
try {
    # The launcher composes the site's settings; its connection-string check is satisfied with the password-less
    # one, which is what the service keeps: the API appends the password from the protected store itself.
    $env:ConnectionStrings__AeroLink = $runtime['ConnectionStrings__AeroLink']
    $plan = & $launcher -InstallationRoot $root -ApiPath $layout.ApiExecutable -ValidateOnly
}
finally { $env:ConnectionStrings__AeroLink = $previousConnection }
$environment = [ordered]@{}
foreach ($entry in $plan.Settings.GetEnumerator()) { $environment[$entry.Key] = $entry.Value }
$environment['ConnectionStrings__AeroLink'] = $runtime['ConnectionStrings__AeroLink']
$environment['SiteServer__InstallationRoot'] = $root
$definition = New-AeroLinkApiServiceDefinition -Site $site -CurrentProgram $layout.Current -Environment $environment
$wasRunning = (Get-Service -Name $site.ServiceName -ErrorAction SilentlyContinue).Status -eq 'Running'
Install-AeroLinkApiService -Definition $definition -LogDirectory $paths.Logs | Out-Null

Step "Firewall: TCP $ApiPort inbound, domain profile only"
Set-AeroLinkApiFirewallRule -RuleName $identity.FirewallRuleName -Port $ApiPort -DisplayName "AeroLink $Name (HTTPS $ApiPort)" | Out-Null

Step 'Daily verified backup task'
if (Get-Command -Name Register-AeroLinkSiteBackupTask -ErrorAction SilentlyContinue) {
    Register-AeroLinkSiteBackupTask -InstallationRoot $root -ScriptRoot $layout.Scripts -Disabled:$NoStart | Out-Null
    if ($NoStart) { Write-Host '    Daily backup task staged disabled; enable it explicitly after attended acceptance.' }
}
else { throw 'This program has no Register-AeroLinkSiteBackupTask (#1649); a site is not installed without its daily backup.' }

if (-not $NoStart) {
    Step 'Start'
    Start-Service -Name $site.PostgresServiceName
    if ($wasRunning) { Restart-Service -Name $site.ServiceName } else { Start-Service -Name $site.ServiceName }
    & (Join-Path $PSScriptRoot 'Test-AeroLinkHost.ps1') -Name $Name -MachineRoot $MachineRoot
}
Write-Host "AeroLink $Name installed: $($verified.Version) at https://$($PublicHostName):$ApiPort" -ForegroundColor Green
