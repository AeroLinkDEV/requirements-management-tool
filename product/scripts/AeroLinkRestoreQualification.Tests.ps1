[CmdletBinding()]
param([string]$PostgresBin)

$ErrorActionPreference = 'Stop'
$productRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$repositoryRoot = (Resolve-Path (Join-Path $productRoot '..')).Path
if (-not $PostgresBin) { $PostgresBin = Join-Path $productRoot '.local\postgresql\pgsql\bin' }
$PostgresBin = [IO.Path]::GetFullPath($PostgresBin)
foreach ($name in 'initdb.exe','pg_ctl.exe','createdb.exe','psql.exe') {
    if (-not (Test-Path -LiteralPath (Join-Path $PostgresBin $name) -PathType Leaf)) { throw "Disposable PostgreSQL qualification requires $name under $PostgresBin." }
}

function Get-FreePort {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0); $listener.Start()
    try { return ([Net.IPEndPoint]$listener.LocalEndpoint).Port } finally { $listener.Stop() }
}
function Invoke-Checked([string]$File, [string[]]$Arguments) {
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$([IO.Path]::GetFileName($File)) failed with exit code $LASTEXITCODE." }
}

$token = [Guid]::NewGuid().ToString('N')
$shortToken = $token.Substring(0,8)
$root = Join-Path ([IO.Path]::GetTempPath()) "arq-$shortToken"
$data = Join-Path $root 'postgres'; $sourceEvidence = Join-Path $root 'source evidence Ω'
$backupRoot = Join-Path ([IO.Path]::GetTempPath()) "arq-backup-$token"; $oldEvidence = Join-Path $root 'production evidence'
$isolatedEvidence = Join-Path $productRoot ".local\restore-validation\q-$shortToken\e"
$pgLog = Join-Path $root 'postgres.log'; $apiOut = Join-Path $root 'seed-api.stdout.log'; $apiErr = Join-Path $root 'seed-api.stderr.log'
$pgPort = Get-FreePort; if ($pgPort -eq 54329) { $pgPort = Get-FreePort }; $seedApiPort = Get-FreePort
$api = $null; $postgresStarted = $false; $previous = @{}
$priorEvidenceRoot = [Environment]::GetEnvironmentVariable('Evidence__Root','Process')
New-Item -ItemType Directory -Path $root,$sourceEvidence,$backupRoot,$oldEvidence -Force | Out-Null
try {
    Invoke-Checked (Join-Path $PostgresBin 'initdb.exe') @('-D',$data,'-U','postgres','-A','trust','--encoding=UTF8')
    Invoke-Checked (Join-Path $PostgresBin 'pg_ctl.exe') @('-D',$data,'-l',$pgLog,'-o',"-p $pgPort -h 127.0.0.1",'-w','start'); $postgresStarted = $true
    Invoke-Checked (Join-Path $PostgresBin 'createdb.exe') @('-h','127.0.0.1','-p',"$pgPort",'-U','postgres','aerolink_source')
    Invoke-Checked (Join-Path $PostgresBin 'createdb.exe') @('-h','127.0.0.1','-p',"$pgPort",'-U','postgres','aerolink')
    Invoke-Checked (Join-Path $PostgresBin 'psql.exe') @('-h','127.0.0.1','-p',"$pgPort",'-U','postgres','-d','aerolink','-v','ON_ERROR_STOP=1','-c',"CREATE TABLE restore_marker(value text NOT NULL); INSERT INTO restore_marker VALUES ('original');")
    Set-Content -LiteralPath (Join-Path $oldEvidence 'original.txt') -Value 'original evidence' -Encoding UTF8

    $settings = [ordered]@{
        'ASPNETCORE_ENVIRONMENT'='Development'; 'ASPNETCORE_URLS'="http://127.0.0.1:$seedApiPort"
        'ConnectionStrings__AeroLink'="Host=127.0.0.1;Port=$pgPort;Database=aerolink_source;Username=postgres"
        'Evidence__Root'=$sourceEvidence; 'DemoData__Enabled'='true'; 'Identity__SeedDemoAccounts'='true'; 'Identity__AllowDemoAccounts'='true'; 'Identity__CookieSecure'='false'
    }
    foreach ($item in $settings.GetEnumerator()) { $previous[$item.Key]=[Environment]::GetEnvironmentVariable($item.Key,'Process'); [Environment]::SetEnvironmentVariable($item.Key,$item.Value,'Process') }
    $apiProject = Join-Path $productRoot 'src\AeroLink.Api\AeroLink.Api.csproj'
    $api = Start-Process -FilePath 'dotnet' -ArgumentList "run --configuration Release --no-build --no-launch-profile --project `"$apiProject`"" `
        -WorkingDirectory $repositoryRoot -RedirectStandardOutput $apiOut -RedirectStandardError $apiErr -WindowStyle Hidden -PassThru
    $ready = $false
    for ($attempt=0;$attempt -lt 180;$attempt++) { if($api.HasExited){break};try{$response=Invoke-WebRequest -Uri "http://127.0.0.1:$seedApiPort/health/ready" -UseBasicParsing -TimeoutSec 2;if($response.StatusCode -eq 200){$ready=$true;break}}catch{};Start-Sleep -Milliseconds 500 }
    if(-not $ready){throw "Disposable seed API did not become ready. See $apiErr"}
    Stop-Process -Id $api.Id -Force; $api.WaitForExit(10000)|Out-Null; $api=$null
    foreach($item in $previous.GetEnumerator()){[Environment]::SetEnvironmentVariable($item.Key,$item.Value,'Process')};$previous=@{}

    $env:Evidence__Root = $sourceEvidence
    & (Join-Path $PSScriptRoot 'Backup-AeroLink.ps1') -RetentionDays 0 -Database aerolink_source -PostgresPort $pgPort `
        -BackupRoot $backupRoot -PostgresBin $PostgresBin -PostgresAlreadyRunning
    if ($LASTEXITCODE -ne 0) { throw 'Disposable configured-root backup failed.' }
    $archive = (Get-ChildItem -LiteralPath $backupRoot -Filter 'aerolink-*.zip' | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
    if (-not $archive) { throw 'Disposable backup archive was not produced.' }
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
    Remove-Item -LiteralPath $archiveUnpacked -Recurse -Force

    & (Join-Path $PSScriptRoot 'Restore-AeroLink.ps1') -BackupArchive $archive -TargetDatabase aerolink_restore_validation `
        -EvidenceTarget $isolatedEvidence -PostgresPort $pgPort -PostgresBin $PostgresBin -ValidationApiPort (Get-FreePort)
    if ($LASTEXITCODE -ne 0) { throw 'Isolated restore and API-download validation failed.' }
    $attachmentCount = (& (Join-Path $PostgresBin 'psql.exe') -h 127.0.0.1 -p $pgPort -U postgres -d aerolink_restore_validation -tA -c 'SELECT count(*) FROM controlled_attachments;').Trim()
    if ([int]$attachmentCount -lt 1) { throw 'The isolated restore qualified no controlled attachments.' }

    $faults=@('BeforeDatabaseRestore','AfterDatabaseRestore','AfterEvidenceCopy','AfterPreActivationValidation','AfterOriginalDatabaseRename','AfterDatabaseActivation','AfterEvidenceActivation','AfterActivationValidation','BeforeRestart','AfterRestart')
    foreach($phase in $faults){
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
    }

    & (Join-Path $PSScriptRoot 'Restore-AeroLink.ps1') -BackupArchive $archive -TargetDatabase aerolink `
        -EvidenceTarget $oldEvidence -PostgresPort $pgPort -PostgresBin $PostgresBin -ValidationApiPort (Get-FreePort) `
        -AllowProductionRestore -Confirmation RESTORE-AEROLINK -DisposableQualification
    if ($LASTEXITCODE -ne 0) { throw 'Disposable production activation qualification failed.' }
    $activatedCount=(& (Join-Path $PostgresBin 'psql.exe') -h 127.0.0.1 -p $pgPort -U postgres -d aerolink -tA -c 'SELECT count(*) FROM controlled_attachments;').Trim()
    if([int]$activatedCount -ne [int]$attachmentCount){throw 'Activated production attachment inventory differs from the isolated restore.'}
    $retainedDatabase=(& (Join-Path $PostgresBin 'psql.exe') -h 127.0.0.1 -p $pgPort -U postgres -d postgres -tA -c "SELECT count(*) FROM pg_database WHERE datname LIKE 'aerolink_pre_restore_%';").Trim()
    $retainedEvidence=@(Get-ChildItem -LiteralPath (Split-Path $oldEvidence -Parent) -Directory -Filter 'evidence-pre-restore-*')
    if([int]$retainedDatabase -lt 1 -or $retainedEvidence.Count -lt 1){throw 'Successful activation did not retain the prior database/evidence pair for rollback.'}

    [pscustomobject]@{Passed=$true;PersistentPortUntouched=($pgPort -ne 54329);PostgresPort=$pgPort;IsolatedAttachments=[int]$attachmentCount;ActivatedAttachments=[int]$activatedCount;FaultPhasesProved=$faults.Count;RollbackProved=$true;PriorDatabaseRetained=$true;PriorEvidenceRetained=$true}
    $global:LASTEXITCODE=0
}
finally {
    if($api -and -not $api.HasExited){Stop-Process -Id $api.Id -Force -ErrorAction SilentlyContinue}
    foreach($item in $previous.GetEnumerator()){[Environment]::SetEnvironmentVariable($item.Key,$item.Value,'Process')}
    [Environment]::SetEnvironmentVariable('Evidence__Root',$priorEvidenceRoot,'Process')
    if($postgresStarted){& (Join-Path $PostgresBin 'pg_ctl.exe') -D $data -m immediate -w stop | Out-Null}
    if(Test-Path -LiteralPath $root){Remove-Item -LiteralPath $root -Recurse -Force}
    if(Test-Path -LiteralPath $backupRoot){Remove-Item -LiteralPath $backupRoot -Recurse -Force}
    $isolatedParent=Split-Path $isolatedEvidence -Parent;if(Test-Path -LiteralPath $isolatedParent){Remove-Item -LiteralPath $isolatedParent -Recurse -Force}
}
