[CmdletBinding()]
param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'build'), [string]$MksApiJar)
$ErrorActionPreference = 'Stop'
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
$jdkPath = if ($env:JAVA_HOME) { Join-Path $env:JAVA_HOME 'bin' } elseif ($env:JAVA_HOME_17_X64) { Join-Path $env:JAVA_HOME_17_X64 'bin' } else { '' }
$compiler = if ($jdkPath) { Join-Path $jdkPath 'javac' } else { 'javac' }
$java = if ($jdkPath) { Join-Path $jdkPath 'java' } else { 'java' }
function Get-VerifiedDependency([string]$Name, [string]$Uri, [string]$Sha256) {
    $path = Join-Path $outputRoot $Name
    if (!(Test-Path -LiteralPath $path)) { Invoke-WebRequest -Uri $Uri -OutFile $path }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $Sha256) { throw "Dependency checksum mismatch: $Name" }
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
