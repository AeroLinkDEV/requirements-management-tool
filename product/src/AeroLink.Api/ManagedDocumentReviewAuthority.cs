using AeroLink.Domain.Identity;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Api;

internal sealed record ManagedDocumentAuthorityEvidence(
    string RequiredAuthority, ProgramRole GrantedAuthority, string Source, Guid? SourceId);

internal static class ManagedDocumentReviewAuthority
{
    public static readonly Guid PolicyId = Guid.Parse("89d7b639-96f1-4fd4-970a-8a0db066c493");
    public const string PolicyName = "AeroLink project document review";
    public const int PolicyVersion = 1;
    public const string FrozenPolicy = "FrozenAtAssignment;ActiveAccountAtSigning";

    private static readonly ProgramRole[] Technical =
    [
        ProgramRole.Reviewer, ProgramRole.Approver, ProgramRole.SystemEngineeringLead,
        ProgramRole.SoftwareEngineeringLead, ProgramRole.ProjectEngineeringLead, ProgramRole.EngineeringManager
    ];
    private static readonly ProgramRole[] Final =
    [
        ProgramRole.SoftwareQualityAnalyst, ProgramRole.ConfigurationManager,
        ProgramRole.Approver, ProgramRole.ProgramManager
    ];

    public static Task<ManagedDocumentAuthorityEvidence?> ResolveTechnicalAsync(AeroLinkDbContext db,
        Guid programId, UserAccount account, DateTimeOffset now, CancellationToken ct) =>
        ResolveAsync(db, programId, account, "TechnicalDocumentReview", Technical, now, ct);

    public static Task<ManagedDocumentAuthorityEvidence?> ResolveFinalAsync(AeroLinkDbContext db,
        Guid programId, UserAccount account, DateTimeOffset now, CancellationToken ct) =>
        ResolveAsync(db, programId, account, "DocumentReleaseAuthorization", Final, now, ct);

    private static async Task<ManagedDocumentAuthorityEvidence?> ResolveAsync(AeroLinkDbContext db,
        Guid programId, UserAccount account, string required, IReadOnlyList<ProgramRole> accepted,
        DateTimeOffset now, CancellationToken ct)
    {
        if (account.State != AccountState.Active) return null;
        if (account.UserName == IdentityService.SystemAdministratorUserName)
            return new(required, ProgramRole.Administrator, "AdministratorSubstitution", null);

        var direct = await db.ProgramMemberships.AsNoTracking()
            .Where(x => x.ProgramId == programId && x.UserId == account.Id && x.EndedAt == null)
            .ToListAsync(ct);
        foreach (var role in accepted)
        {
            var membership = direct.FirstOrDefault(x => x.Role == role);
            if (membership is not null) return new(required, role, "DirectMembership", membership.Id);
        }
        var administrator = direct.FirstOrDefault(x => x.Role == ProgramRole.Administrator);
        if (administrator is not null)
            return new(required, ProgramRole.Administrator, "AdministratorSubstitution", administrator.Id);

        var delegations = await db.RoleDelegations.AsNoTracking()
            .Where(x => x.ProgramId == programId && x.DelegateUserId == account.Id && x.RevokedAt == null)
            .ToListAsync(ct);
        foreach (var role in accepted)
        {
            var delegation = delegations.FirstOrDefault(x => x.Role == role && x.StartsAt <= now && x.EndsAt > now);
            if (delegation is not null) return new(required, role, "ActiveDelegation", delegation.Id);
        }
        var adminDelegation = delegations.FirstOrDefault(x => x.Role == ProgramRole.Administrator && x.StartsAt <= now && x.EndsAt > now);
        if (adminDelegation is not null) return new(required, ProgramRole.Administrator, "AdministratorSubstitution", adminDelegation.Id);

        var backups = await db.ProjectRoleBackups.AsNoTracking()
            .Where(x => x.ProgramId == programId && x.BackupUserId == account.Id && x.RemovedAt == null)
            .ToListAsync(ct);
        foreach (var role in accepted)
        {
            var backup = backups.FirstOrDefault(x => x.Role == role);
            if (backup is not null && direct.Count > 0) return new(required, role, "StandingBackup", backup.Id);
        }
        return null;
    }
}
