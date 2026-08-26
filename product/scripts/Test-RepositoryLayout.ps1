#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$RepositoryRoot
)

$ErrorActionPreference = 'Stop'
if (-not $RepositoryRoot) {
    $RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
}
$RepositoryRoot = [IO.Path]::GetFullPath($RepositoryRoot)
if (-not (Test-Path -LiteralPath $RepositoryRoot -PathType Container)) {
    throw "Repository root does not exist: $RepositoryRoot"
}

$failures = [System.Collections.Generic.List[string]]::new()
function Fail([string]$Message) { [void]$failures.Add($Message) }
function Require-File([string]$RelativePath) {
    $path = Join-Path $RepositoryRoot ($RelativePath -replace '/', '\')
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { Fail "Required file is missing: $RelativePath" }
}
function Require-Directory([string]$RelativePath) {
    $path = Join-Path $RepositoryRoot ($RelativePath -replace '/', '\')
    if (-not (Test-Path -LiteralPath $path -PathType Container)) { Fail "Required documentation home is missing: $RelativePath" }
}

# The five canonical root files, nine intentionally retained relocation shims, and conventional GitHub
# community files are the complete root Markdown allow-list. New narrative belongs under docs/ or product/docs.
$canonicalRootMarkdown = @(
    'README.md', 'PROJECT_STATE.md', 'AGENTS.md', 'CLAUDE.md', 'DECISIONS_AND_OPEN_QUESTIONS.md'
)
$compatibilityShims = [ordered]@{
    'AEROLINK_3_ENTERPRISE_LIFECYCLE_COMPLETION.md' = 'docs/product-definition/AEROLINK_3_ENTERPRISE_LIFECYCLE_COMPLETION.md'
    'CURRENT_PRODUCT_HANDOFF_2026-07-29.md' = 'docs/archive/CURRENT_PRODUCT_HANDOFF_2026-07-29.md'
    'DESIGN_VISION_AND_DASHBOARDS.md' = 'docs/product-definition/DESIGN_VISION_AND_DASHBOARDS.md'
    'ENTERPRISE_REQUIREMENTS_MANAGEMENT_BENCHMARK.md' = 'docs/reference/ENTERPRISE_REQUIREMENTS_MANAGEMENT_BENCHMARK.md'
    'FEATURE_CATALOG.md' = 'docs/product-definition/FEATURE_CATALOG.md'
    'IDENTIFIERS_AND_REQUIREMENT_FIELDS_PROPOSAL.md' = 'docs/product-definition/IDENTIFIERS_AND_REQUIREMENT_FIELDS_PROPOSAL.md'
    'SECURITY_AND_IDENTITY_MODEL.md' = 'docs/product-definition/SECURITY_AND_IDENTITY_MODEL.md'
    'SHOWCASE_STORY_FMS_3_3.md' = 'docs/archive/SHOWCASE_STORY_FMS_3_3.md'
    'SYSTEM_LEVEL_WORKFLOW.md' = 'docs/product-definition/SYSTEM_LEVEL_WORKFLOW.md'
}
$communityMarkdown = @('CODE_OF_CONDUCT.md', 'CONTRIBUTING.md', 'GOVERNANCE.md', 'LICENSE.md', 'SECURITY.md', 'SUPPORT.md')
$allowedRootMarkdown = @($canonicalRootMarkdown + @($compatibilityShims.Keys) + $communityMarkdown)

foreach ($path in $canonicalRootMarkdown) { Require-File $path }
foreach ($path in $compatibilityShims.Keys) { Require-File $path }
Require-File 'docs/README.md'
Require-File 'docs/archive/README.md'
foreach ($path in @('docs/product-definition', 'docs/reference', 'docs/showcase', 'docs/provenance', 'docs/archive')) { Require-Directory $path }

$rootMarkdown = @(Get-ChildItem -LiteralPath $RepositoryRoot -File -Filter '*.md' | ForEach-Object Name)
foreach ($name in $rootMarkdown) {
    if ($name -notin $allowedRootMarkdown) {
        if ($name -match '(?i)^CURRENT_PRODUCT_HANDOFF_\d{4}-\d{2}-\d{2}\.md$') {
            Fail "Dated handoff is not permitted at repository root: $name. Move it to docs/archive/; only CURRENT_PRODUCT_HANDOFF_2026-07-29.md remains as an accepted compatibility redirect."
        } elseif ($name -match '(?i)(audit|handoff|status|work.?log|report)') {
            Fail "Historical narrative is not permitted at repository root: $name. Move it to docs/archive/ and index it there."
        } else {
            Fail "Unapproved root Markdown file: $name. Add it only with an explicit current-authority or compatibility reason, or place it under the appropriate docs/ home."
        }
    }
}

foreach ($entry in $compatibilityShims.GetEnumerator()) {
    $shimPath = Join-Path $RepositoryRoot $entry.Key
    $targetPath = Join-Path $RepositoryRoot ($entry.Value -replace '/', '\')
    if (-not (Test-Path -LiteralPath $targetPath -PathType Leaf)) { Fail "Compatibility shim $($entry.Key) points to missing target: $($entry.Value)"; continue }
    $shimText = [IO.File]::ReadAllText($shimPath)
    if ($shimText.Length -gt 4096) { Fail "Compatibility shim $($entry.Key) is too large ($($shimText.Length) characters); keep it a bounded redirect." }
    if ($shimText -notmatch '(?i)compatibility (?:pointer|redirect)|relocation shim') { Fail "Compatibility shim $($entry.Key) must explicitly identify itself as a compatibility pointer or redirect." }
    if ($shimText -notmatch '(?i)non-authoritative|\bnot\b.{0,160}\b(?:authority|guidance|current|source|backlog|truth|status)\b|never current') { Fail "Compatibility shim $($entry.Key) must explicitly say it is non-authoritative or not current." }
    $targetFull = [IO.Path]::GetFullPath($targetPath)
    $targetLinkFound = $false
    foreach ($match in [regex]::Matches($shimText, '\[[^\]]*\]\(([^)]+)\)')) {
        $rawTarget = ($match.Groups[1].Value.Trim() -split '#', 2)[0].Trim('<>')
        if (-not $rawTarget -or $rawTarget -match '^(?i:https?|mailto|data):' -or $rawTarget.StartsWith('#')) { continue }
        try { $rawTarget = [Uri]::UnescapeDataString($rawTarget) } catch { continue }
        $candidateTarget = [IO.Path]::GetFullPath((Join-Path (Split-Path $shimPath) ($rawTarget -replace '/', '\')))
        if ($candidateTarget -eq $targetFull) { $targetLinkFound = $true; break }
    }
    if (-not $targetLinkFound) { Fail "Compatibility shim $($entry.Key) does not link to its declared target: $($entry.Value)" }
}

# Keep this list deliberately visible: it is the maintained current-document scope for relative-link checks.
$maintainedMarkdown = [System.Collections.Generic.List[string]]::new()
foreach ($path in $canonicalRootMarkdown) { [void]$maintainedMarkdown.Add($path) }
foreach ($path in $compatibilityShims.Keys) { [void]$maintainedMarkdown.Add($path) }
foreach ($path in @('docs/README.md', 'docs/ENGINEERING_LESSONS.md', 'docs/PROJECT_HISTORY.md', 'docs/REMOTE_DEMO_OPERATOR.md', 'docs/archive/README.md', 'docs/provenance/README.md', 'docs/provenance/SOURCE_MATERIAL_TRACEABILITY.md', 'product/README.md', 'product/docs/README.md', 'product/docs/OPERATIONS.md', 'product/docs/MERGING.md')) { [void]$maintainedMarkdown.Add($path) }
foreach ($directory in @('docs/product-definition', 'docs/reference', 'docs/showcase')) {
    Get-ChildItem -LiteralPath (Join-Path $RepositoryRoot ($directory -replace '/', '\')) -File -Filter '*.md' -Recurse | ForEach-Object {
        $full = [IO.Path]::GetFullPath($_.FullName)
        $relative = $full.Substring($RepositoryRoot.Length).TrimStart('\', '/')
        [void]$maintainedMarkdown.Add($relative.Replace('\', '/'))
    }
}
foreach ($path in $maintainedMarkdown | Select-Object -Unique) {
    $source = Join-Path $RepositoryRoot ($path -replace '/', '\')
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { Fail "Maintained Markdown file is missing from the declared link-check scope: $path"; continue }
    $text = [IO.File]::ReadAllText($source)
    foreach ($match in [regex]::Matches($text, '\[[^\]]*\]\(([^)]+)\)')) {
        $raw = $match.Groups[1].Value.Trim()
        if (-not $raw -or $raw -match '^(?i:https?|mailto|data):' -or $raw.StartsWith('#')) { continue }
        $target = ($raw -split '#', 2)[0].Trim('<>')
        if (-not $target) { continue }
        try { $target = [Uri]::UnescapeDataString($target) } catch { Fail "Invalid URI escape in $path`: $raw"; continue }
        $resolved = [IO.Path]::GetFullPath((Join-Path (Split-Path $source) ($target -replace '/', '\')))
        if (-not (Test-Path -LiteralPath $resolved)) { Fail "Broken maintained Markdown link in $path`: $raw" }
    }
}

# The archive index may link to individual historical records. The two current front-door documents may only
# point to the archive index, never present an archived handoff/status report as current guidance.
foreach ($sourceName in @('README.md', 'PROJECT_STATE.md')) {
    $source = Join-Path $RepositoryRoot $sourceName
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { continue }
    $text = [IO.File]::ReadAllText($source)
    foreach ($match in [regex]::Matches($text, '\[[^\]]*\]\(([^)]+)\)')) {
        $target = ($match.Groups[1].Value.Trim() -split '#', 2)[0].Trim('<>')
        if ($target -match '(?i)(?:^|/)docs/archive/(?!README\.md(?:$|#))') {
            Fail "$sourceName must not link directly to an archived record as current authority: $target"
        }
    }
}

$requiredLaunchers = @(
    'AEROLINK_DIAGNOSTICS.bat', 'AEROLINK_REMOTE_DEMO_STATUS.bat', 'BACKUP_AEROLINK.bat',
    'CONFIGURE_AEROLINK_REMOTE_DEMO.bat', 'INSTALL_AEROLINK_DOCUMENT_CONNECTOR.bat', 'RESTORE_AEROLINK.bat',
    'SCHEDULE_AEROLINK_BACKUP.bat', 'START_AEROLINK_PRODUCTION.bat', 'START_AEROLINK_REMOTE_DEMO.bat',
    'START_AEROLINK_SHARED.bat', 'START_AEROLINK.bat', 'STOP_AEROLINK_REMOTE_DEMO.bat', 'STOP_AEROLINK.bat',
    'TEST_AEROLINK_CHANGED.bat', 'VERIFY_AEROLINK_BACKUP.bat'
)
$actualBatCmd = @(Get-ChildItem -LiteralPath $RepositoryRoot -File | Where-Object { $_.Extension -in '.bat', '.cmd' } | ForEach-Object Name)
foreach ($name in $requiredLaunchers) { if ($name -notin $actualBatCmd) { Fail "Required root launcher is missing: $name" } }
foreach ($name in $actualBatCmd) { if ($name -notin $requiredLaunchers) { Fail "Unlisted root .bat/.cmd launcher: $name" } }
$operations = Join-Path $RepositoryRoot 'product\docs\OPERATIONS.md'
if (Test-Path -LiteralPath $operations -PathType Leaf) {
    $operationsText = [IO.File]::ReadAllText($operations)
    foreach ($name in $requiredLaunchers) { if ($operationsText -notmatch [regex]::Escape($name)) { Fail "product/docs/OPERATIONS.md does not mention launcher $name" } }
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Host "FAIL: $_" -ForegroundColor Red }
    Write-Host "Repository layout contract FAILED ($($failures.Count) failure(s))." -ForegroundColor Red
    exit 1
}
Write-Host "Repository layout contract passed (current docs, links, taxonomy, compatibility shims, and $($requiredLaunchers.Count) root launchers checked)." -ForegroundColor Green
exit 0
