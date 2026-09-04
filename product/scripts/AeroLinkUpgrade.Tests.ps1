#Requires -Version 5.1
<#
    Contract coverage for the launcher's database-upgrade orchestration (#881).

    The two properties worth testing here are both about ordering, not about SQL:

      * a known conflict is reported in seconds, with the exact records and the supported decisions, instead
        of becoming a readiness timeout followed by a stack trace (#747, #816);
      * the real database is not touched until the same upgrade has already run green on an isolated
        restored copy, so a failed validation leaves persistent state untouched as a consequence of the
        order rather than as a promise.

    Every step is injected. No test connects to PostgreSQL, runs pg_dump, restores anything, starts the
    maintenance host, or reads the persistent product\.local.
#>
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'AeroLinkUpgrade.psm1') -Force

$failures = [System.Collections.Generic.List[string]]::new()
$fixtures = [System.Collections.Generic.List[string]]::new()

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { $script:failures.Add($Message) }
}
function Assert-Throws([scriptblock]$Action, [string]$Pattern, [string]$Message) {
    try { & $Action | Out-Null }
    catch {
        if ($_.Exception.Message -match $Pattern) { return }
        $script:failures.Add("$Message (threw, but not matching '$Pattern': $($_.Exception.Message))")
        return
    }
    $script:failures.Add("$Message (nothing was thrown)")
}

function New-FixtureProductRoot {
    $root = Join-Path ([IO.Path]::GetTempPath()) ("aerolink-upgrade-" + [Guid]::NewGuid().ToString('N'))
    $script:fixtures.Add($root)
    New-Item -ItemType Directory -Path (Join-Path $root '.local\backups') -Force | Out-Null
    return $root
}

function New-AnalysisRunner {
    <#
        Stands in for the maintenance host: returns the exact JSON shape `maintenance analyze --json` emits,
        preceded by the build noise the real host prints, so the parser is exercised as it will be in life.
    #>
    param([Parameter(Mandatory)][hashtable]$Analysis)
    return {
        param($Arguments, $ConnectionString)
        $json = ([pscustomobject]$Analysis) | ConvertTo-Json -Depth 8
        return [pscustomobject]@{
            ExitCode = 0
            StdOut = "Determining projects to restore...`nRestored in 0.4s`n$json"
            StdErr = ''
        }
    }.GetNewClosure()
}

