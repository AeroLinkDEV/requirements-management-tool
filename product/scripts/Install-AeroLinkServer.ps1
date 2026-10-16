#Requires -Version 5.1
<#
    .SYNOPSIS
        Installs or updates one AeroLink site server installation on the company host (#1614 1.7, 1.8). Elevated.

    .DESCRIPTION
        Idempotent: every step checks what is already there and changes only what is missing or different, so the
        same command installs a new installation, repairs one, and moves an existing one to a newer package.

        In order:
          1. lock C:\AeroLink and %ProgramData%\AeroLink: owner Administrators, no write for Users;
          2. declare the installation (SiteProduction or SiteDevelopment, D8) and its ports, public host name and
             virtual accounts (Set-AeroLinkSiteDeclaration, #1636);
          3. install the release certificate the installer trusts into <root>\trust, refusing to replace a
             different one silently; verify the package against THAT certificate, never the package's own copy;
             extract it into program\versions\<version> and point program\current at it; keep the signed package
             under program\packages for the host check;
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
if (-not $existing -or -not $existing.PSObject.Properties['classification']) {
    Set-AeroLinkInstanceConfig -ProductRoot $productLookup -InstallationRoot $root -Label $identity.Label -Classification $identity.Classification | Out-Null
}
$site = Set-AeroLinkSiteDeclaration -InstallationRoot $root -ApiPort $ApiPort -PostgresPort $PostgresPort -PublicHostName $PublicHostName
if (-not (Test-AeroLinkServiceSidMatchesAccount -Site $site)) { throw "The declaration's runAsSid is an override; a real installation runs as NT SERVICE\$($site.ServiceName). Remove it first." }
Assert-AeroLinkSiteServiceSid -Site $site -Role Postgres | Out-Null
$layout = Get-AeroLinkServerLayout -InstallationRoot $root

Step 'Release: verify against the installed trusted certificate'
New-Item -ItemType Directory -Path $layout.Trust, $layout.Versions, $layout.Packages -Force | Out-Null
$offered = New-Object Security.Cryptography.X509Certificates.X509Certificate2(, [IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $TrustedCertificate).Path))
if (Test-Path -LiteralPath $layout.TrustedCertificate -PathType Leaf) {
    $installed = New-Object Security.Cryptography.X509Certificates.X509Certificate2(, [IO.File]::ReadAllBytes($layout.TrustedCertificate))
    if ($installed.Thumbprint -ne $offered.Thumbprint) {
        if (-not $ReplaceTrustedCertificate) { throw "This installation trusts release certificate $($installed.Thumbprint), not $($offered.Thumbprint). Pass -ReplaceTrustedCertificate only for a deliberate key rotation. Nothing was installed." }
        Copy-Item -LiteralPath $TrustedCertificate -Destination $layout.TrustedCertificate -Force
    }
}
else { Copy-Item -LiteralPath $TrustedCertificate -Destination $layout.TrustedCertificate }
$incoming = Join-Path $layout.Versions ('.incoming-' + [guid]::NewGuid().ToString('N'))
try {
    if ($AllowUnverifiedDevelopment -and $Name -ne 'Development') { throw 'An unverified development package is never installed as Production.' }
    $verified = Test-AeroLinkReleasePackage -Package $Package -TrustedCertificate $layout.TrustedCertificate -Destination $incoming -AllowUnverifiedDevelopment:$AllowUnverifiedDevelopment
    $versionRoot = (Get-AeroLinkServerLayout -InstallationRoot $root -Version $verified.Version).VersionRoot
    if (Test-Path -LiteralPath $versionRoot) { Write-Host "    $($verified.Version) is already installed; keeping it." -ForegroundColor DarkGray }
    else { Move-Item -LiteralPath $incoming -Destination $versionRoot }
}
finally { if (Test-Path -LiteralPath $incoming) { Remove-Item -LiteralPath $incoming -Recurse -Force } }
$packageName = Split-Path $Package -Leaf
Copy-Item -LiteralPath $Package -Destination (Join-Path $layout.Packages $packageName) -Force
Copy-Item -LiteralPath "$Package.sig" -Destination (Join-Path $layout.Packages "$packageName.sig") -Force
$previousTarget = if (Test-Path -LiteralPath $layout.Current) { [string](Get-Item -LiteralPath $layout.Current).Target } else { $null }
$apiWasRunning = (Get-Service -Name $site.ServiceName -ErrorAction SilentlyContinue).Status -eq 'Running'
if ($previousTarget -and -not [string]::Equals(([IO.Path]::GetFullPath($previousTarget)).TrimEnd('\'), $versionRoot.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)) {
    # An update (D9: any time, a short restart outage): a verified backup with the running version's own scripts,
    # then stop the API, switch, and let the new version migrate on start. Rollback is restoring that backup.
    Step "Update from $(Split-Path $previousTarget -Leaf): verified backup, then stop the API"
    $backup = & (Join-Path $previousTarget 'scripts\Backup-AeroLink.ps1') -InstallationRoot $root -RetentionDays 15 |
        Where-Object { $_ -is [pscustomobject] -and $_.PSObject.Properties['Archive'] } | Select-Object -Last 1
    if (-not $backup) { throw 'The pre-update backup produced no archive; nothing was switched.' }
    $null = & (Join-Path $previousTarget 'scripts\Verify-AeroLinkBackup.ps1') -BackupArchive $backup.Archive -InstallationRoot $root
    Write-Host "    pre-update backup: $($backup.Archive)"
    if ($apiWasRunning) { Stop-Service -Name $site.ServiceName }
}
if (Test-Path -LiteralPath $layout.Current) { (Get-Item -LiteralPath $layout.Current).Delete() }
New-Item -ItemType Junction -Path $layout.Current -Target $versionRoot | Out-Null
Write-Host "    current -> $($verified.Version) ($($verified.SourceSha))"

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
    Register-AeroLinkSiteBackupTask -InstallationRoot $root -ScriptRoot $layout.Scripts -RetentionDays 15 | Out-Null
}
else { throw 'This program has no Register-AeroLinkSiteBackupTask (#1649); a site is not installed without its daily backup.' }

if (-not $NoStart) {
    Step 'Start'
    Start-Service -Name $site.PostgresServiceName
    if ($wasRunning) { Restart-Service -Name $site.ServiceName } else { Start-Service -Name $site.ServiceName }
    & (Join-Path $PSScriptRoot 'Test-AeroLinkHost.ps1') -Name $Name -MachineRoot $MachineRoot
}
Write-Host "AeroLink $Name installed: $($verified.Version) at https://$($PublicHostName):$ApiPort" -ForegroundColor Green
