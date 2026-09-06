using System.Net;
using System.Net.Http.Json;
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
/// One change-request target-release invariant, enforced everywhere a change request is born or moved.
///
/// Creation used to check the target release by identifier alone, so a foreign project's unreleased build
/// passed; the import commit checked nothing at all; and ReqIF, integration, and OSLC creation never asked
/// whether the build had been released. Retargeting was the only path with the full check (#849 Finding 4).
/// These prove the shared guard at every entry point: foreign and nonexistent releases are
/// indistinguishable, a released build of the same project is refused with its own lifecycle code, an
/// eligible build succeeds, rejection precedes identifier allocation, and every path refuses before any
/// durable side effect.
/// </summary>
public sealed class ChangeRequestTargetReleaseGuardApiTests
{
    private const string Engineer = "guard.engineer";

    private sealed record Releases(Guid Eligible, Guid SecondEligible, Guid Released, Guid Foreign);

    private static async Task<(Guid ProjectA, Guid ProjectB, Releases TargetReleases)> SeedAsync(AeroLinkApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;
        var program = new ProgramRecord("Guard Program", $"GRD{Guid.NewGuid():N}"[..12]);
        var projectA = new ProjectRecord(program.Id, "Guard Product A", "A");
        var projectB = new ProjectRecord(program.Id, "Guard Product B", "B");
        var eligible = new SoftwareRelease(projectA.Id, "1.0", false);
        var second = new SoftwareRelease(projectA.Id, "2.0", false);
        var released = new SoftwareRelease(projectA.Id, "0.9", true);
        var foreign = new SoftwareRelease(projectB.Id, "9.9", false);
        var account = new UserAccount(Engineer, Engineer, $"{Engineer}@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        db.AddRange(program, projectA, projectB, eligible, second, released, foreign, account,
            new ProgramMembership(account.Id, program.Id, ProgramRole.Engineer, "test.setup", now),
            new ProgramMembership(account.Id, program.Id, ProgramRole.Administrator, "test.setup", now));
        await db.SaveChangesAsync();
        return (projectA.Id, projectB.Id, new Releases(eligible.Id, second.Id, released.Id, foreign.Id));
    }

    private static async Task SignInAsync(HttpClient client, string userName)
    {
        using var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }

    private static async Task<JsonElement> ErrorAsync(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(problem.TryGetProperty("code", out var code), problem.GetRawText());
        return code;
    }

    private static async Task<long> ScrSequenceCountAsync(AeroLinkApiFactory factory) =>
        await factory.Services.CreateScope().ServiceProvider.GetRequiredService<AeroLinkDbContext>()
            .IdentifierSequences.AsNoTracking().Where(x => x.Scope == "SRCR").LongCountAsync();

    private static async Task<int> ScrCountAsync(AeroLinkApiFactory factory, Guid projectId) =>
        await factory.Services.CreateScope().ServiceProvider.GetRequiredService<AeroLinkDbContext>()
            .SystemChangeRequests.AsNoTracking().Where(x => x.ProjectId == projectId).CountAsync();

    [Fact]
    public async Task Creation_shares_one_not_found_posture_and_refuses_released_and_foreign_releases()
    {
        using var factory = new AeroLinkApiFactory();
        var (projectA, _, releases) = await SeedAsync(factory);

        using var client = factory.CreateClient();
        await SignInAsync(client, Engineer);

        using var foreign = await client.PostAsJsonAsync("/api/change-requests",
            new { projectId = projectA, targetReleaseId = releases.Foreign, title = "T", problem = "P", analysis = "A", solution = "S", type = "System" });
        using var nonexistent = await client.PostAsJsonAsync("/api/change-requests",
            new { projectId = projectA, targetReleaseId = Guid.NewGuid(), title = "T", problem = "P", analysis = "A", solution = "S", type = "System" });
        Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, nonexistent.StatusCode);
        Assert.Equal(await foreign.Content.ReadAsStringAsync(), await nonexistent.Content.ReadAsStringAsync());
        Assert.Equal("target_release_not_found", (await ErrorAsync(foreign)).GetString());

