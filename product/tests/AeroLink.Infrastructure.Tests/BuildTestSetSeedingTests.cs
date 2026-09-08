using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Data.Sqlite;

namespace AeroLink.Infrastructure.Tests;

/// <summary>
/// A build's test sets are created on first use, and carry forward what the product already knew.
///
/// "Must be run before release" used to be a checkbox on individual verification decisions. Every procedure
/// that checkbox pointed at is exactly a procedure the build has to run, so replacing the checkbox without
/// carrying those forward would silently discard every decision anybody had already recorded with it.
/// </summary>
public sealed class BuildTestSetSeedingTests
{
    private sealed record Fixture(AeroLinkDbContext Db, Guid ProjectId, Guid ReleaseId, Guid ProcedureRevisionId,
        Guid ChangeRequestId, Guid ReviewId);

    private static async Task<Fixture> DatabaseAsync(bool flagPreReleaseEvidence,
        params IInterceptor[] interceptors)
    {
        var optionsBuilder = new DbContextOptionsBuilder<AeroLinkDbContext>()
            .UseSqlite("Data Source=:memory:");
        if (interceptors.Length > 0) optionsBuilder.AddInterceptors(interceptors);
        var options = optionsBuilder.Options;
        var db = new AeroLinkDbContext(options);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();
        var now = DateTimeOffset.UtcNow;

        var program = new ProgramRecord("Seed Program", "SED");
        var project = new ProjectRecord(program.Id, "Flight Software", "Seed Software");
        var release = new SoftwareRelease(project.Id, "1.6", false);
        var scr = new SystemChangeRequest("SRCR-00800", 0, project.Id, release.Id, "Oceanic", "P", "A", "S", "author", now);
        var procedure = new TestProcedure(project.Id, "SYSTP-000800", "Oceanic sequencing", "test.engineer", now,
            TestProcedureLevel.System);
        var revision = new TestProcedureRevision(procedure.Id, 0, "Objective", "Pre", "Steps", "Expected",
            TestProcedureState.Approved, "test.engineer", now);
        var review = new TestChangeReview(project.Id, release.Id, scr.Id, TestChangeReviewDiscipline.System,
            "SRCR-00800", now, "SYSTPCR-000800");
        db.AddRange(program, project, release, scr, procedure, revision, review);
        await db.SaveChangesAsync();

        var item = VerificationImpactItem.ForIntroducedRequirement(project.Id, release.Id, scr.Id, review.Id,
            Guid.NewGuid(), "SYSR-000800", "Test", now);
        item.Resolve("verification.engineer", VerificationImpactOutcome.ProcedureCoverageConfirmed,
            "Covered by the oceanic sequencing procedure.", now, procedure.Id, revision.Id,
            TestProcedureChangeAction.CreateNew, flagPreReleaseEvidence);
        db.Add(item);
        await db.SaveChangesAsync();
        return new(db, project.Id, release.Id, revision.Id, scr.Id, review.Id);
    }

    [Fact]
    public async Task A_build_gets_one_set_for_each_discipline()
    {
        var fixture = await DatabaseAsync(flagPreReleaseEvidence: false);
        await using var db = fixture.Db;

        var sets = await new BuildTestSetService(db).EnsureForReleaseAsync(fixture.ProjectId, fixture.ReleaseId);

        Assert.Equal(3, sets.Count);
        Assert.Equal(
            [TestChangeReviewDiscipline.System, TestChangeReviewDiscipline.HighLevelSoftware, TestChangeReviewDiscipline.LowLevelSoftware],
            sets.Select(x => x.Discipline).OrderBy(x => x).ToList());
    }

    [Fact]
    public async Task Procedures_that_needed_evidence_before_release_become_the_first_entries()
    {
        var fixture = await DatabaseAsync(flagPreReleaseEvidence: true);
        await using var db = fixture.Db;

        var sets = await new BuildTestSetService(db).EnsureForReleaseAsync(fixture.ProjectId, fixture.ReleaseId);

        var system = sets.Single(x => x.Discipline == TestChangeReviewDiscipline.System);
        var entry = system.Entries.Single();
        Assert.Equal(fixture.ProcedureRevisionId, entry.ProcedureRevisionId);
        Assert.Equal(TestSelectionReason.ChangedRequirement, entry.Reason);
        // Says where it came from, so somebody reading the set later can tell a carried-forward decision from
        // one somebody made in the new surface.
        Assert.Contains("SYSR-000800", entry.Note);
        // It lands in the discipline of the test change request that raised it, not in all three.
        Assert.All(sets.Where(x => x.Discipline != TestChangeReviewDiscipline.System), x => Assert.Empty(x.Entries));
    }

