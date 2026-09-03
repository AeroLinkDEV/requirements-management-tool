#Requires -Version 5.1
<#
    AeroLink protected remote-demo operator CLI.

    Actions:
      Start      Start (or confirm) the protected remote demo: local production
                 AeroLink plus the policy-backed ngrok tunnel, then prove 401.
      Stop       Stop only the AeroLink-owned ngrok tunnel, or with
                 -IncludeLocalStack the whole remote-demo stack.
      Status     Read-only component status with a final
                 AEROLINK REMOTE DEMO READY / NOT READY verdict.
      Configure  Scheduled-recovery task management:
                 Preview | Install | Status | Remove.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Start', 'Stop', 'Status', 'Configure', 'Reconcile')]
    [string]$Action,
    [ValidateSet('Preview', 'Install', 'Status', 'Remove')]
    [string]$ConfigureAction = 'Preview',
    [switch]$IncludeLocalStack,
    [switch]$Scheduled
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'AeroLinkRemoteDemo.psm1') -Force

$moduleRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$localDemoRoot = Join-Path $env:LOCALAPPDATA 'AeroLink\RemoteDemo'

# The task must be registered against the DEDICATED production source, not against whichever checkout this
# script happens to have been started from. Before #881 this used $moduleRoot unconditionally, which is how
# a recovery task installed from the development checkout came to invoke the development checkout's own
# recovery script — the second half of the 2026-09-03 coupling, and the half a configured AeroLinkRoot alone
# would not have fixed.
$configureConfig = $null
try { $configureConfig = Get-AeroLinkRemoteDemoConfig }
catch {
    $configureConfig = [pscustomobject]@{
        AeroLinkRoot = $moduleRoot
        ProductionSourceRoot = $null
        ProductionSourceReason = "The remote-demo configuration could not be read: $($_.Exception.Message)"
        StatePath = Join-Path $localDemoRoot 'state'
        LogsPath = Join-Path $localDemoRoot 'logs'
        NgrokExecutable = ''
        PublicUrl = ''
        TrafficPolicyPath = ''
    }
}

switch ($Action) {
    'Start' {
        $config = Get-AeroLinkRemoteDemoConfig
        try {
            $result = Start-AeroLinkRemoteDemo -Config $config -Scheduled:$Scheduled
            Write-Host 'AEROLINK REMOTE DEMO READY'
            Write-Host "Public URL: $($result.PublicUrl)"
            Write-Host $result.Detail
            exit 0
        }
        catch {
            $failureRun = New-AeroLinkRemoteDemoRun -Scheduled:$Scheduled
            Write-AeroLinkRemoteDemoLog -Config $config -Run $failureRun -Message "AEROLINK REMOTE DEMO NOT READY: $($_.Exception.Message)"
            Write-Host 'AEROLINK REMOTE DEMO NOT READY' -ForegroundColor Red
            Write-Host $_.Exception.Message
            exit 1
        }
    }
    'Stop' {
        $config = Get-AeroLinkRemoteDemoConfig
        Stop-AeroLinkRemoteDemo -Config $config -IncludeLocalStack:$IncludeLocalStack
        exit 0
    }
    'Reconcile' {
        # Bounded polling: advance the dedicated production source, and restart production into it only when
        # origin/main actually moved. A machine that stays up for weeks should not stay weeks behind.
        $config = Get-AeroLinkRemoteDemoConfig
        try {
            $result = Invoke-AeroLinkProductionSourceReconciliation -Config $config -Scheduled:$Scheduled
            Write-Host "AEROLINK PRODUCTION SOURCE $($result.Action.ToUpperInvariant())"
            Write-Host $result.Detail
            exit 0
        }
        catch {
            Write-Host 'AEROLINK PRODUCTION SOURCE RECONCILIATION FAILED' -ForegroundColor Red
            Write-Host $_.Exception.Message
            exit 1
        }
    }
    'Status' {
        $config = Get-AeroLinkRemoteDemoConfig
        $status = Get-AeroLinkRemoteDemoStatus -Config $config
        $status.Checks | Format-Table -AutoSize
        Write-Host $status.Overall
        exit 0
    }
    'Configure' {
        switch ($ConfigureAction) {
            'Preview' {
                Write-Host "Production source: $($configureConfig.AeroLinkRoot)"
                Write-Host "Resolution: $($configureConfig.ProductionSourceReason)"
                Write-Host ''
                Write-Host (Get-AeroLinkRemoteDemoTaskXml -Config $configureConfig)
                exit 0
            }
            'Install' {
                $task = Install-AeroLinkRemoteDemoTask -Config $configureConfig
                $task | Format-List
                if (-not $task.UnattendedBootRecovery) {
                    Write-Host 'This machine would not accept the unattended (S4U) principal, so recovery happens at' -ForegroundColor Yellow
                    Write-Host 'sign-in rather than at boot. A reboot with nobody logged in will NOT recover the demo.' -ForegroundColor Yellow
                }
                $reconcileTask = Install-AeroLinkReconcileTask -Config $configureConfig
                $reconcileTask | Format-List
                Write-Host 'AeroLink remote-demo recovery and production-source reconciliation tasks installed'
                Write-Host '(current user, no admin, no secrets), both bound to the dedicated production source.'
                exit 0
            }
            'Status' {
                Get-AeroLinkRemoteDemoTaskStatus | Format-List
                Get-AeroLinkRemoteDemoTaskStatus -TaskName 'AeroLinkProductionSourceReconcile' | Format-List
                exit 0
            }
            'Remove' {
                Remove-AeroLinkRemoteDemoTask | Format-List
                Remove-AeroLinkRemoteDemoTask -TaskName 'AeroLinkProductionSourceReconcile' | Format-List
                exit 0
            }
        }
    }
}
