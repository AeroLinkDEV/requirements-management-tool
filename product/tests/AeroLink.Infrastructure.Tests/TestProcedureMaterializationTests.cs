using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace AeroLink.Infrastructure.Tests;

/// <summary>
/// The procedure twin of <see cref="RequirementMaterializationTests"/>. Same lifecycle, same assertions, because
/// a test procedure is built and handled the way a requirement is.
/// </summary>
public sealed class TestProcedureMaterializationTests
{
    [Fact]
    public async Task Introduce_modify_and_retire_preserve_revision_history_and_exact_membership()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-tp-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            await using var db = new AeroLinkDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var now = DateTimeOffset.UtcNow;
            var (project, release) = await SeedProjectAsync(db, "FMSP");

            var first = await MaterializeAsync(db, project.Id, release.Id, "SW-00.10", null, now,
                Change("SYSTP-000001", 0, TestProcedureChangeKind.Introduce, "Oceanic sequencing"));
            var second = await MaterializeAsync(db, project.Id, release.Id, "SW-00.20", first.Id, now,
                Change("SYSTP-000001", 1, TestProcedureChangeKind.Modify, "Oceanic sequencing, clarified"));
            var third = await MaterializeAsync(db, project.Id, release.Id, "SW-00.30", second.Id, now,
                Change("SYSTP-000001", 2, TestProcedureChangeKind.Retire, ""));

            var procedure = await db.TestProcedures.SingleAsync();
            var history = await db.TestProcedureRevisions.Where(x => x.ProcedureId == procedure.Id)
                .OrderBy(x => x.Revision).ToListAsync();

            Assert.Equal([0, 1, 2], history.Select(x => x.Revision));
            Assert.Equal(TestProcedureState.Retired, history[^1].State);
            Assert.Single(await db.BaselineTestProcedures.Where(x => x.BaselineId == first.Id).ToListAsync());
            var secondMember = await db.BaselineTestProcedures.SingleAsync(x => x.BaselineId == second.Id);
            Assert.Equal(history[1].Id, secondMember.RevisionId);
            // Retired means gone from the build, not gone from history — the same as a retired requirement.
            Assert.Empty(await db.BaselineTestProcedures.Where(x => x.BaselineId == third.Id).ToListAsync());
            Assert.Equal("Oceanic sequencing, clarified", (await db.TestProcedures.SingleAsync()).Title);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Every_revision_names_the_test_change_request_and_baseline_that_produced_it()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-tp-attr-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            await using var db = new AeroLinkDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var now = DateTimeOffset.UtcNow;
            var (project, release) = await SeedProjectAsync(db, "FMSA");

            var baseline = await MaterializeAsync(db, project.Id, release.Id, "SW-00.10", null, now,
                Change("SYSTP-000001", 0, TestProcedureChangeKind.Introduce, "Oceanic sequencing"));

            var revision = await db.TestProcedureRevisions.SingleAsync();
            var tcr = await db.TestChangeReviews.SingleAsync();
            Assert.Equal(tcr.Id, revision.SourceTestChangeRequestId);
            Assert.Equal(baseline.Id, revision.EffectiveBaselineId);
            // Credited to the engineer who authored the package, not to whoever ran the materialization.
            Assert.Equal("verification.engineer", revision.AuthorId);

            var reloaded = await db.CandidateBaselines.SingleAsync(x => x.Id == baseline.Id);
            Assert.Equal(64, reloaded.TestProceduresHash!.Length);
            Assert.NotNull(reloaded.TestProceduresMaterializedAt);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task A_driving_requirement_becomes_real_coverage_only_once_the_procedure_revision_exists()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-tp-cov-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            await using var db = new AeroLinkDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var now = DateTimeOffset.UtcNow;
            var (project, release) = await SeedProjectAsync(db, "FMSC");

            var requirementRevisionId = await MaterializeRequirementAsync(db, project.Id, release.Id, now);
            // The governed proposal names the exact requirement revision its package carries.
            Assert.Empty(await db.TestCoverage.ToListAsync());
            await MaterializeAsync(db, project.Id, release.Id, "SW-00.20", null, now,
                Change("SYSTP-000001", 0, TestProcedureChangeKind.Introduce, "Oceanic sequencing",
                    JsonSerializer.Serialize(new[] { requirementRevisionId })));

