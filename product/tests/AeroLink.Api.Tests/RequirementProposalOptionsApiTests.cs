using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

public sealed class RequirementProposalOptionsApiTests
{
    [Fact]
    public async Task Options_use_the_selected_build_revision_and_existing_draft_proposal_is_idempotent()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var seeded = await SeedAsync(factory);
        await SignInAsync(client);

        using var optionsResponse = await client.GetAsync(
            $"/api/enterprise-requirements/{seeded.RequirementId}/propose-options?targetReleaseId={seeded.ReleaseId}");
        Assert.Equal(HttpStatusCode.OK, optionsResponse.StatusCode);
        var options = await optionsResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(seeded.SelectedRevisionId, options.GetProperty("requirement").GetProperty("revisionId").GetGuid());
        Assert.Equal(seeded.SelectedRevision, options.GetProperty("requirement").GetProperty("revision").GetInt32());

        var drafts = options.GetProperty("drafts").EnumerateArray().ToList();
        var eligible = Assert.Single(drafts, x => x.GetProperty("id").GetGuid() == seeded.DraftId);
        Assert.True(eligible.GetProperty("eligible").GetBoolean());
        var wrongBuild = Assert.Single(drafts, x => x.GetProperty("id").GetGuid() == seeded.WrongBuildDraftId);
        Assert.False(wrongBuild.GetProperty("eligible").GetBoolean());
        Assert.Contains("different build", wrongBuild.GetProperty("reason").GetString(), StringComparison.OrdinalIgnoreCase);

        using var proposal = await client.PostAsJsonAsync(
            $"/api/enterprise-requirements/{seeded.RequirementId}/propose",
            new
            {
                targetReleaseId = seeded.ReleaseId,
                kind = "Modify",
                existingScrId = seeded.DraftId,
                requirementRevisionId = seeded.SelectedRevisionId,
                expectedVersion = seeded.DraftVersion,
            });
        Assert.Equal(HttpStatusCode.Created, proposal.StatusCode);
        var created = await proposal.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(seeded.SelectedRevisionId, created.GetProperty("requirementRevisionId").GetGuid());
        Assert.False(created.TryGetProperty("duplicate", out _));
        var proposalId = created.GetProperty("proposalId").GetGuid();
        var versionAfterCreate = created.GetProperty("version").GetInt64();
        Assert.True(versionAfterCreate > seeded.DraftVersion);

        // The browser can retry when the first response was lost. The server opens the exact proposal instead
        // of reporting a duplicate or stale-version failure, while retaining the original expected version.
        using var retry = await client.PostAsJsonAsync(
            $"/api/enterprise-requirements/{seeded.RequirementId}/propose",
            new
            {
                targetReleaseId = seeded.ReleaseId,
                kind = "Modify",
                existingScrId = seeded.DraftId,
                requirementRevisionId = seeded.SelectedRevisionId,
                expectedVersion = seeded.DraftVersion,
            });
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        var retried = await retry.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(proposalId, retried.GetProperty("proposalId").GetGuid());
        Assert.True(retried.GetProperty("duplicate").GetBoolean());

