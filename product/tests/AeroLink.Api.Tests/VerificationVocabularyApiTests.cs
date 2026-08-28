using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>
/// #701: a project declares which verification methods it permits, authoring offers them, submission refuses
/// anything else by name, and values already stored outside the vocabulary are reported rather than rewritten.
/// </summary>
public sealed class VerificationVocabularyApiTests
{
    private sealed record Seeded(Guid ProjectId, Guid ReleaseId, Guid SectionId, string ManagerName,
        string AuthorName, string ApproverName);

    private static async Task<Seeded> SeedAsync(AeroLinkApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;
        var tag = Guid.NewGuid().ToString("N")[..8];
        var program = new ProgramRecord($"Verification Vocabulary {tag}", $"VVP{tag}");
        var project = new ProjectRecord(program.Id, "Flight Software", "Vocabulary Software");
        var release = new SoftwareRelease(project.Id, "1.0", false);

        var specification = new RequirementSpecification(project.Id, "SYSRD-000001", "System Requirements Document",
            RequirementLevel.System.ToString(), "Authoritative structured system requirements document.", "seed", now);
        var section = new SpecificationNode(specification.Id, null, 1000, SpecificationNodeType.Section,
            "Functional Behavior", null, "seed", now);

        var managerName = $"vocab.manager.{tag}";
        var authorName = $"vocab.author.{tag}";
        var approverName = $"vocab.approver.{tag}";
        UserAccount Account(string name) => new(name, name, $"{name}@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        var manager = Account(managerName);
        var author = Account(authorName);
        var approver = Account(approverName);

        db.AddRange(program, project, release, specification, section, manager, author, approver,
            new ProgramMembership(manager.Id, program.Id, ProgramRole.ConfigurationManager, "test.setup", now),
            new ProgramMembership(manager.Id, program.Id, ProgramRole.Engineer, "test.setup", now),
            new ProgramMembership(author.Id, program.Id, ProgramRole.Engineer, "test.setup", now),
            new ProgramMembership(approver.Id, program.Id, ProgramRole.Approver, "test.setup", now),
            new ProjectLeadershipAssignment(program.Id, ProjectLeadershipPosition.ConfigurationManager,
                manager.Id, "test.setup", now),
            ProjectVerificationVocabulary.Founding(project.Id, now));
        await db.SaveChangesAsync();
        return new(project.Id, release.Id, section.Id, managerName, authorName, approverName);
    }

    private static async Task SignInAsync(HttpClient client, string userName)
    {
        using var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }

    private static object DraftBody(Seeded seeded, string verificationMethod, string title = "Oceanic sequencing") => new
    {
        projectId = seeded.ProjectId,
        targetReleaseId = seeded.ReleaseId,
        type = "System",
        title,
        problem = "P",
        analysis = "A",
        solution = "S",
        requirementChanges = new[]
        {
            new
            {
                baseNumber = "",
                revision = 0,
                level = "System",
                kind = "Introduce",
                statement = "The FMS shall sequence oceanic waypoints.",
                rationale = "New capability",
                verificationMethod,
                targetSectionId = (Guid?)seeded.SectionId,
            },
        },
    };

    private sealed record DraftResponse(Guid Id, long Version);

    private static async Task<DraftResponse> CreateDraftAsync(HttpClient client, object body)
    {
        using var created = await client.PostAsJsonAsync("/api/change-request-drafts", body);
        Assert.True(created.StatusCode == HttpStatusCode.Created,
            $"{(int)created.StatusCode}: {await created.Content.ReadAsStringAsync()}");
        return (await created.Content.ReadFromJsonAsync<DraftResponse>())!;
    }

    private static Task<HttpResponseMessage> SubmitAsync(HttpClient client, Seeded seeded, DraftResponse draft) =>
        client.PostAsJsonAsync($"/api/change-requests/{draft.Id}/submit", new
        {
            expectedVersion = draft.Version,
            mode = "Sequential",
            approvers = new[] { new { userId = seeded.ApproverName, name = "Vocabulary Approver" } },
        });

    [Fact]
    public async Task A_new_project_is_created_carrying_a_persisted_vocabulary()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        using (var bootstrap = new HttpRequestMessage(HttpMethod.Post, "/api/setup/bootstrap")
               {
                   Content = JsonContent.Create(new
                   {
                       displayName = "Administrator", email = "admin@example.test",
                       password = AeroLinkApiFactory.AdministratorPassword,
                   }),
               })
        {
            bootstrap.Headers.Add("X-AeroLink-Bootstrap-Secret", AeroLinkApiFactory.BootstrapSecret);
            using var created = await client.SendAsync(bootstrap);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }
        using (var login = await client.PostAsJsonAsync("/api/auth/login",
                   new { userName = "admin", password = AeroLinkApiFactory.AdministratorPassword }))
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);