            var coverage = await db.TestCoverage.SingleAsync();
            Assert.Equal(requirementRevisionId, coverage.RequirementRevisionId);
            Assert.Equal((await db.TestProcedureRevisions.SingleAsync()).Id, coverage.ProcedureRevisionId);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Modifying_for_one_requirement_preserves_unchanged_predecessor_coverage()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-tp-carry-coverage-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            await using var db = new AeroLinkDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var now = DateTimeOffset.UtcNow;
            var (project, release) = await SeedProjectAsync(db, "FMSCARRY");
            var changedRequirement = await MaterializeRequirementAsync(db, project.Id, release.Id, now);
            var fixtureMarker = Guid.Parse("5a1d1f92-6c2f-4a1e-9d33-0f5b2c7a4e10");

            var first = await MaterializeAsync(db, project.Id, release.Id, "SW-00.10", null, now,
                Change("SYSTP-000001", 0, TestProcedureChangeKind.Introduce, "Oceanic sequencing",
                    JsonSerializer.Serialize(new[] { changedRequirement, fixtureMarker })));
            var originalCoverage = await db.TestCoverage.AsNoTracking()
                .Where(x => db.TestProcedureRevisions.Where(r => r.Revision == 0)
                    .Select(r => r.Id).Contains(x.ProcedureRevisionId))
                .Select(x => x.RequirementRevisionId).ToHashSetAsync();
            Assert.Equal(2, originalCoverage.Count);
            var unchangedRequirement = originalCoverage.Single(x => x != changedRequirement);
            var predecessorLink = await db.TestCoverage.SingleAsync(x =>
                x.RequirementRevisionId == unchangedRequirement);
            predecessorLink.MarkSuspect("Unchanged requirement still awaits confirmation.", now);
            await db.SaveChangesAsync();

            await MaterializeAsync(db, project.Id, release.Id, "SW-00.20", first.Id, now,
                Change("SYSTP-000001", 1, TestProcedureChangeKind.Modify,
                    "Oceanic sequencing, clarified for the changed requirement",
                    JsonSerializer.Serialize(new[] { changedRequirement })));

            var modifiedRevision = await db.TestProcedureRevisions.SingleAsync(x => x.Revision == 1);
            var modifiedCoverage = await db.TestCoverage.AsNoTracking()
                .Where(x => x.ProcedureRevisionId == modifiedRevision.Id)
                .Select(x => x.RequirementRevisionId).ToHashSetAsync();
            Assert.True(modifiedCoverage.SetEquals(originalCoverage));
            var retainedLink = await db.TestCoverage.SingleAsync(x =>
                x.ProcedureRevisionId == modifiedRevision.Id
                && x.RequirementRevisionId == unchangedRequirement);
            Assert.True(retainedLink.IsSuspect);
            Assert.Equal("Unchanged requirement still awaits confirmation.", retainedLink.SuspectReason);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task A_governed_addition_and_removal_produce_the_exact_approved_coverage_delta()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-tp-delta-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            await using var db = new AeroLinkDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var now = DateTimeOffset.UtcNow;
            var (project, release) = await SeedProjectAsync(db, "FMSDELTA");
            var retained = await MaterializeRequirementAsync(db, project.Id, release.Id, now);
            var marker = Guid.Parse("5a1d1f92-6c2f-4a1e-9d33-0f5b2c7a4e11");
            var first = await MaterializeAsync(db, project.Id, release.Id, "SW-00.10", null, now,
                Change("SYSTP-000001", 0, TestProcedureChangeKind.Introduce, "Oceanic sequencing",
                    JsonSerializer.Serialize(new[] { retained, marker })));
            var removed = (await db.TestCoverage.AsNoTracking()
                .Select(x => x.RequirementRevisionId).ToListAsync()).Single(x => x != retained);

            await MaterializeAsync(db, project.Id, release.Id, "SW-00.20", first.Id, now,
                Change("SYSTP-000001", 1, TestProcedureChangeKind.Modify, "Oceanic sequencing with datalink",
                    JsonSerializer.Serialize(new[] { marker }), JsonSerializer.Serialize(new[] { removed }),
                    "Datalink replaces the legacy waypoint input covered by the removed requirement."));

