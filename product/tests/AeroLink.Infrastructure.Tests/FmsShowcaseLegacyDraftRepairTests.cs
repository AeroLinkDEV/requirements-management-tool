using System.Text.Json;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

[Collection(ShowcaseCollection.Name)]
public sealed class FmsShowcaseLegacyDraftRepairTests(ShowcaseDatabaseFixture showcase)
{
    [Theory]
    [InlineData("obsolete", true)]
    [InlineData("authored", false)]
    [InlineData("approved", false)]
    [InlineData("coverage-reference", false)]
    [InlineData("selected", false)]
    [InlineData("discussion", false)]
    [InlineData("attachment", false)]
    [InlineData("edit-session", false)]
    public async Task Upgrade_archives_only_the_exact_unreferenced_legacy_draft_and_is_idempotent(string scenario, bool archived)
    {
        using var database = showcase.Create();
        await using var db = database.Context();
        var projectId = showcase.Summary.ProjectId;
        var procedure = await db.TestProcedures.SingleAsync(x => x.ProjectId == projectId && x.BaseNumber == "SYSTP-000001");
        var legacy = new TestProcedureRevision(procedure.Id, 1,
            "Verify oceanic round-robin waypoint sequencing against the revised FMS 1.6 behavior.",
            "Load the FMS 1.6 candidate software and the approved navigation database.",
            "Initialize oceanic mode, stimulate the revised sequencing inputs, and record each observable output.",
            "Every observed output meets the linked requirement acceptance criteria.",
            scenario == "approved" ? TestProcedureState.Approved : TestProcedureState.Draft,
            scenario == "authored" ? "operator.author" : "test.author", new DateTimeOffset(2024, 11, 18, 9, 30, 0, TimeSpan.Zero));
        db.Add(legacy);
        var now = DateTimeOffset.UtcNow;
        if (scenario == "discussion")
            db.ArtifactComments.Add(new ArtifactComment(projectId, "TestProcedure", procedure.Id, legacy.Id,
                null, "Operator discussion must retain its exact revision.", "[]", "test.author", now));
        if (scenario == "attachment")
            db.ControlledAttachments.Add(new ControlledAttachment(projectId, "TestProcedure", procedure.Id, legacy.Id,
                Guid.NewGuid(), 1, "Operator evidence", "Supporting authoring evidence", "fixture.txt", "text/plain",
                4, new string('a', 64), "owned-fixture-metadata", null, "test.author", now));
        if (scenario == "edit-session")
            db.ArtifactEditSessions.Add(new ArtifactEditSession(projectId, "TestProcedure", procedure.Id, legacy.Id,
                new string('a', 64), "{}", "test.author", now));
        if (scenario == "coverage-reference")
        {
            var requirement = await (from coverage in db.TestCoverage
                join revision in db.TestProcedureRevisions on coverage.ProcedureRevisionId equals revision.Id
                where revision.ProcedureId == procedure.Id && revision.State == TestProcedureState.Approved
                select coverage.RequirementRevisionId).FirstAsync();
            db.Add(new TestRequirementCoverage(legacy.Id, requirement));
        }
        if (scenario == "selected")
        {
            var member = await db.BaselineTestProcedures.SingleAsync(x => x.BaselineId == showcase.Summary.ReleasedBaselineId
                && x.ProcedureId == procedure.Id);
            db.Entry(member).Property(x => x.RevisionId).CurrentValue = legacy.Id;
        }
        var marker = await db.ShowcaseUpgradeSteps.SingleAsync(x => x.ProgramId == showcase.Summary.ProgramId
            && x.StepKey == "legacy-gap-draft-archive");
        db.Remove(marker);
        await db.SaveChangesAsync();
        var originalJson = JsonSerializer.Serialize(legacy);
        var discussionJson = JsonSerializer.Serialize(await db.ArtifactComments.AsNoTracking()
            .Where(x => x.RevisionId == legacy.Id).OrderBy(x => x.Id).ToListAsync());
        var approvedBefore = await db.TestProcedureRevisions.AsNoTracking().Where(x => x.State == TestProcedureState.Approved)
            .OrderBy(x => x.Id).ToListAsync();
        var approvedJson = JsonSerializer.Serialize(approvedBefore);
        var seeder = new FmsShowcaseSeeder(db);

        await seeder.UpgradeAsync(showcase.Summary.ProgramId);
        Assert.Equal(!archived, await db.TestProcedureRevisions.AnyAsync(x => x.Id == legacy.Id));
        var audit = await db.SecurityAuditEvents.AsNoTracking().Where(x => x.EventType == "ShowcaseLegacyDraftArchived"
            && x.Target == legacy.Id.ToString()).ToListAsync();
        if (archived)
        {
            var record = Assert.Single(audit);
            using var payload = JsonDocument.Parse(record.Detail);
            Assert.Equal(originalJson, payload.RootElement.GetProperty("Revision").GetRawText());
            Assert.Equal("showcase.upgrade", record.ActorId);
        }
        else Assert.Empty(audit);
        Assert.Equal(discussionJson, JsonSerializer.Serialize(await db.ArtifactComments.AsNoTracking()
            .Where(x => x.RevisionId == legacy.Id).OrderBy(x => x.Id).ToListAsync()));
        Assert.Equal(approvedJson, JsonSerializer.Serialize(await db.TestProcedureRevisions.AsNoTracking()
            .Where(x => x.State == TestProcedureState.Approved).OrderBy(x => x.Id).ToListAsync()));
        Assert.Empty(await seeder.UpgradeAsync(showcase.Summary.ProgramId));
        Assert.Equal(audit.Count, await db.SecurityAuditEvents.CountAsync(x => x.EventType == "ShowcaseLegacyDraftArchived"
            && x.Target == legacy.Id.ToString()));
    }
}
