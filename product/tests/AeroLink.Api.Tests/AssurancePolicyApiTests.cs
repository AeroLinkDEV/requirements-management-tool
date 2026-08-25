using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Assurance;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>
/// The assurance-policy surface: what a reader is told, what a project may record, and who the shared
/// authority resolver will let approve a relaxation.
/// </summary>
public sealed class AssurancePolicyApiTests : IClassFixture<SharedApiHost>
{
    private readonly SharedApiHost _host;

    public AssurancePolicyApiTests(SharedApiHost host) => _host = host;

    private sealed record Seeded(Guid ProjectId, Guid ProgramId, Guid ReleaseId, Guid BaselineId,
        string Manager, string Sqa, string Airworthiness, string ConfigurationManager, string Administrator,
        string Engineer, Guid EngineerAccountId);

    private static async Task<Seeded> SeedAsync(AeroLinkApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var tag = Guid.NewGuid().ToString("N")[..8];
        var now = DateTimeOffset.UtcNow;
        var program = new ProgramRecord($"Assurance API {tag}", $"ASA{tag}");
        var project = new ProjectRecord(program.Id, "Assurance posture", "Assurance posture software");
        var release = new SoftwareRelease(project.Id, "1.0", false);
        var baseline = new CandidateBaseline("BL-00000001", 0, project.Id, release.Id, null, "Assurance", "cm", now);

        UserAccount Account(string name) => new(name, name, $"{name}@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        var manager = Account($"assurance.pm.{tag}");
        var sqa = Account($"assurance.sqa.{tag}");
        var airworthiness = Account($"assurance.air.{tag}");
        var configurationManager = Account($"assurance.cm.{tag}");
        var administrator = Account($"assurance.admin.{tag}");
        var engineer = Account($"assurance.eng.{tag}");

        db.AddRange(program, project, release, baseline, manager, sqa, airworthiness, configurationManager,
            administrator, engineer,
            LegacyDefaultProjectLadderFactory.Create(project.Id, now),
            new ProgramMembership(manager.Id, program.Id, ProgramRole.ProgramManager, "test.setup", now),
            new ProgramMembership(sqa.Id, program.Id, ProgramRole.SoftwareQualityAnalyst, "test.setup", now),
            new ProgramMembership(airworthiness.Id, program.Id, ProgramRole.Airworthiness, "test.setup", now),
            new ProgramMembership(configurationManager.Id, program.Id, ProgramRole.ConfigurationManager, "test.setup", now),
            new ProgramMembership(administrator.Id, program.Id, ProgramRole.Administrator, "test.setup", now),
            new ProgramMembership(engineer.Id, program.Id, ProgramRole.Engineer, "test.setup", now));
        await db.SaveChangesAsync();
        return new(project.Id, program.Id, release.Id, baseline.Id, manager.UserName, sqa.UserName,
            airworthiness.UserName, configurationManager.UserName, administrator.UserName, engineer.UserName,
            engineer.Id);
    }

    private static async Task SignInAsync(HttpClient client, string userName)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login",
            new { userName, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }

    private static object RelaxCoverage(int expectedVersion, string approver, string rationale = "The customer runs this campaign.",
        bool airworthinessDesignated = false, string declaredLevel = "LevelB") => new
        {
            expectedVersion,
            declaredLevel,
            reason = "Record the project's declared posture for the pilot build.",
            selections = new[]
            {
                new { lever = nameof(AssurancePolicyLever.RequirementCoverageBeforeRelease), value = nameof(AssuranceLeverValue.NotRequired) },
            },
            deviations = new[]
            {
                new { lever = nameof(AssurancePolicyLever.RequirementCoverageBeforeRelease), scope = "Project", rationale, airworthinessDesignated, approverUserName = approver },
            },
        };

    [Fact]
    public async Task The_default_view_states_the_recommendation_its_basis_and_that_no_certification_mapping_is_approved()
    {
        var seeded = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, seeded.Engineer);

        var response = await client.GetAsync($"/api/projects/{seeded.ProjectId}/assurance-policy");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;

        Assert.Equal("NotDeclared", root.GetProperty("declaredLevel").GetString());
        Assert.Equal(0, root.GetProperty("version").GetInt32());
        Assert.False(root.GetProperty("canManage").GetBoolean());
        Assert.Contains("No certification-derived recommendation mapping has been approved",
            root.GetProperty("mappingNotice").GetString());
        Assert.Contains("AeroLink has not assessed conformity", root.GetProperty("claimBoundary").GetString());

        var levers = root.GetProperty("levers").EnumerateArray().ToList();
        Assert.NotEmpty(levers);
        foreach (var lever in levers)
        {
            Assert.Equal("AeroLinkRule", lever.GetProperty("basisKind").GetString());
            Assert.False(string.IsNullOrWhiteSpace(lever.GetProperty("recommendationBasis").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(lever.GetProperty("enforcementPoint").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(lever.GetProperty("releaseEffect").GetString()));
            // A project that has recorded nothing is running on the recommendation for every lever.
            Assert.Equal(lever.GetProperty("recommended").GetString(), lever.GetProperty("selected").GetString());
            Assert.False(lever.GetProperty("isRelaxation").GetBoolean());
        }
        Assert.Empty(root.GetProperty("deviations").EnumerateArray());
        Assert.Empty(root.GetProperty("history").EnumerateArray());
        Assert.NotEmpty(root.GetProperty("authorityRules").EnumerateArray());
    }

    [Fact]
    public async Task The_declared_level_is_metadata_and_does_not_change_a_single_recommendation()
    {
        var seeded = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, seeded.ConfigurationManager);

        var before = await client.GetFromJsonAsync<JsonElement>($"/api/projects/{seeded.ProjectId}/assurance-policy");
        var baseline = before.GetProperty("levers").EnumerateArray()
            .Select(x => (x.GetProperty("lever").GetString(), x.GetProperty("recommended").GetString(),
                x.GetProperty("recommendationBasis").GetString(), x.GetProperty("basisKind").GetString())).ToList();

        foreach (var level in new[] { "LevelA", "LevelD" })
        {
            var version = (await client.GetFromJsonAsync<JsonElement>($"/api/projects/{seeded.ProjectId}/assurance-policy"))
                .GetProperty("version").GetInt32();
            var declare = await client.PutAsJsonAsync($"/api/projects/{seeded.ProjectId}/assurance-policy", new
            {
                expectedVersion = version,
                declaredLevel = level,
                reason = $"Declare {level} for this project.",
                selections = Array.Empty<object>(),
                deviations = Array.Empty<object>(),
            });
            Assert.Equal(HttpStatusCode.OK, declare.StatusCode);
            using var json = JsonDocument.Parse(await declare.Content.ReadAsStringAsync());
            Assert.Equal(level, json.RootElement.GetProperty("declaredLevel").GetString());
            Assert.Equal(baseline, json.RootElement.GetProperty("levers").EnumerateArray()
                .Select(x => (x.GetProperty("lever").GetString(), x.GetProperty("recommended").GetString(),
                    x.GetProperty("recommendationBasis").GetString(), x.GetProperty("basisKind").GetString())).ToList());
        }
    }

    [Fact]
    public async Task A_relaxation_is_refused_without_a_deviation_and_without_a_rationale()
    {
        var seeded = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, seeded.ConfigurationManager);

        var noDeviation = await client.PutAsJsonAsync($"/api/projects/{seeded.ProjectId}/assurance-policy", new
        {
            expectedVersion = 0,
            declaredLevel = "LevelB",
            reason = "Relax coverage.",
            selections = new[]
            {
                new { lever = nameof(AssurancePolicyLever.RequirementCoverageBeforeRelease), value = nameof(AssuranceLeverValue.NotRequired) },
            },
            deviations = Array.Empty<object>(),
        });
        Assert.Equal(HttpStatusCode.BadRequest, noDeviation.StatusCode);
        Assert.Contains("requires a recorded deviation", await noDeviation.Content.ReadAsStringAsync());

        var noRationale = await client.PutAsJsonAsync($"/api/projects/{seeded.ProjectId}/assurance-policy",
            RelaxCoverage(0, seeded.Sqa, rationale: "   "));
        Assert.Equal(HttpStatusCode.BadRequest, noRationale.StatusCode);
        Assert.Contains("rationale is required", await noRationale.Content.ReadAsStringAsync());

        // Nothing was written by either refusal.
        var view = await client.GetFromJsonAsync<JsonElement>($"/api/projects/{seeded.ProjectId}/assurance-policy");
        Assert.Equal(0, view.GetProperty("version").GetInt32());
        Assert.Empty(view.GetProperty("deviations").EnumerateArray());
    }

