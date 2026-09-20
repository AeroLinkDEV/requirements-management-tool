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
    LogsPath          = '$(Join-Path $tempRoot 'logs')'
    StatePath         = '$(Join-Path $tempRoot 'state')'
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
$moduleText = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'AeroLinkRemoteDemo.psm1'))
Assert-True ($moduleText -match '-NotificationBaseUrl `"\$\(\$Config\.PublicUrl\)`"') 'Remote-demo production helper must pass the protected PublicUrl as the notification-link origin.'

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

$unsafePublicUrlPath = Join-Path $tempRoot 'unsafe-public-url.psd1'
Set-Content -LiteralPath $unsafePublicUrlPath -Value "@{ NgrokExecutable='C:\Tools\ngrok.exe'; PublicUrl='https://operator:password@example.ngrok-free.dev/path?query=1'; TrafficPolicyPath='C:\Tools\policy.yml' }" -Encoding UTF8
$threw = $false
try { Get-AeroLinkRemoteDemoConfig -ConfigPath $unsafePublicUrlPath } catch { $threw = $true }
Assert-True $threw 'Remote-demo PublicUrl with credentials, path, or query must fail closed.'

$pathPublicUrlPath = Join-Path $tempRoot 'path-public-url.psd1'
Set-Content -LiteralPath $pathPublicUrlPath -Value "@{ NgrokExecutable='C:\Tools\ngrok.exe'; PublicUrl='https://example.ngrok-free.dev/aerolink'; TrafficPolicyPath='C:\Tools\policy.yml' }" -Encoding UTF8
$threw = $false
try { Get-AeroLinkRemoteDemoConfig -ConfigPath $pathPublicUrlPath } catch { $threw = $true }
Assert-True $threw 'Remote-demo PublicUrl must be an origin, not a path that would silently change mail routing.'

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

# --- 5. Notification-link origin must be attributable to the current API process ---
New-Item -ItemType Directory -Path $config.StatePath -Force | Out-Null
$runtimeIdentity = { param($C) [pscustomobject]@{ Found = $true; ProcessId = 4242; StartedAt = '2026-08-30T12:34:56.0000000Z'; Detail = 'test runtime' } }
$originState = [pscustomobject]@{
    PublicUrl = $config.PublicUrl
    NotificationBaseUrl = $config.PublicUrl
    LocalApiPid = 4242
    LocalApiStartedAt = '2026-08-30T12:34:56.0000000Z'
}
$originState | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $config.StatePath 'remote-demo-state.json') -Encoding UTF8
$originProof = Test-AeroLinkRemoteDemoNotificationOriginProof -Config $config -RuntimeProbe $runtimeIdentity
Assert-True $originProof.Valid 'Matching public origin plus exact live process identity must be accepted.'

$originState.LocalApiStartedAt = '2026-08-30T08:34:56.0000000-04:00'
$originState | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $config.StatePath 'remote-demo-state.json') -Encoding UTF8
$originProof = Test-AeroLinkRemoteDemoNotificationOriginProof -Config $config -RuntimeProbe $runtimeIdentity
Assert-True $originProof.Valid 'Equivalent process-start instants must match across JSON date materialization and timezone offsets.'

$originState.LocalApiStartedAt = 'not-a-timestamp'
$originState | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $config.StatePath 'remote-demo-state.json') -Encoding UTF8
$originProof = Test-AeroLinkRemoteDemoNotificationOriginProof -Config $config -RuntimeProbe $runtimeIdentity
Assert-True (-not $originProof.Valid) 'An invalid process-start timestamp must fail notification-origin attribution closed.'

$originState.LocalApiStartedAt = '2026-08-30T12:34:56.0000000Z'
$originState.NotificationBaseUrl = 'http://127.0.0.1:5080'
$originState | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $config.StatePath 'remote-demo-state.json') -Encoding UTF8
$originProof = Test-AeroLinkRemoteDemoNotificationOriginProof -Config $config -RuntimeProbe $runtimeIdentity
Assert-True (-not $originProof.Valid) 'A loopback notification origin must not satisfy protected remote-demo readiness.'

