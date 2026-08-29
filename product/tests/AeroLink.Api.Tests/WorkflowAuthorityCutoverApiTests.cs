using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>
/// The Slice 4 cutover contract: new workflow configuration records an explicit required project authority
/// (a base role, or a Project Leadership position), refuses the retired vocabulary outright, and resolves
/// candidates for the picker from the same typed requirement the signing gate answers.
///
/// Legacy rows recorded before the cutover stay readable as legacy, are never rewritten into modern
/// vocabulary, and a revision of one produces an explicit modern version while the historical row is retained.
/// </summary>
public sealed class WorkflowAuthorityCutoverApiTests : IClassFixture<SharedApiHost>
{
    private readonly SharedApiHost _host;

    public WorkflowAuthorityCutoverApiTests(SharedApiHost host) => _host = host;

    private sealed record Seeded(Guid ProjectId, Guid ProgramId, Guid ManagerId, Guid LeadId, Guid BackupId,
        Guid BaseOnlyId, string ManagerName, string LeadName, string BackupName, string BaseOnlyName);

    /// <summary>
    /// A project whose roster carries the full leadership machinery: a System Engineering Lead primary who
    /// holds the base role, a valid standing backup, and a base-only System Engineer who is eligible for the
    /// position but holds no part of it.
    /// </summary>
    private static async Task<Seeded> SeedRosterAsync(AeroLinkApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;
        var tag = Guid.NewGuid().ToString("N")[..8];
        var managerName = $"cutover.manager.{tag}";
        var leadName = $"cutover.lead.{tag}";
        var backupName = $"cutover.backup.{tag}";
        var baseOnlyName = $"cutover.baseonly.{tag}";
        var program = new ProgramRecord($"Cutover Program {tag}", $"CVP{tag}");
        var project = new ProjectRecord(program.Id, "Flight Software", "Cutover Software");
        db.AddRange(program, project);

        UserAccount Account(string name) =>
            new(name, name, $"{name}@example.test", IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);

        var manager = Account(managerName);
        var lead = Account(leadName);
        var backup = Account(backupName);
        var baseOnly = Account(baseOnlyName);
        db.AddRange(manager, lead, backup, baseOnly);
        db.AddRange(
            new ProgramMembership(manager.Id, program.Id, ProgramRole.ProgramManager, "test.setup", now),
            new ProgramMembership(lead.Id, program.Id, ProgramRole.SystemEngineer, "test.setup", now),
            new ProgramMembership(backup.Id, program.Id, ProgramRole.SystemEngineer, "test.setup", now),
            new ProgramMembership(baseOnly.Id, program.Id, ProgramRole.SystemEngineer, "test.setup", now),
            new ProgramMembership(baseOnly.Id, program.Id, ProgramRole.ProjectEngineer, "test.setup", now));
        db.AddRange(
            new ProjectLeadershipAssignment(program.Id, ProjectLeadershipPosition.SystemEngineeringLead,
                lead.Id, "test.setup", now),
            new ProjectLeadershipAssignment(program.Id, ProjectLeadershipPosition.ProgramManager,
                manager.Id, "test.setup", now),
            new ProjectLeadershipBackup(program.Id, ProjectLeadershipPosition.SystemEngineeringLead,
                backup.Id, "test.setup", now));
        await db.SaveChangesAsync();
        return new(project.Id, program.Id, manager.Id, lead.Id, backup.Id, baseOnly.Id,
            managerName, leadName, backupName, baseOnlyName);
    }

    private static async Task SignInAsync(HttpClient client, string userName)
    {
        var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }

    private static object BaseRoleStage(string name, string role, string kind = "Review") => new
    {
        name,
        kind,
        requiredAuthority = new { kind = "BaseRole", role },
    };

    private static object LeadershipStage(string name, string position, string kind = "Review") => new
    {
        name,
        kind,
        requiredAuthority = new { kind = "LeadershipPosition", position },
    };

    [Fact]
    public async Task A_modern_base_role_stage_persists_and_reads_back_explicitly()
    {
        var seeded = await SeedRosterAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, seeded.ManagerName);

