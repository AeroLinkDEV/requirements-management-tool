[CmdletBinding()]
# DiagnosticsDirectory receives a copy of the owned logs when qualification fails. Under GitHub Actions its default
# is the repository's TestResults directory, which the Domain job uploads on failure (the temp roots are never
# uploaded); a local run defaults to a directory inside its own retained temp root.
param([string]$PostgresBin, [int]$ExistingPostgresPort = 0, [string]$DiagnosticsDirectory)

$ErrorActionPreference = 'Stop'
if (-not (Get-Module -Name AeroLinkProcessEnvironment)) {
    Import-Module (Join-Path $PSScriptRoot 'AeroLinkProcessEnvironment.psm1')
}
$productRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$repositoryRoot = (Resolve-Path (Join-Path $productRoot '..')).Path
if (-not $PostgresBin) { $PostgresBin = Join-Path $productRoot '.local\postgresql\pgsql\bin' }
$PostgresBin = [IO.Path]::GetFullPath($PostgresBin)
foreach ($name in 'initdb.exe','pg_ctl.exe','createdb.exe','psql.exe') {
    if (-not (Test-Path -LiteralPath (Join-Path $PostgresBin $name) -PathType Leaf)) { throw "Disposable PostgreSQL qualification requires $name under $PostgresBin." }
}
if ($ExistingPostgresPort -eq 54329 -or $ExistingPostgresPort -lt 0 -or $ExistingPostgresPort -gt 65535) {
    throw 'Existing restore qualification PostgreSQL must be a non-persistent loopback port; 54329 is forbidden.'
}

