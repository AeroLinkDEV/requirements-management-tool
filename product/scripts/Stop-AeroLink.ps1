[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$productRoot=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
foreach($port in 5173,5080){foreach($listener in @(Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue)){$process=Get-CimInstance Win32_Process -Filter "ProcessId=$($listener.OwningProcess)" -ErrorAction SilentlyContinue;$command="$($process.ExecutablePath) $($process.CommandLine)";if($command -notlike "*$productRoot*"){throw "Port $port belongs to an unrelated process. Refusing to stop PID $($listener.OwningProcess)."};Stop-Process -Id $listener.OwningProcess -Force;Write-Host "Stopped AeroLink service on port $port (PID $($listener.OwningProcess))."}}
& (Join-Path $PSScriptRoot 'Stop-Postgres.ps1')
