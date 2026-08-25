#Requires -Version 5.1
<#
    Durable diagnostic capture for dotnet-test qualification suites (#756).

    Console summaries are not the evidence authority: a failing suite must leave a machine-readable TRX
    (and hang/blame evidence when a test wedges) on disk, with the exact path reported to the operator, so
    a truncated console buffer can never again make a mass failure undiagnosable.

    The root deliberately lives under the machine's temporary directory, not product/.local: the planner's
    published contract says it never writes under product/.local, and that promise is worth keeping. The
    trade-off is documented here rather than hidden - temporary-directory diagnostics are durable for the
    qualification session and are named immediately on failure, which is what #756 requires; they are not
    long-term archival records.

    The appended arguments are the standard dotnet-test contracts, not a custom format:
      --logger "trx;LogFileName=<slug>.trx"   one deterministic TRX per suite per run
      --results-directory <unique root>       unique per label and wall-clock second, so parallel shards
                                              and reruns can never overwrite one another
      --blame-hang-timeout 15m                zero cost while every test completes; on a genuine hang,
                                              dotnet writes blame evidence and fails the run instead of
                                              hanging until an outer timeout kills it silently

    Secret-safety: diagnostics stay on the local machine (local runs) or inside the repository's private
    CI artifact store with bounded retention (CI runs). The suites' credentials are synthetic test values;
    no production secret ever flows through these paths.
#>

function Resolve-TestDiagnosticsRoot {
    param([Parameter(Mandatory)][string]$Label)

    $slug = ConvertTo-TestDiagnosticsSlug -Label $Label
    $stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss')
    $root = Join-Path ([System.IO.Path]::GetTempPath()) (Join-Path 'aerolink-test-diagnostics' "$stamp-$slug")
    New-Item -ItemType Directory -Path $root -Force | Out-Null
    return $root
}

function Invoke-TestSuiteWithDiagnostics {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$Arguments,
        [Parameter(Mandatory)][string]$Label,
        [string]$HangTimeout = '15m')

    $slug = ConvertTo-TestDiagnosticsSlug -Label $Label
    $root = Resolve-TestDiagnosticsRoot -Label $Label
    $trxName = "$slug.trx"

    # The diagnostic arguments are appended after the caller's arguments so the caller keeps owning suite
    # selection (project, configuration, filters); the appended logger/results/blame contract is what the
    # synthetic regression fixture asserts against.
    $fullArguments = @($Arguments) + @(
        '--logger', "trx;LogFileName=$trxName",
        '--results-directory', $root,
        '--blame-hang-timeout', $HangTimeout)

    & $FilePath @fullArguments
    $exitCode = $LASTEXITCODE
    Write-Host "Durable test diagnostics ($Label): $root"
    if ($exitCode -ne 0) {
        # The original exit status is preserved by rethrowing after diagnostics are reported: collection
        # and reporting can never convert a failing run into a passing one.
        throw "$FilePath exited with code $exitCode. Durable test diagnostics ($Label): $root"
    }
}

function ConvertTo-TestDiagnosticsSlug {
    param([Parameter(Mandatory)][string]$Label)
    $slug = ($Label -replace '[^A-Za-z0-9]+', '-').Trim('-').ToLowerInvariant()
    if (-not $slug) { $slug = 'suite' }
    return $slug
}

Export-ModuleMember -Function Resolve-TestDiagnosticsRoot, Invoke-TestSuiteWithDiagnostics, ConvertTo-TestDiagnosticsSlug
