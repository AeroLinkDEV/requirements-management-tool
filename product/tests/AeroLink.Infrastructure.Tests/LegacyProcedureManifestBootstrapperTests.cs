using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

public sealed class LegacyProcedureManifestBootstrapperTests
{
    [Fact]
    public async Task Bootstrap_is_exact_attributable_idempotent_and_carries_forward_without_rewriting_coverage()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-legacy-procedure-bootstrap-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            await using var db = new AeroLinkDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var now = new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
            var program = new ProgramRecord("Legacy Procedure Bootstrap", "LPB");
            var project = new ProjectRecord(program.Id, "Flight Management", "Legacy FMS");
            var otherProject = new ProjectRecord(program.Id, "Other Product", "Other Software");
            var release = new SoftwareRelease(project.Id, "1.5", true);
            var source = ApprovedChangeRequest(project.Id, release.Id, "SRCR-09800", now);
            var baseline = FrozenRequirementBaseline(project.Id, release.Id, source, "SW-98.00", now);

            var requirement = new RequirementArtifact(project.Id, "SYSR-09800000", RequirementLevel.System, now);
            var requirementRevision = new RequirementRevision(requirement.Id, 0,
                "The product shall retain its legacy verification inventory.", "Migration integrity.", "Test",
                RequirementRevisionState.Active, source.Id, baseline.Id, now);

            var draftedOverApproved = new TestProcedure(project.Id, "SYSTP-098001",
                "Approved predecessor with a later draft", "legacy.author", now, TestProcedureLevel.System);
            var approved00 = Revision(draftedOverApproved.Id, 0, TestProcedureState.Approved, now);
            var draft01 = Revision(draftedOverApproved.Id, 1, TestProcedureState.Draft, now.AddMinutes(1));

            var retired = new TestProcedure(project.Id, "SYSTP-098002",
                "Retired legacy procedure", "legacy.author", now, TestProcedureLevel.System);
            var retired00 = Revision(retired.Id, 0, TestProcedureState.Approved, now);
            var retired01 = Revision(retired.Id, 1, TestProcedureState.Retired, now.AddMinutes(1));

            var active = new TestProcedure(project.Id, "SYSTP-098003",
                "Still-active legacy procedure", "legacy.author", now, TestProcedureLevel.System);
            var active00 = Revision(active.Id, 0, TestProcedureState.Approved, now);

            var foreign = new TestProcedure(otherProject.Id, "SYSTP-099999",
                "Another project", "other.author", now, TestProcedureLevel.System);
            var foreign00 = Revision(foreign.Id, 0, TestProcedureState.Approved, now);

            var existingCoverage = new TestRequirementCoverage(approved00.Id, requirementRevision.Id);
            db.AddRange(program, project, otherProject, release, source, baseline, requirement,
                requirementRevision,
                new BaselineRequirementSelection(baseline.Id, requirement.Id, requirementRevision.Id),
                draftedOverApproved, approved00, draft01, retired, retired00, retired01,
                active, active00, foreign, foreign00, existingCoverage);
            await db.SaveChangesAsync();

            var bootstrapper = new LegacyProcedureManifestBootstrapper(db);
            var preview = Assert.IsType<LegacyProcedureManifestBootstrapView>(
                await bootstrapper.PreviewAsync(baseline.Id, CancellationToken.None));
            Assert.False(preview.AlreadyBootstrapped);
            Assert.Equal(2, preview.ActiveProcedureCount);
            Assert.Equal(1, preview.RetiredProcedureCount);
            Assert.Equal(1, preview.DraftRevisionCount);
            Assert.Equal(64, preview.ProceduresHash.Length);
            Assert.Contains("non-Draft", preview.SelectionRule);

