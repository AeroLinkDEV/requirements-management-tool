using System.Net;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Traceability;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace AeroLink.Infrastructure.Tests;

[CollectionDefinition("Issue727Postgres", DisableParallelization = true)]
public sealed class Issue727PostgresCollection : ICollectionFixture<object>;

/// <summary>
/// PostgreSQL-only qualification for #727. These tests deliberately migrate from the exact predecessor and
/// execute raw SQL around the save boundary: SQLite cannot prove the trigger contract that keeps attributed
/// #709 evidence immutable after its transient relation or causal revision has been dematerialized.
/// </summary>
[Collection("Issue727Postgres")]
public sealed class CaseProcedureLifecyclePostgresQualificationTests
{
    private const string Predecessor = "20260824025544_AddProcedureControlledDocuments";
    private const string DatabaseName = "aerolink_727_qualify";
    private const int Port = 55472;
    private static readonly DateTimeOffset Now = new(2026, 8, 23, 4, 27, 0, TimeSpan.Zero);

    [DisposablePostgresFact]
    public async Task Exact_predecessor_upgrade_retains_requirement_lifecycle_evidence_through_actual_reopen()
    {
        var connection = QualificationConnectionOrThrow();
        await using var db = await MigrateToPredecessorAsync(connection);
        var program = new ProgramRecord("Issue 727 requirement retention", "I7R");
        var project = new ProjectRecord(program.Id, "Requirement retention", "Issue 727 software");
        var release = new SoftwareRelease(project.Id, "7.27", false);
        var request = ApprovedSystemChange(project.Id, release.Id, "SRCR-727200");
        var baseline = new CandidateBaseline("BL-727200", 0, project.Id, release.Id, null,
            "Candidate exact-link retention", "cm", Now);
        baseline.Select(request, "cm", Now);
        baseline.Freeze("cm", Now);
        baseline.MarkRequirementsMaterialized("cm", new string('a', 64), 2, Now);
        var childArtifact = new RequirementArtifact(project.Id, "HLR-727201", RequirementLevel.HighLevel, Now);
        var parentArtifact = new RequirementArtifact(project.Id, "SYSR-727202", RequirementLevel.System, Now);
        var child = new RequirementRevision(childArtifact.Id, 0, "Child", "Rationale", "Test",
            RequirementRevisionState.Active, request.Id, baseline.Id, Now,
            parentKind: RequirementParentKind.Derived, derivedRationale: "Focused exact-link fixture.");
        var parent = new RequirementRevision(parentArtifact.Id, 0, "Parent", "Rationale", "Test",
            RequirementRevisionState.Active, request.Id, baseline.Id, Now);
        var link = new RequirementTraceLink(project.Id, child.Id, parent.Id,
            RequirementTraceType.DerivedFrom, "Exact predecessor trace.", Now);
        db.AddRange(program, project, release, request, baseline, childArtifact, parentArtifact, child, parent, link);
        db.BaselineRequirements.AddRange(
            new BaselineRequirementSelection(baseline.Id, childArtifact.Id, child.Id),
            new BaselineRequirementSelection(baseline.Id, parentArtifact.Id, parent.Id));
        await db.SaveChangesAsync();

        var lifecycleId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO exact_link_suspect_lifecycles
                ("Id", "ProjectId", "LinkKind", "LinkId", "State", "CauseKind",
                 "CauseRequirementRevisionId", "CauseBaselineImportId", "RaisedBy", "RaisedAt",
                 "RaisedRationale", "AcknowledgedBy", "AcknowledgedAt", "AcknowledgementRationale",
                 "Outcome", "ResolvedBy", "ResolvedAt", "ResolutionRationale", "UpdatedAt", "Version")
            VALUES ({lifecycleId}, {project.Id}, {"RequirementTrace"}, {link.Id}, {"Suspect"},
                {"InternalRequirementRevision"}, {parent.Id}, NULL, {"requirements.materializer"}, {Now},
                {"The exact parent revision changed."}, NULL, NULL, NULL, NULL, NULL, NULL, NULL, {Now}, {1L});
            INSERT INTO exact_link_suspect_events
                ("Id", "LifecycleId", "ProjectId", "LinkKind", "LinkId", "EventType", "CauseKind",
                 "CauseRequirementRevisionId", "CauseBaselineImportId", "ActorId", "Rationale", "Outcome", "OccurredAt")
            VALUES ({eventId}, {lifecycleId}, {project.Id}, {"RequirementTrace"}, {link.Id}, {"Raised"},
                {"InternalRequirementRevision"}, {parent.Id}, NULL, {"requirements.materializer"},
                {"The exact parent revision changed."}, NULL, {Now});
            UPDATE requirement_trace_links SET "ExactLinkSuspectLifecycleId" = {lifecycleId} WHERE "Id" = {link.Id};
            """);

        await db.Database.GetService<IMigrator>().MigrateAsync();
        db.ChangeTracker.Clear();
        var reopened = await db.CandidateBaselines.SingleAsync(x => x.Id == baseline.Id);
        await new RequirementBaselineDematerializer(db, new VerificationImpactService(db))
            .DematerializeAsync(reopened.Id, "cm", reopened.DisplayNumber, Now.AddMinutes(1), default);
        reopened.Reopen("cm", "Correct the in-work candidate.", Now.AddMinutes(1));
        await db.SaveChangesAsync();

        Assert.Equal(CandidateBaselineState.Draft, reopened.State);
        Assert.Null(reopened.RequirementsMaterializedAt);
        Assert.Empty(await db.RequirementTraces.AsNoTracking().ToListAsync());
        Assert.Empty(await db.RequirementRevisions.AsNoTracking()
            .Where(x => x.EffectiveBaselineId == baseline.Id).ToListAsync());
        var retained = await db.ExactLinkSuspectLifecycles.AsNoTracking().SingleAsync(x => x.Id == lifecycleId);
        var retainedEvent = await db.ExactLinkSuspectEvents.AsNoTracking().SingleAsync(x => x.Id == eventId);
        Assert.Equal(parent.Id, retained.CauseRequirementRevisionId);
        Assert.Equal(parent.Id, retainedEvent.CauseRequirementRevisionId);
        Assert.Equal(link.Id, retained.LinkId);
        Assert.Equal(link.Id, retainedEvent.LinkId);
    }

    [DisposablePostgresFact]
    public async Task Latest_migration_fails_closed_for_raw_causes_attribution_evidence_and_link_association_changes()
    {
        var connection = QualificationConnectionOrThrow();
        await using var db = await ResetAtLatestAsync(connection);
        var fixture = await SeedCaseProcedureLifecycleAsync(db);

        await new ExactLinkLifecycleService(db).AcknowledgeAsync(ExactLinkKind.CaseProcedure,
            fixture.CarriedLinkId, "test.lead", "Assess the carried Procedure relationship.",
            Now.AddMinutes(1), default);
        Assert.Equal(2, await db.ExactLinkSuspectEvents.CountAsync(x => x.LifecycleId == fixture.LifecycleId));

        await AssertPostgresRefusalAsync(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO exact_link_suspect_lifecycles
                ("Id", "ProjectId", "LinkKind", "LinkId", "State", "CauseKind",
                 "CauseRequirementRevisionId", "CauseBaselineImportId", "CauseVerificationRevisionId",
                 "RaisedBy", "RaisedAt", "RaisedRationale", "UpdatedAt", "Version")
            VALUES ({Guid.NewGuid()}, {fixture.ProjectId}, {"CaseProcedure"}, {Guid.NewGuid()}, {"Suspect"},
                {"InternalVerificationRevision"}, NULL, NULL, {Guid.NewGuid()}, {"raw.sql"}, {Now},
                {"Missing causal Case revision."}, {Now}, {1L});
            """), "existing Case revision");

        await AssertPostgresRefusalAsync(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE exact_link_suspect_lifecycles SET "RaisedRationale" = {"Rewritten attribution"}
            WHERE "Id" = {fixture.LifecycleId};
            """), "identity, cause, and raised attribution are immutable");

        await AssertPostgresRefusalAsync(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO exact_link_suspect_events
                ("Id", "LifecycleId", "ProjectId", "LinkKind", "LinkId", "EventType", "CauseKind",
                 "CauseRequirementRevisionId", "CauseBaselineImportId", "CauseVerificationRevisionId",
                 "ActorId", "Rationale", "Outcome", "OccurredAt")
            VALUES ({Guid.NewGuid()}, {fixture.LifecycleId}, {fixture.ProjectId}, {"CaseProcedure"},
                {fixture.HistoricalLinkId}, {"Acknowledged"}, {"InternalVerificationRevision"}, NULL, NULL,
                {fixture.CaseRevisionId}, {"raw.sql"}, {"Wrong exact-link attribution."}, NULL, {Now});
            """), "retain its lifecycle exact attribution");

        var raisedEventId = await db.ExactLinkSuspectEvents.AsNoTracking()
            .Where(x => x.LifecycleId == fixture.LifecycleId && x.EventType == ExactLinkLifecycleEventType.Raised)
            .Select(x => x.Id).SingleAsync();
        await AssertPostgresRefusalAsync(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE exact_link_suspect_events SET "Rationale" = {"Rewritten evidence"} WHERE "Id" = {raisedEventId};
            """), "cannot be changed or deleted");
        await AssertPostgresRefusalAsync(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM exact_link_suspect_events WHERE "Id" = {raisedEventId};
            """), "cannot be changed or deleted");

        await AssertPostgresRefusalAsync(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE test_case_procedure_links SET "CaseRevisionId" = {fixture.HistoricalCaseRevisionId}
            WHERE "Id" = {fixture.CarriedLinkId};
            """), "cannot be retargeted");
        await AssertPostgresRefusalAsync(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE test_case_procedure_links SET "ProcedureRevisionId" = {fixture.AlternateProcedureRevisionId}
            WHERE "Id" = {fixture.CarriedLinkId};
            """), "cannot be retargeted");

        await AssertPostgresRefusalAsync(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE test_case_procedure_links SET "ExactLinkSuspectLifecycleId" = {fixture.LifecycleId}
            WHERE "Id" = {fixture.HistoricalLinkId};
            """), "immutable suspect lifecycle association");
        await AssertPostgresRefusalAsync(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE test_case_procedure_links SET "ExactLinkSuspectLifecycleId" = {Guid.NewGuid()}
            WHERE "Id" = {fixture.CarriedLinkId};
            """), "immutable suspect lifecycle association");
        await AssertPostgresRefusalAsync(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE test_case_procedure_links SET "ExactLinkSuspectLifecycleId" = NULL
            WHERE "Id" = {fixture.CarriedLinkId};
            """), "immutable suspect lifecycle association");

        Assert.Equal(1, await db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM test_case_procedure_links WHERE "Id" = {fixture.CarriedLinkId};
            """));
        await AssertPostgresRefusalAsync(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM exact_link_suspect_lifecycles WHERE "Id" = {fixture.LifecycleId};
            """), "cannot be changed or deleted");

        var retained = await db.ExactLinkSuspectLifecycles.AsNoTracking()
            .SingleAsync(x => x.Id == fixture.LifecycleId);
        Assert.Equal(fixture.CarriedLinkId, retained.LinkId);
        Assert.Equal(fixture.CaseRevisionId, retained.CauseVerificationRevisionId);
        Assert.Equal(2, await db.ExactLinkSuspectEvents.AsNoTracking()
            .CountAsync(x => x.LifecycleId == fixture.LifecycleId));
    }

    [Theory]
    [InlineData("Host=example.test;Port=55472;Database=aerolink_727_qualify")]
    [InlineData("Host=127.0.0.1;Port=54329;Database=aerolink_727_qualify")]
    [InlineData("Host=127.0.0.1;Port=55428;Database=aerolink_727_qualify")]
    [InlineData("Host=127.0.0.1;Port=55472;Database=other_database")]
    public void Qualification_connection_rejects_every_non_disposable_target(string connection)
    {
        var error = Assert.Throws<InvalidOperationException>(() => ValidateQualificationConnection(connection));
        Assert.Contains("Issue #727", error.Message, StringComparison.Ordinal);
    }

    private static async Task<CaseLifecycleFixture> SeedCaseProcedureLifecycleAsync(AeroLinkDbContext db)
    {
        var policy = ProcedureEnabledPolicy();
        var program = new ProgramRecord("Issue 727 Case lifecycle", "I7C");
        var project = new ProjectRecord(program.Id, "Case lifecycle", "Issue 727 software");
        var caseArtifact = new TestProcedure(project.Id, "HLRTC-727300", "Controlled Case", "case.author",
            Now, TestProcedureLevel.HighLevel, policy, VerificationArtifactKind.Case);
        var caseRevision0 = new TestProcedureRevision(caseArtifact.Id, 0, "Case objective", "Setup", "Steps",
            "Expected", TestProcedureState.Approved, "case.author", Now,
            parentKind: VerificationProcedureParentKind.Derived, derivedRationale: "Focused Case fixture.");
        var procedureArtifact = new TestProcedure(project.Id, "HLRTP-727300", "Controlled Procedure",
            "procedure.author", Now, TestProcedureLevel.HighLevel, policy, VerificationArtifactKind.Procedure,
            VerificationProcedureParentKind.Allocated);
        var procedureRevision = new TestProcedureRevision(procedureArtifact.Id, 0, "Procedure objective",
            "Procedure setup", "Procedure steps", "Expected observation", TestProcedureState.Draft,
            "procedure.author", Now, environmentSetup: "Procedure setup", orderedSteps: "Procedure steps",
            testData: "Controlled input", expectedObservations: "Expected observation", cleanup: "Restore",
            toolingAutomation: "Qualified runner", parentKind: VerificationProcedureParentKind.Allocated);
        var alternateProcedureArtifact = new TestProcedure(project.Id, "HLRTP-727301",
            "Alternate controlled Procedure", "procedure.author", Now, TestProcedureLevel.HighLevel,
            policy, VerificationArtifactKind.Procedure, VerificationProcedureParentKind.Derived);
        var alternateProcedureRevision = new TestProcedureRevision(alternateProcedureArtifact.Id, 0,
            "Alternate Procedure objective", "Alternate setup", "Alternate steps", "Alternate observation",
            TestProcedureState.Draft, "procedure.author", Now, environmentSetup: "Alternate setup",
            orderedSteps: "Alternate steps", testData: "Alternate controlled input",
            expectedObservations: "Alternate observation", cleanup: "Restore alternate fixture",
            toolingAutomation: "Qualified alternate runner",
            parentKind: VerificationProcedureParentKind.Derived,
            derivedRationale: "The alternate same-level Procedure is independently derived for the retarget probe.");
        var historical = new TestCaseProcedureLink(caseRevision0.Id, procedureRevision.Id);
        db.AddRange(program, project, caseArtifact, caseRevision0, procedureArtifact, procedureRevision,
            alternateProcedureArtifact, alternateProcedureRevision, historical);
        using (db.UseSaveBoundaryPolicy(policy)) await db.SaveChangesAsync();
        db.Entry(procedureRevision).Property(x => x.State).CurrentValue = TestProcedureState.Approved;
        db.Entry(alternateProcedureRevision).Property(x => x.State).CurrentValue = TestProcedureState.Approved;
        using (db.UseSaveBoundaryPolicy(policy)) await db.SaveChangesAsync();

        var caseRevision1 = new TestProcedureRevision(caseArtifact.Id, 1, "Revised Case objective", "Setup",
            "Revised steps", "Expected", TestProcedureState.Approved, "case.author", Now.AddMinutes(1),
            parentKind: VerificationProcedureParentKind.Derived, derivedRationale: "Revised focused Case fixture.");
        db.Add(caseRevision1);
        using (db.UseSaveBoundaryPolicy(policy)) await db.SaveChangesAsync();

        var carried = new TestCaseProcedureLink(caseRevision1.Id, procedureRevision.Id);
        var lifecycle = ExactLinkSuspectLifecycle.Raise(project.Id, ExactLinkKind.CaseProcedure, carried.Id,
            ExactLinkLifecycleCauseKind.InternalVerificationRevision, null, null, "cm",
            "The exact Case revision changed.", Now.AddMinutes(2), caseRevision1.Id);
        carried.AttachExactLinkLifecycle(lifecycle.Id);
        db.AddRange(carried, lifecycle);
        db.ExactLinkSuspectEvents.AddRange(lifecycle.Events);
        using (db.UseSaveBoundaryPolicy(policy)) await db.SaveChangesAsync();
        return new CaseLifecycleFixture(project.Id, caseRevision0.Id, caseRevision1.Id,
            alternateProcedureRevision.Id, historical.Id, carried.Id, lifecycle.Id);
    }

    private static async Task AssertPostgresRefusalAsync(Func<Task<int>> operation, string expected)
    {
        var error = await Assert.ThrowsAsync<PostgresException>(operation);
        Assert.Contains(expected, error.MessageText, StringComparison.Ordinal);
    }

    private static SystemChangeRequest ApprovedSystemChange(Guid projectId, Guid releaseId, string number)
    {
        var request = new SystemChangeRequest(number, 0, projectId, releaseId, "Baseline authority",
            "Problem", "Analysis", "Solution", "author", Now);
        request.AddRequirementChange("author", "SYSR-727200", 0, RequirementLevel.System,
            RequirementChangeKind.Introduce, "The system shall retain exact-link evidence.",
            "Qualification authority.", "Analysis", Now);
        request.SubmitForReview("author", [new ApproverSelection("reviewer", "Reviewer")], Now);
        request.ApproveActiveStage("reviewer", Now);
        return request;
    }

    private static ILadderPolicy ProcedureEnabledPolicy()
    {
        var projectId = Guid.NewGuid();
        var configuration = ProjectLadderConfiguration.CreateDraft(projectId, Now);
        var steps = new List<ProjectLadderStep>();
        foreach (var (level, position) in LegacyLadderPolicy.Instance.OrderedLevels.Select((x, i) => (x, i + 1)))
        {
            var kinds = level == RequirementLevel.System
                ? new[] { VerificationArtifactKind.Procedure }
                : new[] { VerificationArtifactKind.Case, VerificationArtifactKind.Procedure };
            var step = new ProjectLadderStep(configuration.Id, projectId, level, position,
                LegacyLadderPolicy.Instance.Definition(level).Capabilities, Now, kinds);
            configuration.Steps.Add(step);
            steps.Add(step);
        }
        configuration.AllowedUpstream.Add(new ProjectLadderAllowedUpstream(
            configuration.Id, projectId, steps[0].Id, steps[1].Id, Now));
        configuration.AllowedUpstream.Add(new ProjectLadderAllowedUpstream(
            configuration.Id, projectId, steps[1].Id, steps[2].Id, Now));
        return new ResolvedProjectLadderPolicy(ProjectLadderResolver.Resolve(configuration));
    }

    private static async Task<AeroLinkDbContext> ResetAtLatestAsync(string connection)
    {
        var db = new AeroLinkDbContext(Options(connection));
        await db.Database.EnsureDeletedAsync();
        await db.Database.GetService<IMigrator>().MigrateAsync();
        return db;
    }

    private static async Task<AeroLinkDbContext> MigrateToPredecessorAsync(string connection)
    {
        var db = new AeroLinkDbContext(Options(connection));
        await db.Database.EnsureDeletedAsync();
        await db.Database.GetService<IMigrator>().MigrateAsync(Predecessor);
        return db;
    }

    private static DbContextOptions<AeroLinkDbContext> Options(string connection) =>
        new DbContextOptionsBuilder<AeroLinkDbContext>().UseNpgsql(connection).Options;

    private static string QualificationConnectionOrThrow() => ValidateQualificationConnection(
        Environment.GetEnvironmentVariable("AEROLINK_MIGRATIONS_CONNECTION"));

    private static string ValidateQualificationConnection(string? connection)
    {
        if (string.IsNullOrWhiteSpace(connection))
            throw new InvalidOperationException("Issue #727 PostgreSQL qualification requires AEROLINK_MIGRATIONS_CONNECTION.");
        var builder = new NpgsqlConnectionStringBuilder(connection);
        var host = (builder.Host ?? string.Empty).Trim().Trim('[', ']');
        var loopback = string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));
        if (!loopback)
            throw new InvalidOperationException("Issue #727 PostgreSQL qualification requires a loopback host.");
        if (builder.Port != Port)
            throw new InvalidOperationException($"Issue #727 qualification requires exact disposable port {Port} and refuses 54329.");
        if (!string.Equals(builder.Database, DatabaseName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Issue #727 qualification requires dedicated database {DatabaseName}.");
        return connection;
    }

    private sealed class DisposablePostgresFactAttribute : FactAttribute
    {
        public DisposablePostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AEROLINK_MIGRATIONS_CONNECTION")))
                Skip = "Issue #727 PostgreSQL qualification skipped: set AEROLINK_MIGRATIONS_CONNECTION to its dedicated disposable database.";
        }
    }

    private sealed record CaseLifecycleFixture(Guid ProjectId, Guid HistoricalCaseRevisionId,
        Guid CaseRevisionId, Guid AlternateProcedureRevisionId, Guid HistoricalLinkId,
        Guid CarriedLinkId, Guid LifecycleId);
}
