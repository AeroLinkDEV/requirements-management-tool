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
# Exercise only the actual shared-origin condition and value. The launcher itself owns processes,
# network and persistent state, so never invoke it here. Reject executable AST nodes before evaluation.
function Get-SharedNotificationOrigin([string]$Source, [string]$Lan, [int]$ApiPort) {
    $tokens = $null; $parseErrors = $null
    $ast = [Management.Automation.Language.Parser]::ParseInput($Source, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count -gt 0) { throw 'The production launcher must parse before its shared origin can be checked.' }
    $candidates = @($ast.FindAll({ param($node)
        $node -is [Management.Automation.Language.AssignmentStatementAst] -and
        $node.Left -is [Management.Automation.Language.VariableExpressionAst] -and
        $node.Left.VariablePath.UserPath -eq 'effectiveNotificationBaseUrl'
    }, $true) | ForEach-Object {
        $assignment = $_; $parent = $_.Parent
        while ($parent -and $parent -isnot [Management.Automation.Language.IfStatementAst]) { $parent = $parent.Parent }
        if ($parent) {
            foreach ($clause in $parent.Clauses) {
                $contained = @($clause.Item2.FindAll({ param($node) $node -eq $assignment }, $true))
                $shared = @($clause.Item1.FindAll({ param($node)
                    $node -is [Management.Automation.Language.VariableExpressionAst] -and $node.VariablePath.UserPath -eq 'Shared'
                }, $true))
                if ($contained.Count -eq 1 -and $shared.Count -gt 0) {
                    [pscustomobject]@{ Condition = $clause.Item1; Value = $assignment.Right }
                }
            }
        }
    })
    if ($candidates.Count -ne 1) { throw 'Expected one notification-origin assignment in the shared production branch.' }
    $candidate = $candidates[0]
    $allowedNodes = @('PipelineAst', 'CommandExpressionAst', 'VariableExpressionAst', 'ExpandableStringExpressionAst',
        'StringConstantExpressionAst', 'ConstantExpressionAst', 'SubExpressionAst', 'StatementBlockAst',
        'MemberExpressionAst', 'ParenExpressionAst', 'BinaryExpressionAst', 'UnaryExpressionAst')
    foreach ($expression in @($candidate.Condition, $candidate.Value)) {
        foreach ($node in $expression.FindAll({ param($node) $true }, $true)) {
            if ($node.GetType().Name -notin $allowedNodes) { throw "Unsafe shared-origin expression node: $($node.GetType().Name)." }
            if ($node -is [Management.Automation.Language.VariableExpressionAst] -and
                (-not $node.VariablePath.IsUnqualified -or $node.VariablePath.UserPath -notin @('Shared', 'lan', 'endpoints', 'NotificationBaseUrl', 'true', 'false', 'null'))) {
                throw 'The shared-origin expression must use only the supplied fixture values.'
            }
            if ($node -is [Management.Automation.Language.MemberExpressionAst] -and
                ($node.Static -or $node.Expression -isnot [Management.Automation.Language.VariableExpressionAst] -or
                $node.Expression.VariablePath.UserPath -ne 'endpoints' -or
                $node.Member -isnot [Management.Automation.Language.StringConstantExpressionAst] -or $node.Member.Value -ne 'ApiPort')) {
                throw 'The shared-origin expression may only read the resolved API port.'
            }
        }
    }
    $Shared = $true
    $endpoints = [pscustomobject]@{ ApiPort = $ApiPort }
    $NotificationBaseUrl = 'http://127.0.0.1:5080'
    if (& ([scriptblock]::Create($candidate.Condition.Extent.Text))) {
        return & ([scriptblock]::Create($candidate.Value.Extent.Text))
    }
    return $NotificationBaseUrl
}
try {
    foreach ($case in @(
        @{ ApiPort = 5080; Expected = 'http://192.0.2.44:5080' },
        @{ ApiPort = 55181; Expected = 'http://192.0.2.44:55181' }
    )) {
        $sharedOrigin = Get-SharedNotificationOrigin $productionLauncher '192.0.2.44' $case.ApiPort
        Assert-True ($sharedOrigin -ceq $case.Expected) "Shared production mode must replace the local email-demo origin with the LAN host and resolved API port $($case.ApiPort)."
    }
}
catch { Assert-True $false "Shared notification origin could not be checked safely: $($_.Exception.Message)" }

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Host "FAIL: $_" -ForegroundColor Red }
    exit 1
}
Write-Host 'smtp4dev operator contract passed.' -ForegroundColor Green