            var stale = new string('0', 64);
            var refusal = await Assert.ThrowsAsync<DomainException>(() => bootstrapper.BootstrapAsync(
                baseline.Id, "migration.cm", stale, now.AddHours(1), CancellationToken.None));
            Assert.Contains("changed after preview", refusal.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(await db.BaselineTestProcedures.AsNoTracking().ToListAsync());
            Assert.DoesNotContain(await db.BaselineEvents.AsNoTracking().ToListAsync(),
                x => x.EventType == "LegacyProcedureManifestBootstrapped");

            var result = Assert.IsType<LegacyProcedureManifestBootstrapView>(
                await bootstrapper.BootstrapAsync(baseline.Id, "migration.cm", preview.ProceduresHash,
                    now.AddHours(2), CancellationToken.None));
            Assert.True(result.AlreadyBootstrapped);
            Assert.Equal(preview.ProceduresHash, result.ProceduresHash);

            var members = await db.BaselineTestProcedures.AsNoTracking()
                .Where(x => x.BaselineId == baseline.Id).OrderBy(x => x.ProcedureId).ToListAsync();
            Assert.Equal(2, members.Count);
            Assert.Contains(members, x => x.ProcedureId == draftedOverApproved.Id && x.RevisionId == approved00.Id);
            Assert.Contains(members, x => x.ProcedureId == active.Id && x.RevisionId == active00.Id);
            Assert.DoesNotContain(members, x => x.ProcedureId == retired.Id);
            Assert.DoesNotContain(members, x => x.ProcedureId == foreign.Id);
            Assert.Equal(approved00.Id, (await db.TestCoverage.AsNoTracking().SingleAsync()).ProcedureRevisionId);

            var reloaded = await db.CandidateBaselines.AsNoTracking().SingleAsync(x => x.Id == baseline.Id);
            Assert.Equal(preview.ProceduresHash, reloaded.TestProceduresHash);
            Assert.Equal(now.AddHours(2), reloaded.TestProceduresMaterializedAt);
            var recorded = Assert.Single(await db.BaselineEvents.AsNoTracking()
                .Where(x => x.BaselineId == baseline.Id
                            && x.EventType == "LegacyProcedureManifestBootstrapped").ToListAsync());
            Assert.Equal("migration.cm", recorded.ActorId);
            Assert.Contains(preview.ProceduresHash, recorded.Detail);
            Assert.Contains(LegacyProcedureManifestBootstrapper.SelectionRule, recorded.Detail);

            var retried = Assert.IsType<LegacyProcedureManifestBootstrapView>(
                await bootstrapper.BootstrapAsync(baseline.Id, "second.cm", preview.ProceduresHash,
                    now.AddHours(3), CancellationToken.None));
            Assert.True(retried.AlreadyBootstrapped);
            Assert.Equal(preview.ProceduresHash, retried.ProceduresHash);
            Assert.Equal(2, await db.BaselineTestProcedures.CountAsync(x => x.BaselineId == baseline.Id));
            Assert.Equal(1, await db.BaselineEvents.CountAsync(x => x.BaselineId == baseline.Id
                && x.EventType == "LegacyProcedureManifestBootstrapped"));

            // Once the predecessor carries exact immutable membership, the ordinary successor path starts from
            // that exact set. No current-inventory fallback or inferred coverage is needed.
            var successorRelease = new SoftwareRelease(project.Id, "1.6", false, release.Id);
            var successorSource = ApprovedChangeRequest(project.Id, successorRelease.Id, "SRCR-09801", now);
            var successor = FrozenRequirementBaseline(project.Id, successorRelease.Id, successorSource,
                "SW-98.10", now.AddHours(4), baseline.Id);
            db.AddRange(successorRelease, successorSource, successor);
            await db.SaveChangesAsync();
            await new TestProcedureBaselineMaterializer(db).MaterializeAsync(
                successor.Id, "successor.cm", now.AddHours(5), CancellationToken.None);

            var successorMembers = await db.BaselineTestProcedures.AsNoTracking()
                .Where(x => x.BaselineId == successor.Id).Select(x => x.RevisionId).OrderBy(x => x).ToListAsync();
            Assert.Equal(members.Select(x => x.RevisionId).OrderBy(x => x), successorMembers);
            var successorReloaded = await db.CandidateBaselines.AsNoTracking()
                .SingleAsync(x => x.Id == successor.Id);
            Assert.Equal(preview.ProceduresHash, successorReloaded.TestProceduresHash);
            Assert.Single(await db.TestCoverage.AsNoTracking().ToListAsync());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task A_genuinely_empty_legacy_project_can_record_one_exact_empty_manifest()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-empty-legacy-bootstrap-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            await using var db = new AeroLinkDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var now = DateTimeOffset.UtcNow;
            var program = new ProgramRecord("Empty Legacy Program", "ELP");
            var project = new ProjectRecord(program.Id, "Empty Project", "Empty Software");
            var release = new SoftwareRelease(project.Id, "1.0", true);
            var source = ApprovedChangeRequest(project.Id, release.Id, "SRCR-09700", now);
            var baseline = FrozenRequirementBaseline(project.Id, release.Id, source, "SW-97.00", now);
            db.AddRange(program, project, release, source, baseline);
            await db.SaveChangesAsync();

            var service = new LegacyProcedureManifestBootstrapper(db);
            var preview = Assert.IsType<LegacyProcedureManifestBootstrapView>(
                await service.PreviewAsync(baseline.Id, CancellationToken.None));
            Assert.Equal(0, preview.ActiveProcedureCount);
            Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
                preview.ProceduresHash);

            await service.BootstrapAsync(baseline.Id, "migration.cm", preview.ProceduresHash,
                now.AddMinutes(10), CancellationToken.None);
            Assert.Empty(await db.BaselineTestProcedures.AsNoTracking().ToListAsync());
            Assert.Equal(preview.ProceduresHash,
                (await db.CandidateBaselines.AsNoTracking().SingleAsync()).TestProceduresHash);
            Assert.Single(await db.BaselineEvents.AsNoTracking()
                .Where(x => x.EventType == "LegacyProcedureManifestBootstrapped").ToListAsync());
        }
        finally { File.Delete(path); }
    }

    private static TestProcedureRevision Revision(Guid procedureId, int revision, TestProcedureState state,
        DateTimeOffset now) => new(procedureId, revision, $"Objective {revision}", "Preconditions",
        "1. Exercise the procedure.", "Expected result", state, "legacy.author", now);

    private static CandidateBaseline FrozenRequirementBaseline(Guid projectId, Guid releaseId,
        SystemChangeRequest source, string number, DateTimeOffset now, Guid? predecessorId = null)
    {
        var baseline = new CandidateBaseline(number, 0, projectId, releaseId, predecessorId,
            "Legacy predecessor", "cm", now);
        baseline.Select(source, "cm", now);
        baseline.Freeze("cm", now.AddMinutes(1));
        baseline.MarkRequirementsMaterialized("cm", new string('a', 64), 1, now.AddMinutes(2));
        return baseline;
    }

    private static SystemChangeRequest ApprovedChangeRequest(Guid projectId, Guid releaseId,
        string number, DateTimeOffset now)
    {
        var request = new SystemChangeRequest(number, 0, projectId, releaseId,
            "Legacy procedure bootstrap source", "Problem", "Analysis", "Solution", "author", now);
        request.AddRequirementChange("author", number.Replace("SRCR", "SYSR") + "00", 0,
            RequirementLevel.System, RequirementChangeKind.Introduce,
            "The product shall retain its legacy verification inventory.",
            "Migration integrity.", "Test", now);
        request.SubmitForReview("author", [new ApproverSelection("reviewer", "Reviewer")], now);
        request.ApproveActiveStage("reviewer", now.AddMinutes(1));
        return request;
    }
}
