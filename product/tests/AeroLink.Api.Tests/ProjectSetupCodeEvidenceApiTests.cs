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
        await QualifyAsync(factory);
    }

    internal static async Task QualifyAsync(AeroLinkApiFactory factory,
        Func<HttpClient, Guid, long, Func<Task<HttpResponseMessage>>, Task>? acceptMerge = null)
    {
        var project = await ProjectSetupServiceQualificationTests.QualifyAsync(factory);
        // Only the remote transport is replaced. The endpoint, probe, observation and persistence run normally.
        using var transport = new ObservedGitLabProject();
        using var remote = new HttpClient(transport);
        using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.AddSingleton(new GitLabProjectConnectionProbe(remote, Options.Create(new ProjectGitLabOptions
            {
                BaseUrl = "https://code.example.test", ReadAccessToken = "isolated-fixture-token",
            })));
            services.Configure<ProjectGitLabOptions>(options =>
            { options.BaseUrl = "https://code.example.test"; options.ReadAccessToken = "isolated-fixture-token"; });
            services.AddHttpClient<GitLabMetadataReader>().ConfigurePrimaryHttpMessageHandler(() => transport);
        }));
        using var client = configured.CreateClient();
        using (var login = await client.PostAsJsonAsync("/api/auth/login", new
        { userName = "admin", password = AeroLinkApiFactory.AdministratorPassword })) await Success(login);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);

        // Controlled baseline fixture is local to this disposable database. It supplies the later lifecycle
        // prerequisite so repository refusal cannot accidentally be hidden by an absent baseline.
        Guid baselineId;
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
            baselineId = baseline.Id;
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
        await SourceRefusal(1);
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
        await SourceRefusal(configurationBody.GetProperty("version").GetInt64());
        using var verified = await client.PostAsJsonAsync($"/api/projects/{project.ProjectId}/repository/verify",
            new { expectedVersion = configurationBody.GetProperty("version").GetInt64() });
        await Success(verified);
        var observed = await verified.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Verified", observed.GetProperty("repository").GetProperty("status").GetString());
        Assert.Equal(72, observed.GetProperty("repository").GetProperty("remoteProjectId").GetInt32());
        var version = observed.GetProperty("repository").GetProperty("version").GetInt64();
        using var selected = await PostSource(version);
        await Success(selected);
        var source = await selected.Content.ReadFromJsonAsync<JsonElement>();
        var linkRequest = new
        {
            releaseId = project.ReleaseId, mergeRequestIid = 12, targetKind = "RequirementRevision",
            targetId = artifacts[0].RevisionId, meaning = "Implements", expectedConfigurationVersion = version,
            sourceSnapshotId = source.GetProperty("snapshotId").GetGuid(),
            sourceSelectionEventId = source.GetProperty("selectionEventId").GetGuid(),
        };
        foreach (var mismatch in new[] { "project", "path", "origin" })
        {
            transport.Mismatch = mismatch;
            using var refused = await client.PostAsJsonAsync($"/api/projects/{project.ProjectId}/code/relationships/merge-requests", linkRequest);
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            using var checkScope = configured.Services.CreateScope();
            Assert.Empty(await checkScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>().GitLabMergeRequestRelationships.ToListAsync());
        }
        transport.Mismatch = null;
        using var linked = await client.PostAsJsonAsync($"/api/projects/{project.ProjectId}/code/relationships/merge-requests", linkRequest);
        await Success(linked);
        var relationship = await linked.Content.ReadFromJsonAsync<JsonElement>();
        if (acceptMerge is null)
        {
            using var accepted = await PostMerge();
            await Success(accepted);
        }
        else await acceptMerge(client, project.ProjectId, version, PostMerge);
        var completed = await client.GetFromJsonAsync<JsonElement>($"/api/code-traceability?projectId={project.ProjectId}&releaseId={project.ReleaseId}");
        Assert.Equal(2, completed.GetProperty("summary").GetProperty("mapped").GetInt32());
        Assert.True(completed.GetProperty("summary").GetProperty("gateComplete").GetBoolean());
        var evidence = completed.GetProperty("requirements").EnumerateArray().Select(x => x.GetProperty("evidence"))
            .Single(x => x.ValueKind != JsonValueKind.Null);
        Assert.Equal("GitLabContributions", evidence.GetProperty("disposition").GetString());
        var contribution = Assert.Single(evidence.GetProperty("contributions").EnumerateArray());
        Assert.Equal(72, contribution.GetProperty("remoteProjectId").GetInt64());
        Assert.Equal("https://code.example.test", contribution.GetProperty("instanceBaseUrl").GetString());
        Assert.Equal("company/software", contribution.GetProperty("repositoryPath").GetString());
        Assert.Equal("https://code.example.test/company/software/-/merge_requests/12", contribution.GetProperty("mergeRequestUrl").GetString());
        Assert.Equal("admin", contribution.GetProperty("recordedBy").GetString());
        Assert.Equal(ObservedGitLabProject.Sha, contribution.GetProperty("commitSha").GetString());
        Assert.Equal(ObservedGitLabProject.Sha, contribution.GetProperty("mergeResultSha").GetString());
        using var verification = configured.Services.CreateScope();
        var verifyDb = verification.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var snapshotId = source.GetProperty("snapshotId").GetGuid();
        var snapshot = await verifyDb.GitLabSourceSnapshots.SingleAsync(x => x.Id == snapshotId);
        Assert.Equal(version, snapshot.ConfigurationVersion);
        Assert.Equal(ObservedGitLabProject.Sha, snapshot.CommitSha);
        Assert.Equal("admin", snapshot.RecordedBy);
        Assert.Equal(72, snapshot.RemoteProjectId);
        Assert.Equal("company/software", snapshot.PathWithNamespace);
        var set = Assert.Single(await verifyDb.CodeEvidenceDispositionSets.ToListAsync());
        Assert.Equal(project.ProjectId, set.ProjectId);
        Assert.Equal(project.ReleaseId, set.ReleaseId);
        Assert.Equal(snapshotId, set.SourceSnapshotId);
        Assert.Equal(source.GetProperty("selectionEventId").GetGuid(), set.SourceSelectionEventId);
        Assert.Equal(artifacts[0].RevisionId, set.RequirementRevisionId);
        var legacy = Assert.Single(await verifyDb.CodeTraceabilityRecords.ToListAsync());
        Assert.Equal(project.ProjectId, legacy.ProjectId);
        Assert.Equal(project.ReleaseId, legacy.ReleaseId);
        Assert.False(legacy.IsDemonstration);

        Task<HttpResponseMessage> PostSource(long configurationVersion) => client.PostAsJsonAsync(
            $"/api/projects/{project.ProjectId}/code/source", new
        {
            releaseId = project.ReleaseId, reference = ObservedGitLabProject.Sha, referenceKind = "Commit",
            previewSha = ObservedGitLabProject.Sha, expectedConfigurationVersion = configurationVersion,
            expectedSelectionVersion = 0,
        });
        Task<HttpResponseMessage> PostMerge() => client.PostAsJsonAsync($"/api/projects/{project.ProjectId}/code/evidence", new
        {
            releaseId = project.ReleaseId, expectedBaselineId = baselineId,
            requirementArtifactId = artifacts[0].ArtifactId, requirementRevisionId = artifacts[0].RevisionId,
            disposition = "GitLabContributions", expectedSelectorVersion = 0,
            expectedConfigurationVersion = version, expectedSourceSelectionVersion = source.GetProperty("version").GetInt64(),
            expectedSourceSnapshotId = source.GetProperty("snapshotId").GetGuid(),
            expectedSourceSelectionEventId = source.GetProperty("selectionEventId").GetGuid(),
            contributions = new[] { new { kind = "MergeRequest",
                relationshipId = relationship.GetProperty("relationshipId").GetGuid(),
                expectedRelationshipVersion = relationship.GetProperty("version").GetInt64() } },
        });
        async Task SourceRefusal(long configurationVersion)
        {
            using var response = await PostSource(configurationVersion);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("repository_changed", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
            using var checkScope = configured.Services.CreateScope();
            var db = checkScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            Assert.Empty(await db.GitLabSourceSnapshots.ToListAsync());
            Assert.Empty(await db.GitLabSourceSelectionEvents.ToListAsync());
            Assert.Empty(await db.GitLabCurrentSourceSelections.ToListAsync());
        }
        static async Task Success(HttpResponseMessage response) =>
            Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    private sealed class ObservedGitLabProject : HttpMessageHandler
    {
        public static readonly string Sha = new('b', 40);
        public string? Mismatch { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://code.example.test", request.RequestUri!.GetLeftPart(UriPartial.Authority));
            var path = request.RequestUri!.AbsolutePath;
            object body;
            if (path == "/api/v4/projects/company%2Fsoftware")
                body = new { id = 72, path_with_namespace = "company/software", web_url = "https://code.example.test/company/software" };
            else if (path == "/api/v4/projects/72/repository/commits/" + Sha || path == "/api/v4/projects/72/repository/merge_base")
                body = new { id = Sha };
            else if (path == "/api/v4/projects/72/merge_requests/12")
                body = new { id = 1200, project_id = Mismatch == "project" ? 73 : 72, iid = 12, title = "Observed merge", state = "merged", draft = false,
                    web_url = Mismatch == "origin" ? "https://foreign.test/company/software/-/merge_requests/12"
                        : Mismatch == "path" ? "https://code.example.test/other/project/-/merge_requests/12"
                        : "https://code.example.test/company/software/-/merge_requests/12", sha = Sha,
                    merge_commit_sha = Sha, merged_at = "2026-09-19T12:00:00Z" };
            else throw new InvalidOperationException("Unexpected provider request: " + request.RequestUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body) });
        }
    }
}
