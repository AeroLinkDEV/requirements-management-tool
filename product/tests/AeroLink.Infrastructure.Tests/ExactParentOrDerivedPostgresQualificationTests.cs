using System.Net;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace AeroLink.Infrastructure.Tests;

/// <summary>
/// Disposable PostgreSQL qualification for #738. This deliberately migrates the exact #724 predecessor,
/// inserts evidence through the predecessor schema, and then proves that the #738 backfill is evidence-only
/// and idempotent. It is skipped unless the caller supplies the dedicated disposable connection.
/// </summary>
[CollectionDefinition("Issue738Postgres", DisableParallelization = true)]
public sealed class Issue738PostgresCollection;

[Collection("Issue738Postgres")]
public sealed class ExactParentOrDerivedPostgresQualificationTests
{
    private const string Predecessor = "20260823061857_AddDormantSoftwareProcedures";
    private const string Migration = "20260823100915_GeneralizeExactParentOrDerived";
    private const string DatabaseName = "aerolink_738_qualify";

    [DisposablePostgresFact]
    public async Task Exact_predecessor_upgrade_backfills_only_honest_parent_evidence_and_is_idempotent()
    {
        var connection = QualificationConnectionOrThrow();
        await using var db = await MigrateToPredecessorAsync(connection);
        var fixture = await SeedPredecessorFixtureAsync(db);
        var reviewEvidenceBefore = await db.ReviewCycles.AsNoTracking()
            .Where(x => x.Id == fixture.ReviewCycleId)
            .Select(x => new { x.Id, x.SnapshotHash, x.State, x.Sequence })
            .SingleAsync();
        var signatureEvidenceBefore = await db.Set<AeroLink.Domain.Identity.ElectronicSignature>().AsNoTracking()
            .Where(x => x.Id == fixture.SignatureId)
            .Select(x => new { x.Id, x.ContentHash, x.ArtifactId, x.ArtifactRevision, x.Meaning })
            .SingleAsync();

        await db.Database.GetService<IMigrator>().MigrateAsync();
        db.ChangeTracker.Clear();

        var reviewEvidenceAfter = await db.ReviewCycles.AsNoTracking()
            .Where(x => x.Id == fixture.ReviewCycleId)
            .Select(x => new { x.Id, x.SnapshotHash, x.State, x.Sequence })
            .SingleAsync();
        var signatureEvidenceAfter = await db.Set<AeroLink.Domain.Identity.ElectronicSignature>().AsNoTracking()
            .Where(x => x.Id == fixture.SignatureId)
            .Select(x => new { x.Id, x.ContentHash, x.ArtifactId, x.ArtifactRevision, x.Meaning })
            .SingleAsync();
        Assert.Equal(reviewEvidenceBefore, reviewEvidenceAfter);
        Assert.Equal(signatureEvidenceBefore, signatureEvidenceAfter);

        var allocatedRequirement = await db.RequirementRevisions.AsNoTracking()
            .SingleAsync(x => x.Id == fixture.AllocatedRequirementRevisionId);
        var derivedRequirement = await db.RequirementRevisions.AsNoTracking()
            .SingleAsync(x => x.Id == fixture.DerivedRequirementRevisionId);
        var blankDerivedRequirement = await db.RequirementRevisions.AsNoTracking()
            .SingleAsync(x => x.Id == fixture.BlankDerivedRequirementRevisionId);
        var unknownMarkerRequirement = await db.RequirementRevisions.AsNoTracking()
            .SingleAsync(x => x.Id == fixture.UnknownMarkerRequirementRevisionId);

        Assert.Equal("Allocated", allocatedRequirement.ParentKind.ToString());
        Assert.Equal($"[\"{fixture.ParentRequirementRevisionId:D}\"]", allocatedRequirement.ParentRevisionIdsJson);
        Assert.Equal("Derived", derivedRequirement.ParentKind.ToString());
        Assert.Equal("actual authored rationale", derivedRequirement.DerivedRationale);
        Assert.Equal("Unspecified", blankDerivedRequirement.ParentKind.ToString());
        Assert.Equal(string.Empty, blankDerivedRequirement.DerivedRationale);
        Assert.Equal("Unspecified", unknownMarkerRequirement.ParentKind.ToString());

        // Suspect carried links are lifecycle evidence, not the immutable authored parent selection.
        Assert.Equal(3, await db.RequirementTraces.CountAsync(x =>
            x.SourceRevisionId == fixture.AllocatedRequirementRevisionId));
        Assert.Single(await db.RequirementTraces.AsNoTracking().Where(x =>
            x.SourceRevisionId == fixture.AllocatedRequirementRevisionId
            && x.ExactLinkSuspectLifecycleId == null).ToListAsync());
        Assert.Single(await (from link in db.RequirementTraces.AsNoTracking()
                             join lifecycle in db.ExactLinkSuspectLifecycles.AsNoTracking()
                                 on link.ExactLinkSuspectLifecycleId equals lifecycle.Id
                             where link.SourceRevisionId == fixture.AllocatedRequirementRevisionId
                                 && lifecycle.State == AeroLink.Domain.Traceability.ExactLinkLifecycleState.Closed
                             select link).ToListAsync());

        var systemProcedureRevision = await db.TestProcedureRevisions.AsNoTracking()
            .SingleAsync(x => x.Id == fixture.SystemProcedureRevisionId);
        var caseRevision = await db.TestProcedureRevisions.AsNoTracking()
            .SingleAsync(x => x.Id == fixture.CaseRevisionId);
        Assert.Equal("Allocated", systemProcedureRevision.ParentKind.ToString());
        Assert.Equal("Allocated", caseRevision.ParentKind.ToString());

        var introduce = await db.Set<AeroLink.Domain.Verification.TestProcedureChange>().AsNoTracking()
            .SingleAsync(x => x.Id == fixture.IntroduceChangeId);
        var materializedModify = await db.Set<AeroLink.Domain.Verification.TestProcedureChange>().AsNoTracking()
            .SingleAsync(x => x.Id == fixture.MaterializedModifyChangeId);
        var pendingModify = await db.Set<AeroLink.Domain.Verification.TestProcedureChange>().AsNoTracking()
            .SingleAsync(x => x.Id == fixture.PendingModifyChangeId);
        Assert.Equal("Allocated", introduce.ParentKind.ToString());
        Assert.Equal("Allocated", materializedModify.ParentKind.ToString());
        Assert.Equal("Unspecified", pendingModify.ParentKind.ToString());
        Assert.Equal($"[\"{fixture.AllocatedRequirementRevisionId:D}\"]", materializedModify.ParentRevisionIdsJson);
        Assert.DoesNotContain(fixture.DerivedRequirementRevisionId.ToString("D"), materializedModify.ParentRevisionIdsJson,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, await db.TestCoverage.CountAsync());

        var before = await SnapshotAsync(db);
        await db.Database.GetService<IMigrator>().MigrateAsync();
        Assert.Equal(before, await SnapshotAsync(db));

        // The predecessor's signed review/hash rows are not rewritten by this additive migration.
        Assert.Equal(3, await db.TestChangeReviews.CountAsync());
        Assert.Equal(3, await db.Set<AeroLink.Domain.Verification.TestProcedureChange>().CountAsync());
    }

