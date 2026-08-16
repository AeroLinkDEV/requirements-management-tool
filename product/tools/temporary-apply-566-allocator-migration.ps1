# Temporary branch-only helper for #566. Removes exactly four direct allocator methods from the API test
# class after equivalent Infrastructure tests and mutation sensitivity have been proven.
$ErrorActionPreference = 'Stop'
$path = 'product/tests/AeroLink.Api.Tests/IdentifierAllocationTests.cs'
$text = [IO.File]::ReadAllText($path)
$methods = @(
    'Two_allocations_before_either_record_is_saved_do_not_collide',
    'Each_prefix_numbers_independently_and_continuously_across_projects',
    'A_number_handed_out_is_not_returned_to_the_pool_when_its_record_is_never_written',
    'Attachment_versions_are_claimed_per_logical_file_and_never_repeat'
)

function Remove-FactMethod([string]$Source, [string]$MethodName) {
    $signature = "    public async Task $MethodName()"
    $signatureIndex = $Source.IndexOf($signature, [StringComparison]::Ordinal)
    if ($signatureIndex -lt 0) { throw "Method signature not found: $MethodName" }
    if ($Source.IndexOf($signature, $signatureIndex + 1, [StringComparison]::Ordinal) -ge 0) { throw "Method signature was not unique: $MethodName" }

    $attributeIndex = $Source.LastIndexOf('    [Fact]', $signatureIndex, [StringComparison]::Ordinal)
    if ($attributeIndex -lt 0) { throw "[Fact] attribute not found for: $MethodName" }
    $between = $Source.Substring($attributeIndex, $signatureIndex - $attributeIndex)
    if ($between -notmatch '^    \[Fact\]\r?\n$') { throw "Unexpected text between [Fact] and signature for: $MethodName" }

    $braceStart = $Source.IndexOf('{', $signatureIndex)
    if ($braceStart -lt 0) { throw "Opening brace not found for: $MethodName" }
    $depth = 0
    $braceEnd = -1
    for ($i = $braceStart; $i -lt $Source.Length; $i++) {
        if ($Source[$i] -eq '{') { $depth++ }
        elseif ($Source[$i] -eq '}') {
            $depth--
            if ($depth -eq 0) { $braceEnd = $i; break }
        }
    }
    if ($braceEnd -lt 0) { throw "Closing brace not found for: $MethodName" }

    $removeEnd = $braceEnd + 1
    if ($removeEnd -lt $Source.Length -and $Source[$removeEnd] -eq "`r") { $removeEnd++ }
    if ($removeEnd -lt $Source.Length -and $Source[$removeEnd] -eq "`n") { $removeEnd++ }
    if ($removeEnd -lt $Source.Length -and $Source[$removeEnd] -eq "`r") { $removeEnd++ }
    if ($removeEnd -lt $Source.Length -and $Source[$removeEnd] -eq "`n") { $removeEnd++ }

    return $Source.Remove($attributeIndex, $removeEnd - $attributeIndex)
}

foreach ($method in $methods) { $text = Remove-FactMethod $text $method }
foreach ($method in $methods) {
    if ($text.Contains("public async Task $method()")) { throw "Migrated method remained in API class: $method" }
}
foreach ($required in @(
    'Authoring_context_previews_do_not_claim_controlled_numbers',
    'An_existing_database_starts_numbering_past_what_it_already_recorded',
    'Two_uploads_of_one_logical_file_leave_exactly_one_active_version',
    'Concurrent_allocations_of_one_prefix_all_receive_distinct_numbers'
)) {
    if (-not $text.Contains("public async Task $required()")) { throw "Hosted boundary test was accidentally removed: $required" }
}

[IO.File]::WriteAllText($path, $text, [Text.UTF8Encoding]::new($false))
git diff --check
if ($LASTEXITCODE -ne 0) { throw 'git diff --check failed after API test migration.' }
Write-Host 'Removed exactly four direct allocator methods from AeroLink.Api.Tests; hosted boundary methods remain.'