    [Fact]
    public async Task A_changed_requirement_decision_is_mandatory_even_when_the_client_asks_for_false()
    {
        var fixture = await DatabaseAsync(flagPreReleaseEvidence: false);
        await using var db = fixture.Db;

        var sets = await new BuildTestSetService(db).EnsureForReleaseAsync(fixture.ProjectId, fixture.ReleaseId);

        Assert.Single(sets.Single(x => x.Discipline == TestChangeReviewDiscipline.System).Entries);
        Assert.All(sets.Where(x => x.Discipline != TestChangeReviewDiscipline.System), x => Assert.Empty(x.Entries));
    }

    [Fact]
    public async Task Asking_twice_neither_duplicates_the_sets_nor_reseeds_them()
    {
        var fixture = await DatabaseAsync(flagPreReleaseEvidence: true);
        await using var db = fixture.Db;
        var service = new BuildTestSetService(db);

        await service.EnsureForReleaseAsync(fixture.ProjectId, fixture.ReleaseId);
        var system = (await service.EnsureForReleaseAsync(fixture.ProjectId, fixture.ReleaseId))
            .Single(x => x.Discipline == TestChangeReviewDiscipline.System);

        Assert.Equal(3, await db.BuildTestSets.CountAsync());
        // A second pass must not re-add what a lead may deliberately have removed.
        Assert.Single(system.Entries);
    }

    [Fact]
    public async Task A_configured_subset_policy_hides_retained_sets_for_removed_verification_disciplines()
    {
        var fixture = await DatabaseAsync(flagPreReleaseEvidence: false);
        await using var db = fixture.Db;
        var now = DateTimeOffset.UtcNow;
        db.AddRange(
            new BuildTestSet(fixture.ProjectId, fixture.ReleaseId, TestChangeReviewDiscipline.System, now),
            new BuildTestSet(fixture.ProjectId, fixture.ReleaseId, TestChangeReviewDiscipline.HighLevelSoftware, now),
            new BuildTestSet(fixture.ProjectId, fixture.ReleaseId, TestChangeReviewDiscipline.LowLevelSoftware, now));
        await db.SaveChangesAsync();

        var configuration = ProjectLadderConfiguration.CreateDraft(fixture.ProjectId, now);
        configuration.Steps.Add(new ProjectLadderStep(configuration.Id, fixture.ProjectId, RequirementLevel.System, 1,
            LegacyLadderPolicy.Instance.Definition(RequirementLevel.System).Capabilities, now));
        var policy = new ResolvedProjectLadderPolicy(ProjectLadderResolver.Resolve(configuration));
        var service = new BuildTestSetService(db,
            policyResolver: new FixedProjectLadderPolicyResolver(policy));

        var visible = await service.EnsureForReleaseAsync(fixture.ProjectId, fixture.ReleaseId);

        Assert.Single(visible);
        Assert.Equal(TestChangeReviewDiscipline.System, visible[0].Discipline);
        Assert.Equal(3, await db.BuildTestSets.CountAsync(x => x.ReleaseId == fixture.ReleaseId));
    }

    [Fact]
    public async Task A_procedure_required_by_a_changed_requirement_cannot_be_removed()
    {
        var fixture = await DatabaseAsync(flagPreReleaseEvidence: true);
        await using var db = fixture.Db;
        var service = new BuildTestSetService(db);
        var sets = await service.EnsureForReleaseAsync(fixture.ProjectId, fixture.ReleaseId);

        var system = sets.Single(x => x.Discipline == TestChangeReviewDiscipline.System);
        var error = Assert.Throws<DomainException>(() =>
            system.Exclude(fixture.ProcedureRevisionId, DateTimeOffset.UtcNow));
        Assert.Contains("mandatory before release", error.Message);
        Assert.Single(system.Entries);
    }

