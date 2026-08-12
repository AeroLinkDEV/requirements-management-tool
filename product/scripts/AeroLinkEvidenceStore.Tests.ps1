$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'AeroLinkEvidenceStore.psm1') -Force
$productRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$root = Join-Path ([IO.Path]::GetTempPath()) ("AeroLink evidence Ω spaces " + [Guid]::NewGuid().ToString('N'))

function Expect-Failure([scriptblock]$Action, [string]$Pattern) {
    try { & $Action; throw "Expected failure matching '$Pattern'." }
    catch { if ($_.Exception.Message -notlike "*$Pattern*") { throw } }
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
}
finally {
    Remove-Item Env:\Evidence__Root -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