        using var released = await client.PostAsJsonAsync("/api/change-requests",
            new { projectId = projectA, targetReleaseId = releases.Released, title = "T", problem = "P", analysis = "A", solution = "S", type = "System" });
        Assert.Equal(HttpStatusCode.BadRequest, released.StatusCode);
        Assert.Equal("release_is_closed", (await ErrorAsync(released)).GetString());

        // Rejection happened before any identifier allocation or durable record.
        Assert.Equal(0, await ScrSequenceCountAsync(factory));
        Assert.Equal(0, await ScrCountAsync(factory, projectA));

        using var eligible = await client.PostAsJsonAsync("/api/change-requests",
            new { projectId = projectA, targetReleaseId = releases.Eligible, title = "T", problem = "P", analysis = "A", solution = "S", type = "System" });
        Assert.Equal(HttpStatusCode.Created, eligible.StatusCode);
    }

    [Fact]
    public async Task Draft_creation_shares_the_same_invariant()
    {
        using var factory = new AeroLinkApiFactory();
        var (projectA, _, releases) = await SeedAsync(factory);

        using var client = factory.CreateClient();
        await SignInAsync(client, Engineer);

        using var foreign = await client.PostAsJsonAsync("/api/change-request-drafts",
            new { projectId = projectA, targetReleaseId = releases.Foreign, title = "T", problem = "P", analysis = "A", solution = "S", type = "System", requirementChanges = Array.Empty<object>() });
        using var released = await client.PostAsJsonAsync("/api/change-request-drafts",
            new { projectId = projectA, targetReleaseId = releases.Released, title = "T", problem = "P", analysis = "A", solution = "S", type = "System", requirementChanges = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);
        Assert.Equal("target_release_not_found", (await ErrorAsync(foreign)).GetString());
        Assert.Equal(HttpStatusCode.BadRequest, released.StatusCode);
        Assert.Equal("release_is_closed", (await ErrorAsync(released)).GetString());
        Assert.Equal(0, await ScrSequenceCountAsync(factory));
    }

    [Fact]
    public async Task Retargeting_shares_the_invariant()
    {
        using var factory = new AeroLinkApiFactory();
        var (projectA, _, releases) = await SeedAsync(factory);

        using var client = factory.CreateClient();
        await SignInAsync(client, Engineer);
        using var created = await client.PostAsJsonAsync("/api/change-requests",
            new { projectId = projectA, targetReleaseId = releases.Eligible, title = "T", problem = "P", analysis = "A", solution = "S", type = "System" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var scrId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        using var foreign = await client.PostAsJsonAsync($"/api/change-requests/{scrId}/retarget",
            new { targetReleaseId = releases.Foreign, reason = "Testing the guard." });
        using var nonexistent = await client.PostAsJsonAsync($"/api/change-requests/{scrId}/retarget",
            new { targetReleaseId = Guid.NewGuid(), reason = "Testing the guard." });
        using var releasedTarget = await client.PostAsJsonAsync($"/api/change-requests/{scrId}/retarget",
            new { targetReleaseId = releases.Released, reason = "Testing the guard." });
        Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);
        Assert.Equal("target_release_not_found", (await ErrorAsync(foreign)).GetString());
        Assert.Equal(await foreign.Content.ReadAsStringAsync(), await nonexistent.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, releasedTarget.StatusCode);
        Assert.Equal("release_is_closed", (await ErrorAsync(releasedTarget)).GetString());

        using var eligible = await client.PostAsJsonAsync($"/api/change-requests/{scrId}/retarget",
            new { targetReleaseId = releases.SecondEligible, reason = "Moving to the next build." });
        Assert.Equal(HttpStatusCode.OK, eligible.StatusCode);
    }

    [Fact]
    public async Task Import_commit_rejects_a_target_release_before_allocating_an_identifier()
    {
        using var factory = new AeroLinkApiFactory();
        var (projectA, _, releases) = await SeedAsync(factory);
        Guid jobId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var job = new RequirementInterchangeJob(projectA, "rows.json", "hash", "{}", "[]", 0, 0, Engineer, DateTimeOffset.UtcNow);
            db.Add(job);
            await db.SaveChangesAsync();
            jobId = job.Id;
        }

        using var client = factory.CreateClient();
        await SignInAsync(client, Engineer);
        using var foreign = await client.PostAsJsonAsync($"/api/enterprise-requirements/import/{jobId}/commit",
            new { targetReleaseId = releases.Foreign, title = "T", problem = "P", analysis = "A", solution = "S", type = "System" });
        using var nonexistent = await client.PostAsJsonAsync($"/api/enterprise-requirements/import/{jobId}/commit",
            new { targetReleaseId = Guid.NewGuid(), title = "T", problem = "P", analysis = "A", solution = "S", type = "System" });
        using var releasedTarget = await client.PostAsJsonAsync($"/api/enterprise-requirements/import/{jobId}/commit",
            new { targetReleaseId = releases.Released, title = "T", problem = "P", analysis = "A", solution = "S", type = "System" });
        Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);
        Assert.Equal("target_release_not_found", (await ErrorAsync(foreign)).GetString());
        Assert.Equal(HttpStatusCode.BadRequest, nonexistent.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, releasedTarget.StatusCode);
        Assert.Equal("release_is_closed", (await ErrorAsync(releasedTarget)).GetString());

        Assert.Equal(0, await ScrSequenceCountAsync(factory));
        Assert.Equal(0, await ScrCountAsync(factory, projectA));

        using var eligible = await client.PostAsJsonAsync($"/api/enterprise-requirements/import/{jobId}/commit",
            new { targetReleaseId = releases.Eligible, title = "T", problem = "P", analysis = "A", solution = "S", type = "System" });
        Assert.Equal(HttpStatusCode.Created, eligible.StatusCode);
    }

    [Fact]
    public async Task Reqif_import_commit_rejects_foreign_nonexistent_and_released_releases()
    {
        using var factory = new AeroLinkApiFactory();
        var (projectA, _, releases) = await SeedAsync(factory);

        const string xml = """
            <REQ-IF xmlns="http://www.omg.org/spec/ReqIF/20110401/reqif.xsd">
              <REQ-IF-CONTENT>
                <SPEC-TYPES>
                  <ATTRIBUTE-DEFINITION-STRING IDENTIFIER="AD-IDENTIFIER" LONG-NAME="AeroLink.Identifier" />
                  <ATTRIBUTE-DEFINITION-STRING IDENTIFIER="AD-STATEMENT" LONG-NAME="AeroLink.Statement" />
                </SPEC-TYPES>
                <SPEC-OBJECTS>
                  <SPEC-OBJECT IDENTIFIER="OBJ-1"><VALUES>
                    <ATTRIBUTE-VALUE-STRING THE-VALUE="SYSR-000500"><DEFINITION><ATTRIBUTE-DEFINITION-STRING-REF>AD-IDENTIFIER</ATTRIBUTE-DEFINITION-STRING-REF></DEFINITION></ATTRIBUTE-VALUE-STRING>
                    <ATTRIBUTE-VALUE-STRING THE-VALUE="Imported statement"><DEFINITION><ATTRIBUTE-DEFINITION-STRING-REF>AD-STATEMENT</ATTRIBUTE-DEFINITION-STRING-REF></DEFINITION></ATTRIBUTE-VALUE-STRING>
                  </VALUES></SPEC-OBJECT>
                </SPEC-OBJECTS>
              </REQ-IF-CONTENT>
            </REQ-IF>
            """;
        using var client = factory.CreateClient();
        await SignInAsync(client, Engineer);
        Guid jobId;
        using (var preview = await client.PostAsync("/api/reqif/imports/preview?projectId=" + projectA,
            new MultipartFormDataContent
            {
                { new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(xml)) { Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/xml") } }, "file", "import.reqif" },
            }))
        {
            Assert.True(preview.StatusCode == HttpStatusCode.OK, await preview.Content.ReadAsStringAsync());
            jobId = (await preview.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("job").GetProperty("id").GetGuid();
        }
        using var processed = await client.PostAsJsonAsync($"/api/reqif/jobs/{jobId}/process", new { batchSize = 100 });
        Assert.Equal(HttpStatusCode.OK, processed.StatusCode);

        async Task<HttpResponseMessage> CommitAsync(Guid targetReleaseId) =>
            await client.PostAsJsonAsync($"/api/reqif/jobs/{jobId}/commit",
                new { targetReleaseId, title = "T", problem = "P", analysis = "A", solution = "S", type = "System" });

        using var foreign = await CommitAsync(releases.Foreign);
        Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);
        Assert.Equal("target_release_not_found", (await ErrorAsync(foreign)).GetString());
        using var releasedTarget = await CommitAsync(releases.Released);
        Assert.Equal(HttpStatusCode.BadRequest, releasedTarget.StatusCode);
        Assert.Equal("release_is_closed", (await ErrorAsync(releasedTarget)).GetString());

        using var eligible = await CommitAsync(releases.Eligible);
        Assert.Equal(HttpStatusCode.Created, eligible.StatusCode);
    }

    [Fact]
    public async Task Service_conditional_writes_reject_released_and_foreign_releases()
    {
        using var factory = new AeroLinkApiFactory();
        var (projectA, _, releases) = await SeedAsync(factory);
        Guid artifactId; string identityKey;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var now = DateTimeOffset.UtcNow;
            var origin = new SystemChangeRequest("SRCR-00001", 0, projectA, db.Releases.Single(x => x.Id == releases.Eligible).Id,
                "Origin", "P", "A", "S", Engineer, now);
            var baseline = new CandidateBaseline("SW-01.00", 0, projectA, releases.Eligible, null, "Baseline", "cm", now);
            var artifact = new RequirementArtifact(projectA, "SYSR-000001", RequirementLevel.System, now);
            var revision = new RequirementRevision(artifact.Id, 0, "Statement", "Rationale", "Test",
                RequirementRevisionState.Active, origin.Id, baseline.Id, now);
            db.AddRange(origin, baseline, artifact, revision);
            await db.SaveChangesAsync();
            artifactId = artifact.Id;
        }

        using var client = factory.CreateClient();
        await SignInAsync(client, Engineer);
        // Program Administrator membership is the product's stewardship capability that may mint service identities.
        using var identity = await client.PostAsJsonAsync("/api/integrations/service-identities",
            new { projectId = projectA, name = "Guard pipeline", scopes = new[] { "requirements:read", "requirements:write" } });
        Assert.Equal(HttpStatusCode.Created, identity.StatusCode);
        identityKey = (await identity.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("apiKey").GetString()!;

        using var service = factory.CreateClient();
        service.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", identityKey);
        using var read = await service.GetAsync($"/api/v1/requirements/{artifactId}");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var etag = read.Headers.ETag!.ToString();

        async Task<HttpResponseMessage> ProposeAsync(Guid targetReleaseId)
        {
            using var put = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/requirements/{artifactId}")
            {
                Content = JsonContent.Create(new
                {
                    targetReleaseId,
                    title = "Service change",
                    analysis = "A",
                    solution = "S",
                    statement = "The controller shall retry.",
                    rationale = "R",
                    verificationMethod = "Test",
                    type = "System",
                }),
            };
            put.Headers.TryAddWithoutValidation("If-Match", etag);
            return await service.SendAsync(put);
        }

        using var released = await ProposeAsync(releases.Released);
        Assert.Equal(HttpStatusCode.BadRequest, released.StatusCode);
        Assert.Equal("release_is_closed", (await ErrorAsync(released)).GetString());
        using var foreignWrite = await ProposeAsync(releases.Foreign);
        Assert.Equal(HttpStatusCode.BadRequest, foreignWrite.StatusCode);
        Assert.Equal("target_release_not_found", (await ErrorAsync(foreignWrite)).GetString());
        using var nonexistentWrite = await ProposeAsync(Guid.NewGuid());
        Assert.Equal(HttpStatusCode.BadRequest, nonexistentWrite.StatusCode);
        Assert.Equal(await foreignWrite.Content.ReadAsStringAsync(), await nonexistentWrite.Content.ReadAsStringAsync());

        using var accepted = await ProposeAsync(releases.Eligible);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
    }

    [Fact]
    public async Task Oslc_consumption_rejects_released_and_foreign_releases()
    {
        using var factory = new AeroLinkApiFactory();
        var (projectA, _, releases) = await SeedAsync(factory);
        string identityKey; Guid mappingId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            db.Add(new RequirementImportMapping(projectA, "Guard mapping", "{}", Engineer, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
            mappingId = await db.RequirementImportMappings.AsNoTracking()
                .Where(x => x.ProjectId == projectA).Select(x => x.Id).SingleAsync();
        }

        using var client = factory.CreateClient();
        await SignInAsync(client, Engineer);
        // Program Administrator membership is the product's stewardship capability that may mint service identities.
        using var identity = await client.PostAsJsonAsync("/api/integrations/service-identities",
            new { projectId = projectA, name = "Guard OSLC", scopes = new[] { "oslc:write" } });
        Assert.Equal(HttpStatusCode.Created, identity.StatusCode);
        identityKey = (await identity.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("apiKey").GetString()!;

        using var service = factory.CreateClient();
        service.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", identityKey);
        service.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));

        async Task<HttpResponseMessage> ConsumeAsync(Guid targetReleaseId) =>
            await service.PostAsJsonAsync("/api/v1/oslc/rm/consume", new
            {
                targetReleaseId,
                mappingId,
                sourceUri = "https://external.example/rm/req-1",
                sourceEtag = "\"1\"",
                externalIdentifier = "EXT-1",
                localIdentifier = "SYSR-000001",
                level = "System",
                statement = "The consumed requirement statement.",
                rationale = "R",
                verificationMethod = "Test",
                title = "Consumed change",
                analysis = "A",
                solution = "S",
                type = "System",
            });

        using var released = await ConsumeAsync(releases.Released);
        Assert.Equal(HttpStatusCode.BadRequest, released.StatusCode);
        Assert.Equal("release_is_closed", (await ErrorAsync(released)).GetString());
        using var foreign = await ConsumeAsync(releases.Foreign);
        Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);
        Assert.Equal("target_release_not_found", (await ErrorAsync(foreign)).GetString());

        using var accepted = await ConsumeAsync(releases.Eligible);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
    }

    [Fact]
    public async Task Proposal_keeps_its_stable_released_build_contract()
    {
        using var factory = new AeroLinkApiFactory();
        var (projectA, _, releases) = await SeedAsync(factory);
        Guid artifactId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var now = DateTimeOffset.UtcNow;
            var origin = new SystemChangeRequest("SRCR-00001", 0, projectA, releases.Eligible,
                "Origin", "P", "A", "S", Engineer, now);
            var baseline = new CandidateBaseline("SW-01.00", 0, projectA, releases.Eligible, null, "Baseline", "cm", now);
            var artifact = new RequirementArtifact(projectA, "SYSR-000001", RequirementLevel.System, now);
            var revision = new RequirementRevision(artifact.Id, 0, "Statement", "Rationale", "Test",
                RequirementRevisionState.Active, origin.Id, baseline.Id, now);
            db.AddRange(origin, baseline, artifact, revision);
            await db.SaveChangesAsync();
            artifactId = artifact.Id;
        }

        using var client = factory.CreateClient();
        await SignInAsync(client, Engineer);
        using var released = await client.PostAsJsonAsync($"/api/enterprise-requirements/{artifactId}/propose",
            new { targetReleaseId = releases.Released, kind = "Modify", statement = "S" });
        Assert.Equal(HttpStatusCode.BadRequest, released.StatusCode);
        Assert.Equal("released_build_read_only", (await ErrorAsync(released)).GetString());

        using var foreign = await client.PostAsJsonAsync($"/api/enterprise-requirements/{artifactId}/propose",
            new { targetReleaseId = releases.Foreign, kind = "Modify", statement = "S" });
        Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);
        Assert.Equal("target_release_not_found", (await ErrorAsync(foreign)).GetString());
    }
}
