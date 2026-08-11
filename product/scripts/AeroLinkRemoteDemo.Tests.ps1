#Requires -Version 5.1
<#
    Deterministic regression coverage for the AeroLink remote-demo operator mode.
    Self-contained (no Pester dependency). It exercises configuration validation,
    ngrok launch-command construction, process ownership matching, idempotent
    start decisions, the 401-required public protection classification, and
    scheduled-task XML construction without secrets.

    ngrok itself is NOT exercised here; attended Windows qualification against the
    real public endpoint is documented separately in docs/REMOTE_DEMO_OPERATOR.md.
#>
$ErrorActionPreference = 'Stop'

$modulePath = Join-Path $PSScriptRoot 'AeroLinkRemoteDemo.psm1'
Import-Module $modulePath -Force

$moduleRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("aerolink-remote-demo-tests-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null

$failures = [System.Collections.Generic.List[string]]::new()

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { $script:failures.Add($Message) }
}

function New-ValidConfigFile([string]$Path) {
    @"
@{
    NgrokExecutable   = 'C:\Tools\ngrok.exe'
    PublicUrl         = 'https://example.ngrok-free.dev'
    TrafficPolicyPath = 'C:\Tools\policy.yml'
    Upstream          = 'http://127.0.0.1:5080'
    LocalApiBaseUri   = 'http://127.0.0.1:5080'
    AeroLinkRoot      = '$moduleRoot'
}
"@ | Set-Content -LiteralPath $Path -Encoding UTF8
}

# --- 1. Configuration validation ---
$validConfigPath = Join-Path $tempRoot 'valid.psd1'
New-ValidConfigFile -Path $validConfigPath
$config = Get-AeroLinkRemoteDemoConfig -ConfigPath $validConfigPath
Assert-True ($config.NgrokExecutable -eq 'C:\Tools\ngrok.exe') 'Valid config did not load NgrokExecutable.'
Assert-True ($config.PublicUrl -eq 'https://example.ngrok-free.dev') 'Valid config did not load PublicUrl.'
Assert-True ($config.Upstream -eq 'http://127.0.0.1:5080') 'Valid config did not apply the default Upstream.'

$missingConfig = Join-Path $tempRoot 'missing.psd1'
$threw = $false
try { Get-AeroLinkRemoteDemoConfig -ConfigPath $missingConfig } catch { $threw = $true }
Assert-True $threw 'Missing config file should fail closed.'

$malformedPath = Join-Path $tempRoot 'malformed.psd1'
Set-Content -LiteralPath $malformedPath -Value '@{ not valid' -Encoding UTF8
$threw = $false
try { Get-AeroLinkRemoteDemoConfig -ConfigPath $malformedPath } catch { $threw = $true }
Assert-True $threw 'Malformed config file should fail closed.'

$unknownKeyPath = Join-Path $tempRoot 'unknown-key.psd1'
Set-Content -LiteralPath $unknownKeyPath -Value "@{ NgrokExecutable='C:\Tools\ngrok.exe'; PublicUrl='https://example.ngrok-free.dev'; TrafficPolicyPath='C:\Tools\policy.yml'; Password='hunter2' }" -Encoding UTF8
$threw = $false
try { Get-AeroLinkRemoteDemoConfig -ConfigPath $unknownKeyPath } catch { $threw = $true }
Assert-True $threw 'Config with an unknown (secret-looking) key should fail closed.'

$missingKeyPath = Join-Path $tempRoot 'missing-key.psd1'
Set-Content -LiteralPath $missingKeyPath -Value "@{ NgrokExecutable='C:\Tools\ngrok.exe'; PublicUrl='https://example.ngrok-free.dev' }" -Encoding UTF8
$threw = $false
try { Get-AeroLinkRemoteDemoConfig -ConfigPath $missingKeyPath } catch { $threw = $true }
Assert-True $threw 'Config missing a required key should fail closed.'

# --- 2. ngrok launch arguments contain the contract and no secrets ---
$arguments = Get-AeroLinkRemoteDemoNgrokArguments -Config $config
$joined = $arguments -join ' '
Assert-True ($joined -match 'http://127\.0\.0\.1:5080') 'Launch arguments must contain the upstream.'
Assert-True ($joined -match 'https://example\.ngrok-free\.dev') 'Launch arguments must contain the public URL.'
Assert-True ($joined -match '--traffic-policy-file') 'Launch arguments must contain the traffic-policy flag.'
Assert-True ($joined -match 'C:\\Tools\\policy\.yml') 'Launch arguments must contain the traffic policy path.'
Assert-True ($joined -notmatch 'hunter2|SUPERSECRET|authtoken') 'Launch arguments must not contain secrets.'

