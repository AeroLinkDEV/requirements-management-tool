using AeroLink.Domain.Identity;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Api;

/// <summary>
/// One authoritative policy for accountable Project-document assignments. Global administration is
/// deliberately not authoring authority: an assignee must be an active person with current Program
/// engineering membership or an active Engineer delegation.
/// </summary>
internal static class ManagedDocumentAssignmentPolicy
{
    public const string DirectoryAuthority = "ManagedDocumentAuthor";

    public static async Task<bool> IsEligibleAsync(AeroLinkDbContext db, IdentityService identity, Guid projectId,
        string? userName, DateTimeOffset now, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(userName)) return false;
        var programId = await db.Projects.AsNoTracking().Where(x => x.Id == projectId)
            .Select(x => (Guid?)x.ProgramId).SingleOrDefaultAsync(ct);
        if (programId is null) return false;
        var account = await db.UserAccounts.AsNoTracking().SingleOrDefaultAsync(
            x => x.UserName == userName.Trim().ToLower() && x.State == AccountState.Active, ct);
        if (account is null || account.UserName == IdentityService.SystemAdministratorUserName) return false;
        var hasMembership = await db.ProgramMemberships.AsNoTracking().AnyAsync(
            x => x.UserId == account.Id && x.ProgramId == programId && x.EndedAt == null, ct);
        var delegations = await db.RoleDelegations.AsNoTracking().Where(
            x => x.ProgramId == programId && x.DelegateUserId == account.Id && x.Role == ProgramRole.Engineer && x.RevokedAt == null).ToListAsync(ct);
        return (hasMembership || delegations.Any(x => x.StartsAt <= now && x.EndsAt > now))
            && await identity.HasRoleAsync(account.Id, programId.Value, ProgramRole.Engineer, now, ct);
    }

    public static async Task<HashSet<string>> EligibleUserNamesAsync(AeroLinkDbContext db, IdentityService identity,
        Guid projectId, DateTimeOffset now, CancellationToken ct)
    {
        var names = await db.UserAccounts.AsNoTracking().Where(x => x.State == AccountState.Active)
            .Select(x => x.UserName).ToListAsync(ct);
        var eligible = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
            if (await IsEligibleAsync(db, identity, projectId, name, now, ct)) eligible.Add(name);
        return eligible;
    }
}
