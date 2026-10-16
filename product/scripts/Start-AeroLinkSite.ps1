#Requires -Version 5.1
<#
    .SYNOPSIS
        Starts the AeroLink API for a site server installation (#1614): Production, HTTPS from Kestrel.

    .DESCRIPTION
        The launch path for the two installations on the company site host, SiteProduction and SiteDevelopment
        (#1614 D4, D8). It is new and separate on purpose: the HOME launchers (START_AEROLINK*.bat,
        Start-AeroLink.ps1, Start-AeroLinkProduction.ps1) are unchanged, still run Development, and refuse a site
        installation; this launcher refuses everything that is not one.

        It does three things and nothing else:
          1. reads the installation's declaration: instance.json (SiteProduction or SiteDevelopment, with the
             site block Set-AeroLinkSiteDeclaration writes) and the non-secret site-server.json;
          2. composes the Production environment: no demonstration data or accounts, secure cookies, no insecure
             webhooks, the installation-owned state of #1614 1.5 (key ring, evidence, connector key and id,
             notification authority, instance id), and the SiteServer settings the API needs to serve HTTPS;
          3. runs the API in the foreground, in the API's own directory, and returns its exit code.

        Every rule is enforced again by the API, which refuses to start a site server that is not a secure
        Production server (SiteServerHosting.cs). This script is the supported way to compose that, not the
        only guard. It writes nothing: the declaration, including the instance id, is minted by
        Set-AeroLinkSiteDeclaration, and the installation root is read-only to the service account.

        Secrets are never written by this script. The connection string (ConnectionStrings__AeroLink, with the
        application password) and, on first install, the bootstrap secret (Identity__BootstrapSecret) are
        inherited from the environment the supervisor gives it.

        The API port and the public host name are the site declaration's ApiPort and PublicHostName, the single
        source Stop, Restore and Diagnostics read too. site-server.json, beside instance.json, names the rest of
        the HTTPS endpoint and must name neither:
          {
            "bindAddress": "0.0.0.0",
            "http1Only":   false,
            "certificate": { "thumbprint": "..." }      or { "subject": "..." [, "store": "My"] }
                                                         or { "path": "C:\\...\\site.crt", "keyPath": "C:\\...\\site.key" }
          }

    .PARAMETER InstallationRoot
        The site installation's root, for example C:\AeroLink\AeroLink Production.

    .PARAMETER ApiPath
        The built or published API: AeroLink.Api.exe, or AeroLink.Api.dll run through dotnet.

    .PARAMETER ValidateOnly
        Compose and return the non-secret launch settings without starting anything.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$InstallationRoot,
    [Parameter(Mandatory)][string]$ApiPath,
    [switch]$ValidateOnly
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'AeroLinkInstallation.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'AeroLinkSiteInstallation.psm1') -Force
$productRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

if (-not [IO.Path]::IsPathRooted($InstallationRoot)) { throw "InstallationRoot must be an absolute path; it is '$InstallationRoot'." }
$InstallationRoot = [IO.Path]::GetFullPath($InstallationRoot)
if (-not (Test-Path -LiteralPath $InstallationRoot -PathType Container)) {
    throw "The site installation root does not exist: $InstallationRoot. Nothing was started."
}

# Refuses anything that is not declared SiteProduction or SiteDevelopment. Never mints: see the description.
$instance = Get-AeroLinkInstanceConfig -ProductRoot $productRoot -InstallationRoot $InstallationRoot -Mode Site
if ([string]::IsNullOrWhiteSpace([string]$instance.InstanceId)) {
    throw "The site installation at $InstallationRoot has no instanceId. Declare it with Set-AeroLinkSiteDeclaration, which mints one. Nothing was started."
}
$declaration = Get-AeroLinkSiteDeclaration -InstallationRoot $InstallationRoot
if (-not $declaration) {
    throw "The site installation at $InstallationRoot has no site declaration (ports, service accounts, public host name). Declare it with Set-AeroLinkSiteDeclaration. Nothing was started."
}
if ([string]::IsNullOrWhiteSpace([string]$declaration.PublicHostName)) {
    throw "The site declaration at $($declaration.ConfigPath) has no publicHostName. Set it with Set-AeroLinkSiteDeclaration -PublicHostName. Nothing was started."
}

$sitePath = Join-Path $InstallationRoot 'site-server.json'
if (-not (Test-Path -LiteralPath $sitePath -PathType Leaf)) {
    throw "The site installation has no $sitePath, so there is no HTTPS endpoint to serve. Nothing was started."
}
try { $site = Get-Content -LiteralPath $sitePath -Raw | ConvertFrom-Json }
catch { throw "The site server settings at $sitePath are malformed: $($_.Exception.Message)" }

