#Requires -Version 5.1
<#
    The action of the brokered first-deployment task. Never an operator entry point.

    Initialize-AeroLinkHomeProcessControl.ps1 (elevated, once) registers a Limited S4U task whose only action is this
    script, stages ONE request, and starts the task. This process is then the OUTER authority of a HOME transition in
    the supported scheduled context: it qualifies that context about itself, owns the lease, runs admission over
    every prior attempt, and runs the first deployment as a contained attempt - teardown and advance by the delegate,
    restoration by a continuation from the advanced source, every surviving service by launch request - before it
    verifies the result itself.

    It never tears anything down before its own context is qualified. The result is published bound to the request id,
    and the process exit code is that result's exit code, so setup can require both to agree for the SAME invocation.
#>
[CmdletBinding()]
param([Parameter(Mandatory)][string]$InstallationRoot)
$ErrorActionPreference = 'Stop'
$directory = Join-Path $InstallationRoot 'bootstrap\first-deployment'
$requestPath = Join-Path $directory 'request.json'
$code = 1
$resultPath = $null
$result = [ordered]@{ decision = 'HostError'; exitCode = 1; detail = 'the deployment did not reach a result'; attempts = @() }
$lease = $null
try {
    Import-Module (Join-Path $PSScriptRoot 'AeroLinkTransitionKernel.psm1') -DisableNameChecking
    $request = Read-AeroLinkJsonRecord -Path $requestPath
    if ($request.Class -ne 'Valid' -or -not $request.Value.requestId) { exit 2 }
    $resultPath = Join-Path $directory ("$([string]$request.Value.requestId).result.json")
    $result['requestId'] = [string]$request.Value.requestId
    $result['pid'] = $PID
    $token = Get-AeroLinkTokenFacts
    $result['token'] = $token
    if (-not $token.Readable -or $token.LogonSids -notcontains 'S-1-5-3') { throw 'The first deployment runs only in the scheduled batch logon context it was registered for.' }
    # The per-user configuration the setup operator uses: the remote-demo and production-source configurations live under it.
    $env:LOCALAPPDATA = [string]$request.Value.configurationProfile

    Import-Module (Join-Path $PSScriptRoot 'AeroLinkTransition.psm1')
    Import-Module (Join-Path $PSScriptRoot 'AeroLinkProductionSource.psm1')
    Import-Module (Join-Path $PSScriptRoot 'AeroLinkRemoteDemo.psm1')
    $configuration = Get-AeroLinkProductionSourceConfig
    if ([IO.Path]::GetFullPath($configuration.SourceRoot).TrimEnd('\') -ine [IO.Path]::GetFullPath([string]$request.Value.sourceRoot).TrimEnd('\')) {
        throw 'The staged request names a different dedicated production source than this profile configures.'
    }
    $demoConfig = $null
    if (Test-Path -LiteralPath (Get-AeroLinkRemoteDemoConfigPath) -PathType Leaf) { $demoConfig = Get-AeroLinkRemoteDemoConfig }
    $lease = Enter-AeroLinkTransition -InstallationRoot $InstallationRoot -Policy Preserve
    if (-not $lease.Owner) { throw 'The first deployment must own the HOME transition lease.' }
    $transition = Invoke-AeroLinkHomeTransitionOuter -InstallationRoot $InstallationRoot -Lease $lease -Operation FirstDeployment `
        -SourceRoot $configuration.SourceRoot -Config $demoConfig -Policy Preserve -StreamToHost
    $code = [int]$transition.ExitCode
    $result = [ordered]@{ requestId = [string]$request.Value.requestId; pid = $PID; decision = $transition.Decision; exitCode = $code; restored = $transition.Restored
        restorationRequired = $transition.RestorationRequired; detail = $transition.Detail; attempts = @($transition.Attempts); token = $token }
}
catch {
    $result['detail'] = $_.Exception.Message
    $code = 1
}
finally {
    if ($lease) { try { Exit-AeroLinkTransition -Lease $lease } catch { $code = 1; $result['decision'] = 'HostError'; $result['detail'] = "lease release failed: $($_.Exception.Message)" } }
    $result['exitCode'] = $code
    $result['at'] = (Get-Date).ToUniversalTime().ToString('o')
    if ($resultPath) { try { Publish-AeroLinkJsonAtomic -Path $resultPath -Value $result } catch { $code = 1 } }
}
exit $code