$originState.NotificationBaseUrl = $config.PublicUrl
$originState.LocalApiPid = 9999
$originState | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $config.StatePath 'remote-demo-state.json') -Encoding UTF8
$originProof = Test-AeroLinkRemoteDemoNotificationOriginProof -Config $config -RuntimeProbe $runtimeIdentity
Assert-True (-not $originProof.Valid) 'A proof recorded for an old API process must be rejected.'

Remove-Item -LiteralPath (Join-Path $config.StatePath 'remote-demo-state.json') -Force
$originProof = Test-AeroLinkRemoteDemoNotificationOriginProof -Config $config -RuntimeProbe $runtimeIdentity
Assert-True (-not $originProof.Valid) 'AlreadyReady must fail closed when notification-origin proof is missing.'
Assert-True ($moduleText -match "AlreadyReady'[\s\S]+Test-AeroLinkRemoteDemoNotificationOriginProof") 'AlreadyReady must validate durable notification-origin proof before reporting ready.'

# --- 6. Public protection classification (401 required) ---
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

# A disposable qualification installation may point the protection probe at a loopback stand-in edge (the same
# override the authority's tunnel readiness uses); anything else, including a non-loopback override, is ignored.
$savedProtectionEnv = @{ Root = $env:AEROLINK_INSTALLATION_ROOT; Probe = $env:AEROLINK_QUALIFICATION_PROTECTION_PROBE }
try {
    $env:AEROLINK_INSTALLATION_ROOT = 'C:\disposable-installation'
    $env:AEROLINK_QUALIFICATION_PROTECTION_PROBE = 'http://127.0.0.1:5197/'
    $script:capturedProtectionTarget = $null
    $captureLoopback = { param($PublicUrl) $script:capturedProtectionTarget = $PublicUrl; [pscustomobject]@{ StatusCode = 200 } }
    $null = Test-AeroLinkRemoteDemoPublicProtection -Config $config -ProbeScriptBlock $captureLoopback
    Assert-True ($script:capturedProtectionTarget -eq 'http://127.0.0.1:5197/') 'A disposable installation must probe the loopback stand-in edge it names.'
    $env:AEROLINK_QUALIFICATION_PROTECTION_PROBE = 'https://example.com/'
    $script:capturedProtectionTarget = $null
    $captureRemote = { param($PublicUrl) $script:capturedProtectionTarget = $PublicUrl; [pscustomobject]@{ StatusCode = 200 } }
    $null = Test-AeroLinkRemoteDemoPublicProtection -Config $config -ProbeScriptBlock $captureRemote
    Assert-True ($script:capturedProtectionTarget -eq 'https://example.ngrok-free.dev') 'A non-loopback protection override must be ignored.'
}
finally {
    if ($null -eq $savedProtectionEnv.Root) { Remove-Item Env:\AEROLINK_INSTALLATION_ROOT -ErrorAction SilentlyContinue } else { $env:AEROLINK_INSTALLATION_ROOT = $savedProtectionEnv.Root }
    if ($null -eq $savedProtectionEnv.Probe) { Remove-Item Env:\AEROLINK_QUALIFICATION_PROTECTION_PROBE -ErrorAction SilentlyContinue } else { $env:AEROLINK_QUALIFICATION_PROTECTION_PROBE = $savedProtectionEnv.Probe }
}

