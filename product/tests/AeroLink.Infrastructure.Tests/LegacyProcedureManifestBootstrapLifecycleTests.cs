using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace AeroLink.Infrastructure.Tests;

public sealed class LegacyProcedureManifestBootstrapLifecycleTests
{
    [Fact]
    public async Task Bootstrapped_membership_carries_forward_then_normal_modify_retire_and_introduce_apply()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-legacy-lifecycle-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            await using var db = new AeroLinkDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var now = DateTimeOffset.UtcNow;
            var program = new ProgramRecord("Legacy Lifecycle", "LBLIFE");
            var project = new ProjectRecord(program.Id, "Flight Management", "Legacy FMS");
            var release15 = new SoftwareRelease(project.Id, "1.5", true);
            var source15 = ApprovedRequest(project.Id, release15.Id, "SRCR-09500", now);
            var baseline15 = FrozenBaseline(project.Id, release15.Id, source15, "SW-95.00", now);
            var first = new TestProcedure(project.Id, "SYSTP-095001", "First legacy procedure",
                "legacy.author", now, TestProcedureLevel.System);
            var first00 = Revision(first.Id, 0, TestProcedureState.Approved, now);
            var second = new TestProcedure(project.Id, "SYSTP-095002", "Second legacy procedure",
                "legacy.author", now, TestProcedureLevel.System);
            var second00 = Revision(second.Id, 0, TestProcedureState.Approved, now);
            db.AddRange(program, project, release15, source15, baseline15, first, first00, second, second00);
            await db.SaveChangesAsync();

            var bootstrap = new LegacyProcedureManifestBootstrapper(db);
            var preview = (await bootstrap.PreviewAsync(baseline15.Id, CancellationToken.None))!;
            await bootstrap.BootstrapAsync(baseline15.Id, "migration.cm", preview.ProceduresHash,
                now.AddHours(1), CancellationToken.None);

            var release16 = new SoftwareRelease(project.Id, "1.6", false, release15.Id);
            var source16 = ApprovedRequest(project.Id, release16.Id, "SRCR-09501", now);
            var baseline16 = FrozenBaseline(project.Id, release16.Id, source16, "SW-95.10",
                now.AddHours(2), baseline15.Id);
            db.AddRange(release16, source16, baseline16);
            await db.SaveChangesAsync();
            await new TestProcedureBaselineMaterializer(db).MaterializeAsync(
                baseline16.Id, "cm", now.AddHours(3), CancellationToken.None);
            var carried = await db.BaselineTestProcedures.AsNoTracking()
                .Where(x => x.BaselineId == baseline16.Id).Select(x => x.RevisionId).ToListAsync();
            Assert.Equal(2, carried.Count);
            Assert.Contains(first00.Id, carried);
            Assert.Contains(second00.Id, carried);

            var release17 = new SoftwareRelease(project.Id, "1.7", false, release16.Id);
            var source17 = ApprovedRequest(project.Id, release17.Id, "SRCR-09502", now);
            var baseline17 = FrozenBaseline(project.Id, release17.Id, source17, "SW-95.20",
                now.AddHours(4), baseline16.Id);
            var requirementArtifact = new RequirementArtifact(project.Id,
                "SYSR-09502000", RequirementLevel.System, now.AddHours(4));
            var requirementRevision = new RequirementRevision(requirementArtifact.Id, 0,
                "The product shall verify the successor behavior.", "Successor verification input.",
                "Test", RequirementRevisionState.Active, source17.Id, baseline17.Id, now.AddHours(4));
            var requirementMember = new BaselineRequirementSelection(baseline17.Id,
                requirementArtifact.Id, requirementRevision.Id);
            var tcr = new TestChangeReview(project.Id, release17.Id, source17.Id,
                TestChangeReviewDiscipline.System, source17.DisplayNumber, now.AddHours(4));
            var impact = VerificationImpactItem.ForIntroducedRequirement(project.Id, release17.Id,
                source17.Id, tcr.Id, source17.RequirementChanges.First().Id,
                "SYSR-09502000.00", "Test", now.AddHours(4));
            impact.LinkRequirementRevision(requirementRevision.Id, now.AddHours(4));
            impact.Resolve("verification.engineer", VerificationImpactOutcome.NewProcedureRequired,
                "The successor requirement needs a new controlled procedure.", now.AddHours(4));
            tcr.RecordTestChangeRequired("verification.engineer", now.AddHours(4));
            tcr.WriteCase("verification.engineer", "Legacy successor procedure work", "Problem",
                "Analysis", "Solution", now.AddHours(4));
            tcr.AssignControlledNumber("SYSTCR-09502", now.AddHours(4));
            tcr.AddProcedureChange("verification.engineer", Change("SYSTP-095001", 1,
                TestProcedureChangeKind.Modify, "First procedure revised"), now.AddHours(4));
            tcr.AddProcedureChange("verification.engineer", Change("SYSTP-095002", 1,
                TestProcedureChangeKind.Retire, ""), now.AddHours(4));
            tcr.AddProcedureChange("verification.engineer", Change("SYSTP-095003", 0,
                TestProcedureChangeKind.Introduce, "New successor procedure",
                JsonSerializer.Serialize(new[] { requirementRevision.Id })), now.AddHours(4));
            tcr.Submit("verification.engineer", "test.lead", true, now.AddHours(4).AddMinutes(1));
            tcr.Approve("test.lead", "Reviewed.", now.AddHours(4).AddMinutes(2));
            db.AddRange(release17, source17, baseline17, requirementArtifact,
                requirementRevision, requirementMember, tcr, impact);
            await db.SaveChangesAsync();
            baseline17.SelectTestChangeRequest(tcr, "verification.lead", now.AddHours(4).AddMinutes(3));
            await db.SaveChangesAsync();