        using var refreshedOptionsResponse = await client.GetAsync(
            $"/api/enterprise-requirements/{seeded.RequirementId}/propose-options?targetReleaseId={seeded.ReleaseId}");
        var refreshedOptions = await refreshedOptionsResponse.Content.ReadFromJsonAsync<JsonElement>();
        var duplicateDraft = Assert.Single(refreshedOptions.GetProperty("drafts").EnumerateArray(),
            x => x.GetProperty("id").GetGuid() == seeded.DraftId);
        Assert.False(duplicateDraft.GetProperty("eligible").GetBoolean());
        Assert.Equal(proposalId, duplicateDraft.GetProperty("existingProposalId").GetGuid());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var requirement = await db.Requirements.AsNoTracking().SingleAsync(x => x.Id == seeded.RequirementId);
        var revisions = await db.RequirementRevisions.AsNoTracking().Where(x => x.ArtifactId == requirement.Id)
            .OrderBy(x => x.Revision).ToListAsync();
        Assert.Equal(2, revisions.Count);
        Assert.Equal(seeded.SelectedRevisionId, revisions[0].Id);
        Assert.DoesNotContain(revisions, x => x.SourceChangeRequestId == seeded.DraftId);
        Assert.Equal(1, await db.RequirementChanges.CountAsync(x => x.ChangeRequestId == seeded.DraftId
            && x.BaseNumber == requirement.BaseNumber));
    }

    [Fact]
    public async Task Existing_draft_with_active_checkout_is_ineligible_even_for_the_checkout_owner()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var seeded = await SeedAsync(factory);
        await SignInAsync(client);

        using var checkout = await client.PostAsJsonAsync("/api/controlled-editing/checkout", new
        {
            artifactType = "ChangeRequest",
            artifactId = seeded.DraftId,
            leaseMinutes = 15,
        });
        Assert.Equal(HttpStatusCode.Created, checkout.StatusCode);

        using var proposal = await client.PostAsJsonAsync(
            $"/api/enterprise-requirements/{seeded.RequirementId}/propose",
            new
            {
                targetReleaseId = seeded.ReleaseId,
                kind = "Modify",
                existingScrId = seeded.DraftId,
                requirementRevisionId = seeded.SelectedRevisionId,
                expectedVersion = seeded.DraftVersion,
            });
        Assert.Equal(HttpStatusCode.Conflict, proposal.StatusCode);
        var body = await proposal.Content.ReadAsStringAsync();
        Assert.Contains("active_edit_session", body);
    }

    [Fact]
    public async Task Existing_draft_requires_the_exact_current_requirement_revision()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var seeded = await SeedAsync(factory);
        await SignInAsync(client);

        using var proposal = await client.PostAsJsonAsync(
            $"/api/enterprise-requirements/{seeded.RequirementId}/propose",
            new
            {
                targetReleaseId = seeded.ReleaseId,
                kind = "Modify",
                existingScrId = seeded.DraftId,
                expectedVersion = seeded.DraftVersion,
            });
        Assert.Equal(HttpStatusCode.Conflict, proposal.StatusCode);
        Assert.Contains("stale_requirement_revision", await proposal.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Existing_draft_rejects_a_stale_expected_version_after_the_draft_changes()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var seeded = await SeedAsync(factory);
        await SignInAsync(client);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            await db.SystemChangeRequests.Where(x => x.Id == seeded.DraftId)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.Version, x => x.Version + 1));
        }

        using var proposal = await PostExistingAsync(client, seeded);
        Assert.Equal(HttpStatusCode.Conflict, proposal.StatusCode);
        var body = await proposal.Content.ReadAsStringAsync();
        Assert.Contains("stale_version", body);
        Assert.Contains("currentVersion", body);

        using var scopeAfter = factory.Services.CreateScope();
        var dbAfter = scopeAfter.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        Assert.Equal(0, await dbAfter.RequirementChanges.CountAsync(x => x.ChangeRequestId == seeded.DraftId));
    }

    [Fact]
    public async Task Existing_proposal_revalidates_draft_lifecycle_and_released_build_truth()
    {
        using (var factory = new AeroLinkApiFactory())
        {
            using var client = factory.CreateClient();
            var seeded = await SeedAsync(factory);
            await SignInAsync(client);
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
                await db.SystemChangeRequests.Where(x => x.Id == seeded.DraftId)
                    .ExecuteUpdateAsync(update => update.SetProperty(x => x.State, ChangeRequestState.InReview));
            }

            using var proposal = await PostExistingAsync(client, seeded);
            Assert.Equal(HttpStatusCode.BadRequest, proposal.StatusCode);
            var body = await proposal.Content.ReadAsStringAsync();
            Assert.Contains("draft_required", body);
        }

        using (var factory = new AeroLinkApiFactory())
        {
            using var client = factory.CreateClient();
            var seeded = await SeedAsync(factory);
            await SignInAsync(client);
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
                await db.Releases.Where(x => x.Id == seeded.ReleaseId)
                    .ExecuteUpdateAsync(update => update.SetProperty(x => x.IsReleased, true));
            }

            using var proposal = await PostExistingAsync(client, seeded);
            Assert.Equal(HttpStatusCode.BadRequest, proposal.StatusCode);
            var body = await proposal.Content.ReadAsStringAsync();
            Assert.Contains("released_build_read_only", body);
        }
    }

    [Fact]
    public async Task Existing_retire_or_stale_modify_proposals_are_not_advertised_as_reopenable()
    {
        foreach (var (kind, revision) in new[]
        {
            (RequirementChangeKind.Retire, 1),
            (RequirementChangeKind.Modify, 7),
        })
        {
            using var factory = new AeroLinkApiFactory();
            using var client = factory.CreateClient();
            var seeded = await SeedAsync(factory);
            var version = await AddExistingProposalAsync(factory, seeded, kind, revision);
            await SignInAsync(client);

            using var optionsResponse = await client.GetAsync(
                $"/api/enterprise-requirements/{seeded.RequirementId}/propose-options?targetReleaseId={seeded.ReleaseId}");
            Assert.Equal(HttpStatusCode.OK, optionsResponse.StatusCode);
            var options = await optionsResponse.Content.ReadFromJsonAsync<JsonElement>();
            var row = Assert.Single(options.GetProperty("drafts").EnumerateArray(),
                x => x.GetProperty("id").GetGuid() == seeded.DraftId);
            Assert.False(row.GetProperty("eligible").GetBoolean());
            Assert.True(row.GetProperty("existingProposalId").ValueKind is JsonValueKind.Null);
            Assert.Contains("non-reopenable", row.GetProperty("reason").GetString(), StringComparison.OrdinalIgnoreCase);

            using var proposal = await client.PostAsJsonAsync(
                $"/api/enterprise-requirements/{seeded.RequirementId}/propose",
                new
                {
                    targetReleaseId = seeded.ReleaseId,
                    kind = "Modify",
                    existingScrId = seeded.DraftId,
                    requirementRevisionId = seeded.SelectedRevisionId,
                    expectedVersion = version,
                });
            Assert.Equal(HttpStatusCode.Conflict, proposal.StatusCode);
            Assert.Contains("duplicate_requirement_proposal", await proposal.Content.ReadAsStringAsync());
        }
    }

    private static async Task<Seeded> SeedAsync(AeroLinkApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;
        var program = new ProgramRecord("Requirement Proposal Program", "RPP");
        var project = new ProjectRecord(program.Id, "Requirements Proposal Project", "Requirements Proposal Product");
        var release = new SoftwareRelease(project.Id, "7.8", false);
        var wrongBuildRelease = new SoftwareRelease(project.Id, "7.9", false, release.Id);
        var origin = new SystemChangeRequest("SRCR-07800", 0, project.Id, release.Id,
            "Origin requirement", "Problem", "Analysis", "Solution", "proposal.author", now);
        var draft = new SystemChangeRequest("SRCR-07801", 0, project.Id, release.Id,
            "Eligible requirement Draft", "Problem", "Analysis", "Solution", "proposal.author", now);
        var wrongBuildDraft = new SystemChangeRequest("SRCR-07802", 0, project.Id, wrongBuildRelease.Id,
            "Different build Draft", "Problem", "Analysis", "Solution", "proposal.author", now);
        var user = new UserAccount("proposal.author", "Proposal Author", "proposal.author@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        var baseline = new CandidateBaseline("SW-78.00", 0, project.Id, release.Id, null,
            "Selected requirement baseline", user.UserName, now);
        var requirement = new RequirementArtifact(project.Id, "SYSR-078001", RequirementLevel.System, now);
        var selected = new RequirementRevision(requirement.Id, 0, "The system shall preserve exact build content.",
            "The baseline is authoritative.", "Inspection", RequirementRevisionState.Active, origin.Id, baseline.Id, now);
        var laterProjectRevision = new RequirementRevision(requirement.Id, 1, "The project latest is not the selected build.",
            "This revision deliberately remains outside the selected baseline.", "Inspection",
            RequirementRevisionState.Active, origin.Id, baseline.Id, now.AddMinutes(1));
        db.AddRange(program, project, release, wrongBuildRelease, origin, draft, wrongBuildDraft, user, baseline,
            requirement, selected, laterProjectRevision,
            new BaselineRequirementSelection(baseline.Id, requirement.Id, selected.Id));
        db.ProgramMemberships.Add(new ProgramMembership(user.Id, program.Id, ProgramRole.Engineer, "test.setup", now));
        await db.SaveChangesAsync();
        await db.CandidateBaselines.Where(x => x.Id == baseline.Id)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.RequirementsMaterializedAt, now));
        return new Seeded(project.Id, release.Id, requirement.Id, selected.Id, selected.Revision,
            draft.Id, draft.Version, wrongBuildDraft.Id);
    }

    private static async Task SignInAsync(HttpClient client)
    {
        using var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName = "proposal.author", password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }

    private static Task<HttpResponseMessage> PostExistingAsync(HttpClient client, Seeded seeded) =>
        client.PostAsJsonAsync($"/api/enterprise-requirements/{seeded.RequirementId}/propose", new
        {
            targetReleaseId = seeded.ReleaseId,
            kind = "Modify",
            existingScrId = seeded.DraftId,
            requirementRevisionId = seeded.SelectedRevisionId,
            expectedVersion = seeded.DraftVersion,
        });

    private static async Task<long> AddExistingProposalAsync(AeroLinkApiFactory factory, Seeded seeded,
        RequirementChangeKind kind, int revision)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var draft = await db.SystemChangeRequests.Include(x => x.RequirementChanges)
            .SingleAsync(x => x.Id == seeded.DraftId);
        var now = DateTimeOffset.UtcNow;
        draft.AddRequirementChange("proposal.author", "SYSR-078001", revision, RequirementLevel.System, kind,
            kind == RequirementChangeKind.Retire ? "" : "A stale or non-reopenable proposal.",
            "Existing proposal test", "Inspection", now);
        await db.SaveChangesAsync();
        return draft.Version;
    }

    private sealed record Seeded(Guid ProjectId, Guid ReleaseId, Guid RequirementId, Guid SelectedRevisionId,
        int SelectedRevision, Guid DraftId, long DraftVersion, Guid WrongBuildDraftId);
}