function Get-SiteValue([object]$Object, [string]$Name) {
    if ($null -ne $Object -and $Object.PSObject.Properties[$Name] -and $null -ne $Object.$Name) { return [string]$Object.$Name }
    return ''
}

foreach ($duplicate in @('port', 'apiPort', 'publicHost', 'publicHostName')) {
    if ($site.PSObject.Properties[$duplicate]) {
        throw "$sitePath names $duplicate. The API port and public host name are declared once, in the site declaration (Set-AeroLinkSiteDeclaration); remove it here. Nothing was started."
    }
}

$certificate = if ($site.PSObject.Properties['certificate']) { $site.certificate } else { $null }
$http1Only = (Get-SiteValue $site 'http1Only')
$settings = [ordered]@{
    'ASPNETCORE_ENVIRONMENT'                    = 'Production'
    # The API listens on its HTTPS endpoint only; an inherited URL list would only produce a warning.
    'ASPNETCORE_URLS'                           = $null
    'SiteServer__Enabled'                       = 'true'
    'SiteServer__PublicHost'                    = [string]$declaration.PublicHostName
    'SiteServer__BindAddress'                   = (Get-SiteValue $site 'bindAddress')
    'SiteServer__Port'                          = [string]$declaration.ApiPort
    'SiteServer__Http1Only'                     = $(if ($http1Only) { $http1Only.ToLowerInvariant() } else { 'false' })
    'SiteServer__Certificate__Thumbprint'       = (Get-SiteValue $certificate 'thumbprint')
    'SiteServer__Certificate__Subject'          = (Get-SiteValue $certificate 'subject')
    'SiteServer__Certificate__Store'            = (Get-SiteValue $certificate 'store')
    'SiteServer__Certificate__Path'             = (Get-SiteValue $certificate 'path')
    'SiteServer__Certificate__KeyPath'          = (Get-SiteValue $certificate 'keyPath')
    'DemoData__Enabled'                         = 'false'
    'Identity__SeedDemoAccounts'                = 'false'
    'Identity__AllowDemoAccounts'               = 'false'
    'Identity__CookieSecure'                    = 'true'
    'Integrations__AllowInsecureWebhookTargets' = 'false'
    'Runtime__Mode'                             = $(if ($instance.Classification -eq 'SiteProduction') { 'SITE-PRODUCTION' } else { 'SITE-DEVELOPMENT' })
    'Instance__InstanceId'                      = $instance.InstanceId
    'Instance__Label'                           = $instance.Label
    'Instance__Classification'                  = $instance.Classification
}

# Installation-owned state (#1614 1.5): without it the key ring and evidence fall back to the service account's
# profile. Its connection string carries no password and would replace the inherited one, so it is left out.
$owned = Get-AeroLinkSiteRuntimeEnvironment -InstallationRoot $InstallationRoot
foreach ($entry in $owned.GetEnumerator()) {
    if ($entry.Key -eq 'ConnectionStrings__AeroLink') { continue }
    $settings[$entry.Key] = $entry.Value
}

if ([string]::IsNullOrWhiteSpace($env:ConnectionStrings__AeroLink)) {
    throw 'ConnectionStrings__AeroLink is not set. A site installation names its own database; this launcher never falls back to the HOME database. Nothing was started.'
}

$api = if ([IO.Path]::IsPathRooted($ApiPath)) { [IO.Path]::GetFullPath($ApiPath) } else { [IO.Path]::GetFullPath((Join-Path (Get-Location).Path $ApiPath)) }
if (-not (Test-Path -LiteralPath $api -PathType Leaf)) { throw "The API named by -ApiPath does not exist: $api. Nothing was started." }
$extension = [IO.Path]::GetExtension($api).ToLowerInvariant()
if ($extension -notin @('.exe', '.dll')) { throw "-ApiPath must name AeroLink.Api.exe or AeroLink.Api.dll; it names $api." }

if ($ValidateOnly) {
    return [pscustomobject]@{
        InstallationRoot = $InstallationRoot
        Classification   = $instance.Classification
        Api              = $api
        Settings         = $settings
    }
}

foreach ($entry in $settings.GetEnumerator()) {
    [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
}

# The API's directory is its content root: appsettings.json and a published wwwroot sit beside the assembly.
Push-Location -LiteralPath (Split-Path $api -Parent)
try {
    Write-Host "Starting AeroLink $($instance.Classification) ($($instance.Label)) at https://$($settings['SiteServer__PublicHost']):$($settings['SiteServer__Port'])" -ForegroundColor Cyan
    if ($extension -eq '.exe') { & $api } else { & dotnet $api }
    $code = $LASTEXITCODE
}
finally { Pop-Location }
exit $code
