[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$built = & (Join-Path $PSScriptRoot 'Build-Extractor.ps1') -OutputDirectory (Join-Path $outputRoot 'classes')
& $built.Java -cp $built.ClassPath $built.MainClass fixture (Join-Path $PSScriptRoot 'fixtures/config.json') (Join-Path $outputRoot 'checkpoint') (Join-Path $outputRoot 'source.zip')
if ($LASTEXITCODE -ne 0) { throw 'Fixture extraction failed' }
