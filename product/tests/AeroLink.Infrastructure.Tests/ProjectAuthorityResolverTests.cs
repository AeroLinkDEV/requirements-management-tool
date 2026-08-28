using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

/// <summary>
/// The #816 authority model, at the one place that decides it.
///
/// The distinction these pin is the whole point of the slice: performing a job and holding the accountable
/// post of the same name are different facts, and only the second carries the post's authority. Before this
/// resolver existed the two were one call and one answer, so every gate that named a position accepted
/// anybody granted the role.
/// </summary>
public sealed class ProjectAuthorityResolverTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"aerolink-authority-{Guid.NewGuid():N}.db");
    private readonly AeroLinkDbContext _db;
    private readonly ProjectAuthorityResolver _resolver;
    private readonly ProgramRecord _program;
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;

    public ProjectAuthorityResolverTests()
    {
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>()
            .UseSqlite($"Data Source={_path};Pooling=False").Options;
        _db = new AeroLinkDbContext(options);
        _db.Database.OpenConnection();
        _db.Database.EnsureCreated();
        _program = new ProgramRecord("Authority Resolver", $"AR{Guid.NewGuid():N}"[..12]);
        _db.Add(_program);
        _db.SaveChanges();
        _resolver = new ProjectAuthorityResolver(_db);
    }

    private UserAccount Person(string name, params ProgramRole[] roles)
    {
        var account = new UserAccount($"res.{name}.{Guid.NewGuid():N}"[..40], name, $"{name}@example.test",
            IdentityService.HashPassword("StrongPass!2026"), _now);
        _db.Add(account);
        foreach (var role in roles)
            _db.ProgramMemberships.Add(new ProgramMembership(account.Id, _program.Id, role, "test", _now));
        _db.SaveChanges();
        return account;
    }

    private void Assign(UserAccount account, ProjectLeadershipPosition position)
    {
        _db.ProjectLeadershipAssignments.Add(
            new ProjectLeadershipAssignment(_program.Id, position, account.Id, "test", _now));
        _db.SaveChanges();
    }

    private void BackUp(UserAccount account, ProjectLeadershipPosition position)
    {
        _db.ProjectLeadershipBackups.Add(
            new ProjectLeadershipBackup(_program.Id, position, account.Id, "test", _now));
        _db.SaveChanges();
    }

    private Task<ProjectAuthorityDecision> ResolveAsync(UserAccount account, ProjectLeadershipPosition position) =>
        _resolver.ResolveAsync(account.Id, _program.Id, ProjectAuthorityRequirement.Leadership(position), _now);

    // ---- Base role is not the position -------------------------------------------------------------------

    /// <summary>
    /// The four roles the enum conflated with their posts. Holding the role is eligibility and nothing else;
    /// this is the assertion that would have failed for every consumer before the resolver existed.
    /// </summary>
    [Theory]
    [InlineData(ProgramRole.ProjectEngineer, ProjectLeadershipPosition.ProjectEngineer)]
    [InlineData(ProgramRole.ProgramManager, ProjectLeadershipPosition.ProgramManager)]
    [InlineData(ProgramRole.EngineeringManager, ProjectLeadershipPosition.EngineeringManager)]
    [InlineData(ProgramRole.ConfigurationManager, ProjectLeadershipPosition.ConfigurationManager)]
    public async Task The_base_role_alone_does_not_grant_the_identically_named_position(
        ProgramRole role, ProjectLeadershipPosition position)
    {
        var person = Person("base", role);
        var decision = await ResolveAsync(person, position);
        Assert.False(decision.Granted);
        Assert.Equal(ProjectAuthoritySource.None, decision.Source);
    }

    [Theory]
    [InlineData(ProgramRole.ProjectEngineer)]
    [InlineData(ProgramRole.ProgramManager)]
    [InlineData(ProgramRole.EngineeringManager)]
    [InlineData(ProgramRole.ConfigurationManager)]
    public async Task Several_people_may_hold_a_base_eligibility_role(ProgramRole role)
    {
        var first = Person("first", role);
        var second = Person("second", role);
        var third = Person("third", role);

        foreach (var person in new[] { first, second, third })
            Assert.True(await _resolver.IsSatisfiedAsync(
                person.Id, _program.Id, ProjectAuthorityRequirement.BaseRole(role), _now));
    }

    [Fact]
    public async Task A_raw_retired_position_membership_does_not_answer_the_positions_demands()
    {
        var person = Person("legacy-position", ProgramRole.SystemEngineeringLead);

        Assert.False(await _resolver.IsSatisfiedAsync(person.Id, _program.Id,
            ProjectAuthorityRequirement.LegacyRoleDemand(ProgramRole.SystemEngineeringLead), _now));
        Assert.False(await _resolver.IsSatisfiedAsync(person.Id, _program.Id,
            ProjectAuthorityRequirement.LegacyRoleDemand(ProgramRole.Reviewer), _now));
        Assert.False(await _resolver.IsSatisfiedAsync(person.Id, _program.Id,
            ProjectAuthorityRequirement.LegacyRoleDemand(ProgramRole.Approver), _now));
    }

    // ---- Primary and backup ------------------------------------------------------------------------------

    [Fact]
    public async Task The_primary_holds_the_position_and_the_provenance_says_so()
    {
        var person = Person("primary", ProgramRole.SystemEngineer);
        Assign(person, ProjectLeadershipPosition.SystemEngineeringLead);

        var decision = await ResolveAsync(person, ProjectLeadershipPosition.SystemEngineeringLead);
        Assert.True(decision.Granted);
        Assert.Equal(ProjectAuthoritySource.LeadershipPrimary, decision.Source);
        Assert.Equal(ProjectLeadershipPosition.SystemEngineeringLead, decision.Position);
    }

    /// <summary>The owner decided a standing backup carries the same live authority, not a contact field.</summary>
    [Fact]
    public async Task A_standing_backup_carries_the_same_authority_as_the_primary()
    {
        var backup = Person("backup", ProgramRole.SystemEngineer);
        BackUp(backup, ProjectLeadershipPosition.SystemEngineeringLead);

        var decision = await ResolveAsync(backup, ProjectLeadershipPosition.SystemEngineeringLead);
        Assert.True(decision.Granted);
        Assert.Equal(ProjectAuthoritySource.LeadershipBackup, decision.Source);
    }

    [Fact]
    public async Task Removing_the_backup_removes_the_authority_immediately()
    {
        var backup = Person("backup", ProgramRole.SystemEngineer);
        BackUp(backup, ProjectLeadershipPosition.SystemEngineeringLead);
        Assert.True((await ResolveAsync(backup, ProjectLeadershipPosition.SystemEngineeringLead)).Granted);

        var row = await _db.ProjectLeadershipBackups.SingleAsync(x => x.BackupUserId == backup.Id);
        row.Remove("test", _now);
        await _db.SaveChangesAsync();

        Assert.False((await ResolveAsync(backup, ProjectLeadershipPosition.SystemEngineeringLead)).Granted);
    }

    [Fact]
    public async Task Losing_the_base_eligibility_removes_the_leadership_authority_immediately()
    {
        var person = Person("primary", ProgramRole.SystemEngineer);
        Assign(person, ProjectLeadershipPosition.SystemEngineeringLead);
        Assert.True((await ResolveAsync(person, ProjectLeadershipPosition.SystemEngineeringLead)).Granted);

        var membership = await _db.ProgramMemberships.SingleAsync(
            x => x.UserId == person.Id && x.Role == ProgramRole.SystemEngineer);
        membership.End("test", _now);
        await _db.SaveChangesAsync();

        Assert.False((await ResolveAsync(person, ProjectLeadershipPosition.SystemEngineeringLead)).Granted);
    }

    [Fact]
    public async Task An_inactive_account_holds_no_authority()
    {
        var person = Person("primary", ProgramRole.SystemEngineer);
        Assign(person, ProjectLeadershipPosition.SystemEngineeringLead);
        person.Disable(_now);
        await _db.SaveChangesAsync();

        Assert.False((await ResolveAsync(person, ProjectLeadershipPosition.SystemEngineeringLead)).Granted);
        Assert.False((await _resolver.ResolveAnyLeadershipSatisfyingAsync(
            person.Id, _program.Id, ProgramRole.SystemEngineeringLead, default)).Granted);
    }

    [Fact]
    public async Task An_ended_program_membership_removes_the_authority()
    {
        var person = Person("primary", ProgramRole.SystemEngineer);
        Assign(person, ProjectLeadershipPosition.SystemEngineeringLead);

        foreach (var membership in await _db.ProgramMemberships.Where(x => x.UserId == person.Id).ToListAsync())
            membership.End("test", _now);
        await _db.SaveChangesAsync();

        Assert.False((await ResolveAsync(person, ProjectLeadershipPosition.SystemEngineeringLead)).Granted);
    }

    // ---- The cross-position eligibility leak (#824 P1 1) -------------------------------------------------

    /// <summary>
    /// The finding that started this correction.
    ///
    /// Somebody holding two positions used to be checked against the union of both positions' required base
    /// roles, so losing the eligibility for one was rescued by still holding the other. A System Engineering
    /// Lead who stopped being a System Engineer kept the lead's Reviewer authority because they were also
    /// the Configuration Manager. The Reviewer demand is answered by the lead position, not the Configuration
    /// Manager one, so the eligibility that matters is the lead's.
    /// </summary>
    [Fact]
    public async Task Eligibility_for_one_position_cannot_rescue_another_whose_eligibility_lapsed()
    {
        var person = Person("dual", ProgramRole.SystemEngineer, ProgramRole.ConfigurationManager);
        Assign(person, ProjectLeadershipPosition.SystemEngineeringLead);
        Assign(person, ProjectLeadershipPosition.ConfigurationManager);

        Assert.True(await _resolver.IsSatisfiedAsync(
            person.Id, _program.Id, ProjectAuthorityRequirement.LegacyRoleDemand(ProgramRole.Reviewer), _now));

        // They stop being a System Engineer, but remain the Configuration Manager.
        var systemEngineer = await _db.ProgramMemberships.SingleAsync(
            x => x.UserId == person.Id && x.Role == ProgramRole.SystemEngineer);
        systemEngineer.End("test", _now);
        await _db.SaveChangesAsync();

        Assert.False((await ResolveAsync(person, ProjectLeadershipPosition.SystemEngineeringLead)).Granted);
        // Configuration Manager does not answer Reviewer, so no other position rescues it either.
        Assert.False(await _resolver.IsSatisfiedAsync(
            person.Id, _program.Id, ProjectAuthorityRequirement.LegacyRoleDemand(ProgramRole.Reviewer), _now));
        // The position they are still eligible for is untouched.
        Assert.True((await ResolveAsync(person, ProjectLeadershipPosition.ConfigurationManager)).Granted);
    }

    // ---- Replacement -------------------------------------------------------------------------------------

    /// <summary>
    /// The flow the owner asked for: the replacement becomes eligible while the incumbent is still in post,
    /// then the position moves. Membership singularity used to make the first step impossible.
    /// </summary>
    [Fact]
    public async Task A_replacement_can_be_made_eligible_while_the_incumbent_still_holds_the_post()
    {
        var incumbent = Person("incumbent", ProgramRole.ProgramManager);
        Assign(incumbent, ProjectLeadershipPosition.ProgramManager);
        var successor = Person("successor", ProgramRole.ProgramManager);

        Assert.True((await ResolveAsync(incumbent, ProjectLeadershipPosition.ProgramManager)).Granted);
        Assert.False((await ResolveAsync(successor, ProjectLeadershipPosition.ProgramManager)).Granted);

        var assignment = await _db.ProjectLeadershipAssignments.SingleAsync(
            x => x.HolderUserId == incumbent.Id && x.EndedAt == null);
        assignment.End("test", _now);
        Assign(successor, ProjectLeadershipPosition.ProgramManager);

        Assert.False((await ResolveAsync(incumbent, ProjectLeadershipPosition.ProgramManager)).Granted);
        Assert.True((await ResolveAsync(successor, ProjectLeadershipPosition.ProgramManager)).Granted);
    }

    // ---- Holder projection agrees with the per-person answer ---------------------------------------------

    /// <summary>
    /// The candidate picker reads <c>ResolveHoldersAsync</c> and the signing gate reads <c>ResolveAsync</c>.
    /// If they disagree the UI offers somebody who is then refused, or hides somebody who could have signed.
    /// </summary>
    [Fact]
    public async Task The_holder_projection_agrees_with_the_per_person_decision()
    {
        var primary = Person("primary", ProgramRole.SystemEngineer);
        var backup = Person("backup", ProgramRole.SystemEngineer);
        var eligibleOnly = Person("eligible", ProgramRole.SystemEngineer);
        var unrelated = Person("unrelated", ProgramRole.SoftwareQualityAnalyst);
        Assign(primary, ProjectLeadershipPosition.SystemEngineeringLead);
        BackUp(backup, ProjectLeadershipPosition.SystemEngineeringLead);

        var holders = await _resolver.ResolveHoldersAsync(_program.Id, ProgramRole.SystemEngineeringLead, _now);
        var holderIds = holders.Select(x => x.UserId).ToHashSet();

        Assert.Contains(primary.Id, holderIds);
        Assert.Contains(backup.Id, holderIds);
        Assert.DoesNotContain(eligibleOnly.Id, holderIds);
        Assert.DoesNotContain(unrelated.Id, holderIds);

        foreach (var person in new[] { primary, backup, eligibleOnly, unrelated })
            Assert.Equal(
                holderIds.Contains(person.Id),
                await _resolver.IsSatisfiedAsync(person.Id, _program.Id,
                    ProjectAuthorityRequirement.LegacyRoleDemand(ProgramRole.SystemEngineeringLead), _now));
    }

    [Fact]
    public async Task The_holder_projection_includes_the_active_global_administrator_without_membership()
    {
        var administrator = new UserAccount(IdentityService.SystemAdministratorUserName, "Global Administrator",
            "global.admin@example.test", IdentityService.HashPassword("StrongPass!2026"), _now);
        _db.Add(administrator);
        await _db.SaveChangesAsync();

        Assert.False(await _db.ProgramMemberships.AnyAsync(x => x.UserId == administrator.Id));

        var holders = await _resolver.ResolveHoldersAsync(
            _program.Id, ProgramRole.SystemEngineeringLead, _now);
        var projected = Assert.Single(holders, x => x.UserId == administrator.Id);
        Assert.Equal(ProjectAuthoritySource.AdministratorSubstitution, projected.Source);
        Assert.True(await _resolver.IsSatisfiedAsync(administrator.Id, _program.Id,
            ProjectAuthorityRequirement.LegacyRoleDemand(ProgramRole.SystemEngineeringLead), _now));
    }

    public void Dispose()
    {
        _db.Database.CloseConnection();
        _db.Dispose();
        try { File.Delete(_path); } catch { /* temp cleanup best effort */ }
    }
}
