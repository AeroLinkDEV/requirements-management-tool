$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$bin = Join-Path $root '.local\postgresql\pgsql\bin'
$data = Join-Path $root '.local\pgdata'
$log = Join-Path $root '.local\postgresql.log'
if (-not (Test-Path (Join-Path $bin 'postgres.exe'))) { throw 'Local PostgreSQL binaries are missing. Run Setup-Postgres.ps1 first.' }
& (Join-Path $bin 'pg_isready.exe') -h 127.0.0.1 -p 55432 -U postgres -d postgres *> $null
if ($LASTEXITCODE -eq 0) { Write-Host 'PostgreSQL is already accepting connections on 127.0.0.1:55432.'; return }
if (-not (Test-Path (Join-Path $data 'PG_VERSION'))) { & (Join-Path $bin 'initdb.exe') -D $data -U postgres -A trust --encoding=UTF8 --no-locale }
& (Join-Path $bin 'pg_ctl.exe') -D $data -l $log -o '"-p 55432 -h 127.0.0.1"' start
Start-Sleep -Seconds 2
& (Join-Path $bin 'createdb.exe') -h 127.0.0.1 -p 55432 -U postgres aerolink 2>$null
& (Join-Path $bin 'pg_isready.exe') -h 127.0.0.1 -p 55432 -U postgres
