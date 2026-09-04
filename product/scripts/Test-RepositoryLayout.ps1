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
function Test-RepositoryContainment([string]$CandidatePath) {
    $fullPath = [IO.Path]::GetFullPath($CandidatePath)
    $rootWithSeparator = $RepositoryRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    return [string]::Equals($fullPath, $RepositoryRoot, [StringComparison]::OrdinalIgnoreCase) -or
        $fullPath.StartsWith($rootWithSeparator, [StringComparison]::OrdinalIgnoreCase)
}

# Repository policy applies to files Git can admit as repository content. Explicitly ignored, untracked
# scratch files remain local working-copy state; tracked files still receive the full policy even when
# their names also match an ignore pattern. Disposable non-Git fixtures are checked as plain directories.
$gitExecutable = $null
$gitTopLevel = $null
$gitCommand = Get-Command git.exe -ErrorAction SilentlyContinue
if (-not $gitCommand) { $gitCommand = Get-Command git -ErrorAction SilentlyContinue }
if ($gitCommand -and (Test-Path -LiteralPath (Join-Path $RepositoryRoot '.git'))) {
    $topLevelOutput = @(& $gitCommand.Source -C $RepositoryRoot rev-parse --show-toplevel 2>$null)
    if ($LASTEXITCODE -eq 0 -and $topLevelOutput.Count -gt 0) {
        $candidateTopLevel = [IO.Path]::GetFullPath(([string]$topLevelOutput[0]).Trim())
        if ([string]::Equals($candidateTopLevel.TrimEnd('\', '/'), $RepositoryRoot.TrimEnd('\', '/'), [StringComparison]::OrdinalIgnoreCase)) {
            $gitExecutable = $gitCommand.Source
            $gitTopLevel = $candidateTopLevel
        }
    }
}
function Test-GitIgnoredRootFile([string]$Name) {
    if (-not $gitExecutable -or -not $gitTopLevel) { return $false }
    & $gitExecutable -C $RepositoryRoot check-ignore --quiet -- $Name 2>$null
    return $LASTEXITCODE -eq 0
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

$rootMarkdown = @(
    Get-ChildItem -LiteralPath $RepositoryRoot -File -Filter '*.md' |
        Where-Object { -not (Test-GitIgnoredRootFile $_.Name) } |
        ForEach-Object Name
)
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
        if (-not $rawTarget -or $rawTarget.StartsWith('#')) { continue }
        try { $rawTarget = [Uri]::UnescapeDataString($rawTarget) } catch { continue }
        if ($rawTarget -match '^(?i:https?|mailto|data):') { continue }
        $candidateTarget = [IO.Path]::GetFullPath((Join-Path (Split-Path $shimPath) ($rawTarget -replace '/', '\')))
        if (-not (Test-RepositoryContainment $candidateTarget)) {
            Fail "Compatibility shim $($entry.Key) links outside the repository: $rawTarget"
            continue
        }
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
        if (-not $raw -or $raw.StartsWith('#')) { continue }
        $target = ($raw -split '#', 2)[0].Trim('<>')
        if (-not $target) { continue }
        try { $target = [Uri]::UnescapeDataString($target) } catch { Fail "Invalid URI escape in $path`: $raw"; continue }
        if ($target -match '^(?i:https?|mailto|data):') { continue }
        $resolved = [IO.Path]::GetFullPath((Join-Path (Split-Path $source) ($target -replace '/', '\')))
        if (-not (Test-RepositoryContainment $resolved)) {
            Fail "Maintained Markdown link escapes the repository in $path`: $raw"
            continue
        }
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
        $rawTarget = $match.Groups[1].Value.Trim()
        if (-not $rawTarget -or $rawTarget.StartsWith('#')) { continue }
        $target = ($rawTarget -split '#', 2)[0].Trim('<>')
        if (-not $target) { continue }
        try { $target = [Uri]::UnescapeDataString($target) } catch { Fail "Invalid URI escape in $sourceName`: $rawTarget"; continue }
        if ($target -match '^(?i:https?|mailto|data):') { continue }
        $resolved = [IO.Path]::GetFullPath((Join-Path (Split-Path $source) ($target -replace '/', '\')))
        if (-not (Test-RepositoryContainment $resolved)) {
            Fail "$sourceName contains a link outside the repository: $rawTarget"
            continue
        }
        $rootWithSeparator = $RepositoryRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
        $repositoryRelativeTarget = if ([string]::Equals($resolved, $RepositoryRoot, [StringComparison]::OrdinalIgnoreCase)) {
            ''
        } else {
            $resolved.Substring($rootWithSeparator.Length).Replace('\', '/')
        }
        if ($repositoryRelativeTarget -match '(?i)^docs/archive/(?!README\.md$)') {
            Fail "$sourceName must not link directly to an archived record as current authority: $target"
        }
    }
}

$requiredLaunchers = @(
    'AEROLINK_DIAGNOSTICS.bat', 'AEROLINK_REMOTE_DEMO_STATUS.bat', 'BACKUP_AEROLINK.bat',
    'CONFIGURE_AEROLINK_REMOTE_DEMO.bat', 'INSTALL_AEROLINK_DOCUMENT_CONNECTOR.bat', 'RESTORE_AEROLINK.bat',
    'SCHEDULE_AEROLINK_BACKUP.bat', 'START_AEROLINK_PRODUCTION.bat', 'START_AEROLINK_REMOTE_DEMO.bat',
    'START_AEROLINK_SHARED.bat', 'START_AEROLINK.bat', 'STOP_AEROLINK_REMOTE_DEMO.bat', 'STOP_AEROLINK.bat',
    'START_AEROLINK_EMAIL_DEMO.bat', 'START_AEROLINK_SMTP4DEV.bat', 'AEROLINK_SMTP4DEV_STATUS.bat',
    'STOP_AEROLINK_SMTP4DEV.bat', 'TEST_AEROLINK_CHANGED.bat', 'VERIFY_AEROLINK_BACKUP.bat',
    'CONFIGURE_AEROLINK_PRODUCTION_SOURCE.bat', 'REFRESH_AEROLINK_FROM_HOME.bat',
    'DECLARE_AEROLINK_INSTANCE.bat'
)
$actualBatCmd = @(Get-ChildItem -LiteralPath $RepositoryRoot -File | Where-Object { $_.Extension -in '.bat', '.cmd' } | ForEach-Object Name)
foreach ($name in $requiredLaunchers) { if ($name -notin $actualBatCmd) { Fail "Required root launcher is missing: $name" } }
foreach ($name in $actualBatCmd) { if ($name -notin $requiredLaunchers) { Fail "Unlisted root .bat/.cmd launcher: $name" } }
$operations = Join-Path $RepositoryRoot 'product\docs\OPERATIONS.md'
if (Test-Path -LiteralPath $operations -PathType Leaf) {
    $operationsText = [IO.File]::ReadAllText($operations)
    foreach ($name in $requiredLaunchers) { if ($operationsText -notmatch [regex]::Escape($name)) { Fail "product/docs/OPERATIONS.md does not mention launcher $name" } }
}

# Every launcher script must parse in the host that will run it.
#
# The supported launcher chain is Windows PowerShell 5.1, which reads a .ps1 with no byte-order mark as
# ANSI. A UTF-8 em dash inside a STRING LITERAL therefore decodes to three CP1252 characters, one of which
# is a smart quote — and the string silently runs to the end of the file. The failure appears as an
# unrelated syntax error hundreds of lines away, in a file whose author's editor showed nothing wrong. The
# same character in a comment is harmless, which is why the trap survives review. Parsing every script here
# catches it in CI rather than on the machine that was trying to recover a demo.
$scriptsRoot = Join-Path $RepositoryRoot 'product\scripts'
$launcherScripts = if (Test-Path -LiteralPath $scriptsRoot -PathType Container) {
    @(Get-ChildItem -LiteralPath $scriptsRoot -File | Where-Object { $_.Extension -in '.ps1', '.psm1' })
} else { @() }
foreach ($script in $launcherScripts) {
    $parseErrors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile($script.FullName, [ref]$null, [ref]$parseErrors)
    if ($parseErrors -and $parseErrors.Count -gt 0) {
        Fail "product/scripts/$($script.Name) does not parse in this PowerShell host: $($parseErrors[0].Message) (line $($parseErrors[0].Extent.StartLineNumber))"
    }
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Host "FAIL: $_" -ForegroundColor Red }
    Write-Host "Repository layout contract FAILED ($($failures.Count) failure(s))." -ForegroundColor Red
    exit 1
}
Write-Host "Repository layout contract passed (current docs, links, taxonomy, compatibility shims, and $($requiredLaunchers.Count) root launchers checked)." -ForegroundColor Green
exit 0
