#Requires -Version 5.1
<#
    Operator CLI for the dedicated HOME production source.

    Actions:
      Preview   Show exactly what Install would create and where, and change nothing.
      Install   Create (or confirm) the dedicated production clone, point it at this machine's canonical
                AeroLink installation, and write the per-user production-source configuration.
      Status    Read-only: where the production source is, whether it is canonical, and how it relates to
                origin/main.
      Update    Bring the production source to the current approved origin/main by strict fast-forward.

    This never touches the development checkout it is run from beyond reading its origin URL and its
    installation root, and it never initializes, copies, or migrates persistent AeroLink data.
#>
[CmdletBinding()]
param(
    [ValidateSet('Preview', 'Install', 'Status', 'Update')]
    [string]$Action = 'Preview',

    # Where the dedicated production source should live. The default sits beside the development checkout so
    # both are visible in one place, and neither is inside the other.
    [string]$SourceRoot,

    # The canonical persistent installation the production source must use. Defaults to this checkout's,
    # which is the point: source is separated, data is not.
    [string]$InstallationRoot
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'AeroLinkProductionSource.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'AeroLinkInstallation.psm1') -Force

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$productRoot = Join-Path $repositoryRoot 'product'

if (-not $SourceRoot) { $SourceRoot = Join-Path (Split-Path $repositoryRoot -Parent) 'AeroLink Production' }
if (-not $InstallationRoot) { $InstallationRoot = (Get-AeroLinkInstallationPaths -ProductRoot $productRoot).InstallationRoot }

switch ($Action) {
    'Preview' {
        Write-Host 'AeroLink dedicated production source - preview' -ForegroundColor Cyan
        Write-Host ''
        Write-Host "Development checkout (untouched): $repositoryRoot"
        Write-Host "Production source to create:      $SourceRoot"
        Write-Host "Canonical installation to use:    $InstallationRoot"
        Write-Host "Configuration to write:           $(Get-AeroLinkProductionSourceConfigPath)"
        Write-Host ''
        Write-Host 'Install would clone origin/main into the production source, mark it as dedicated'
        Write-Host 'production source, and record an installation pointer so it runs the canonical HOME'
        Write-Host 'PostgreSQL, evidence, attachments and backups rather than initializing new ones.'
        Write-Host 'No persistent data is created, copied, reset, or migrated.'
        exit 0
    }
    'Install' {
        $result = Initialize-AeroLinkProductionSource -SourceRoot $SourceRoot -InstallationRoot $InstallationRoot `
            -ReferenceRepositoryRoot $repositoryRoot -WriteConfig
        $result | Format-List
        if (-not $result.Canonical) {
            Write-Host "The production source exists but is not canonical: $($result.Reason)" -ForegroundColor Yellow
            exit 1
        }
        # Declare the installation HOME CANONICAL as part of setting HOME production up.
        #
        # Not only a badge. Import-AeroLinkHomeSnapshot refuses to overwrite an installation declared
        # HomeCanonical, so an installation left Undeclared — which is what a normally configured HOME was,
        # because nothing established it — had no protection against having the canonical database replaced
        # by a laptop snapshot. Declaring it is what arms that guard.
        $existingInstance = Get-AeroLinkInstanceConfig -ProductRoot $productRoot -Mode HomeCanonical
        if ($existingInstance.Classification -eq 'HomeCanonical') {
            Write-Host "Instance already declared: $($existingInstance.Label) ($($existingInstance.Classification))." -ForegroundColor DarkGray
        }
        elseif ($existingInstance.Classification -ne 'Undeclared') {
            Write-Host "This installation is declared $($existingInstance.Label) ($($existingInstance.Classification)), not HOME CANONICAL." -ForegroundColor Yellow
            Write-Host 'Leaving it alone: reclassifying an installation is an operator decision, not a side effect of' -ForegroundColor Yellow
            Write-Host 'setting up a production source. Correct it with Set-AeroLinkInstanceConfig if that is wrong.' -ForegroundColor Yellow
        }
        else {
            Set-AeroLinkInstanceConfig -ProductRoot $productRoot -Label 'HOME CANONICAL' -Classification 'HomeCanonical' | Out-Null
            Write-Host 'Instance declared: HOME CANONICAL.' -ForegroundColor Green
            Write-Host 'The HOME-to-laptop snapshot import will now refuse to replace this database.' -ForegroundColor Green
        }

        Write-Host 'AeroLink dedicated production source ready.' -ForegroundColor Green
        Write-Host 'Reinstall the remote-demo recovery task so it invokes this source:' -ForegroundColor DarkGray
        Write-Host '      CONFIGURE_AEROLINK_REMOTE_DEMO.bat' -ForegroundColor Gray
        exit 0
    }
    'Status' {
        $config = Get-AeroLinkProductionSourceConfig
        $posture = Get-AeroLinkProductionSourcePosture -SourceRoot $config.SourceRoot -RemoteName $config.RemoteName
        [pscustomobject]@{
            SourceRoot       = $config.SourceRoot
            InstallationRoot = (Get-AeroLinkInstallationPaths -ProductRoot (Join-Path $config.SourceRoot 'product')).InstallationRoot
            Dedicated        = $posture.Dedicated
            Canonical        = $posture.Canonical
            Branch           = if ($posture.Posture) { $posture.Posture.Branch } else { $null }
            HeadSha          = if ($posture.Posture) { $posture.Posture.ShortSha } else { $null }
            RemoteMainSha    = if ($posture.Posture) { $posture.Posture.ShortRemoteMainSha } else { $null }
            Relationship     = if ($posture.Posture) { $posture.Posture.Relationship } else { $null }
            Reason           = $posture.Reason
        } | Format-List
        exit 0
    }
    'Update' {
        $config = Get-AeroLinkProductionSourceConfig
        $result = Update-AeroLinkProductionSource -SourceRoot $config.SourceRoot -RemoteName $config.RemoteName `
            -FetchTimeoutSeconds $config.FetchTimeoutSeconds
        $result | Format-List
        exit ($(if ($result.Canonical) { 0 } else { 1 }))
    }
}
