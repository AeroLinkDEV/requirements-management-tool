using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>
/// Controlled attachment mutation is not a read-level activity.
///
/// Program membership alone used to authorize replacing Requirement evidence, an exact revision was
/// optional, and nothing checked whether that revision was still the requirement's current one or had
/// already been carried into a released baseline (#849 Finding 2). These cover the shared mutation
/// policy: engineering authority, exact revision identity, lifecycle eligibility, released-baseline
/// truth, chain-keeping supersession, the retained Change Request rule, and the controlled-export
/// refusal to present legacy unbound evidence.
/// </summary>
public sealed class ControlledAttachmentMutationApiTests
{
    private const string Engineer = "attach.engineer";
    private const string Reader = "attach.reader";

    private static async Task<(Guid ProgramId, Guid ProjectId, Guid ReleaseId)> SeedProjectAsync(AeroLinkDbContext db, string prefix)
    {
        var now = DateTimeOffset.UtcNow;
        var program = new ProgramRecord($"{prefix} Program", $"{prefix}{Guid.NewGuid():N}"[..12]);
        var project = new ProjectRecord(program.Id, $"{prefix} Product", $"{prefix} Product");
        var release = new SoftwareRelease(project.Id, "1.0", false);
        db.AddRange(program, project, release);
        return (program.Id, project.Id, release.Id);
    }