            var modified = await db.TestProcedureRevisions.SingleAsync(x => x.Revision == 1);
            var final = await db.TestCoverage.AsNoTracking().Where(x => x.ProcedureRevisionId == modified.Id)
                .Select(x => x.RequirementRevisionId).ToListAsync();
            Assert.Equal(2, final.Count);
            Assert.Contains(retained, final);
            Assert.DoesNotContain(removed, final);
            var added = Assert.Single(final, x => !new[] { retained, removed }.Contains(x));
            Assert.NotEqual(Guid.Empty, added);
            var decision = await db.Set<TestProcedureChange>().SingleAsync(x => x.Revision == 1);
            Assert.Equal("Datalink replaces the legacy waypoint input covered by the removed requirement.",
                decision.CoverageChangeRationale);
            Assert.Equal("verification.engineer", decision.CoverageChangedBy);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task An_invalid_coverage_removal_rolls_back_the_new_revision_and_manifest()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-tp-delta-rollback-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            await using var db = new AeroLinkDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var now = DateTimeOffset.UtcNow;
            var (project, release) = await SeedProjectAsync(db, "FMSROLLBACK");
            var first = await MaterializeAsync(db, project.Id, release.Id, "SW-00.10", null, now,
                Change("SYSTP-000001", 0, TestProcedureChangeKind.Introduce, "Oceanic sequencing"));
            var marker = Guid.Parse("5a1d1f92-6c2f-4a1e-9d33-0f5b2c7a4e11");

            var error = await Assert.ThrowsAsync<DomainException>(() => MaterializeAsync(db, project.Id,
                release.Id, "SW-00.20", first.Id, now,
                Change("SYSTP-000001", 1, TestProcedureChangeKind.Modify, "Invalid removal", "[]",
                    JsonSerializer.Serialize(new[] { marker }), "Remove coverage that never existed.")));
            Assert.Contains("does not cover", error.Message, StringComparison.OrdinalIgnoreCase);

            Assert.Single(await db.TestProcedureRevisions.ToListAsync());
            Assert.Single(await db.TestCoverage.ToListAsync());
            var failed = await db.CandidateBaselines.SingleAsync(x => x.BaseNumber == "SW-00.20");
            Assert.Null(failed.TestProceduresMaterializedAt);
            Assert.Empty(await db.BaselineTestProcedures.Where(x => x.BaselineId == failed.Id).ToListAsync());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Materialization_refuses_an_out_of_scope_driving_requirement_without_partial_writes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-tp-scope-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            Guid baselineId;
            await using (var db = new AeroLinkDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();
                var now = DateTimeOffset.UtcNow;
                var (project, release) = await SeedProjectAsync(db, "FMSSCOPE");
                var source = ApprovedChangeRequest(project.Id, release.Id, "SRCR-00400", now);
                var unrelated = ApprovedChangeRequest(project.Id, release.Id, "SRCR-00401", now);
                var baseline = new CandidateBaseline("SW-04.00", 0, project.Id, release.Id, null,
                    "Governed scope", "cm", now);
                baseline.Select(source, "cm", now);
                baseline.Select(unrelated, "cm", now);
                baseline.Freeze("cm", now);
                baseline.MarkRequirementsMaterialized("cm", new string('f', 64), 2, now);

                var governedArtifact = new RequirementArtifact(project.Id, "SYSR-000400", RequirementLevel.System, now);
                var governedRevision = new RequirementRevision(governedArtifact.Id, 0, "Governed requirement.",
                    "Source package.", "Test", RequirementRevisionState.Active, source.Id, baseline.Id, now);
                var unrelatedArtifact = new RequirementArtifact(project.Id, "SYSR-000401", RequirementLevel.System, now);
                var unrelatedRevision = new RequirementRevision(unrelatedArtifact.Id, 0, "Unrelated requirement.",
                    "Different package.", "Test", RequirementRevisionState.Active, unrelated.Id, baseline.Id, now);

                var tcr = new TestChangeReview(project.Id, release.Id, source.Id,
                    TestChangeReviewDiscipline.System, source.DisplayNumber, now);
                tcr.RecordTestChangeRequired("verification.engineer", now);
                tcr.AssignControlledNumber("SYSTCR-000400", now);
                tcr.AddProcedureChange("verification.engineer",
                    Change("SYSTP-000400", 0, TestProcedureChangeKind.Introduce, "Malformed legacy proposal",
                        JsonSerializer.Serialize(new[] { unrelatedRevision.Id })), now);
                tcr.Submit("verification.engineer", "test.lead", true, now);
                tcr.Approve("test.lead", "Approved legacy snapshot.", now);
                var item = VerificationImpactItem.ForIntroducedRequirement(project.Id, release.Id, source.Id,
                    tcr.Id, source.RequirementChanges.First().Id, "SYSR-000400.00", "Test", now);
                item.LinkRequirementRevision(governedRevision.Id, now);

                db.AddRange(source, unrelated, baseline, governedArtifact, governedRevision,
                    unrelatedArtifact, unrelatedRevision, tcr, item,
                    new BaselineRequirementSelection(baseline.Id, governedArtifact.Id, governedRevision.Id),
                    new BaselineRequirementSelection(baseline.Id, unrelatedArtifact.Id, unrelatedRevision.Id));
                await db.SaveChangesAsync();
                baseline.SelectTestChangeRequest(tcr, "verification.lead", now);
                await db.SaveChangesAsync();
                baselineId = baseline.Id;

                var error = await Assert.ThrowsAsync<DomainException>(() =>
                    new TestProcedureBaselineMaterializer(db).MaterializeAsync(baseline.Id, "cm", now, default));
                Assert.Contains("outside", error.Message, StringComparison.OrdinalIgnoreCase);
            }