# --- 7. Scheduled-task XML contains no secrets ---
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
# S4U, not InteractiveToken, since #881: a logon-token task recovers after Sean signs in, which is not what
# "the machine rebooted" means - and on 2026-09-03 nobody was at the keyboard. S4U runs in the operator's own
# account with no interactive session and no stored password. Still the current user, and still not SYSTEM:
# ngrok's agent configuration and credential store are per-user.
Assert-True ($xml -match '<LogonType>S4U</LogonType>') 'Task XML must run unattended in the current user account, not only after an interactive logon.'
Assert-True ($xml -match [regex]::Escape("$env:USERDOMAIN\$env:USERNAME")) 'Task XML must run as the current user.'
Assert-True ($xml -notmatch 'S-1-5-18') 'Task XML must not run as SYSTEM; ngrok configuration and credentials are per-user.'
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

# --- 9. Postgres query probe: an empty answer is a failed answer, never a crash (#1055 TA-2) ---
# Measured in the disposable integration world on 2026-09-17: the first start of a cluster whose `aerolink`
# database does not exist yet ran `SELECT 1 FROM pg_database WHERE datname='aerolink'`, psql exited 0 with no
# rows, and `([string]$value).Trim()` threw InvokeMethodOnNull - because in Windows PowerShell 5.1 a [string]
# cast of $null is still $null. The supported start path must answer "false" there and continue to createdb.
Import-Module (Join-Path $PSScriptRoot 'AeroLinkNativeRunner.psm1') -Force
Assert-True ($null -eq (Get-AeroLinkNativeOutputLine $null)) 'Empty native output must yield $null, not throw.'
Assert-True ($null -eq (Get-AeroLinkNativeOutputLine '')) 'Blank native output must yield $null.'
Assert-True ($null -eq (Get-AeroLinkNativeOutputLine "`r`n")) 'Whitespace-only native output must yield $null.'
Assert-True ((Get-AeroLinkNativeOutputLine "`r`n1`r`n") -eq '1') 'The last non-empty native output line must be returned.'
Assert-True ((Get-AeroLinkNativeOutputLine "connecting`nrow-a`nrow-b") -eq 'row-b') 'The LAST non-empty line must be returned.'

# The default query probe must observe that empty answer without throwing, and the readiness result must be a
# truthful Ready=false. Two quiet stubs stand in for the binaries; nothing here starts a PostgreSQL cluster.
# csc.exe is used directly so the same stub compiles under Windows PowerShell 5.1 and PowerShell 7.
$stubBin = Join-Path $tempRoot 'stub-pg-bin'
New-Item -ItemType Directory -Path $stubBin -Force | Out-Null
$stubAssembly = Join-Path $stubBin 'quiet-probe.exe'
$stubSource = Join-Path $stubBin 'quiet-probe.cs'
Set-Content -LiteralPath $stubSource -Value 'public static class AeroLinkQuietProbeStub { public static int Main(string[] args) { return 0; } }' -Encoding ASCII
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
& $csc @('/nologo', '/target:exe', ('/out:' + $stubAssembly), $stubSource) | Out-Null
Assert-True (Test-Path -LiteralPath $stubAssembly) 'The quiet native probe stub must compile for the readiness contract.'
Copy-Item -LiteralPath $stubAssembly -Destination (Join-Path $stubBin 'psql.exe') -Force
Copy-Item -LiteralPath $stubAssembly -Destination (Join-Path $stubBin 'pg_isready.exe') -Force
$stubConfig = [pscustomobject]@{ LogsPath = (Join-Path $tempRoot 'stub-logs'); AeroLinkRoot = $moduleRoot }
$stubReadiness = Test-AeroLinkRemoteDemoPostgresReady -Config $stubConfig -PostgresBin $stubBin -DatabasePort 55999 -DatabaseName 'aerolink'
Assert-True ($stubReadiness.PgIsreadyOk -eq $true) 'The quiet stub pg_isready must be observed as accepting connections.'
Assert-True ($stubReadiness.QueryOk -eq $false) 'A query that answers with no rows must report QueryOk=false.'
Assert-True ($stubReadiness.Ready -eq $false) 'A query that answers with no rows must report Ready=false, not throw.'
Assert-True ($stubReadiness.Detail -match 'SELECT 1') 'The not-ready detail must name the real query, not a listener.'