            await new TestProcedureBaselineMaterializer(db).MaterializeAsync(
                baseline17.Id, "cm", now.AddHours(5), CancellationToken.None);

            var predecessorStillExact = await db.BaselineTestProcedures.AsNoTracking()
                .Where(x => x.BaselineId == baseline16.Id).Select(x => x.RevisionId).ToListAsync();
            Assert.Contains(first00.Id, predecessorStillExact);
            Assert.Contains(second00.Id, predecessorStillExact);

            var finalMembers = await (from member in db.BaselineTestProcedures.AsNoTracking()
                                    join procedure in db.TestProcedures.AsNoTracking()
                                        on member.ProcedureId equals procedure.Id
                                    join revision in db.TestProcedureRevisions.AsNoTracking()
                                        on member.RevisionId equals revision.Id
                                    where member.BaselineId == baseline17.Id
                                    orderby procedure.BaseNumber
                                    select new { procedure.BaseNumber, revision.Revision, revision.SourceTestChangeRequestId })
                .ToListAsync();
            Assert.Equal(2, finalMembers.Count);
            Assert.Contains(finalMembers, x => x.BaseNumber == "SYSTP-095001" && x.Revision == 1
                && x.SourceTestChangeRequestId == tcr.Id);
            Assert.Contains(finalMembers, x => x.BaseNumber == "SYSTP-095003" && x.Revision == 0
                && x.SourceTestChangeRequestId == tcr.Id);
            Assert.DoesNotContain(finalMembers, x => x.BaseNumber == "SYSTP-095002");
            var retired = await db.TestProcedureRevisions.AsNoTracking()
                .SingleAsync(x => x.ProcedureId == second.Id && x.Revision == 1);
            Assert.Equal(TestProcedureState.Retired, retired.State);
        }
        finally { File.Delete(path); }
    }

    private static TestProcedureChangeDraft Change(string number, int revision,
        TestProcedureChangeKind kind, string title,
        string drivingRequirementRevisionIdsJson = "[]") =>
        new(number, revision, TestProcedureLevel.System, kind, title,
            kind == TestProcedureChangeKind.Retire ? "" : "Verify the successor behavior.",
            "Configured product.", kind == TestProcedureChangeKind.Retire ? "" : "1. Exercise the behavior.",
            kind == TestProcedureChangeKind.Retire ? "" : "Behavior is correct.",
            "Approved successor procedure disposition.", drivingRequirementRevisionIdsJson);

    private static TestProcedureRevision Revision(Guid procedureId, int revision,
        TestProcedureState state, DateTimeOffset now) => new(procedureId, revision,
        "Verify the legacy behavior.", "Configured product.", "1. Exercise the behavior.",
        "Behavior is correct.", state, "legacy.author", now);

    private static CandidateBaseline FrozenBaseline(Guid projectId, Guid releaseId,
        SystemChangeRequest source, string number, DateTimeOffset now, Guid? predecessorId = null)
    {
        var baseline = new CandidateBaseline(number, 0, projectId, releaseId, predecessorId,
            "Controlled baseline", "cm", now);
        baseline.Select(source, "cm", now);
        baseline.Freeze("cm", now.AddMinutes(1));
        baseline.MarkRequirementsMaterialized("cm", new string('a', 64), 1, now.AddMinutes(2));
        return baseline;
    }

    private static SystemChangeRequest ApprovedRequest(Guid projectId, Guid releaseId,
        string number, DateTimeOffset now)
    {
        var request = new SystemChangeRequest(number, 0, projectId, releaseId,
            "Controlled source", "Problem", "Analysis", "Solution", "author", now);
        request.AddRequirementChange("author", number.Replace("SRCR", "SYSR") + "00", 0,
            RequirementLevel.System, RequirementChangeKind.Introduce,
            "The product shall preserve controlled verification evidence.",
            "Configuration integrity.", "Test", now);
        request.SubmitForReview("author", [new ApproverSelection("reviewer", "Reviewer")], now);
        request.ApproveActiveStage("reviewer", now.AddMinutes(1));
        return request;
    }
}