            await using var assertDb = new AeroLinkDbContext(options);
            Assert.Empty(await assertDb.TestProcedures.ToListAsync());
            Assert.Empty(await assertDb.TestProcedureRevisions.ToListAsync());
            Assert.Empty(await assertDb.TestCoverage.ToListAsync());
            Assert.Empty(await assertDb.BaselineTestProcedures.ToListAsync());
            Assert.Null((await assertDb.CandidateBaselines.SingleAsync(x => x.Id == baselineId))
                .TestProceduresMaterializedAt);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task A_modification_of_a_procedure_no_build_carries_is_refused()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-tp-orphan-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            await using var db = new AeroLinkDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var now = DateTimeOffset.UtcNow;
            var (project, release) = await SeedProjectAsync(db, "FMSO");

            var error = await Assert.ThrowsAsync<DomainException>(() =>
                MaterializeAsync(db, project.Id, release.Id, "SW-00.10", null, now,
                    Change("SYSTP-000009", 1, TestProcedureChangeKind.Modify, "Nothing to modify")));
            Assert.Contains("SYSTP-000009", error.Message);

            // And a revision that does not advance is refused, so history cannot be overwritten in place.
            var first = await MaterializeAsync(db, project.Id, release.Id, "SW-00.20", null, now,
                Change("SYSTP-000001", 0, TestProcedureChangeKind.Introduce, "Oceanic sequencing"));
            await Assert.ThrowsAsync<DomainException>(() =>
                MaterializeAsync(db, project.Id, release.Id, "SW-00.30", first.Id, now,
                    Change("SYSTP-000001", 0, TestProcedureChangeKind.Modify, "Same revision again")));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task A_predecessor_with_no_procedure_manifest_starts_the_successor_empty_rather_than_failing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-tp-legacy-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            await using var db = new AeroLinkDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var now = DateTimeOffset.UtcNow;
            var (project, release) = await SeedProjectAsync(db, "FMSL");

            // Every build that exists today is this: frozen and materialized for requirements, with no procedure
            // manifest at all. Its successor has to be able to start.
            var legacy = new CandidateBaseline("SW-00.10", 0, project.Id, release.Id, null, "Legacy", "cm", now);
            legacy.Select(ApprovedChangeRequest(project.Id, release.Id, "SRCR-00099", now), "cm", now);
            legacy.Freeze("cm", now);
            legacy.MarkRequirementsMaterialized("cm", new string('a', 64), 0, now);
            db.Add(legacy);
            await db.SaveChangesAsync();

            var successor = await MaterializeAsync(db, project.Id, release.Id, "SW-00.20", legacy.Id, now,
                Change("SYSTP-000001", 0, TestProcedureChangeKind.Introduce, "Oceanic sequencing"));

            Assert.Single(await db.BaselineTestProcedures.Where(x => x.BaselineId == successor.Id).ToListAsync());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task A_decision_that_asked_for_a_procedure_settles_when_the_test_change_request_delivers_it()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-tp-settle-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            await using var db = new AeroLinkDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var now = DateTimeOffset.UtcNow;
            var (project, release) = await SeedProjectAsync(db, "FMSS");

            var requirementRevisionId = await MaterializeRequirementAsync(db, project.Id, release.Id, now);
            var item = await AwaitingNewProcedureAsync(db, project.Id, release.Id, requirementRevisionId, now);