# #1183: a port-0 bind hands out a port from Windows' dynamic (ephemeral) range, 49152-65535 by default. The
# port is released at once and bound again only after the restore work, and in that window any outbound
# connection, including this scenario's own psql and Npgsql sessions, can take it as its source port, so the
# validation API's bind fails and it exits during startup. Outbound source ports only ever come from the dynamic
# range, so a free port below it can be lost only to another listener. Ports the OS has excluded (Hyper-V and
# similar reservations) refuse the probe bind and are skipped.
function Get-FreePort {
    $dynamicStart = 49152
    $range = netsh int ipv4 show dynamicport tcp 2>$null | Select-String -Pattern 'Start Port\s*:\s*(\d+)'
    if ($range) { $dynamicStart = [int]$range.Matches[0].Groups[1].Value }
    $low = 20000; $high = [Math]::Min(40000, $dynamicStart)
    for ($attempt = 0; $attempt -lt 200; $attempt++) {
        $candidate = Get-Random -Minimum $low -Maximum $high
        if ($candidate -eq 54329) { continue }
        $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $candidate)
        try { $listener.Start(); return $candidate }
        catch [Net.Sockets.SocketException] { continue }
        finally { $listener.Stop() }
    }
    throw "No free loopback port was found in $low-$($high - 1), below the dynamic port range that starts at $dynamicStart."
}
function Invoke-Checked([string]$File, [string[]]$Arguments) {
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$([IO.Path]::GetFileName($File)) failed with exit code $LASTEXITCODE." }
}
function Invoke-QualificationSql([string]$Database, [string]$Sql) {
    # Windows PowerShell's native argument marshalling strips embedded SQL identifier quotes.
    # Send SQL as stdin so PostgreSQL receives the controlled mixed-case EF names unchanged.
    $result = $Sql | & (Join-Path $PostgresBin 'psql.exe') -h 127.0.0.1 -p $pgPort -U postgres -d $Database -v ON_ERROR_STOP=1 -tA -f -
    if ($LASTEXITCODE -ne 0) { throw "Qualification SQL failed in owned database '$Database'." }
    return ($result -join "`n").Trim()
}
function Assert-OwnedQualificationPath([string]$Path, [string]$Parent) {
    $resolved = [IO.Path]::GetFullPath($Path)
    $prefix = [IO.Path]::GetFullPath($Parent).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw "Qualification path escaped its owned root: $resolved" }
    return $resolved
}
# #1498: a hosted run waited the whole readiness budget for the seed API and threw a message naming only a
# stderr path on the runner's temp drive, which no job uploads, so the cause was lost. The wait now records
# every attempt and puts the reason in the thrown message itself: whether the process exited (and its code),
# what each readiness probe saw, the listener on the port, and the last lines of both logs. The budget and
# the readiness condition are unchanged: an HTTP 200 from /health/ready, abandoned early only on exit.
function Get-QualificationLogTail([string]$Path, [int]$Lines = 15) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return '(no file)' }
    $tail = @(Get-Content -LiteralPath $Path -Tail $Lines -ErrorAction SilentlyContinue | ForEach-Object { if ($_.Length -gt 400) { $_.Substring(0,400) + '...' } else { $_ } })
    if ($tail.Count -eq 0) { return '(empty)' }
    return ($tail -join [Environment]::NewLine)
}
function Wait-QualificationApiReady {
    param([Parameter(Mandatory)][Diagnostics.Process]$Process, [Parameter(Mandatory)][int]$Port, [Parameter(Mandatory)][int]$Attempts,
        [Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][string]$StdoutPath, [Parameter(Mandatory)][string]$StderrPath,
        [Parameter(Mandatory)][string]$AttemptLogPath)
    $url = "http://127.0.0.1:$Port/health/ready"
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $history = New-Object System.Collections.Generic.List[object]
    for ($attempt=0;$attempt -lt $Attempts;$attempt++) {
        if ($Process.HasExited) { break }
        $startedMs = $clock.ElapsedMilliseconds
        try {
            $response = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 2
            $outcome = "HTTP $([int]$response.StatusCode)"
            if ($response.StatusCode -eq 200) { return [pscustomobject]@{ Attempts=$attempt+1; ElapsedMs=$clock.ElapsedMilliseconds } }
        } catch {
            $failure = $_.Exception
            if ($failure -is [Net.WebException] -and $failure.Response) { $outcome = "HTTP $([int]$failure.Response.StatusCode)" }
            elseif ($failure -is [Net.WebException]) { $outcome = "$($failure.Status): $($failure.Message)" }
            else { $outcome = "$($failure.GetType().Name): $($failure.Message)" }
        }
        # Under Windows PowerShell a refused loopback connect surfaces as a 2-second timeout, so a port with no
        # listener and a listener that never answers look alike here; the listener check below tells them apart.
        # The startup seeders run before app.Run() and do not log, so the API's CPU time is what separates a slow
        # seeder (CPU keeps rising) from a stuck one (CPU flat) while nothing listens yet.
        $cpuMs = try { [long]$Process.TotalProcessorTime.TotalMilliseconds } catch { $null }
        $history.Add([pscustomobject]@{ attempt=$attempt+1; startedMs=$startedMs; durationMs=$clock.ElapsedMilliseconds-$startedMs; cpuMs=$cpuMs; outcome=$outcome.TrimEnd('.') })
        Start-Sleep -Milliseconds 500
    }
    $elapsed = [Math]::Round($clock.Elapsed.TotalSeconds,1)
    if ($Process.HasExited) {
        $Process.WaitForExit()
        $state = "process $($Process.Id) exited with code $($Process.ExitCode)"
        try { $state += " at $($Process.ExitTime.ToUniversalTime().ToString('HH:mm:ss.fff'))Z" } catch { }
    } else {
        $state = "process $($Process.Id) is still running"
        try {
            $wall = [Math]::Round(([DateTime]::Now - $Process.StartTime).TotalSeconds,1)
            $cpu = [Math]::Round($Process.TotalProcessorTime.TotalSeconds,1)
            $state += " with ${cpu}s CPU time in ${wall}s wall time since start"
            $recent = @($history | Select-Object -Last 10 | Where-Object { $null -ne $_.cpuMs })
            if ($recent.Count -ge 2) {
                $cpuDelta = [Math]::Round(($recent[-1].cpuMs - $recent[0].cpuMs) / 1000.0,1)
                $wallDelta = [Math]::Round(($recent[-1].startedMs - $recent[0].startedMs) / 1000.0,1)
                $state += " (+${cpuDelta}s CPU over the last ${wallDelta}s of probes)"
            }
        } catch { $state += " (CPU time unavailable: $($_.Exception.Message))" }
    }
    $processStartUtc = try { $Process.StartTime.ToUniversalTime().ToString('o') } catch { 'unknown' }
    try {
        $owners = @(Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction Stop | Select-Object -ExpandProperty OwningProcess -Unique)
        $listener = "pid $($owners -join ',') (API pid $($Process.Id))"
    } catch {
        # Only "no matching connection" means nothing listens; any other failure is reported, not read as none.
        if ($_.FullyQualifiedErrorId -like 'CmdletizationQuery_NotFound*') { $listener = 'none' }
        else { $listener = "unknown (Get-NetTCPConnection failed: $($_.Exception.Message))" }
    }
    $outcomes = @($history | Group-Object outcome | Sort-Object Count -Descending | ForEach-Object { "$($_.Count)x $($_.Name)" }) -join '; '
    $listening = @(Select-String -LiteralPath $StdoutPath -Pattern 'Now listening on:.*' -ErrorAction SilentlyContinue | ForEach-Object { $_.Matches[0].Value })
    $summary = [ordered]@{ name=$Name; url=$url; attempts=$history.Count; attemptBudget=$Attempts; elapsedSeconds=$elapsed; state=$state; listener=$listener
        listening=($listening -join ' | '); processStartUtc=$processStartUtc; history=$history.ToArray() }
    try { $summary | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $AttemptLogPath -Encoding UTF8 } catch { }
    $nl = [Environment]::NewLine
    throw ("$Name did not become ready after $($history.Count) of $Attempts attempts in ${elapsed}s; $state. " +
        "Listener on port ${Port}: $listener. Kestrel reported: $(if ($listening) { $listening -join ' | ' } else { 'no listening address' }). " +
        "Probe outcomes: $(if ($outcomes) { $outcomes } else { 'none' }).$nl" +
        "--- last stderr ($StderrPath) ---$nl$(Get-QualificationLogTail $StderrPath)$nl" +
        "--- last stdout ($StdoutPath) ---$nl$(Get-QualificationLogTail $StdoutPath)")
}
# Copies the run's owned logs and summaries, never key material or database files, to a directory the CI job
# uploads. The Domain job uploads TestResults/ on failure, which is where the caller points this under GitHub Actions.
function Save-QualificationDiagnostics {
    param([Parameter(Mandatory)][string[]]$Sources, [Parameter(Mandatory)][string]$Destination)
    $copied = 0
    foreach ($source in $Sources) {
        if (-not (Test-Path -LiteralPath $source -PathType Container)) { continue }
        $sourceFull = [IO.Path]::GetFullPath($source).TrimEnd('\')
        $target = Join-Path $Destination (Split-Path $sourceFull -Leaf)
        # Logs from any depth (PostgreSQL, the APIs, the relay); summaries only from the top level, so seeded
        # evidence files and archive contents are never mistaken for diagnostics. Restore validation's own logs
        # are not among them: Restore-AeroLink.ps1 deletes its temporary validation-logs directory in its own
        # finally, before this copy runs.
        foreach ($file in @(Get-ChildItem -LiteralPath $sourceFull -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $_.Length -le 20MB })) {
            $relative = $file.FullName.Substring($sourceFull.Length).TrimStart('\')
            $topLevel = $relative.IndexOf('\') -lt 0
            if (-not ($file.Extension -in '.log','.jsonl' -or ($topLevel -and $file.Extension -in '.json','.txt'))) { continue }
            $copy = Join-Path $target $relative
            New-Item -ItemType Directory -Path (Split-Path $copy -Parent) -Force | Out-Null
            Copy-Item -LiteralPath $file.FullName -Destination $copy -Force
            $copied++
        }
    }
    return $copied
}
# At the moment of failure, before the APIs are stopped, records what every PostgreSQL session of the disposable
# instance is doing, so a seed API that is waiting on the database is told apart from one the database waits on.
# Called only for the instance this run started; it never throws, so it cannot replace the failure.
function Save-QualificationDatabaseActivity([string]$Path) {
    try {
        $activity = Invoke-QualificationSql 'postgres' ("SELECT coalesce(json_agg(a ORDER BY a.pid), '[]'::json) FROM (SELECT now() AS sampled_at, pid, datname, " +
            "application_name, backend_type, state, wait_event_type, wait_event, xact_start, query_start, state_change, left(query, 300) AS query " +
            "FROM pg_stat_activity WHERE pid <> pg_backend_pid()) a;")
        Set-Content -LiteralPath $Path -Value $activity -Encoding UTF8
    } catch { Write-Warning "PostgreSQL activity at failure could not be recorded: $($_.Exception.Message)" }
}
function Start-NotificationQualificationApi([string]$Database, [string]$Evidence, [string]$Name) {
    $port = Get-FreePort
    $values = [ordered]@{
        'ASPNETCORE_ENVIRONMENT'='Development'; 'ASPNETCORE_URLS'="http://127.0.0.1:$port"
        'ConnectionStrings__AeroLink'="Host=127.0.0.1;Port=$pgPort;Database=$Database;Username=postgres"
        'Database__Provider'='PostgreSql'; 'Evidence__Root'=$Evidence; 'DemoData__Enabled'='false'
        'Identity__SeedDemoAccounts'='false'; 'Identity__AllowDemoAccounts'='true'; 'Identity__CookieSecure'='false'
        'Instance__InstanceId'=$instanceId; 'Instance__Label'='Disposable notification restore qualification'
        'DataProtection__KeyRingPath'=$keyRing; 'Notifications__DispatchIntervalSeconds'='1'
        'Logging__LogLevel__Microsoft.EntityFrameworkCore.Database.Command'='Warning'
        'Logging__Console__TimestampFormat'='HH:mm:ss.fff '; 'Logging__Console__UseUtcTimestamp'='true'
    }
    $snapshot = Get-AeroLinkProcessEnvironmentSnapshot -Name @($values.Keys)
    try {
        foreach ($value in $values.GetEnumerator()) { [Environment]::SetEnvironmentVariable($value.Key,$value.Value,'Process') }
        $process = Start-Process -FilePath $apiExecutable -WorkingDirectory (Split-Path $apiExecutable -Parent) `
            -RedirectStandardOutput (Join-Path $root "$Name.stdout.log") -RedirectStandardError (Join-Path $root "$Name.stderr.log") -WindowStyle Hidden -PassThru
    } finally { Restore-AeroLinkProcessEnvironmentSnapshot -Snapshot $snapshot }
    # Retain the handle at once so Windows PowerShell can still report the exit code of a quick exit.
    [void]$process.Handle
    try {
        [void](Wait-QualificationApiReady -Process $process -Port $port -Attempts 120 -Name "Notification dispatcher API ($Name)" `
            -StdoutPath (Join-Path $root "$Name.stdout.log") -StderrPath (Join-Path $root "$Name.stderr.log") -AttemptLogPath (Join-Path $root "$Name.readiness.json"))
        return [pscustomobject]@{ Process=$process; BaseUrl="http://127.0.0.1:$port" }
    } catch { if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force; $process.WaitForExit(10000)|Out-Null }; throw }
}
function Stop-NotificationQualificationApi([object]$HostProcess) {
    if ($HostProcess -and -not $HostProcess.Process.HasExited) { Stop-Process -Id $HostProcess.Process.Id -Force; $HostProcess.Process.WaitForExit(10000)|Out-Null }
}
function Get-QualificationOperations([string]$BaseUrl) {
    $session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    [void](Invoke-RestMethod "$BaseUrl/api/auth/login" -Method Post -ContentType 'application/json' -Body '{"userName":"admin","password":"AeroLink!2026"}' -WebSession $session)
    return Invoke-RestMethod "$BaseUrl/api/operations/notifications" -WebSession $session
}
function Assert-NoNotificationSockets([string]$Phase) {
    # This is zero-TCP evidence from the actual dispatcher process, not TLS or SMTP acceptance evidence.
    for ($probe=0;$probe -lt 8;$probe++) {
        if ($smtpMonitor.Pending()) { throw "The restored dispatcher opened a relay socket during $Phase." }
        Start-Sleep -Milliseconds 250
    }
}
function Assert-RevokedNotificationBacklog([string]$Phase) {
    Assert-NoNotificationSockets $Phase
    $state = Invoke-QualificationSql 'aerolink' "SELECT `"State`" FROM notification_delivery_generations WHERE `"Id`"='$notificationGenerationId';"
    $attempts = Invoke-QualificationSql 'aerolink' "SELECT count(*) FROM notification_physical_attempts WHERE `"GenerationId`"='$notificationGenerationId';"
    if ($state -ne 'HeldAdmission' -or [int]$attempts -ne 0) { throw "Revoked restored G1 is not held without SMTP attempts during $Phase (state=$state, attempts=$attempts)." }
}
function New-QualificationTlsCertificate([string]$Directory) {
    # Modern .NET exports ephemeral PEM directly; never create an OS certificate-store/key-container entry.
    $pwsh = Get-Command pwsh.exe -ErrorAction Stop
    $generator = Join-Path $Directory 'generate-certificate.ps1'
    @'
param([string]$Directory)
$ErrorActionPreference = 'Stop'
$key = [Security.Cryptography.RSA]::Create(2048)
try {
    $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=localhost', $key,
        [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $san = [Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder]::new()
    $san.AddDnsName('localhost'); $request.CertificateExtensions.Add($san.Build())
    $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($true,$false,0,$true))
    $usage = [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature -bor
        [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::KeyCertSign -bor [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::CrlSign
    $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new($usage,$true))
    $oids = [Security.Cryptography.OidCollection]::new(); [void]$oids.Add([Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.1'))
    $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($oids,$true))
    $certificate = $request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddDays(-2),[DateTimeOffset]::UtcNow.AddDays(2))
    try {
        [IO.File]::WriteAllText((Join-Path $Directory 'certificate.pem'),$certificate.ExportCertificatePem())
        [IO.File]::WriteAllText((Join-Path $Directory 'key.pem'),$key.ExportPkcs8PrivateKeyPem())
    } finally { $certificate.Dispose() }
} finally { $key.Dispose() }
'@ | Set-Content -LiteralPath $generator -Encoding UTF8
    Invoke-Checked $pwsh.Source @('-NoProfile','-File',$generator,'-Directory',$Directory)
}
function Start-QualificationTlsRelay([string]$Directory) {
    $python = if ($env:AEROLINK_TEST_PYTHON) { $env:AEROLINK_TEST_PYTHON } else { (Get-Command python.exe -ErrorAction Stop).Source }
    $relay = Join-Path $PSScriptRoot 'test-support\NotificationTlsRelay.py'
    if (-not (Test-Path -LiteralPath $relay -PathType Leaf)) { throw 'The shared executable TLS relay fixture is missing.' }
    $stdout = Join-Path $Directory 'relay.stdout.log'
    $stderr = Join-Path $Directory 'relay.stderr.log'
    $process = Start-Process -FilePath $python -ArgumentList @(('"'+$relay+'"'),('"'+$Directory+'"'),'AcceptThenDropQuit',"$smtpPort",'180') `
        -RedirectStandardOutput $stdout -RedirectStandardError $stderr -WindowStyle Hidden -PassThru
    # Retain the process handle before a quick relay can exit; Windows PowerShell otherwise may lose
    # the exit code when WaitForExit is first called after termination.
    [void]$process.Handle
    try {
        for ($attempt=0;$attempt -lt 100;$attempt++) {
            if ($process.HasExited) { break }
            if ((Test-Path -LiteralPath $stdout) -and ((Get-Content -LiteralPath $stdout -TotalCount 1) -eq "$smtpPort")) { return $process }
            Start-Sleep -Milliseconds 100
        }
        throw "Owned STARTTLS relay did not become ready. See $stderr."
    } catch { if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force }; throw }
}

