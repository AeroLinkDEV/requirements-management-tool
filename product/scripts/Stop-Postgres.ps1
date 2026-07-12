$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$bin = Join-Path $root '.local\postgresql\pgsql\bin'
$data = Join-Path $root '.local\pgdata'
& (Join-Path $bin 'pg_ctl.exe') -D $data stop -m fast
