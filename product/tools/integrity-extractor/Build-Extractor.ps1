[CmdletBinding()]
param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'build'), [string]$MksApiJar)
$ErrorActionPreference = 'Stop'
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
$jdkPath = if ($env:JAVA_HOME) { Join-Path $env:JAVA_HOME 'bin' } elseif ($env:JAVA_HOME_17_X64) { Join-Path $env:JAVA_HOME_17_X64 'bin' } else { '' }
$compiler = if ($jdkPath) { Join-Path $jdkPath 'javac' } else { 'javac' }
$java = if ($jdkPath) { Join-Path $jdkPath 'java' } else { 'java' }
# Pinned jars are downloaded once per machine into a checksum-verified cache, not once per build: every test
# fixture builds into a fresh directory, and Maven Central rate-limits shared CI runner addresses with 429 (#1378).
$dependencyCache = if ($env:AEROLINK_DEPENDENCY_CACHE) { $env:AEROLINK_DEPENDENCY_CACHE } else { Join-Path ([IO.Path]::GetTempPath()) 'aerolink-dependency-cache' }
New-Item -ItemType Directory -Force -Path $dependencyCache | Out-Null
function Test-Sha256([string]$Path, [string]$Sha256) {
    (Test-Path -LiteralPath $Path) -and (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -eq $Sha256
}
function Get-VerifiedDependency([string]$Name, [string]$Uri, [string]$Sha256) {
    $cached = Join-Path $dependencyCache $Name
    if (!(Test-Sha256 $cached $Sha256)) {
        # Download beside the cache entry and move it in only once verified, so a concurrent build never reads a
        # partial file. Rate limits, server errors and dropped connections are retried with backoff; a 404 or a
        # checksum mismatch is not.
        $partial = Join-Path $dependencyCache ("$Name." + [Guid]::NewGuid().ToString('N') + '.partial')
        for ($attempt = 1; ; $attempt++) {
            try { Invoke-WebRequest -Uri $Uri -OutFile $partial; break }
            catch {
                $status = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 0 }
                if (($status -ne 0 -and $status -ne 429 -and $status -lt 500) -or $attempt -ge 5) { throw }
                Start-Sleep -Seconds ([Math]::Pow(2, $attempt))
            }
        }
        if (!(Test-Sha256 $partial $Sha256)) { Remove-Item -LiteralPath $partial -Force; throw "Dependency checksum mismatch: $Name" }
        try { Move-Item -LiteralPath $partial -Destination $cached -Force }
        catch { if (!(Test-Sha256 $cached $Sha256)) { throw } }
        finally { if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Force } }
    }
    $path = Join-Path $outputRoot $Name
    Copy-Item -LiteralPath $cached -Destination $path -Force
    if (!(Test-Sha256 $path $Sha256)) { throw "Dependency checksum mismatch: $Name" }
    return $path
}
$gson = Get-VerifiedDependency 'gson-2.13.2.jar' 'https://repo.maven.apache.org/maven2/com/google/code/gson/gson/2.13.2/gson-2.13.2.jar' 'dd0ce1b55a3ed2080cb70f9c655850cda86c206862310009dcb5e5c95265a5e0'
if (!$MksApiJar) {
    $MksApiJar = Get-VerifiedDependency 'mksapi-4.16.2671.jar' 'https://repo.maven.apache.org/maven2/com/mks/api/mksapi-jar/4.16.2671/mksapi-jar-4.16.2671.jar' '7a64d5c9c0c5cb76b57cb9328fab82743b879c975ddcfc03f15a8754ccaed4d3'
}
$classPath = $outputRoot, $gson, ([IO.Path]::GetFullPath($MksApiJar)) -join [IO.Path]::PathSeparator
$sources = Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src/aerolink/integrity') -Filter '*.java' | Select-Object -ExpandProperty FullName
& $compiler --release 17 -encoding UTF-8 -cp $classPath -d $outputRoot @sources
if ($LASTEXITCODE -ne 0) { throw 'Integrity extractor compilation failed.' }
[pscustomobject]@{ Java = $java; ClassPath = $classPath; MainClass = 'aerolink.integrity.Extractor' }
