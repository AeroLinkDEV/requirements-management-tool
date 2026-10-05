[CmdletBinding()]
param([string]$PostgresBin, [int]$ExistingPostgresPort = 0)

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
    }
    $snapshot = Get-AeroLinkProcessEnvironmentSnapshot -Name @($values.Keys)
    try {
        foreach ($value in $values.GetEnumerator()) { [Environment]::SetEnvironmentVariable($value.Key,$value.Value,'Process') }
        $process = Start-Process -FilePath $apiExecutable -WorkingDirectory (Split-Path $apiExecutable -Parent) `
            -RedirectStandardOutput (Join-Path $root "$Name.stdout.log") -RedirectStandardError (Join-Path $root "$Name.stderr.log") -WindowStyle Hidden -PassThru
    } finally { Restore-AeroLinkProcessEnvironmentSnapshot -Snapshot $snapshot }
    try {
        $ready = $false
        for ($attempt=0;$attempt -lt 120;$attempt++) {
            if ($process.HasExited) { break }
            try { if ((Invoke-WebRequest "http://127.0.0.1:$port/health/ready" -UseBasicParsing -TimeoutSec 2).StatusCode -eq 200) { $ready=$true;break } } catch { }
            Start-Sleep -Milliseconds 500
        }
        if (-not $ready) { throw "Notification dispatcher API did not become ready. See $root/$Name.stderr.log" }
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
$api = $null; $dispatcherHost = $null; $smtpMonitor = $null; $postgresStarted = $false; $qualificationPassed = $false; $previous = @{}; $originalDatabases = @(); $databaseOwnershipEstablished = $false
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
$g1Policy = [ordered]@{installationId=$instanceId;hostIdentity=[Environment]::MachineName;sendGeneration=$g1;policyRevision=[guid]::NewGuid().ToString('D');maximumMode=3;relayHosts=@('127.0.0.1');senders=@('restore-fixture@example.invalid');recipientDomains=@('example.invalid');recipientAddresses=@();diagnosticTarget='diagnostic@example.invalid';baseUrl='https://restore-fixture.invalid';allowManagedCredentials=$false;trustAnchorsPem=@()}
$g1Json = ConvertTo-Json -InputObject $g1Policy -Depth 6 -Compress
[IO.File]::WriteAllText($authorityPath,$g1Json,[Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText(($authorityPath+'.generation'),$g1,[Text.UTF8Encoding]::new($false))
try {
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
    }
    $previous = Get-AeroLinkProcessEnvironmentSnapshot -Name @($settings.Keys)
    foreach ($item in $settings.GetEnumerator()) { [Environment]::SetEnvironmentVariable($item.Key,$item.Value,'Process') }
    $api = Start-Process -FilePath $apiExecutable -WorkingDirectory (Split-Path $apiExecutable -Parent) `
        -RedirectStandardOutput $apiOut -RedirectStandardError $apiErr -WindowStyle Hidden -PassThru
    $ready = $false
    for ($attempt=0;$attempt -lt 180;$attempt++) { if($api.HasExited){break};try{$response=Invoke-WebRequest -Uri "http://127.0.0.1:$seedApiPort/health/ready" -UseBasicParsing -TimeoutSec 2;if($response.StatusCode -eq 200){$ready=$true;break}}catch{};Start-Sleep -Milliseconds 500 }
    if(-not $ready){throw "Disposable seed API did not become ready. See $apiErr"}
    # Primary executable owner: ordinary settings save and explicit G1 activation produce a real bound
    # Pending generation before backup. A future due time permits a restart control without TCP transport.
    # Successful post-backup TLS DATA acceptance requires the separately owned TLS fixture; do not infer it.
    $baseUrl = "http://127.0.0.1:$seedApiPort"; $session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    [void](Invoke-RestMethod "$baseUrl/api/auth/login" -Method Post -ContentType 'application/json' -Body '{"userName":"admin","password":"AeroLink!2026"}' -WebSession $session)
    $csrf = Invoke-RestMethod "$baseUrl/api/auth/csrf" -WebSession $session
    $headers = @{'X-AeroLink-CSRF'=$csrf.token}
    $workspaces = Invoke-RestMethod "$baseUrl/api/workspaces" -WebSession $session
    $workspace = @($workspaces | Where-Object { $_.program.code -eq 'FMSLIVE' })[0]
    if (-not $workspace) { throw 'Notification restore fixture requires an actual seeded FMS Project.' }
    $notificationProject = [string]$workspace.projects[0].project.id
    $operations = Invoke-RestMethod "$baseUrl/api/operations/notifications" -WebSession $session
    $save = @{operationKey=[guid]::NewGuid().ToString('D');expectedVersion=$operations.settings.version;mode='Live';host='127.0.0.1';port=$smtpPort;sender='restore-fixture@example.invalid';displayName='Restore fixture';baseUrl='https://restore-fixture.invalid'}
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
    $authorityBeforeValidation = [IO.File]::ReadAllText($authorityPath)
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
    $revokedPolicyJson = [IO.File]::ReadAllText($authorityPath)
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
    foreach ($invalidAuthority in 'Missing','Corrupt','CopiedHost','WrongInstallation','MissingWitness') {
        [IO.File]::WriteAllText($authorityPath,$revokedPolicyJson,[Text.UTF8Encoding]::new($false))
        [IO.File]::WriteAllText(($authorityPath+'.generation'),$revokedWitness,[Text.UTF8Encoding]::new($false))
        switch ($invalidAuthority) {
            'Missing' { Remove-Item -LiteralPath $authorityPath }
            'Corrupt' { [IO.File]::WriteAllText($authorityPath,'{broken',[Text.UTF8Encoding]::new($false)) }
            'CopiedHost' { $bad=ConvertFrom-Json $revokedPolicyJson; $bad.hostIdentity='qualification-other-host'; $bad.maximumMode=3; $bad|ConvertTo-Json -Depth 6|Set-Content -LiteralPath $authorityPath -Encoding UTF8 }
            'WrongInstallation' { $bad=ConvertFrom-Json $revokedPolicyJson; $bad.installationId=[guid]::NewGuid().ToString('D'); $bad.maximumMode=3; $bad|ConvertTo-Json -Depth 6|Set-Content -LiteralPath $authorityPath -Encoding UTF8 }
            'MissingWitness' { Remove-Item -LiteralPath ($authorityPath+'.generation') }
        }
        $invalid = Get-QualificationOperations $dispatcherHost.BaseUrl
        if ($invalid.smtp.configured -or $invalid.policy.sendAuthority -ne 'blocked') { throw "Invalid notification authority $invalidAuthority did not fail closed." }
        Assert-RevokedNotificationBacklog $invalidAuthority
    }
    # Explicit G2 commissioning/activation still admits only future events, not restored G1 backlog.
    $g2Policy = ConvertFrom-Json $g1Json; $g2Policy.sendGeneration=[string]$revoked.sendGeneration; $g2Policy.policyRevision=[guid]::NewGuid().ToString('D')
    [IO.File]::WriteAllText(($authorityPath+'.generation'),[string]$g2Policy.sendGeneration,[Text.UTF8Encoding]::new($false))
    $g2Policy | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $authorityPath -Encoding UTF8
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
        NotificationFullScenarioProved=$false;
        PostBackupSmtpAcceptance='NotRun: requires the separately owned trusted TLS fixture';RealOperatorStopRestart='NotRun: DisposableQualification skips supported Stop/Start processes'}
    $qualificationPassed = $true
    $global:LASTEXITCODE=0
}
finally {
    Stop-NotificationQualificationApi $dispatcherHost
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
    } else { Write-Warning "Restore qualification failed; owned diagnostics retained at $root and $backupRoot." }
}
