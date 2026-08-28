using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Releases;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

public sealed class ProjectConfigurationApiTests : IClassFixture<SharedApiHost>
{
    private readonly SharedApiHost _host;

    public ProjectConfigurationApiTests(SharedApiHost host) => _host = host;

    private sealed record Seeded(Guid ProjectId, Guid ReleaseId, string ManagerName, string MemberName);

    private static async Task<Seeded> SeedAsync(AeroLinkApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var tag = Guid.NewGuid().ToString("N")[..8];
        var now = DateTimeOffset.UtcNow;
        var program = new ProgramRecord($"Ladder API {tag}", $"LAD{tag}");
        var project = new ProjectRecord(program.Id, "Configurable Ladder", "Configurable Ladder Software");
        var release = new SoftwareRelease(project.Id, "1.0", false);
        var managerName = $"ladder.manager.{tag}";
        var memberName = $"ladder.member.{tag}";
        UserAccount Account(string name) => new(name, name, $"{name}@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        var manager = Account(managerName); var member = Account(memberName);
        db.AddRange(program, project, release, manager, member,
            new ProgramMembership(manager.Id, program.Id, ProgramRole.ConfigurationManager, "test.setup", now),
            new ProgramMembership(manager.Id, program.Id, ProgramRole.Engineer, "test.setup", now),
            new ProgramMembership(member.Id, program.Id, ProgramRole.Engineer, "test.setup", now),
            new ProjectLeadershipAssignment(program.Id, ProjectLeadershipPosition.ConfigurationManager,
                manager.Id, "test.setup", now),
            LegacyDefaultProjectLadderFactory.Create(project.Id, now));
        await db.SaveChangesAsync();
        return new(project.Id, release.Id, managerName, memberName);
    }

    private static async Task SignInAsync(HttpClient client, string userName)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login",
            new { userName, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }

    [Fact]
    public async Task Authorized_edit_records_reason_history_and_rejects_stale_or_lifecycle_mutations()
    {
        var seeded = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, seeded.ManagerName);

        var read = await client.GetAsync($"/api/projects/{seeded.ProjectId}/configuration");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var readJson = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
        Assert.Equal(1, readJson.RootElement.GetProperty("version").GetInt64());
        Assert.True(readJson.RootElement.GetProperty("canManage").GetBoolean());
        Assert.Equal(new[] { "System", "HighLevel", "LowLevel", "Customer", "Interface" },
            readJson.RootElement.GetProperty("catalogue").EnumerateArray().Select(x => x.GetProperty("catalogueEntry").GetString()).ToArray());

        var edit = await client.PutAsJsonAsync($"/api/projects/{seeded.ProjectId}/configuration", new
        {
            expectedVersion = 1,
            reason = "Use a two-level draft for the pilot",
            steps = new[]
            {
                new { catalogueEntry = "System", position = 1, capabilities = 7 },
                new { catalogueEntry = "HighLevel", position = 2, capabilities = 7 },
            },
            relationships = new[] { new { parent = "System", child = "HighLevel" } },
        });
        Assert.True(edit.IsSuccessStatusCode, await edit.Content.ReadAsStringAsync());
        using var edited = JsonDocument.Parse(await edit.Content.ReadAsStringAsync());
        Assert.Equal("Draft", edited.RootElement.GetProperty("state").GetString());
        Assert.True(edited.RootElement.GetProperty("canManage").GetBoolean());
        Assert.Equal(2, edited.RootElement.GetProperty("version").GetInt64());
        Assert.Equal(1, edited.RootElement.GetProperty("history").GetArrayLength());
        Assert.Equal("Use a two-level draft for the pilot", edited.RootElement.GetProperty("history")[0].GetProperty("reason").GetString());
        var editedSnapshot = edited.RootElement.GetProperty("history")[0].GetProperty("canonicalSnapshot").GetString();
        Assert.Equal("schema[2]|steps[1:System:7:Procedure;2:HighLevel:7:Case]|edges[System>HighLevel]", editedSnapshot);
        Assert.Equal(ProjectLadderSnapshot.Hash(editedSnapshot!),
            edited.RootElement.GetProperty("history")[0].GetProperty("snapshotHash").GetString());
        Assert.Equal(2, edited.RootElement.GetProperty("history")[0].GetProperty("snapshotSchemaVersion").GetInt32());

        var stale = await client.PutAsJsonAsync($"/api/projects/{seeded.ProjectId}/configuration", new
        {
            expectedVersion = 1, reason = "stale", steps = new[] { new { catalogueEntry = "System", position = 1, capabilities = 7 } }, relationships = Array.Empty<object>(),
        });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        var lifecycle = await client.PutAsJsonAsync($"/api/projects/{seeded.ProjectId}/configuration", new
        {
            expectedVersion = 2, reason = "malicious", state = "Active", steps = new[] { new { catalogueEntry = "System", position = 1, capabilities = 7 } }, relationships = Array.Empty<object>(),
        });
        Assert.Equal(HttpStatusCode.BadRequest, lifecycle.StatusCode);
    }

