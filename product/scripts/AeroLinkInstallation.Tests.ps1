#Requires -Version 5.1
<#
    Contract coverage for installation identity (#881).

    The failure this suite exists to make impossible: relocating the production SOURCE quietly creating a
    second, empty AeroLink installation - a demo that starts perfectly, passes every health check, and holds
    none of the operator's data.

    Every scenario runs against disposable directories under the machine temp directory. No test connects to
    PostgreSQL, reads or writes the persistent product\.local, touches evidence, or starts a product process.
#>
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'AeroLinkInstallation.psm1') -Force

$failures = [System.Collections.Generic.List[string]]::new()
$fixtures = [System.Collections.Generic.List[string]]::new()

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { $script:failures.Add($Message) }
}
function Assert-Throws([scriptblock]$Action, [string]$Pattern, [string]$Message) {
    try { & $Action | Out-Null }
    catch {
        if ($_.Exception.Message -match $Pattern) { return }
        $script:failures.Add("$Message (threw, but the message did not match '$Pattern': $($_.Exception.Message))")
        return
    }
    $script:failures.Add("$Message (nothing was thrown)")
}

function New-FixtureProductRoot {
    param([switch]$WithLocal)
    $root = Join-Path ([IO.Path]::GetTempPath()) ("aerolink-installation-" + [Guid]::NewGuid().ToString('N'))
    $script:fixtures.Add($root)
    $productRoot = Join-Path $root 'product'
    New-Item -ItemType Directory -Path $productRoot -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $productRoot 'src\AeroLink.Api') -Force | Out-Null
    # The connection string the identity fingerprint reads. Loopback, no credential.
    @'
{ "ConnectionStrings": { "AeroLink": "Host=127.0.0.1;Port=54329;Database=aerolink;Username=postgres" } }
'@ | Set-Content -LiteralPath (Join-Path $productRoot 'src\AeroLink.Api\appsettings.Development.json') -Encoding UTF8
    if ($WithLocal) { New-Item -ItemType Directory -Path (Join-Path $productRoot '.local') -Force | Out-Null }
    return $productRoot
}

$previousOverride = $env:AEROLINK_INSTALLATION_ROOT
$env:AEROLINK_INSTALLATION_ROOT = $null