# ---------------------------------------------------------------------------------------------------------
# The remote-demo log is SHARED: the transition outer tails it while the delegate and the continuation each
# write it. Two measured facts drive this protocol:
#   * Add-Content (FileShare.Read) denies other writers: #1055 S4 ON failed an attempt whose services were
#     already restored with "being used by another process".
#   * Sharing widely (FileShare.ReadWrite|Delete) is NOT enough: two append handles opened before either write
#     both start at the same end offset, and the second write overwrites the first (Astra's interleaving probe;
#     both writes returned success and only one line survived).
# The logger therefore serialises writers with a bounded retry on a FileShare.Read open, which still admits the
# tail reader and never silently loses or overwrites a line.
# ---------------------------------------------------------------------------------------------------------

# Negative control: reproduce the overwrite interleaving with the WIDE share mode, so the reason for the
# serialised protocol is asserted here rather than assumed.
$interleaveDir = Join-Path $tempRoot 'interleave-log'
New-Item -ItemType Directory -Path $interleaveDir -Force | Out-Null
$interleavePath = Join-Path $interleaveDir 'remote-demo.log'
$wideShare = [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete
$writerA = [IO.File]::Open($interleavePath, [IO.FileMode]::Append, [IO.FileAccess]::Write, $wideShare)
$writerB = [IO.File]::Open($interleavePath, [IO.FileMode]::Append, [IO.FileAccess]::Write, $wideShare)
try {
    $a = [Text.Encoding]::UTF8.GetBytes("writer-A`n"); $writerA.Write($a, 0, $a.Length); $writerA.Flush($true)
    $b = [Text.Encoding]::UTF8.GetBytes("writer-B`n"); $writerB.Write($b, 0, $b.Length); $writerB.Flush($true)
}
finally { $writerA.Dispose(); $writerB.Dispose() }
$interleaved = (Get-Content -LiteralPath $interleavePath -Raw)
Assert-True (-not ($interleaved -match 'writer-A' -and $interleaved -match 'writer-B')) `
    'The wide-share interleaving control must show one append overwriting the other; otherwise the serialised protocol is not justified.'

# The protocol's actual serialisation: a second append handle cannot be opened while one is in flight.
$serialisePath = Join-Path $interleaveDir 'serialised.log'
$first = [IO.File]::Open($serialisePath, [IO.FileMode]::Append, [IO.FileAccess]::Write, [IO.FileShare]::Read)
$secondDenied = $false
try { $null = [IO.File]::Open($serialisePath, [IO.FileMode]::Append, [IO.FileAccess]::Write, [IO.FileShare]::Read) }
catch [IO.IOException] { $secondDenied = $true }
finally { $first.Dispose() }
Assert-True $secondDenied 'A second writer must not open the log while an append is in flight; this is what preserves both lines.'

# The real logger succeeds while the TAIL READER (FileShare.ReadWrite|Delete, the arrangement the transition
# outer actually uses) holds the file.
$sharedLogConfig = [pscustomobject]@{ LogsPath = (Join-Path $tempRoot 'shared-log') }
New-Item -ItemType Directory -Path $sharedLogConfig.LogsPath -Force | Out-Null
$sharedLogPath = Join-Path $sharedLogConfig.LogsPath 'remote-demo.log'
$sharedLine = "reader-sharing-probe $(Get-Date -Format o)"
$tailReader = [IO.File]::Open($sharedLogPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::Read, ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
try { Write-AeroLinkRemoteDemoLog -Config $sharedLogConfig -Message $sharedLine }
finally { $tailReader.Dispose() }
Assert-True ((Get-Content -LiteralPath $sharedLogPath -Raw) -match [regex]::Escape($sharedLine)) `
    'The logger must append while the transition tail reader holds the file.'

# A genuinely incompatible holder denies writing: the logger must refuse within its bound, with a named error,
# and must not change the file.
$blockedDir = Join-Path $tempRoot 'blocked-log'
New-Item -ItemType Directory -Path $blockedDir -Force | Out-Null
$blockedPath = Join-Path $blockedDir 'remote-demo.log'
Set-Content -LiteralPath $blockedPath -Value 'preexisting' -Encoding UTF8
$blocker = [IO.File]::Open($blockedPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
$blockedConfig = [pscustomobject]@{ LogsPath = $blockedDir }
$blockedRefused = $false
$blockedMessage = ''
$started = Get-Date
try { Write-AeroLinkRemoteDemoLog -Config $blockedConfig -Message 'must-not-land' }
catch { $blockedRefused = $true; $blockedMessage = $_.Exception.Message }
finally { $blocker.Dispose() }
$blockedSeconds = ((Get-Date) - $started).TotalSeconds
Assert-True $blockedRefused 'An incompatible holder must make the logger refuse rather than drop the line silently.'
Assert-True ($blockedMessage -match 'NOT written') 'The refusal must be named, not a raw sharing violation.'
Assert-True ($blockedSeconds -lt 20) 'The refusal must be bounded, not an unbounded wait.'
Assert-True ((Get-Content -LiteralPath $blockedPath -Raw) -match 'preexisting') 'A refused append must not change the file.'

# Overlapping REAL appends across processes: every unique record must appear exactly once and no line may be
# partial. This is the behaviour the operator-facing log depends on.
$overlapDir = Join-Path $tempRoot 'overlap-log'
New-Item -ItemType Directory -Path $overlapDir -Force | Out-Null
$appendProbe = Join-Path $tempRoot 'append-probe.ps1'
[IO.File]::WriteAllText($appendProbe, @'
param([string]$ModulePath, [string]$LogsPath, [string]$Marker, [int]$DelayMs = 0)
$ErrorActionPreference = 'Stop'
Import-Module $ModulePath -Force
if ($DelayMs -gt 0) { Start-Sleep -Milliseconds $DelayMs }
Write-AeroLinkRemoteDemoLog -Config ([pscustomobject]@{ LogsPath = $LogsPath }) -Message $Marker
'@, (New-Object Text.UTF8Encoding($false)))
$markers = @(1..12 | ForEach-Object { "overlap-$_-" + [guid]::NewGuid().ToString('N') })
$appendProcs = @()
foreach ($marker in $markers) {
    $appendProcs += Start-Process -FilePath (Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe') `
        -ArgumentList ('-NoProfile -ExecutionPolicy Bypass -File "' + $appendProbe + '" -ModulePath "' + $modulePath + '" -LogsPath "' + $overlapDir + '" -Marker ' + $marker + ' -DelayMs ' + (Get-Random -Minimum 0 -Maximum 40)) `
        -WindowStyle Hidden -PassThru
}
foreach ($proc in $appendProcs) { $proc.WaitForExit(60000) | Out-Null }
$overlapText = Get-Content -LiteralPath (Join-Path $overlapDir 'remote-demo.log') -Raw
foreach ($marker in $markers) {
    $occurrences = ([regex]::Matches($overlapText, [regex]::Escape($marker))).Count
    Assert-True ($occurrences -eq 1) "Overlapping append '$marker' must appear exactly once (found $occurrences)."
}
$partialLines = @(($overlapText -split "`r?`n") | Where-Object { $_ -and $_ -notmatch '^\d{4}-\d{2}-\d{2}T[^]]+\] overlap-\d+-[0-9a-f]{32}$' })
Assert-True ($partialLines.Count -eq 0) "Overlapping appends produced $($partialLines.Count) partial or interleaved line(s)."

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Host "FAIL: $_" -ForegroundColor Red }
    Write-Host "Remote-demo operator regression FAILED ($($failures.Count) failure(s))." -ForegroundColor Red
    exit 1
}

Write-Host 'Remote-demo operator regression passed.' -ForegroundColor Green
exit 0