    [Fact]
    public async Task Authorized_edit_can_select_interface_above_system_and_persist_its_change_control_capability()
    {
        var seeded = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, seeded.ManagerName);

        var response = await client.PutAsJsonAsync($"/api/projects/{seeded.ProjectId}/configuration", new
        {
            expectedVersion = 1,
            reason = "Configure Interface Control Documents above System",
            steps = new[]
            {
                new { catalogueEntry = "Interface", position = 1, capabilities = 1 },
                new { catalogueEntry = "System", position = 2, capabilities = 7 },
            },
            relationships = new[] { new { parent = "Interface", child = "System" } },
        });

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(["Interface", "System"], body.RootElement.GetProperty("steps").EnumerateArray()
            .Select(x => x.GetProperty("catalogueEntry").GetString()!).ToArray());
        Assert.Equal("Interface", body.RootElement.GetProperty("relationships")[0].GetProperty("parent").GetString());
        Assert.Equal("System", body.RootElement.GetProperty("relationships")[0].GetProperty("child").GetString());

        using var activation = await client.PostAsJsonAsync($"/api/projects/{seeded.ProjectId}/configuration/activate",
            new { expectedVersion = 2, reason = "Activate Interface Control Document ladder" });
        Assert.True(activation.IsSuccessStatusCode, await activation.Content.ReadAsStringAsync());

        using var workflow = await client.PutAsJsonAsync($"/api/projects/{seeded.ProjectId}/approval-configuration/Interface", new
        {
            stages = new[] { new { name = "ICD approval", requiredRole = "ConfigurationManager", kind = "Approval" } },
        });
        Assert.True(workflow.IsSuccessStatusCode, await workflow.Content.ReadAsStringAsync());
        using var applicable = await client.GetAsync($"/api/review-workflows/applicable?projectId={seeded.ProjectId}&type=Interface");
        Assert.Equal(HttpStatusCode.OK, applicable.StatusCode);
        using var applicableBody = JsonDocument.Parse(await applicable.Content.ReadAsStringAsync());
        Assert.True(applicableBody.RootElement.GetProperty("required").GetBoolean());

        using var draft = await client.PostAsJsonAsync("/api/change-request-drafts", new
        {
            projectId = seeded.ProjectId,
            targetReleaseId = seeded.ReleaseId,
            type = "Interface",
            title = "Author ICD change",
            problem = "P", analysis = "A", solution = "S",
            requirementChanges = new[]
            {
                new { level = "Interface", kind = "Introduce", statement = "The interface shall preserve its contract.",
                    rationale = "Traceable interface ownership", verificationMethod = "Not applicable" },
            },
        });
        Assert.True(draft.StatusCode == HttpStatusCode.Created, $"{draft.StatusCode}: {await draft.Content.ReadAsStringAsync()}");
        using var draftBody = JsonDocument.Parse(await draft.Content.ReadAsStringAsync());
        Assert.StartsWith("ICDCR-", draftBody.RootElement.GetProperty("displayNumber").GetString());
        Assert.StartsWith("ICDR-", draftBody.RootElement.GetProperty("requirementChanges")[0].GetProperty("displayNumber").GetString());

