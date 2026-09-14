using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AeroLink.Api.Tests;

public sealed class ProjectSetupCodeEvidenceApiTests
{
    [Fact]
    public async Task Fresh_project_defers_repository_then_requires_observed_matching_identity_for_GitLab_evidence()
    {
        using var factory = new AeroLinkApiFactory();
        var project = await ProjectSetupServiceQualificationTests.QualifyAsync(factory);
        // Only the remote transport is replaced. The endpoint, probe, observation and persistence run normally.
        using var remote = new HttpClient(new ObservedGitLabProject());
        using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddSingleton(new GitLabProjectConnectionProbe(remote, Options.Create(new ProjectGitLabOptions
            {
                BaseUrl = "https://code.example.test", ReadAccessToken = "isolated-fixture-token",
            })))));
        using var client = configured.CreateClient();
        using (var login = await client.PostAsJsonAsync("/api/auth/login", new
        { userName = "admin", password = AeroLinkApiFactory.AdministratorPassword })) await Success(login);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);

        // Controlled baseline fixture is local to this disposable database. It supplies the later lifecycle
        // prerequisite so repository refusal cannot accidentally be hidden by an absent baseline.
        var artifacts = new List<(Guid ArtifactId, Guid RevisionId)>();
        using (var scope = configured.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var now = DateTimeOffset.UtcNow;
            var change = new SystemChangeRequest("LLRCR-991037", 0, project.ProjectId, project.ReleaseId,
                "Isolated implementation requirements", "Fixture", "Fixture", "Fixture", "fixture.author", now,
                ChangeRequestType.Software, softwareLevel: RequirementLevel.LowLevel);
            for (var number = 1; number <= 2; number++)
                change.AddRequirementChange("fixture.author", $"LLR-99103{number}", 0, RequirementLevel.LowLevel,
                    RequirementChangeKind.Introduce, "The fixture shall retain its operating mode.", "Isolated code evidence fixture.",
                    "Test", now, attributesJson: "{\"derived\":true}");
            change.SubmitForReview("fixture.author", [new ApproverSelection("fixture.reviewer", "Fixture reviewer")], now);
            change.ApproveActiveStage("fixture.reviewer", now);
            var baseline = new CandidateBaseline("SW-01.30", 0, project.ProjectId, project.ReleaseId, null,
                "Isolated code population", "fixture.cm", now);
            baseline.Select(change, "fixture.cm", now);
            baseline.Freeze("fixture.cm", now);
            baseline.MarkRequirementsMaterialized("fixture.cm", new string('a', 64), 2, now);
            db.AddRange(change, baseline, new ReleaseCampaign(project.ProjectId, project.ReleaseId, baseline.Id,
                "Isolated code qualification", "fixture.cm", now));
            for (var number = 1; number <= 2; number++)
            {
                var artifact = new RequirementArtifact(project.ProjectId, $"LLR-99103{number}", RequirementLevel.LowLevel, now);
                var revision = new RequirementRevision(artifact.Id, 0, "The fixture shall retain its operating mode.",
                    "Isolated code evidence fixture.", "Test", RequirementRevisionState.Active, change.Id, baseline.Id, now,
                    parentKind: RequirementParentKind.Derived, derivedRationale: "Isolated derived requirement fixture.");
                db.AddRange(artifact, revision, new BaselineRequirementSelection(baseline.Id, artifact.Id, revision.Id));
                artifacts.Add((artifact.Id, revision.Id));
            }
            await db.SaveChangesAsync();
        }

        var overview = await client.GetFromJsonAsync<JsonElement>($"/api/code-traceability?projectId={project.ProjectId}&releaseId={project.ReleaseId}");
        Assert.Equal("Pending", overview.GetProperty("repository").GetProperty("status").GetString());
        Assert.False(overview.GetProperty("repository").GetProperty("canRecordGitLabMerge").GetBoolean());
        await Refusal("repository_pending", "company/software", "https://code.example.test/company/software/-/merge_requests/12");
        using (var noCode = await client.PostAsJsonAsync("/api/code-traceability", new
        {
            projectId = project.ProjectId, releaseId = project.ReleaseId,
            requirementArtifactId = artifacts[1].ArtifactId, requirementRevisionId = artifacts[1].RevisionId,
            disposition = "NoCodeChangeRequired", noCodeChangeRationale = "The existing implementation already meets this clarification.",
        })) await Success(noCode);
        using var configuration = await client.PutAsJsonAsync($"/api/projects/{project.ProjectId}/repository", new
        {
            expectedVersion = 1, mode = "ConnectNow", provider = "GitLab", endpoint = "https://code.example.test/company/software",
        });
        await Success(configuration);
        var configurationBody = await configuration.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ConfiguredUnverified", configurationBody.GetProperty("status").GetString());
        await Refusal("repository_unverified", "company/software", "https://code.example.test/company/software/-/merge_requests/12");
        using var verified = await client.PostAsJsonAsync($"/api/projects/{project.ProjectId}/repository/verify",
            new { expectedVersion = configurationBody.GetProperty("version").GetInt64() });
        await Success(verified);
        var observed = await verified.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Verified", observed.GetProperty("repository").GetProperty("status").GetString());
        Assert.Equal(72, observed.GetProperty("repository").GetProperty("remoteProjectId").GetInt32());
        await Refusal("repository_identity_mismatch", "other/project", "https://code.example.test/company/software/-/merge_requests/12");
        await Refusal("repository_identity_mismatch", "company/software", "https://gitlab.foreign.test/company/software/-/merge_requests/12");
        using (var accepted = await PostMerge("company/software", "https://code.example.test/company/software/-/merge_requests/12"))
            await Success(accepted);
        var completed = await client.GetFromJsonAsync<JsonElement>($"/api/code-traceability?projectId={project.ProjectId}&releaseId={project.ReleaseId}");
        Assert.Equal(2, completed.GetProperty("summary").GetProperty("mapped").GetInt32());
        Assert.True(completed.GetProperty("summary").GetProperty("gateComplete").GetBoolean());
        using var verification = configured.Services.CreateScope();
        var verifyDb = verification.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        Assert.All(await verifyDb.CodeTraceabilityRecords.ToListAsync(), x =>
        { Assert.Equal(project.ProjectId, x.ProjectId); Assert.Equal(project.ReleaseId, x.ReleaseId); Assert.False(x.IsDemonstration); });

        Task<HttpResponseMessage> PostMerge(string path, string url) => client.PostAsJsonAsync("/api/code-traceability", new
        {
            projectId = project.ProjectId, releaseId = project.ReleaseId, requirementArtifactId = artifacts[0].ArtifactId,
            requirementRevisionId = artifacts[0].RevisionId, disposition = "GitLabMerge", repositoryPath = path,
            mergeRequestReference = "!12", mergeRequestTitle = "Isolated merge assertion", mergeRequestUrl = url,
            mergeCommitSha = new string('b', 40), mergedAt = DateTimeOffset.UtcNow,
        });
        async Task Refusal(string code, string path, string url)
        {
            using var response = await PostMerge(path, url);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal(code, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
        static async Task Success(HttpResponseMessage response) =>
            Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    private sealed class ObservedGitLabProject : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://code.example.test/api/v4/projects/company%2Fsoftware", request.RequestUri!.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { id = 72, path_with_namespace = "company/software", web_url = "https://code.example.test/company/software" }),
            });
        }
    }
}
