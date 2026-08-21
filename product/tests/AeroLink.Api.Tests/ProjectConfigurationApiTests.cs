using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Hierarchy;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

public sealed class ProjectConfigurationApiTests : IClassFixture<SharedApiHost>
{
    private readonly SharedApiHost _host;

    public ProjectConfigurationApiTests(SharedApiHost host) => _host = host;

    private sealed record Seeded(Guid ProjectId, string ManagerName, string MemberName);

    private static async Task<Seeded> SeedAsync(AeroLinkApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var tag = Guid.NewGuid().ToString("N")[..8];
        var now = DateTimeOffset.UtcNow;
        var program = new ProgramRecord($"Ladder API {tag}", $"LAD{tag}");
        var project = new ProjectRecord(program.Id, "Configurable Ladder", "Configurable Ladder Software");
        var managerName = $"ladder.manager.{tag}";
        var memberName = $"ladder.member.{tag}";
        UserAccount Account(string name) => new(name, name, $"{name}@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        var manager = Account(managerName); var member = Account(memberName);
        db.AddRange(program, project, manager, member,
            new ProgramMembership(manager.Id, program.Id, ProgramRole.ConfigurationManager, "test.setup", now),
            new ProgramMembership(member.Id, program.Id, ProgramRole.Engineer, "test.setup", now),
            LegacyDefaultProjectLadderFactory.Create(project.Id, now));
        await db.SaveChangesAsync();
        return new(project.Id, managerName, memberName);
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
        Assert.Equal(new[] { "System", "HighLevel", "LowLevel" },
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
        Assert.Equal("steps[1:System:7;2:HighLevel:7]|edges[System>HighLevel]",
            edited.RootElement.GetProperty("history")[0].GetProperty("canonicalSnapshot").GetString());
        Assert.Equal(ProjectLadderSnapshot.Hash("steps[1:System:7;2:HighLevel:7]|edges[System>HighLevel]"),
            edited.RootElement.GetProperty("history")[0].GetProperty("snapshotHash").GetString());

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
        Assert.Equal(18, readiness.GetProperty("consumers").GetArrayLength());
        var manifestVersion = readiness.GetProperty("version").GetString();
        var manifestHash = readiness.GetProperty("hash").GetString();
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