        using var mismatch = await client.PostAsJsonAsync("/api/change-request-drafts", new
        {
            projectId = seeded.ProjectId,
            targetReleaseId = seeded.ReleaseId,
            type = "Interface",
            title = "Reject mismatched ICD change",
            problem = "P", analysis = "A", solution = "S",
            requirementChanges = new[]
            {
                new { level = "System", kind = "Introduce", statement = "Wrong scope.", rationale = "Mismatch", verificationMethod = "Test" },
            },
        });
        Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);

        Guid interfaceRevisionId;
        Guid systemAssessmentId;
        using (var scope = _host.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var now = DateTimeOffset.UtcNow;
            var origin = new SystemChangeRequest("ICDCR-09001", 0, seeded.ProjectId, seeded.ReleaseId,
                "Approved ICD baseline origin", "P", "A", "S", seeded.ManagerName, now,
                ChangeRequestType.Interface);
            origin.AddRequirementChange(seeded.ManagerName, "ICDR-090001", 0, RequirementLevel.Interface,
                RequirementChangeKind.Introduce, "The interface baseline contract shall remain compatible.", "Baseline trace", "Not applicable", now);
            origin.SubmitForReview(seeded.ManagerName, [new ApproverSelection(seeded.ManagerName, "Configuration Manager")], now);
            origin.ApproveActiveStage(seeded.ManagerName, now);
            var baseline = new CandidateBaseline("SW-01.00", 0, seeded.ProjectId, seeded.ReleaseId, null,
                "ICD parent baseline", seeded.ManagerName, now);
            baseline.Select(origin, seeded.ManagerName, now);
            baseline.Freeze(seeded.ManagerName, now);
            baseline.MarkRequirementsMaterialized(seeded.ManagerName, new string('a', 64), 1, now);
            var artifact = new RequirementArtifact(seeded.ProjectId, "ICDR-090001", RequirementLevel.Interface, now);
            var revision = new RequirementRevision(artifact.Id, 0, "The interface baseline contract shall remain compatible.",
                "Baseline trace", "Not applicable", RequirementRevisionState.Active, origin.Id, baseline.Id, now);
            var configured = ProjectLadderConfiguration.CreateDraft(seeded.ProjectId, now);
            var interfaceStep = new ProjectLadderStep(configured.Id, seeded.ProjectId, RequirementLevel.Interface, 1,
                LegacyLadderPolicy.Instance.Definition(RequirementLevel.Interface).Capabilities, now);
            var systemStep = new ProjectLadderStep(configured.Id, seeded.ProjectId, RequirementLevel.System, 2,
                LegacyLadderPolicy.Instance.Definition(RequirementLevel.System).Capabilities, now);
            configured.Steps.Add(interfaceStep);
            configured.Steps.Add(systemStep);
            configured.AllowedUpstream.Add(new ProjectLadderAllowedUpstream(configured.Id, seeded.ProjectId,
                interfaceStep.Id, systemStep.Id, now));
            var resolvedPolicy = new ResolvedProjectLadderPolicy(ProjectLadderResolver.Resolve(configured));
            var assessment = new DownstreamChangeAssessment(seeded.ProjectId, seeded.ReleaseId, origin.Id,
                origin.DisplayNumber, RequirementLevel.System, now, resolvedPolicy);
            assessment.Assign(seeded.ManagerName, seeded.ManagerName, now);
            assessment.RecordChangeRequired(seeded.ManagerName, now);
            db.AddRange(origin, baseline, artifact, revision);
            db.BaselineRequirements.Add(new BaselineRequirementSelection(baseline.Id, artifact.Id, revision.Id));
            db.Add(assessment);
            await db.SaveChangesAsync();
            interfaceRevisionId = revision.Id;
            systemAssessmentId = assessment.Id;
        }

        using var upstreamPicker = await client.GetAsync(
            $"/api/authoring/upstream-requirements?projectId={seeded.ProjectId}&releaseId={seeded.ReleaseId}&childLevel=System");
        Assert.Equal(HttpStatusCode.OK, upstreamPicker.StatusCode);
        using var upstreamBody = JsonDocument.Parse(await upstreamPicker.Content.ReadAsStringAsync());
        var upstream = Assert.Single(upstreamBody.RootElement.EnumerateArray());
        Assert.Equal(interfaceRevisionId, upstream.GetProperty("revisionId").GetGuid());
        Assert.Equal("Interface", upstream.GetProperty("level").GetString());

        using var systemDraft = await client.PostAsJsonAsync("/api/change-request-drafts", new
        {
            projectId = seeded.ProjectId,
            targetReleaseId = seeded.ReleaseId,
            type = "System",
            title = "System allocation to ICD",
            problem = "P", analysis = "A", solution = "S",
            requirementChanges = new[]
            {
                new { level = "System", kind = "Introduce", statement = "The system shall honor the interface contract.",
                    rationale = "Configured ICD allocation", verificationMethod = "Test",
                    upstreamRevisionIds = new[] { interfaceRevisionId } },
            },
        });
        Assert.True(systemDraft.StatusCode == HttpStatusCode.Created,
            $"{systemDraft.StatusCode}: {await systemDraft.Content.ReadAsStringAsync()}");

        using var assessmentLink = await client.PostAsJsonAsync($"/api/downstream-assessments/{systemAssessmentId}/change-requests",
            new { changeRequestId = JsonDocument.Parse(await systemDraft.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid() });
        Assert.Equal(HttpStatusCode.OK, assessmentLink.StatusCode);

        using var search = await client.GetAsync($"/api/search?projectId={seeded.ProjectId}&releaseId={seeded.ReleaseId}&query=Author%20ICD%20change");
        Assert.Equal(HttpStatusCode.OK, search.StatusCode);
        using var searchBody = JsonDocument.Parse(await search.Content.ReadAsStringAsync());
        var searchHit = Assert.Single(searchBody.RootElement.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("id").GetGuid() == draftBody.RootElement.GetProperty("id").GetGuid());
        Assert.Equal("Interface", searchHit.GetProperty("level").GetString());
    }

    [Fact]
    public async Task Non_default_activation_succeeds_through_the_sole_gate_and_records_manifest_and_history()
    {
        var seeded = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, seeded.ManagerName);

        var edit = await client.PutAsJsonAsync($"/api/projects/{seeded.ProjectId}/configuration", new
        {
            expectedVersion = 1, reason = "Prepare activation review",
            steps = new[] { new { catalogueEntry = "System", position = 1, capabilities = 7 }, new { catalogueEntry = "HighLevel", position = 2, capabilities = 7 } },
            relationships = new[] { new { parent = "System", child = "HighLevel" } },
        });
        Assert.True(edit.IsSuccessStatusCode, await edit.Content.ReadAsStringAsync());

        using var invalidActivation = await client.PostAsJsonAsync($"/api/projects/{seeded.ProjectId}/configuration/activate",
            new { expectedVersion = 2, reason = "" });
        Assert.Equal(HttpStatusCode.BadRequest, invalidActivation.StatusCode);
        using (var failedScope = _host.Factory.Services.CreateScope())
        {
            var failedDb = failedScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var failedConfiguration = await failedDb.ProjectLadderConfigurations.AsNoTracking()
                .SingleAsync(x => x.ProjectId == seeded.ProjectId);
            Assert.Equal(ProjectLadderConfigurationState.Draft, failedConfiguration.State);
            Assert.Null(failedConfiguration.ActivationManifestHash);
            Assert.Single(await failedDb.ProjectLadderConfigurationHistories
                .Where(x => x.ConfigurationId == failedConfiguration.Id).ToListAsync());
        }

        var activation = await client.PostAsJsonAsync($"/api/projects/{seeded.ProjectId}/configuration/activate",
            new { expectedVersion = 2, reason = "Attempt activation" });
        Assert.Equal(HttpStatusCode.OK, activation.StatusCode);
        using var activationJson = JsonDocument.Parse(await activation.Content.ReadAsStringAsync());
        var activationBody = activationJson.RootElement;
        Assert.Equal("Active", activationBody.GetProperty("state").GetString());
        Assert.Equal(3, activationBody.GetProperty("version").GetInt64());
        Assert.Equal(2, activationBody.GetProperty("effectiveSteps").GetArrayLength());
        var readiness = activationBody.GetProperty("readiness");
        Assert.True(readiness.GetProperty("isReady").GetBoolean());
        Assert.Equal(LadderConsumerManifestCatalog.RequiredConsumerIds.Count, readiness.GetProperty("consumers").GetArrayLength());
        var manifestVersion = readiness.GetProperty("version").GetString();
        var manifestHash = readiness.GetProperty("hash").GetString();
        Assert.Equal(LadderConsumerManifestCatalog.VersionV2, manifestVersion);
        Assert.False(string.IsNullOrWhiteSpace(manifestVersion));
        Assert.Matches("^[0-9a-f]{64}$", manifestHash ?? "");
        Assert.Equal(manifestVersion, activationBody.GetProperty("activationManifestVersion").GetString());
        Assert.Equal(manifestHash, activationBody.GetProperty("activationManifestHash").GetString());
        Assert.Equal(2, activationBody.GetProperty("history").GetArrayLength());
        Assert.Contains("Activated ladder: Attempt activation", activationBody.GetProperty("history")[0].GetProperty("reason").GetString());

        using var scope = _host.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var configuration = await db.ProjectLadderConfigurations.SingleAsync(x => x.ProjectId == seeded.ProjectId);
        Assert.Equal(ProjectLadderConfigurationState.Active, configuration.State);
        Assert.Equal(manifestVersion, configuration.ActivationManifestVersion);
        Assert.Equal(manifestHash, configuration.ActivationManifestHash);
        var history = await db.ProjectLadderConfigurationHistories.AsNoTracking()
            .Where(x => x.ConfigurationId == configuration.Id).OrderByDescending(x => x.Revision).ToListAsync();
        Assert.Equal(2, history.Count);
        Assert.Contains("Activated ladder: Attempt activation", history[0].Reason);
        Assert.Equal(2, history[0].SnapshotSchemaVersion);

        using var staleActivation = await client.PostAsJsonAsync($"/api/projects/{seeded.ProjectId}/configuration/activate",
            new { expectedVersion = 2, reason = "Stale activation must not mutate the active row" });
        Assert.Equal(HttpStatusCode.Conflict, staleActivation.StatusCode);
        var unchanged = await db.ProjectLadderConfigurations.AsNoTracking()
            .SingleAsync(x => x.ProjectId == seeded.ProjectId);
        Assert.Equal(ProjectLadderConfigurationState.Active, unchanged.State);
        Assert.Equal(3, unchanged.Version);
        Assert.Equal(2, await db.ProjectLadderConfigurationHistories.CountAsync(x => x.ConfigurationId == unchanged.Id));
    }

    [Fact]
    public async Task Authorized_edit_accepts_a_non_adjacent_forward_relationship_from_selected_catalogue_steps()
    {
        var seeded = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, seeded.ManagerName);

        var edit = await client.PutAsJsonAsync($"/api/projects/{seeded.ProjectId}/configuration", new
        {
            expectedVersion = 1,
            reason = "Pilot a direct System to Low-Level relationship",
            steps = new[]
            {
                new { catalogueEntry = "System", position = 1, capabilities = 7 },
                new { catalogueEntry = "LowLevel", position = 2, capabilities = 15 },
            },
            relationships = new[] { new { parent = "System", child = "LowLevel" } },
        });
        Assert.True(edit.IsSuccessStatusCode, await edit.Content.ReadAsStringAsync());
        using var body = JsonDocument.Parse(await edit.Content.ReadAsStringAsync());
        Assert.Equal("System", body.RootElement.GetProperty("relationships")[0].GetProperty("parent").GetString());
        Assert.Equal("LowLevel", body.RootElement.GetProperty("relationships")[0].GetProperty("child").GetString());
    }

    [Fact]
    public async Task An_engineer_can_read_but_cannot_edit_project_configuration()
    {
        var seeded = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, seeded.MemberName);
        var read = await client.GetAsync($"/api/projects/{seeded.ProjectId}/configuration");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var readJson = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
        Assert.False(readJson.RootElement.GetProperty("canManage").GetBoolean());
        var response = await client.PutAsJsonAsync($"/api/projects/{seeded.ProjectId}/configuration", new { expectedVersion = 1, reason = "No", steps = Array.Empty<object>(), relationships = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Concurrent_same_version_edits_have_one_winner_and_preserve_the_winner_graph_and_history()
    {
        static object Edit(string reason, object[] steps, object[] relationships) => new
        {
            expectedVersion = 1,
            reason,
            steps,
            relationships,
        };

        var firstPayload = Edit("Concurrent HLR winner candidate",
            [new { catalogueEntry = "System", position = 1, capabilities = 7 }, new { catalogueEntry = "HighLevel", position = 2, capabilities = 7 }],
            [new { parent = "System", child = "HighLevel" }]);
        var secondPayload = Edit("Concurrent LLR winner candidate",
            [new { catalogueEntry = "System", position = 1, capabilities = 7 }, new { catalogueEntry = "LowLevel", position = 2, capabilities = 15 }],
            [new { parent = "System", child = "LowLevel" }]);

        var concurrentSeed = await SeedAsync(_host.Factory);
        using var concurrentFirst = _host.CreateClient();
        using var concurrentSecond = _host.CreateClient();
        await SignInAsync(concurrentFirst, concurrentSeed.ManagerName);
        await SignInAsync(concurrentSecond, concurrentSeed.ManagerName);
        var firstTask = concurrentFirst.PutAsJsonAsync($"/api/projects/{concurrentSeed.ProjectId}/configuration", firstPayload);
        var secondTask = concurrentSecond.PutAsJsonAsync($"/api/projects/{concurrentSeed.ProjectId}/configuration", secondPayload);
        // The request tasks are created before either is awaited, so the two independent writers race at the
        // service's version-claim transaction rather than merely exercising sequential stale-version handling.
        var responses = await Task.WhenAll(firstTask, secondTask);
        using var firstConcurrentResponse = responses[0];
        using var secondConcurrentResponse = responses[1];
        var statuses = new[] { firstConcurrentResponse.StatusCode, secondConcurrentResponse.StatusCode };
        Assert.Contains(HttpStatusCode.OK, statuses);
        Assert.Contains(HttpStatusCode.Conflict, statuses);

        var final = await concurrentFirst.GetAsync($"/api/projects/{concurrentSeed.ProjectId}/configuration");
        Assert.Equal(HttpStatusCode.OK, final.StatusCode);
        using var finalJson = JsonDocument.Parse(await final.Content.ReadAsStringAsync());
        Assert.Equal(2, finalJson.RootElement.GetProperty("version").GetInt64());
        Assert.Equal(1, finalJson.RootElement.GetProperty("history").GetArrayLength());
        var winnerReason = finalJson.RootElement.GetProperty("history")[0].GetProperty("reason").GetString();
        Assert.NotNull(winnerReason);
        var winnerEntry = finalJson.RootElement.GetProperty("steps").EnumerateArray()
            .Single(x => x.GetProperty("position").GetInt32() == 2).GetProperty("catalogueEntry").GetString();
        var winnerChild = finalJson.RootElement.GetProperty("relationships")[0].GetProperty("child").GetString();
        var winnerSnapshot = finalJson.RootElement.GetProperty("history")[0].GetProperty("canonicalSnapshot").GetString();
        if (winnerReason == "Concurrent HLR winner candidate")
        {
            Assert.Equal("HighLevel", winnerEntry);
            Assert.Equal("HighLevel", winnerChild);
            Assert.Contains("HighLevel", winnerSnapshot);
        }
        else
        {
            Assert.Equal("Concurrent LLR winner candidate", winnerReason);
            Assert.Equal("LowLevel", winnerEntry);
            Assert.Equal("LowLevel", winnerChild);
            Assert.Contains("LowLevel", winnerSnapshot);
        }
    }

    [Fact]
    public async Task Concurrent_same_version_activation_has_one_success_one_conflict_and_one_atomic_active_history()
    {
        var seeded = await SeedAsync(_host.Factory);
        using var editor = _host.CreateClient();
        await SignInAsync(editor, seeded.ManagerName);

        var edit = await editor.PutAsJsonAsync($"/api/projects/{seeded.ProjectId}/configuration", new
        {
            expectedVersion = 1,
            reason = "Prepare the concurrent activation race",
            steps = new[]
            {
                new { catalogueEntry = "System", position = 1, capabilities = 7 },
                new { catalogueEntry = "LowLevel", position = 2, capabilities = 7 },
            },
            relationships = new[] { new { parent = "System", child = "LowLevel" } },
        });
        Assert.True(edit.IsSuccessStatusCode, await edit.Content.ReadAsStringAsync());

        using var first = _host.CreateClient();
        using var second = _host.CreateClient();
        await SignInAsync(first, seeded.ManagerName);
        await SignInAsync(second, seeded.ManagerName);
        using var gate = new SaveRaceGate(_host.Factory.ConnectionString);
        try
        {
            // The interceptor holds both requests after they have loaded Version 2 and reached SaveChanges.
            // Releasing the first proves the second loses on the EF concurrency token rather than merely
            // observing a completed request during the service's optimistic pre-check.
            var firstTask = first.PostAsJsonAsync($"/api/projects/{seeded.ProjectId}/configuration/activate",
                new { expectedVersion = 2, reason = "Concurrent activation candidate one" });
            Assert.True(await gate.FirstEnteredAsync(TimeSpan.FromSeconds(30)),
                "The first activation request never reached SaveChanges.");
            var secondTask = second.PostAsJsonAsync($"/api/projects/{seeded.ProjectId}/configuration/activate",
                new { expectedVersion = 2, reason = "Concurrent activation candidate two" });
            Assert.True(await gate.SecondEnteredAsync(TimeSpan.FromSeconds(30)),
                "The second activation request never reached SaveChanges.");

            gate.ReleaseFirst();
            using var firstResponse = await firstTask;
            gate.ReleaseSecond();
            using var secondResponse = await secondTask;
            var statuses = new[] { firstResponse.StatusCode, secondResponse.StatusCode };
            Assert.Contains(HttpStatusCode.OK, statuses);
            Assert.Contains(HttpStatusCode.Conflict, statuses);
            var successfulResponse = firstResponse.IsSuccessStatusCode ? firstResponse : secondResponse;
            using var successfulJson = JsonDocument.Parse(await successfulResponse.Content.ReadAsStringAsync());
            Assert.Equal("Active", successfulJson.RootElement.GetProperty("state").GetString());
            Assert.False(string.IsNullOrWhiteSpace(successfulJson.RootElement.GetProperty("activationManifestVersion").GetString()));
            Assert.Matches("^[0-9a-f]{64}$", successfulJson.RootElement.GetProperty("activationManifestHash").GetString() ?? "");
        }
        finally
        {
            gate.Dispose();
        }

        using var scope = _host.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var configuration = await db.ProjectLadderConfigurations.AsNoTracking()
            .SingleAsync(x => x.ProjectId == seeded.ProjectId);
        Assert.Equal(ProjectLadderConfigurationState.Active, configuration.State);
        Assert.Equal(3, configuration.Version);
        Assert.False(string.IsNullOrWhiteSpace(configuration.ActivationManifestVersion));
        Assert.Matches("^[0-9a-f]{64}$", configuration.ActivationManifestHash ?? "");
        var history = await db.ProjectLadderConfigurationHistories.AsNoTracking()
            .Where(x => x.ConfigurationId == configuration.Id)
            .OrderBy(x => x.Revision)
            .ToListAsync();
        Assert.Equal(2, history.Count);
        Assert.Single(history, x => x.Reason.StartsWith("Activated ladder:", StringComparison.Ordinal));
    }
}