        var saved = await client.PutAsJsonAsync(
            $"/api/projects/{seeded.ProjectId}/approval-configuration/{nameof(ReviewSubject.System)}",
            new { stages = new[] { BaseRoleStage("Technical review", nameof(ProgramRole.SystemEngineer)) } });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var body = await saved.Content.ReadFromJsonAsync<JsonElement>();
        var stage = body.GetProperty("stages").EnumerateArray().Single();
        Assert.Equal("BaseRole", stage.GetProperty("authorityKind").GetString());
        Assert.False(stage.GetProperty("isLegacy").GetBoolean());
        var authority = stage.GetProperty("requiredAuthority");
        Assert.Equal("BaseRole", authority.GetProperty("kind").GetString());
        Assert.Equal(nameof(ProgramRole.SystemEngineer), authority.GetProperty("role").GetString());
        Assert.True(authority.GetProperty("position").ValueKind is JsonValueKind.Null or JsonValueKind.Undefined);
    }

    [Fact]
    public async Task A_modern_leadership_stage_persists_and_reads_back_explicitly()
    {
        var seeded = await SeedRosterAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, seeded.ManagerName);

        var saved = await client.PutAsJsonAsync(
            $"/api/projects/{seeded.ProjectId}/approval-configuration/{nameof(ReviewSubject.System)}",
            new { stages = new[] { LeadershipStage("Lead approval", nameof(ProjectLeadershipPosition.SystemEngineeringLead), "Approval") } });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var body = await saved.Content.ReadFromJsonAsync<JsonElement>();
        var stage = body.GetProperty("stages").EnumerateArray().Single();
        Assert.Equal("LeadershipPosition", stage.GetProperty("authorityKind").GetString());
        var authority = stage.GetProperty("requiredAuthority");
        Assert.Equal(nameof(ProjectLeadershipPosition.SystemEngineeringLead), authority.GetProperty("position").GetString());
        Assert.True(authority.GetProperty("role").ValueKind is JsonValueKind.Null or JsonValueKind.Undefined);
    }

    [Theory]
    [InlineData("""{"name":"No authority","kind":"Review"}""")]
    [InlineData("""{"name":"Unknown kind","kind":"Review","requiredAuthority":{"kind":"SomethingElse","role":"SystemEngineer"}}""")]
    [InlineData("""{"name":"Base with position","kind":"Review","requiredAuthority":{"kind":"BaseRole","position":"ConfigurationManager"}}""")]
    [InlineData("""{"name":"Leadership with role","kind":"Review","requiredAuthority":{"kind":"LeadershipPosition","role":"SystemEngineer"}}""")]
    [InlineData("""{"name":"Legacy write","kind":"Review","requiredAuthority":{"kind":"LegacyRoleDemand","role":"Reviewer"}}""")]
    public async Task A_write_that_is_missing_contradictory_or_legacy_shaped_is_refused(string stageJson)
    {
        var seeded = await SeedRosterAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, seeded.ManagerName);

        var stage = JsonDocument.Parse(stageJson).RootElement.Clone();
        var response = await client.PutAsJsonAsync(
            $"/api/projects/{seeded.ProjectId}/approval-configuration/{nameof(ReviewSubject.System)}",
            new { stages = new[] { stage } });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadAsStringAsync();
        Assert.True(
            error.Contains("authority", StringComparison.OrdinalIgnoreCase)
            || error.Contains("position", StringComparison.OrdinalIgnoreCase),
            error);
    }

    [Theory]
    [InlineData(nameof(ProgramRole.Reviewer))]
    [InlineData(nameof(ProgramRole.Approver))]
    [InlineData(nameof(ProgramRole.ProjectEngineeringLead))]
    [InlineData(nameof(ProgramRole.SystemEngineeringLead))]
    [InlineData(nameof(ProgramRole.Engineer))]
    public async Task Retired_and_non_configurable_roles_are_refused_as_modern_base_role_authority(string role)
    {
        var seeded = await SeedRosterAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, seeded.ManagerName);

        var response = await client.PutAsJsonAsync(
            $"/api/projects/{seeded.ProjectId}/approval-configuration/{nameof(ReviewSubject.System)}",
            new { stages = new[] { BaseRoleStage("Generic stage", role) } });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_leadership_stage_offers_its_primary_and_backup_but_never_a_base_only_member()
    {
        var seeded = await SeedAsync(_host.Factory,
            stages: [LeadershipStage("Lead approval", nameof(ProjectLeadershipPosition.SystemEngineeringLead))]);
        using var client = _host.CreateClient();
        await SignInAsync(client, seeded.ManagerName);

        var body = await client.GetFromJsonAsync<JsonElement>(
            $"/api/review-workflows/applicable?projectId={seeded.ProjectId}&type=System");
        var stage = body.GetProperty("stages").EnumerateArray().Single();
        Assert.Equal("LeadershipPosition", stage.GetProperty("authorityKind").GetString());
        var candidates = stage.GetProperty("candidates").EnumerateArray()
            .ToDictionary(x => x.GetProperty("userId").GetString()!, x => x.GetProperty("via").GetString());

        Assert.Equal(nameof(ProjectAuthoritySource.LeadershipPrimary), candidates[seeded.LeadName]);
        Assert.Equal(nameof(ProjectAuthoritySource.LeadershipBackup), candidates[seeded.BackupName]);
        Assert.DoesNotContain(candidates, x => x.Key == seeded.BaseOnlyName);
    }

    [Fact]
    public async Task Base_role_and_leadership_demands_on_the_same_name_resolve_different_candidate_sets()
    {
        // The #816 regression: ProjectEngineer the JOB and ProjectEngineer the POSITION are two different
        // demands. The base-only member answers the job; only the leadership machinery answers the position.
        var seeded = await SeedAsync(_host.Factory, stages:
        [
            BaseRoleStage("Engineering the work", nameof(ProgramRole.ProjectEngineer)),
            LeadershipStage("Accountable engineering", nameof(ProjectLeadershipPosition.ProjectEngineer)),
        ]);
        using var client = _host.CreateClient();
        await SignInAsync(client, seeded.ManagerName);

        var body = await client.GetFromJsonAsync<JsonElement>(
            $"/api/review-workflows/applicable?projectId={seeded.ProjectId}&type=System");
        var stages = body.GetProperty("stages").EnumerateArray().ToList();
        var baseStage = stages.Single(x => x.GetProperty("name").GetString() == "Engineering the work");
        var leadershipStage = stages.Single(x => x.GetProperty("name").GetString() == "Accountable engineering");

        var baseCandidates = baseStage.GetProperty("candidates").EnumerateArray()
            .Select(x => x.GetProperty("userId").GetString()).ToHashSet();
        var leadershipCandidates = leadershipStage.GetProperty("candidates").EnumerateArray()
            .Select(x => x.GetProperty("userId").GetString()).ToHashSet();

        Assert.Contains(seeded.BaseOnlyName, baseCandidates);
        Assert.DoesNotContain(seeded.LeadName, baseCandidates);
        Assert.DoesNotContain(seeded.BaseOnlyName, leadershipCandidates);
    }

    [Fact]
    public async Task The_signing_gate_answers_the_same_requirement_the_picker_offered()
    {
        var seeded = await SeedAsync(_host.Factory, stages:
        [
            LeadershipStage("Lead approval", nameof(ProjectLeadershipPosition.SystemEngineeringLead), "Approval"),
            BaseRoleStage("Engineering review", nameof(ProgramRole.ProjectEngineer)),
        ]);
        Guid workflowId;
        using (var scope = _host.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var workflow = await db.ReviewWorkflows.AsNoTracking().Include(x => x.Stages)
                .SingleAsync(x => x.ProjectId == seeded.ProjectId);
            workflowId = workflow.Id;
        }

        using var gateScope = _host.Factory.Services.CreateScope();
        var gateDb = gateScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var specification = (await gateDb.ReviewWorkflows.AsNoTracking().Include(x => x.Stages)
            .SingleAsync(x => x.Id == workflowId)).Specification();

        // The leadership primary and the standing backup answer the leadership stage; the base-only System
        // Engineer does not — the exact set the candidate picker offered.
        Assert.Equal(ProgramRole.SystemEngineeringLead, await WorkflowEndpoints.StageAuthorityAsync(
            gateDb, seeded.ProjectId, seeded.LeadId, specification.Stages[0], default));
        Assert.Equal(ProgramRole.SystemEngineeringLead, await WorkflowEndpoints.StageAuthorityAsync(
            gateDb, seeded.ProjectId, seeded.BackupId, specification.Stages[0], default));
        Assert.Null(await WorkflowEndpoints.StageAuthorityAsync(
            gateDb, seeded.ProjectId, seeded.BaseOnlyId, specification.Stages[0], default));

        // The base-role stage answers the job, and elevation alone does not answer it for somebody who
        // never held the job.
        Assert.Equal(ProgramRole.ProjectEngineer, await WorkflowEndpoints.StageAuthorityAsync(
            gateDb, seeded.ProjectId, seeded.BaseOnlyId, specification.Stages[1], default));
        Assert.Null(await WorkflowEndpoints.StageAuthorityAsync(
            gateDb, seeded.ProjectId, seeded.LeadId, specification.Stages[1], default));
    }

    [Fact]
    public async Task New_personnel_grants_of_Reviewer_and_Approver_are_refused_by_the_server()
    {
        var seeded = await SeedRosterAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, seeded.ManagerName);

        foreach (var role in new[] { "Reviewer", "Approver" })
        {
            var refused = await client.PostAsJsonAsync($"/api/projects/{seeded.ProjectId}/personnel",
                new { userId = seeded.BaseOnlyId, roles = new[] { role } });
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Contains("signature meaning", await refused.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task A_legacy_workflow_reads_as_legacy_and_its_revision_writes_an_explicit_modern_version()
    {
        var seeded = await SeedRosterAsync(_host.Factory);
        using (var scope = _host.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var now = DateTimeOffset.UtcNow;
            var legacy = new ReviewWorkflow(seeded.ProjectId, "Legacy review", ReviewSubject.System,
                ReviewMode.Sequential,
                [new ReviewWorkflowStageDraft("Historic generic review", ProgramRole.Reviewer)],
                "test.setup", now);
            legacy.Activate("test.setup", now);
            db.ReviewWorkflows.Add(legacy);
            await db.SaveChangesAsync();
        }

        using var client = _host.CreateClient();
        await SignInAsync(client, seeded.ManagerName);

        // The historical row stays exactly what it was, and says so: a persisted Reviewer demand is never
        // presented as a modern base role.
        var before = await client.GetFromJsonAsync<JsonElement>(
            $"/api/projects/{seeded.ProjectId}/approval-configuration");
        var legacyStage = before.GetProperty("artifacts").EnumerateArray()
            .Single(x => x.GetProperty("subject").GetString() == "System")
            .GetProperty("stages").EnumerateArray().Single();
        Assert.True(legacyStage.GetProperty("isLegacy").GetBoolean());
        var legacyAuthority = legacyStage.GetProperty("requiredAuthority");
        Assert.Equal("LegacyRoleDemand", legacyAuthority.GetProperty("kind").GetString());
        Assert.Equal(nameof(ProgramRole.Reviewer), legacyAuthority.GetProperty("role").GetString());
        Assert.Equal(nameof(ReviewStageKind.Review), legacyStage.GetProperty("kind").GetString());

        // Revising produces a NEW explicit version. The prior version is retained unchanged for history.
        var revised = await client.PutAsJsonAsync(
            $"/api/projects/{seeded.ProjectId}/approval-configuration/{nameof(ReviewSubject.System)}",
            new { name = "Legacy review", stages = new[] { LeadershipStage("Lead approval", nameof(ProjectLeadershipPosition.SystemEngineeringLead), "Approval") } });
        Assert.Equal(HttpStatusCode.OK, revised.StatusCode);
        var revisedBody = await revised.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, revisedBody.GetProperty("version").GetInt32());
        Assert.Equal("LeadershipPosition",
            revisedBody.GetProperty("stages").EnumerateArray().Single().GetProperty("authorityKind").GetString());

        using (var scope = _host.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var versions = await db.ReviewWorkflows.AsNoTracking().Include(x => x.Stages)
                .Where(x => x.ProjectId == seeded.ProjectId).OrderBy(x => x.Version).ToListAsync();
            Assert.Equal([ReviewWorkflowState.Retired, ReviewWorkflowState.Active], versions.Select(x => x.State).ToArray());
            var historical = versions[0].Stages.Single();
            Assert.Null(historical.RequiredAuthorityKind);
            Assert.Equal(ProgramRole.Reviewer, historical.RequiredRole);
            var modern = versions[1].Stages.Single();
            Assert.Equal(ReviewStageAuthorityKind.LeadershipPosition, modern.RequiredAuthorityKind);
        }
    }

    private static async Task<Seeded> SeedAsync(AeroLinkApiFactory factory,
        IReadOnlyList<object>? stages = null)
    {
        var seeded = await SeedRosterAsync(factory);
        if (stages is null) return seeded;

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;
        var workflow = new ReviewWorkflow(seeded.ProjectId, "Cutover review", ReviewSubject.System,
            ReviewMode.Sequential, stages.Select(ToDomainStage).ToList(), "test.setup", now);
        workflow.Activate("test.setup", now);
        db.ReviewWorkflows.Add(workflow);
        await db.SaveChangesAsync();
        return seeded;
    }

    private static ReviewWorkflowStageDraft ToDomainStage(object stage)
    {
        var element = JsonSerializer.SerializeToElement(stage);
        var name = element!.GetProperty("name").GetString()!;
        var kind = Enum.Parse<ReviewStageKind>(element.GetProperty("kind").GetString());
        var authority = element.GetProperty("requiredAuthority");
        return authority.GetProperty("kind").GetString() switch
        {
            "BaseRole" => new ReviewWorkflowStageDraft(name,
                Enum.Parse<ProgramRole>(authority.GetProperty("role").GetString()!), kind,
                ReviewStageAuthorityKind.BaseRole),
            _ => new ReviewWorkflowStageDraft(name,
                Enum.Parse<ProgramRole>(authority.GetProperty("position").GetString()!), kind,
                ReviewStageAuthorityKind.LeadershipPosition),
        };
    }
}