# --- 3. Process ownership matching ---
$fakeProcesses = @(
    [pscustomobject]@{ ProcessId = 101; ExecutablePath = 'C:\Tools\ngrok.exe'; CommandLine = '"C:\Tools\ngrok.exe" http http://127.0.0.1:5080 --url https://example.ngrok-free.dev --traffic-policy-file C:\Tools\policy.yml --log stdout' },
    [pscustomobject]@{ ProcessId = 102; ExecutablePath = 'C:\Other\ngrok.exe'; CommandLine = '"C:\Other\ngrok.exe" http http://127.0.0.1:5080 --url https://example.ngrok-free.dev --traffic-policy-file C:\Tools\policy.yml --log stdout' },
    [pscustomobject]@{ ProcessId = 103; ExecutablePath = 'C:\Tools\ngrok.exe'; CommandLine = '"C:\Tools\ngrok.exe" http http://127.0.0.1:5080' }
)
$ownership = Get-AeroLinkRemoteDemoNgrokProcess -Config $config -ProcessInfos $fakeProcesses
Assert-True (@($ownership.Owned).Count -eq 1 -and @($ownership.Owned)[0].ProcessId -eq 101) 'Ownership should match only the exact executable + contract process.'
Assert-True (@($ownership.Mismatched).Count -eq 2) 'Mismatched executable/contract processes must be reported, not owned.'

# --- 4. Idempotent/fail-closed start decisions ---
$decision = Get-AeroLinkRemoteDemoStartDecision -LocalReady $false -OwnedProcessPresent $false -Protected $false -ProbeStatusCode 404
Assert-True ($decision.Decision -eq 'BlockedLocalNotReady') 'Not-locally-ready must block start.'
$decision = Get-AeroLinkRemoteDemoStartDecision -LocalReady $true -OwnedProcessPresent $true -Protected $true -ProbeStatusCode 401
Assert-True ($decision.Decision -eq 'AlreadyReady') 'Owned + protected must be AlreadyReady (idempotent).'
$decision = Get-AeroLinkRemoteDemoStartDecision -LocalReady $true -OwnedProcessPresent $true -Protected $false -ProbeStatusCode 400
Assert-True ($decision.Decision -eq 'BlockedOwnedNotProtected') 'Owned but not protected must block a second tunnel.'
$decision = Get-AeroLinkRemoteDemoStartDecision -LocalReady $true -OwnedProcessPresent $false -Protected $false -ProbeStatusCode 404
Assert-True ($decision.Decision -eq 'CanStart') 'Free endpoint (404) must allow start.'
$decision = Get-AeroLinkRemoteDemoStartDecision -LocalReady $true -OwnedProcessPresent $false -Protected $false -ProbeStatusCode 200
Assert-True ($decision.Decision -eq 'BlockedForeignResponder') 'Foreign 2xx responder must block start.'
$decision = Get-AeroLinkRemoteDemoStartDecision -LocalReady $true -OwnedProcessPresent $false -Protected $false -ProbeStatusCode $null
Assert-True ($decision.Decision -eq 'CanStart') 'Unreachable probe must still allow start; post-start probe enforces 401.'

# --- 5. Public protection classification (401 required) ---
$stub401 = {
    param($PublicUrl)
    $response = [pscustomobject]@{ StatusCode = 401 }
    $exception = New-Object System.Exception('unauthorized')
    $exception | Add-Member -NotePropertyName Response -NotePropertyValue $response
    throw $exception
}
$probe = Test-AeroLinkRemoteDemoPublicProtection -Config $config -ProbeScriptBlock $stub401
Assert-True ($probe.Protected -eq $true -and $probe.StatusCode -eq 401) '401 must be classified as protected.'

$stub200 = { param($PublicUrl) [pscustomobject]@{ StatusCode = 200 } }
$probe = Test-AeroLinkRemoteDemoPublicProtection -Config $config -ProbeScriptBlock $stub200
Assert-True ($probe.Protected -eq $false -and $probe.StatusCode -eq 200) '2xx must not be classified as protected.'

