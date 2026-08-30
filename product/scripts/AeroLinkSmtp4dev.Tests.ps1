#Requires -Version 5.1
<#
    Static contract coverage for the optional local SMTP catcher. It intentionally never starts a process,
    downloads a tool, connects to SMTP, or touches AeroLink product state.
#>
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\\..')).Path
$scriptPath = Join-Path $PSScriptRoot 'AeroLinkSmtp4dev.ps1'
$text = [IO.File]::ReadAllText($scriptPath)
$failures = [Collections.Generic.List[string]]::new()

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { $script:failures.Add($Message) }
}

Assert-True ($text -match '\$version = ''3\.15\.0''') 'smtp4dev version must remain pinned to 3.15.0.'
Assert-True ($text -match 'Rnwood\.Smtp4dev --version \$version --tool-path') 'smtp4dev acquisition must install the pinned package into the owned tool path.'
Assert-True ($text -match '\.store\\rnwood\.smtp4dev\\\$version.*Rnwood\.Smtp4dev\.exe') 'smtp4dev must launch and own the pinned package executable rather than assuming dotnet creates an exe shim.'
Assert-True ($text -match "Name='Rnwood\.Smtp4dev\.exe'") 'smtp4dev ownership must match the installed package process name.'
Assert-True (([regex]::Matches($text, '@\(Get-OwnedSmtp4devProcess\)')).Count -eq 4) 'Every owned-process result must be normalized to an array under Windows PowerShell 5.1.'
Assert-True ($text -match 'AddSeconds\(10\)') 'smtp4dev stop must use a bounded ownership and listener shutdown wait.'
Assert-True ($text -match '\$env:LOCALAPPDATA') 'smtp4dev data must live under LOCALAPPDATA rather than the repository.'
Assert-True ($text -match '\$root = Join-Path \$env:LOCALAPPDATA') 'smtp4dev tool and message store must be rooted under LOCALAPPDATA.'
Assert-True ($text -match "ValidateSet\('Start', 'Status', 'Stop'\)") 'smtp4dev must expose only the explicit start/status/stop actions.'
Assert-True ($text -match '127\.0\.0\.1') 'smtp4dev must bind the documented loopback inbox.'
Assert-True ($text -match '--allowremoteconnections- --bindaddress 127\.0\.0\.1 --disableipv6\+') 'smtp4dev SMTP must be explicitly loopback-only, including IPv6.'
Assert-True ($text -match '--imapport= --pop3port= --relaysmtpserver=') 'smtp4dev must disable unused mail protocols and outbound relay.'
Assert-True ($text -match '--locksettings\+') 'smtp4dev runtime security settings must not be mutable through its web UI.'
Assert-True ($text -match 'Refusing to attach to or replace another process') 'smtp4dev must not take over a port owned by another process.'
Assert-True ($text -match 'AddSeconds\(15\)') 'smtp4dev startup must use a bounded readiness wait rather than a fixed startup delay.'
Assert-True ($text -match '\$smtpReady -and \$webReady') 'smtp4dev readiness must prove both the SMTP listener and inbox web listener.'

foreach ($launcher in @('START_AEROLINK_SMTP4DEV.bat', 'AEROLINK_SMTP4DEV_STATUS.bat', 'STOP_AEROLINK_SMTP4DEV.bat', 'START_AEROLINK_EMAIL_DEMO.bat')) {
    $launcherPath = Join-Path $root $launcher
    Assert-True (Test-Path -LiteralPath $launcherPath -PathType Leaf) "Missing root SMTP operator launcher: $launcher"
    if (Test-Path -LiteralPath $launcherPath -PathType Leaf) {
        $launcherText = [IO.File]::ReadAllText($launcherPath)
        Assert-True ($launcherText -match "`r`n" -and $launcherText -notmatch "(?<!`r)`n") "$launcher must preserve CRLF line endings."
        Assert-True ($launcherText -match 'PSModulePath') "$launcher must isolate Windows PowerShell module resolution."
    }
}
$emailDemo = [IO.File]::ReadAllText((Join-Path $root 'START_AEROLINK_EMAIL_DEMO.bat'))
Assert-True ($emailDemo -match 'Notifications__Smtp__Host=127\.0\.0\.1') 'Email demo must point SMTP at loopback smtp4dev.'
Assert-True ($emailDemo -match 'NotificationBaseUrl "http://127\.0\.0\.1:5080"') 'Email demo must give mail the exact loopback public origin.'
Assert-True ($emailDemo -match 'NotificationBaseUrl "http://127\.0\.0\.1:5080" %\*') 'Email demo must forward -Shared to the production launcher.'
$productionLauncher = [IO.File]::ReadAllText((Join-Path $root 'product\scripts\Start-AeroLinkProduction.ps1'))
Assert-True ($productionLauncher -match '\$effectiveNotificationBaseUrl = "http://\$\{lan\}:5080"') 'Shared production mode must replace the local email-demo origin with its LAN origin.'

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Host "FAIL: $_" -ForegroundColor Red }
    exit 1
}
Write-Host 'smtp4dev operator contract passed.' -ForegroundColor Green