    private static async Task AddMemberAsync(AeroLinkDbContext db, Guid programId, string userName, ProgramRole role)
    {
        var now = DateTimeOffset.UtcNow;
        var account = new UserAccount(userName, userName, $"{userName}@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        db.AddRange(account, new ProgramMembership(account.Id, programId, role, "test.setup", now));
    }

    /// <summary>
    /// One requirement artifact with an approved origin change request, a baseline carrying the artifact's
    /// exact current revision (baseline membership is one revision per artifact), and a revision row per
    /// requested count — the newest Active, the rest Superseded. Returns every revision id, oldest first.
    /// </summary>
    private static async Task<(Guid ArtifactId, IReadOnlyList<Guid> RevisionIds, Guid BaselineId)> SeedRequirementAsync(
        AeroLinkDbContext db, Guid projectId, Guid releaseId, int revisionCount = 1, int sequence = 1)
    {
        var now = DateTimeOffset.UtcNow;
        var origin = new SystemChangeRequest($"SRCR-{sequence:D5}", 0, projectId, releaseId, "Origin", "P", "A", "S", Engineer, now);
        origin.AddRequirementChange(Engineer, $"SYSR-{sequence:D6}", 0, RequirementLevel.System,
            RequirementChangeKind.Introduce, "The controller shall retain state.", "Safety continuity", "Test", now);
        origin.SubmitForReview(Engineer, [new ApproverSelection(Reader, "Reviewer")], now);
        origin.ApproveActiveStage(Reader, now);
        var baseline = new CandidateBaseline($"SW-{sequence:D2}.00", 0, projectId, releaseId, null, "Origin baseline", "cm", now);
        baseline.Select(origin, "cm", now);
        var artifact = new RequirementArtifact(projectId, $"SYSR-{sequence:D6}", RequirementLevel.System, now);
        var revisionRows = new List<RequirementRevision>();
        for (var revision = 0; revision < revisionCount; revision++)
        {
            var state = revision == revisionCount - 1 ? RequirementRevisionState.Active : RequirementRevisionState.Superseded;
            revisionRows.Add(new RequirementRevision(artifact.Id, revision, $"Statement {revision}", "Rationale", "Test",
                state, origin.Id, baseline.Id, now));
        }
        // Two saves: the membership row carries no navigations, so it must be written after the rows it
        // references exist rather than trusting batch ordering.
        db.AddRange(origin, baseline, artifact);
        db.AddRange(revisionRows);
        await db.SaveChangesAsync();
        db.AddRange(new BaselineRequirementSelection(baseline.Id, artifact.Id, revisionRows[^1].Id));
        await db.SaveChangesAsync();
        return (artifact.Id, revisionRows.Select(x => x.Id).ToList(), baseline.Id);
    }

    private static async Task<Guid> SeedAttachmentAsync(AeroLinkDbContext db, EvidenceFileStore store,
        Guid projectId, Guid artifactId, Guid? revisionId, Guid logicalId, string payload)
    {
        var stored = await store.StoreAsync(new MemoryStream(Encoding.UTF8.GetBytes(payload)),
            "evidence.txt", "text/plain", default);
        var attachment = new ControlledAttachment(projectId, "Requirement", artifactId, revisionId,
            logicalId, 1, "Seeded evidence", "Seeded before the request under test.", stored.OriginalFileName,
            stored.ContentType, stored.Size, stored.Sha256, stored.StorageKey, null, Engineer, DateTimeOffset.UtcNow);
        db.Add(attachment);
        await db.SaveChangesAsync();
        return attachment.Id;
    }

    /// <summary>Walks the seeded baseline through freeze and materialization so it can be released.</summary>
    private static async Task<Guid> MaterializeBaselineAsync(AeroLinkDbContext db, Guid baselineId)
    {
        var baseline = await db.CandidateBaselines.SingleAsync(x => x.Id == baselineId);
        baseline.Freeze("cm", DateTimeOffset.UtcNow);
        baseline.MarkRequirementsMaterialized("cm", Convert.ToHexString(SHA256.HashData("manifest"u8)).ToLowerInvariant(),
            1, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();
        return baselineId;
    }

    private static async Task SignInAsync(HttpClient client, string userName)
    {
        using var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }

    private static MultipartFormDataContent UploadForm(Guid projectId, string artifactType, Guid artifactId,
        string? revisionId = null, Guid? logicalId = null)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent(projectId.ToString()), "projectId" },
            { new StringContent(artifactType), "artifactType" },
            { new StringContent(artifactId.ToString()), "artifactId" },
            { new StringContent("Controlled evidence"), "label" },
            { new StringContent("Attached under the shared mutation policy."), "description" },
            { new ByteArrayContent(Encoding.UTF8.GetBytes("Controlled evidence bytes.")) { Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain") } }, "file", "evidence.txt" },
        };
        if (revisionId is not null) form.Add(new StringContent(revisionId), "revisionId");
        if (logicalId is not null) form.Add(new StringContent(logicalId.Value.ToString()), "logicalId");
        return form;
    }

    private static Task<HttpResponseMessage> UploadAsync(HttpClient client, Guid projectId, string artifactType,
        Guid artifactId, string? revisionId = null, Guid? logicalId = null) =>
        client.PostAsync(
            $"/api/enterprise-hardening/attachments?projectId={projectId}&artifactType={artifactType}&artifactId={artifactId}",
            UploadForm(projectId, artifactType, artifactId, revisionId, logicalId));

    private static async Task<JsonElement> ErrorAsync(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(problem.TryGetProperty("code", out var code), problem.GetRawText());
        return code;
    }

    [Fact]
    public async Task A_program_member_without_engineering_authority_cannot_attach_requirement_evidence()
    {
        using var factory = new AeroLinkApiFactory();
        Guid projectId, artifactId, revisionId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var (programId, seedProject, seedRelease) = await SeedProjectAsync(db, "ATTN");
            await AddMemberAsync(db, programId, Engineer, ProgramRole.Engineer);
            await AddMemberAsync(db, programId, Reader, ProgramRole.Reviewer);
            await db.SaveChangesAsync();
            (artifactId, var revisions, _) = await SeedRequirementAsync(db, seedProject, seedRelease);
            (projectId, revisionId) = (seedProject, revisions[^1]);
        }

        using var client = factory.CreateClient();
        await SignInAsync(client, Reader);
        using var denied = await UploadAsync(client, projectId, "Requirement", artifactId, revisionId.ToString());

        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            Assert.Empty(await db.ControlledAttachments.Where(x => x.ArtifactId == artifactId).ToListAsync());
        }
    }

    [Fact]
    public async Task An_engineer_attaches_to_the_current_revision_and_creation_and_supersession_are_audited()
    {
        using var factory = new AeroLinkApiFactory();
        Guid projectId, artifactId, revisionId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var (programId, seedProject, seedRelease) = await SeedProjectAsync(db, "ATTC");
            await AddMemberAsync(db, programId, Engineer, ProgramRole.Engineer);
            await db.SaveChangesAsync();
            (artifactId, var revisions, _) = await SeedRequirementAsync(db, seedProject, seedRelease);
            (projectId, revisionId) = (seedProject, revisions[^1]);
        }

        using var client = factory.CreateClient();
        await SignInAsync(client, Engineer);
        using var created = await UploadAsync(client, projectId, "Requirement", artifactId, revisionId.ToString());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var stored = await created.Content.ReadFromJsonAsync<JsonElement>();
        var logicalId = stored.GetProperty("logicalId").GetGuid();
        Assert.Equal(1, stored.GetProperty("version").GetInt32());

        using var superseded = await UploadAsync(client, projectId, "Requirement", artifactId, revisionId.ToString(), logicalId);
        Assert.Equal(HttpStatusCode.Created, superseded.StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var rows = await db.ControlledAttachments.AsNoTracking().OrderBy(x => x.Version).ToListAsync();
            Assert.Equal(2, rows.Count);
            Assert.All(rows, row => Assert.Equal(revisionId, row.RevisionId));
            Assert.Equal(ControlledAttachmentState.Superseded, rows[0].State);
            Assert.Equal(ControlledAttachmentState.Active, rows[1].State);
            Assert.Equal(rows[0].Id, rows[1].SupersedesId);
            var events = (await db.SecurityAuditEvents.AsNoTracking()
                .Where(x => x.Target == $"Requirement:{artifactId}").ToListAsync())
                .OrderBy(x => x.OccurredAt).ToList();
            Assert.Equal(["ControlledAttachmentCreated", "ControlledAttachmentSuperseded"],
                events.Select(x => x.EventType).ToArray());
            Assert.All(events, item => Assert.Equal(Engineer, item.ActorId));
            Assert.All(events, item => Assert.Equal("Success", item.Outcome));
            Assert.Contains(rows[1].Sha256, events[1].Detail);
        }
    }

    [Fact]
    public async Task A_requirement_upload_without_its_exact_revision_is_rejected()
    {
        using var factory = new AeroLinkApiFactory();
        Guid projectId, artifactId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var (programId, seedProject, seedRelease) = await SeedProjectAsync(db, "ATTR");
            await AddMemberAsync(db, programId, Engineer, ProgramRole.Engineer);
            await db.SaveChangesAsync();
            (artifactId, _, _) = await SeedRequirementAsync(db, seedProject, seedRelease);
            projectId = seedProject;
        }

        using var client = factory.CreateClient();
        await SignInAsync(client, Engineer);
        using var rejected = await UploadAsync(client, projectId, "Requirement", artifactId);

        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal("revision_identity_required", (await ErrorAsync(rejected)).GetString());
    }

    [Fact]
    public async Task A_revision_from_another_requirement_is_rejected()
    {
        using var factory = new AeroLinkApiFactory();
        Guid projectId, firstArtifact, secondRevisionId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var (programId, seedProject, seedRelease) = await SeedProjectAsync(db, "ATTM");
            await AddMemberAsync(db, programId, Engineer, ProgramRole.Engineer);
            await db.SaveChangesAsync();
            (firstArtifact, var firstRevisions, _) = await SeedRequirementAsync(db, seedProject, seedRelease, sequence: 1);
            (_, var secondRevisions, _) = await SeedRequirementAsync(db, seedProject, seedRelease, sequence: 2);
            (projectId, secondRevisionId) = (seedProject, secondRevisions[^1]);
            _ = firstRevisions;
        }

        using var client = factory.CreateClient();
        await SignInAsync(client, Engineer);
        using var rejected = await UploadAsync(client, projectId, "Requirement", firstArtifact, secondRevisionId.ToString());

        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal("revision_identity_mismatch", (await ErrorAsync(rejected)).GetString());
    }

    [Fact]
    public async Task A_stale_revision_is_rejected()
    {
        using var factory = new AeroLinkApiFactory();
        Guid projectId, artifactId, staleRevisionId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var (programId, seedProject, seedRelease) = await SeedProjectAsync(db, "ATTS");
            await AddMemberAsync(db, programId, Engineer, ProgramRole.Engineer);
            await db.SaveChangesAsync();
            (artifactId, var revisions, _) = await SeedRequirementAsync(db, seedProject, seedRelease, revisionCount: 2);
            (projectId, staleRevisionId) = (seedProject, revisions[0]);
        }

        using var client = factory.CreateClient();
        await SignInAsync(client, Engineer);
        using var rejected = await UploadAsync(client, projectId, "Requirement", artifactId, staleRevisionId.ToString());

        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        Assert.Equal("revision_not_current", (await ErrorAsync(rejected)).GetString());
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            Assert.Empty(await db.ControlledAttachments.Where(x => x.ArtifactId == artifactId).ToListAsync());
        }
    }

    [Fact]
    public async Task Frozen_baseline_evidence_can_be_replaced_and_released_baseline_evidence_cannot()
    {
        using var factory = new AeroLinkApiFactory();
        Guid projectId, artifactId, revisionId, baselineId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var (programId, seedProject, seedRelease) = await SeedProjectAsync(db, "ATTB");
            await AddMemberAsync(db, programId, Engineer, ProgramRole.Engineer);
            await db.SaveChangesAsync();
            (artifactId, var revisions, var seededBaseline) = await SeedRequirementAsync(db, seedProject, seedRelease);
            baselineId = await MaterializeBaselineAsync(db, seededBaseline);
            (projectId, revisionId) = (seedProject, revisions[^1]);
        }

        using var client = factory.CreateClient();
        await SignInAsync(client, Engineer);

        // Frozen truth is still in work: evidence may be attached to the exact revision.
        using var frozen = await UploadAsync(client, projectId, "Requirement", artifactId, revisionId.ToString());
        Assert.Equal(HttpStatusCode.Created, frozen.StatusCode);
        var frozenChain = (await frozen.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("logicalId").GetGuid();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            (await db.CandidateBaselines.SingleAsync(x => x.Id == baselineId)).MarkReleased("cm", DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }

        // Once released, the same revision refuses both a new chain and a supersession of the bound one.
        using var newChain = await UploadAsync(client, projectId, "Requirement", artifactId, revisionId.ToString());
        Assert.Equal(HttpStatusCode.Conflict, newChain.StatusCode);
        Assert.Equal("revision_released", (await ErrorAsync(newChain)).GetString());
        using var sameChain = await UploadAsync(client, projectId, "Requirement", artifactId, revisionId.ToString(), frozenChain);
        Assert.Equal(HttpStatusCode.Conflict, sameChain.StatusCode);
        Assert.Equal("revision_released", (await ErrorAsync(sameChain)).GetString());

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            Assert.Single(await db.ControlledAttachments.Where(x => x.ArtifactId == artifactId).ToListAsync());
        }
    }

    [Fact]
    public async Task Supersession_cannot_jump_a_bound_chain_to_a_different_revision()
    {
        using var factory = new AeroLinkApiFactory();
        Guid projectId, artifactId, supersededRevisionId, currentRevisionId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var (programId, seedProject, seedRelease) = await SeedProjectAsync(db, "ATTJ");
            await AddMemberAsync(db, programId, Engineer, ProgramRole.Engineer);
            await db.SaveChangesAsync();
            (artifactId, var revisions, _) = await SeedRequirementAsync(db, seedProject, seedRelease, revisionCount: 2);
            (projectId, supersededRevisionId, currentRevisionId) = (seedProject, revisions[0], revisions[^1]);
            var store = scope.ServiceProvider.GetRequiredService<EvidenceFileStore>();
            // The chain's evidence was attached while revision zero was current; history keeps that binding.
            await SeedAttachmentAsync(db, store, projectId, artifactId, supersededRevisionId,
                Guid.NewGuid(), "Revision-zero chain head from before the newer revision.");
        }

        using var client = factory.CreateClient();
        await SignInAsync(client, Engineer);
        var chainId = Guid.Empty;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            chainId = (await db.ControlledAttachments.AsNoTracking().SingleAsync(x => x.ArtifactId == artifactId)).LogicalId;
        }

        // Superseding that chain with the new revision would jump its revision identity: refused.
        using var jumping = await UploadAsync(client, projectId, "Requirement", artifactId, currentRevisionId.ToString(), chainId);
        Assert.Equal(HttpStatusCode.Conflict, jumping.StatusCode);
        Assert.Equal("attachment_chain_revision_mismatch", (await ErrorAsync(jumping)).GetString());
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var head = await db.ControlledAttachments.AsNoTracking().SingleAsync(x => x.ArtifactId == artifactId);
            Assert.Equal(supersededRevisionId, head.RevisionId);
            Assert.Equal(ControlledAttachmentState.Active, head.State);
        }

        // Evidence for the new revision belongs in a new chain.
        using var freshChain = await UploadAsync(client, projectId, "Requirement", artifactId, currentRevisionId.ToString());
        Assert.Equal(HttpStatusCode.Created, freshChain.StatusCode);
    }

    [Fact]
    public async Task A_legacy_unbound_chain_is_bound_only_through_an_explicit_eligible_revision()
    {
        using var factory = new AeroLinkApiFactory();
        Guid projectId, artifactId, revisionId, logicalId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var (programId, seedProject, seedRelease) = await SeedProjectAsync(db, "ATTL");
            await AddMemberAsync(db, programId, Engineer, ProgramRole.Engineer);
            await db.SaveChangesAsync();
            (artifactId, var revisions, _) = await SeedRequirementAsync(db, seedProject, seedRelease);
            (projectId, revisionId) = (seedProject, revisions[^1]);
            var store = scope.ServiceProvider.GetRequiredService<EvidenceFileStore>();
            // Pre-policy history: an Active attachment with no revision binding at all.
            await SeedAttachmentAsync(db, store, projectId, artifactId, null, logicalId = Guid.NewGuid(),
                "Legacy evidence uploaded before exact binding existed.");
        }

        using var client = factory.CreateClient();
        await SignInAsync(client, Engineer);
        using var bound = await UploadAsync(client, projectId, "Requirement", artifactId, revisionId.ToString(), logicalId);
        Assert.Equal(HttpStatusCode.Created, bound.StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var rows = await db.ControlledAttachments.AsNoTracking().OrderBy(x => x.Version).ToListAsync();
            Assert.Equal(2, rows.Count);
            Assert.Null(rows[0].RevisionId);
            Assert.Equal(ControlledAttachmentState.Superseded, rows[0].State);
            Assert.Equal(revisionId, rows[1].RevisionId);
            Assert.Equal(ControlledAttachmentState.Active, rows[1].State);
        }
    }

    [Fact]
    public async Task A_logical_chain_from_another_requirement_is_refused()
    {
        using var factory = new AeroLinkApiFactory();
        Guid projectId, firstArtifact, secondArtifact, secondRevisionId, foreignChain;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var (programId, seedProject, seedRelease) = await SeedProjectAsync(db, "ATTX2");
            await AddMemberAsync(db, programId, Engineer, ProgramRole.Engineer);
            await db.SaveChangesAsync();
            (firstArtifact, var firstRevisions, _) = await SeedRequirementAsync(db, seedProject, seedRelease, sequence: 1);
            (secondArtifact, var secondRevisions, _) = await SeedRequirementAsync(db, seedProject, seedRelease, sequence: 2);
            (projectId, secondRevisionId) = (seedProject, secondRevisions[^1]);
            var store = scope.ServiceProvider.GetRequiredService<EvidenceFileStore>();
            await SeedAttachmentAsync(db, store, projectId, firstArtifact, firstRevisions[^1],
                foreignChain = Guid.NewGuid(), "Another requirement's chain.");
        }

        using var client = factory.CreateClient();
        await SignInAsync(client, Engineer);
        using var refused = await UploadAsync(client, projectId, "Requirement", secondArtifact, secondRevisionId.ToString(), foreignChain);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    [Fact]
    public async Task Change_request_supporting_files_keep_the_draft_author_rule()
    {
        using var factory = new AeroLinkApiFactory();
        Guid projectId, releaseId, changeRequestId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var (programId, seedProject, seedRelease) = await SeedProjectAsync(db, "ATTQ");
            await AddMemberAsync(db, programId, Engineer, ProgramRole.Engineer);
            await AddMemberAsync(db, programId, Reader, ProgramRole.Reviewer);
            await db.SaveChangesAsync();
            (projectId, releaseId) = (seedProject, seedRelease);
            var scr = new SystemChangeRequest("SRCR-00001", 0, seedProject, seedRelease,
                "Draft change", "P", "A", "S", Engineer, DateTimeOffset.UtcNow);
            db.Add(scr);
            await db.SaveChangesAsync();
            changeRequestId = scr.Id;
        }

        using var author = factory.CreateClient();
        await SignInAsync(author, Engineer);
        using var allowed = await UploadAsync(author, projectId, "ChangeRequest", changeRequestId);
        Assert.Equal(HttpStatusCode.Created, allowed.StatusCode);

        using var outsider = factory.CreateClient();
        await SignInAsync(outsider, Reader);
        using var denied = await UploadAsync(outsider, projectId, "ChangeRequest", changeRequestId);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Fact]
    public async Task An_unsupported_artifact_type_fails_closed()
    {
        using var factory = new AeroLinkApiFactory();
        Guid projectId, artifactId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var (programId, seedProject, seedRelease) = await SeedProjectAsync(db, "ATTU");
            await AddMemberAsync(db, programId, Engineer, ProgramRole.Engineer);
            await db.SaveChangesAsync();
            (artifactId, _, _) = await SeedRequirementAsync(db, seedProject, seedRelease);
            projectId = seedProject;
        }

        using var client = factory.CreateClient();
        await SignInAsync(client, Engineer);
        using var rejected = await UploadAsync(client, projectId, "Widget", artifactId);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal("unsupported_artifact_type", (await ErrorAsync(rejected)).GetString());
    }

    [Fact]
    public async Task A_controlled_export_fails_closed_while_legacy_unbound_evidence_is_in_scope()
    {
        using var factory = new AeroLinkApiFactory();
        Guid projectId, releaseId, artifactId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var (programId, seedProject, seedRelease) = await SeedProjectAsync(db, "ATTX");
            await AddMemberAsync(db, programId, Engineer, ProgramRole.Engineer);
            await db.SaveChangesAsync();
            (artifactId, var revisions, var seededBaseline) = await SeedRequirementAsync(db, seedProject, seedRelease);
            await MaterializeBaselineAsync(db, seededBaseline);
            (projectId, releaseId) = (seedProject, seedRelease);
            var store = scope.ServiceProvider.GetRequiredService<EvidenceFileStore>();
            await SeedAttachmentAsync(db, store, projectId, artifactId, null, Guid.NewGuid(),
                "Unbound legacy evidence in the export scope.");
        }

        using var client = factory.CreateClient();
        await SignInAsync(client, Engineer);
        using var refused = await client.PostAsJsonAsync("/api/reqif/exports", new { projectId, releaseId });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("attachment_revision_binding_required", (await ErrorAsync(refused)).GetString());

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            Assert.Empty(await db.ReqIfExchangeJobs.Where(x => x.ProjectId == projectId).ToListAsync());
        }
    }
}
