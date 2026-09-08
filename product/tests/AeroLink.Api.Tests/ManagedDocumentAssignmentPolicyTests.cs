using AeroLink.Api;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Api.Tests;

public sealed class ManagedDocumentAssignmentPolicyTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"aerolink-managed-document-assignment-{Guid.NewGuid():N}.db");
    private readonly AeroLinkDbContext _db;
    private ProgramRecord _program = null!;
    private ProjectRecord _project = null!;
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;

    public ManagedDocumentAssignmentPolicyTests()
    {
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>()
            .UseSqlite($"Data Source={_path};Pooling=False").Options;
        _db = new AeroLinkDbContext(options);
        _db.Database.OpenConnection();
    }

    public async Task InitializeAsync()
    {
        await _db.Database.EnsureCreatedAsync();
        _program = new ProgramRecord("Managed document assignment authority", $"MDA{Guid.NewGuid():N}"[..12]);
        _project = new ProjectRecord(_program.Id, "Flight Software", "Assignment authority");
        _db.AddRange(_program, _project);
        await _db.SaveChangesAsync();
    }

    private async Task<UserAccount> PersonAsync(string name, ProgramRole role)
    {
        var account = new UserAccount($"mda.assignment.{name}.{Guid.NewGuid():N}"[..40], name,
            $"{name}@example.test", IdentityService.HashPassword("StrongPass!2026"), _now);
        _db.Add(account);
        _db.ProgramMemberships.Add(new ProgramMembership(account.Id, _program.Id, role, "test", _now));
        await _db.SaveChangesAsync();
        return account;
    }

    private static AuthenticatedUser Actor(UserAccount account) =>
        new(account.Id, account.UserName, account.DisplayName, account.Email, false, []);

    [Fact]
    public async Task Project_engineer_position_holder_is_accepted_and_base_only_configuration_manager_is_refused()
    {
        var projectEngineer = await PersonAsync("project-engineer", ProgramRole.ProjectEngineer);
        var baseOnlyConfigurationManager = await PersonAsync("base-cm", ProgramRole.ConfigurationManager);
        _db.ProjectLeadershipAssignments.Add(new ProjectLeadershipAssignment(
            _program.Id, ProjectLeadershipPosition.ProjectEngineer, projectEngineer.Id, "test", _now));
        await _db.SaveChangesAsync();

        Assert.True(await ManagedDocumentAssignmentPolicy.HasExplicitAuthorityAsync(
            _db, _project.Id, Actor(projectEngineer), _now, default, ProgramRole.ProjectEngineeringLead));
        Assert.False(await ManagedDocumentAssignmentPolicy.HasExplicitAuthorityAsync(
            _db, _project.Id, Actor(baseOnlyConfigurationManager), _now, default, ProgramRole.ConfigurationManager));
    }

    public Task DisposeAsync()
    {
        _db.Database.CloseConnection();
        _db.Dispose();
        try { File.Delete(_path); } catch { /* temp cleanup best effort */ }
        return Task.CompletedTask;
    }
}
