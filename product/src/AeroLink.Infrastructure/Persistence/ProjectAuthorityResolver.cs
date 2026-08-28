using AeroLink.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>
/// The one place that answers "may this person act here", and says where the answer came from.
///
/// Slice 2 introduced the Project Leadership model but left every consumer to re-derive authority from
/// whatever tables it happened to know about — <c>ManagedDocumentReviewAuthority</c> read memberships,
/// delegations and legacy role backups; the workflow candidate picker read a third combination; Problem
/// Report recovery read raw membership roles. Each re-implementation was a chance to disagree, and they did:
/// a newly assigned System Engineering Lead was offered by one and refused by another.
///
/// This is deliberately not an authorization framework. It is one method with a typed question, so that a
/// caller can no longer accidentally accept base membership where the position was meant, and so the answer
/// carries its provenance instead of leaving each caller to guess.
/// </summary>
public sealed class ProjectAuthorityResolver(AeroLinkDbContext db)
{
    /// <summary>
    /// Resolve one authority question for one person on one program.
    ///
    /// Order is precedence, and precedence is deliberate: the strongest true statement about why somebody
    /// may act is the one worth recording. A leadership primary who also holds the base role is recorded as
    /// the primary, because that is what an audit needs to see.
    /// </summary>
    public async Task<ProjectAuthorityDecision> ResolveAsync(
        Guid userId, Guid programId, ProjectAuthorityRequirement requirement, DateTimeOffset now, CancellationToken ct = default)
    {
        var account = await db.UserAccounts.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == userId && x.State == AccountState.Active, ct);
        if (account is null) return ProjectAuthorityDecision.Denied;
        if (account.UserName == IdentityService.SystemAdministratorUserName)
            return ProjectAuthorityDecision.From(ProjectAuthoritySource.AdministratorSubstitution);
        if (requirement.AllowProgramAdministratorSubstitution
            && await db.ProgramMemberships.AsNoTracking().AnyAsync(
                x => x.UserId == userId && x.ProgramId == programId && x.EndedAt == null
                     && x.Role == ProgramRole.Administrator, ct))
            return ProjectAuthorityDecision.From(ProjectAuthoritySource.AdministratorSubstitution);