            // Before: the engineer has decided a procedure must be written, and none exists.
            Assert.True(item.AwaitsNewProcedure);

            await MaterializeAsync(db, project.Id, release.Id, "SW-00.20", null, now,
                Change("SYSTP-000001", 0, TestProcedureChangeKind.Introduce, "Oceanic sequencing",
                    JsonSerializer.Serialize(new[] { requirementRevisionId })));

            // The direct-authoring endpoint already settles these on approval. A procedure delivered by a test
            // change request never passes through it, so without settling here the engineer would be asked the
            // same question twice and the coverage gate would keep holding against work already done.
            var settled = await db.VerificationImpactItems.SingleAsync(x => x.Id == item.Id);
            Assert.False(settled.AwaitsNewProcedure);
            Assert.Equal(VerificationImpactOutcome.ProcedureCoverageConfirmed, settled.Outcome);
            Assert.Equal((await db.TestProcedureRevisions.SingleAsync()).Id, settled.ResolvedProcedureRevisionId);

            var history = await db.VerificationImpactDecisionHistory
                .SingleAsync(x => x.VerificationImpactItemId == item.Id);
            Assert.Contains("SYSTP-000001.00", history.Rationale);
            Assert.Contains("SYSTCR-", history.Rationale);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task A_procedure_for_a_different_requirement_is_refused_and_settles_nothing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-tp-nosettle-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            await using var db = new AeroLinkDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var now = DateTimeOffset.UtcNow;
            var (project, release) = await SeedProjectAsync(db, "FMSN");

            var requirementRevisionId = await MaterializeRequirementAsync(db, project.Id, release.Id, now);
            var item = await AwaitingNewProcedureAsync(db, project.Id, release.Id, requirementRevisionId, now);

            // A malformed snapshot cannot create unrelated coverage or quietly close the governed decision.
            await Assert.ThrowsAsync<DomainException>(() => MaterializeAsync(db, project.Id, release.Id,
                "SW-00.20", null, now, Change("SYSTP-000001", 0,
                    TestProcedureChangeKind.Introduce, "Something else",
                    JsonSerializer.Serialize(new[] { Guid.NewGuid() }))));

