$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$local = Join-Path $root '.local'
$archive = Join-Path $local 'postgresql-18.4.zip'
$destination = Join-Path $local 'postgresql'
New-Item -ItemType Directory -Force -Path $local | Out-Null
if (-not (Test-Path $archive)) { curl.exe -L --fail --silent --show-error -o $archive 'https://get.enterprisedb.com/postgresql/postgresql-18.4-1-windows-x64-binaries.zip' }
if (-not (Test-Path (Join-Path $destination 'pgsql\share\postgresql.conf.sample'))) {
    New-Item -ItemType Directory -Force -Path $destination | Out-Null
    tar.exe -xf $archive -C $destination
}
& (Join-Path $PSScriptRoot 'Start-Postgres.ps1')
