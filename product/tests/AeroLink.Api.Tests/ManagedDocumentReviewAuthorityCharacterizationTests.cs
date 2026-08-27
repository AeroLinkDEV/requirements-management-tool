using AeroLink.Api;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Api.Tests;

/// <summary>
/// Characterizes the managed-document review/release authority resolver exactly as #816 Slice 2 found
/// it — this resolver is deliberately separate from IdentityService and has its own accepted-role sets
/// and its own resolution order, so the Project Leadership migration has to move it explicitly rather
/// than inherit whatever IdentityService becomes.
///
/// Pinned here: the accepted sets themselves (Technical vs Final — note ProjectEngineeringLead is
/// accepted for Technical review but NOT for Final release authorization, and ProjectEngineer is in
/// neither), and the precedence direct membership → program-administrator membership → active delegation
/// → standing backup (which additionally requires a current membership to mean anything).
/// </summary>
public sealed class ManagedDocumentReviewAuthorityCharacterizationTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"aerolink-mdauth-{Guid.NewGuid():N}.db");
    private readonly AeroLinkDbContext _db;
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;

    private readonly ProgramRecord _program;
    private readonly Dictionary<string, UserAccount> _accounts = new();

    public ManagedDocumentReviewAuthorityCharacterizationTests()
    {
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={_path};Pooling=False").Options;
        _db = new AeroLinkDbContext(options);
        _db.Database.OpenConnection();
        _db.Database.EnsureCreated();
        _program = new ProgramRecord("Document Authority Characterization", $"DA{Guid.NewGuid():N}"[..12]);
        _db.Add(_program);
        _db.SaveChanges();
    }

    private UserAccount AddAccount(string key, params ProgramRole[] roles)
    {
        var account = new UserAccount($"mda.{key}", $"Characterization {key}", $"{key}@example.test",
            IdentityService.HashPassword("StrongPass!2026"), _now);
        _db.Add(account);
        foreach (var role in roles)
            _db.ProgramMemberships.Add(new ProgramMembership(account.Id, _program.Id, role, "admin", _now));
        _accounts[key] = account;
        return account;
    }

    [Fact]
    public async Task The_technical_set_accepts_the_engineering_leadership_and_control_roles()
    {
        AddAccount("pel", ProgramRole.ProjectEngineeringLead);
        AddAccount("sel", ProgramRole.SystemEngineeringLead);
        AddAccount("engmgr", ProgramRole.EngineeringManager);
        AddAccount("reviewer", ProgramRole.Reviewer);
        await _db.SaveChangesAsync();

        foreach (var key in new[] { "pel", "sel", "engmgr", "reviewer" })
        {
            var evidence = await ManagedDocumentReviewAuthority.ResolveTechnicalAsync(_db, _program.Id, _accounts[key], _now, default);
            Assert.NotNull(evidence);
            Assert.Equal("DirectMembership", evidence!.Source);
        }
    }

    [Fact]
    public async Task The_technical_set_rejects_non_review_authorities()
    {
        AddAccount("sqa", ProgramRole.SoftwareQualityAnalyst);
        AddAccount("airworthiness", ProgramRole.Airworthiness);
        AddAccount("testengineer", ProgramRole.SystemTestEngineer);
        AddAccount("plainmember", ProgramRole.Engineer);
        await _db.SaveChangesAsync();

        foreach (var key in new[] { "sqa", "airworthiness", "testengineer", "plainmember" })
        {
            var evidence = await ManagedDocumentReviewAuthority.ResolveTechnicalAsync(_db, _program.Id, _accounts[key], _now, default);
            Assert.Null(evidence);
        }
    }

    /// <summary>
    /// The Final (release-authorization) set is deliberately narrower on the engineering side: the
    /// retiring ProjectEngineeringLead cannot release a document, and neither can a discipline lead.
    /// Release authorization belongs to configuration/program leadership and independent assurance.
    /// </summary>
    [Fact]
    public async Task The_final_set_excludes_the_engineering_leadership_roles()
    {
        AddAccount("pel", ProgramRole.ProjectEngineeringLead);
        AddAccount("sel", ProgramRole.SystemEngineeringLead);
        AddAccount("engmgr", ProgramRole.EngineeringManager);
        AddAccount("cm", ProgramRole.ConfigurationManager);
        AddAccount("pm", ProgramRole.ProgramManager);
        AddAccount("sqa", ProgramRole.SoftwareQualityAnalyst);
        AddAccount("approver", ProgramRole.Approver);
        await _db.SaveChangesAsync();

        foreach (var key in new[] { "cm", "pm", "sqa", "approver" })
        {
            var evidence = await ManagedDocumentReviewAuthority.ResolveFinalAsync(_db, _program.Id, _accounts[key], _now, default);
            Assert.NotNull(evidence);
        }
        foreach (var key in new[] { "pel", "sel", "engmgr" })
        {
            var evidence = await ManagedDocumentReviewAuthority.ResolveFinalAsync(_db, _program.Id, _accounts[key], _now, default);
            Assert.Null(evidence);
        }
    }

    [Fact]
    public async Task A_program_administrator_membership_substitutes_when_no_accepted_role_matches()
    {
        AddAccount("program.admin", ProgramRole.Administrator);
        await _db.SaveChangesAsync();

        var evidence = await ManagedDocumentReviewAuthority.ResolveTechnicalAsync(_db, _program.Id, _accounts["program.admin"], _now, default);
        Assert.NotNull(evidence);
        Assert.Equal(ProgramRole.Administrator, evidence!.GrantedAuthority);
        Assert.Equal("AdministratorSubstitution", evidence.Source);
    }

    [Fact]
    public async Task A_delegation_of_an_accepted_role_is_honored_within_its_interval()
    {
        var delegator = AddAccount("cm", ProgramRole.ConfigurationManager);
        var delegatee = AddAccount("delegated", ProgramRole.SystemEngineer);
        _db.RoleDelegations.Add(new RoleDelegation(_program.Id, delegator.Id, delegatee.Id,
            ProgramRole.ConfigurationManager, _now.AddMinutes(-1), _now.AddDays(1), "CM away.", "admin", _now));
        await _db.SaveChangesAsync();

        var evidence = await ManagedDocumentReviewAuthority.ResolveFinalAsync(_db, _program.Id, _accounts["delegated"], _now, default);
        Assert.NotNull(evidence);
        Assert.Equal("ActiveDelegation", evidence!.Source);

        var afterExpiry = _now.AddDays(2);
        Assert.Null(await ManagedDocumentReviewAuthority.ResolveFinalAsync(_db, _program.Id, _accounts["delegated"], afterExpiry, default));
    }

    /// <summary>
    /// A standing backup resolves only while the backup is still a current member of the program AND the
    /// backup designation itself is still active. The resolver re-checks both rather than trusting the
    /// backup row alone — the fail-closed rule the Project Leadership model must carry forward for all
    /// eight positions. (The database enforces one ACTIVE backup per program+role through a partial
    /// unique index; a removed designation is retained with its removal attribution and is inert, and it
    /// frees the slot for a new backup.)
    /// </summary>
    [Fact]
    public async Task A_standing_backup_requires_a_current_membership_and_an_active_designation()
    {
        // The member backup holds an unrelated current membership — the resolver accepts any current
        // membership as the fail-closed membership proof, not only one of the accepted roles. The
        // departed backup holds no membership at all, so their (different-role) backup row is inert.
        var memberBackup = AddAccount("backup.member");
        var departedBackup = AddAccount("backup.departed");
        _db.AddRange(
            new ProjectRoleBackup(_program.Id, ProgramRole.Approver, memberBackup.Id, "admin", _now),
            new ProjectRoleBackup(_program.Id, ProgramRole.SoftwareEngineeringLead, departedBackup.Id, "admin", _now),
            new ProgramMembership(memberBackup.Id, _program.Id, ProgramRole.SoftwareEngineer, "admin", _now));
        await _db.SaveChangesAsync();

        var memberEvidence = await ManagedDocumentReviewAuthority.ResolveTechnicalAsync(_db, _program.Id, _accounts["backup.member"], _now, default);
        Assert.NotNull(memberEvidence);
        Assert.Equal("StandingBackup", memberEvidence!.Source);

        var departedEvidence = await ManagedDocumentReviewAuthority.ResolveTechnicalAsync(_db, _program.Id, _accounts["backup.departed"], _now, default);
        Assert.Null(departedEvidence);

        // A designation that is formally removed stops conferring authority even though the row remains
        // as history, and the partial unique index frees the program+role slot for a new backup.
        var removedBackup = AddAccount("backup.removed");
        var removedRow = new ProjectRoleBackup(_program.Id, ProgramRole.ProjectEngineeringLead, removedBackup.Id, "admin", _now);
        _db.Add(new ProgramMembership(removedBackup.Id, _program.Id, ProgramRole.SystemEngineer, "admin", _now));
        _db.Add(removedRow); await _db.SaveChangesAsync();
        Assert.NotNull(await ManagedDocumentReviewAuthority.ResolveTechnicalAsync(_db, _program.Id, _accounts["backup.removed"], _now, default));
        removedRow.Remove("admin", _now);
        await _db.SaveChangesAsync();
        Assert.Null(await ManagedDocumentReviewAuthority.ResolveTechnicalAsync(_db, _program.Id, _accounts["backup.removed"], _now, default));

        var replacement = AddAccount("backup.replacement");
        _db.Add(new ProjectRoleBackup(_program.Id, ProgramRole.ProjectEngineeringLead, replacement.Id, "admin", _now));
        _db.Add(new ProgramMembership(replacement.Id, _program.Id, ProgramRole.SystemEngineer, "admin", _now));
        await _db.SaveChangesAsync();
        Assert.NotNull(await ManagedDocumentReviewAuthority.ResolveTechnicalAsync(_db, _program.Id, _accounts["backup.replacement"], _now, default));
    }

    public void Dispose()
    {
        _db.Database.CloseConnection();
        _db.Dispose();
        try { File.Delete(_path); } catch { /* temp cleanup best effort */ }
    }
}
