using System.Text.Json;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Verification;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

public sealed record SoftwareProcedureCutoverResult(int ProjectsUpgraded, int ProceduresGenerated,
    int ExecutionsRebound, int TestSetEntriesRebound, int BaselineSelectionsRebound, int ImpactItemsRebound);

/// <summary>
/// The governed #726 all-project cutover. There is intentionally no endpoint registration for this
/// authority. It upgrades every sealed project with software verification to the [Case, Procedure] profile,
/// generates exactly one deterministic Procedure revision per exact software Case revision with honest
/// platform-migration provenance, and rebinds executions, test-set entries, baseline selections and impact
/// references to the new executable revisions — all inside per-project transactions guarded by the ladder's
/// optimistic version, after a typed v2 readiness check that refuses the ENTIRE cutover if any effective
/// execution consumer is absent or lacks the Procedure artifact key.
///
/// No human reviewer, TCR, approval, or signature is fabricated: generated Procedure revisions carry the
/// migration actor as author, no source TCR, an explicit Allocated parent link to the exact Case revision,
/// and a named audit event per generation. Reruns are idempotent per Case revision and per Completed marker.
/// </summary>
public sealed class SoftwareProcedureExecutionCutoverAuthority(
    AeroLinkDbContext db,
    IEnumerable<ILadderConsumerRegistration> consumerRegistrations,
    IEnumerable<IVerificationArtifactConsumerRegistration>? artifactConsumerRegistrations = null,
    bool allowSqliteExecution = false)
{
    public const string MigrationMarker = "VerificationExecutionCutover.SoftwareProcedures.v1";
    public const string Actor = VerificationArtifactProfileSchema.GovernedMigrationActor;
    private const string CompletedEvent = MigrationMarker + ".Completed";
    private const string UpgradeVersion = "v1";
    private const string UpgradeReason =
        "Activate the software Procedure execution tier and migrate every software Case onto an exact migration-generated Procedure.";

    private readonly IReadOnlyList<ILadderConsumerRegistration> registrations =
        consumerRegistrations?.ToArray() ?? throw new ArgumentNullException(nameof(consumerRegistrations));
    private readonly IReadOnlyList<IVerificationArtifactConsumerRegistration> artifactRegistrations =
        artifactConsumerRegistrations?.ToArray() ?? [];

    public async Task<SoftwareProcedureCutoverResult> EnsureCompletedAsync(CancellationToken ct = default)
    {
        // The governed cutover is a production-database authority, exactly like the Case identity migration:
        // SQLite test hosts (EnsureCreated) do not carry real sealed product state, and running the cutover
        // there would rewrite fixtures the tests intentionally keep Case-only. Focused cutover tests opt in.
        if (!db.Database.IsNpgsql() && !allowSqliteExecution) return new(0, 0, 0, 0, 0, 0);
        if (await db.SecurityAuditEvents.AsNoTracking().AnyAsync(x => x.EventType == CompletedEvent, ct))
            return new(0, 0, 0, 0, 0, 0);

        var configurations = await db.ProjectLadderConfigurations.AsNoTracking()
            .Include(x => x.Steps).Include(x => x.AllowedUpstream)
            .Where(x => x.IsSealed && x.State != ProjectLadderConfigurationState.Retired)
            .OrderBy(x => x.ProjectId)
            .ToListAsync(ct);
        var pending = configurations.Where(x => TargetKinds(x) is not null).ToList();
        if (pending.Count == 0)
        {
            await MarkCompletedAsync(ct, new SoftwareProcedureCutoverResult(0, 0, 0, 0, 0, 0));
            return new(0, 0, 0, 0, 0, 0);
        }

        // Validate every project BEFORE writing anything. A missing typed execution consumer refuses the
        // ENTIRE cutover with no partial Procedures, rebindings, configuration history, or activation evidence.
        foreach (var configuration in pending)
        {
            var readiness = BuildReadiness(configuration);
            if (!readiness.IsReady)
                throw new InvalidOperationException(
                    $"Software Procedure cutover is refused until every effective execution consumer is routed: "
                    + string.Join(", ", readiness.MissingArtifactCoverage.Select(x => $"artifact:{x.ConsumerId}:{x.ArtifactKey}"))
                    + ". No partial cutover was written.");
        }

        var totals = new SoftwareProcedureCutoverResult(0, 0, 0, 0, 0, 0);
        var now = DateTimeOffset.UtcNow;
        foreach (var configuration in pending)
        {
            var perProject = await UpgradeProjectAsync(configuration.Id, configuration.ProjectId, now, ct);
            totals = totals with
            {
                ProjectsUpgraded = totals.ProjectsUpgraded + 1,
                ProceduresGenerated = totals.ProceduresGenerated + perProject.ProceduresGenerated,
                ExecutionsRebound = totals.ExecutionsRebound + perProject.ExecutionsRebound,
                TestSetEntriesRebound = totals.TestSetEntriesRebound + perProject.TestSetEntriesRebound,
                BaselineSelectionsRebound = totals.BaselineSelectionsRebound + perProject.BaselineSelectionsRebound,
                ImpactItemsRebound = totals.ImpactItemsRebound + perProject.ImpactItemsRebound,
            };
        }
        await MarkCompletedAsync(ct, totals);
        return totals;
    }

    private async Task<SoftwareProcedureCutoverResult> UpgradeProjectAsync(Guid configurationId,
        Guid projectId, DateTimeOffset now, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        db.ChangeTracker.Clear();
        var configuration = await db.ProjectLadderConfigurations
            .Include(x => x.Steps).Include(x => x.AllowedUpstream)
            .SingleAsync(x => x.Id == configurationId, ct);
        if (!configuration.IsSealed)
            throw new InvalidOperationException("A governed Procedure cutover requires an already sealed ladder.");
        var kindsByStep = TargetKinds(configuration)
            ?? throw new InvalidOperationException("The project is not pending the software Procedure cutover.");
        var readiness = BuildReadiness(configuration);
        foreach (var step in configuration.Steps)
            if (kindsByStep.TryGetValue(Enum.Parse<RequirementLevel>(step.CatalogueEntry, false), out var kinds))
                step.ApplyPlatformUpgradeKinds(kinds);

        configuration.RecordPlatformUpgrade(UpgradeVersion, Actor, readiness.Hash, now);
        // RecordPlatformUpgrade converts a LegacyDefault Stored ladder into an authored Draft so the change is
        // attributable and reviewable. #726 is itself the governed activation: the cutover immediately
        // activates that Draft through the same evidence authority, leaving the configuration sealed and
        // EFFECTIVE (never a half-applied Draft that the runtime would ignore).
        if (configuration.State == ProjectLadderConfigurationState.Draft)
            configuration.Activate(Actor, now, LadderConsumerManifestCatalog.VersionV2, readiness.Hash);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException)
        {
            throw new InvalidOperationException("Another ladder edit, seal, or platform upgrade was saved during the Procedure cutover. Refresh and retry.");
        }

        var cases = await db.TestProcedures.AsNoTracking()
            .Where(x => x.ProjectId == projectId && x.ArtifactKind == VerificationArtifactKind.Case
                && (x.Level == TestProcedureLevel.HighLevel || x.Level == TestProcedureLevel.LowLevel))
            .OrderBy(x => x.BaseNumber)
            .ToListAsync(ct);
        if (cases.Count == 0) { await transaction.CommitAsync(ct); return new(0, 0, 0, 0, 0, 0); }
        var caseIds = cases.Select(x => x.Id).ToList();
        var caseRevisions = await db.TestProcedureRevisions.AsNoTracking()
            .Where(x => caseIds.Contains(x.ProcedureId))
            .OrderBy(x => x.ProcedureId).ThenBy(x => x.Revision)
            .ToListAsync(ct);
        var caseRevisionIds = caseRevisions.Select(x => x.Id).ToList();
        if (caseRevisionIds.Count == 0) { await transaction.CommitAsync(ct); return new(0, 0, 0, 0, 0, 0); }
        var existingLinks = await db.TestCaseProcedureLinks.AsNoTracking()
            .Where(x => caseRevisionIds.Contains(x.CaseRevisionId))
            .Select(x => new { x.CaseRevisionId, x.ProcedureRevisionId })
            .ToListAsync(ct);
        var existingProcedureRevisionIds = existingLinks.Select(x => x.ProcedureRevisionId).Distinct().ToList();
        var migrationOwnedRevisions = existingProcedureRevisionIds.Count == 0
            ? []
            : await db.TestProcedureRevisions.AsNoTracking()
                .Where(x => existingProcedureRevisionIds.Contains(x.Id)
                    && x.AuthorId == Actor && x.ParentKind == VerificationProcedureParentKind.Allocated)
                .Select(x => new { x.Id, x.ProcedureId })
                .ToDictionaryAsync(x => x.Id, x => x.ProcedureId, ct);

        // Exact identity mapping: every Case revision maps to exactly one (Procedure artifact, Procedure
        // revision) pair. One coherent Procedure artifact per Case carries the mirrored revision numbers, so
        // a Case baseline selection can never pair an old revision with a newer artifact.
        var caseRevisionToProcedure = new Dictionary<Guid, (Guid ArtifactId, Guid RevisionId)>();
        foreach (var link in existingLinks)
            if (migrationOwnedRevisions.TryGetValue(link.ProcedureRevisionId, out var procedureArtifactId))
                caseRevisionToProcedure[link.CaseRevisionId] = (procedureArtifactId, link.ProcedureRevisionId);

        var caseById = cases.ToDictionary(x => x.Id);
        var generated = 0;
        foreach (var caseArtifact in cases)
        {
            var artifactRevisions = caseRevisions.Where(x => x.ProcedureId == caseArtifact.Id).ToList();
            var pending = artifactRevisions
                .Where(x => !caseRevisionToProcedure.ContainsKey(x.Id)).ToList();
            if (pending.Count == 0) continue;
            var baseNumber = await IdentifierAllocator.NextTestProcedureAsync(db, caseArtifact.Level,
                VerificationArtifactKind.Procedure, ct);
            var procedure = new TestProcedure(projectId, baseNumber, caseArtifact.Title, caseArtifact.OwnerId,
                now, caseArtifact.Level, artifactKind: VerificationArtifactKind.Procedure,
                parentKind: VerificationProcedureParentKind.Allocated);
            db.TestProcedures.Add(procedure);
            foreach (var caseRevision in pending)
            {
                var revision = new TestProcedureRevision(procedure.Id, caseRevision.Revision,
                    caseRevision.Objective, caseRevision.Preconditions, caseRevision.Steps,
                    caseRevision.ExpectedResult, TestProcedureState.Approved, Actor, now,
                    effectiveBaselineId: caseRevision.EffectiveBaselineId,
                    environmentSetup: caseRevision.Preconditions,
                    testData: "",
                    orderedSteps: caseRevision.Steps,
                    expectedObservations: caseRevision.ExpectedResult,
                    cleanup: "",
                    toolingAutomation: "",
                    parentKind: VerificationProcedureParentKind.Allocated);
                var link = new TestCaseProcedureLink(caseRevision.Id, revision.Id);
                db.TestProcedureRevisions.Add(revision);
                db.TestCaseProcedureLinks.Add(link);
                db.SecurityAuditEvents.Add(new SecurityAuditEvent(
                    MigrationMarker + ".ProcedureGenerated", Actor,
                    $"TestCaseProcedureLink:{link.Id}", "Succeeded",
                    Json(new
                    {
                        migration = MigrationMarker,
                        projectId,
                        sourceCaseRevisionId = caseRevision.Id,
                        sourceCaseBaseNumber = caseArtifact.BaseNumber,
                        generatedProcedureId = procedure.Id,
                        generatedProcedureBaseNumber = baseNumber,
                        generatedProcedureRevision = revision.Id,
                        sourceTcrId = caseRevision.SourceTestChangeRequestId,
                        reason = "Deterministically generated from the exact legacy Case revision; no TCR, reviewer, or human approval fabricated."
                    }), "", now));
                caseRevisionToProcedure[caseRevision.Id] = (procedure.Id, revision.Id);
                generated++;
            }
        }
        if (generated == 0 && caseRevisionToProcedure.Count == 0)
        {
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return new(0, 0, 0, 0, 0, 0);
        }
        var migratedCaseRevisionIdsList = caseRevisionToProcedure.Keys.ToList();
        var migratedCaseArtifactIds = migratedCaseRevisionIdsList
            .Select(revisionId => caseById[caseRevisions.Single(x => x.Id == revisionId).ProcedureId].Id)
            .Distinct().ToList();
        var caseArtifactToProcedureArtifact = migratedCaseRevisionIdsList
            .GroupBy(revisionId => caseById[caseRevisions.Single(x => x.Id == revisionId).ProcedureId].Id)
            .ToDictionary(group => group.Key,
                group => caseRevisionToProcedure[group.First()].ArtifactId);

        var executions = await db.TestExecutions
            .Where(x => migratedCaseRevisionIdsList.Contains(x.ProcedureRevisionId)).ToListAsync(ct);
        foreach (var execution in executions)
            execution.RebindMigrationExecutable(
                caseRevisionToProcedure[execution.ProcedureRevisionId].RevisionId, now);
        var entries = await db.BuildTestSetEntries
            .Where(x => migratedCaseRevisionIdsList.Contains(x.ProcedureRevisionId)).ToListAsync(ct);
        foreach (var entry in entries)
            entry.RebindMigrationExecutable(
                caseRevisionToProcedure[entry.ProcedureRevisionId].RevisionId);
        var baselineSelections = await db.BaselineTestProcedures
            .Where(x => migratedCaseRevisionIdsList.Contains(x.RevisionId)
                || migratedCaseArtifactIds.Contains(x.ProcedureId))
            .ToListAsync(ct);
        foreach (var selection in baselineSelections)
            if (caseRevisionToProcedure.TryGetValue(selection.RevisionId, out var executable))
                selection.RebindMigrationExecutable(executable.ArtifactId, executable.RevisionId);
        var impactItems = await db.VerificationImpactItems
            .Where(x => x.ResolvedProcedureRevisionId != null
                    && migratedCaseRevisionIdsList.Contains(x.ResolvedProcedureRevisionId.Value)
                || x.ProcedureId != null && migratedCaseArtifactIds.Contains(x.ProcedureId.Value)
                || x.ResolvedProcedureId != null && migratedCaseArtifactIds.Contains(x.ResolvedProcedureId.Value))
            .ToListAsync(ct);
        foreach (var item in impactItems)
        {
            Guid? procedureId = item.ProcedureId is { } pid
                && caseArtifactToProcedureArtifact.TryGetValue(pid, out var newPid) ? newPid : null;
            Guid? resolvedProcedureId = item.ResolvedProcedureId is { } rpid
                && caseArtifactToProcedureArtifact.TryGetValue(rpid, out var newRpid) ? newRpid : null;
            Guid? resolvedRevisionId = item.ResolvedProcedureRevisionId is { } rrid
                && caseRevisionToProcedure.TryGetValue(rrid, out var revisionPair)
                    ? revisionPair.RevisionId : null;
            if (procedureId is not null || resolvedProcedureId is not null || resolvedRevisionId is not null)
                item.RebindMigrationExecutable(procedureId, resolvedProcedureId, resolvedRevisionId);
        }

        db.SecurityAuditEvents.Add(new SecurityAuditEvent(
            MigrationMarker + ".ProjectUpgraded", Actor,
            $"Project:{projectId}", "Succeeded",
            Json(new
            {
                migration = MigrationMarker,
                projectId,
                proceduresGenerated = generated,
                executionsRebound = executions.Count,
                testSetEntriesRebound = entries.Count,
                baselineSelectionsRebound = baselineSelections.Count,
                impactItemsRebound = impactItems.Count,
                sourceCaseRevisions = migratedCaseRevisionIdsList.Count,
                reason = UpgradeReason
            }), "", now));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(1, generated, executions.Count, entries.Count, baselineSelections.Count, impactItems.Count);
    }

    private async Task MarkCompletedAsync(CancellationToken ct, SoftwareProcedureCutoverResult totals)
    {
        db.SecurityAuditEvents.Add(new SecurityAuditEvent(
            CompletedEvent, Actor, "software-procedure-execution-cutover", "Succeeded",
            Json(new
            {
                migration = MigrationMarker,
                projectsUpgraded = totals.ProjectsUpgraded,
                proceduresGenerated = totals.ProceduresGenerated,
                executionsRebound = totals.ExecutionsRebound,
                testSetEntriesRebound = totals.TestSetEntriesRebound,
                baselineSelectionsRebound = totals.BaselineSelectionsRebound,
                impactItemsRebound = totals.ImpactItemsRebound,
                reason = "All eligible sealed projects now execute the software Procedure tier; reruns are idempotent."
            }), "", DateTimeOffset.UtcNow));
        await db.SaveChangesAsync(ct);
    }

    private LadderConsumerManifestV2 BuildReadiness(ProjectLadderConfiguration configuration)
    {
        var policy = ProjectLadderPolicyStorage.ResolvePersisted(configuration, configuration.ProjectId);
        var profile = new List<VerificationArtifactDefinition>();
        foreach (var step in configuration.Steps.OrderBy(x => x.Position))
        {
            if (!step.Capabilities.HasFlag(LevelCapabilities.HasVerification)) continue;
            var level = Enum.Parse<RequirementLevel>(step.CatalogueEntry, false);
            var kinds = TargetKinds(configuration)?.TryGetValue(level, out var upgraded) == true
                ? upgraded
                : step.EnabledArtifactKinds;
            foreach (var kind in kinds)
                profile.Add(VerificationArtifactVocabulary.Definition(
                    new VerificationArtifactKey(
                        VerificationArtifactProfile.ToNeutral(policy.Discipline(level)), kind)));
        }
        return LadderConsumerManifestCatalog.BuildV2(registrations, artifactRegistrations, profile);
    }

    /// <summary>
    /// State matrix: only a sealed LegacyDefault Stored ladder is eligible for the governed upgrade.
    /// A pre-existing sealed Draft is never made runtime-effective by the cutover; Retired history stays
    /// untouched; a deliberately authored (NonDefault) Active Case-only profile is preserved; an Active
    /// [Case, Procedure] profile is already upgraded. System levels are never listed.
    /// </summary>
    private static IReadOnlyDictionary<RequirementLevel, IReadOnlyList<VerificationArtifactKind>>? TargetKinds(
        ProjectLadderConfiguration configuration)
    {
        if (!configuration.IsSealed
            || configuration.State != ProjectLadderConfigurationState.Stored
            || configuration.Classification != ProjectLadderConfigurationClassification.LegacyDefault)
            return null;
        var pending = new Dictionary<RequirementLevel, IReadOnlyList<VerificationArtifactKind>>();
        foreach (var step in configuration.Steps)
        {
            if (!step.Capabilities.HasFlag(LevelCapabilities.HasVerification)) continue;
            var level = Enum.Parse<RequirementLevel>(step.CatalogueEntry, false);
            if (level is not (RequirementLevel.HighLevel or RequirementLevel.LowLevel)) continue;
            var current = step.EnabledArtifactKinds;
            if (current.Contains(VerificationArtifactKind.Case)
                && current.Contains(VerificationArtifactKind.Procedure)) continue;
            pending[level] = [VerificationArtifactKind.Case, VerificationArtifactKind.Procedure];
        }
        return pending.Count == 0 ? null : pending;
    }

    private static string Json(object value) => JsonSerializer.Serialize(value);
}