        using var workspace = await client.PostAsJsonAsync("/api/workspaces", new
        {
            programName = "Founded Program", programCode = "FND", projectName = "Founded Project",
            softwareProduct = "Founded Software", initialRelease = "1.0", initialReleaseIsReleased = false,
        });
        Assert.True(workspace.IsSuccessStatusCode, await workspace.Content.ReadAsStringAsync());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var projectId = await db.Projects.AsNoTracking().Where(x => x.Name == "Founded Project")
            .Select(x => x.Id).SingleAsync();
        var vocabulary = await db.ProjectVerificationVocabularies.AsNoTracking().Include(x => x.Methods)
            .SingleAsync(x => x.ProjectId == projectId);
        Assert.Equal(["Test", "Analysis", "Inspection", "Demonstration"], vocabulary.OrderedValues);
        Assert.Equal([1, 2, 3, 4], vocabulary.Methods.OrderBy(x => x.Position).Select(x => x.Position));
        Assert.All(vocabulary.Methods, method => Assert.Equal(projectId, method.ProjectId));
    }

    [Fact]
    public async Task An_authorized_manager_reads_and_replaces_the_vocabulary_with_attributable_evidence()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var seeded = await SeedAsync(factory);
        await SignInAsync(client, seeded.ManagerName);

        using var read = await client.GetAsync($"/api/projects/{seeded.ProjectId}/verification-methods");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var readBody = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
        Assert.True(readBody.RootElement.GetProperty("persisted").GetBoolean());
        Assert.True(readBody.RootElement.GetProperty("canManage").GetBoolean());
        Assert.Equal(1, readBody.RootElement.GetProperty("version").GetInt64());
        Assert.Equal(["Test", "Analysis", "Inspection", "Demonstration"], Methods(readBody.RootElement));
        Assert.Empty(readBody.RootElement.GetProperty("nonConforming").EnumerateArray());

        using var update = await client.PutAsJsonAsync($"/api/projects/{seeded.ProjectId}/verification-methods", new
        {
            expectedVersion = 1,
            reason = "This programme verifies by similarity to a qualified predecessor",
            methods = new[] { "Test", "Analysis", "Inspection", "Demonstration", "Similarity" },
        });
        Assert.True(update.IsSuccessStatusCode, await update.Content.ReadAsStringAsync());
        using var updated = JsonDocument.Parse(await update.Content.ReadAsStringAsync());
        Assert.Equal(["Test", "Analysis", "Inspection", "Demonstration", "Similarity"], Methods(updated.RootElement));
        Assert.Equal(2, updated.RootElement.GetProperty("version").GetInt64());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var audit = await db.SecurityAuditEvents.AsNoTracking()
            .SingleAsync(x => x.EventType == "VerificationVocabularyConfigured");
        Assert.Equal(seeded.ManagerName, audit.ActorId);
        Assert.Equal($"project:{seeded.ProjectId:D}", audit.Target);
        Assert.Contains("Similarity", audit.Detail, StringComparison.Ordinal);
        Assert.Contains("verifies by similarity", audit.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unauthorized_member_may_read_but_not_replace_the_vocabulary()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var seeded = await SeedAsync(factory);
        await SignInAsync(client, seeded.AuthorName);

        using var read = await client.GetAsync($"/api/projects/{seeded.ProjectId}/verification-methods");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var readBody = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
        Assert.False(readBody.RootElement.GetProperty("canManage").GetBoolean());

        using var update = await client.PutAsJsonAsync($"/api/projects/{seeded.ProjectId}/verification-methods", new
        {
            expectedVersion = 1, reason = "Not my authority", methods = new[] { "Test" },
        });
        Assert.Equal(HttpStatusCode.Forbidden, update.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var vocabulary = await db.ProjectVerificationVocabularies.AsNoTracking().Include(x => x.Methods)
            .SingleAsync(x => x.ProjectId == seeded.ProjectId);
        Assert.Equal(["Test", "Analysis", "Inspection", "Demonstration"], vocabulary.OrderedValues);
        Assert.Equal(1, vocabulary.Version);
    }

    [Fact]
    public async Task A_stale_expected_version_conflicts_and_a_blank_or_duplicate_edit_is_refused()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var seeded = await SeedAsync(factory);
        await SignInAsync(client, seeded.ManagerName);

        using var first = await client.PutAsJsonAsync($"/api/projects/{seeded.ProjectId}/verification-methods", new
        {
            expectedVersion = 1, reason = "Narrow the vocabulary for the pilot", methods = new[] { "Test", "Analysis" },
        });
        Assert.True(first.IsSuccessStatusCode, await first.Content.ReadAsStringAsync());

        using var stale = await client.PutAsJsonAsync($"/api/projects/{seeded.ProjectId}/verification-methods", new
        {
            expectedVersion = 1, reason = "Stale", methods = new[] { "Test" },
        });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Contains("Refresh before editing again", await stale.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var caseDuplicate = await client.PutAsJsonAsync($"/api/projects/{seeded.ProjectId}/verification-methods", new
        {
            expectedVersion = 2, reason = "Two spellings", methods = new[] { "Test", "test" },
        });
        Assert.Equal(HttpStatusCode.BadRequest, caseDuplicate.StatusCode);
        Assert.Contains("differ only in case", await caseDuplicate.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var blank = await client.PutAsJsonAsync($"/api/projects/{seeded.ProjectId}/verification-methods", new
        {
            expectedVersion = 2, reason = "Blank member", methods = new[] { "Test", "  " },
        });
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);

        using var empty = await client.PutAsJsonAsync($"/api/projects/{seeded.ProjectId}/verification-methods", new
        {
            expectedVersion = 2, reason = "Empty", methods = Array.Empty<string>(),
        });
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);

        using var unreasoned = await client.PutAsJsonAsync($"/api/projects/{seeded.ProjectId}/verification-methods", new
        {
            expectedVersion = 2, reason = "  ", methods = new[] { "Test" },
        });
        Assert.Equal(HttpStatusCode.BadRequest, unreasoned.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var vocabulary = await db.ProjectVerificationVocabularies.AsNoTracking().Include(x => x.Methods)
            .SingleAsync(x => x.ProjectId == seeded.ProjectId);
        Assert.Equal(["Test", "Analysis"], vocabulary.OrderedValues);
        Assert.Equal(2, vocabulary.Version);
    }

    [Fact]
    public async Task A_configured_member_controlled_records_still_declare_cannot_be_removed()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var seeded = await SeedAsync(factory);
        await SignInAsync(client, seeded.AuthorName);
        await CreateDraftAsync(client, DraftBody(seeded, "Test"));
        await SignInAsync(client, seeded.ManagerName);

        using var refused = await client.PutAsJsonAsync($"/api/projects/{seeded.ProjectId}/verification-methods", new
        {
            expectedVersion = 1, reason = "Drop Test", methods = new[] { "Analysis", "Inspection" },
        });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        using var body = JsonDocument.Parse(await refused.Content.ReadAsStringAsync());
        Assert.Contains("still declared by controlled requirement records",
            body.RootElement.GetProperty("error").GetString()!, StringComparison.Ordinal);
        Assert.Equal(["Test"], body.RootElement.GetProperty("strandedMethods").EnumerateArray()
            .Select(x => x.GetString()!).ToArray());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var vocabulary = await db.ProjectVerificationVocabularies.AsNoTracking().Include(x => x.Methods)
            .SingleAsync(x => x.ProjectId == seeded.ProjectId);
        Assert.Equal(["Test", "Analysis", "Inspection", "Demonstration"], vocabulary.OrderedValues);
        Assert.Equal(1, vocabulary.Version);
    }

    /// <summary>
    /// The #701 review finding. Review matches the configured spelling byte-for-byte, so re-spelling a member
    /// that controlled records declare strands every one of them without removing anything — the records go
    /// non-conforming and their future submissions are refused, silently. It is refused as a removal.
    /// </summary>
    [Fact]
    public async Task Re_spelling_a_configured_member_controlled_records_declare_is_refused()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var seeded = await SeedAsync(factory);
        await SignInAsync(client, seeded.ManagerName);
        using (var narrowed = await client.PutAsJsonAsync(
                   $"/api/projects/{seeded.ProjectId}/verification-methods", new
                   {
                       expectedVersion = 1, reason = "This programme verifies by test only", methods = new[] { "Test" },
                   }))
            Assert.True(narrowed.IsSuccessStatusCode, await narrowed.Content.ReadAsStringAsync());

        Guid changeId;
        Guid revisionId;
        using (var scope = factory.Services.CreateScope())
        {
            // Both authorities declare the exact configured spelling: an in-flight proposal and a
            // materialized revision. Either one alone is enough to pin it.
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var now = DateTimeOffset.UtcNow;
            var origin = new SystemChangeRequest("SRCR-70150", 0, seeded.ProjectId, seeded.ReleaseId,
                "Declares the configured spelling", "P", "A", "S", seeded.AuthorName, now);
            origin.AddRequirementChange(seeded.AuthorName, "SYSR-701500", 0, RequirementLevel.System,
                RequirementChangeKind.Introduce, "The FMS shall hold altitude.", "Rationale", "Test", now);
            origin.SubmitForReview(seeded.AuthorName, [new ApproverSelection(seeded.AuthorName, "Author")], now);
            origin.ApproveActiveStage(seeded.AuthorName, now);
            var baseline = new CandidateBaseline("SW-01.00", 0, seeded.ProjectId, seeded.ReleaseId, null,
                "Spelling baseline", seeded.AuthorName, now);
            baseline.Select(origin, seeded.AuthorName, now);
            baseline.Freeze(seeded.AuthorName, now);
            baseline.MarkRequirementsMaterialized(seeded.AuthorName, new string('a', 64), 1, now);
            var artifact = new RequirementArtifact(seeded.ProjectId, "SYSR-701500", RequirementLevel.System, now);
            var revision = new RequirementRevision(artifact.Id, 0, "The FMS shall hold altitude.", "Rationale",
                "Test", RequirementRevisionState.Active, origin.Id, baseline.Id, now);
            changeId = origin.RequirementChanges.Single().Id;
            revisionId = revision.Id;
            db.AddRange(origin, baseline, artifact, revision);
            await db.SaveChangesAsync();
        }

        using var refused = await client.PutAsJsonAsync($"/api/projects/{seeded.ProjectId}/verification-methods", new
        {
            expectedVersion = 2, reason = "Lower-case the configured spelling", methods = new[] { "test" },
        });

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        using var body = JsonDocument.Parse(await refused.Content.ReadAsStringAsync());
        Assert.Contains("cannot be removed or re-spelled",
            body.RootElement.GetProperty("error").GetString()!, StringComparison.Ordinal);
        Assert.Equal(["Test"], body.RootElement.GetProperty("strandedMethods").EnumerateArray()
            .Select(x => x.GetString()!).ToArray());

        using var scope2 = factory.Services.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        // Nothing moved: display values, positions, versions, timestamps, and no second audit event.
        var vocabulary = await db2.ProjectVerificationVocabularies.AsNoTracking().Include(x => x.Methods)
            .SingleAsync(x => x.ProjectId == seeded.ProjectId);
        Assert.Equal(["Test"], vocabulary.OrderedValues);
        Assert.Equal(2, vocabulary.Version);
        var member = vocabulary.Methods.Single();
        Assert.Equal("Test", member.DisplayValue);
        Assert.Equal("test", member.NormalizedValue);
        Assert.Equal(1, member.Position);
        Assert.Equal(1, member.Version);
        Assert.Equal(member.CreatedAt, member.UpdatedAt);
        Assert.Single(await db2.SecurityAuditEvents.AsNoTracking()
            .Where(x => x.EventType == "VerificationVocabularyConfigured").ToListAsync());
        // And the controlled records still say exactly what they said.
        Assert.Equal("Test", await db2.RequirementChanges.AsNoTracking()
            .Where(x => x.Id == changeId).Select(x => x.VerificationMethod).SingleAsync());
        Assert.Equal("Test", await db2.RequirementRevisions.AsNoTracking()
            .Where(x => x.Id == revisionId).Select(x => x.VerificationMethod).SingleAsync());
        // The records remain conforming, because the refusal kept the vocabulary that permits them.
        using var read = await client.GetAsync($"/api/projects/{seeded.ProjectId}/verification-methods");
        using var readBody = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
        Assert.Empty(readBody.RootElement.GetProperty("nonConforming").EnumerateArray());
    }

    [Fact]
    public async Task A_casing_change_no_controlled_record_declares_is_permitted()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var seeded = await SeedAsync(factory);
        await SignInAsync(client, seeded.ManagerName);

        // Nothing declares "Inspection", so nothing is stranded by correcting its spelling.
        using var accepted = await client.PutAsJsonAsync($"/api/projects/{seeded.ProjectId}/verification-methods", new
        {
            expectedVersion = 1,
            reason = "House style spells it in upper case",
            methods = new[] { "Test", "Analysis", "INSPECTION", "Demonstration" },
        });
        Assert.True(accepted.IsSuccessStatusCode, await accepted.Content.ReadAsStringAsync());
        using var body = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync());
        Assert.Equal(["Test", "Analysis", "INSPECTION", "Demonstration"], Methods(body.RootElement));
    }

    /// <summary>
    /// A retirement declares no verification method. Submission skips it and the reconciliation report skips
    /// it, so it must not be the one place that treats the same record as a declaration and pins a spelling
    /// nobody asserted against configuration.
    /// </summary>
    [Fact]
    public async Task A_retirement_carrying_a_historical_value_does_not_pin_that_spelling()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var seeded = await SeedAsync(factory);
        await SignInAsync(client, seeded.ManagerName);
        using (var narrowed = await client.PutAsJsonAsync(
                   $"/api/projects/{seeded.ProjectId}/verification-methods", new
                   {
                       expectedVersion = 1, reason = "This programme verifies by test only", methods = new[] { "Test" },
                   }))
            Assert.True(narrowed.IsSuccessStatusCode, await narrowed.Content.ReadAsStringAsync());

        Guid retirementId;
        using (var scope = factory.Services.CreateScope())
        {
            // A retirement that still carries the value the requirement it retires used to declare.
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var now = DateTimeOffset.UtcNow;
            var request = new SystemChangeRequest("SRCR-70160", 0, seeded.ProjectId, seeded.ReleaseId,
                "Retire a requirement", "P", "A", "S", seeded.AuthorName, now);
            request.AddRequirementChange(seeded.AuthorName, "SYSR-701600", 1, RequirementLevel.System,
                RequirementChangeKind.Retire, "", "No longer required", "Test", now);
            retirementId = request.RequirementChanges.Single().Id;
            db.Add(request);
            await db.SaveChangesAsync();
        }

        // Not reported as non-conforming either — the report and the stranding rule agree.
        using (var read = await client.GetAsync($"/api/projects/{seeded.ProjectId}/verification-methods"))
        {
            using var body = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
            Assert.Empty(body.RootElement.GetProperty("nonConforming").EnumerateArray());
        }

        // Both the removal and the re-spelling that a genuine declaration would refuse are permitted here.
        using var respelled = await client.PutAsJsonAsync($"/api/projects/{seeded.ProjectId}/verification-methods", new
        {
            expectedVersion = 2, reason = "House style", methods = new[] { "test" },
        });
        Assert.True(respelled.IsSuccessStatusCode, await respelled.Content.ReadAsStringAsync());
        using var removed = await client.PutAsJsonAsync($"/api/projects/{seeded.ProjectId}/verification-methods", new
        {
            expectedVersion = 3, reason = "Analysis only from here", methods = new[] { "Analysis" },
        });
        Assert.True(removed.IsSuccessStatusCode, await removed.Content.ReadAsStringAsync());

        using var scope2 = factory.Services.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        // The retirement itself was never rewritten by any of that.
        Assert.Equal("Test", await db2.RequirementChanges.AsNoTracking()
            .Where(x => x.Id == retirementId).Select(x => x.VerificationMethod).SingleAsync());
    }

    [Fact]
    public async Task A_genuine_declaration_alongside_a_retirement_still_pins_its_spelling()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var seeded = await SeedAsync(factory);
        await SignInAsync(client, seeded.ManagerName);
        using (var narrowed = await client.PutAsJsonAsync(
                   $"/api/projects/{seeded.ProjectId}/verification-methods", new
                   {
                       expectedVersion = 1, reason = "This programme verifies by test only", methods = new[] { "Test" },
                   }))
            Assert.True(narrowed.IsSuccessStatusCode, await narrowed.Content.ReadAsStringAsync());

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var now = DateTimeOffset.UtcNow;
            var request = new SystemChangeRequest("SRCR-70161", 0, seeded.ProjectId, seeded.ReleaseId,
                "One retirement and one declaration", "P", "A", "S", seeded.AuthorName, now);
            request.AddRequirementChange(seeded.AuthorName, "SYSR-701610", 1, RequirementLevel.System,
                RequirementChangeKind.Retire, "", "No longer required", "Test", now);
            request.AddRequirementChange(seeded.AuthorName, "SYSR-701611", 0, RequirementLevel.System,
                RequirementChangeKind.Introduce, "The FMS shall hold altitude.", "Rationale", "Test", now);
            db.Add(request);
            await db.SaveChangesAsync();
        }

        using var refused = await client.PutAsJsonAsync($"/api/projects/{seeded.ProjectId}/verification-methods", new
        {
            expectedVersion = 2, reason = "House style", methods = new[] { "test" },
        });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        using var body = JsonDocument.Parse(await refused.Content.ReadAsStringAsync());
        Assert.Equal(["Test"], body.RootElement.GetProperty("strandedMethods").EnumerateArray()
            .Select(x => x.GetString()!).ToArray());

        using var scope2 = factory.Services.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var vocabulary = await db2.ProjectVerificationVocabularies.AsNoTracking().Include(x => x.Methods)
            .SingleAsync(x => x.ProjectId == seeded.ProjectId);
        Assert.Equal(["Test"], vocabulary.OrderedValues);
        Assert.Equal(2, vocabulary.Version);
        Assert.Equal(1, vocabulary.Methods.Single().Version);
        // The refusal produced no audit event of its own: only the earlier legitimate narrowing remains.
        Assert.Single(await db2.SecurityAuditEvents.AsNoTracking()
            .Where(x => x.EventType == "VerificationVocabularyConfigured").ToListAsync());
        Assert.Equal(["Test", "Test"], await db2.RequirementChanges.AsNoTracking()
            .OrderBy(x => x.BaseNumber).Select(x => x.VerificationMethod).ToListAsync());
    }

    [Fact]
    public async Task A_revision_still_pins_its_spelling_even_though_the_requirement_was_later_retired()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var seeded = await SeedAsync(factory);
        await SignInAsync(client, seeded.ManagerName);
        using (var narrowed = await client.PutAsJsonAsync(
                   $"/api/projects/{seeded.ProjectId}/verification-methods", new
                   {
                       expectedVersion = 1, reason = "This programme verifies by test only", methods = new[] { "Test" },
                   }))
            Assert.True(narrowed.IsSuccessStatusCode, await narrowed.Content.ReadAsStringAsync());

        using (var scope = factory.Services.CreateScope())
        {
            // Immutable history says what it says. Retiring the requirement afterwards does not unsay the
            // declaration the revision carries, so the spelling stays pinned.
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var now = DateTimeOffset.UtcNow;
            var origin = new SystemChangeRequest("SRCR-70162", 0, seeded.ProjectId, seeded.ReleaseId,
                "Historical declaration", "P", "A", "S", seeded.AuthorName, now);
            origin.AddRequirementChange(seeded.AuthorName, "SYSR-701620", 0, RequirementLevel.System,
                RequirementChangeKind.Introduce, "The FMS shall hold altitude.", "Rationale", "Test", now);
            origin.SubmitForReview(seeded.AuthorName, [new ApproverSelection(seeded.AuthorName, "Author")], now);
            origin.ApproveActiveStage(seeded.AuthorName, now);
            var baseline = new CandidateBaseline("SW-01.00", 0, seeded.ProjectId, seeded.ReleaseId, null,
                "Historical baseline", seeded.AuthorName, now);
            baseline.Select(origin, seeded.AuthorName, now);
            baseline.Freeze(seeded.AuthorName, now);
            baseline.MarkRequirementsMaterialized(seeded.AuthorName, new string('a', 64), 1, now);
            var artifact = new RequirementArtifact(seeded.ProjectId, "SYSR-701620", RequirementLevel.System, now);
            var revision = new RequirementRevision(artifact.Id, 0, "The FMS shall hold altitude.", "Rationale",
                "Test", RequirementRevisionState.Active, origin.Id, baseline.Id, now);
            var retirement = new SystemChangeRequest("SRCR-70163", 0, seeded.ProjectId, seeded.ReleaseId,
                "Retire it afterwards", "P", "A", "S", seeded.AuthorName, now);
            retirement.AddRequirementChange(seeded.AuthorName, "SYSR-701620", 1, RequirementLevel.System,
                RequirementChangeKind.Retire, "", "No longer required", "Test", now);
            db.AddRange(origin, baseline, artifact, revision, retirement);
            await db.SaveChangesAsync();
        }

        using var refused = await client.PutAsJsonAsync($"/api/projects/{seeded.ProjectId}/verification-methods", new
        {
            expectedVersion = 2, reason = "House style", methods = new[] { "test" },
        });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        using var body = JsonDocument.Parse(await refused.Content.ReadAsStringAsync());
        Assert.Equal(["Test"], body.RootElement.GetProperty("strandedMethods").EnumerateArray()
            .Select(x => x.GetString()!).ToArray());
    }

    [Fact]
    public async Task An_exact_configured_value_reaches_review()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var seeded = await SeedAsync(factory);
        await SignInAsync(client, seeded.AuthorName);

        var draft = await CreateDraftAsync(client, DraftBody(seeded, "Test"));
        using var submit = await SubmitAsync(client, seeded, draft);
        Assert.True(submit.IsSuccessStatusCode, await submit.Content.ReadAsStringAsync());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var change = await db.RequirementChanges.AsNoTracking().SingleAsync(x => x.ChangeRequestId == draft.Id);
        Assert.Equal("Test", change.VerificationMethod);
    }

    [Fact]
    public async Task A_programme_configured_method_becomes_submittable_the_moment_it_is_configured()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var seeded = await SeedAsync(factory);

        await SignInAsync(client, seeded.AuthorName);
        var before = await CreateDraftAsync(client, DraftBody(seeded, "Similarity", "Similarity before"));
        using var refused = await SubmitAsync(client, seeded, before);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        await SignInAsync(client, seeded.ManagerName);
        using var configured = await client.PutAsJsonAsync($"/api/projects/{seeded.ProjectId}/verification-methods", new
        {
            expectedVersion = 1,
            reason = "Similarity to a qualified predecessor is a permitted method on this programme",
            methods = new[] { "Test", "Analysis", "Inspection", "Demonstration", "Similarity" },
        });
        Assert.True(configured.IsSuccessStatusCode, await configured.Content.ReadAsStringAsync());

        // Authoring now offers it, and the same package reaches review unchanged.
        using var authoring = await client.GetAsync($"/api/projects/{seeded.ProjectId}/verification-methods");
        using var authoringBody = JsonDocument.Parse(await authoring.Content.ReadAsStringAsync());
        Assert.Contains("Similarity", Methods(authoringBody.RootElement));

        await SignInAsync(client, seeded.AuthorName);
        using var reread = await client.GetAsync($"/api/change-requests/{before.Id}");
        using var rereadBody = JsonDocument.Parse(await reread.Content.ReadAsStringAsync());
        using var accepted = await SubmitAsync(client, seeded,
            new DraftResponse(before.Id, rereadBody.RootElement.GetProperty("version").GetInt64()));
        Assert.True(accepted.IsSuccessStatusCode, await accepted.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("test")]
    [InlineData("TEST")]
    [InlineData("Testing")]
    [InlineData("Similarity")]
    [InlineData("")]
    public async Task A_value_outside_the_vocabulary_is_refused_at_submission_and_names_the_permitted_values(
        string declared)
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var seeded = await SeedAsync(factory);
        await SignInAsync(client, seeded.ManagerName);
        using (var narrowed = await client.PutAsJsonAsync($"/api/projects/{seeded.ProjectId}/verification-methods", new
               {
                   expectedVersion = 1, reason = "This programme verifies by test only", methods = new[] { "Test" },
               }))
            Assert.True(narrowed.IsSuccessStatusCode, await narrowed.Content.ReadAsStringAsync());

        await SignInAsync(client, seeded.AuthorName);
        var draft = await CreateDraftAsync(client, DraftBody(seeded, declared));
        using var submit = await SubmitAsync(client, seeded, draft);

        Assert.Equal(HttpStatusCode.BadRequest, submit.StatusCode);
        var body = await submit.Content.ReadAsStringAsync();
        Assert.Contains("Permitted verification methods: Test.", body, StringComparison.Ordinal);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var request = await db.SystemChangeRequests.AsNoTracking()
            .Include(x => x.RequirementChanges).SingleAsync(x => x.Id == draft.Id);
        // Nothing moved: no review cycle, the same version, and above all the declared value still says
        // exactly what its author wrote rather than the nearest permitted word.
        Assert.Equal(ChangeRequestState.Draft, request.State);
        Assert.Equal(draft.Version, request.Version);
        Assert.Equal(declared, request.RequirementChanges.Single().VerificationMethod);
        Assert.Empty(await db.ReviewCycles.AsNoTracking().Where(x => x.ChangeRequestId == draft.Id).ToListAsync());
    }

    [Fact]
    public async Task An_existing_non_conforming_change_is_reported_and_left_exactly_as_stored()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var seeded = await SeedAsync(factory);
        await SignInAsync(client, seeded.AuthorName);
        await CreateDraftAsync(client, DraftBody(seeded, "Testing", "Historical wording"));
        await SignInAsync(client, seeded.ManagerName);

        using var read = await client.GetAsync($"/api/projects/{seeded.ProjectId}/verification-methods");
        using var body = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
        var row = body.RootElement.GetProperty("nonConforming").EnumerateArray().Single();
        Assert.Equal("Testing", row.GetProperty("value").GetString());
        Assert.Equal(1, row.GetProperty("changeCount").GetInt32());
        Assert.Equal(0, row.GetProperty("revisionCount").GetInt32());
        Assert.Equal(1, row.GetProperty("totalCount").GetInt32());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        Assert.Equal("Testing",
            await db.RequirementChanges.AsNoTracking().Select(x => x.VerificationMethod).SingleAsync());
    }

    [Fact]
    public async Task An_existing_non_conforming_revision_is_reported_and_left_exactly_as_stored()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var seeded = await SeedAsync(factory);
        Guid revisionId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var now = DateTimeOffset.UtcNow;
            var origin = new SystemChangeRequest("SRCR-70101", 0, seeded.ProjectId, seeded.ReleaseId,
                "Historical baseline origin", "P", "A", "S", seeded.AuthorName, now);
            origin.AddRequirementChange(seeded.AuthorName, "SYSR-701001", 0, RequirementLevel.System,
                RequirementChangeKind.Introduce, "The FMS shall hold altitude.", "Historical", "test", now);
            origin.SubmitForReview(seeded.AuthorName, [new ApproverSelection(seeded.AuthorName, "Author")], now);
            origin.ApproveActiveStage(seeded.AuthorName, now);
            var baseline = new CandidateBaseline("SW-01.00", 0, seeded.ProjectId, seeded.ReleaseId, null,
                "Historical baseline", seeded.AuthorName, now);
            baseline.Select(origin, seeded.AuthorName, now);
            baseline.Freeze(seeded.AuthorName, now);
            baseline.MarkRequirementsMaterialized(seeded.AuthorName, new string('a', 64), 1, now);
            var artifact = new RequirementArtifact(seeded.ProjectId, "SYSR-701001", RequirementLevel.System, now);
            var revision = new RequirementRevision(artifact.Id, 0, "The FMS shall hold altitude.", "Historical",
                "test", RequirementRevisionState.Active, origin.Id, baseline.Id, now);
            revisionId = revision.Id;
            db.AddRange(origin, baseline, artifact, revision);
            await db.SaveChangesAsync();
        }

        using var client2 = factory.CreateClient();
        await SignInAsync(client2, seeded.ManagerName);
        using var read = await client2.GetAsync($"/api/projects/{seeded.ProjectId}/verification-methods");
        using var body = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
        var row = body.RootElement.GetProperty("nonConforming").EnumerateArray()
            .Single(x => x.GetProperty("value").GetString() == "test");
        Assert.Equal(1, row.GetProperty("changeCount").GetInt32());
        Assert.Equal(1, row.GetProperty("revisionCount").GetInt32());
        Assert.Equal(2, row.GetProperty("totalCount").GetInt32());
        // One requirement, reported once, with both authorities counted accurately.
        Assert.Equal(["SYSR-701001.00"], row.GetProperty("examples").EnumerateArray().Select(x => x.GetString()!).ToArray());

        using var scope2 = factory.Services.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        Assert.Equal("test",
            await db2.RequirementRevisions.AsNoTracking().Where(x => x.Id == revisionId)
                .Select(x => x.VerificationMethod).SingleAsync());
    }

    [Fact]
    public async Task A_level_without_verification_capability_keeps_its_not_applicable_semantics()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var seeded = await SeedAsync(factory);
        await SignInAsync(client, seeded.ManagerName);
        using (var configured = await client.PutAsJsonAsync(
                   $"/api/projects/{seeded.ProjectId}/verification-methods", new
                   {
                       expectedVersion = 1, reason = "Test only", methods = new[] { "Test" },
                   }))
            Assert.True(configured.IsSuccessStatusCode, await configured.Content.ReadAsStringAsync());
        using (var ladder = await client.PutAsJsonAsync($"/api/projects/{seeded.ProjectId}/configuration", new
               {
                   expectedVersion = 1,
                   reason = "Author Interface Control Documents above System",
                   steps = new[]
                   {
                       new { catalogueEntry = "Interface", position = 1, capabilities = 1 },
                       new { catalogueEntry = "System", position = 2, capabilities = 7 },
                   },
                   relationships = new[] { new { parent = "Interface", child = "System" } },
               }))
            Assert.True(ladder.IsSuccessStatusCode, await ladder.Content.ReadAsStringAsync());
        using (var activation = await client.PostAsJsonAsync(
                   $"/api/projects/{seeded.ProjectId}/configuration/activate",
                   new { expectedVersion = 2, reason = "Activate the Interface ladder" }))
            Assert.True(activation.IsSuccessStatusCode, await activation.Content.ReadAsStringAsync());

        var draft = await CreateDraftAsync(client, new
        {
            projectId = seeded.ProjectId,
            targetReleaseId = seeded.ReleaseId,
            type = "Interface",
            title = "Interface change",
            problem = "P", analysis = "A", solution = "S",
            requirementChanges = new[]
            {
                new { level = "Interface", kind = "Introduce", statement = "The interface shall preserve its contract.",
                    rationale = "Traceable interface ownership", verificationMethod = "Not applicable" },
            },
        });
        using var submit = await client.PostAsJsonAsync($"/api/change-requests/{draft.Id}/submit", new
        {
            expectedVersion = draft.Version,
            mode = "Sequential",
            approvers = new[] { new { userId = seeded.ApproverName, name = "Vocabulary Approver" } },
        });

        // An ICD has no verification artifact, so its changes carry the product's sentinel rather than a
        // method. Enforcement follows the effective ladder capability, so the sentinel is not held to a
        // vocabulary it was never meant to satisfy — and it is not reported as non-conforming either.
        Assert.True(submit.IsSuccessStatusCode, await submit.Content.ReadAsStringAsync());
        using var read = await client.GetAsync($"/api/projects/{seeded.ProjectId}/verification-methods");
        using var body = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
        Assert.Empty(body.RootElement.GetProperty("nonConforming").EnumerateArray());
    }

    [Fact]
    public async Task A_project_carrying_no_vocabulary_has_the_founding_one_materialized_at_submission()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var seeded = await SeedAsync(factory);
        using (var scope = factory.Services.CreateScope())
        {
            // A store that predates the #701 backfill. The submission authority must not answer from a
            // conventional in-memory set; it persists the founding vocabulary so the configuration screen and
            // the reconciliation report see exactly what review enforced.
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            db.ProjectVerificationMethods.RemoveRange(
                db.ProjectVerificationMethods.Where(x => x.ProjectId == seeded.ProjectId));
            db.ProjectVerificationVocabularies.RemoveRange(
                db.ProjectVerificationVocabularies.Where(x => x.ProjectId == seeded.ProjectId));
            await db.SaveChangesAsync();
        }

        await SignInAsync(client, seeded.AuthorName);
        using (var read = await client.GetAsync($"/api/projects/{seeded.ProjectId}/verification-methods"))
        {
            using var body = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
            Assert.False(body.RootElement.GetProperty("persisted").GetBoolean());
            Assert.Equal(0, body.RootElement.GetProperty("version").GetInt64());
        }

        var draft = await CreateDraftAsync(client, DraftBody(seeded, "Test"));
        using var submit = await SubmitAsync(client, seeded, draft);
        Assert.True(submit.IsSuccessStatusCode, await submit.Content.ReadAsStringAsync());

        using var scope2 = factory.Services.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var vocabulary = await db2.ProjectVerificationVocabularies.AsNoTracking().Include(x => x.Methods)
            .SingleAsync(x => x.ProjectId == seeded.ProjectId);
        Assert.Equal(["Test", "Analysis", "Inspection", "Demonstration"], vocabulary.OrderedValues);
        var audit = await db2.SecurityAuditEvents.AsNoTracking()
            .SingleAsync(x => x.EventType == "VerificationVocabularyMaterialized");
        Assert.Equal(seeded.AuthorName, audit.ActorId);
    }

    [Fact]
    public async Task A_refused_submission_against_an_unpersisted_vocabulary_materializes_nothing()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var seeded = await SeedAsync(factory);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            db.ProjectVerificationMethods.RemoveRange(
                db.ProjectVerificationMethods.Where(x => x.ProjectId == seeded.ProjectId));
            db.ProjectVerificationVocabularies.RemoveRange(
                db.ProjectVerificationVocabularies.Where(x => x.ProjectId == seeded.ProjectId));
            await db.SaveChangesAsync();
        }

        await SignInAsync(client, seeded.AuthorName);
        var draft = await CreateDraftAsync(client, DraftBody(seeded, "Testing"));
        using var submit = await SubmitAsync(client, seeded, draft);
        Assert.Equal(HttpStatusCode.BadRequest, submit.StatusCode);

        using var scope2 = factory.Services.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        // The materialization commits with the submission or not at all; a refused submission leaves the
        // project exactly as it was.
        Assert.Empty(await db2.ProjectVerificationVocabularies.AsNoTracking()
            .Where(x => x.ProjectId == seeded.ProjectId).ToListAsync());
    }

    private static string[] Methods(JsonElement root) =>
        root.GetProperty("methods").EnumerateArray().Select(x => x.GetString()!).ToArray();
}
