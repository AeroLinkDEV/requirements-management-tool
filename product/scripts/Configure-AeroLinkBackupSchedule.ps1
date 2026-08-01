[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet('Install', 'Preview', 'Status', 'Remove')]
    [string]$Action = 'Install',

    [string]$DailyAt = '02:00',

    [ValidateRange(1, 3650)]
    [int]$RetentionDays = 30,

    [ValidateNotNullOrEmpty()]
    [string]$TaskName = 'AeroLink Daily Backup'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$runner = (Resolve-Path (Join-Path $PSScriptRoot 'Invoke-AeroLinkScheduledBackup.ps1')).Path
$windowsPowerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent().Name
$parsedTime = [DateTime]::MinValue
$validTime = [DateTime]::TryParseExact(
    $DailyAt,
    'HH:mm',
    [Globalization.CultureInfo]::InvariantCulture,
    [Globalization.DateTimeStyles]::None,
    [ref]$parsedTime)
if (-not $validTime) { throw "DailyAt must use 24-hour HH:mm format, for example 02:00 or 18:30." }

$normalizedTime = $parsedTime.ToString('HH:mm')
$taskArguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "{0}" -RetentionDays {1}' -f $runner, $RetentionDays
$preview = [pscustomobject]@{
    Installed = $false
    TaskName = $TaskName
    Schedule = 'Daily'
    DailyAt = $normalizedTime
    RetentionDays = $RetentionDays
    User = $identity
    Executable = $windowsPowerShell
    Arguments = $taskArguments
    WorkingDirectory = $repositoryRoot
    Runner = $runner
}

if ($Action -eq 'Preview') { $preview; return }

Import-Module ScheduledTasks -ErrorAction Stop
$existing = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue

if ($Action -eq 'Status') {
    if (-not $existing) { $preview; return }
    $info = Get-ScheduledTaskInfo -TaskName $TaskName
    [pscustomobject]@{
        Installed = $true
        TaskName = $TaskName
        State = $existing.State
        NextRunTime = $info.NextRunTime
        LastRunTime = $info.LastRunTime
        LastTaskResult = $info.LastTaskResult
        Executable = $existing.Actions[0].Execute
        Arguments = $existing.Actions[0].Arguments
        WorkingDirectory = $existing.Actions[0].WorkingDirectory
    }
    return
}

if ($Action -eq 'Remove') {
    if (-not $existing) { Write-Host "AeroLink backup schedule is not installed."; return }
    if ($PSCmdlet.ShouldProcess($TaskName, 'Remove scheduled backup task')) {
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
        Write-Host "Removed scheduled task '$TaskName'." -ForegroundColor Green
    }
    return
}

if (-not $PSCmdlet.ShouldProcess($TaskName, "Install daily backup at $normalizedTime")) { return }

$triggerAt = (Get-Date).Date.Add($parsedTime.TimeOfDay)
$scheduledAction = New-ScheduledTaskAction -Execute $windowsPowerShell -Argument $taskArguments -WorkingDirectory $repositoryRoot
$trigger = New-ScheduledTaskTrigger -Daily -At $triggerAt
$principal = New-ScheduledTaskPrincipal -UserId $identity -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet `
    -StartWhenAvailable `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit (New-TimeSpan -Hours 2) `
    -MultipleInstances IgnoreNew
$definition = New-ScheduledTask `
    -Action $scheduledAction `
    -Trigger $trigger `
    -Principal $principal `
    -Settings $settings `
    -Description 'Creates and integrity-verifies the complete AeroLink PostgreSQL, evidence, and configuration backup.'

Register-ScheduledTask -TaskName $TaskName -InputObject $definition -Force | Out-Null
Write-Host "Installed '$TaskName' for $normalizedTime each day with $RetentionDays-day retention." -ForegroundColor Green
Write-Host 'The task runs while this Windows user is signed in, including while the workstation is locked.'
& $PSCommandPath -Action Status -TaskName $TaskName