    [Fact]
    public async Task A_verification_relaxation_needs_sqa_and_refuses_a_program_manager_an_administrator_and_a_cm()
    {
        var seeded = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, seeded.ConfigurationManager);

        foreach (var (approver, expected) in new[]
        {
            (seeded.Manager, "Software Quality Analyst"),
            (seeded.Administrator, "carries no assurance authority"),
            (seeded.ConfigurationManager, "Self-approval is prohibited"),
        })
        {
            var refused = await client.PutAsJsonAsync($"/api/projects/{seeded.ProjectId}/assurance-policy",
                RelaxCoverage(0, approver));
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            var body = await refused.Content.ReadAsStringAsync();
            Assert.Contains("deviation_approval_refused", body);
            Assert.Contains(expected, body);
        }

        var approved = await client.PutAsJsonAsync($"/api/projects/{seeded.ProjectId}/assurance-policy",
            RelaxCoverage(0, seeded.Sqa));
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        using var json = JsonDocument.Parse(await approved.Content.ReadAsStringAsync());
        var root = json.RootElement;
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.Equal("LevelB", root.GetProperty("declaredLevel").GetString());

        var deviation = Assert.Single(root.GetProperty("deviations").EnumerateArray().ToList());
        Assert.Equal("Verification", deviation.GetProperty("deviationClass").GetString());
        Assert.Equal("Required", deviation.GetProperty("recommended").GetString());
        Assert.Equal("NotRequired", deviation.GetProperty("selected").GetString());
        Assert.Equal("AeroLinkRule", deviation.GetProperty("basisKind").GetString());
        Assert.Equal("Software Quality Analyst", deviation.GetProperty("approvalAuthority").GetString());
        Assert.Equal("Membership", deviation.GetProperty("approvalAuthoritySource").GetString());
        Assert.Equal(seeded.ConfigurationManager, deviation.GetProperty("proposedBy").GetString());
        Assert.Equal(seeded.Sqa, deviation.GetProperty("approvedBy").GetString());
        Assert.Equal(JsonValueKind.Null, deviation.GetProperty("supersededAt").ValueKind);
        Assert.True(deviation.GetProperty("recordVerified").GetBoolean());
        Assert.NotEmpty(deviation.GetProperty("releaseEffect").GetString()!);