$stub400 = {
    param($PublicUrl)
    $response = [pscustomobject]@{ StatusCode = 400 }
    $exception = New-Object System.Exception('bad request')
    $exception | Add-Member -NotePropertyName Response -NotePropertyValue $response
    throw $exception
}
$probe = Test-AeroLinkRemoteDemoPublicProtection -Config $config -ProbeScriptBlock $stub400
Assert-True ($probe.Protected -eq $false -and $probe.StatusCode -eq 400) 'AeroLink 400 must not be classified as protected.'

$stubUnreachable = { param($PublicUrl) throw 'network down' }
$probe = Test-AeroLinkRemoteDemoPublicProtection -Config $config -ProbeScriptBlock $stubUnreachable
Assert-True ($probe.Protected -eq $false -and $null -eq $probe.StatusCode) 'Unreachable endpoint must not be classified as protected.'

# --- 6. Scheduled-task XML contains no secrets ---
$taskConfig = [pscustomobject]@{
    AeroLinkRoot = $moduleRoot
    StatePath = Join-Path $tempRoot 'state'
    LogsPath = Join-Path $tempRoot 'logs'
}
$xml = Get-AeroLinkRemoteDemoTaskXml -Config $taskConfig
Assert-True ($xml -match 'AeroLinkRemoteDemoRecovery') 'Task XML must name the recovery task.'
Assert-True ($xml -match 'encoding="UTF-16"') 'Task XML must declare UTF-16 encoding.'
Assert-True ($xml -match 'LogonTrigger') 'Task XML must use a logon trigger.'
Assert-True ($xml -match 'StartWhenAvailable') 'Task XML must enable StartWhenAvailable.'
Assert-True ($xml -match 'InteractiveToken') 'Task XML must run as the interactive current user.'
Assert-True ($xml -match 'LeastPrivilege') 'Task XML must run with least privilege.'
Assert-True ($xml -match 'AeroLinkRemoteDemo\.ps1" -Action Start -Scheduled') 'Task XML must invoke the same tested start implementation.'
Assert-True ($xml -notmatch 'SUPERSECRET|hunter2|AeroLink!2026|authtoken') 'Task XML must not contain secrets.'

# The written file must use the encoding its declaration promises (UTF-16 LE with BOM),
# or schtasks rejects the XML as malformed.
$savedXmlPath = Join-Path $tempRoot 'saved-task.xml'
Save-AeroLinkRemoteDemoTaskXml -Config $taskConfig -Path $savedXmlPath
$bytes = [System.IO.File]::ReadAllBytes($savedXmlPath)
Assert-True ($bytes.Length -ge 2 -and $bytes[0] -eq 0xFF -and $bytes[1] -eq 0xFE) 'Task XML file must be written as UTF-16 LE with BOM.'
$savedText = [System.IO.File]::ReadAllText($savedXmlPath, [System.Text.Encoding]::Unicode)
$parsed = $null
try { $parsed = [xml]$savedText } catch { }
Assert-True ($null -ne $parsed -and $parsed.Task.Triggers.LogonTrigger -ne $null) 'Task XML file must parse as well-formed task XML.'

# Operator log lines must carry a parseable ISO-8601 (round-trip) timestamp.
$logConfig = [pscustomobject]@{ LogsPath = Join-Path $tempRoot 'log-test' }
Write-AeroLinkRemoteDemoLog -Config $logConfig -Message 'log-format-probe'
$logLine = Get-Content -LiteralPath (Join-Path $logConfig.LogsPath 'remote-demo.log') | Select-Object -First 1
$timestampText = ($logLine -split ' ', 2)[0]
$parsedTimestamp = $null
try {
    $parsedTimestamp = [datetime]::Parse($timestampText, [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::RoundtripKind)
} catch { }
Assert-True ($null -ne $parsedTimestamp) "Operator log timestamp must parse as ISO-8601; got '$timestampText'."
Assert-True ($logLine -match 'log-format-probe') 'Operator log line must contain the message.'

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Host "FAIL: $_" -ForegroundColor Red }
    Write-Host "Remote-demo operator regression FAILED ($($failures.Count) failure(s))." -ForegroundColor Red
    exit 1
}

Write-Host 'Remote-demo operator regression passed.' -ForegroundColor Green
exit 0
