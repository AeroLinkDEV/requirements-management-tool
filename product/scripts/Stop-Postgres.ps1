$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$bin = Join-Path $root '.local\postgresql\pgsql\bin'
$data = Join-Path $root '.local\pgdata'
$logs = Join-Path $root '.local\logs'
Import-Module (Join-Path $PSScriptRoot 'AeroLinkNativeRunner.psm1') -Force

$stopRun = Invoke-AeroLinkNativeCommand -FilePath (Join-Path $bin 'pg_ctl.exe') `
    -ArgumentList @('-D', $data, 'stop', '-m', 'fast') `
    -StandardOutput (Join-Path $logs 'postgres-stop.stdout.log') `
    -StandardError (Join-Path $logs 'postgres-stop.stderr.log') `
    -TimeoutSeconds 120 -StepName 'pg_ctl fast stop'
if ($stopRun.ExitCode -ne 0) {
    throw "PostgreSQL did not shut down cleanly: $($stopRun.Detail)"
}