    [DisposablePostgresFact]
    public async Task Clean_current_install_applies_issue_738_without_fabricated_artifacts()
    {
        var connection = QualificationConnectionOrThrow();
        await using var db = new AeroLinkDbContext(Options(connection));
        await db.Database.EnsureDeletedAsync();
        await db.Database.GetService<IMigrator>().MigrateAsync();

        Assert.Equal(0, await db.RequirementRevisions.CountAsync());
        Assert.Equal(0, await db.TestProcedureRevisions.CountAsync());
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>(
            $"SELECT COUNT(*)::int AS \"Value\" FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = '{Migration}'")
            .SingleAsync());
    }

    [Theory]
    [InlineData("Host=127.0.0.1;Port=54329;Database=aerolink_738_qualify")]
    [InlineData("Host=127.0.0.1;Port=55438;Database=other_database")]
    [InlineData("Host=10.0.0.1;Port=55438;Database=aerolink_738_qualify")]
    public void Qualification_connection_rejects_protected_or_wrong_scope(string connection)
    {
        Assert.Throws<InvalidOperationException>(() => ValidateQualificationConnection(connection));
    }

    private static async Task<AeroLinkDbContext> MigrateToPredecessorAsync(string connection)
    {
        var db = new AeroLinkDbContext(Options(connection));
        await db.Database.EnsureDeletedAsync();
        await db.Database.GetService<IMigrator>().MigrateAsync(Predecessor);
        return db;
    }

    private static async Task<Fixture> SeedPredecessorFixtureAsync(AeroLinkDbContext db)
    {
        var now = "2026-08-23 12:00:00+00";
        var programId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var releaseId = Guid.NewGuid();
        var baselineId = Guid.NewGuid();
        var schemaId = Guid.NewGuid();
        var scrId = Guid.NewGuid();
        var parentRequirementId = Guid.NewGuid();
        var allocatedRequirementId = Guid.NewGuid();
        var derivedRequirementId = Guid.NewGuid();
        var blankDerivedRequirementId = Guid.NewGuid();
        var unknownMarkerRequirementId = Guid.NewGuid();
        var parentRevisionId = Guid.NewGuid();
        var allocatedRevisionId = Guid.NewGuid();
        var derivedRevisionId = Guid.NewGuid();
        var blankDerivedRevisionId = Guid.NewGuid();
        var unknownMarkerRevisionId = Guid.NewGuid();
        var authoredTraceId = Guid.NewGuid();
        var suspectTraceId = Guid.NewGuid();
        var closedTraceId = Guid.NewGuid();
        var lifecycleId = Guid.NewGuid();
        var closedLifecycleId = Guid.NewGuid();
        var systemProcedureId = Guid.NewGuid();
        var systemProcedureRevisionId = Guid.NewGuid();
        var caseProcedureId = Guid.NewGuid();
        var caseRevisionId = Guid.NewGuid();
        var introduceReviewId = Guid.NewGuid();
        var materializedModifyReviewId = Guid.NewGuid();
        var pendingModifyReviewId = Guid.NewGuid();
        var introduceChangeId = Guid.NewGuid();
        var materializedModifyChangeId = Guid.NewGuid();
        var pendingModifyChangeId = Guid.NewGuid();
        var reviewCycleId = Guid.NewGuid();
        var signatureId = Guid.NewGuid();
        // This is the fixed pre-#738 TestChangeReview snapshot hash. The review cycle and signature
        // below deliberately point at the same package artifact, rather than at its originating SCR.
        const string historicalSnapshotHash = "8bfe6b3d71c9be303f81829492cacd23d8ab8aed61d45d8a996693ff690eb7bd";

        await Sql(db, $"INSERT INTO \"programs\" (\"Id\",\"Name\",\"Code\") VALUES ('{programId}','Issue 738 qualification','Q738');");
        await Sql(db, $"INSERT INTO \"projects\" (\"Id\",\"ProgramId\",\"Name\",\"SoftwareProduct\") VALUES ('{projectId}','{programId}','Issue 738 project','Qualification product');");
        await Sql(db, $"INSERT INTO \"software_releases\" (\"Id\",\"ProjectId\",\"Version\",\"IsReleased\") VALUES ('{releaseId}','{projectId}','1.0',TRUE);");
        await Sql(db, $"INSERT INTO \"candidate_baselines\" (\"Id\",\"BaseNumber\",\"Revision\",\"ProjectId\",\"ReleaseId\",\"Name\",\"CreatedAt\",\"State\",\"UpdatedAt\") VALUES ('{baselineId}','BL-738-001',0,'{projectId}','{releaseId}','Issue 738 predecessor fixture','{now}','Frozen','{now}');");
        await Sql(db, $"INSERT INTO \"artifact_schema_definitions\" (\"Id\",\"ProjectId\",\"Key\",\"Name\",\"AppliesTo\",\"Description\",\"Version\",\"IsActive\",\"CreatedBy\",\"CreatedAt\") VALUES ('{schemaId}','{projectId}','Q738','Issue 738 schema','Requirement','qualification schema',1,TRUE,'qualification','{now}');");
        await Sql(db, $"INSERT INTO \"system_change_requests\" (\"Id\",\"BaseNumber\",\"Revision\",\"ProjectId\",\"TargetReleaseId\",\"Title\",\"Problem\",\"Analysis\",\"Solution\",\"AuthorId\",\"State\",\"CreatedAt\",\"UpdatedAt\") VALUES ('{scrId}','SCR-738',0,'{projectId}','{releaseId}','Issue 738 fixture','Problem','Analysis','Solution','qualification','Approved','{now}','{now}');");

        await Sql(db, $"INSERT INTO \"requirements\" (\"Id\",\"ProjectId\",\"BaseNumber\",\"Level\",\"CreatedAt\") VALUES ('{parentRequirementId}','{projectId}','SYSR-738','System','{now}'),('{allocatedRequirementId}','{projectId}','HLRR-738-A','HighLevel','{now}'),('{derivedRequirementId}','{projectId}','HLRR-738-D','HighLevel','{now}'),('{blankDerivedRequirementId}','{projectId}','HLRR-738-B','HighLevel','{now}'),('{unknownMarkerRequirementId}','{projectId}','HLRR-738-U','HighLevel','{now}');");
        await Sql(db, $"INSERT INTO \"requirement_revisions\" (\"Id\",\"ArtifactId\",\"Revision\",\"Statement\",\"Rationale\",\"VerificationMethod\",\"State\",\"SourceChangeRequestId\",\"EffectiveBaselineId\",\"CreatedAt\",\"OriginKind\") VALUES ('{parentRevisionId}','{parentRequirementId}',0,'System parent','', 'manual','Active','{scrId}','{baselineId}','{now}','ChangeRequest'),('{allocatedRevisionId}','{allocatedRequirementId}',0,'Allocated requirement','', 'manual','Active','{scrId}','{baselineId}','{now}','ChangeRequest'),('{derivedRevisionId}','{derivedRequirementId}',0,'Derived requirement','actual authored rationale', 'manual','Active','{scrId}','{baselineId}','{now}','ChangeRequest'),('{blankDerivedRevisionId}','{blankDerivedRequirementId}',0,'Blank derived marker','', 'manual','Active','{scrId}','{baselineId}','{now}','ChangeRequest'),('{unknownMarkerRevisionId}','{unknownMarkerRequirementId}',0,'Unknown marker','not a marker', 'manual','Active','{scrId}','{baselineId}','{now}','ChangeRequest');");
        await Sql(db, $"INSERT INTO \"requirement_revision_profiles\" (\"Id\",\"RevisionId\",\"SchemaId\",\"RichText\",\"AttributesJson\",\"TagsJson\",\"UpdatedBy\",\"UpdatedAt\") VALUES ('{Guid.NewGuid()}','{derivedRevisionId}','{schemaId}','{{}}','{{\"derived\":true}}','[]','qualification','{now}'),('{Guid.NewGuid()}','{blankDerivedRevisionId}','{schemaId}','{{}}','{{\"derived\":true}}','[]','qualification','{now}'),('{Guid.NewGuid()}','{unknownMarkerRevisionId}','{schemaId}','{{}}','{{\"marker\":true}}','[]','qualification','{now}');");
        await Sql(db, $"INSERT INTO \"exact_link_suspect_lifecycles\" (\"Id\",\"ProjectId\",\"LinkKind\",\"LinkId\",\"State\",\"CauseKind\",\"CauseRequirementRevisionId\",\"RaisedBy\",\"RaisedAt\",\"RaisedRationale\",\"UpdatedAt\",\"Version\") VALUES ('{lifecycleId}','{projectId}','RequirementTrace','{suspectTraceId}','Suspect','InternalRequirementRevision','{allocatedRevisionId}','qualification','{now}','carried projection','{now}',1),('{closedLifecycleId}','{projectId}','RequirementTrace','{closedTraceId}','Closed','InternalRequirementRevision','{allocatedRevisionId}','qualification','{now}','resolved carried projection','{now}',2);");
        await Sql(db, $"UPDATE \"exact_link_suspect_lifecycles\" SET \"Outcome\" = 'ExistingDownstreamRevisionRemainsValid', \"ResolvedBy\" = 'qualification', \"ResolvedAt\" = '{now}', \"ResolutionRationale\" = 'The existing downstream revision remains valid.' WHERE \"Id\" = '{closedLifecycleId}';");
        // The predecessor uniqueness constraint permits one exact trace for a source/target/type pair.
        // Keep the open suspect and the resolved carried projection as distinct lifecycle evidence rows;
        // they are deliberately excluded from immutable authored-parent backfill by the migration.
        await Sql(db, $"INSERT INTO \"requirement_trace_links\" (\"Id\",\"ProjectId\",\"SourceRevisionId\",\"TargetRevisionId\",\"Type\",\"Rationale\",\"CreatedAt\") VALUES ('{authoredTraceId}','{projectId}','{allocatedRevisionId}','{parentRevisionId}','AllocatedFrom','authored exact parent','{now}'),('{suspectTraceId}','{projectId}','{allocatedRevisionId}','{derivedRevisionId}','AllocatedFrom','carried suspect','{now}'),('{closedTraceId}','{projectId}','{allocatedRevisionId}','{blankDerivedRevisionId}','AllocatedFrom','resolved carried projection','{now}');");
        await Sql(db, $"UPDATE \"requirement_trace_links\" SET \"ExactLinkSuspectLifecycleId\" = '{lifecycleId}' WHERE \"Id\" = '{suspectTraceId}';");
        await Sql(db, $"UPDATE \"requirement_trace_links\" SET \"ExactLinkSuspectLifecycleId\" = '{closedLifecycleId}' WHERE \"Id\" = '{closedTraceId}';");

        await Sql(db, $"INSERT INTO \"test_procedures\" (\"Id\",\"ProjectId\",\"BaseNumber\",\"Title\",\"OwnerId\",\"CreatedAt\",\"Level\",\"ArtifactDiscipline\",\"ArtifactKind\") VALUES ('{systemProcedureId}','{projectId}','SYSTP-000738','System procedure','qualification','{now}','System','System','Procedure'),('{caseProcedureId}','{projectId}','HLRTC-000738','Case procedure','qualification','{now}','HighLevel','HighLevelSoftware','Case');");
        await Sql(db, $"INSERT INTO \"test_change_reviews\" (\"Id\",\"ProjectId\",\"ReleaseId\",\"ChangeRequestId\",\"Discipline\",\"SourceChangeRequestNumber\",\"BaseNumber\",\"Revision\",\"State\",\"ApprovalRationale\",\"CreatedAt\",\"UpdatedAt\",\"Version\") VALUES ('{introduceReviewId}','{projectId}','{releaseId}','{scrId}','System','SCR-738.00','SYSTPCR-000738',0,'Approved','legacy approval','{now}','{now}',1),('{materializedModifyReviewId}','{projectId}','{releaseId}','{scrId}','HighLevelSoftware','SCR-738.00','HLRTCCR-000738',0,'Approved','legacy approval','{now}','{now}',1),('{pendingModifyReviewId}','{projectId}','{releaseId}','{scrId}','LowLevelSoftware','SCR-738.00','LLRTCCR-000738',0,'Open','','{now}','{now}',1);");
        await Sql(db, $"INSERT INTO \"review_cycles\" (\"Id\",\"ChangeRequestId\",\"Sequence\",\"SnapshotHash\",\"State\",\"StartedAt\",\"CompletedAt\",\"TestChangeReviewId\") VALUES ('{reviewCycleId}',NULL,1,'{historicalSnapshotHash}','Approved','{now}','{now}','{introduceReviewId}');");
        await Sql(db, $"INSERT INTO \"electronic_signatures\" (\"Id\",\"UserId\",\"UserName\",\"DisplayName\",\"ProgramId\",\"ArtifactType\",\"ArtifactId\",\"ArtifactRevision\",\"Action\",\"Meaning\",\"ContentHash\",\"IpAddress\",\"SignedAt\",\"Authority\",\"AuthoritySource\",\"Rationale\",\"ReviewCycle\") VALUES ('{signatureId}','{Guid.NewGuid()}','qualification','Qualification Reviewer','{programId}','TestChangeRequest','{introduceReviewId}','SYSTPCR-000738.00','Approve','historical signed review','{historicalSnapshotHash}','127.0.0.1','{now}','Qualification','Fixture','predecessor evidence',1);");
        await Sql(db, $"INSERT INTO \"test_procedure_changes\" (\"Id\",\"TestChangeReviewId\",\"BaseNumber\",\"Revision\",\"Level\",\"Kind\",\"Objective\",\"Preconditions\",\"Steps\",\"ExpectedResult\",\"Rationale\",\"DrivingRequirementRevisionIdsJson\") VALUES ('{introduceChangeId}','{introduceReviewId}','SYSTP-000738',0,'System','Introduce','objective','preconditions','steps','expected','rationale','[\"{allocatedRevisionId:D}\"]'),('{materializedModifyChangeId}','{materializedModifyReviewId}','HLRTC-000738',1,'HighLevel','Modify','objective','preconditions','steps','expected','rationale','[\"{allocatedRevisionId:D}\"]'),('{pendingModifyChangeId}','{pendingModifyReviewId}','LLRTC-000738',1,'LowLevel','Modify','objective','preconditions','steps','expected','rationale','[\"{allocatedRevisionId:D}\"]');");
        await Sql(db, $"INSERT INTO \"test_procedure_revisions\" (\"Id\",\"ProcedureId\",\"Revision\",\"Objective\",\"Preconditions\",\"Steps\",\"ExpectedResult\",\"State\",\"AuthorId\",\"CreatedAt\",\"SourceTestChangeRequestId\",\"EffectiveBaselineId\") VALUES ('{systemProcedureRevisionId}','{systemProcedureId}',0,'objective','preconditions','steps','expected','Approved','qualification','{now}','{introduceReviewId}','{baselineId}'),('{caseRevisionId}','{caseProcedureId}',1,'objective','preconditions','steps','expected','Approved','qualification','{now}','{materializedModifyReviewId}','{baselineId}');");
        await Sql(db, $"INSERT INTO \"test_requirement_coverage\" (\"Id\",\"ProcedureRevisionId\",\"RequirementRevisionId\",\"IsSuspect\",\"SuspectReason\",\"SuspectSince\") VALUES ('{Guid.NewGuid()}','{systemProcedureRevisionId}','{allocatedRevisionId}',FALSE,'','{now}'),('{Guid.NewGuid()}','{caseRevisionId}','{allocatedRevisionId}',FALSE,'','{now}'),('{Guid.NewGuid()}','{caseRevisionId}','{derivedRevisionId}',TRUE,'carried lifecycle evidence','{now}');");

        return new Fixture(parentRevisionId, allocatedRevisionId, derivedRevisionId, blankDerivedRevisionId,
            unknownMarkerRevisionId, systemProcedureRevisionId, caseRevisionId, introduceChangeId,
            materializedModifyChangeId, pendingModifyChangeId, reviewCycleId, signatureId);
    }

    private static Task Sql(AeroLinkDbContext db, string sql) => db.Database.ExecuteSqlRawAsync(
        sql.Replace("{", "{{", StringComparison.Ordinal)
            .Replace("}", "}}", StringComparison.Ordinal));

    private static async Task<(int Requirements, int RequirementLinks, int ProcedureRevisions, int Changes)> SnapshotAsync(AeroLinkDbContext db) =>
        (await db.RequirementRevisions.CountAsync(), await db.RequirementTraces.CountAsync(),
            await db.TestProcedureRevisions.CountAsync(),
            await db.Set<AeroLink.Domain.Verification.TestProcedureChange>().CountAsync());

    private static DbContextOptions<AeroLinkDbContext> Options(string connection) =>
        new DbContextOptionsBuilder<AeroLinkDbContext>().UseNpgsql(connection).Options;

    private static string QualificationConnectionOrThrow() => ValidateQualificationConnection(
        Environment.GetEnvironmentVariable("AEROLINK_MIGRATIONS_CONNECTION"));

    private static string ValidateQualificationConnection(string? connection)
    {
        if (string.IsNullOrWhiteSpace(connection))
            throw new InvalidOperationException("Issue #738 PostgreSQL qualification requires AEROLINK_MIGRATIONS_CONNECTION.");
        var builder = new NpgsqlConnectionStringBuilder(connection);
        var host = (builder.Host ?? string.Empty).Trim().Trim('[', ']');
        var loopback = string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));
        if (!loopback)
            throw new InvalidOperationException("Issue #738 PostgreSQL qualification requires a loopback host.");
        if (builder.Port != 55438)
            throw new InvalidOperationException("Issue #738 qualification requires disposable PostgreSQL port 55438 and refuses 54329.");
        if (!string.Equals(builder.Database, DatabaseName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Issue #738 qualification requires dedicated database {DatabaseName}.");
        return connection;
    }

    private sealed class DisposablePostgresFactAttribute : FactAttribute
    {
        public DisposablePostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AEROLINK_MIGRATIONS_CONNECTION")))
                Skip = "Issue #738 PostgreSQL qualification skipped: set AEROLINK_MIGRATIONS_CONNECTION to the dedicated disposable database.";
        }
    }

    private sealed record Fixture(
        Guid ParentRequirementRevisionId,
        Guid AllocatedRequirementRevisionId,
        Guid DerivedRequirementRevisionId,
        Guid BlankDerivedRequirementRevisionId,
        Guid UnknownMarkerRequirementRevisionId,
        Guid SystemProcedureRevisionId,
        Guid CaseRevisionId,
        Guid IntroduceChangeId,
        Guid MaterializedModifyChangeId,
        Guid PendingModifyChangeId,
        Guid ReviewCycleId,
        Guid SignatureId);
}
