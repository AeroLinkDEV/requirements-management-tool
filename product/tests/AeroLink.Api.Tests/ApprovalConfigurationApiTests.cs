using System.Net;
using System.Net.Http.Json;
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
public sealed class ApprovalConfigurationApiTests
{
    private const string Manager = "config.manager";
    private const string Lead = "config.lead";
    private const string Deputy = "config.deputy";

    private sealed record Seeded(Guid ProjectId, Guid ProgramId, Guid ManagerId, Guid LeadId, Guid DeputyId);

    private static async Task<Seeded> SeedAsync(AeroLinkApiFactory factory, IReadOnlyList<ReviewWorkflowStageDraft> stages)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;
        var program = new ProgramRecord("Approval Config Program", "ACP");
        var project = new ProjectRecord(program.Id, "Flight Software", "Approval Config Software");
        db.AddRange(program, project);

        UserAccount Account(string name) =>
            new(name, name, $"{name}@example.test", IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);

        var manager = Account(Manager);
        var lead = Account(Lead);
        var deputy = Account(Deputy);
        db.AddRange(manager, lead, deputy);
        db.AddRange(
            new ProgramMembership(manager.Id, program.Id, ProgramRole.ProgramManager, "test.setup", now),
            new ProgramMembership(lead.Id, program.Id, ProgramRole.SystemEngineeringLead, "test.setup", now),
            new ProgramMembership(deputy.Id, program.Id, ProgramRole.SystemEngineer, "test.setup", now));

        var workflow = new ReviewWorkflow(project.Id, "System review", ReviewSubject.System, ReviewMode.Sequential,
            stages, "test.setup", now);
        workflow.Activate("test.setup", now);
        db.ReviewWorkflows.Add(workflow);
        await db.SaveChangesAsync();
        return new(project.Id, program.Id, manager.Id, lead.Id, deputy.Id);
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
        await using var factory = new AeroLinkApiFactory();
        var seeded = await SeedAsync(factory,
        [
            new("Discipline review", ProgramRole.SystemEngineeringLead),
            new("Release approval", ProgramRole.ProgramManager, ReviewStageKind.Approval),
        ]);
        using var client = factory.CreateClient();
        await SignInAsync(client, Manager);

        var system = (await ReadAsync(client, seeded.ProjectId)).Artifacts.Single(x => x.Subject == "System");
        Assert.True(system.Configured);
        Assert.Equal(0, system.BlockingStages);

        var discipline = system.Stages!.Single(x => x.Name == "Discipline review");
        Assert.Equal("Review", discipline.Kind);
        Assert.True(discipline.Required.Singular);
        Assert.Equal([Lead], discipline.Required.Holders);
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
        await using var factory = new AeroLinkApiFactory();
        var seeded = await SeedAsync(factory,
        [
            new("Airworthiness review", ProgramRole.Airworthiness),
        ]);
        using var client = factory.CreateClient();
        await SignInAsync(client, Manager);

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
        await using var factory = new AeroLinkApiFactory();
        var seeded = await SeedAsync(factory,
        [
            new("Assurance review", ProgramRole.SoftwareQualityAnalyst),
        ]);
        using var client = factory.CreateClient();
        await SignInAsync(client, Manager);

        var before = (await ReadAsync(client, seeded.ProjectId)).Artifacts.Single(x => x.Subject == "System");
        Assert.Equal(1, before.BlockingStages);

        var named = await client.PostAsJsonAsync($"/api/projects/{seeded.ProjectId}/personnel/backups",
            new { backupUserId = seeded.DeputyId, role = nameof(ProgramRole.SoftwareQualityAnalyst) });
        Assert.Equal(HttpStatusCode.NoContent, named.StatusCode);

        var after = (await ReadAsync(client, seeded.ProjectId)).Artifacts.Single(x => x.Subject == "System");
        Assert.Equal(0, after.BlockingStages);
        Assert.Equal([Deputy], after.Stages!.Single().Required.Backups);
    }

    /// <summary>
    /// Ending the holder's position turns a signable stage into a blocked one, which is the whole reason the
    /// two pages are read together.
    /// </summary>
    [Fact]
    public async Task Ending_the_only_holder_blocks_the_stage_that_named_them()
    {
        await using var factory = new AeroLinkApiFactory();
        var seeded = await SeedAsync(factory,
        [
            new("Discipline review", ProgramRole.SystemEngineeringLead),
        ]);
        using var client = factory.CreateClient();
        await SignInAsync(client, Manager);

        Assert.Equal(0, (await ReadAsync(client, seeded.ProjectId)).Artifacts.Single(x => x.Subject == "System").BlockingStages);

        await client.DeleteAsync($"/api/projects/{seeded.ProjectId}/personnel/{seeded.LeadId}/roles/{nameof(ProgramRole.SystemEngineeringLead)}");

        Assert.Equal(1, (await ReadAsync(client, seeded.ProjectId)).Artifacts.Single(x => x.Subject == "System").BlockingStages);
    }

    /// <summary>
    /// An artifact with no recorded procedure is not a blocked one. A rule nobody has written down must not
    /// become a rule that stops work — the same principle the workflow aggregate itself is built on.
    /// </summary>
    [Fact]
    public async Task An_artifact_with_no_procedure_is_reported_as_unconfigured_not_blocked()
    {
        await using var factory = new AeroLinkApiFactory();
        var seeded = await SeedAsync(factory, [new("Discipline review", ProgramRole.SystemEngineeringLead)]);
        using var client = factory.CreateClient();
        await SignInAsync(client, Manager);

        var software = (await ReadAsync(client, seeded.ProjectId)).Artifacts.Single(x => x.Subject == "Software");
        Assert.False(software.Configured);
        Assert.Equal(0, software.BlockingStages);
        Assert.Null(software.Stages);
    }

    /// <summary>Every artifact type is reported, so an unconfigured one is visible rather than absent.</summary>
    [Fact]
    public async Task Every_artifact_type_is_reported()
    {
        await using var factory = new AeroLinkApiFactory();
        var seeded = await SeedAsync(factory, [new("Discipline review", ProgramRole.SystemEngineeringLead)]);
        using var client = factory.CreateClient();
        await SignInAsync(client, Manager);

        var subjects = (await ReadAsync(client, seeded.ProjectId)).Artifacts.Select(x => x.Subject).ToList();
        Assert.Equal(
            ["System", "Software", "SystemTest", "HighLevelSoftwareTest", "LowLevelSoftwareTest"],
            subjects);
    }

    private sealed record ConfigurationResponse(Guid ProjectId, bool CanManage, ArtifactRow[] Artifacts);
    private sealed record ArtifactRow(string Subject, bool Configured, string? Name, int? Version, string? Mode,
        StageRow[]? Stages, int BlockingStages);
    private sealed record StageRow(int Position, string Name, string Kind, RequiredRow Required);
    private sealed record RequiredRow(string Role, bool Singular, string[] Holders, string[] Backups, bool Blocking);
}
