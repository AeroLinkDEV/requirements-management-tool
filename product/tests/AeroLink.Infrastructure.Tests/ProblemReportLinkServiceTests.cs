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
    public async Task Linking_or_removing_a_draft_correction_does_not_change_problem_report_state()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-pr-link-reconcile-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            await using var db = new AeroLinkDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var now = DateTimeOffset.UtcNow;
            var program = new ProgramRecord("PR link reconciliation", "PRLR");
            var project = new ProjectRecord(program.Id, "FMS", "FMS");
            var release = new SoftwareRelease(project.Id, "1.6", false);
            var report = new ProblemReport(project.Id, "PR-00458", "Automatic implementation",
                "The draft correction is the only implementation basis.", "", "engineer", now,
                targetReleaseId: release.Id);
            report.ReadyForSccb("engineer", now.AddMinutes(1));
            report.OpenBySccb("sccb", now.AddMinutes(2));
            var change = new SystemChangeRequest("SRCR-00458", 0, project.Id, release.Id,
                "Draft correction", "P", "A", "S", "engineer", now);
            db.AddRange(program, project, release, report, change);
            db.ProblemReportLinks.Add(new ProblemReportLink(report.Id, "Release", release.Id,
                ProblemReportRelationshipPolicy.BuildScope, "engineer", now));
            await db.SaveChangesAsync();

            var service = new ProblemReportLinkService(db);
            await service.ReplaceDraftChangeRequestLinksAsync(change, [report.Id], "engineer", now.AddMinutes(3), default);
            await db.SaveChangesAsync();
            Assert.Equal(ProblemReportState.Open, report.State);

            await service.ReplaceDraftChangeRequestLinksAsync(change, [], "engineer", now.AddMinutes(4), default);
            await db.SaveChangesAsync();

            Assert.Equal(ProblemReportState.Open, report.State);
            Assert.Empty(await db.ProblemReportRevisions.Where(item => item.ProblemReportId == report.Id)
                .ToListAsync());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Automatic_reconciliation_preserves_other_manual_substantive_and_approved_sources()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-pr-link-sources-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            await using var db = new AeroLinkDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var now = DateTimeOffset.UtcNow;
            var program = new ProgramRecord("PR implementation sources", "PRIS");
            var project = new ProjectRecord(program.Id, "FMS", "FMS");
            var release = new SoftwareRelease(project.Id, "1.6", false);
            ProblemReport OpenReport(string number, string title)
            {
                var item = new ProblemReport(project.Id, number, title, "Controlled anomaly.", "", "engineer",
                    now, targetReleaseId: release.Id);
                item.ReadyForSccb("engineer", now.AddMinutes(1));
                item.OpenBySccb("sccb", now.AddMinutes(2));
                return item;
            }
            SystemChangeRequest Change(string number, string title) => new(number, 0, project.Id, release.Id,
                title, "P", "A", "S", "engineer", now);

            var multiple = OpenReport("PR-04581", "Multiple corrections");
            var substantive = OpenReport("PR-04582", "Substantive investigation");
            var manual = OpenReport("PR-04583", "Manual implementation");
            manual.BeginImplementation("engineer", now.AddMinutes(3));
            var approved = OpenReport("PR-04584", "Approved correction");
            var first = Change("SRCR-04581", "First draft correction");
            var second = Change("SRCR-04582", "Second draft correction");
            var substantiveChange = Change("SRCR-04583", "Investigated draft correction");
            var manualChange = Change("SRCR-04584", "Manual-state draft correction");
            var draftWithApproval = Change("SRCR-04585", "Draft beside approved correction");
            var approvedChange = Change("SRCR-04586", "Approved correction source");
            db.AddRange(program, project, release, multiple, substantive, manual, approved, first, second,
                substantiveChange, manualChange, draftWithApproval, approvedChange);
            foreach (var report in new[] { multiple, substantive, manual, approved })
                db.ProblemReportLinks.Add(new ProblemReportLink(report.Id, "Release", release.Id,
                    ProblemReportRelationshipPolicy.BuildScope, "engineer", now));
            db.ProblemReportRevisions.Add(new ProblemReportRevision(manual.Id, manual.Revision,
                "ImplementationStarted", "engineer", manual.CanonicalHash(), manual.CanonicalSnapshot(),
                now.AddMinutes(3)));
            await db.SaveChangesAsync();
            var service = new ProblemReportLinkService(db);

            await service.ReplaceDraftChangeRequestLinksAsync(first, [multiple.Id], "engineer", now.AddMinutes(4), default);
            await service.ReplaceDraftChangeRequestLinksAsync(second, [multiple.Id], "engineer", now.AddMinutes(5), default);
            await db.SaveChangesAsync();
            await service.ReplaceDraftChangeRequestLinksAsync(first, [], "engineer", now.AddMinutes(6), default);
            await db.SaveChangesAsync();
            Assert.Equal(ProblemReportState.Open, multiple.State);
            var versionBeforeNoOp = multiple.Version;
            var historyBeforeNoOp = await db.ProblemReportRevisions.CountAsync(item => item.ProblemReportId == multiple.Id);
            await service.ReplaceDraftChangeRequestLinksAsync(second, [multiple.Id], "engineer", now.AddMinutes(7), default);
            await db.SaveChangesAsync();
            Assert.Equal(versionBeforeNoOp, multiple.Version);
            Assert.Equal(historyBeforeNoOp,
                await db.ProblemReportRevisions.CountAsync(item => item.ProblemReportId == multiple.Id));

            await service.ReplaceDraftChangeRequestLinksAsync(substantiveChange, [substantive.Id], "engineer", now.AddMinutes(8), default);
            await db.SaveChangesAsync();
            substantive.BeginInvestigation("engineer", "Confirmed analysis", "Root cause", "Effect", "", now.AddMinutes(9));
            db.ProblemReportRevisions.Add(new ProblemReportRevision(substantive.Id, substantive.Revision,
                "InvestigationRecorded", "engineer", substantive.CanonicalHash(), substantive.CanonicalSnapshot(),
                now.AddMinutes(9)));
            await db.SaveChangesAsync();
            await service.ReplaceDraftChangeRequestLinksAsync(substantiveChange, [], "engineer", now.AddMinutes(10), default);
            await db.SaveChangesAsync();
            Assert.Equal(ProblemReportState.Implementing, substantive.State);

            await service.ReplaceDraftChangeRequestLinksAsync(manualChange, [manual.Id], "engineer", now.AddMinutes(11), default);
            await db.SaveChangesAsync();
            await service.ReplaceDraftChangeRequestLinksAsync(manualChange, [], "engineer", now.AddMinutes(12), default);
            await db.SaveChangesAsync();
            Assert.Equal(ProblemReportState.Implementing, manual.State);

            await service.ReplaceDraftChangeRequestLinksAsync(draftWithApproval, [approved.Id], "engineer", now.AddMinutes(13), default);
            db.ProblemReportLinks.Add(ProblemReportRelationshipPolicy.CreateControlled(approved.Id, "ChangeRequest",
                approvedChange.Id, ProblemReportRelationshipPolicy.ApprovedCorrectiveAction,
                ProblemReportRelationshipProducer.ChangeRequestWorkflow, "approver", now.AddMinutes(14)));
            await db.SaveChangesAsync();
            await service.ReplaceDraftChangeRequestLinksAsync(draftWithApproval, [], "engineer", now.AddMinutes(15), default);
            await db.SaveChangesAsync();
            Assert.Equal(ProblemReportState.Open, approved.State);
            Assert.Contains(await db.ProblemReportLinks.Where(item => item.ProblemReportId == approved.Id).ToListAsync(),
                item => item.Relationship == ProblemReportRelationshipPolicy.ApprovedCorrectiveAction);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }

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
            await service.LinkChangeRequestAsync(scr.Id, scr.DisplayNumber, [report.Id], "software.engineer", now, default);
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
    public async Task A_controlled_corrective_link_change_invalidates_the_pending_closure_candidate_without_changing_lifecycle_state()
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
                "{}", new string('c', 64), new string('d', 64), "engineer", now.AddMinutes(5),
                reportSnapshotSchemaVersion: 1);
            var change = new SystemChangeRequest("SRCR-00461", 0, project.Id, release.Id,
                "Correct closure link", "P", "A", "S", "change.engineer", now);
            db.AddRange(program, project, release, report, candidate, change);
            await db.SaveChangesAsync();

            await new ProblemReportLinkService(db).LinkChangeRequestAsync(change.Id, change.DisplayNumber, [report.Id],
                "change.engineer", now.AddMinutes(6), default);
            await db.SaveChangesAsync();

            Assert.Equal(ProblemReportState.WaitingForSqaToClose, report.State);
            Assert.Equal(executionId, report.ResolutionVerificationExecutionId);
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

    [Fact]
    public async Task A_controlled_relationship_cannot_be_added_after_closure_until_reopen()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-pr-frozen-links-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            await using var db = new AeroLinkDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var now = DateTimeOffset.UtcNow;
            var program = new ProgramRecord("Frozen PR links", "FPRL");
            var project = new ProjectRecord(program.Id, "FMS", "FMS");
            var release = new SoftwareRelease(project.Id, "1.6", false);
            var report = new ProblemReport(project.Id, "PR-00451", "Frozen closure",
                "Closed relationships must not drift.", "", "engineer", now, targetReleaseId: release.Id);
            report.ReadyForSccb("engineer", now.AddMinutes(1));
            report.OpenBySccb("sccb", now.AddMinutes(2));
            report.BeginInvestigation("engineer", "Analysis", "Cause", "Effect", "", now.AddMinutes(3));
            report.ProposeResolution("engineer", "Correction", now.AddMinutes(4));
            report.RecordResolutionVerification("engineer", Guid.NewGuid(), now.AddMinutes(5));
            report.ApproveClosure("quality", Guid.NewGuid(), now.AddMinutes(6));
            var change = new SystemChangeRequest("SRCR-00451", 0, project.Id, release.Id,
                "Late correction", "P", "A", "S", "change.engineer", now);
            db.AddRange(program, project, release, report, change); await db.SaveChangesAsync();

            var version = report.Version;
            var error = await Assert.ThrowsAsync<DomainException>(() =>
                new ProblemReportLinkService(db).LinkChangeRequestAsync(change.Id, change.DisplayNumber, [report.Id],
                    "change.engineer", now.AddMinutes(7), default));
            Assert.Contains("closed or dispositioned", error.Message);
            Assert.Equal(version, report.Version);
            Assert.Empty(db.ChangeTracker.Entries<ProblemReportLink>());
            Assert.Empty(await db.ProblemReportLinks.AsNoTracking().ToListAsync());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
