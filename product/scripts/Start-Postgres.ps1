$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$bin = Join-Path $root '.local\postgresql\pgsql\bin'
$data = Join-Path $root '.local\pgdata'
$log = Join-Path $root '.local\postgresql.log'
if (-not (Test-Path (Join-Path $bin 'postgres.exe'))) { throw 'Local PostgreSQL binaries are missing. Run Setup-Postgres.ps1 first.' }
& (Join-Path $bin 'pg_isready.exe') -h 127.0.0.1 -p 54329 -U postgres -d postgres *> $null
if ($LASTEXITCODE -eq 0) { Write-Host 'PostgreSQL is already accepting connections on 127.0.0.1:54329.'; return }
if (-not (Test-Path (Join-Path $data 'PG_VERSION'))) { & (Join-Path $bin 'initdb.exe') -D $data -U postgres -A trust --encoding=UTF8 --no-locale }
$pidFile = Join-Path $data 'postmaster.pid'
if (Test-Path $pidFile) {
    $recordedPid = [int](Get-Content $pidFile -TotalCount 1)
    $owner = Get-Process -Id $recordedPid -ErrorAction SilentlyContinue
    if ($owner -and $owner.ProcessName -like 'postgres*') { throw "PostgreSQL process $recordedPid exists but is not accepting connections. Inspect $log." }
    Remove-Item -LiteralPath $pidFile -Force
    Write-Host "Removed stale PostgreSQL PID file from process $recordedPid."
}
& (Join-Path $bin 'pg_ctl.exe') -D $data -l $log -o '"-p 54329 -h 127.0.0.1"' start
Start-Sleep -Seconds 2
$databaseExists = & (Join-Path $bin 'psql.exe') -h 127.0.0.1 -p 54329 -U postgres -d postgres -tAc "SELECT 1 FROM pg_database WHERE datname='aerolink'"
if ($databaseExists -ne '1') { & (Join-Path $bin 'createdb.exe') -h 127.0.0.1 -p 54329 -U postgres aerolink }
& (Join-Path $bin 'pg_isready.exe') -h 127.0.0.1 -p 54329 -U postgres