$token = [Guid]::NewGuid().ToString('N')
$shortToken = $token.Substring(0,8)
$root = Join-Path ([IO.Path]::GetTempPath()) "arq-$shortToken"
$data = Join-Path $root 'postgres'; $sourceEvidence = Join-Path $root 'source evidence Ω'
$backupRoot = Join-Path ([IO.Path]::GetTempPath()) "arq-backup-$token"; $oldEvidence = Join-Path $root 'production evidence'
$installationRoot = Join-Path $root 'installation'
$isolatedEvidence = Join-Path $installationRoot "restore-validation\q-$shortToken\e"
$keyRing = Join-Path $root 'key-ring'; $instanceId = [guid]::NewGuid().ToString('D'); $g1 = [guid]::NewGuid().ToString('D')
$apiExecutable = Join-Path $productRoot 'src\AeroLink.Api\bin\Release\net10.0\AeroLink.Api.exe'
if (-not (Test-Path -LiteralPath $apiExecutable -PathType Leaf)) { throw 'Restore qualification requires the current caller-built Release API executable; no binary fallback is permitted.' }
$pgLog = Join-Path $root 'postgres.log'; $apiOut = Join-Path $root 'seed-api.stdout.log'; $apiErr = Join-Path $root 'seed-api.stderr.log'
$pgPort = if ($ExistingPostgresPort) { $ExistingPostgresPort } else { Get-FreePort }; $seedApiPort = Get-FreePort; $smtpPort = Get-FreePort
$api = $null; $dispatcherHost = $null; $smtpMonitor = $null; $tlsRelay = $null; $postgresStarted = $false; $qualificationPassed = $false; $previous = @{}; $originalDatabases = @(); $databaseOwnershipEstablished = $false
$priorEvidenceRoot = Get-AeroLinkProcessEnvironmentSnapshot -Name @('Evidence__Root')
$priorQualificationEnvironment = Get-AeroLinkProcessEnvironmentSnapshot -Name @('AEROLINK_INSTALLATION_ROOT','AEROLINK_NOTIFICATION_AUTHORITY_ROOT','DataProtection__KeyRingPath','Connector__SigningKeyPath')
New-Item -ItemType Directory -Path $root,$sourceEvidence,$backupRoot,$oldEvidence,$installationRoot,$keyRing -Force | Out-Null
$env:AEROLINK_INSTALLATION_ROOT = $installationRoot
$env:DataProtection__KeyRingPath = $keyRing
$env:Connector__SigningKeyPath = Join-Path $root 'connector-signing-key.pem'
if (-not $env:AEROLINK_NOTIFICATION_AUTHORITY_ROOT) { $env:AEROLINK_NOTIFICATION_AUTHORITY_ROOT = Join-Path $root 'authority' }
New-Item -ItemType Directory -Path $env:AEROLINK_NOTIFICATION_AUTHORITY_ROOT -Force | Out-Null
Import-Module (Join-Path $PSScriptRoot 'AeroLinkNotificationAuthority.psm1') -Force
$authorityPath = Get-AeroLinkNotificationAuthorityPath -InstanceId $instanceId
$instanceConfig = Join-Path $installationRoot 'instance.json'
[ordered]@{instanceId=$instanceId;label='Disposable notification restore qualification';classification='LocalDemo'} | ConvertTo-Json | Set-Content -LiteralPath $instanceConfig -Encoding UTF8
$savedInstanceConfig = [IO.File]::ReadAllBytes($instanceConfig)
$g1Policy = [ordered]@{installationId=$instanceId;hostIdentity=[Environment]::MachineName;sendGeneration=$g1;policyRevision=[guid]::NewGuid().ToString('D');maximumMode=3;relayHosts=@('localhost');senders=@('restore-fixture@example.invalid');recipientDomains=@('example.invalid');recipientAddresses=@();diagnosticTarget='diagnostic@example.invalid';baseUrl='https://restore-fixture.invalid';allowManagedCredentials=$false;trustAnchorsPem=@()}
$g1Json = ConvertTo-Json -InputObject $g1Policy -Depth 6 -Compress
[IO.File]::WriteAllText($authorityPath,$g1Json,[Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText(($authorityPath+'.generation'),$g1,[Text.UTF8Encoding]::new($false))
try {
    $tlsRoot = Join-Path $root 'owned-starttls'
    New-Item -ItemType Directory -Path $tlsRoot | Out-Null
    New-QualificationTlsCertificate $tlsRoot
    $g1Policy.trustAnchorsPem = @([IO.File]::ReadAllText((Join-Path $tlsRoot 'certificate.pem')))
    $g1Json = ConvertTo-Json -InputObject $g1Policy -Depth 6 -Compress
    [IO.File]::WriteAllText($authorityPath,$g1Json,[Text.UTF8Encoding]::new($false))
    # Qualify repeatable OS-file revocation before expensive seeding. No database, admission or worker
    # exists yet; restoring this artificial initial policy is fixture setup, never recovery of sent work.
    for ($revocationProbe=0;$revocationProbe -lt 2;$revocationProbe++) {
        $probeResult = Revoke-AeroLinkNotificationSendGeneration -ProductRoot $productRoot
        if ($probeResult.State -ne 'DisabledRevoked' -or $probeResult.SendGeneration -eq $g1) { throw 'The repeated installation revocation preflight failed.' }
    }
    [IO.File]::WriteAllText(($authorityPath+'.generation'),$g1,[Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($authorityPath,$g1Json,[Text.UTF8Encoding]::new($false))
    if (-not $ExistingPostgresPort) {
        Invoke-Checked (Join-Path $PostgresBin 'initdb.exe') @('-D',$data,'-U','postgres','-A','trust','--encoding=UTF8')
        Invoke-Checked (Join-Path $PostgresBin 'pg_ctl.exe') @('-D',$data,'-l',$pgLog,'-o',"-p $pgPort -h 127.0.0.1",'-w','start'); $postgresStarted = $true
    }
    $originalDatabases = @(Invoke-QualificationSql 'postgres' 'SELECT datname FROM pg_database;' | ForEach-Object { $_ -split "`n" })
    foreach ($reserved in 'aerolink','aerolink_source','aerolink_restore_validation') { if ($reserved -in $originalDatabases) { throw "Restore qualification refuses existing database '$reserved' on port $pgPort; it is not owned by this run." } }
    $databaseOwnershipEstablished = $true
    Invoke-Checked (Join-Path $PostgresBin 'createdb.exe') @('-h','127.0.0.1','-p',"$pgPort",'-U','postgres','aerolink_source')
    Invoke-Checked (Join-Path $PostgresBin 'createdb.exe') @('-h','127.0.0.1','-p',"$pgPort",'-U','postgres','aerolink')
    Invoke-Checked (Join-Path $PostgresBin 'psql.exe') @('-h','127.0.0.1','-p',"$pgPort",'-U','postgres','-d','aerolink','-v','ON_ERROR_STOP=1','-c',"CREATE TABLE restore_marker(value text NOT NULL); INSERT INTO restore_marker VALUES ('original');")
    Set-Content -LiteralPath (Join-Path $oldEvidence 'original.txt') -Value 'original evidence' -Encoding UTF8

    $settings = [ordered]@{
        'ASPNETCORE_ENVIRONMENT'='Development'; 'ASPNETCORE_URLS'="http://127.0.0.1:$seedApiPort"
        'ConnectionStrings__AeroLink'="Host=127.0.0.1;Port=$pgPort;Database=aerolink_source;Username=postgres"
        'Evidence__Root'=$sourceEvidence; 'DemoData__Enabled'='true'; 'Identity__SeedDemoAccounts'='true'; 'Identity__AllowDemoAccounts'='true'; 'Identity__CookieSecure'='false'
        'Database__Provider'='PostgreSql'; 'Instance__InstanceId'=$instanceId; 'Instance__Label'='Disposable notification restore qualification'
        'DataProtection__KeyRingPath'=$keyRing; 'Notifications__DispatchIntervalSeconds'='3600'
        'Logging__LogLevel__Microsoft.EntityFrameworkCore.Database.Command'='Warning'
        # UTC timestamps on every API log line, so a slow startup shows when migrations ended and the first logged
        # line after them. The startup seeders do not log and run before app.Run(), so the timestamps bound the
        # seeding phase but cannot name a seeder; the readiness wait's CPU time says whether it was busy or stuck.
        # The default console logger honours these Console-level keys; the FormatterOptions section did not apply.
        'Logging__Console__TimestampFormat'='HH:mm:ss.fff '; 'Logging__Console__UseUtcTimestamp'='true'
    }
    $previous = Get-AeroLinkProcessEnvironmentSnapshot -Name @($settings.Keys)
    foreach ($item in $settings.GetEnumerator()) { [Environment]::SetEnvironmentVariable($item.Key,$item.Value,'Process') }
    $api = Start-Process -FilePath $apiExecutable -WorkingDirectory (Split-Path $apiExecutable -Parent) `
        -RedirectStandardOutput $apiOut -RedirectStandardError $apiErr -WindowStyle Hidden -PassThru
    [void]$api.Handle
    $seedReady = Wait-QualificationApiReady -Process $api -Port $seedApiPort -Attempts 180 -Name 'Disposable seed API' `
        -StdoutPath $apiOut -StderrPath $apiErr -AttemptLogPath (Join-Path $root 'seed-api.readiness.json')
    Write-Host "Disposable seed API ready after $($seedReady.Attempts) readiness attempts in $([Math]::Round($seedReady.ElapsedMs/1000,1))s."
    # Primary executable owner: ordinary settings save and explicit G1 activation produce a real bound
    # Pending generation before backup. A future due time permits a restart control without TCP transport.
    # A shared actual STARTTLS relay below proves post-backup DATA acceptance and the durable receipt.
    $baseUrl = "http://127.0.0.1:$seedApiPort"; $session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    [void](Invoke-RestMethod "$baseUrl/api/auth/login" -Method Post -ContentType 'application/json' -Body '{"userName":"admin","password":"AeroLink!2026"}' -WebSession $session)
    $csrf = Invoke-RestMethod "$baseUrl/api/auth/csrf" -WebSession $session
    $headers = @{'X-AeroLink-CSRF'=$csrf.token}
    $workspaces = Invoke-RestMethod "$baseUrl/api/workspaces" -WebSession $session
    $workspace = @($workspaces | Where-Object { $_.program.code -eq 'FMSLIVE' })[0]
    if (-not $workspace) { throw 'Notification restore fixture requires an actual seeded FMS Project.' }
    $notificationProject = [string]$workspace.projects[0].project.id
    $operations = Invoke-RestMethod "$baseUrl/api/operations/notifications" -WebSession $session
    $save = @{operationKey=[guid]::NewGuid().ToString('D');expectedVersion=$operations.settings.version;mode='Live';host='localhost';port=$smtpPort;sender='restore-fixture@example.invalid';displayName='Restore fixture';baseUrl='https://restore-fixture.invalid'}
    [void](Invoke-RestMethod "$baseUrl/api/operations/notifications/settings" -Method Post -Headers $headers -ContentType 'application/json' -Body ($save|ConvertTo-Json) -WebSession $session)
    $operations = Invoke-RestMethod "$baseUrl/api/operations/notifications" -WebSession $session
    $activate = @{operationKey=[guid]::NewGuid().ToString('D');expectedVersion=$operations.settings.version;family='Activate';mode='Live'}
    [void](Invoke-RestMethod "$baseUrl/api/operations/notifications/commands" -Method Post -Headers $headers -ContentType 'application/json' -Body ($activate|ConvertTo-Json) -WebSession $session)
    $operations = Invoke-RestMethod "$baseUrl/api/operations/notifications" -WebSession $session
    $diagnostic = @{operationKey=[guid]::NewGuid().ToString('D');expectedVersion=$operations.settings.version;projectId=$notificationProject}
    $notice = Invoke-RestMethod "$baseUrl/api/operations/notifications/transport-test" -Method Post -Headers $headers -ContentType 'application/json' -Body ($diagnostic|ConvertTo-Json) -WebSession $session
    $deliveryId = Invoke-QualificationSql 'aerolink_source' "SELECT `"Id`" FROM notification_deliveries WHERE `"NotificationId`"='$($notice.result.notificationId)';"
    $readmit = @{operationKey=[guid]::NewGuid().ToString('D');expectedVersion=$notice.result.version;family='Readmit';deliveryId=$deliveryId}
    $generation = Invoke-RestMethod "$baseUrl/api/operations/notifications/commands" -Method Post -Headers $headers -ContentType 'application/json' -Body ($readmit|ConvertTo-Json) -WebSession $session
    $notificationGenerationId = [string]$generation.result.generationId
    $futureDue = [DateTimeOffset]::UtcNow.AddHours(2).UtcTicks
    [void](Invoke-QualificationSql 'aerolink_source' "UPDATE notification_delivery_generations SET `"DueTicks`"=$futureDue WHERE `"Id`"='$notificationGenerationId';")
    $notificationSnapshotSql = "SELECT row_to_json(g)::text FROM (SELECT `"State`",`"Version`",`"DueTicks`",`"SendGeneration`",`"AdmissionEpochId`",`"MessageId`",`"Attempts`" FROM notification_delivery_generations WHERE `"Id`"='$notificationGenerationId') g;"
    $beforeRestart = Invoke-QualificationSql 'aerolink_source' $notificationSnapshotSql
    if ($beforeRestart -notmatch 'Pending' -or $beforeRestart -notmatch $g1) { throw 'The real G1 activation did not create the Pending generation to back up.' }
    Stop-Process -Id $api.Id -Force; $api.WaitForExit(10000)|Out-Null; $api=$null
    Restore-AeroLinkProcessEnvironmentSnapshot -Snapshot $previous;$previous=@{}
    $dispatcherHost = Start-NotificationQualificationApi 'aerolink_source' $sourceEvidence 'ordinary-restart-control'
    if ((Invoke-QualificationSql 'aerolink_source' $notificationSnapshotSql) -ne $beforeRestart) { throw 'Ordinary restart changed the admission/generation/due schedule of future Pending work.' }
    Stop-NotificationQualificationApi $dispatcherHost; $dispatcherHost=$null

    # Back up due work while no dispatcher runs. The restored zero-socket result must not depend on
    # a future schedule hiding transport behind the clock; the independent send authority must hold it.
    [void](Invoke-QualificationSql 'aerolink_source' "UPDATE notification_delivery_generations SET `"DueTicks`"=$([DateTimeOffset]::UtcNow.UtcTicks) WHERE `"Id`"='$notificationGenerationId';")
    $backedUpGeneration = Invoke-QualificationSql 'aerolink_source' $notificationSnapshotSql

    $env:Evidence__Root = $sourceEvidence
    & (Join-Path $PSScriptRoot 'Backup-AeroLink.ps1') -RetentionDays 0 -Database aerolink_source -PostgresPort $pgPort `
        -BackupRoot $backupRoot -PostgresBin $PostgresBin -PostgresAlreadyRunning
    if ($LASTEXITCODE -ne 0) { throw 'Disposable configured-root backup failed.' }
    $archive = (Get-ChildItem -LiteralPath $backupRoot -Filter 'aerolink-*.zip' | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
    if (-not $archive) { throw 'Disposable backup archive was not produced.' }
    # Decisive restore positive control: this exact G1 Pending generation is in the backup, then the
    # normal independent API dispatcher sends it through real STARTTLS and records SMTP acceptance.
    # The relay flushes its real final-DATA response evidence; neither side substitutes a producer flag.
    $tlsRelay = Start-QualificationTlsRelay $tlsRoot
    $dispatcherHost = Start-NotificationQualificationApi 'aerolink_source' $sourceEvidence 'post-backup-g1-sender'
    $accepted = $false
    for ($attempt=0;$attempt -lt 120;$attempt++) {
        $sentState = Invoke-QualificationSql 'aerolink_source' "SELECT `"State`" FROM notification_delivery_generations WHERE `"Id`"='$notificationGenerationId';"
        if ($sentState -eq 'SmtpAccepted') { $accepted=$true; break }
        if ($sentState -in @('AcceptanceUnknown','PermanentFailed','HeldAdmission','ConfigBlocked','RetryExhausted')) { throw "Post-backup G1 did not reach SMTP acceptance: $sentState." }
        Start-Sleep -Milliseconds 500
    }
    if (-not $accepted) { throw 'The actual post-backup G1 dispatcher never persisted SMTP acceptance.' }
    $relayExited = $tlsRelay.WaitForExit(10000); $tlsRelay.Refresh()
    if (-not $relayExited -or $tlsRelay.ExitCode -ne 0) { throw "The actual TLS relay did not complete successfully after accepted G1 (exited=$relayExited, code=$($tlsRelay.ExitCode))." }
    $events = @(Get-Content -LiteralPath (Join-Path $tlsRoot 'events.jsonl') | ForEach-Object { ConvertFrom-Json $_ })
    foreach ($event in 'TcpAccepted','TlsStarted','DataReceived','FinalDataAccepted') {
        if (@($events | Where-Object event -eq $event).Count -ne 1) { throw "The actual post-backup relay did not prove exactly one $event." }
    }
    $physicalReceipt = Invoke-QualificationSql 'aerolink_source' "SELECT count(*) FROM notification_physical_attempts WHERE `"GenerationId`"='$notificationGenerationId' AND `"Outcome`"='SmtpAccepted' AND `"TransportDisposed`"=true;"
    if ([int]$physicalReceipt -ne 1) { throw 'Post-backup G1 did not persist one accepted and disposed physical transport receipt.' }
    $postBackupAcceptedGeneration = Invoke-QualificationSql 'aerolink_source' $notificationSnapshotSql
    Stop-NotificationQualificationApi $dispatcherHost; $dispatcherHost=$null; $tlsRelay=$null
    $authorityBeforeValidation = [IO.File]::ReadAllText($authorityPath)
    $witnessBeforeValidation = [IO.File]::ReadAllText($authorityPath+'.generation')
    $archiveUnpacked=Join-Path $root 'archive-negative';Expand-Archive -LiteralPath $archive -DestinationPath $archiveUnpacked
    $archiveInventory=ConvertFrom-Json -InputObject (Get-Content -LiteralPath (Join-Path $archiveUnpacked 'attachment-inventory.json') -Raw)
    $negativeInventory=@($archiveInventory|ForEach-Object{$_})
    Import-Module (Join-Path $PSScriptRoot 'AeroLinkEvidenceStore.psm1') -Force
    $negativeEvidence=Join-Path $archiveUnpacked 'evidence';$sample=$negativeInventory[0]
    $samplePath=Join-Path $negativeEvidence (([string]$sample.StorageKey).Replace('/',[IO.Path]::DirectorySeparatorChar))
    $sampleBytes=[IO.File]::ReadAllBytes($samplePath)
    Remove-Item -LiteralPath $samplePath
    try{Test-AeroLinkAttachmentInventory -Inventory $negativeInventory -EvidenceRoot $negativeEvidence|Out-Null;throw 'Missing evidence was accepted.'}catch{if($_.Exception.Message -notlike '*missing*'){throw}}
    [IO.File]::WriteAllBytes($samplePath,$sampleBytes[0..($sampleBytes.Length-2)])
    try{Test-AeroLinkAttachmentInventory -Inventory $negativeInventory -EvidenceRoot $negativeEvidence|Out-Null;throw 'Wrong evidence size was accepted.'}catch{if($_.Exception.Message -notlike '*size mismatch*'){throw}}
    [IO.File]::WriteAllBytes($samplePath,$sampleBytes);$sampleBytes[0]=$sampleBytes[0]-bxor 1;[IO.File]::WriteAllBytes($samplePath,$sampleBytes)
    try{Test-AeroLinkAttachmentInventory -Inventory $negativeInventory -EvidenceRoot $negativeEvidence|Out-Null;throw 'Wrong evidence hash was accepted.'}catch{if($_.Exception.Message -notlike '*hash mismatch*'){throw}}
    $unsafe=@([pscustomobject]@{Id=$sample.Id;StorageKey='../escape';Size=$sample.Size;Sha256=$sample.Sha256})
    try{Test-AeroLinkAttachmentInventory -Inventory $unsafe -EvidenceRoot $negativeEvidence|Out-Null;throw 'Unsafe evidence key was accepted.'}catch{if($_.Exception.Message -notlike '*Unsafe attachment storage key*'){throw}}
    $ownedArchiveUnpacked = Assert-OwnedQualificationPath $archiveUnpacked $root
    Remove-Item -LiteralPath $ownedArchiveUnpacked -Recurse -Force

    & (Join-Path $PSScriptRoot 'Restore-AeroLink.ps1') -BackupArchive $archive -TargetDatabase aerolink_restore_validation `
        -EvidenceTarget $isolatedEvidence -PostgresPort $pgPort -PostgresBin $PostgresBin -ValidationApiPort (Get-FreePort)
    if ($LASTEXITCODE -ne 0) { throw 'Isolated restore and API-download validation failed.' }
    if ([IO.File]::ReadAllText($authorityPath) -ne $authorityBeforeValidation) { throw 'Read-only isolated restore validation changed send authority.' }
    if ([IO.File]::ReadAllText($authorityPath+'.generation') -ne $witnessBeforeValidation) { throw 'Read-only isolated restore validation changed the independent send-generation witness.' }
    if ((Invoke-QualificationSql 'aerolink_restore_validation' $notificationSnapshotSql) -ne $backedUpGeneration) { throw 'Read-only validation mutated the backed-up Pending generation.' }
    $attachmentCount = (& (Join-Path $PostgresBin 'psql.exe') -h 127.0.0.1 -p $pgPort -U postgres -d aerolink_restore_validation -tA -c 'SELECT count(*) FROM controlled_attachments;').Trim()
    if ([int]$attachmentCount -lt 1) { throw 'The isolated restore qualified no controlled attachments.' }

    $faults=@('BeforeDatabaseRestore','AfterDatabaseRestore','AfterEvidenceCopy','AfterPreActivationValidation','AfterOriginalDatabaseRename','AfterDatabaseActivation','AfterEvidenceActivation','AfterActivationValidation','BeforeRestart','AfterRestart')
    foreach($phase in $faults){
        [IO.File]::WriteAllText(($authorityPath+'.generation'),$g1,[Text.UTF8Encoding]::new($false))
        [IO.File]::WriteAllText($authorityPath,$g1Json,[Text.UTF8Encoding]::new($false))
        $rolledBack=$false
        try {
            & (Join-Path $PSScriptRoot 'Restore-AeroLink.ps1') -BackupArchive $archive -TargetDatabase aerolink `
                -EvidenceTarget $oldEvidence -PostgresPort $pgPort -PostgresBin $PostgresBin -ValidationApiPort (Get-FreePort) `
                -AllowProductionRestore -Confirmation RESTORE-AEROLINK -DisposableQualification -FaultInjection $phase
        } catch {
            if ($_.Exception.Message -like '*Automatic rollback also failed*') { throw }
            if ($_.Exception.Message -eq "Injected restore fault at $phase.") { $rolledBack=$true } else { throw }
        }
        if(-not $rolledBack){throw "The production activation fault at $phase was not observed."}
        $marker=(& (Join-Path $PostgresBin 'psql.exe') -h 127.0.0.1 -p $pgPort -U postgres -d aerolink -tA -c 'SELECT value FROM restore_marker;').Trim()
        if($marker -ne 'original' -or -not(Test-Path -LiteralPath (Join-Path $oldEvidence 'original.txt'))){throw "Database/evidence rollback did not restore the original production pair after $phase."}
        $revoked = Get-Content -LiteralPath $authorityPath -Raw | ConvertFrom-Json
        if ([string]$revoked.sendGeneration -eq $g1 -or [int]$revoked.maximumMode -ne 0) { throw "Restore fault $phase failed to revoke G1 independently of database rollback." }
        [IO.File]::WriteAllBytes($instanceConfig,$savedInstanceConfig)
        if ((Get-Content -LiteralPath $authorityPath -Raw | ConvertFrom-Json).sendGeneration -eq $g1) { throw "Old instance configuration revived G1 after $phase." }
    }

    [IO.File]::WriteAllText($authorityPath,$g1Json,[Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText(($authorityPath+'.generation'),$g1,[Text.UTF8Encoding]::new($false))
    & (Join-Path $PSScriptRoot 'Restore-AeroLink.ps1') -BackupArchive $archive -TargetDatabase aerolink `
        -EvidenceTarget $oldEvidence -PostgresPort $pgPort -PostgresBin $PostgresBin -ValidationApiPort (Get-FreePort) `
        -AllowProductionRestore -Confirmation RESTORE-AEROLINK -DisposableQualification
    if ($LASTEXITCODE -ne 0) { throw 'Disposable production activation qualification failed.' }
    $revoked = Get-Content -LiteralPath $authorityPath -Raw | ConvertFrom-Json
    if ([string]$revoked.sendGeneration -eq $g1 -or [int]$revoked.maximumMode -ne 0) { throw 'Successful supported restore did not revoke G1 before activating the restored database.' }
    [IO.File]::WriteAllBytes($instanceConfig,$savedInstanceConfig)
    if ((Get-Content -LiteralPath $authorityPath -Raw | ConvertFrom-Json).sendGeneration -ne $revoked.sendGeneration) { throw 'Restoring old instance configuration rewrote protected send authority.' }
    $revokedWitness = [IO.File]::ReadAllText($authorityPath+'.generation')
    if ($revokedWitness -ne [string]$revoked.sendGeneration) { throw 'Supported restore did not commit the independent revocation witness.' }
    $smtpMonitor = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,$smtpPort); $smtpMonitor.Start()
    # Independent positive control: the socket observer must detect a known loopback connection.
    $positiveSocket = [Net.Sockets.TcpClient]::new()
    try { $positiveSocket.Connect('127.0.0.1',$smtpPort); if (-not $smtpMonitor.Pending()) { throw 'Relay-socket observer positive control failed.' }; $acceptedProbe=$smtpMonitor.AcceptTcpClient();$acceptedProbe.Dispose() }
    finally { $positiveSocket.Dispose() }
    $dispatcherHost = Start-NotificationQualificationApi 'aerolink' $oldEvidence 'restored-revoked-dispatcher'
    Assert-RevokedNotificationBacklog 'supported restore with G2 revocation'
    [IO.File]::WriteAllBytes($instanceConfig,$savedInstanceConfig)
    Assert-RevokedNotificationBacklog 'old instance configuration replay'
    # Replacing only the protected policy with archived G1 cannot match the current independent witness.
    [IO.File]::WriteAllText($authorityPath,$g1Json,[Text.UTF8Encoding]::new($false))
    $invalid = Get-QualificationOperations $dispatcherHost.BaseUrl
    if ($invalid.smtp.configured -or $invalid.policy.sendAuthority -ne 'blocked') { throw 'An archived G1 policy revived authority without the independent witness.' }
    Assert-RevokedNotificationBacklog 'archived G1 policy replacement'
    # A valid replacement policy is the positive control for each single-field refusal. The original
    # restored G1 work remains held even while G2 transport is permitted: no admission is inferred.
    $g2Policy = ConvertFrom-Json $g1Json; $g2Policy.sendGeneration=[string]$revoked.sendGeneration; $g2Policy.policyRevision=[guid]::NewGuid().ToString('D')
    $g2Json = ConvertTo-Json -InputObject $g2Policy -Depth 6 -Compress
    foreach ($invalidAuthority in 'Missing','Corrupt','CopiedHost','WrongInstallation','MissingWitness') {
        [IO.File]::WriteAllText($authorityPath,$g2Json,[Text.UTF8Encoding]::new($false))
        [IO.File]::WriteAllText(($authorityPath+'.generation'),[string]$g2Policy.sendGeneration,[Text.UTF8Encoding]::new($false))
        $permitted = Get-QualificationOperations $dispatcherHost.BaseUrl
        if (-not $permitted.smtp.configured -or $permitted.policy.sendAuthority -ne 'current') { throw "Valid G2 authority positive control failed before $invalidAuthority." }
        Assert-RevokedNotificationBacklog "valid G2 policy before $invalidAuthority"
        switch ($invalidAuthority) {
            'Missing' { Remove-Item -LiteralPath $authorityPath }
            'Corrupt' { [IO.File]::WriteAllText($authorityPath,'{broken',[Text.UTF8Encoding]::new($false)) }
            'CopiedHost' { $bad=ConvertFrom-Json $g2Json; $bad.hostIdentity='qualification-other-host'; $bad|ConvertTo-Json -Depth 6|Set-Content -LiteralPath $authorityPath -Encoding UTF8 }
            'WrongInstallation' { $bad=ConvertFrom-Json $g2Json; $bad.installationId=[guid]::NewGuid().ToString('D'); $bad|ConvertTo-Json -Depth 6|Set-Content -LiteralPath $authorityPath -Encoding UTF8 }
            'MissingWitness' { Remove-Item -LiteralPath ($authorityPath+'.generation') }
        }
        $invalid = Get-QualificationOperations $dispatcherHost.BaseUrl
        if ($invalid.smtp.configured -or $invalid.policy.sendAuthority -ne 'blocked') { throw "Invalid notification authority $invalidAuthority did not fail closed." }
        Assert-RevokedNotificationBacklog $invalidAuthority
    }
    # Explicit G2 commissioning/activation still admits only future events, not restored G1 backlog.
    [IO.File]::WriteAllText(($authorityPath+'.generation'),[string]$g2Policy.sendGeneration,[Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($authorityPath,$g2Json,[Text.UTF8Encoding]::new($false))
    $session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    [void](Invoke-RestMethod "$($dispatcherHost.BaseUrl)/api/auth/login" -Method Post -ContentType 'application/json' -Body '{"userName":"admin","password":"AeroLink!2026"}' -WebSession $session)
    $csrf=Invoke-RestMethod "$($dispatcherHost.BaseUrl)/api/auth/csrf" -WebSession $session
    $operations=Invoke-RestMethod "$($dispatcherHost.BaseUrl)/api/operations/notifications" -WebSession $session
    $activate=@{operationKey=[guid]::NewGuid().ToString('D');expectedVersion=$operations.settings.version;family='Activate';mode='Live'}
    [void](Invoke-RestMethod "$($dispatcherHost.BaseUrl)/api/operations/notifications/commands" -Method Post -Headers @{'X-AeroLink-CSRF'=$csrf.token} -ContentType 'application/json' -Body ($activate|ConvertTo-Json) -WebSession $session)
    Assert-RevokedNotificationBacklog 'explicit G2 future-only activation'
    $generationCount=Invoke-QualificationSql 'aerolink' "SELECT count(*) FROM notification_delivery_generations WHERE `"DeliveryId`"='$deliveryId';"
    if ([int]$generationCount -ne 1) { throw 'G2 activation implicitly reissued the restored delivery backlog.' }
    Stop-NotificationQualificationApi $dispatcherHost; $dispatcherHost=$null
    $smtpMonitor.Stop(); $smtpMonitor=$null
    $activatedCount=(& (Join-Path $PostgresBin 'psql.exe') -h 127.0.0.1 -p $pgPort -U postgres -d aerolink -tA -c 'SELECT count(*) FROM controlled_attachments;').Trim()
    if([int]$activatedCount -ne [int]$attachmentCount){throw 'Activated production attachment inventory differs from the isolated restore.'}
    $retainedDatabase=(& (Join-Path $PostgresBin 'psql.exe') -h 127.0.0.1 -p $pgPort -U postgres -d postgres -tA -c "SELECT count(*) FROM pg_database WHERE datname LIKE 'aerolink_pre_restore_%';").Trim()
    $retainedEvidence=@(Get-ChildItem -LiteralPath (Split-Path $oldEvidence -Parent) -Directory -Filter 'evidence-pre-restore-*')
    if([int]$retainedDatabase -lt 1 -or $retainedEvidence.Count -lt 1){throw 'Successful activation did not retain the prior database/evidence pair for rollback.'}

    [pscustomobject]@{Passed=$true;PersistentPortUntouched=($pgPort -ne 54329);PostgresPort=$pgPort;IsolatedAttachments=[int]$attachmentCount;ActivatedAttachments=[int]$activatedCount;FaultPhasesProved=$faults.Count;RollbackProved=$true;PriorDatabaseRetained=$true;PriorEvidenceRetained=$true;
        NotificationRevocationProved=$true;NotificationRollbackRevocationProved=$true;OldConfigurationCannotReviveG1=$true;IndependentWitnessProved=$true;
        RestoredDispatcherZeroSocketsProved=$true;RestoredG1HeldAdmissionProved=$true;G2FutureOnlyAdmissionProved=$true;OrdinaryRestartPreservedAdmissionAndDue=$true;
        NotificationFullScenarioProved=$true;PostBackupSmtpAcceptance='Proved: real STARTTLS final DATA acceptance and matching durable disposed SMTP receipt';
        PostBackupAcceptedGeneration=$postBackupAcceptedGeneration;RealOperatorStopRestart='NotRun: DisposableQualification skips supported Stop/Start processes'}
    $qualificationPassed = $true
    $global:LASTEXITCODE=0
}
catch {
    $qualificationFailure = $_
    if ($postgresStarted) { Save-QualificationDatabaseActivity (Join-Path $root 'pg-stat-activity-at-failure.json') }
    # The rethrow is what fails the run: after the finally's pg_ctl stop, $LASTEXITCODE is 0 again.
    throw
}
finally {
    Stop-NotificationQualificationApi $dispatcherHost
    if ($tlsRelay -and -not $tlsRelay.HasExited) { Stop-Process -Id $tlsRelay.Id -Force; $tlsRelay.WaitForExit(10000)|Out-Null }
    if ($smtpMonitor) { $smtpMonitor.Stop() }
    if($api -and -not $api.HasExited){Stop-Process -Id $api.Id -Force -ErrorAction SilentlyContinue}
    if($previous.Count -gt 0){Restore-AeroLinkProcessEnvironmentSnapshot -Snapshot $previous}
    Restore-AeroLinkProcessEnvironmentSnapshot -Snapshot $priorEvidenceRoot
    if($postgresStarted){& (Join-Path $PostgresBin 'pg_ctl.exe') -D $data -m immediate -w stop | Out-Null}
    elseif ($ExistingPostgresPort -and $databaseOwnershipEstablished) {
        $createdDatabases = @(Invoke-QualificationSql 'postgres' 'SELECT datname FROM pg_database;' | ForEach-Object { $_ -split "`n" } | Where-Object { $_ -notin $originalDatabases })
        foreach ($ownedDatabase in $createdDatabases) {
            if ($ownedDatabase -notmatch '^aerolink(?:_source|_restore_validation|_(?:restore_stage|pre_restore|failed_restore)_[a-z0-9]+)?$') { throw "Unexpected database appeared during restore qualification; it is not owned for cleanup: $ownedDatabase" }
            [void](Invoke-QualificationSql 'postgres' "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname='$ownedDatabase' AND pid<>pg_backend_pid();")
            Invoke-Checked (Join-Path $PostgresBin 'dropdb.exe') @('-h','127.0.0.1','-p',"$pgPort",'-U','postgres',$ownedDatabase)
        }
    }
    if (-not $qualificationPassed) {
        if (Test-Path -LiteralPath $authorityPath) { Copy-Item -LiteralPath $authorityPath -Destination (Join-Path $root 'notification-authority-at-exit.json') }
        if (Test-Path -LiteralPath ($authorityPath+'.generation')) { Copy-Item -LiteralPath ($authorityPath+'.generation') -Destination (Join-Path $root 'notification-authority-generation-at-exit.txt') }
    }
    if(Test-Path -LiteralPath $authorityPath){Remove-Item -LiteralPath $authorityPath -Force}
    if(Test-Path -LiteralPath ($authorityPath+'.generation')){Remove-Item -LiteralPath ($authorityPath+'.generation') -Force}
    Restore-AeroLinkProcessEnvironmentSnapshot -Snapshot $priorQualificationEnvironment
    if ($qualificationPassed) {
        if(Test-Path -LiteralPath $root){$ownedRoot=Assert-OwnedQualificationPath $root ([IO.Path]::GetTempPath());Remove-Item -LiteralPath $ownedRoot -Recurse -Force}
        if(Test-Path -LiteralPath $backupRoot){$ownedBackupRoot=Assert-OwnedQualificationPath $backupRoot ([IO.Path]::GetTempPath());Remove-Item -LiteralPath $ownedBackupRoot -Recurse -Force}
    } else {
        Write-Warning "Restore qualification failed; owned diagnostics retained at $root and $backupRoot."
        # Best effort: a failure to copy diagnostics must never replace the qualification failure being reported.
        try {
            $apiFile = Get-Item -LiteralPath $apiExecutable -ErrorAction SilentlyContinue
            [ordered]@{ failedAtUtc=[DateTime]::UtcNow.ToString('o'); message="$qualificationFailure"
                position=$(if ($qualificationFailure) { $qualificationFailure.InvocationInfo.PositionMessage } else { $null })
                scriptStackTrace=$(if ($qualificationFailure) { $qualificationFailure.ScriptStackTrace } else { $null })
                postgresPort=$pgPort; seedApiPort=$seedApiPort; smtpPort=$smtpPort; postgresBin=$PostgresBin
                dynamicPortRange=(@(netsh int ipv4 show dynamicport tcp 2>$null) -join ' ').Trim()
                apiExecutable=$apiExecutable; apiExecutableWrittenUtc=$(if ($apiFile) { $apiFile.LastWriteTimeUtc.ToString('o') } else { $null })
                seedApiSettings=$settings } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $root 'qualification-failure.json') -Encoding UTF8
            # Only a GitHub Actions run defaults to TestResults, which the Domain job uploads; a local run keeps the
            # copy inside its own retained root rather than writing machine paths into the checkout.
            $diagnosticsTarget = if ($DiagnosticsDirectory) { $DiagnosticsDirectory }
                elseif ($env:GITHUB_ACTIONS -eq 'true') { Join-Path $repositoryRoot 'TestResults' }
                else { Join-Path $root 'collected-diagnostics' }
            $diagnosticsTarget = Join-Path $diagnosticsTarget "restore-qualification-$shortToken"
            $copiedCount = Save-QualificationDiagnostics -Sources @($root,$backupRoot) -Destination $diagnosticsTarget
            Write-Warning "Copied $copiedCount restore qualification diagnostic files to $diagnosticsTarget."
        } catch { Write-Warning "Restore qualification diagnostics could not be copied: $($_.Exception.Message)" }
    }
}
