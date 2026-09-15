$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'AeroLinkBackupRetention.psm1') -Force
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.IO.Compression
$root=Join-Path ([IO.Path]::GetTempPath()) ('aerolink-retention-test-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($root)
function New-Archive([datetime]$When,[string]$Folder='') {
    $dir=Join-Path $root $Folder; [void][IO.Directory]::CreateDirectory($dir)
    $path=Join-Path $dir ('aerolink-'+$When.ToString('yyyyMMdd-HHmmss')+'.zip')
    $zip=[IO.Compression.ZipFile]::Open($path,[IO.Compression.ZipArchiveMode]::Create)
    try {
        $entry=$zip.CreateEntry('manifest.json');$writer=New-Object IO.StreamWriter($entry.Open())
        try {$writer.Write((@{FormatVersion=2;CreatedAtUtc=$When.ToUniversalTime().ToString('o');Database=@{Name='aerolink'}}|ConvertTo-Json))}finally{$writer.Dispose()}
    }finally{$zip.Dispose()}
    ((Get-FileHash -LiteralPath $path).Hash+'  '+[IO.Path]::GetFileName($path)) | Set-Content -LiteralPath ($path+'.sha256')
    return $path
}
try {
    $now=[datetime]'2026-09-15T18:00:00'
    $expired=New-Archive $now.AddDays(-15)
    $boundary=New-Archive $now.Date.AddDays(-14).AddHours(2)
    $extra=New-Archive $now.Date.AddHours(2)
    $latest=New-Archive $now.Date.AddHours(10) 'checkpoint'
    $lock=Enter-AeroLinkBackupLock $root
    try {
        $refused=$false;try{$other=Enter-AeroLinkBackupLock $root;$other.Dispose()}catch{$refused=$true}
        if(-not $refused){throw 'Overlapping retention was accepted.'}
        $plan=@(Get-AeroLinkBackupRetentionPlan $root -Now $now)
        if(@($plan|Where-Object Action -eq 'Keep').Count -ne 2){throw 'Daily/calendar boundary selection failed.'}
        if(@(Get-ChildItem $root -Filter '*.zip' -Recurse).Count -ne 4){throw 'Preview mutated archives.'}
        $original=[IO.File]::ReadAllBytes($latest)
        [IO.File]::AppendAllText($latest,'tampered')
        $refused=$false;try{Invoke-AeroLinkBackupRetentionPlan $root $plan}catch{$refused=$true}
        if(-not $refused -or -not(Test-Path $expired) -or -not(Test-Path $extra)){throw 'Bad survivor did not preserve existing backups.'}
        [IO.File]::WriteAllBytes($latest,$original)
        $plan=@(Get-AeroLinkBackupRetentionPlan $root -Now $now)
        Invoke-AeroLinkBackupRetentionPlan $root $plan
        if((Test-Path $expired) -or (Test-Path ($expired+'.sha256')) -or (Test-Path $extra)){throw 'Surplus backups remained.'}
        if(-not(Test-Path $latest) -or -not(Test-Path $boundary)){throw 'Daily survivors were removed.'}
        $plan=@(Get-AeroLinkBackupRetentionPlan $root -Now $now)
        if(@($plan|Where-Object Action -eq 'Delete').Count){throw 'Retention is not idempotent.'}
    }finally{$lock.Dispose()}
    # A stale set alone must never be purged without a current recovery point.
    $plan=@(Get-AeroLinkBackupRetentionPlan $root -Now $now.AddDays(20))
    $refused=$false;try{Invoke-AeroLinkBackupRetentionPlan $root $plan}catch{$refused=$true}
    if(-not $refused){throw 'Retention removed the last recovery point.'}
    [pscustomobject]@{Passed=$true;DailyLimit=$true;FifteenDayBoundary=$true;NestedArchives=$true;CorruptionRefused=$true;OverlapRefused=$true}
}finally{
    $expected=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar
    if(-not [IO.Path]::GetFullPath($root).StartsWith($expected,[StringComparison]::OrdinalIgnoreCase)){throw 'Test cleanup escaped temp.'}
    Remove-Item -LiteralPath $root -Recurse -Force
}