try {
    $productRoot = New-FixtureProductRoot

    # --- 1. Nothing pending: current, and the launcher does no upgrade work at all ---
    $current = Get-AeroLinkUpgradeAnalysis -ProductRoot $productRoot -CommandRunner (New-AnalysisRunner @{
        status = 'current'; databaseReachable = $true; databaseName = 'aerolink'
        pendingEfMigrations = @(); pendingSemanticUpgrades = @(); conflicts = @()
        upgradeRequired = $false; deterministicUpgrade = $false; databaseModified = $false
    })
    Assert-True ($current.Status -eq 'current') 'A database with nothing pending reports current.'
    Assert-True ($current.Analysis.databaseModified -eq $false) 'Analysis must report that it changed nothing.'

    # --- 2. Pending EF migrations are reported as a deterministic upgrade ---
    $pending = Get-AeroLinkUpgradeAnalysis -ProductRoot $productRoot -CommandRunner (New-AnalysisRunner @{
        status = 'upgrade-required'; databaseReachable = $true; databaseName = 'aerolink'
        pendingEfMigrations = @('20260829125743_AddReviewStageAuthorityKind', '20260830160428_AddApprovalStepAuthorityProvenance')
        pendingSemanticUpgrades = @(); conflicts = @(); upgradeRequired = $true; deterministicUpgrade = $true; databaseModified = $false
    })
    Assert-True ($pending.Status -eq 'upgrade-required') 'Pending schema migrations require an upgrade.'
    Assert-True (@($pending.Analysis.pendingEfMigrations).Count -eq 2) 'Every pending migration is reported, not merely a count.'

    # --- 3. A pending semantic upgrade with no conflict is still deterministic ---
    $semantic = Get-AeroLinkUpgradeAnalysis -ProductRoot $productRoot -CommandRunner (New-AnalysisRunner @{
        status = 'upgrade-required'; databaseReachable = $true; databaseName = 'aerolink'
        pendingEfMigrations = @()
        pendingSemanticUpgrades = @(@{ Marker = 'AuthorityMigration.ProjectLeadership.v2'; Target = 'project-leadership'; Completed = $false })
        conflicts = @(); upgradeRequired = $true; deterministicUpgrade = $true; databaseModified = $false
    })
    Assert-True ($semantic.Status -eq 'upgrade-required') 'A pending semantic upgrade requires an upgrade.'

    # --- 4. The #816 conflict: reported with the exact people and the supported decisions ---
    $conflictAnalysis = @{
        status = 'conflict'; databaseReachable = $true; databaseName = 'aerolink'
        pendingEfMigrations = @(); pendingSemanticUpgrades = @()
        conflicts = @(
            @{
                Code = 'project-leadership.legacy-backup-base-role-missing'
                Authority = 'AuthorityMigration.ProjectLeadership.v2'
                Summary = 'Flight Management System: the legacy SoftwareEngineeringLead standing backup does not hold the required SoftwareEngineer base role.'
                Subject = @{
                    program = 'Flight Management System'; programId = '11111111-1111-1111-1111-111111111111'
                    position = 'SoftwareEngineeringLead'; person = 'Avery Chen'; personUserName = 'software.engineer.070'
                    personId = '22222222-2222-2222-2222-222222222222'
                    requiredBaseRole = 'SoftwareEngineer'; heldBaseRoles = 'Engineer'; currentPrimary = 'Rina Shah'
                    legacyBackupId = '33333333-3333-3333-3333-333333333333'
                }
                Choices = @(
                    @{ Key = 'grant-required-role-and-keep-backup'; Description = 'Grant Avery Chen the SoftwareEngineer base role and keep the backup.'; GrantsNewAuthority = $true }
                    @{ Key = 'retire-legacy-backup'; Description = 'Retire the legacy backup, preserving it as ended history.'; GrantsNewAuthority = $false }
                )
            }
            @{
                Code = 'project-leadership.legacy-backup-ambiguous'
                Authority = 'AuthorityMigration.ProjectLeadership.v2'
                Summary = 'Second Program: legacy standing backups that map to SystemTestLead name different people.'
                Subject = @{ program = 'Second Program'; position = 'SystemTestLead' }
                Choices = @()
            }
        )
        upgradeRequired = $true; deterministicUpgrade = $false; databaseModified = $false
    }
    $conflict = Get-AeroLinkUpgradeAnalysis -ProductRoot $productRoot -CommandRunner (New-AnalysisRunner $conflictAnalysis)
    Assert-True ($conflict.Status -eq 'conflict') 'A modelled conflict is reported as a conflict, not as a failure to start.'
    Assert-True (@($conflict.Analysis.conflicts).Count -eq 2) `
        'Every conflict in the database must be reported in ONE analysis, not discovered one restart at a time.'
    $first = @($conflict.Analysis.conflicts)[0]
    Assert-True ($first.Subject.person -eq 'Avery Chen') 'The conflict must name the person, not only the table.'
    Assert-True ($first.Subject.requiredBaseRole -eq 'SoftwareEngineer' -and $first.Subject.heldBaseRoles -eq 'Engineer') `
        'The conflict must state the role required and the role held, which is the whole decision.'
    Assert-True (@($first.Choices | Where-Object { $_.GrantsNewAuthority }).Count -eq 1) `
        'Exactly one of the offered decisions grants new authority, and it must be flagged as doing so.'

    # The rendered operator block says what happened and, crucially, what did not.
    $report = & { Write-AeroLinkUpgradeConflictReport -Analysis $conflict.Analysis } 6>&1 | ForEach-Object { "$_" }
    $reportText = $report -join "`n"
    Assert-True ($reportText -match 'DATABASE ATTENTION REQUIRED') 'The operator block leads with what is wrong.'
    Assert-True ($reportText -match 'Avery Chen' -and $reportText -match 'Rina Shah') 'The operator block names the people involved.'
    Assert-True ($reportText -match 'grants authority somebody does not have today') 'A decision that grants authority must be marked as such.'
    Assert-True ($reportText -match 'AeroLink made NO authority decision automatically') 'The operator must be told no decision was made for them.'
    Assert-True ($reportText -match 'No persistent data was changed') 'The operator must be told the database is untouched.'
    Assert-True ($reportText -notmatch '11111111-1111') 'Raw identifiers are noise in an operator block; they belong to the resolver.'
    Assert-True ($reportText -notmatch '(?i)stack trace|at AeroLink\.') 'A stack trace must never be the operator-facing message.'

    # --- 5. An unreachable database is a status, not an exception ---
    $unreachable = Get-AeroLinkUpgradeAnalysis -ProductRoot $productRoot -CommandRunner {
        param($Arguments, $ConnectionString)
        [pscustomobject]@{ ExitCode = 30; StdOut = 'MSBuild version 17'; StdErr = '' }
    }
    Assert-True ($unreachable.Status -eq 'unreachable') 'A host that produced no analysis must report unreachable rather than crash the launcher.'

    # =====================================================================================================
    # Clone-validated upgrade. The ordering IS the safety property.
    # =====================================================================================================
    $steps = [System.Collections.Generic.List[string]]::new()
    $backup = { $steps.Add('backup'); 'C:\fixture\backups\aerolink-20260903-120000.zip' }.GetNewClosure()
    $restore = { param($Archive, $Database) $steps.Add("restore:$Database"); $true }.GetNewClosure()
    $cleanup = { param($Database) $steps.Add("cleanup:$Database") }.GetNewClosure()
    # Current code standing up against the UPGRADED clone: ready, authentication answering, storage coherent.
    $cloneHealthy = { param($ConnectionString) $steps.Add('validate:clone'); [pscustomobject]@{ Passed = $true; Detail = 'ready, authentication answering' } }.GetNewClosure()

    # --- 6. Happy path: backup, restore, clone upgrade, clone re-analysis, cleanup, THEN the real upgrade ---
    $steps.Clear()
    $evidenceRoots = [System.Collections.Generic.List[string]]::new()
    $upgradeRunner = { param($ConnectionString, $EvidenceRoot) $steps.Add($(if ($ConnectionString) { 'upgrade:clone' } else { 'upgrade:real' })); $evidenceRoots.Add([string]$EvidenceRoot); [pscustomobject]@{ ExitCode = 0; StdOut = ''; StdErr = '' } }.GetNewClosure()
    $cloneCurrent = { param($ConnectionString, $EvidenceRoot) $steps.Add('analyze:clone'); $evidenceRoots.Add([string]$EvidenceRoot); [pscustomobject]@{ Status = 'current'; Analysis = $null; ExitCode = 0; Detail = '' } }.GetNewClosure()
    $evidenceRoots.Clear()
    $result = Invoke-AeroLinkCloneValidatedUpgrade -ProductRoot $productRoot -BackupRunner $backup -RestoreRunner $restore `
        -UpgradeRunner $upgradeRunner -AnalysisRunner $cloneCurrent -CleanupRunner $cleanup -CurrentCodeValidator $cloneHealthy
    Assert-True ($result.Validated -and $result.Applied) 'A validated upgrade is applied.'
    $order = $steps -join ' -> '
    Assert-True ($order -match '^backup -> restore:aerolink_upgrade_validation_[a-f0-9]{10} -> upgrade:clone -> analyze:clone -> validate:clone -> cleanup:') `
        "The real upgrade must be the LAST step, after backup, restore, clone upgrade, clone analysis and current-code validation. Order was: $order"
    Assert-True ($steps[-1] -eq 'upgrade:real') "The real database must be upgraded only at the end. Order was: $order"
    Assert-True ($result.Detail -match 'pre-upgrade backup is retained') 'The operator must be told where the recoverable point is.'

    # A database is not the only persistent thing an upgrade writes. A semantic authority in this set rewrites
    # controlled renditions through EvidenceFileStore, which resolves Evidence:Root and defaults to the LIVE
    # evidence tree - so isolating the connection string alone let a clone upgrade put new objects into the
    # canonical evidence store, where a database rollback cannot reach them.
    $cloneEvidence = @($evidenceRoots | Where-Object { $_ })
    Assert-True ($cloneEvidence.Count -eq 2) 'Both the clone upgrade and the clone analysis must be given an evidence root.'
    Assert-True (($cloneEvidence | Select-Object -Unique).Count -eq 1) 'The clone upgrade and its analysis must share one isolated evidence tree.'
    Assert-True ($cloneEvidence[0] -match 'restore-validation\\aerolink_upgrade_validation_[a-f0-9]{10}\\evidence$') `
        "The clone's evidence must live under the isolated restore-validation tree, not the live evidence store. It was: $($cloneEvidence[0])"
    Assert-True ($evidenceRoots[-1] -eq '') 'The real upgrade uses the installation''s own evidence root, not an isolated one.'

    # --- 6b. The analyzer says current, but current AeroLink cannot serve the upgraded copy ---
    #
    # "Analyzer-current" means the schema and semantic markers line up; it does not mean the application
    # works. #881 asks for readiness, authentication and storage invariants on the isolated copy BEFORE the
    # real database is mutated, and without this step a clone that no build could actually run would still
    # have authorised mutating real data.
    $steps.Clear()
    $cloneUnhealthy = { param($ConnectionString) $steps.Add('validate:clone'); [pscustomobject]@{ Passed = $false; Detail = 'current AeroLink never became ready against the upgraded copy.' } }.GetNewClosure()
    $unhealthy = Invoke-AeroLinkCloneValidatedUpgrade -ProductRoot $productRoot -BackupRunner $backup -RestoreRunner $restore `
        -UpgradeRunner $upgradeRunner -AnalysisRunner $cloneCurrent -CleanupRunner $cleanup -CurrentCodeValidator $cloneUnhealthy
    Assert-True (-not $unhealthy.Applied) 'A clone the current build cannot serve must not authorise the real upgrade.'
    Assert-True ($steps -notcontains 'upgrade:real') 'A failed current-code validation must leave the real database untouched.'
    Assert-True ($unhealthy.Detail -match 'never touched and is unchanged') 'The failure must state plainly that the real database is unchanged.'
    Assert-True (($steps | Where-Object { $_ -like 'cleanup:*' }).Count -eq 1) 'The disposable copy is cleaned up when current-code validation fails.'

    # --- 7. Clone upgrade fails: the real database is NEVER touched ---
    $steps.Clear()
    $failingClone = { param($ConnectionString)
        if ($ConnectionString) { $steps.Add('upgrade:clone'); return [pscustomobject]@{ ExitCode = 20; StdOut = 'Conflicting legacy Project Leadership authority.'; StdErr = '' } }
        $steps.Add('upgrade:real'); return [pscustomobject]@{ ExitCode = 0; StdOut = ''; StdErr = '' }
    }.GetNewClosure()
    $failed = Invoke-AeroLinkCloneValidatedUpgrade -ProductRoot $productRoot -BackupRunner $backup -RestoreRunner $restore `
        -UpgradeRunner $failingClone -AnalysisRunner $cloneCurrent -CleanupRunner $cleanup -CurrentCodeValidator $cloneHealthy
    Assert-True (-not $failed.Validated -and -not $failed.Applied) 'A failed clone upgrade must not be applied.'
    Assert-True ($steps -notcontains 'upgrade:real') 'A failed clone upgrade must leave the real database untouched.'
    Assert-True (($steps | Where-Object { $_ -like 'cleanup:*' }).Count -eq 1) 'The disposable copy is cleaned up even when validation fails.'
    Assert-True ($failed.Detail -match 'never touched and is unchanged') 'The failure must state plainly that the real database is unchanged.'

    # --- 8. The clone upgrades but is still not current: also refuses ---
    $steps.Clear()
    $cloneNotCurrent = { param($ConnectionString) $steps.Add('analyze:clone'); [pscustomobject]@{ Status = 'upgrade-required'; Analysis = $null; ExitCode = 10; Detail = '' } }.GetNewClosure()
    $notCurrent = Invoke-AeroLinkCloneValidatedUpgrade -ProductRoot $productRoot -BackupRunner $backup -RestoreRunner $restore `
        -UpgradeRunner $upgradeRunner -AnalysisRunner $cloneNotCurrent -CleanupRunner $cleanup -CurrentCodeValidator $cloneHealthy
    Assert-True (-not $notCurrent.Applied) 'A clone that is not current after upgrading must not authorize the real upgrade.'
    Assert-True ($steps -notcontains 'upgrade:real') 'A clone that is not current must leave the real database untouched.'

    # --- 9. The isolated restore fails: nothing is upgraded anywhere ---
    $steps.Clear()
    $failingRestore = { param($Archive, $Database) $steps.Add('restore:failed'); $false }.GetNewClosure()
    $restoreFailed = Invoke-AeroLinkCloneValidatedUpgrade -ProductRoot $productRoot -BackupRunner $backup -RestoreRunner $failingRestore `
        -UpgradeRunner $upgradeRunner -AnalysisRunner $cloneCurrent -CleanupRunner $cleanup -CurrentCodeValidator $cloneHealthy
    Assert-True (-not $restoreFailed.Applied) 'A failed isolated restore must not authorize an upgrade.'
    Assert-True ($steps -notcontains 'upgrade:clone' -and $steps -notcontains 'upgrade:real') 'A failed restore must upgrade nothing at all.'

    # --- 10. No verified backup: refuse before anything else happens ---
    $steps.Clear()
    $noBackup = Invoke-AeroLinkCloneValidatedUpgrade -ProductRoot $productRoot -BackupRunner { $steps.Add('backup'); $null } `
        -RestoreRunner $restore -UpgradeRunner $upgradeRunner -AnalysisRunner $cloneCurrent -CleanupRunner $cleanup -CurrentCodeValidator $cloneHealthy
    Assert-True (-not $noBackup.Applied) 'Without a verified backup there is no recoverable point, so there is no upgrade.'
    Assert-True ($steps.Count -eq 1 -and $steps[0] -eq 'backup') 'A missing backup must stop the sequence immediately.'

    # --- 11. Cleanup can only ever be handed a generated disposable name ---
    Assert-Throws { Remove-AeroLinkUpgradeValidationDatabase -ProductRoot $productRoot -Database 'aerolink' } `
        'Refusing to drop' 'The cleanup helper must refuse the persistent database by name.'
    Assert-Throws { Remove-AeroLinkUpgradeValidationDatabase -ProductRoot $productRoot -Database 'aerolink_restore_validation' } `
        'Refusing to drop' 'The cleanup helper must refuse any database it did not generate.'
    Assert-Throws { Remove-AeroLinkSnapshotStagingDatabase -ProductRoot $productRoot -Database 'aerolink' } `
        'Refusing to drop' 'The snapshot staging cleanup must refuse the persistent database by name.'

    # --- 12. Isolating the database without isolating the evidence is not isolation ---
    #
    # The maintenance host is refused outright rather than trusted to be called correctly, because the
    # failure it prevents is silent: the clone upgrade succeeds, the validation passes, and the only trace
    # is new objects in the canonical evidence tree that no rollback will remove.
    Assert-Throws { Invoke-AeroLinkMaintenanceCommand -ProductRoot $productRoot -Arguments @('maintenance', 'analyze') -ConnectionString 'Host=127.0.0.1;Port=54329;Database=aerolink_clone;Username=postgres' } `
        'requires an isolated -EvidenceRoot' 'Pointing maintenance at an isolated database without an isolated evidence root must be refused.'
}
finally {
    foreach ($fixture in $fixtures) {
        if (Test-Path -LiteralPath $fixture) { Remove-Item -LiteralPath $fixture -Recurse -Force -ErrorAction SilentlyContinue }
    }
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Host "FAIL: $_" -ForegroundColor Red }
    Write-Host "AeroLink upgrade-orchestration contract FAILED ($($failures.Count) failure(s))." -ForegroundColor Red
    exit 1
}
Write-Host 'AeroLink upgrade-orchestration contract passed.' -ForegroundColor Green
exit 0
