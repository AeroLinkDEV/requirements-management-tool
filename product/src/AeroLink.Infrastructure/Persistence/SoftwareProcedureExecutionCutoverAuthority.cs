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
    IEnumerable<IVerificationArtifactConsumerRegistration>? artifactConsumerRegistrations = null)
{
    public const string MigrationMarker = "VerificationExecutionCutover.SoftwareProcedures.v1";
    public const string Actor = "aerolink-migration";
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
        if (await db.SecurityAuditEvents.AsNoTracking().AnyAsync(x => x.EventType == CompletedEvent, ct))
            return new(0, 0, 0, 0, 0, 0);

        var configurations = await db.ProjectLadderConfigurations.AsNoTracking()
            .Include(x => x.Steps)
            .Where(x => x.IsSealed)
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
            .Include(x => x.Steps)
            .SingleAsync(x => x.Id == configurationId, ct);
        if (!configuration.IsSealed)
            throw new InvalidOperationException("A governed Procedure cutover requires an already sealed ladder.");
        var kindsByStep = TargetKinds(configuration)
            ?? throw new InvalidOperationException("The project is not pending the software Procedure cutover.");
        foreach (var step in configuration.Steps)
            if (kindsByStep.TryGetValue(Enum.Parse<RequirementLevel>(step.CatalogueEntry, false), out var kinds))
                step.ApplyPlatformUpgradeKinds(kinds);

        var readiness = BuildReadiness(configuration);
        configuration.RecordPlatformUpgrade(UpgradeVersion, Actor, readiness.Hash, now);
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
        var existingLinks = await db.TestCaseProcedureLinks.AsNoTracking()
            .Where(x => caseIds.Contains(x.CaseRevisionId))
            .Select(x => new { x.CaseRevisionId, x.ProcedureRevisionId })
            .ToListAsync(ct);
        var existingProcedureRevisionIds = existingLinks.Select(x => x.ProcedureRevisionId).Distinct().ToList();
        var migrationOwned = existingProcedureRevisionIds.Count == 0
            ? []
            : await db.TestProcedureRevisions.AsNoTracking()
                .Where(x => existingProcedureRevisionIds.Contains(x.Id) && x.AuthorId == Actor)
                .Select(x => x.Id).ToListAsync(ct);
        var migratedCaseRevisionIds = existingLinks
            .Where(x => migrationOwned.Contains(x.ProcedureRevisionId))
            .Select(x => x.CaseRevisionId).ToHashSet();

        var revisionToCase = caseRevisions.ToDictionary(x => x.Id);
        var caseById = cases.ToDictionary(x => x.Id);
        var caseRevisionToProcedureRevision = new Dictionary<Guid, Guid>();
        var caseArtifactToProcedureArtifact = new Dictionary<Guid, Guid>();
        var generated = 0;
        foreach (var caseRevision in caseRevisions)
        {
            if (migratedCaseRevisionIds.Contains(caseRevision.Id)) continue;
            var caseArtifact = caseById[caseRevision.ProcedureId];
            var baseNumber = await IdentifierAllocator.NextTestProcedureAsync(db, caseArtifact.Level,
                VerificationArtifactKind.Procedure, ct);
            var procedure = new TestProcedure(projectId, baseNumber, caseArtifact.Title, caseArtifact.OwnerId,
                now, caseArtifact.Level, artifactKind: VerificationArtifactKind.Procedure,
                parentKind: VerificationProcedureParentKind.Allocated);
            var revision = new TestProcedureRevision(procedure.Id, caseRevision.Revision,
                caseRevision.Objective, caseRevision.Preconditions, caseRevision.Steps,
                caseRevision.ExpectedResult, TestProcedureState.Approved, Actor, now,
                environmentSetup: caseRevision.Preconditions,
                testData: "",
                orderedSteps: caseRevision.Steps,
                expectedObservations: caseRevision.ExpectedResult,
                cleanup: "",
                toolingAutomation: "",
                parentKind: VerificationProcedureParentKind.Allocated);
            var link = new TestCaseProcedureLink(caseRevision.Id, revision.Id);
            db.TestProcedures.Add(procedure);
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
            caseRevisionToProcedureRevision[caseRevision.Id] = revision.Id;
            caseArtifactToProcedureArtifact[caseArtifact.Id] = procedure.Id;
            generated++;
        }
        if (caseRevisionToProcedureRevision.Count == 0)
        {
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return new(0, 0, 0, 0, 0, 0);
        }
        var migratedCaseRevisionIdsList = caseRevisionToProcedureRevision.Keys.ToList();

        var executions = await db.TestExecutions
            .Where(x => migratedCaseRevisionIdsList.Contains(x.ProcedureRevisionId)).ToListAsync(ct);
        foreach (var execution in executions)
            execution.RebindMigrationExecutable(caseRevisionToProcedureRevision[execution.ProcedureRevisionId], now);
        var entries = await db.BuildTestSetEntries
            .Where(x => migratedCaseRevisionIdsList.Contains(x.ProcedureRevisionId)).ToListAsync(ct);
        foreach (var entry in entries)
            entry.RebindMigrationExecutable(caseRevisionToProcedureRevision[entry.ProcedureRevisionId]);
        var baselineSelections = await db.BaselineTestProcedures
            .Where(x => migratedCaseRevisionIdsList.Contains(x.RevisionId)
                || caseArtifactToProcedureArtifact.ContainsKey(x.ProcedureId))
            .ToListAsync(ct);
        foreach (var selection in baselineSelections)
            if (caseRevisionToProcedureRevision.TryGetValue(selection.RevisionId, out var newRevisionId)
                && caseArtifactToProcedureArtifact.TryGetValue(selection.ProcedureId, out var newProcedureId))
                selection.RebindMigrationExecutable(newProcedureId, newRevisionId);
        var impactItems = await db.VerificationImpactItems
            .Where(x => migratedCaseRevisionIdsList.Contains(x.ResolvedProcedureRevisionId ?? Guid.Empty)
                || caseArtifactToProcedureArtifact.ContainsKey(x.ProcedureId ?? Guid.Empty)
                || caseArtifactToProcedureArtifact.ContainsKey(x.ResolvedProcedureId ?? Guid.Empty))
            .ToListAsync(ct);
        foreach (var item in impactItems)
        {
            Guid? procedureId = item.ProcedureId is { } pid
                && caseArtifactToProcedureArtifact.TryGetValue(pid, out var newPid) ? newPid : null;
            Guid? resolvedProcedureId = item.ResolvedProcedureId is { } rpid
                && caseArtifactToProcedureArtifact.TryGetValue(rpid, out var newRpid) ? newRpid : null;
            Guid? resolvedRevisionId = item.ResolvedProcedureRevisionId is { } rrid
                && caseRevisionToProcedureRevision.TryGetValue(rrid, out var newRrid) ? newRrid : null;
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

    /// <summary>Software levels pending the upgrade map to [Case, Procedure]; System and already-upgraded levels are absent.</summary>
    private static IReadOnlyDictionary<RequirementLevel, IReadOnlyList<VerificationArtifactKind>>? TargetKinds(
        ProjectLadderConfiguration configuration)
    {
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