            var untouched = await db.VerificationImpactItems.SingleAsync(x => x.Id == item.Id);
            Assert.True(untouched.AwaitsNewProcedure);
            Assert.Empty(await db.VerificationImpactDecisionHistory.ToListAsync());
        }
        finally { File.Delete(path); }
    }

    /// <summary>An item an engineer has answered with "a procedure must be written", which nothing satisfies yet.</summary>
    private static async Task<VerificationImpactItem> AwaitingNewProcedureAsync(AeroLinkDbContext db,
        Guid projectId, Guid releaseId, Guid requirementRevisionId, DateTimeOffset now)
    {
        var scr = ApprovedChangeRequest(projectId, releaseId, "SRCR-00777", now);
        var review = new TestChangeReview(projectId, releaseId, scr.Id,
            TestChangeReviewDiscipline.System, scr.DisplayNumber, now);
        var item = VerificationImpactItem.ForIntroducedRequirement(projectId, releaseId, scr.Id,
            review.Id, scr.RequirementChanges.First().Id, "SYSR-000001.00", "Test", now);
        item.LinkRequirementRevision(requirementRevisionId, now);
        item.Resolve("verification.engineer", VerificationImpactOutcome.NewProcedureRequired,
            "No procedure exercises oceanic sequencing yet.", now);
        db.AddRange(scr, review, item);
        await db.SaveChangesAsync();
        return item;
    }

    private static async Task<(ProjectRecord, SoftwareRelease)> SeedProjectAsync(AeroLinkDbContext db, string prefix)
    {
        var program = new ProgramRecord("FMS", prefix);
        var project = new ProjectRecord(program.Id, "Software", "FMS Software");
        var release = new SoftwareRelease(project.Id, "3.3", false);
        db.AddRange(program, project, release);
        await db.SaveChangesAsync();
        return (project, release);
    }

    private static TestProcedureChangeDraft Change(string baseNumber, int revision, TestProcedureChangeKind kind,
        // Defaulted to naming a requirement, because submission refuses an introduced procedure that names
        // none. A fixed identifier rather than a fresh one: these tests assert on membership and history, so
        // a value that changed per run would make a failure harder to read. Tests that care pass their own.
        string title, string drivingRequirementRevisionIdsJson = "[\"5a1d1f92-6c2f-4a1e-9d33-0f5b2c7a4e10\"]",
        string removedRequirementRevisionIdsJson = "[]", string coverageChangeRationale = "") =>
        new(baseNumber, revision, TestProcedureLevel.System, kind, title,
            kind == TestProcedureChangeKind.Retire ? "" : "Verify oceanic waypoint sequencing.",
            kind == TestProcedureChangeKind.Retire ? "" : "The aircraft is in cruise on an oceanic plan.",
            kind == TestProcedureChangeKind.Retire ? "" : "1. Load the plan. 2. Read the sequencer.",
            kind == TestProcedureChangeKind.Retire ? "" : "The next eligible waypoint is sequenced.",
            "The approved change altered oceanic sequencing.", drivingRequirementRevisionIdsJson,
            removedRequirementRevisionIdsJson, coverageChangeRationale);

    /// <summary>Frozen, requirements materialized, one approved test change request carried, then materialized.</summary>
    private static async Task<CandidateBaseline> MaterializeAsync(AeroLinkDbContext db, Guid projectId,
        Guid releaseId, string number, Guid? predecessor, DateTimeOffset now, TestProcedureChangeDraft draft)
    {
        var scr = ApprovedChangeRequest(projectId, releaseId, $"SRCR-{Math.Abs(number.GetHashCode()) % 100000:D5}", now);
        var baseline = new CandidateBaseline(number, 0, projectId, releaseId, predecessor, number, "cm", now);
        baseline.Select(scr, "cm", now);
        baseline.Freeze("cm", now);

        var tcr = new TestChangeReview(projectId, releaseId, scr.Id, TestChangeReviewDiscipline.System,
            scr.DisplayNumber, now);
        tcr.RecordTestChangeRequired("verification.engineer", now);
        tcr.AssignControlledNumber($"SYSTCR-{Math.Abs(number.GetHashCode()) % 1000000:D6}", now);
        var requestedIds = JsonSerializer.Deserialize<List<Guid>>(draft.DrivingRequirementRevisionIdsJson) ?? [];
        var removedIds = JsonSerializer.Deserialize<List<Guid>>(draft.RemovedRequirementRevisionIdsJson) ?? [];
        var fixtureMarker = Guid.Parse("5a1d1f92-6c2f-4a1e-9d33-0f5b2c7a4e10");
        var additionMarker = Guid.Parse("5a1d1f92-6c2f-4a1e-9d33-0f5b2c7a4e11");
        var fixtureRows = new List<(RequirementRevision Revision, RequirementArtifact Artifact)>();
        var predecessorRevisionId = predecessor is null
            ? Guid.Empty
            : await db.BaselineRequirements.Where(x => x.BaselineId == predecessor.Value)
                .Select(x => x.RevisionId).FirstOrDefaultAsync();
        if (predecessorRevisionId != Guid.Empty
            && (requestedIds.Contains(fixtureMarker) || removedIds.Contains(fixtureMarker)))
        {
            requestedIds = requestedIds.Select(x => x == fixtureMarker ? predecessorRevisionId : x).ToList();
            removedIds = removedIds.Select(x => x == fixtureMarker ? predecessorRevisionId : x).ToList();
        }
        var markerToCreate = requestedIds.Contains(additionMarker) || removedIds.Contains(additionMarker)
            ? additionMarker
            : requestedIds.Contains(fixtureMarker) || removedIds.Contains(fixtureMarker)
                ? fixtureMarker
                : Guid.Empty;
        if (markerToCreate != Guid.Empty)
        {
            var artifact = new RequirementArtifact(projectId,
                $"SYSR-{Math.Abs(number.GetHashCode()) % 1000000:D6}", RequirementLevel.System, now);
            var revision = new RequirementRevision(artifact.Id, 0,
                "The FMS shall sequence oceanic waypoints.", "Governed fixture requirement.", "Test",
                RequirementRevisionState.Active, scr.Id, baseline.Id, now);
            db.AddRange(artifact, revision);
            fixtureRows.Add((revision, artifact));
            requestedIds = requestedIds.Select(x => x == markerToCreate ? revision.Id : x).ToList();
            removedIds = removedIds.Select(x => x == markerToCreate ? revision.Id : x).ToList();
        }
        draft = draft with
        {
            DrivingRequirementRevisionIdsJson = JsonSerializer.Serialize(requestedIds),
            RemovedRequirementRevisionIdsJson = JsonSerializer.Serialize(removedIds)
        };
        var scopedIds = requestedIds.Concat(removedIds).Distinct().ToList();
        var persisted = await (from revision in db.RequirementRevisions
                               join artifact in db.Requirements on revision.ArtifactId equals artifact.Id
                               where scopedIds.Contains(revision.Id)
                               select new { Revision = revision, Artifact = artifact }).ToListAsync();
        var known = persisted.Select(x => (x.Revision, x.Artifact)).Concat(fixtureRows).ToList();
        var predecessorId = predecessor ?? Guid.Empty;
        var carried = await (from selection in db.BaselineRequirements
                             where selection.BaselineId == predecessorId
                             join revision in db.RequirementRevisions on selection.RevisionId equals revision.Id
                             join artifact in db.Requirements on selection.ArtifactId equals artifact.Id
                             select new { Revision = revision, Artifact = artifact }).ToListAsync();
        var manifest = carried.Select(x => (x.Revision, x.Artifact)).Concat(known)
            .DistinctBy(x => x.Revision.Id).ToList();
        foreach (var row in manifest)
            db.Add(new BaselineRequirementSelection(baseline.Id, row.Artifact.Id, row.Revision.Id));
        foreach (var row in known)
        {
            var impact = VerificationImpactItem.ForIntroducedRequirement(projectId, releaseId, scr.Id,
                tcr.Id, scr.RequirementChanges.First().Id,
                $"{row.Artifact.BaseNumber}.{row.Revision.Revision:D2}", "Test", now);
            impact.LinkRequirementRevision(row.Revision.Id, now);
            db.Add(impact);
        }
        baseline.MarkRequirementsMaterialized("cm", new string('b', 64), manifest.Count, now);
        tcr.AddProcedureChange("verification.engineer", draft, now);
        tcr.Submit("verification.engineer", "test.lead", true, now);
        tcr.Approve("test.lead", "Procedure decisions are complete.", now);
        db.AddRange(scr, tcr, baseline);
        await db.SaveChangesAsync();

        baseline.SelectTestChangeRequest(tcr, "verification.lead", now);
        await db.SaveChangesAsync();

        await new TestProcedureBaselineMaterializer(db).MaterializeAsync(baseline.Id, "cm", now, default);
        return baseline;
    }

    private static async Task<Guid> MaterializeRequirementAsync(AeroLinkDbContext db, Guid projectId,
        Guid releaseId, DateTimeOffset now)
    {
        var scr = new SystemChangeRequest("SRCR-00001", 0, projectId, releaseId, "Oceanic", "P", "A", "S", "author", now);
        scr.AddRequirementChange("author", "SYSR-000001", 0, RequirementLevel.System,
            RequirementChangeKind.Introduce, "The FMS shall sequence oceanic waypoints.", "Needed.", "Test", now);
        scr.SubmitForReview("author", [new ApproverSelection("reviewer", "Reviewer")], now);
        scr.ApproveActiveStage("reviewer", now);
        var baseline = new CandidateBaseline("SW-00.05", 0, projectId, releaseId, null, "Requirements", "cm", now);
        baseline.Select(scr, "cm", now);
        baseline.Freeze("cm", now);
        db.AddRange(scr, baseline);
        await db.SaveChangesAsync();
        await new RequirementBaselineMaterializer(db, new VerificationImpactService(db))
            .MaterializeAsync(baseline.Id, "cm", now, default);
        return (await db.RequirementRevisions.SingleAsync()).Id;
    }

    private static SystemChangeRequest ApprovedChangeRequest(Guid projectId, Guid releaseId, string number,
        DateTimeOffset now)
    {
        var scr = new SystemChangeRequest(number, 0, projectId, releaseId, "Oceanic sequencing", "P", "A", "S", "author", now);
        scr.AddRequirementChange("author", $"SYSR-{Math.Abs(number.GetHashCode()) % 1000000:D6}", 0,
            RequirementLevel.System, RequirementChangeKind.Introduce,
            "The FMS shall sequence oceanic waypoints.", "Needed.", "Test", now);
        scr.SubmitForReview("author", [new ApproverSelection("reviewer", "Reviewer")], now);
        scr.ApproveActiveStage("reviewer", now);
        return scr;
    }
}
