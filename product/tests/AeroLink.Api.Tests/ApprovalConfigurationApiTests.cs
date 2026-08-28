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
/// Reading a review procedure against the roster.
///
/// A stage names an authority rather than a person so it survives somebody changing jobs. The cost is that a
/// procedure can require a position nobody holds and look perfectly healthy until an author submits and the
/// review stops there. These assert that the answer is available before that happens, and that a standing
/// backup counts — a stage with an empty position but a named backup is not blocked.
/// </summary>
public sealed class ApprovalConfigurationApiTests : IClassFixture<SharedApiHost>
{
    private readonly SharedApiHost _host;

    public ApprovalConfigurationApiTests(SharedApiHost host)
    {
        _host = host;
    }

    private sealed record Seeded(Guid ProjectId, Guid ProgramId, Guid ManagerId, Guid LeadId, Guid DeputyId,
        string ManagerName, string LeadName, string DeputyName);

    private static async Task<Seeded> SeedAsync(AeroLinkApiFactory factory, IReadOnlyList<ReviewWorkflowStageDraft> stages)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;
        // Unique per test: user accounts and Program codes are globally unique-constrained, so a shared
        // host/database requires per-test identities.
        var tag = Guid.NewGuid().ToString("N")[..8];
        var managerName = $"config.manager.{tag}";
        var leadName = $"config.lead.{tag}";
        var deputyName = $"config.deputy.{tag}";
        var program = new ProgramRecord($"Approval Config Program {tag}", $"ACP{tag}");
        var project = new ProjectRecord(program.Id, "Flight Software", "Approval Config Software");
        db.AddRange(program, project);

        UserAccount Account(string name) =>
            new(name, name, $"{name}@example.test", IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);

        var manager = Account(managerName);
        var lead = Account(leadName);
        var deputy = Account(deputyName);
        db.AddRange(manager, lead, deputy);
        db.AddRange(
            new ProgramMembership(manager.Id, program.Id, ProgramRole.ProgramManager, "test.setup", now),
            new ProgramMembership(lead.Id, program.Id, ProgramRole.SystemEngineer, "test.setup", now),
            new ProgramMembership(deputy.Id, program.Id, ProgramRole.SystemEngineer, "test.setup", now));
        // The stage names the System Engineering Lead. Since #816 that demand is answered by the position, so
        // the lead is elevated into it rather than granted the retired role name — and managing the roster is
        // the Program Manager position's, so the manager is elevated too.
        db.AddRange(
            new ProjectLeadershipAssignment(
                program.Id, ProjectLeadershipPosition.SystemEngineeringLead, lead.Id, "test.setup", now),
            new ProjectLeadershipAssignment(
                program.Id, ProjectLeadershipPosition.ProgramManager, manager.Id, "test.setup", now));