    [Fact]
    public async Task A_non_race_write_failure_propagates_and_preserves_unrelated_tracked_work()
    {
        var failure = new BuildSetSaveFailureInterceptor(BuildSetSaveFailureKind.NonRaceWrite);
        var fixture = await DatabaseAsync(flagPreReleaseEvidence: false, failure);
        await using var db = fixture.Db;
        var unrelated = new ProgramRecord("Unrelated pending program", "UNRELATED_PENDING");
        db.Programs.Add(unrelated);

        var error = await Assert.ThrowsAsync<DbUpdateException>(() =>
            new BuildTestSetService(db).EnsureForReleaseAsync(fixture.ProjectId, fixture.ReleaseId));

        Assert.Contains("non-race", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(EntityState.Added, db.Entry(unrelated).State);
        Assert.DoesNotContain(db.ChangeTracker.Entries<BuildTestSet>(), x => x.State == EntityState.Added);
        Assert.Empty(await db.BuildTestSets.AsNoTracking().ToListAsync());

        // The caller can still save its own pending work after handling the failed initializer.
        failure.Enabled = false;
        await db.SaveChangesAsync();
        Assert.True(await db.Programs.AsNoTracking().AnyAsync(x => x.Id == unrelated.Id));
    }

    [Fact]
    public async Task Cancellation_propagates_and_does_not_leave_initializer_candidates_armed()
    {
        var failure = new BuildSetSaveFailureInterceptor(BuildSetSaveFailureKind.Cancellation);
        var fixture = await DatabaseAsync(flagPreReleaseEvidence: false, failure);
        await using var db = fixture.Db;

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new BuildTestSetService(db).EnsureForReleaseAsync(fixture.ProjectId, fixture.ReleaseId));

        Assert.DoesNotContain(db.ChangeTracker.Entries<BuildTestSet>(), x => x.State == EntityState.Added);
        Assert.Empty(await db.BuildTestSets.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task An_unrelated_unique_constraint_failure_is_not_treated_as_a_set_creation_race()
    {
        var fixture = await DatabaseAsync(flagPreReleaseEvidence: false);
        await using var db = fixture.Db;
        var now = DateTimeOffset.UtcNow;
        var existing = new BuildTestSet(fixture.ProjectId, fixture.ReleaseId,
            TestChangeReviewDiscipline.System, now);
        existing.Include("test.lead", fixture.ProcedureRevisionId, TestSelectionReason.Chosen, "existing", now);
        db.BuildTestSets.Add(existing);
        await db.SaveChangesAsync();

        // A caller-owned duplicate entry is deliberately tracked alongside the initializer's missing sets.
        var duplicate = new BuildTestSetEntry(existing.Id, fixture.ProcedureRevisionId,
            TestSelectionReason.Chosen, "duplicate", "caller", now);
        db.BuildTestSetEntries.Add(duplicate);

        var error = await Assert.ThrowsAsync<DbUpdateException>(() =>
            new BuildTestSetService(db).EnsureForReleaseAsync(fixture.ProjectId, fixture.ReleaseId));

        Assert.Contains("build_test_set_entries", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(EntityState.Added, db.Entry(duplicate).State);
        Assert.DoesNotContain(db.ChangeTracker.Entries<BuildTestSet>(), x => x.State == EntityState.Added);
        Assert.Equal(1, await db.BuildTestSets.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task A_matching_unique_failure_is_success_only_when_all_configured_sets_are_persisted()
    {
        var partialRace = new BuildSetRaceInterceptor(completeWinner: false);
        var fixture = await DatabaseAsync(flagPreReleaseEvidence: false, partialRace);
        await using var db = fixture.Db;

        var error = await Assert.ThrowsAsync<DbUpdateException>(() =>
            new BuildTestSetService(db).EnsureForReleaseAsync(fixture.ProjectId, fixture.ReleaseId));

        Assert.Contains("expected set race", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, await db.BuildTestSets.AsNoTracking().CountAsync());
        Assert.DoesNotContain(db.ChangeTracker.Entries<BuildTestSet>(), x => x.State == EntityState.Added);
    }

    [Fact]
    public async Task A_matching_unique_failure_returns_the_complete_winner_without_clearing_unrelated_work()
    {
        var completeRace = new BuildSetRaceInterceptor(completeWinner: true);
        var fixture = await DatabaseAsync(flagPreReleaseEvidence: false, completeRace);
        await using var db = fixture.Db;
        var unrelated = new ProgramRecord("Winner race pending program", "WINNER_RACE_PENDING");
        db.Programs.Add(unrelated);

        var sets = await new BuildTestSetService(db).EnsureForReleaseAsync(fixture.ProjectId, fixture.ReleaseId);

        Assert.Equal(3, sets.Count);
        Assert.Equal(3, await db.BuildTestSets.AsNoTracking().CountAsync());
        Assert.Equal(EntityState.Added, db.Entry(unrelated).State);
        Assert.DoesNotContain(db.ChangeTracker.Entries<BuildTestSet>(), x => x.State == EntityState.Added);
    }

    [Fact]
    public async Task An_invalid_carried_procedure_reference_propagates_instead_of_returning_empty_sets()
    {
        var fixture = await DatabaseAsync(flagPreReleaseEvidence: false);
        await using var db = fixture.Db;
        var invalid = VerificationImpactItem.ForIntroducedRequirement(fixture.ProjectId, fixture.ReleaseId,
            fixture.ChangeRequestId, fixture.ReviewId, Guid.NewGuid(), "SYSR-INVALID", "Test",
            DateTimeOffset.UtcNow);
        invalid.Resolve("verification.engineer", VerificationImpactOutcome.ProcedureCoverageConfirmed,
            "Injected invalid reference for fail-closed qualification.", DateTimeOffset.UtcNow,
            Guid.NewGuid(), Guid.NewGuid(), TestProcedureChangeAction.CreateNew, preReleaseEvidenceRequired: true);

        // This fixture deliberately models a legacy/corrupt row that the service must not turn into an
        // apparent successful initialization. The FK is disabled only while inserting that disposable row.
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF");
        db.VerificationImpactItems.Add(invalid);
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON");

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            new BuildTestSetService(db).EnsureForReleaseAsync(fixture.ProjectId, fixture.ReleaseId));

        Assert.Empty(await db.BuildTestSets.AsNoTracking().ToListAsync());
        Assert.DoesNotContain(db.ChangeTracker.Entries<BuildTestSet>(), x => x.State == EntityState.Added);
    }

    private enum BuildSetSaveFailureKind { NonRaceWrite, Cancellation }

    private sealed class BuildSetSaveFailureInterceptor(BuildSetSaveFailureKind kind) : SaveChangesInterceptor
    {
        public bool Enabled { get; set; } = true;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Enabled && eventData.Context?.ChangeTracker.Entries<BuildTestSet>()
                    .Any(entry => entry.State == EntityState.Added) == true)
            {
                if (kind == BuildSetSaveFailureKind.Cancellation)
                    throw new OperationCanceledException("Injected build-test-set cancellation.", cancellationToken);
                throw new DbUpdateException("Injected non-race build-test-set write failure.", (Exception?)null);
            }
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class BuildSetRaceInterceptor(bool completeWinner) : SaveChangesInterceptor
    {
        private bool _armed;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (_armed || eventData.Context is not AeroLinkDbContext db) return result;
            var candidates = db.ChangeTracker.Entries<BuildTestSet>()
                .Where(entry => entry.State == EntityState.Added).Select(entry => entry.Entity).ToArray();
            if (candidates.Length == 0) return result;
            _armed = true;

            var winners = completeWinner ? candidates : candidates.Take(1).ToArray();
            foreach (var winner in winners)
                await InsertAsync(db, winner, cancellationToken);

            // Trigger a real SQLite unique-index exception after the partial or complete winner is present.
            try { await InsertAsync(db, winners[0], cancellationToken, Guid.NewGuid()); }
            catch (SqliteException ex)
            {
                throw new DbUpdateException("Injected expected set race.", ex);
            }
            throw new InvalidOperationException("The race interceptor did not produce a unique violation.");
        }

        private static async Task InsertAsync(AeroLinkDbContext db, BuildTestSet set,
            CancellationToken cancellationToken, Guid? id = null)
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = """
                INSERT INTO build_test_sets ("Id", "ProjectId", "ReleaseId", "Discipline", "CreatedAt", "UpdatedAt", "Version")
                VALUES ($id, $project, $release, $discipline, $created, $updated, $version)
                """;
            command.Parameters.Add(new SqliteParameter("$id", id ?? set.Id));
            command.Parameters.Add(new SqliteParameter("$project", set.ProjectId));
            command.Parameters.Add(new SqliteParameter("$release", set.ReleaseId));
            command.Parameters.Add(new SqliteParameter("$discipline", set.Discipline.ToString()));
            command.Parameters.Add(new SqliteParameter("$created", set.CreatedAt));
            command.Parameters.Add(new SqliteParameter("$updated", set.UpdatedAt));
            command.Parameters.Add(new SqliteParameter("$version", set.Version));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
