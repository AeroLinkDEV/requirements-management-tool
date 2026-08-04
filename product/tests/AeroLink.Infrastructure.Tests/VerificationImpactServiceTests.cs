using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

public sealed class VerificationImpactServiceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public async Task Approving_a_change_request_raises_verification_work_for_new_and_modified_requirements()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-vimpact-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            Guid changeRequestId, releaseId, projectId;
            await using (var setup = new AeroLinkDbContext(options))
            {
                await setup.Database.EnsureCreatedAsync();
                var program = new ProgramRecord("Verification Program", "VFP");
                var project = new ProjectRecord(program.Id, "Software", "Verification Software");
                var release = new SoftwareRelease(project.Id, "1.6", false);
                var scr = new SystemChangeRequest("SRCR-00010", 0, project.Id, release.Id, "Oceanic routing", "P", "A", "S", "author", Now);
                scr.AddRequirementChange("author", "SYSR-00000101", 0, RequirementLevel.System, RequirementChangeKind.Introduce,
                    "The FMS shall sequence oceanic waypoints.", "New capability", "Test", Now);
                scr.AddRequirementChange("author", "SYSR-00000102", 1, RequirementLevel.System, RequirementChangeKind.Modify,
                    "The FMS shall advance on the configured trigger.", "Clarified trigger", "Test", Now);
                scr.AddRequirementChange("author", "SYSR-00000103", 1, RequirementLevel.System, RequirementChangeKind.Retire,
                    "", "Superseded", "Test", Now);
                scr.AddRequirementChange("author", "SYSR-00000104", 0, RequirementLevel.System, RequirementChangeKind.Introduce,
                    "The FMS shall record oceanic entry time.", "New capability", "Analysis", Now);
                scr.SubmitForReview("author", [new("reviewer", "Reviewer")], Now);
                scr.ApproveActiveStage("reviewer", Now);
                setup.AddRange(program, project, release, scr);
                await setup.SaveChangesAsync();
                changeRequestId = scr.Id; releaseId = release.Id; projectId = project.Id;
            }

            await using (var act = new AeroLinkDbContext(options))
            {
                var scr = await act.SystemChangeRequests.Include(x => x.RequirementChanges).SingleAsync(x => x.Id == changeRequestId);
                var raised = await new VerificationImpactService(act).RaiseForApprovedChangeRequestAsync(scr, Now, default);
                await act.SaveChangesAsync();
                Assert.Equal(3, raised); // two introductions and one modification; retirement raises nothing here
            }

            await using (var assert = new AeroLinkDbContext(options))
            {
                var items = await assert.VerificationImpactItems.AsNoTracking().OrderBy(x => x.SubjectDisplayNumber).ToListAsync();
                Assert.Equal(3, items.Count);
                Assert.All(items, x => Assert.Equal(releaseId, x.ReleaseId));
                Assert.All(items, x => Assert.Equal(projectId, x.ProjectId));
                Assert.All(items, x => Assert.Equal(VerificationImpactState.Open, x.State));
                Assert.All(items, x => Assert.True(x.BlocksBaselineApproval));
                Assert.All(items, x => Assert.NotNull(x.RequirementChangeId));
                Assert.All(items, x => Assert.Null(x.RequirementRevisionId)); // no baseline materialised yet

                Assert.Equal(2, items.Count(x => x.Trigger == VerificationImpactTrigger.RequirementIntroduced));
                Assert.Single(items, x => x.Trigger == VerificationImpactTrigger.RequirementModified);
                Assert.DoesNotContain(items, x => x.SubjectDisplayNumber.StartsWith("SYSR-00000103"));

                // The author's declared method rides along as context for the verification engineer.
                Assert.Single(items, x => x.DeclaredVerificationMethod == "Analysis");
            }
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task Raising_is_idempotent_and_outstanding_work_follows_a_retargeted_change_request()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-vimpact-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            Guid changeRequestId, deferredReleaseId;
            await using (var setup = new AeroLinkDbContext(options))
            {
                await setup.Database.EnsureCreatedAsync();
                var program = new ProgramRecord("Verification Program", "VFP2");
                var project = new ProjectRecord(program.Id, "Software", "Verification Software");
                var release = new SoftwareRelease(project.Id, "1.6", false);
                var deferred = new SoftwareRelease(project.Id, "1.7", false);
                var scr = new SystemChangeRequest("SRCR-00011", 0, project.Id, release.Id, "Deferred work", "P", "A", "S", "author", Now);
                scr.AddRequirementChange("author", "SYSR-00000201", 0, RequirementLevel.System, RequirementChangeKind.Introduce,
                    "The FMS shall do the thing.", "New", "Test", Now);
                scr.SubmitForReview("author", [new("reviewer", "Reviewer")], Now);
                scr.ApproveActiveStage("reviewer", Now);
                setup.AddRange(program, project, release, deferred, scr);
                await setup.SaveChangesAsync();
                changeRequestId = scr.Id; deferredReleaseId = deferred.Id;
            }

            await using (var first = new AeroLinkDbContext(options))
            {
                var scr = await first.SystemChangeRequests.Include(x => x.RequirementChanges).SingleAsync(x => x.Id == changeRequestId);
                Assert.Equal(1, await new VerificationImpactService(first).RaiseForApprovedChangeRequestAsync(scr, Now, default));
                await first.SaveChangesAsync();
            }

            await using (var repeat = new AeroLinkDbContext(options))
            {
                // A retried approval must not duplicate the verification team's work.
                var scr = await repeat.SystemChangeRequests.Include(x => x.RequirementChanges).SingleAsync(x => x.Id == changeRequestId);
                Assert.Equal(0, await new VerificationImpactService(repeat).RaiseForApprovedChangeRequestAsync(scr, Now, default));
                await repeat.SaveChangesAsync();
                Assert.Equal(1, await repeat.VerificationImpactItems.CountAsync());
            }

            await using (var retarget = new AeroLinkDbContext(options))
            {
                var service = new VerificationImpactService(retarget);
                Assert.Equal(1, await service.RetargetAsync(changeRequestId, deferredReleaseId, Now.AddDays(1), default));
                await retarget.SaveChangesAsync();

                var outstanding = await service.OutstandingForReleaseAsync(deferredReleaseId, default);
                Assert.Single(outstanding);
                Assert.Equal(deferredReleaseId, outstanding[0].ReleaseId);
            }
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task Only_unresolved_items_hold_a_release_back()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-vimpact-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            Guid changeRequestId, releaseId;
            await using (var setup = new AeroLinkDbContext(options))
            {
                await setup.Database.EnsureCreatedAsync();
                var program = new ProgramRecord("Verification Program", "VFP3");
                var project = new ProjectRecord(program.Id, "Software", "Verification Software");
                var release = new SoftwareRelease(project.Id, "1.6", false);
                var scr = new SystemChangeRequest("SRCR-00012", 0, project.Id, release.Id, "Gate work", "P", "A", "S", "author", Now);
                scr.AddRequirementChange("author", "SYSR-00000301", 0, RequirementLevel.System, RequirementChangeKind.Introduce,
                    "First.", "New", "Test", Now);
                scr.AddRequirementChange("author", "SYSR-00000302", 0, RequirementLevel.System, RequirementChangeKind.Introduce,
                    "Second.", "New", "Analysis", Now);
                scr.SubmitForReview("author", [new("reviewer", "Reviewer")], Now);
                scr.ApproveActiveStage("reviewer", Now);
                setup.AddRange(program, project, release, scr);
                await setup.SaveChangesAsync();
                changeRequestId = scr.Id; releaseId = release.Id;
            }

            await using (var raise = new AeroLinkDbContext(options))
            {
                var scr = await raise.SystemChangeRequests.Include(x => x.RequirementChanges).SingleAsync(x => x.Id == changeRequestId);
                await new VerificationImpactService(raise).RaiseForApprovedChangeRequestAsync(scr, Now, default);
                await raise.SaveChangesAsync();
            }

            await using (var resolve = new AeroLinkDbContext(options))
            {
                var service = new VerificationImpactService(resolve);
                Assert.Equal(2, (await service.OutstandingForReleaseAsync(releaseId, default)).Count);

                var items = await resolve.VerificationImpactItems.OrderBy(x => x.SubjectDisplayNumber).ToListAsync();
                items[0].AssignToEngineer("test.lead", "test.engineer", Now);
                items[0].Resolve("test.engineer", VerificationImpactOutcome.NoTestRequired,
                    "Verified by an attributable analysis record.", Now.AddHours(1));
                await resolve.SaveChangesAsync();

                var remaining = await service.OutstandingForReleaseAsync(releaseId, default);
                Assert.Single(remaining);
                Assert.Equal("Analysis", remaining[0].DeclaredVerificationMethod);
            }
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task Work_is_still_raised_after_the_change_has_been_selected_into_a_candidate_baseline()
    {
        // Selecting an approved change moves it to SelectedForBaseline. A raise attempted at that point must
        // still work, or a retry after selection would silently drop the verification team's work.
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-vimpact-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            Guid changeRequestId;
            await using (var setup = new AeroLinkDbContext(options))
            {
                await setup.Database.EnsureCreatedAsync();
                var program = new ProgramRecord("Verification Program", "VFP4");
                var project = new ProjectRecord(program.Id, "Software", "Verification Software");
                var release = new SoftwareRelease(project.Id, "1.6", false);
                var scr = new SystemChangeRequest("SRCR-00013", 0, project.Id, release.Id, "Selected", "P", "A", "S", "author", Now);
                scr.AddRequirementChange("author", "SYSR-00000401", 0, RequirementLevel.System, RequirementChangeKind.Introduce,
                    "Selected requirement.", "New", "Test", Now);
                scr.SubmitForReview("author", [new("reviewer", "Reviewer")], Now);
                scr.ApproveActiveStage("reviewer", Now);
                var baseline = new AeroLink.Domain.Baselines.CandidateBaseline("SW-01.30", 0, project.Id, release.Id, null, "Candidate", "cm", Now);
                baseline.Select(scr, "cm", Now);
                Assert.Equal(ChangeRequestState.SelectedForBaseline, scr.State);
                setup.AddRange(program, project, release, scr, baseline);
                await setup.SaveChangesAsync();
                changeRequestId = scr.Id;
            }

            await using (var raise = new AeroLinkDbContext(options))
            {
                var scr = await raise.SystemChangeRequests.Include(x => x.RequirementChanges).SingleAsync(x => x.Id == changeRequestId);
                Assert.Equal(1, await new VerificationImpactService(raise).RaiseForApprovedChangeRequestAsync(scr, Now, default));
                await raise.SaveChangesAsync();
                Assert.Equal(1, await raise.VerificationImpactItems.CountAsync());
            }
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (File.Exists(path)) File.Delete(path); }
    }
}
