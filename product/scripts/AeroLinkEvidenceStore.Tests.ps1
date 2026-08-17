$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'AeroLinkEvidenceStore.psm1') -Force
$productRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$copyRoot = Join-Path ([IO.Path]::GetTempPath()) ('AeroLink long-path copy ' + [Guid]::NewGuid().ToString('N'))
$root = Join-Path ([IO.Path]::GetTempPath()) ("AeroLink evidence Ω spaces " + [Guid]::NewGuid().ToString('N'))

function Expect-Failure([scriptblock]$Action, [string]$Pattern) {
    try { & $Action; throw "Expected failure matching '$Pattern'." }
    catch { if ($_.Exception.Message -notlike "*$Pattern*") { throw } }
}

function Invoke-RobocopyChecked([string[]]$Arguments, [string]$FailureMessage) {
    & robocopy.exe @Arguments | Out-Null
    $exitCode = $LASTEXITCODE
    $global:LASTEXITCODE = 0
    if ($exitCode -ge 8) { throw "$FailureMessage (robocopy exit code $exitCode)." }
}

function Remove-LongPathTree([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $emptyRoot = Join-Path ([IO.Path]::GetTempPath()) ('AeroLink empty mirror ' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $emptyRoot -Force | Out-Null
    try {
        Invoke-RobocopyChecked @($emptyRoot, $Path, '/MIR', '/R:1', '/W:1', '/NFL', '/NDL', '/NJH', '/NJS', '/NP') 'Could not clean the long-path evidence fixture'
    }
    finally {
        if (Test-Path -LiteralPath $emptyRoot) { Remove-Item -LiteralPath $emptyRoot -Recurse -Force }
    }
    if (Test-Path -LiteralPath $Path) { Remove-Item -LiteralPath $Path -Recurse -Force }
}

try {
    New-Item -ItemType Directory -Path (Join-Path $root 'aa') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $root 'bb') -Force | Out-Null
    $bytes = [Text.Encoding]::UTF8.GetBytes('exact controlled evidence')
    [IO.File]::WriteAllBytes((Join-Path $root 'aa\first.docx'), $bytes)
    [IO.File]::WriteAllBytes((Join-Path $root 'bb\duplicate.docx'), $bytes)
    [IO.File]::WriteAllText((Join-Path $root 'orphan.bin'), 'unreferenced')
    $hash = (Get-FileHash -LiteralPath (Join-Path $root 'aa\first.docx') -Algorithm SHA256).Hash.ToLowerInvariant()
    $inventory = @(
        [pscustomobject]@{ Id=[Guid]::NewGuid(); StorageKey='aa/first.docx'; Size=$bytes.Length; Sha256=$hash; ArtifactType='ManagedDocument'; ArtifactId=[Guid]::NewGuid(); RevisionId=[Guid]::NewGuid() },
        [pscustomobject]@{ Id=[Guid]::NewGuid(); StorageKey='bb/duplicate.docx'; Size=$bytes.Length; Sha256=$hash; ArtifactType='ManagedDocument'; ArtifactId=[Guid]::NewGuid(); RevisionId=[Guid]::NewGuid() }
    )
    $result = Test-AeroLinkAttachmentInventory -Inventory $inventory -EvidenceRoot $root
    if ($result.ReferencedAttachments -ne 2 -or $result.ReferencedObjects -ne 2 -or $result.UnreferencedObjects.Count -ne 1) { throw 'Inventory counts did not preserve duplicate hashes and report the orphan separately.' }

    # Keep this a genuine > MAX_PATH regression, but construct and inspect it through robocopy rather than
    # System.IO. Windows PowerShell 5.1/.NET Framework can reject extended-length System.IO paths when the
    # machine-wide LongPathsEnabled policy is disabled, even though robocopy itself supports long paths.
    $segment = 'segment-' + ('x' * 52)
    $longRelativeDirectory = "$segment\$segment\$segment"
    $longRelative = "$longRelativeDirectory\retained-evidence.docx"
    $longSource = Join-Path $copyRoot 'source'
    $longDestination = Join-Path $copyRoot 'destination'
    $longSourceLeaf = Join-Path $longSource $longRelativeDirectory
    $longPath = Join-Path $longSource $longRelative
    if ($longPath.Length -le 260) { throw "The long-path fixture was not actually longer than MAX_PATH: $($longPath.Length) characters." }

    $seedRoot = Join-Path $copyRoot 'seed'
    New-Item -ItemType Directory -Path $seedRoot -Force | Out-Null
    [IO.File]::WriteAllBytes((Join-Path $seedRoot 'retained-evidence.docx'), $bytes)
    Invoke-RobocopyChecked @($seedRoot, $longSourceLeaf, 'retained-evidence.docx', '/R:1', '/W:1', '/NFL', '/NDL', '/NJH', '/NJS', '/NP') 'Could not create the genuine long-path evidence fixture'

    Copy-AeroLinkEvidenceTree -Source $longSource -Destination $longDestination

    # Read the copied object back through robocopy into a shallow path so the assertion does not rely on
    # System.IO long-path opt-in. This still proves that the destination contains the exact >260-character
    # evidence object and that its bytes survived the production copy helper.
    $verifyRoot = Join-Path $copyRoot 'verify'
    New-Item -ItemType Directory -Path $verifyRoot -Force | Out-Null
    $longDestinationLeaf = Join-Path $longDestination $longRelativeDirectory
    $longDestinationPath = Join-Path $longDestination $longRelative
    if ($longDestinationPath.Length -le 260) { throw 'The copied evidence path was not actually longer than MAX_PATH.' }
    Invoke-RobocopyChecked @($longDestinationLeaf, $verifyRoot, 'retained-evidence.docx', '/R:1', '/W:1', '/NFL', '/NDL', '/NJH', '/NJS', '/NP') 'The supported long-path evidence copy did not preserve the object'
    $verifiedPath = Join-Path $verifyRoot 'retained-evidence.docx'
    if (-not [IO.File]::Exists($verifiedPath)) { throw 'The supported long-path evidence copy did not preserve the object.' }
    $verifiedHash = (Get-FileHash -LiteralPath $verifiedPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($verifiedHash -ne $hash) { throw 'The supported long-path evidence copy changed the object bytes.' }

    $missing = @($inventory | ForEach-Object { $_.PSObject.Copy() }); $missing[0].StorageKey = 'aa/missing.docx'
    Expect-Failure { Test-AeroLinkAttachmentInventory -Inventory $missing -EvidenceRoot $root } 'missing'
    $wrongSize = @($inventory | ForEach-Object { $_.PSObject.Copy() }); $wrongSize[0].Size = $bytes.Length + 1
    Expect-Failure { Test-AeroLinkAttachmentInventory -Inventory $wrongSize -EvidenceRoot $root } 'size mismatch'
    $wrongHash = @($inventory | ForEach-Object { $_.PSObject.Copy() }); $wrongHash[0].Sha256 = ('0' * 64)
    Expect-Failure { Test-AeroLinkAttachmentInventory -Inventory $wrongHash -EvidenceRoot $root } 'hash mismatch'
    $unsafe = @($inventory | ForEach-Object { $_.PSObject.Copy() }); $unsafe[0].StorageKey = '../escape.docx'
    Expect-Failure { Test-AeroLinkAttachmentInventory -Inventory $unsafe -EvidenceRoot $root } 'Unsafe attachment storage key'

    $env:Evidence__Root = $root
    if ((Get-AeroLinkEvidenceRoot -ProductRoot $productRoot) -ne [IO.Path]::GetFullPath($root)) { throw 'Evidence__Root did not take precedence.' }
    Remove-Item Env:\Evidence__Root
    $default = Get-AeroLinkEvidenceRoot -ProductRoot $productRoot
    if ([string]::IsNullOrWhiteSpace($default) -or -not [IO.Path]::IsPathRooted($default)) { throw 'The default evidence root was not resolved canonically.' }
    [pscustomobject]@{ Passed=$true; ReferencedAttachments=$result.ReferencedAttachments; ReferencedObjects=$result.ReferencedObjects; UnreferencedObjects=$result.UnreferencedObjects.Count; CustomRoot=$root; DefaultRoot=$default }
    $global:LASTEXITCODE = 0
}
finally {
    Remove-Item Env:\Evidence__Root -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
    Remove-LongPathTree $copyRoot
}
