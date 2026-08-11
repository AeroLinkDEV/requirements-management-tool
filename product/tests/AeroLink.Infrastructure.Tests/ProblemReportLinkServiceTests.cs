using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

public sealed class ProblemReportLinkServiceTests
{
    [Fact]
    public void Relationship_registry_assigns_one_artifact_type_and_producer_to_every_semantic()
    {
        Assert.Equal(ProblemReportRelationshipPolicy.Definitions.Count,
            ProblemReportRelationshipPolicy.Definitions.Select(item => item.Relationship).Distinct(StringComparer.Ordinal).Count());
        Assert.All(ProblemReportRelationshipPolicy.Definitions, item =>
        {
            Assert.False(string.IsNullOrWhiteSpace(item.ArtifactType));
            Assert.True(ProblemReportRelationshipPolicy.Matches(item.Relationship, item.ArtifactType));
        });
        Assert.Equal(7, ProblemReportRelationshipPolicy.Definitions.Count(item => item.IsControlled));
        Assert.True(ProblemReportRelationshipPolicy.IsGenericContextPair("Requirement", ProblemReportRelationshipPolicy.AffectedRequirement));
        Assert.False(ProblemReportRelationshipPolicy.IsGenericContextPair("ChangeRequest", ProblemReportRelationshipPolicy.ApprovedCorrectiveAction));
        Assert.Throws<DomainException>(() => ProblemReportRelationshipPolicy.CreateControlled(Guid.NewGuid(),
            "TestExecution", Guid.NewGuid(), ProblemReportRelationshipPolicy.ResolutionVerification,
            ProblemReportRelationshipProducer.ChangeRequestWorkflow, "actor", DateTimeOffset.UtcNow));
        Assert.Throws<DomainException>(() => ProblemReportRelationshipPolicy.CreateGenericContext(Guid.NewGuid(),
            "ChangeRequest", Guid.NewGuid(), ProblemReportRelationshipPolicy.ApprovedCorrectiveAction,
            "actor", DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task A_build_scoped_pr_flows_from_proposed_change_to_tcr_and_approved_corrective_action()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-pr-links-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            await using var db = new AeroLinkDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var now = DateTimeOffset.UtcNow;
            var program = new ProgramRecord("PR traceability", "PRTR");
            var project = new ProjectRecord(program.Id, "FMS", "FMS");
            var release = new SoftwareRelease(project.Id, "1.6", false);
            var otherRelease = new SoftwareRelease(project.Id, "1.7", false);
            var report = new ProblemReport(project.Id, "PR-00001", "Position disagreement",
                "Sources disagree during approach.", "", "quality.engineer", now);
            var scr = new SystemChangeRequest("LLRCR-00001", 0, project.Id, release.Id,
                "Correct source selection", "P", "A", "S", "software.engineer", now,
                ChangeRequestType.Software, softwareLevel: RequirementLevel.LowLevel);
            scr.AddRequirementChange("software.engineer", "LLR-000001", 1, RequirementLevel.LowLevel,
                RequirementChangeKind.Modify, "The software shall reject a stale position source.",
                "Correct the reported disagreement.", "Test", now);
            var replacement = new ProblemReport(project.Id, "PR-00002", "Replacement trace",
                "The original selection was incorrect.", "", "quality.engineer", now);
            db.AddRange(program, project, release, otherRelease, report, replacement, scr);
            db.ProblemReportLinks.Add(new ProblemReportLink(report.Id, "Release", release.Id,
                "BuildScope", "quality.engineer", now));
            db.ProblemReportLinks.Add(new ProblemReportLink(replacement.Id, "Release", release.Id,
                "BuildScope", "quality.engineer", now));
            await db.SaveChangesAsync();

            var service = new ProblemReportLinkService(db);
            Assert.Null(await service.ValidateSelectionAsync(project.Id, release.Id, [report.Id], default));
            Assert.NotNull(await service.ValidateSelectionAsync(project.Id, otherRelease.Id, [report.Id], default));
            await service.LinkChangeRequestAsync(scr.Id, [report.Id], "software.engineer", now, default);
            await db.SaveChangesAsync();
            await service.ReplaceDraftChangeRequestLinksAsync(scr, [replacement.Id], "software.engineer", now, default);
            await db.SaveChangesAsync();
            Assert.DoesNotContain(await db.ProblemReportLinks.ToListAsync(), x => x.ArtifactType == "ChangeRequest"
                && x.ArtifactId == scr.Id && x.ProblemReportId == report.Id);
            await service.ReplaceDraftChangeRequestLinksAsync(scr, [report.Id], "software.engineer", now, default);
            scr.SubmitForReview("software.engineer", [new("reviewer", "Reviewer")], now);
            await db.SaveChangesAsync();
            scr.ApproveActiveStage("reviewer", now);
            await new VerificationImpactService(db, service).RaiseForApprovedChangeRequestAsync(
                scr, now, default, "reviewer");
            await service.RecordApprovedCorrectiveActionsAsync(scr, "reviewer", now, default);
            await db.SaveChangesAsync();
            var tcr = await db.TestChangeReviews.SingleAsync();

            var links = await db.ProblemReportLinks.AsNoTracking()
                .Where(x => x.ProblemReportId == report.Id).ToListAsync();
            Assert.Contains(links, x => x.ArtifactType == "ChangeRequest"
                && x.ArtifactId == scr.Id && x.Relationship == "ProposedCorrectiveAction");
            Assert.Contains(links, x => x.ArtifactType == "ChangeRequest"
                && x.ArtifactId == scr.Id && x.Relationship == "ApprovedCorrectiveAction");
            Assert.Contains(links, x => x.ArtifactType == "TestChangeRequest"
                && x.ArtifactId == tcr.Id && x.Relationship == "VerificationForProblem"
                && x.AddedBy == "reviewer");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task A_controlled_corrective_link_change_invalidates_the_pending_closure_candidate_atomically()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-pr-closure-link-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            await using var db = new AeroLinkDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var now = DateTimeOffset.UtcNow;
            var program = new ProgramRecord("PR closure links", "PRCL");
            var project = new ProjectRecord(program.Id, "FMS", "FMS");
            var release = new SoftwareRelease(project.Id, "1.6", false);
            var report = new ProblemReport(project.Id, "PR-00461", "Closure link basis",
                "The corrective link set must stay exact.", "", "engineer", now, targetReleaseId: release.Id);
            report.ReadyForSccb("engineer", now.AddMinutes(1));
            report.OpenBySccb("sccb", now.AddMinutes(2));
            report.BeginInvestigation("engineer", "Analysis", "Cause", "Effect", "", now.AddMinutes(3));
            report.ProposeResolution("engineer", "Correct it", now.AddMinutes(4));
            var executionId = Guid.NewGuid();
            report.RecordResolutionVerification("engineer", executionId, now.AddMinutes(5));
            var candidate = new ProblemReportClosureCandidate(report.Id, report.Revision, 1, 1,
                report.Version, "{}", new string('a', 64), executionId, "{}", new string('b', 64),
                "{}", new string('c', 64), new string('d', 64), "engineer", now.AddMinutes(5));
            var change = new SystemChangeRequest("SRCR-00461", 0, project.Id, release.Id,
                "Correct closure link", "P", "A", "S", "change.engineer", now);
            db.AddRange(program, project, release, report, candidate, change);
            await db.SaveChangesAsync();

            await new ProblemReportLinkService(db).LinkChangeRequestAsync(change.Id, [report.Id],
                "change.engineer", now.AddMinutes(6), default);
            await db.SaveChangesAsync();

            Assert.Equal(ProblemReportState.Verifying, report.State);
            Assert.Null(report.ResolutionVerificationExecutionId);
            Assert.Equal(ProblemReportClosureCandidateState.Invalidated, candidate.State);
            Assert.Equal("ProposedCorrectiveActionLinked", candidate.InvalidationReason);
            Assert.Single(await db.ProblemReportRevisions.Where(item => item.ProblemReportId == report.Id
                && item.EventType == "ClosureVerificationInvalidatedByChange").ToListAsync());
            Assert.Contains(await db.ProblemReportLinks.Where(item => item.ProblemReportId == report.Id).ToListAsync(),
                item => item.Relationship == ProblemReportRelationshipPolicy.ProposedCorrectiveAction);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
