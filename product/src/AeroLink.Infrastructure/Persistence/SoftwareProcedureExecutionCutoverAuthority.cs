using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Npgsql;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Traceability;
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
    bool allowSqliteExecution = false,
    ControlledOutputGenerator? generator = null,
    EvidenceFileStore? files = null)
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
    private ControlledOutputGenerator? generatorField = generator;
    private EvidenceFileStore? filesField = files;
    // Storage keys written by the CURRENT in-flight project transaction. If that transaction rolls back,
    // these bytes are unreferenced output and are removed by the safe cleanup below; previously referenced
    // evidence is never deleted.
    private readonly List<string> _pendingNewStorageKeys = [];

    private EvidenceFileStore Files => filesField ??= new EvidenceFileStore(
        Path.Combine(Path.GetTempPath(), "aerolink-726-evidence"));

    private ControlledOutputGenerator Generator => generatorField ??= new ControlledOutputGenerator(
        db, new RichContentPublisher(db, Files),
        policyResolver: new EffectiveProjectLadderPolicyResolver(db));

    public async Task<SoftwareProcedureCutoverResult> EnsureCompletedAsync(CancellationToken ct = default)
    {
        // The governed cutover is a production-database authority, exactly like the Case identity migration:
        // SQLite test hosts (EnsureCreated) do not carry real sealed product state, and running the cutover
        // there would rewrite fixtures the tests intentionally keep Case-only. Focused cutover tests opt in.
        if (!db.Database.IsNpgsql() && !allowSqliteExecution) return new(0, 0, 0, 0, 0, 0);
        // The global Completed marker is an atomic, database-enforced claim (unique marker row) committed in
        // the SAME save as the Completed audit evidence. A completed run is finished forever: the immutable
        // totals stored with the marker are authoritative and are never recomputed from live data, which
        // drifts as ordinary post-cutover executions, test-set entries, baseline selections, and impact
        // records are created.
        var completion = await db.GovernedMigrationCompletions.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Marker == MigrationMarker, ct);
        if (completion is not null)
        {
            await RecoverMissingCompletionAuditAsync(completion, ct);
            return ParseTotals(completion.TotalsJson);
        }

        var configurations = await db.ProjectLadderConfigurations.AsNoTracking()
            .Include(x => x.Steps).Include(x => x.AllowedUpstream)
            .Where(x => x.IsSealed && x.State != ProjectLadderConfigurationState.Retired)
            .OrderBy(x => x.ProjectId)
            .ToListAsync(ct);
        var pending = configurations.Where(x => TargetKinds(x) is not null).ToList();
        var now = DateTimeOffset.UtcNow;
        if (pending.Count == 0)
        {
            // A crash before the global Completed marker must not report inaccurate zero totals: recover the
            // honest per-project outcome from the persisted governed evidence.
            var recoveredAtStart = await RecoverCompletedTotalsAsync(new HashSet<Guid>(), ct);
            return await ClaimCompletedAsync(recoveredAtStart, now, ct);
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
        var pendingProjectIds = new HashSet<Guid>();
        foreach (var configuration in pending)
        {
            pendingProjectIds.Add(configuration.ProjectId);
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
        // If a previous run crashed part-way, projects it already upgraded are Active and no longer pending;
        // their governed evidence must be counted too, never reported as zero.
        var recoveredAfterLoop = await RecoverCompletedTotalsAsync(pendingProjectIds, ct);
        totals = totals with
        {
            ProjectsUpgraded = totals.ProjectsUpgraded + recoveredAfterLoop.ProjectsUpgraded,
            ProceduresGenerated = totals.ProceduresGenerated + recoveredAfterLoop.ProceduresGenerated,
            ExecutionsRebound = totals.ExecutionsRebound + recoveredAfterLoop.ExecutionsRebound,
            TestSetEntriesRebound = totals.TestSetEntriesRebound + recoveredAfterLoop.TestSetEntriesRebound,
            BaselineSelectionsRebound = totals.BaselineSelectionsRebound + recoveredAfterLoop.BaselineSelectionsRebound,
            ImpactItemsRebound = totals.ImpactItemsRebound + recoveredAfterLoop.ImpactItemsRebound,
        };
        return await ClaimCompletedAsync(totals, now, ct);
    }

    /// <summary>
    /// Atomically claims the global completion marker and writes its Completed audit evidence in ONE save
    /// boundary. Only the winning instance writes the completion evidence; a concurrent loser observes the
    /// verified unique-marker conflict and returns the winner's immutable stored totals. Any other
    /// persistence failure propagates — it is never mistaken for a lost race.
    /// </summary>
    private async Task<SoftwareProcedureCutoverResult> ClaimCompletedAsync(SoftwareProcedureCutoverResult totals,
        DateTimeOffset now, CancellationToken ct)
    {
        db.GovernedMigrationCompletions.Add(new GovernedMigrationCompletion(
            MigrationMarker, Actor, now, Json(totals)));
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
                reason = "All eligible sealed projects now execute the software Procedure tier; reruns return the immutable stored completion totals."
            }), "", now));
        try
        {
            await db.SaveChangesAsync(ct);
            return totals;
        }
        catch (DbUpdateException ex) when (IsUniqueMarkerConflict(ex))
        {
            // A concurrent startup instance committed the marker+audit first. The stored marker is the only
            // authoritative completion record; return its immutable totals, never this run's own numbers.
            db.ChangeTracker.Clear();
            var winner = await db.GovernedMigrationCompletions.AsNoTracking()
                .SingleAsync(x => x.Marker == MigrationMarker, ct);
            await RecoverMissingCompletionAuditAsync(winner, ct);
            return ParseTotals(winner.TotalsJson);
        }
    }

    /// <summary>
    /// Fail-closed repair for the only historically possible inconsistent state: a completion marker whose
    /// Completed audit evidence was never committed (the pre-atomic build saved the two rows separately).
    /// The audit is reconstructed from the marker's immutable stored totals, and only when it is genuinely
    /// missing.
    /// </summary>
    private async Task RecoverMissingCompletionAuditAsync(GovernedMigrationCompletion completion,
        CancellationToken ct)
    {
        var auditExists = await db.SecurityAuditEvents.AsNoTracking().AnyAsync(x =>
            x.EventType == CompletedEvent
            && x.ActorId == Actor
            && x.Target == "software-procedure-execution-cutover", ct);
        if (auditExists) return;
        var totals = ParseTotals(completion.TotalsJson);
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
                reason = "Recovered missing completion audit evidence after an interrupted claim; totals are the immutable stored completion totals."
            }), "", DateTimeOffset.UtcNow));
        await db.SaveChangesAsync(ct);
    }

    private static SoftwareProcedureCutoverResult ParseTotals(string totalsJson) =>
        JsonSerializer.Deserialize<SoftwareProcedureCutoverResult>(totalsJson)
        ?? throw new InvalidOperationException(
            "The stored completion marker carries malformed immutable totals evidence.");

    /// <summary>
    /// A concurrent claim loses ONLY on the verified unique-marker conflict (PostgreSQL 23505 or the SQLite
    /// unique index on the expected marker constraint). Every other DbUpdateException is a real persistence
    /// failure and must surface.
    /// </summary>
    private static bool IsUniqueMarkerConflict(DbUpdateException exception)
    {
        for (var inner = exception.InnerException; inner is not null; inner = inner.InnerException)
        {
            if (inner is PostgresException { SqlState: "23505" } postgres
                && postgres.ConstraintName?.Contains(
                    "IX_governed_migration_completions_Marker", StringComparison.OrdinalIgnoreCase) == true)
                return true;
            if (inner is SqliteException { SqliteErrorCode: 19 } sqlite
                && (sqlite.Message?.Contains(
                    "IX_governed_migration_completions_Marker", StringComparison.OrdinalIgnoreCase) == true
                    || sqlite.Message?.Contains(
                        "governed_migration_completions.Marker", StringComparison.OrdinalIgnoreCase) == true))
                return true;
        }
        return false;
    }

    private async Task<SoftwareProcedureCutoverResult> UpgradeProjectAsync(Guid configurationId,
        Guid projectId, DateTimeOffset now, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
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
            // RecordPlatformUpgrade converts a LegacyDefault Stored ladder into an authored Draft so the
            // change is attributable and reviewable. #726 is itself the governed activation: the cutover
            // immediately activates that Draft through the same evidence authority, leaving the configuration
            // sealed and EFFECTIVE (never a half-applied Draft that the runtime would ignore).
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
        var caseIds = cases.Select(x => x.Id).ToList();
        var caseRevisions = await db.TestProcedureRevisions.AsNoTracking()
            .Where(x => caseIds.Contains(x.ProcedureId))
            .OrderBy(x => x.ProcedureId).ThenBy(x => x.Revision)
            .ToListAsync(ct);
        var caseRevisionIds = caseRevisions.Select(x => x.Id).ToList();
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
        var selectedCaseRevisionIds = await db.BaselineTestProcedures.AsNoTracking()
            .Where(x => caseRevisionIds.Contains(x.RevisionId))
            .Select(x => x.RevisionId).Distinct().ToListAsync(ct);
        foreach (var caseArtifact in cases)
        {
            var artifactRevisions = caseRevisions.Where(x => x.ProcedureId == caseArtifact.Id).ToList();
            var pending = artifactRevisions
                .Where(x => !caseRevisionToProcedure.ContainsKey(x.Id)).ToList();
            if (pending.Count == 0) continue;
            var firstRetired = pending.All(x => x.State == TestProcedureState.Retired);
            var baseNumber = await IdentifierAllocator.NextTestProcedureAsync(db, caseArtifact.Level,
                VerificationArtifactKind.Procedure, ct);
            var procedure = new TestProcedure(projectId, baseNumber, caseArtifact.Title, caseArtifact.OwnerId,
                now, caseArtifact.Level, artifactKind: VerificationArtifactKind.Procedure,
                parentKind: VerificationProcedureParentKind.Allocated);
            db.TestProcedures.Add(procedure);
            foreach (var caseRevision in pending)
            {
                var retired = caseRevision.State == TestProcedureState.Retired;
                // A generated Procedure mirrors the source Case revision exactly, including its governed
                // baseline — but ONLY when that Case revision is actually carried by a baseline manifest.
                // A Case that is effective on paper but selected nowhere yields a dormant mirror, not a
                // governed claim the build never made.
                var effectiveBaselineId = selectedCaseRevisionIds.Contains(caseRevision.Id)
                    ? caseRevision.EffectiveBaselineId
                    : null;
                var revision = new TestProcedureRevision(procedure.Id, caseRevision.Revision,
                    caseRevision.Objective, caseRevision.Preconditions, caseRevision.Steps,
                    caseRevision.ExpectedResult,
                    retired ? TestProcedureState.Retired : TestProcedureState.Approved,
                    Actor, now,
                    effectiveBaselineId: effectiveBaselineId,
                    environmentSetup: retired ? "" : caseRevision.Preconditions,
                    testData: "",
                    orderedSteps: retired ? "" : caseRevision.Steps,
                    expectedObservations: retired ? "" : caseRevision.ExpectedResult,
                    cleanup: "",
                    toolingAutomation: "",
                    parentKind: VerificationProcedureParentKind.Allocated);
                db.TestProcedureRevisions.Add(revision);
                // A Retired Case cannot be an exact active parent under the persistence rules; the retired
                // history is preserved by the mirrored Retired Procedure revision and its audit event, never
                // by manufacturing an active executable claim from retired content. The same rule applies to
                // a historically-effective Case revision that is no longer selected by any current baseline:
                // its mirror cannot be selected in the Case's governed baseline (membership moved on), so it
                // is preserved as history without manufacturing a governed executable claim.
                var link = retired || (effectiveBaselineId is null
                        && caseRevision.EffectiveBaselineId is not null)
                    ? null
                    : new TestCaseProcedureLink(caseRevision.Id, revision.Id);
                if (link is not null) db.TestCaseProcedureLinks.Add(link);
                db.SecurityAuditEvents.Add(new SecurityAuditEvent(
                    MigrationMarker + ".ProcedureGenerated", Actor,
                    link is null ? $"TestProcedureRevision:{revision.Id}" : $"TestCaseProcedureLink:{link.Id}",
                    "Succeeded",
                    Json(new
                    {
                        migration = MigrationMarker,
                        projectId,
                        sourceCaseRevisionId = caseRevision.Id,
                        sourceCaseBaseNumber = caseArtifact.BaseNumber,
                        sourceCaseState = caseRevision.State.ToString(),
                        generatedProcedureId = procedure.Id,
                        generatedProcedureBaseNumber = baseNumber,
                        generatedProcedureRevision = revision.Id,
                        generatedState = revision.State.ToString(),
                        sourceTcrId = caseRevision.SourceTestChangeRequestId,
                        reason = retired || (effectiveBaselineId is null
                                && caseRevision.EffectiveBaselineId is not null)
                            ? "Historical Case revision mirrored without an active executable claim (retired or superseded by a later selected revision); no governed claim was manufactured."
                            : "Deterministically generated from the exact legacy Case revision; no TCR, reviewer, or human approval fabricated."
                    }), "", now));
                caseRevisionToProcedure[caseRevision.Id] = (procedure.Id, revision.Id);
                generated++;
            }
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
        // Finding 5: capture per-baseline provenance BEFORE the rebind replaces Case identities with
        // Procedure identities, so the manifest event can name the exact old Case revision and its new
        // Procedure artifact/revision for that baseline, deterministically ordered.
        var provenanceByBaseline = baselineSelections
            .Where(selection => migratedCaseRevisionIdsList.Contains(selection.RevisionId))
            .GroupBy(selection => selection.BaselineId)
            .ToDictionary(group => group.Key, group => string.Join(";", group
                .Select(selection =>
                {
                    var (procedureArtifactId, procedureRevisionId) =
                        caseRevisionToProcedure[selection.RevisionId];
                    return $"case:{selection.RevisionId}->procedure:{procedureArtifactId}:{procedureRevisionId}";
                })
                .OrderBy(identity => identity, StringComparer.Ordinal)));
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

        // Capture the pre-cutover membership counts BEFORE the rebinds are persisted, so the governed
        // manifest event truthfully reports old vs new executable membership.
        var oldMembershipCountByBaseline = await db.BaselineTestProcedures.AsNoTracking()
            .Where(x => baselineSelections.Select(s => s.BaselineId).Contains(x.BaselineId))
            .GroupBy(x => x.BaselineId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        // Persist the rebinds first so the canonical manifest is recomputed from the membership the build now
        // actually carries — never from Case rows that are about to disappear.
        await db.SaveChangesAsync(ct);

        // Blocker 1: never leave a baseline's canonical TestProceduresHash describing Case identities while
        // membership points to Procedure identities. Recompute the canonical manifest through the governed
        // CandidateBaseline manifest-migration operation for every materialized (frozen or released) baseline
        // whose membership this project changed.
        var affectedBaselineIds = baselineSelections.Select(x => x.BaselineId).Distinct().ToList();
        if (affectedBaselineIds.Count != 0)
        {
            var materializedBaselines = await db.CandidateBaselines
                .Where(x => affectedBaselineIds.Contains(x.Id) && x.TestProceduresMaterializedAt != null)
                .ToListAsync(ct);
            foreach (var baseline in materializedBaselines)
            {
                var previousHash = baseline.TestProceduresHash;
                var manifestEntries = await (from member in db.BaselineTestProcedures.AsNoTracking()
                                             where member.BaselineId == baseline.Id
                                             join revision in db.TestProcedureRevisions.AsNoTracking()
                                                 on member.RevisionId equals revision.Id
                                             join procedure in db.TestProcedures.AsNoTracking()
                                                 on member.ProcedureId equals procedure.Id
                                             select new TestProcedureManifestEntry(procedure.Id, revision.Id,
                                                 procedure.BaseNumber, revision.Revision)).ToListAsync(ct);
                var newHash = TestProcedureManifest.Hash(manifestEntries);
                if (string.Equals(baseline.TestProceduresHash, newHash, StringComparison.OrdinalIgnoreCase))
                    continue;
                baseline.RecordExecutionCutoverManifestMigration(Actor, previousHash, newHash,
                    oldMembershipCountByBaseline.GetValueOrDefault(baseline.Id), manifestEntries.Count,
                    provenanceByBaseline.GetValueOrDefault(baseline.Id) ?? "no migrated Case executable memberships in this baseline",
                    now);
            }
            await db.SaveChangesAsync(ct);
            await RegenerateAffectedDocumentsAsync(affectedBaselineIds, now, ct);
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
                affectedBaselines = affectedBaselineIds.Count,
                reason = UpgradeReason
            }), "", now));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        _pendingNewStorageKeys.Clear();
        return new(1, generated, executions.Count, entries.Count, baselineSelections.Count, impactItems.Count);
        }
        catch
        {
            // The surrounding transaction rolls back; any rendition bytes stored by this in-flight project
            // transaction are now unreferenced output and are removed safely. Previously referenced evidence
            // is never deleted.
            RemovePendingNewStorageKeys();
            throw;
        }
    }

    /// <summary>
    /// Removes only the storage keys written by the current in-flight project transaction after a rollback.
    /// Old referenced evidence is never touched.
    /// </summary>
    private void RemovePendingNewStorageKeys()
    {
        foreach (var storageKey in _pendingNewStorageKeys)
        {
            try { Files.Delete(storageKey); }
            catch { /* best-effort cleanup; referenced evidence is never deleted */ }
        }
        _pendingNewStorageKeys.Clear();
    }

    /// <summary>
    /// Governed controlled-document regeneration for the affected baselines. Prior stored rendition bytes are
    /// preserved (never deleted) and replaced only after the renderer produced and hashed the new bytes;
    /// existing human signatures are superseded as evidence, never rewritten or fabricated.
    /// </summary>
    private async Task RegenerateAffectedDocumentsAsync(
        IReadOnlyCollection<Guid> affectedBaselineIds, DateTimeOffset now, CancellationToken ct)
    {
        var baselineIds = affectedBaselineIds.ToList();
        var documents = await db.ControlledDocuments
            .Where(x => baselineIds.Contains(x.BaselineId)
                && (x.Type == ControlledDocumentType.SystemTestProcedures
                    || x.Type == ControlledDocumentType.HighLevelTestCases
                    || x.Type == ControlledDocumentType.LowLevelTestCases))
            .OrderBy(x => x.Id).ToListAsync(ct);
        if (documents.Count == 0) return;
        var artifacts = await db.ControlledDocumentArtifacts
            .Where(x => documents.Select(d => d.Id).Contains(x.DocumentId))
            .OrderBy(x => x.DocumentId).ThenBy(x => x.Format).ToListAsync(ct);
        var documentIds = documents.Select(x => x.Id).ToList();
        var artifactIds = artifacts.Select(x => x.Id).ToList();
        var signatures = await db.ElectronicSignatures.AsNoTracking()
            .Where(x => x.ArtifactType == "ControlledDocument" && documentIds.Contains(x.ArtifactId)
                || x.ArtifactType == "ControlledDocumentArtifact" && artifactIds.Contains(x.ArtifactId))
            .ToListAsync(ct);
        var signatureTargets = signatures.Select(x => $"ElectronicSignature:{x.Id}").ToHashSet();
        var alreadyPending = await db.SecurityAuditEvents.AsNoTracking()
            .Where(x => x.EventType == MigrationMarker + ".SignatureSuperseded"
                && signatureTargets.Contains(x.Target))
            .Select(x => x.Target).ToHashSetAsync(ct);
        var alreadyCompletedTargets = await db.SecurityAuditEvents.AsNoTracking()
            .Where(x => (x.EventType == MigrationMarker + ".SignatureSupersessionCompleted"
                    || x.EventType == MigrationMarker + ".SignatureHashVerified")
                && signatureTargets.Contains(x.Target))
            .Select(x => x.Target).ToHashSetAsync(ct);
        foreach (var signature in signatures.Where(x =>
                     !alreadyPending.Contains($"ElectronicSignature:{x.Id}")
                     && !alreadyCompletedTargets.Contains($"ElectronicSignature:{x.Id}")))
            db.SecurityAuditEvents.Add(new SecurityAuditEvent(
                MigrationMarker + ".SignatureSuperseded", Actor,
                $"ElectronicSignature:{signature.Id}", "Superseded",
                Json(new
                {
                    migration = MigrationMarker,
                    oldArtifactIdentity = signature.ArtifactRevision,
                    oldSignatureId = signature.Id,
                    oldSignatureHash = signature.ContentHash,
                    newContentHash = (string?)null,
                    reason = "Affected controlled document bytes/content basis changed under the governed Procedure cutover; the original human signature row and hash remain unchanged and require a new human signature."
                }), "", now));
        await db.SaveChangesAsync(ct);

        var renditionByDocumentId = new Dictionary<Guid, (string OldHash, string NewHash)>();
        var renditionByArtifactId = new Dictionary<Guid, (string OldHash, string NewHash)>();
        var baselinesById = await db.CandidateBaselines.AsNoTracking()
            .Where(x => baselineIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, ct);
        foreach (var document in documents)
        {
            var baseline = baselinesById[document.BaselineId];
            // #747 temporal rule: a document is exact ONLY when the baseline was already verification-
            // materialized at or before the document's own generation time. A document generated before
            // later baseline materialization remains a legacy compatibility document forever.
            var exactManifest = baseline.TestProceduresMaterializedAt is { } materializedAt
                && materializedAt <= document.GeneratedAt;
            string contentBasis;
            if (exactManifest)
            {
                if (string.IsNullOrWhiteSpace(baseline.TestProceduresHash))
                    throw new InvalidOperationException(
                        $"Procedure cutover cannot render document {document.Id} without a verification manifest hash.");
                contentBasis = $"{baseline.TestProceduresHash}|{document.Type}|{document.ArtifactCount}|{Actor}";
            }
            else
            {
                // #747-compatible legacy document: no exact manifest exists for THIS document and none is
                // invented. Reconstruct the historical compatibility snapshot at the document's ORIGINAL
                // GeneratedAt, validate the artifact count, reject duplicate revision identities, require
                // every revision identity and owner to resolve, then hash the exact identities through the
                // canonical TestProcedureManifest semantics.
                var snapshot = await ControlledProcedureDocumentSnapshotProjection.ForDocumentAsync(db,
                    document.BaselineId, ArtifactKeyForDocument(document.Type), document.GeneratedAt, ct);
                if (snapshot.IsExactManifest)
                    throw new InvalidOperationException(
                        $"Procedure cutover legacy document {document.Id} on baseline {baseline.Id} resolved as an exact manifest despite the temporal legacy rule; refusing to fabricate a manifest.");
                if (snapshot.Rows.Count != document.ArtifactCount)
                    throw new InvalidOperationException(
                        $"Procedure cutover legacy document {document.Id} reconstructed {snapshot.Rows.Count} rows but records ArtifactCount {document.ArtifactCount}.");
                var snapshotRevisionIds = snapshot.Rows.Select(row => row.RevisionId).ToList();
                if (snapshotRevisionIds.Distinct().Count() != snapshotRevisionIds.Count)
                    throw new InvalidOperationException(
                        $"Procedure cutover legacy document {document.Id} on baseline {baseline.Id} contains duplicate revision identities; refusing to regenerate.");
                var snapshotOwners = await db.TestProcedureRevisions.AsNoTracking()
                    .Where(revision => snapshotRevisionIds.Contains(revision.Id))
                    .Select(revision => new { revision.Id, revision.ProcedureId })
                    .ToListAsync(ct);
                if (snapshotOwners.Count != snapshotRevisionIds.Count)
                    throw new InvalidOperationException(
                        $"Procedure cutover legacy document {document.Id} on baseline {baseline.Id} references one or more revisions that no longer exist; refusing to regenerate.");
                var procedureIdByRevision = snapshotOwners.ToDictionary(x => x.Id, x => x.ProcedureId);
                var ownerProcedureIds = procedureIdByRevision.Values.Distinct().ToList();
                var resolvedOwners = await db.TestProcedures.AsNoTracking()
                    .Where(procedure => ownerProcedureIds.Contains(procedure.Id)
                        && procedure.ProjectId == baseline.ProjectId)
                    .Select(procedure => procedure.Id)
                    .ToListAsync(ct);
                if (resolvedOwners.Count != ownerProcedureIds.Count)
                    throw new InvalidOperationException(
                        $"Procedure cutover legacy document {document.Id} on baseline {baseline.Id} references owners that are missing or outside the baseline project; refusing to regenerate.");
                var compatibilityEntries = snapshot.Rows.Select(row => new TestProcedureManifestEntry(
                    procedureIdByRevision[row.RevisionId], row.RevisionId, row.BaseNumber, row.Revision)).ToList();
                var compatibilitySnapshotHash = TestProcedureManifest.Hash(compatibilityEntries);
                contentBasis = $"{compatibilitySnapshotHash}|{document.Type}|{document.ArtifactCount}|{Actor}";
                db.SecurityAuditEvents.Add(new SecurityAuditEvent(
                    MigrationMarker + ".LegacyDocumentBasisReconstructed", Actor,
                    $"ControlledDocument:{document.Id}", "Succeeded",
                    Json(new
                    {
                        migration = MigrationMarker,
                        documentId = document.Id,
                        baselineId = baseline.Id,
                        generatedAt = document.GeneratedAt,
                        artifactCount = document.ArtifactCount,
                        compatibilitySnapshotHash,
                        baselineManifestStatePreserved = true,
                        baselineMaterializedAt = baseline.TestProceduresMaterializedAt,
                        baselineWasMaterializedWhenDocumentGenerated = false,
                        documentGeneratedAtPreserved = true,
                        reason = "The controlled document predates exact build-scoped verification manifests. Its execution-cutover basis was reconstructed from the existing generation-time compatibility snapshot; no historical baseline materialization state was fabricated and the original generation time was preserved so later revisions cannot leak into the regenerated document."
                    }), "", now));
            }
            var contentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(contentBasis)))
                .ToLowerInvariant();
            var oldHash = document.ContentHash;
            renditionByDocumentId[document.Id] = (oldHash, contentHash);
            document.RecordExecutionCutoverContentBasis(contentHash);
            db.SecurityAuditEvents.Add(new SecurityAuditEvent(
                MigrationMarker + ".DocumentContentBasisRewritten", Actor,
                $"ControlledDocument:{document.Id}", "Succeeded",
                Json(new
                {
                    migration = MigrationMarker, documentId = document.Id,
                    oldContentHash = oldHash, newContentHash = contentHash,
                    storedArtifactCount = artifacts.Count(x => x.DocumentId == document.Id),
                    outputBytesRegenerated = artifacts.Any(x => x.DocumentId == document.Id),
                    exactManifest,
                    generatedAtPreserved = document.GeneratedAt,
                    reason = exactManifest
                        ? "Affected document content basis was refreshed before governed stored-rendition regeneration."
                        : "Legacy unmaterialized document content basis was recomputed from the historical compatibility snapshot at its original GeneratedAt; no baseline manifest hash or materialization was invented."
                }), "", now));
        }
        await db.SaveChangesAsync(ct);

        foreach (var artifact in artifacts)
        {
            ct.ThrowIfCancellationRequested();
            if (!Files.Exists(artifact.StorageKey))
                throw new InvalidOperationException(
                    $"Procedure cutover cannot read stored rendition {artifact.Id} ({artifact.StorageKey}).");
            var oldHash = artifact.Sha256;
            var oldStorageKey = artifact.StorageKey;
            var output = await Generator.GenerateAsync(artifact.DocumentId, artifact.Format, ct)
                ?? throw new InvalidOperationException(
                    $"Procedure cutover could not regenerate document {artifact.DocumentId} ({artifact.Format}).");
            await using var content = new MemoryStream(output.Content, writable: false);
            var stored = await Files.StoreAsync(content, output.FileName, output.ContentType, ct);
            _pendingNewStorageKeys.Add(stored.StorageKey);
            artifact.ReplaceMigrationRendition(stored.StorageKey, stored.OriginalFileName,
                stored.ContentType, stored.Size, stored.Sha256, now);
            renditionByArtifactId[artifact.Id] = (oldHash, stored.Sha256);
            db.SecurityAuditEvents.Add(new SecurityAuditEvent(
                MigrationMarker + ".DocumentRenditionRewritten", Actor,
                $"ControlledDocumentArtifact:{artifact.Id}", "Succeeded",
                Json(new
                {
                    migration = MigrationMarker, documentId = artifact.DocumentId, format = artifact.Format,
                    oldStorageKey, oldContentHash = oldHash, newStorageKey = stored.StorageKey,
                    newContentHash = stored.Sha256,
                    reason = "Regenerated through ControlledOutputGenerator after the governed Procedure execution cutover; prior rendition bytes were preserved."
                }), "", now));
        }
        await CompleteDocumentSignatureSupersessionsAsync(
            signatures.Select(x => x.Id).ToHashSet(), renditionByDocumentId,
            renditionByArtifactId, now, ct);
        await db.SaveChangesAsync(ct);
    }

    private async Task CompleteDocumentSignatureSupersessionsAsync(
        IReadOnlySet<Guid> scopedSignatureIds,
        IReadOnlyDictionary<Guid, (string OldHash, string NewHash)> renditionByDocumentId,
        IReadOnlyDictionary<Guid, (string OldHash, string NewHash)> renditionByArtifactId,
        DateTimeOffset now, CancellationToken ct)
    {
        var scopedTargets = scopedSignatureIds.Select(x => $"ElectronicSignature:{x}").ToHashSet();
        // A signature already carrying completion evidence is never reprocessed; only this project's own
        // pending supersessions are completed, so one project's cutover cannot touch another's evidence.
        var alreadyCompletedTargets = await db.SecurityAuditEvents.AsNoTracking()
            .Where(x => (x.EventType == MigrationMarker + ".SignatureSupersessionCompleted"
                    || x.EventType == MigrationMarker + ".SignatureHashVerified")
                && scopedTargets.Contains(x.Target))
            .Select(x => x.Target).ToHashSetAsync(ct);
        var pending = await db.SecurityAuditEvents
            .Where(x => x.EventType == MigrationMarker + ".SignatureSuperseded"
                && scopedTargets.Contains(x.Target))
            .ToListAsync(ct);
        foreach (var evidence in pending)
        {
            if (alreadyCompletedTargets.Contains(evidence.Target)) continue;
            var detail = JsonNode.Parse(evidence.Detail)?.AsObject()
                ?? throw new InvalidOperationException(
                    $"Procedure cutover signature evidence {evidence.Id} is not valid structured JSON.");
            var signatureId = ParseSignatureTarget(evidence.Target);
            var signature = await db.ElectronicSignatures.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == signatureId, ct)
                ?? throw new InvalidOperationException(
                    $"Procedure cutover evidence {evidence.Id} has no source signature {signatureId}.");
            string replacementHash;
            string eventType = MigrationMarker + ".SignatureSupersessionCompleted";
            string reason = "The controlled document content basis or stored rendition bytes changed under the governed Procedure cutover; the original human signature row and hash remain unchanged and require a new human signature.";
            if (signature.ArtifactType.Equals("ControlledDocumentArtifact", StringComparison.OrdinalIgnoreCase)
                && renditionByArtifactId.TryGetValue(signature.ArtifactId, out var artifactRendition))
            {
                if (!string.Equals(signature.ContentHash, artifactRendition.OldHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"Signature {signature.Id} does not match the exact pre-migration stored artifact hash.");
                replacementHash = artifactRendition.NewHash;
            }
            else if (signature.ArtifactType.Equals("ControlledDocument", StringComparison.OrdinalIgnoreCase)
                     && renditionByDocumentId.TryGetValue(signature.ArtifactId, out var documentRendition))
            {
                if (!string.Equals(signature.ContentHash, documentRendition.OldHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"Signature {signature.Id} does not match the exact pre-migration controlled document content-basis hash.");
                replacementHash = documentRendition.NewHash;
            }
            else
                throw new InvalidOperationException(
                    $"Signature {signature.Id} ({signature.ArtifactType}/{signature.ArtifactId}) has no exact replacement hash authority.");
            if (string.Equals(replacementHash, signature.ContentHash, StringComparison.OrdinalIgnoreCase))
            {
                eventType = MigrationMarker + ".SignatureHashVerified";
                reason = "The signed bytes remained byte-identical after the governed Procedure cutover; the signature remains valid evidence.";
            }
            db.SecurityAuditEvents.Add(new SecurityAuditEvent(
                eventType, Actor, evidence.Target, "Succeeded",
                Json(new
                {
                    migration = MigrationMarker, pendingEvidenceId = evidence.Id, signatureId = signature.Id,
                    oldArtifactIdentity = signature.ArtifactRevision, oldSignatureHash = signature.ContentHash,
                    newArtifactIdentity = detail["oldArtifactIdentity"]?.GetValue<string>(),
                    newContentHash = replacementHash, reason
                }), "", now));
        }
    }

    private static Guid ParseSignatureTarget(string target)
    {
        var value = target.StartsWith("ElectronicSignature:", StringComparison.Ordinal)
            ? target["ElectronicSignature:".Length..] : "";
        return Guid.TryParse(value, out var id) && id != Guid.Empty
            ? id : throw new InvalidOperationException(
                $"Procedure cutover signature target '{target}' is not a valid ElectronicSignature identity.");
    }

    private static VerificationArtifactKey ArtifactKeyForDocument(ControlledDocumentType type) =>
        type switch
        {
            ControlledDocumentType.SystemTestProcedures => new VerificationArtifactKey(
                VerificationDiscipline.System, VerificationArtifactKind.Procedure),
            ControlledDocumentType.HighLevelTestCases => new VerificationArtifactKey(
                VerificationDiscipline.HighLevelSoftware, VerificationArtifactKind.Case),
            ControlledDocumentType.LowLevelTestCases => new VerificationArtifactKey(
                VerificationDiscipline.LowLevelSoftware, VerificationArtifactKind.Case),
            _ => throw new InvalidOperationException(
                $"Unsupported #726 cutover document type {type}."),
        };

    /// <summary>
    /// Recovers the honest per-project cutover totals from persisted governed evidence. Used when a previous
    /// run crashed before the global Completed marker, so a rerun never reports zero for completed work.
    /// </summary>
    private async Task<SoftwareProcedureCutoverResult> RecoverCompletedTotalsAsync(
        IReadOnlySet<Guid> excludeProjectIds, CancellationToken ct)
    {
        var upgradedProjectIds = await db.ProjectLadderConfigurations.AsNoTracking()
            .Where(x => x.IsSealed && x.State == ProjectLadderConfigurationState.Active
                && x.LastUpgradeVersion == UpgradeVersion && x.LastUpgradeBy == Actor
                && !excludeProjectIds.Contains(x.ProjectId))
            .Select(x => x.ProjectId).ToListAsync(ct);
        if (upgradedProjectIds.Count == 0) return new(0, 0, 0, 0, 0, 0);
        // The generated revisions must be restricted to the recovered projects' own procedures; unrelated
        // or still-pending projects' evidence must never leak into these totals.
        var upgradedProjectIdSet = upgradedProjectIds.ToHashSet();
        var generatedRevisions = await (from revision in db.TestProcedureRevisions.AsNoTracking()
                                        join procedure in db.TestProcedures.AsNoTracking()
                                            on revision.ProcedureId equals procedure.Id
                                        where revision.AuthorId == Actor
                                            && revision.ParentKind == VerificationProcedureParentKind.Allocated
                                            && (revision.State == TestProcedureState.Approved
                                                || revision.State == TestProcedureState.Retired)
                                            && upgradedProjectIdSet.Contains(procedure.ProjectId)
                                        select new { revision.Id, revision.ProcedureId }).ToListAsync(ct);
        var generatedRevisionIds = generatedRevisions.Select(x => x.Id).ToHashSet();
        var generatedProcedureIds = generatedRevisions.Select(x => x.ProcedureId).ToHashSet();
        var executions = await db.TestExecutions.AsNoTracking()
            .CountAsync(x => generatedRevisionIds.Contains(x.ProcedureRevisionId), ct);
        var entries = await db.BuildTestSetEntries.AsNoTracking()
            .CountAsync(x => generatedRevisionIds.Contains(x.ProcedureRevisionId), ct);
        var selections = await db.BaselineTestProcedures.AsNoTracking()
            .CountAsync(x => generatedProcedureIds.Contains(x.ProcedureId), ct);
        var impacts = await db.VerificationImpactItems.AsNoTracking()
            .CountAsync(x => x.ResolvedProcedureRevisionId != null
                    && generatedRevisionIds.Contains(x.ResolvedProcedureRevisionId.Value)
                || x.ProcedureId != null && generatedProcedureIds.Contains(x.ProcedureId.Value)
                || x.ResolvedProcedureId != null && generatedProcedureIds.Contains(x.ResolvedProcedureId.Value), ct);
        return new(upgradedProjectIds.Count, generatedRevisions.Count, executions, entries, selections, impacts);
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
