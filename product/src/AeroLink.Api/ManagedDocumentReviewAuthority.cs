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

    /// <summary>
    /// Whether a role in the accepted sets names a Project Leadership position rather than a job.
    ///
    /// The two groups are already distinguished in the domain: the retired position roles are the singular
    /// ones, and the four that became eligibility requirements are the base-eligibility ones. Everything
    /// else here — Reviewer, Approver, SoftwareQualityAnalyst — is still a job somebody performs, and
    /// membership rightly answers for it until Slice 4 retires the workflow-stage pair.
    /// </summary>
    private static bool IsPositionGoverned(ProgramRole role) =>
        SingularProgramRoles.IsSingular(role) || SingularProgramRoles.IsBaseEligibility(role);

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
        // The position roles, each resolved against its own eligibility and its own active designation, so a
        // primary or standing backup signs and nobody else does. Leadership is checked before a general job
        // membership so frozen evidence records the strongest controlled fact: a Configuration Manager
        // position holder who also holds Approver must not degrade to Approver/DirectMembership.
        var resolver = new ProjectAuthorityResolver(db);
        foreach (var role in accepted.Where(IsPositionGoverned))
        {
            var decision = await resolver.ResolveAnyLeadershipSatisfyingAsync(account.Id, programId, role, ct);
            if (!decision.Granted) continue;
            return new(required, role,
                decision.Source == ProjectAuthoritySource.LeadershipBackup ? "ProjectLeadershipBackup" : "ProjectLeadershipPrimary",
                decision.SourceId);
        }

        // Only the roles that still describe a job are answerable by membership. The rest of this list names
        // positions — ProgramManager, ConfigurationManager, the discipline leads — and under #816 holding
        // that role is eligibility for the position, not the position itself. Reading them from membership
        // here is what let a base-role-only member sign a technical review.
        foreach (var role in accepted.Where(x => !IsPositionGoverned(x)))
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
        // Legacy role-keyed backups keep answering the roles that are still jobs. A legacy backup of a
        // *position* must not: the migration moves those to ProjectLeadershipBackup, and honouring the old
        // row as well would leave a replaced backup signing after the API reported them removed.
        foreach (var role in accepted.Where(x => !IsPositionGoverned(x)))
        {
            var backup = backups.FirstOrDefault(x => x.Role == role);
            if (backup is not null && direct.Count > 0) return new(required, role, "StandingBackup", backup.Id);
        }
        return null;
    }
}