try {
    # --- 1. An ordinary clone with no pointer resolves exactly as it always did ---
    $productRoot = New-FixtureProductRoot
    $paths = Get-AeroLinkInstallationPaths -ProductRoot $productRoot
    Assert-True ($paths.InstallationRoot -eq [IO.Path]::GetFullPath((Join-Path $productRoot '.local'))) `
        'A checkout with no pointer must resolve to its own product\.local, unchanged from before #881.'
    Assert-True (-not $paths.IsRelocated) 'A checkout with no pointer is not relocated.'
    Assert-True ($paths.PostgresData -eq (Join-Path $paths.InstallationRoot 'pgdata')) 'pgdata hangs off the installation root.'
    Assert-True ($paths.PostgresBin -eq (Join-Path $paths.InstallationRoot 'postgresql\pgsql\bin')) 'The PostgreSQL client binaries hang off the installation root.'

    # --- 2. A pointer relocates every persistent path together ---
    $canonicalProduct = New-FixtureProductRoot -WithLocal
    $canonicalInstallation = Join-Path $canonicalProduct '.local'
    New-Item -ItemType Directory -Path (Join-Path $canonicalInstallation 'pgdata') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $canonicalInstallation 'pgdata\PG_VERSION') -Value '18' -Encoding ASCII
    New-Item -ItemType Directory -Path (Join-Path $canonicalInstallation 'backups') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $canonicalInstallation 'backups\aerolink-20260901-120000.zip') -Value 'x' -Encoding ASCII

    $productionProduct = New-FixtureProductRoot
    Set-AeroLinkInstallationPointer -ProductRoot $productionProduct -InstallationRoot $canonicalInstallation | Out-Null
    $productionPaths = Get-AeroLinkInstallationPaths -ProductRoot $productionProduct
    Assert-True ($productionPaths.InstallationRoot -eq [IO.Path]::GetFullPath($canonicalInstallation)) `
        'A pointer must make a second source checkout resolve to the canonical installation.'
    Assert-True ($productionPaths.IsRelocated) 'A pointed checkout reports itself relocated.'
    Assert-True ($productionPaths.PostgresData -eq (Join-Path $canonicalInstallation 'pgdata')) `
        'The relocated checkout must address the canonical cluster, not one of its own.'

    # --- 3. Relocation changes nothing about installation IDENTITY. This is acceptance 66-70. ---
    $canonicalIdentity = Get-AeroLinkInstallationIdentity -ProductRoot $canonicalProduct
    $productionIdentity = Get-AeroLinkInstallationIdentity -ProductRoot $productionProduct
    Assert-True ($productionIdentity.PostgresData -eq $canonicalIdentity.PostgresData) 'Moving source must not move the PostgreSQL cluster.'
    Assert-True ($productionIdentity.PostgresClusterInitialized -and $canonicalIdentity.PostgresClusterInitialized) `
        'The relocated checkout must see the SAME initialized cluster; a fresh one would be the second installation this guards against.'
    Assert-True ($productionIdentity.BackupRoot -eq $canonicalIdentity.BackupRoot) 'Moving source must not move the backup root.'
    Assert-True ($productionIdentity.BackupArchiveCount -eq 1 -and $canonicalIdentity.BackupArchiveCount -eq 1) `
        'Existing backups must remain visible from the relocated source.'
    Assert-True ($productionIdentity.DatabaseName -eq 'aerolink' -and $productionIdentity.DatabaseName -eq $canonicalIdentity.DatabaseName) `
        'The canonical database name must be identical before and after the source move.'
    Assert-True ($productionIdentity.DatabasePort -eq $canonicalIdentity.DatabasePort) 'The canonical database endpoint must not change with the source.'
    Assert-True ($productionIdentity.EvidenceRoot -eq $canonicalIdentity.EvidenceRoot) 'The evidence root must not change with the source.'

    # --- 4. Identity carries no credential ---
    $identityText = ($productionIdentity | ConvertTo-Json -Depth 4)
    Assert-True ($identityText -notmatch '(?i)username|password|user id') `
        'The installation identity must report the database by name and endpoint only, never a credential.'

    # --- 5. A dangling pointer FAILS CLOSED. Falling back is how the empty installation gets created. ---
    $danglingProduct = New-FixtureProductRoot -WithLocal
    [pscustomobject]@{ installationRoot = 'C:\AeroLink\definitely-not-here' } | ConvertTo-Json |
        Set-Content -LiteralPath (Get-AeroLinkInstallationPointerPath -ProductRoot $danglingProduct) -Encoding UTF8
    Assert-Throws { Get-AeroLinkInstallationRoot -ProductRoot $danglingProduct } 'will not create a second installation' `
        'A pointer naming a missing installation root must refuse, never silently fall back to this checkout.'

    # --- 6. A malformed or empty pointer refuses too ---
    $malformedProduct = New-FixtureProductRoot -WithLocal
    Set-Content -LiteralPath (Get-AeroLinkInstallationPointerPath -ProductRoot $malformedProduct) -Value '{ not json' -Encoding UTF8
    Assert-Throws { Get-AeroLinkInstallationRoot -ProductRoot $malformedProduct } 'malformed' 'A malformed pointer must refuse.'
    Set-Content -LiteralPath (Get-AeroLinkInstallationPointerPath -ProductRoot $malformedProduct) -Value '{}' -Encoding UTF8
    Assert-Throws { Get-AeroLinkInstallationRoot -ProductRoot $malformedProduct } 'does not name an installationRoot' 'A pointer naming nothing must refuse.'
    [pscustomobject]@{ installationRoot = 'relative\path' } | ConvertTo-Json |
        Set-Content -LiteralPath (Get-AeroLinkInstallationPointerPath -ProductRoot $malformedProduct) -Encoding UTF8
    Assert-Throws { Get-AeroLinkInstallationRoot -ProductRoot $malformedProduct } 'absolute' 'A relative installation root must refuse.'

    # --- 7. Pointing at a root that does not exist is refused at WRITE time as well as at read time ---
    Assert-Throws { Set-AeroLinkInstallationPointer -ProductRoot (New-FixtureProductRoot) -InstallationRoot 'C:\AeroLink\nowhere' } `
        'does not exist' 'Writing a pointer to a missing installation root must refuse.'

    # --- 8. Instance identity is declared, never inferred ---
    $undeclared = New-FixtureProductRoot -WithLocal
    $devInstance = Get-AeroLinkInstanceConfig -ProductRoot $undeclared -Mode Development
    Assert-True ($devInstance.Classification -eq 'Undeclared') 'An installation that declared nothing must not claim a classification.'
    Assert-True ($devInstance.Label -eq 'LOCAL DEVELOPMENT') 'An undeclared development launch gets a modest default label.'
    $prodInstance = Get-AeroLinkInstanceConfig -ProductRoot $undeclared -Mode HomeCanonical
    Assert-True ($prodInstance.Label -eq 'LOCAL PRODUCTION' -and $prodInstance.Classification -eq 'Undeclared') `
        'A production-mode launch of an undeclared installation must NOT call itself HOME CANONICAL.'

    Set-AeroLinkInstanceConfig -ProductRoot $undeclared -Label 'HOME CANONICAL' -Classification 'HomeCanonical' | Out-Null
    $declared = Get-AeroLinkInstanceConfig -ProductRoot $undeclared -Mode HomeCanonical
    Assert-True ($declared.Label -eq 'HOME CANONICAL' -and $declared.Classification -eq 'HomeCanonical' -and $declared.Declared) `
        'A declared instance must report exactly what the operator declared.'

    # A stable instance identifier, minted once and never changing.
    #
    # #881 asks for one alongside source, mode and classification. It answers what a label cannot: two
    # installations can both be labelled WORK-LAPTOP LOCAL, and a restored snapshot carries the source's
    # label with it.
    # Minting is deliberate, not a side effect of reading. A getter that writes made
    # REFRESH_AEROLINK_FROM_HOME.bat Preview change a file on disk while reporting nothing had changed, and
    # "read-only" has to be literally true to be worth saying.
    $identified = New-FixtureProductRoot -WithLocal
    $pureRead = Get-AeroLinkInstanceConfig -ProductRoot $identified -Mode Development
    Assert-True ([string]::IsNullOrWhiteSpace($pureRead.InstanceId)) 'Reading an installation must not mint an identifier for it.'
    Assert-True (-not (Test-Path -LiteralPath $pureRead.ConfigPath -PathType Leaf)) 'A read of an undeclared installation must not create its declaration file.'

    $firstRead = Get-AeroLinkInstanceConfig -ProductRoot $identified -Mode Development -EnsureInstanceId
    Assert-True (-not [string]::IsNullOrWhiteSpace($firstRead.InstanceId)) 'An installation must have a stable instance identifier once one is ensured.'
    $secondRead = Get-AeroLinkInstanceConfig -ProductRoot $identified -Mode Development -EnsureInstanceId
    Assert-True ($secondRead.InstanceId -eq $firstRead.InstanceId) 'The instance identifier must be minted once, not regenerated on every read.'
    Assert-True ((Get-AeroLinkInstanceConfig -ProductRoot $identified -Mode Development).InstanceId -eq $firstRead.InstanceId) `
        'A plain read must still report the identifier that already exists.'
    Set-AeroLinkInstanceConfig -ProductRoot $identified -Label 'WORK-LAPTOP LOCAL' -Classification 'WorkLaptopLocal' | Out-Null
    Assert-True ((Get-AeroLinkInstanceConfig -ProductRoot $identified -Mode Development).InstanceId -eq $firstRead.InstanceId) `
        'Declaring a label must not change the instance identifier.'
    Assert-True ($firstRead.InstanceId -notmatch [regex]::Escape($env:COMPUTERNAME)) 'The instance identifier must identify without describing the machine.'
    Assert-True ((Get-AeroLinkInstanceConfig -ProductRoot (New-FixtureProductRoot -WithLocal) -Mode Development -EnsureInstanceId).InstanceId -ne $firstRead.InstanceId) `
        'Two installations must not share an identifier.'

    # A declaration survives an unrelated update, so snapshot metadata cannot erase the label.
    Set-AeroLinkInstanceConfig -ProductRoot $undeclared -Snapshot @{ sourceLabel = 'HOME CANONICAL'; createdAtUtc = '2026-09-01T10:00:00Z' } | Out-Null
    $withSnapshot = Get-AeroLinkInstanceConfig -ProductRoot $undeclared -Mode Development
    Assert-True ($withSnapshot.Label -eq 'HOME CANONICAL') 'Recording snapshot metadata must not discard the declared label.'
    # Compared as an instant, not as text: PowerShell 7 deserializes an ISO timestamp in JSON as a DateTime
    # while Windows PowerShell leaves it as a string, and a contract that passes on one host and fails on the
    # other is worse than no contract. Both supported launcher hosts must agree on the instant.
    Assert-True ([DateTimeOffset]::Parse($withSnapshot.SnapshotCreatedAtUtc).UtcDateTime -eq ([datetime]'2026-09-01T10:00:00Z').ToUniversalTime()) `
        "Snapshot age must be inspectable as an exact instant on both PowerShell hosts; got '$($withSnapshot.SnapshotCreatedAtUtc)'."

    # --- 9. The environment override is absolute-and-must-exist, so qualification cannot point at nothing ---
    $env:AEROLINK_INSTALLATION_ROOT = 'not-absolute'
    Assert-Throws { Get-AeroLinkInstallationRoot -ProductRoot $productRoot } 'absolute' 'A relative AEROLINK_INSTALLATION_ROOT must refuse.'
    $env:AEROLINK_INSTALLATION_ROOT = 'C:\AeroLink\nowhere-at-all'
    Assert-Throws { Get-AeroLinkInstallationRoot -ProductRoot $productRoot } 'does not exist' 'A missing AEROLINK_INSTALLATION_ROOT must refuse.'
    $env:AEROLINK_INSTALLATION_ROOT = $canonicalInstallation
    Assert-True ((Get-AeroLinkInstallationRoot -ProductRoot $productRoot) -eq [IO.Path]::GetFullPath($canonicalInstallation)) `
        'A valid override wins over the pointer and the default.'
    $env:AEROLINK_INSTALLATION_ROOT = $null
}
finally {
    $env:AEROLINK_INSTALLATION_ROOT = $previousOverride
    foreach ($fixture in $fixtures) {
        if (Test-Path -LiteralPath $fixture) { Remove-Item -LiteralPath $fixture -Recurse -Force -ErrorAction SilentlyContinue }
    }
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Host "FAIL: $_" -ForegroundColor Red }
    Write-Host "AeroLink installation-identity contract FAILED ($($failures.Count) failure(s))." -ForegroundColor Red
    exit 1
}
Write-Host 'AeroLink installation-identity contract passed.' -ForegroundColor Green
exit 0