        var lever = root.GetProperty("levers").EnumerateArray()
            .Single(x => x.GetProperty("lever").GetString() == nameof(AssurancePolicyLever.RequirementCoverageBeforeRelease));
        Assert.True(lever.GetProperty("isRelaxation").GetBoolean());
    }

    [Fact]
    public async Task An_airworthiness_designated_deviation_requires_airworthiness_even_when_sqa_would_otherwise_approve()
    {
        var seeded = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, seeded.ConfigurationManager);

        var refused = await client.PutAsJsonAsync($"/api/projects/{seeded.ProjectId}/assurance-policy",
            RelaxCoverage(0, seeded.Sqa, airworthinessDesignated: true));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("Airworthiness", await refused.Content.ReadAsStringAsync());

        var approved = await client.PutAsJsonAsync($"/api/projects/{seeded.ProjectId}/assurance-policy",
            RelaxCoverage(0, seeded.Airworthiness, airworthinessDesignated: true));
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        using var json = JsonDocument.Parse(await approved.Content.ReadAsStringAsync());
        var deviation = Assert.Single(json.RootElement.GetProperty("deviations").EnumerateArray().ToList());
        Assert.Equal("Airworthiness", deviation.GetProperty("deviationClass").GetString());
        Assert.True(deviation.GetProperty("airworthinessDesignated").GetBoolean());
    }

    [Fact]
    public async Task A_scoped_and_dated_delegation_approves_and_an_expired_one_does_not()
    {
        var seeded = await SeedAsync(_host.Factory);
        var now = DateTimeOffset.UtcNow;
        using (var scope = _host.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var sqa = await db.UserAccounts.SingleAsync(x => x.UserName == seeded.Sqa);
            db.RoleDelegations.Add(new RoleDelegation(seeded.ProgramId, sqa.Id, seeded.EngineerAccountId,
                ProgramRole.SoftwareQualityAnalyst, now.AddDays(-10), now.AddDays(-1), "Cover while away",
                "test.setup", now));
            await db.SaveChangesAsync();
        }

        using var client = _host.CreateClient();
        await SignInAsync(client, seeded.ConfigurationManager);

        var expired = await client.PutAsJsonAsync($"/api/projects/{seeded.ProjectId}/assurance-policy",
            RelaxCoverage(0, seeded.Engineer));
        Assert.Equal(HttpStatusCode.BadRequest, expired.StatusCode);
        Assert.Contains("deviation_approval_refused", await expired.Content.ReadAsStringAsync());

        using (var scope = _host.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var sqa = await db.UserAccounts.SingleAsync(x => x.UserName == seeded.Sqa);
            db.RoleDelegations.Add(new RoleDelegation(seeded.ProgramId, sqa.Id, seeded.EngineerAccountId,
                ProgramRole.SoftwareQualityAnalyst, now.AddDays(-1), now.AddDays(10), "Cover while away",
                "test.setup", now));
            await db.SaveChangesAsync();
        }

        var approved = await client.PutAsJsonAsync($"/api/projects/{seeded.ProjectId}/assurance-policy",
            RelaxCoverage(0, seeded.Engineer));
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        using var json = JsonDocument.Parse(await approved.Content.ReadAsStringAsync());
        var deviation = Assert.Single(json.RootElement.GetProperty("deviations").EnumerateArray().ToList());
        Assert.Equal("Delegation", deviation.GetProperty("approvalAuthoritySource").GetString());
    }

    [Fact]
    public async Task Policy_versions_and_deviations_are_superseded_rather_than_rewritten()
    {
        var seeded = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, seeded.ConfigurationManager);

        var first = await client.PutAsJsonAsync($"/api/projects/{seeded.ProjectId}/assurance-policy",
            RelaxCoverage(0, seeded.Sqa));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // A stale expected version is refused rather than silently overwriting a policy that moved on.
        var stale = await client.PutAsJsonAsync($"/api/projects/{seeded.ProjectId}/assurance-policy", new
        {
            expectedVersion = 0,
            declaredLevel = "LevelB",
            reason = "Stale edit.",
            selections = Array.Empty<object>(),
            deviations = Array.Empty<object>(),
        });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        var restore = await client.PutAsJsonAsync($"/api/projects/{seeded.ProjectId}/assurance-policy", new
        {
            expectedVersion = 1,
            declaredLevel = "LevelB",
            reason = "Return coverage to the AeroLink recommendation.",
            selections = new[]
            {
                new { lever = nameof(AssurancePolicyLever.RequirementCoverageBeforeRelease), value = nameof(AssuranceLeverValue.Required) },
            },
            deviations = Array.Empty<object>(),
        });
        Assert.Equal(HttpStatusCode.OK, restore.StatusCode);

        using var json = JsonDocument.Parse(await restore.Content.ReadAsStringAsync());
        var root = json.RootElement;
        Assert.Equal(2, root.GetProperty("version").GetInt32());

        var history = root.GetProperty("history").EnumerateArray().ToList();
        Assert.Equal(2, history.Count);
        var superseded = history.Single(x => x.GetProperty("version").GetInt32() == 1);
        Assert.NotEqual(JsonValueKind.Null, superseded.GetProperty("supersededAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, history.Single(x => x.GetProperty("version").GetInt32() == 2)
            .GetProperty("supersededAt").ValueKind);
        // The superseded version still says exactly what it said, hash and all.
        Assert.Contains("RequirementCoverageBeforeRelease=NotRequired", superseded.GetProperty("selectionsSnapshot").GetString());

        var deviation = Assert.Single(root.GetProperty("deviations").EnumerateArray().ToList());
        Assert.NotEqual(JsonValueKind.Null, deviation.GetProperty("supersededAt").ValueKind);
        Assert.Contains("returned", deviation.GetProperty("supersededReason").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("NotRequired", deviation.GetProperty("selected").GetString());
        Assert.True(deviation.GetProperty("recordVerified").GetBoolean());
    }

    [Fact]
    public async Task Assurance_policy_cannot_reach_the_sealed_ladders_structural_configuration()
    {
        var seeded = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, seeded.ConfigurationManager);

        var beforeLadder = await client.GetFromJsonAsync<JsonElement>($"/api/projects/{seeded.ProjectId}/configuration");
        var beforeSteps = beforeLadder.GetProperty("effectiveSteps").GetRawText();
        var beforeVersion = beforeLadder.GetProperty("version").GetInt64();

        var structural = await client.PutAsJsonAsync($"/api/projects/{seeded.ProjectId}/assurance-policy", new
        {
            expectedVersion = 0,
            declaredLevel = "LevelB",
            reason = "Try to take verification off a ladder step.",
            selections = Array.Empty<object>(),
            deviations = Array.Empty<object>(),
            steps = new[] { new { catalogueEntry = "LowLevel", position = 3, capabilities = 1 } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, structural.StatusCode);
        Assert.Contains("structural project configuration", await structural.Content.ReadAsStringAsync());

        // And a policy that *is* accepted leaves the ladder untouched.
        var approved = await client.PutAsJsonAsync($"/api/projects/{seeded.ProjectId}/assurance-policy",
            RelaxCoverage(0, seeded.Sqa));
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        var afterLadder = await client.GetFromJsonAsync<JsonElement>($"/api/projects/{seeded.ProjectId}/configuration");
        Assert.Equal(beforeVersion, afterLadder.GetProperty("version").GetInt64());
        Assert.Equal(beforeSteps, afterLadder.GetProperty("effectiveSteps").GetRawText());
    }

    [Fact]
    public async Task Recording_a_policy_requires_configuration_authority_while_reading_needs_only_project_access()
    {
        var seeded = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, seeded.Engineer);

        Assert.Equal(HttpStatusCode.OK,
            (await client.GetAsync($"/api/projects/{seeded.ProjectId}/assurance-policy")).StatusCode);
        var refused = await client.PutAsJsonAsync($"/api/projects/{seeded.ProjectId}/assurance-policy", new
        {
            expectedVersion = 0,
            declaredLevel = "LevelB",
            reason = "An engineer should not be able to set project policy.",
            selections = Array.Empty<object>(),
            deviations = Array.Empty<object>(),
        });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    [Fact]
    public async Task A_release_campaign_records_the_policy_snapshot_it_began_under()
    {
        var seeded = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, seeded.ConfigurationManager);

        var created = await client.PostAsJsonAsync("/api/release-campaigns", new
        {
            projectId = seeded.ProjectId,
            releaseId = seeded.ReleaseId,
            baselineId = seeded.BaselineId,
            name = "Pre-policy campaign",
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using (var scope = _host.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var campaign = await db.ReleaseCampaigns.AsNoTracking()
                .SingleAsync(x => x.ProjectId == seeded.ProjectId);
            // Created before any policy existed, so it carries no snapshot and resolves to the recommendations.
            Assert.Null(campaign.AssurancePolicyVersionId);
        }

        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync(
            $"/api/projects/{seeded.ProjectId}/assurance-policy", RelaxCoverage(0, seeded.Sqa))).StatusCode);

        using (var scope = _host.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var campaign = await db.ReleaseCampaigns.AsNoTracking()
                .SingleAsync(x => x.ProjectId == seeded.ProjectId);
            // Recording a policy does not reach back into a campaign already under way.
            Assert.Null(campaign.AssurancePolicyVersionId);
        }

        var second = await client.PostAsJsonAsync("/api/release-campaigns", new
        {
            projectId = seeded.ProjectId,
            releaseId = (await NextReleaseAsync(seeded.ProjectId)),
            baselineId = (await NextBaselineAsync(seeded.ProjectId)),
            name = "Post-policy campaign",
        });
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);

        using (var scope = _host.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var effective = await db.ProjectAssurancePolicies.AsNoTracking()
                .SingleAsync(x => x.ProjectId == seeded.ProjectId && x.SupersededAt == null);
            var campaign = await db.ReleaseCampaigns.AsNoTracking()
                .SingleAsync(x => x.ProjectId == seeded.ProjectId && x.Name == "Post-policy campaign");
            Assert.Equal(effective.Id, campaign.AssurancePolicyVersionId);
        }
    }

    private async Task<Guid> NextReleaseAsync(Guid projectId)
    {
        using var scope = _host.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var existing = await db.Releases.AsNoTracking().SingleAsync(x => x.ProjectId == projectId && x.Version == "1.0");
        var release = new SoftwareRelease(projectId, "1.1", false, existing.Id);
        db.Releases.Add(release);
        await db.SaveChangesAsync();
        return release.Id;
    }

    private async Task<Guid> NextBaselineAsync(Guid projectId)
    {
        using var scope = _host.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var release = await db.Releases.AsNoTracking().SingleAsync(x => x.ProjectId == projectId && x.Version == "1.1");
        var baseline = new CandidateBaseline("BL-00000002", 0, projectId, release.Id, null, "Second", "cm", DateTimeOffset.UtcNow);
        db.CandidateBaselines.Add(baseline);
        await db.SaveChangesAsync();
        return baseline.Id;
    }
}