        var workflow = new ReviewWorkflow(project.Id, "System review", ReviewSubject.System, ReviewMode.Sequential,
            stages, "test.setup", now);
        workflow.Activate("test.setup", now);
        db.ReviewWorkflows.Add(workflow);
        await db.SaveChangesAsync();
        return new(project.Id, program.Id, manager.Id, lead.Id, deputy.Id, managerName, leadName, deputyName);
    }

    private static async Task SignInAsync(HttpClient client, string userName)
    {
        var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }

    private static async Task<ConfigurationResponse> ReadAsync(HttpClient client, Guid projectId)
    {
        var response = await client.GetAsync($"/api/projects/{projectId}/approval-configuration");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ConfigurationResponse>())!;
    }

    [Fact]
    public async Task A_stage_naming_a_position_one_person_holds_resolves_to_that_person()
    {
        var seeded = await SeedAsync(_host.Factory,
        [
            new("Discipline review", ProgramRole.SystemEngineeringLead),
            new("Release approval", ProgramRole.ProgramManager, ReviewStageKind.Approval),
        ]);
        using var client = _host.CreateClient();
        await SignInAsync(client, seeded.ManagerName);

        var system = (await ReadAsync(client, seeded.ProjectId)).Artifacts.Single(x => x.Subject == "System");
        Assert.True(system.Configured);
        Assert.Equal(0, system.BlockingStages);

        var discipline = system.Stages!.Single(x => x.Name == "Discipline review");
        Assert.Equal("Review", discipline.Kind);
        Assert.True(discipline.Required.Singular);
        Assert.Equal([seeded.LeadName], discipline.Required.Holders);
        Assert.False(discipline.Required.Blocking);

        // The kind is what makes the two signatures distinguishable; before this every step read "Reviewer".
        Assert.Equal("Approval", system.Stages!.Single(x => x.Name == "Release approval").Kind);
    }

    /// <summary>
    /// The failure this page exists to catch, found while configuring rather than at a release gate.
    /// </summary>
    [Fact]
    public async Task A_stage_naming_a_position_nobody_holds_is_reported_as_blocking()
    {
        var seeded = await SeedAsync(_host.Factory,
        [
            new("Airworthiness review", ProgramRole.Airworthiness),
        ]);
        using var client = _host.CreateClient();
        await SignInAsync(client, seeded.ManagerName);

        var system = (await ReadAsync(client, seeded.ProjectId)).Artifacts.Single(x => x.Subject == "System");
        Assert.Equal(1, system.BlockingStages);
        var stage = system.Stages!.Single();
        Assert.True(stage.Required.Blocking);
        Assert.Empty(stage.Required.Holders);
    }

    /// <summary>
    /// A standing backup can sign, so an empty position with a named backup is not a blocked stage. Reporting
    /// it as blocked would send somebody to fill a position that is already covered.
    /// </summary>
    [Fact]
    public async Task A_standing_backup_keeps_a_stage_signable()
    {
        var seeded = await SeedAsync(_host.Factory,
        [
            new("Assurance review", ProgramRole.SoftwareQualityAnalyst),
        ]);
        using var client = _host.CreateClient();
        await SignInAsync(client, seeded.ManagerName);

        var before = (await ReadAsync(client, seeded.ProjectId)).Artifacts.Single(x => x.Subject == "System");
        Assert.Equal(1, before.BlockingStages);

        var named = await client.PostAsJsonAsync($"/api/projects/{seeded.ProjectId}/personnel/backups",
            new { backupUserId = seeded.DeputyId, role = nameof(ProgramRole.SoftwareQualityAnalyst) });
        Assert.Equal(HttpStatusCode.NoContent, named.StatusCode);

        var after = (await ReadAsync(client, seeded.ProjectId)).Artifacts.Single(x => x.Subject == "System");
        Assert.Equal(0, after.BlockingStages);
        Assert.Equal([seeded.DeputyName], after.Stages!.Single().Required.Backups);
    }

    /// <summary>
    /// Ending the holder's position turns a signable stage into a blocked one, which is the whole reason the
    /// two pages are read together.
    /// </summary>
    [Fact]
    public async Task Ending_the_only_holder_blocks_the_stage_that_named_them()
    {
        var seeded = await SeedAsync(_host.Factory,
        [
            new("Discipline review", ProgramRole.SystemEngineeringLead),
        ]);
        using var client = _host.CreateClient();
        await SignInAsync(client, seeded.ManagerName);

        Assert.Equal(0, (await ReadAsync(client, seeded.ProjectId)).Artifacts.Single(x => x.Subject == "System").BlockingStages);

        // Removing the eligibility is what stands the position down: the assignment survives as history, but
        // #816 re-checks the base role whenever the authority is exercised, so the stage has nobody to sign
        // it from the moment the lead stops being a System Engineer.
        await client.DeleteAsync($"/api/projects/{seeded.ProjectId}/personnel/{seeded.LeadId}/roles/{nameof(ProgramRole.SystemEngineer)}");

        Assert.Equal(1, (await ReadAsync(client, seeded.ProjectId)).Artifacts.Single(x => x.Subject == "System").BlockingStages);
    }

    /// <summary>
    /// An artifact with no recorded procedure is not a blocked one. A rule nobody has written down must not
    /// become a rule that stops work — the same principle the workflow aggregate itself is built on.
    /// </summary>
    [Fact]
    public async Task An_artifact_with_no_procedure_is_reported_as_unconfigured_not_blocked()
    {
        var seeded = await SeedAsync(_host.Factory, [new("Discipline review", ProgramRole.SystemEngineeringLead)]);
        using var client = _host.CreateClient();
        await SignInAsync(client, seeded.ManagerName);

        var software = (await ReadAsync(client, seeded.ProjectId)).Artifacts.Single(x => x.Subject == "Software");
        Assert.False(software.Configured);
        Assert.Equal(0, software.BlockingStages);
        Assert.Null(software.Stages);
    }

    /// <summary>Every artifact type is reported, so an unconfigured one is visible rather than absent.</summary>
    [Fact]
    public async Task Every_artifact_type_is_reported()
    {
        var seeded = await SeedAsync(_host.Factory, [new("Discipline review", ProgramRole.SystemEngineeringLead)]);
        using var client = _host.CreateClient();
        await SignInAsync(client, seeded.ManagerName);

        var subjects = (await ReadAsync(client, seeded.ProjectId)).Artifacts.Select(x => x.Subject).ToList();
        Assert.Equal(
            ["System", "Software", "SystemTest", "HighLevelSoftwareCase", "LowLevelSoftwareCase"],
            subjects);
    }

    [Fact]
    public async Task An_authorized_manager_can_save_and_revise_an_active_configuration()
    {
        var seeded = await SeedAsync(_host.Factory, [new("Initial review", ProgramRole.SystemEngineer)]);
        using var client = _host.CreateClient();
        await SignInAsync(client, seeded.ManagerName);
        const string subject = nameof(ReviewSubject.System);

        var first = await client.PutAsJsonAsync($"/api/projects/{seeded.ProjectId}/approval-configuration/{subject}", new
        {
            stages = new[]
            {
                new { name = "System engineer review", requiredRole = ProgramRole.SystemEngineer.ToString(), kind = "Review" },
                new { name = "Program approval", requiredRole = ProgramRole.ProgramManager.ToString(), kind = "Approval" },
            },
        });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstBody = await first.Content.ReadFromJsonAsync<ConfiguredResponse>();
        Assert.Equal(2, firstBody!.Version);
        Assert.Equal(2, firstBody.Stages.Length);

        var second = await client.PutAsJsonAsync($"/api/projects/{seeded.ProjectId}/approval-configuration/{subject}", new
        {
            stages = new[]
            {
                new { name = "Lead review", requiredRole = ProgramRole.SystemEngineeringLead.ToString(), kind = "Review" },
            },
        });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var secondBody = await second.Content.ReadFromJsonAsync<ConfiguredResponse>();
        Assert.Equal(3, secondBody!.Version);
        Assert.Single(secondBody.Stages);

        using var scope = _host.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var history = await db.ReviewWorkflows.AsNoTracking().Where(x => x.ProjectId == seeded.ProjectId && x.AppliesTo == ReviewSubject.System).OrderBy(x => x.Version).ToListAsync();
        Assert.Equal([ReviewWorkflowState.Retired, ReviewWorkflowState.Retired, ReviewWorkflowState.Active], history.Select(x => x.State).ToArray());
    }

    [Fact]
    public async Task A_non_manager_cannot_mutate_approval_configuration()
    {
        var seeded = await SeedAsync(_host.Factory, [new("Initial review", ProgramRole.SystemEngineer)]);
        using var client = _host.CreateClient();
        await SignInAsync(client, seeded.LeadName);

        var response = await client.PutAsJsonAsync($"/api/projects/{seeded.ProjectId}/approval-configuration/System", new
        {
            stages = new[] { new { name = "Unauthorized", requiredRole = ProgramRole.SystemEngineer.ToString(), kind = "Review" } },
        });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Configuration_management_requires_the_position_not_only_its_base_role()
    {
        var seeded = await SeedAsync(_host.Factory, [new("Initial review", ProgramRole.SystemEngineer)]);
        var tag = Guid.NewGuid().ToString("N")[..8];
        var baseName = $"config.base.{tag}";
        var holderName = $"config.holder.{tag}";
        using (var scope = _host.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var now = DateTimeOffset.UtcNow;
            UserAccount Account(string name) => new(name, name, $"{name}@example.test",
                IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            var baseOnly = Account(baseName);
            var holder = Account(holderName);
            db.AddRange(baseOnly, holder);
            db.AddRange(
                new ProgramMembership(baseOnly.Id, seeded.ProgramId, ProgramRole.ConfigurationManager, "test.setup", now),
                new ProgramMembership(holder.Id, seeded.ProgramId, ProgramRole.ConfigurationManager, "test.setup", now),
                new ProjectLeadershipAssignment(seeded.ProgramId, ProjectLeadershipPosition.ConfigurationManager,
                    holder.Id, "test.setup", now));
            await db.SaveChangesAsync();
        }

        using var baseClient = _host.CreateClient();
        await SignInAsync(baseClient, baseName);
        Assert.False((await ReadAsync(baseClient, seeded.ProjectId)).CanManage);
        var refused = await baseClient.PutAsJsonAsync(
            $"/api/projects/{seeded.ProjectId}/approval-configuration/{nameof(ReviewSubject.System)}", new
            {
                stages = new[] { new { name = "Base-only attempt", requiredRole = ProgramRole.SystemEngineer.ToString(), kind = "Review" } },
            });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        using var holderClient = _host.CreateClient();
        await SignInAsync(holderClient, holderName);
        Assert.True((await ReadAsync(holderClient, seeded.ProjectId)).CanManage);
        var accepted = await holderClient.PutAsJsonAsync(
            $"/api/projects/{seeded.ProjectId}/approval-configuration/{nameof(ReviewSubject.System)}", new
            {
                stages = new[] { new { name = "Position-holder update", requiredRole = ProgramRole.SystemEngineer.ToString(), kind = "Review" } },
            });
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
    }

    [Fact]
    public async Task Legacy_revision_version_collision_returns_conflict()
    {
        var seeded = await SeedAsync(_host.Factory, [new("Initial review", ProgramRole.SystemEngineer)]);
        Guid currentId;
        using (var scope = _host.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var current = await db.ReviewWorkflows.Include(x => x.Stages)
                .SingleAsync(x => x.ProjectId == seeded.ProjectId && x.AppliesTo == ReviewSubject.System
                    && x.State == ReviewWorkflowState.Active);
            var competing = current.Revise("Competing revision", ReviewMode.Sequential,
                [new("Competing review", ProgramRole.SystemEngineer)], "test.setup", DateTimeOffset.UtcNow);
            db.ReviewWorkflows.Add(competing);
            await db.SaveChangesAsync();
            currentId = current.Id;
        }

        using var client = _host.CreateClient();
        await SignInAsync(client, seeded.ManagerName);
        var response = await client.PostAsJsonAsync($"/api/review-workflows/{currentId}/revise", new
        {
            name = "Racing revision",
            mode = ReviewMode.Sequential.ToString(),
            stages = new[]
            {
                new { name = "Racing review", requiredRole = ProgramRole.SystemEngineer.ToString(), kind = "Review" },
            },
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Applicable_workflow_lists_a_live_exact_role_delegate_as_a_required_stage_candidate()
    {
        var seeded = await SeedAsync(_host.Factory,
        [
            new("Lead approval", ProgramRole.SystemEngineeringLead, ReviewStageKind.Approval),
        ]);
        using var scope = _host.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;
        db.RoleDelegations.Add(new RoleDelegation(seeded.ProgramId, seeded.LeadId, seeded.DeputyId,
            ProgramRole.SystemEngineeringLead, now.AddMinutes(-1), now.AddHours(1),
            "Temporary lead coverage.", "test.setup", now));
        await db.SaveChangesAsync();

        using var client = _host.CreateClient();
        await SignInAsync(client, seeded.ManagerName);
        var body = await client.GetFromJsonAsync<JsonElement>(
            $"/api/review-workflows/applicable?projectId={seeded.ProjectId}&type=System");
        var candidate = body.GetProperty("stages")[0].GetProperty("candidates")
            .EnumerateArray().Single(x => x.GetProperty("userId").GetString() == seeded.DeputyName);

        Assert.Equal(nameof(ProgramRole.SystemEngineeringLead), candidate.GetProperty("role").GetString());
    }

    [Fact]
    public async Task Applicable_workflow_does_not_offer_a_different_role_delegate_for_a_generic_stage()
    {
        var seeded = await SeedAsync(_host.Factory,
        [
            new("Generic review", ProgramRole.Reviewer),
        ]);
        using var scope = _host.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;
        db.RoleDelegations.Add(new RoleDelegation(seeded.ProgramId, seeded.LeadId, seeded.DeputyId,
            ProgramRole.SystemEngineeringLead, now.AddMinutes(-1), now.AddHours(1),
            "Temporary lead coverage.", "test.setup", now));
        await db.SaveChangesAsync();

        using var client = _host.CreateClient();
        await SignInAsync(client, seeded.ManagerName);
        var body = await client.GetFromJsonAsync<JsonElement>(
            $"/api/review-workflows/applicable?projectId={seeded.ProjectId}&type=System");
        var candidates = body.GetProperty("stages")[0].GetProperty("candidates").EnumerateArray();

        // A delegation is exact-role authority. SystemEngineeringLead satisfies a generic Reviewer stage for
        // direct holders, but it must not be inferred through a delegation that IdentityService would reject.
        Assert.DoesNotContain(candidates, x => x.GetProperty("userId").GetString() == seeded.DeputyName);
    }

    private sealed record ConfigurationResponse(Guid ProjectId, bool CanManage, ArtifactRow[] Artifacts);
    private sealed record ArtifactRow(string Subject, bool Configured, string? Name, int? Version, string? Mode,
        StageRow[]? Stages, int BlockingStages);
    private sealed record StageRow(int Position, string Name, string Kind, RequiredRow Required);
    private sealed record RequiredRow(string Role, bool Singular, string[] Holders, string[] Backups, bool Blocking);
    private sealed record ConfiguredResponse(Guid ProjectId, string Subject, bool Configured, string Name, int Version,
        string Mode, ConfiguredStage[] Stages);
    private sealed record ConfiguredStage(int Position, string Name, string Kind, string RequiredRole);
}