        return requirement.Kind switch
        {
            ProjectAuthorityKind.BaseRole => await ResolveBaseRoleAsync(userId, programId, requirement.Role!.Value, now, ct),
            ProjectAuthorityKind.LeadershipPosition => await ResolveLeadershipAsync(userId, programId, requirement.Position!.Value, ct),
            _ => await ResolveLegacyDemandAsync(userId, programId, requirement.Role!.Value, now, ct),
        };
    }

    public async Task<bool> IsSatisfiedAsync(
        Guid userId, Guid programId, ProjectAuthorityRequirement requirement, DateTimeOffset now, CancellationToken ct = default)
        => (await ResolveAsync(userId, programId, requirement, now, ct)).Granted;

    /// <summary>
    /// The roles a membership may answer for a base-role question.
    ///
    /// <c>ProgramRoleAuthority.Satisfying</c> still folds the retired position roles into Reviewer, Approver
    /// and Engineer, because a stored workflow stage naming one has to keep resolving. But a *membership*
    /// carrying <c>SystemEngineeringLead</c> must not answer those demands — that is the conflation #816
    /// exists to remove, and honouring it would let a roster grant hand out a lead's review authority with no
    /// assignment anywhere. The four base eligibility roles are still jobs, though: a Project Engineer or
    /// Engineering Manager is still an Engineer for ordinary authoring gates. Positions are answered by the
    /// leadership pass instead.
    /// </summary>
    private static IReadOnlyList<ProgramRole> BaseRoleMembershipAnswerable(ProgramRole demanded) =>
        [.. ProgramRoleAuthority.Satisfying(demanded).Where(x => !SingularProgramRoles.IsSingular(x))];

    /// <summary>
    /// A legacy demand naming a governed role meant the position, not the eligibility membership. For an
    /// ordinary job demand it keeps the normal satisfying-role implications.
    /// </summary>
    private static IReadOnlyList<ProgramRole> LegacyDemandMembershipAnswerable(ProgramRole demanded) =>
        SingularProgramRoles.IsPositionGoverned(demanded) ? [] : BaseRoleMembershipAnswerable(demanded);

    /// <summary>What work somebody performs. Membership answers it; elevation is beside the point.</summary>
    private async Task<ProjectAuthorityDecision> ResolveBaseRoleAsync(
        Guid userId, Guid programId, ProgramRole role, DateTimeOffset now, CancellationToken ct)
    {
        // A base-role question is asked about the job. The four eligibility roles therefore remain ordinary
        // work roles here even though they do not answer the identically named Project Leadership position.
        var accepted = BaseRoleMembershipAnswerable(role);
        if (await db.ProgramMemberships.AsNoTracking().AnyAsync(
                x => x.UserId == userId && x.ProgramId == programId && x.EndedAt == null && accepted.Contains(x.Role), ct))
            return ProjectAuthorityDecision.From(ProjectAuthoritySource.DirectBaseRole);
        return await ResolveDelegationAsync(userId, programId, role, now, ct);
    }

    /// <summary>
    /// Whether somebody holds the position — as primary, or as the standing backup that the owner decided
    /// carries the same live authority.
    ///
    /// Every axis fails closed and every axis is checked against *this* position. Eligibility borrowed from
    /// another position the person happens to hold is the bug this method exists to prevent.
    /// </summary>
    private async Task<ProjectAuthorityDecision> ResolveLeadershipAsync(
        Guid userId, Guid programId, ProjectLeadershipPosition position, CancellationToken ct)
    {
        var requiredBaseRole = ProjectLeadership.RequiredBaseRole(position);
        var eligible = await db.ProgramMemberships.AsNoTracking().AnyAsync(
            x => x.UserId == userId && x.ProgramId == programId && x.EndedAt == null && x.Role == requiredBaseRole, ct);
        if (!eligible) return ProjectAuthorityDecision.Denied;

        var assignmentId = await db.ProjectLeadershipAssignments.AsNoTracking()
            .Where(x => x.ProgramId == programId && x.Position == position && x.HolderUserId == userId && x.EndedAt == null)
            .Select(x => (Guid?)x.Id).FirstOrDefaultAsync(ct);
        if (assignmentId is not null)
            return ProjectAuthorityDecision.From(ProjectAuthoritySource.LeadershipPrimary, position, assignmentId);

        var backupId = await db.ProjectLeadershipBackups.AsNoTracking()
            .Where(x => x.ProgramId == programId && x.Position == position && x.BackupUserId == userId && x.RemovedAt == null)
            .Select(x => (Guid?)x.Id).FirstOrDefaultAsync(ct);
        if (backupId is not null)
            return ProjectAuthorityDecision.From(ProjectAuthoritySource.LeadershipBackup, position, backupId);

        return ProjectAuthorityDecision.Denied;
    }

    /// <summary>
    /// A role-shaped demand from before the split. It cannot say whether it meant the job or the position,
    /// so both answer it — but the leadership half is resolved position by position, each validated on its
    /// own eligibility.
    /// </summary>
    private async Task<ProjectAuthorityDecision> ResolveLegacyDemandAsync(
        Guid userId, Guid programId, ProgramRole role, DateTimeOffset now, CancellationToken ct)
    {
        var accepted = LegacyDemandMembershipAnswerable(role);
        if (await db.ProgramMemberships.AsNoTracking().AnyAsync(
                x => x.UserId == userId && x.ProgramId == programId && x.EndedAt == null && accepted.Contains(x.Role), ct))
            return ProjectAuthorityDecision.From(ProjectAuthoritySource.DirectBaseRole);

        var leadership = await ResolveAnyLeadershipSatisfyingAsync(userId, programId, role, ct);
        if (leadership.Granted) return leadership;

        var legacyBackup = await ResolveLegacyBackupAsync(userId, programId, accepted, ct);
        if (legacyBackup.Granted) return legacyBackup;

        return await ResolveDelegationAsync(userId, programId, role, now, ct);
    }

    /// <summary>
    /// The positions that actually answer this demand, each validated independently.
    ///
    /// The original implementation collected every position the person held, asked whether *any* of them
    /// answered the demand, and then tested eligibility against the *union* of their required base roles.
    /// Somebody holding System Engineering Lead and Configuration Manager therefore kept the lead's
    /// Reviewer authority after losing <c>SystemEngineer</c>, rescued by the Configuration Manager
    /// eligibility they still had. Filtering to the matching positions first is the whole fix.
    /// </summary>
    public async Task<ProjectAuthorityDecision> ResolveAnyLeadershipSatisfyingAsync(
        Guid userId, Guid programId, ProgramRole demanded, CancellationToken ct)
    {
        // This helper is public because a few policy services need leadership-only provenance. They do not
        // all enter through ResolveAsync, so the active-account gate has to live here too; otherwise a
        // disabled holder can retain authority through this sibling while the main resolver refuses it.
        if (!await db.UserAccounts.AsNoTracking()
                .AnyAsync(x => x.Id == userId && x.State == AccountState.Active, ct))
            return ProjectAuthorityDecision.Denied;

        var accepted = ProgramRoleAuthority.Satisfying(demanded);
        var matching = ProjectLeadership.All
            .Where(position =>
            {
                var demands = ProjectLeadership.SatisfyingDemands(position);
                return demands.Contains(demanded) || accepted.Any(demands.Contains);
            })
            .ToList();
        if (matching.Count == 0) return ProjectAuthorityDecision.Denied;

        foreach (var position in matching)
        {
            var decision = await ResolveLeadershipAsync(userId, programId, position, ct);
            if (decision.Granted) return decision;
        }
        return ProjectAuthorityDecision.Denied;
    }

    /// <summary>
    /// A legacy role-keyed backup, for the roles that are still jobs.
    ///
    /// Read here as well as in <see cref="ResolveHoldersAsync"/> so the per-person answer and the projection
    /// cannot disagree — a picker that offers somebody the signing gate then refuses is the exact failure
    /// this resolver exists to remove. Position roles are excluded: their designation lives on
    /// <c>ProjectLeadershipBackup</c>.
    /// </summary>
    private async Task<ProjectAuthorityDecision> ResolveLegacyBackupAsync(
        Guid userId, Guid programId, IReadOnlyList<ProgramRole> accepted, CancellationToken ct)
    {
        var backed = await db.ProjectRoleBackups.AsNoTracking()
            .Where(x => x.ProgramId == programId && x.BackupUserId == userId && x.RemovedAt == null)
            .Select(x => x.Role).ToListAsync(ct);
        if (!backed.Any(x => !SingularProgramRoles.IsPositionGoverned(x) && accepted.Contains(x)))
            return ProjectAuthorityDecision.Denied;
        // A backup who has left the project is not cover. Unchanged fail-closed rule.
        return await db.ProgramMemberships.AsNoTracking()
            .AnyAsync(x => x.UserId == userId && x.ProgramId == programId && x.EndedAt == null, ct)
            ? ProjectAuthorityDecision.From(ProjectAuthoritySource.LegacyCompatibility)
            : ProjectAuthorityDecision.Denied;
    }

    private async Task<ProjectAuthorityDecision> ResolveDelegationAsync(
        Guid userId, Guid programId, ProgramRole role, DateTimeOffset now, CancellationToken ct)
    {
        // Exact-role and time-bounded, exactly as before: a delegation of one role is not a delegation of
        // everything that role could satisfy.
        var delegated = await db.RoleDelegations.AsNoTracking()
            .Where(x => x.ProgramId == programId && x.DelegateUserId == userId && x.Role == role && x.RevokedAt == null)
            .ToListAsync(ct);
        return delegated.Any(x => x.StartsAt <= now && x.EndsAt > now)
            ? ProjectAuthorityDecision.From(ProjectAuthoritySource.Delegation)
            : ProjectAuthorityDecision.Denied;
    }

    /// <summary>
    /// Everybody who currently answers a demand on this program, with provenance — the one source the
    /// candidate picker and the signing gate must both read so they cannot disagree.
    /// </summary>
    public async Task<IReadOnlyList<(Guid UserId, ProjectAuthoritySource Source, ProjectLeadershipPosition? Position)>>
        ResolveHoldersAsync(Guid programId, ProgramRole demanded, DateTimeOffset now,
            bool includeProgramAdministratorSubstitution = false, CancellationToken ct = default)
    {
        var accepted = LegacyDemandMembershipAnswerable(demanded);
        var results = new Dictionary<Guid, (ProjectAuthoritySource, ProjectLeadershipPosition?)>();

        var activeMembers = await db.ProgramMemberships.AsNoTracking()
            .Where(x => x.ProgramId == programId && x.EndedAt == null)
            .Join(db.UserAccounts.AsNoTracking().Where(u => u.State == AccountState.Active),
                m => m.UserId, u => u.Id, (m, u) => new { m.UserId, m.Role })
            .ToListAsync(ct);

        var byMembership = activeMembers.Where(x => accepted.Contains(x.Role)).Select(x => x.UserId).ToHashSet();
        foreach (var userId in byMembership)
            results.TryAdd(userId, (ProjectAuthoritySource.DirectBaseRole, null));

        var matching = ProjectLeadership.All
            .Where(position =>
            {
                var demands = ProjectLeadership.SatisfyingDemands(position);
                return demands.Contains(demanded) || accepted.Any(demands.Contains);
            })
            .ToList();

        foreach (var position in matching)
        {
            var requiredBaseRole = ProjectLeadership.RequiredBaseRole(position);
            var eligibleUsers = activeMembers.Where(x => x.Role == requiredBaseRole).Select(x => x.UserId).ToHashSet();
            if (eligibleUsers.Count == 0) continue;

            var primaries = await db.ProjectLeadershipAssignments.AsNoTracking()
                .Where(x => x.ProgramId == programId && x.Position == position && x.EndedAt == null)
                .Select(x => x.HolderUserId).ToListAsync(ct);
            foreach (var holder in primaries.Where(eligibleUsers.Contains))
                results[holder] = (ProjectAuthoritySource.LeadershipPrimary, position);

            var backups = await db.ProjectLeadershipBackups.AsNoTracking()
                .Where(x => x.ProgramId == programId && x.Position == position && x.RemovedAt == null)
                .Select(x => x.BackupUserId).ToListAsync(ct);
            // TryAdd, not assignment: somebody who answers the demand in their own right AND happens to back
            // up a position is a holder, and recording them only as a backup made the Approval Configuration
            // Center report a stage as unheld when it was held.
            foreach (var backup in backups.Where(eligibleUsers.Contains))
                results.TryAdd(backup, (ProjectAuthoritySource.LeadershipBackup, position));
        }

        var activeMemberIds = activeMembers.Select(x => x.UserId).ToHashSet();

        // Legacy role-keyed backups still stand for the roles that are still jobs — Reviewer, SQA and the
        // rest. They are deliberately NOT honoured for position roles: that designation belongs on
        // ProjectLeadershipBackup, and reading both is what let a removed backup keep signing.
        var legacyBackups = await db.ProjectRoleBackups.AsNoTracking()
            .Where(x => x.ProgramId == programId && x.RemovedAt == null)
            .Select(x => new { x.BackupUserId, x.Role }).ToListAsync(ct);
        foreach (var backup in legacyBackups.Where(x =>
                     !SingularProgramRoles.IsSingular(x.Role) && !SingularProgramRoles.IsBaseEligibility(x.Role)
                     && accepted.Contains(x.Role) && activeMemberIds.Contains(x.BackupUserId)))
            results.TryAdd(backup.BackupUserId, (ProjectAuthoritySource.LegacyCompatibility, null));

        var delegations = await db.RoleDelegations.AsNoTracking()
            .Where(x => x.ProgramId == programId && x.Role == demanded && x.RevokedAt == null)
            .ToListAsync(ct);
        var activeDelegations = delegations.Where(x => x.StartsAt <= now && x.EndsAt > now).ToList();
        var delegatedUserIds = activeDelegations.Select(x => x.DelegateUserId).Distinct().ToList();
        HashSet<Guid> activeDelegatedUserIds = delegatedUserIds.Count == 0
            ? []
            : (await db.UserAccounts.AsNoTracking()
                .Where(x => delegatedUserIds.Contains(x.Id) && x.State == AccountState.Active)
                .Select(x => x.Id).ToListAsync(ct)).ToHashSet();
        // A delegation is intentionally an exact, time-bounded authority in its own right and the legacy
        // compatibility contract does not require the delegate to retain another Program membership. Project
        // every active delegate that ResolveAsync would grant so pickers cannot hide a valid signer.
        foreach (var delegation in activeDelegations)
            if (activeDelegatedUserIds.Contains(delegation.DelegateUserId))
                results.TryAdd(delegation.DelegateUserId, (ProjectAuthoritySource.Delegation, null));

        if (includeProgramAdministratorSubstitution)
            foreach (var administratorId in activeMembers
                         .Where(x => x.Role == ProgramRole.Administrator)
                         .Select(x => x.UserId).Distinct())
                results[administratorId] = (ProjectAuthoritySource.AdministratorSubstitution, null);

        // ResolveAsync grants the one active installation administrator before consulting any project-scoped
        // source. The holder projection must report that same substitution even when the account deliberately
        // has no Program membership; otherwise a picker can hide somebody whom the signing gate accepts and
        // the Approval Configuration Center can call a signable stage blocked. Add it last so the provenance
        // matches ResolveAsync even if the administrator also happens to hold a project role.
        var systemAdministratorId = await db.UserAccounts.AsNoTracking()
            .Where(x => x.State == AccountState.Active
                        && x.UserName == IdentityService.SystemAdministratorUserName)
            .Select(x => (Guid?)x.Id)
            .SingleOrDefaultAsync(ct);
        if (systemAdministratorId is not null)
            results[systemAdministratorId.Value] = (ProjectAuthoritySource.AdministratorSubstitution, null);

        return results.Select(x => (x.Key, x.Value.Item1, x.Value.Item2)).ToList();
    }
}
