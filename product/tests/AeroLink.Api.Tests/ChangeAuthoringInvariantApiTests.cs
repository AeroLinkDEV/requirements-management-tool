using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

public sealed class ChangeAuthoringInvariantApiTests
{
    private sealed record Scenario(Guid ProjectId, Guid ReleaseId, Guid SystemSectionId, Guid HlrSectionId);

    [Fact]
    public async Task Authored_attributes_and_sections_survive_creation_and_server_owned_derived_cannot_be_spoofed()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var scenario = await SeedAsync(factory);
        await SignInAsync(client);
        var complete = RequirementAuthoringJson.CompleteImpactDispositions;

        using var systemResponse = await client.PostAsJsonAsync("/api/scr-drafts", new
        {
            projectId = scenario.ProjectId, targetReleaseId = scenario.ReleaseId, type = "System",
            title = "Persist system metadata", problem = "P", analysis = "A", solution = "S",
            requirementChanges = new[]
            {
                new { level = "System", kind = "Introduce", statement = "The FMS shall retain metadata.",
                    rationale = "Controlled ownership", verificationMethod = "Test",
                    attributesJson = """{"owner":"systems.author","criticality":"Mission Critical","derived":true}""",
                    impactDispositionJson = complete, isDerived = true, targetSectionId = scenario.SystemSectionId }
            }
        });
        var systemBody = await systemResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Created, systemResponse.StatusCode);
        var system = JsonSerializer.Deserialize<JsonElement>(systemBody);
        var systemChange = system.GetProperty("requirementChanges")[0];
        Assert.Equal(scenario.SystemSectionId, systemChange.GetProperty("targetSectionId").GetGuid());
        using var systemAttributes = JsonDocument.Parse(systemChange.GetProperty("attributesJson").GetString()!);
        Assert.Equal("systems.author", systemAttributes.RootElement.GetProperty("owner").GetString());
        Assert.Equal("Mission Critical", systemAttributes.RootElement.GetProperty("criticality").GetString());
        Assert.False(systemAttributes.RootElement.TryGetProperty("derived", out _));
        using var checkout = await client.PostAsJsonAsync("/api/controlled-editing/checkout",
            new { artifactType = "SCR", artifactId = system.GetProperty("id").GetGuid(), leaseMinutes = 15 });
        Assert.Equal(HttpStatusCode.Created, checkout.StatusCode);
        var checkedOut = JsonSerializer.Deserialize<JsonElement>(await checkout.Content.ReadAsStringAsync());
        using var recovery = JsonDocument.Parse(checkedOut.GetProperty("draftJson").GetString()!);
        Assert.Equal(scenario.SystemSectionId, recovery.RootElement.GetProperty("requirementChanges")[0]
            .GetProperty("targetSectionId").GetGuid());

        using var softwareResponse = await client.PostAsJsonAsync("/api/scr-drafts", new
        {
            projectId = scenario.ProjectId, targetReleaseId = scenario.ReleaseId, type = "Software",
            title = "Persist software metadata", problem = "P", analysis = "A", solution = "S",
            requirementChanges = new[]
            {
                new { level = "HighLevel", kind = "Introduce", statement = "The software shall retain metadata.",
                    rationale = "Controlled ownership", verificationMethod = "Test",
                    attributesJson = """{"owner":"software.author","criticality":"Safety Significant","derived":false}""",
                    impactDispositionJson = complete, isDerived = true, targetSectionId = scenario.HlrSectionId }
            }
        });
        var softwareBody = await softwareResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Created, softwareResponse.StatusCode);
        var software = JsonSerializer.Deserialize<JsonElement>(softwareBody);
        var softwareChange = software.GetProperty("requirementChanges")[0];
        using var softwareAttributes = JsonDocument.Parse(softwareChange.GetProperty("attributesJson").GetString()!);
        Assert.Equal("software.author", softwareAttributes.RootElement.GetProperty("owner").GetString());
        Assert.True(softwareAttributes.RootElement.GetProperty("derived").GetBoolean());

        using var invalid = await client.PostAsJsonAsync("/api/scr-drafts", new
        {
            projectId = scenario.ProjectId, targetReleaseId = scenario.ReleaseId, type = "System",
            title = "Reject unknown metadata", problem = "P", analysis = "A", solution = "S",
            requirementChanges = new[]
            {
                new { level = "System", kind = "Introduce", statement = "The FMS shall reject unknown metadata.",
                    rationale = "Schema authority", verificationMethod = "Test",
                    attributesJson = """{"owner":"systems.author","invented":"not allowed"}""" }
            }
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Contains("not allowed by the System Requirement schema", await invalid.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Missing_authored_attributes_are_reported_without_rewriting_history()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var scenario = await SeedAsync(factory);
        Guid scrId;
        using (var seedScope = factory.Services.CreateScope())
        {
            var seedDb = seedScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var scr = new SystemChangeRequest("SCR-00000999", 0, scenario.ProjectId, scenario.ReleaseId,
                "Legacy gap", "P", "A", "S", "invariant.author", DateTimeOffset.UtcNow);
            scr.AddRequirementChange("invariant.author", "SYSR-00000999", 0, RequirementLevel.System,
                RequirementChangeKind.Introduce, "The FMS shall expose a legacy gap.", "R", "Test",
                DateTimeOffset.UtcNow, attributesJson: "{}", impactDispositionJson: "{}");
            seedDb.Add(scr);
            await seedDb.SaveChangesAsync();
            scrId = scr.Id;
        }
        await SignInAsync(client);

        using var response = await client.GetAsync($"/api/authoring/attribute-gaps?projectId={scenario.ProjectId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rows = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        var row = Assert.Single(rows.EnumerateArray(), x => x.GetProperty("id").GetGuid() == scrId);
        Assert.Equal(new[] { "criticality", "owner" },
            row.GetProperty("missing").EnumerateArray().Select(x => x.GetString()).ToArray());
        Assert.Equal($"scr:{scrId}", row.GetProperty("reconciliation").GetString());

        using var checkpointResponse = await client.PostAsJsonAsync(
            "/api/enterprise-hardening/integrity-checkpoints", new { projectId = scenario.ProjectId });
        Assert.Equal(HttpStatusCode.Created, checkpointResponse.StatusCode);
        var checkpoint = JsonSerializer.Deserialize<JsonElement>(await checkpointResponse.Content.ReadAsStringAsync());
        Assert.Equal("Attention", checkpoint.GetProperty("state").GetString());
        Assert.Contains("1 invalid impact-disposition proposal(s)", checkpoint.GetProperty("detail").GetString());

        using var verificationScope = factory.Services.CreateScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        Assert.Equal("{}", (await verificationDb.RequirementChanges.SingleAsync(x => x.ScrId == scrId)).AttributesJson);
    }

    [Fact]
    public async Task Direct_api_submission_cannot_bypass_incomplete_impact_dispositions()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var scenario = await SeedAsync(factory);
        await SignInAsync(client);
        using var created = await client.PostAsJsonAsync("/api/scr-drafts", new
        {
            projectId = scenario.ProjectId, targetReleaseId = scenario.ReleaseId, type = "System",
            title = "Incomplete impacts", problem = "P", analysis = "A", solution = "S",
            requirementChanges = new[]
            {
                new { level = "System", kind = "Introduce", statement = "The FMS shall require dispositions.",
                    rationale = "Lifecycle integrity", verificationMethod = "Test",
                    impactDispositionJson = "{}" }
            }
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var draft = JsonSerializer.Deserialize<JsonElement>(await created.Content.ReadAsStringAsync());

        using var submitted = await client.PostAsJsonAsync($"/api/scrs/{draft.GetProperty("id").GetGuid()}/submit",
            new { actorId = "invariant.author", expectedVersion = draft.GetProperty("version").GetInt64(), mode = "Sequential",
                approvers = new[] { new { userId = "invariant.reviewer", name = "Caller supplied name" } } });
        Assert.Equal(HttpStatusCode.BadRequest, submitted.StatusCode);
        Assert.Contains("Complete every impact disposition", await submitted.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Legacy_invalid_dispositions_cannot_be_selected_or_frozen()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var scenario = await SeedAsync(factory);
        Guid selectedBaselineId;
        Guid emptyBaselineId;
        Guid scrId;
        using (var seedScope = factory.Services.CreateScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var now = DateTimeOffset.UtcNow;
            var scr = new SystemChangeRequest("SCR-00000888", 0, scenario.ProjectId, scenario.ReleaseId,
                "Legacy invalid impacts", "P", "A", "S", "invariant.author", now);
            scr.AddRequirementChange("invariant.author", "SYSR-00000888", 0, RequirementLevel.System,
                RequirementChangeKind.Introduce, "The FMS shall reject legacy invalid impacts.", "R", "Test", now);
            scr.SubmitForReview("invariant.author", [new("invariant.reviewer", "Invariant Reviewer")], now);
            scr.ApproveActiveStage("invariant.reviewer", now);
            var selected = new CandidateBaseline("SWBL-00000888", 0, scenario.ProjectId, scenario.ReleaseId,
                null, "Selected legacy record", "invariant.author", now);
            selected.Select(scr, "invariant.author", now);
            var empty = new CandidateBaseline("SWBL-00000889", 0, scenario.ProjectId, scenario.ReleaseId,
                null, "Selection guard", "invariant.author", now);
            db.AddRange(scr, selected, empty);
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE requirement_changes SET ImpactDispositionJson = '{{}}' WHERE ScrId = {scr.Id}");
            selectedBaselineId = selected.Id;
            emptyBaselineId = empty.Id;
            scrId = scr.Id;
        }
        await SignInAsync(client);

        using var selection = await client.PostAsJsonAsync($"/api/baselines/{emptyBaselineId}/selections",
            new { scrId, actorId = "invariant.author" });
        Assert.Equal(HttpStatusCode.BadRequest, selection.StatusCode);
        Assert.Contains("Complete every impact disposition", await selection.Content.ReadAsStringAsync());

        using var freeze = await client.PostAsJsonAsync($"/api/baselines/{selectedBaselineId}/freeze",
            new { actorId = "invariant.author" });
        Assert.Equal(HttpStatusCode.BadRequest, freeze.StatusCode);
        Assert.Contains("Complete every impact disposition", await freeze.Content.ReadAsStringAsync());
    }

    private static async Task<Scenario> SeedAsync(AeroLinkApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;
        var program = new ProgramRecord("Invariant Program", "IVP");
        var project = new ProjectRecord(program.Id, "Invariant Project", "Invariant Software");
        var release = new SoftwareRelease(project.Id, "1.0", false);
        var account = new UserAccount("invariant.author", "Invariant Author", "invariant@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        var reviewer = new UserAccount("invariant.reviewer", "Invariant Reviewer", "reviewer@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        db.AddRange(program, project, release, account, reviewer,
            new ProgramMembership(account.Id, program.Id, ProgramRole.Engineer, "test.setup", now),
            new ProgramMembership(account.Id, program.Id, ProgramRole.ConfigurationManager, "test.setup", now),
            new ProgramMembership(reviewer.Id, program.Id, ProgramRole.Reviewer, "test.setup", now));
        await db.SaveChangesAsync();
        await new EnterpriseRequirementsService(db).SynchronizeProjectAsync(project.Id, account.UserName);
        var sections = await (from node in db.SpecificationNodes
                              join specification in db.RequirementSpecifications on node.SpecificationId equals specification.Id
                              where specification.ProjectId == project.Id && node.Type == SpecificationNodeType.Section
                              select new { node.Id, specification.Level }).ToListAsync();
        return new(project.Id, release.Id,
            sections.First(x => x.Level == RequirementLevel.System.ToString()).Id,
            sections.First(x => x.Level == RequirementLevel.HighLevel.ToString()).Id);
    }

    private static async Task SignInAsync(HttpClient client)
    {
        var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName = "invariant.author", password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }
}
