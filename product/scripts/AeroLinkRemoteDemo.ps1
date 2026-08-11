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
    [ValidateSet('Start', 'Stop', 'Status', 'Configure')]
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
$configureConfig = [pscustomobject]@{
    AeroLinkRoot = $moduleRoot
    StatePath = Join-Path $localDemoRoot 'state'
    LogsPath = Join-Path $localDemoRoot 'logs'
    NgrokExecutable = ''
    PublicUrl = ''
    TrafficPolicyPath = ''
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
                Write-Host (Get-AeroLinkRemoteDemoTaskXml -Config $configureConfig)
                exit 0
            }
            'Install' {
                $task = Install-AeroLinkRemoteDemoTask -Config $configureConfig
                $task | Format-List
                Write-Host 'AeroLink remote-demo recovery task installed (current user, no admin, no secrets).'
                exit 0
            }
            'Status' {
                Get-AeroLinkRemoteDemoTaskStatus | Format-List
                exit 0
            }
            'Remove' {
                Remove-AeroLinkRemoteDemoTask | Format-List
                exit 0
            }
        }
    }
}
