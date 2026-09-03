# Installs the PostgreSQL AeroLink runs against, into the repository's ignored .local directory.
#
# Everything here is relative to this script, so it works from whatever path the repository is cloned to and
# on any machine. There is nothing per-machine to configure and no second copy of this script to keep in step.
#
# The care taken over a partial download is not defensive programming for its own sake. This fetches 320 MB
# from a public host, often over a corporate network, and the earlier version kept whatever arrived: it
# checked only whether the file *existed*, so an interrupted transfer was cached and re-extracted forever.
# `tar` unpacks bin/ before share/, so the result was a postgres.exe with no postgres.bki — and the launcher's
# guard only looked for postgres.exe, so the half-install passed it and died inside initdb reporting a
# "corrupted installation", which tells an operator nothing they can act on.

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
# The installation this source belongs to, which for a dedicated production checkout is the canonical HOME
# installation rather than this folder (#881). Installing into the wrong one would produce a second, empty
# AeroLink that starts perfectly and holds nothing.
Import-Module (Join-Path $PSScriptRoot 'AeroLinkInstallation.psm1') -Force
$installation = Get-AeroLinkInstallationPaths -ProductRoot $root
$local = $installation.InstallationRoot
$archive = $installation.PostgresArchive
$destination = $installation.PostgresHome
$source = 'https://get.enterprisedb.com/postgresql/postgresql-18.4-1-windows-x64-binaries.zip'

# Two files, because either alone can be present without the other. postgres.exe proves something was
# extracted; postgres.bki is what initdb reads to create a cluster, and is the file whose absence produced the
# unhelpful error. Checking the one the next step actually needs is the point.
$binary = Join-Path $installation.PostgresBin 'postgres.exe'
$catalogue = $installation.PostgresCatalogue
function Test-PostgresInstalled { (Test-Path $binary) -and (Test-Path $catalogue) }

New-Item -ItemType Directory -Force -Path $local | Out-Null

if (Test-PostgresInstalled) {
    Write-Host 'PostgreSQL is already installed under product\.local\postgresql.' -ForegroundColor Green
}
else {
    # Twice, not once: the first attempt may be working from a cached archive that turns out to be truncated,
    # and the operator should not have to know that in order to get a working install.
    for ($attempt = 1; $attempt -le 2 -and -not (Test-PostgresInstalled); $attempt++) {
        if (-not (Test-Path $archive)) {
            Write-Host "Downloading PostgreSQL 18.4 (about 320 MB), attempt $attempt..." -ForegroundColor Cyan
            # --fail catches an HTTP error; --retry survives a transient one. Neither catches a body that
            # stops early, which is why the archive is verified below rather than trusted.
            & curl.exe -L --fail --retry 3 --retry-all-errors --progress-bar -o $archive $source
            if ($LASTEXITCODE -ne 0) {
                Remove-Item $archive -Force -ErrorAction SilentlyContinue
                throw "The PostgreSQL download failed. If this machine reaches the internet through a proxy, " +
                      "download $source by hand and save it as $archive, then run this script again."
            }
        }

        Write-Host 'Verifying the archive...' -ForegroundColor Cyan
        & tar.exe -tf $archive *> $null
        if ($LASTEXITCODE -ne 0) {
            Write-Host '      The archive is incomplete. Discarding it and downloading again.' -ForegroundColor Yellow
            Remove-Item $archive -Force -ErrorAction SilentlyContinue
            continue
        }

        Write-Host 'Extracting...' -ForegroundColor Cyan
        # Cleared first, so a previous partial extraction cannot leave files that make this one look complete.
        Remove-Item $destination -Recurse -Force -ErrorAction SilentlyContinue
        New-Item -ItemType Directory -Force -Path $destination | Out-Null
        & tar.exe -xf $archive -C $destination
        if (-not (Test-PostgresInstalled)) {
            Write-Host '      The extraction did not produce a complete installation. Discarding and retrying.' -ForegroundColor Yellow
            Remove-Item $archive -Force -ErrorAction SilentlyContinue
        }
    }

    if (-not (Test-PostgresInstalled)) {
        throw "PostgreSQL could not be installed. Expected $catalogue after extraction and it is not there. " +
              "Delete product\.local\postgresql-18.4.zip and product\.local\postgresql, then run this script again."
    }
    Write-Host 'PostgreSQL installed.' -ForegroundColor Green
}

& (Join-Path $PSScriptRoot 'Start-Postgres.ps1')
