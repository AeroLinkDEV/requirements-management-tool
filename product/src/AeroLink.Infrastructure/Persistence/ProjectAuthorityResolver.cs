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
        if (requirement.AllowProgramAdministratorSubstitution)
        {
            var administratorMembershipId = await db.ProgramMemberships.AsNoTracking()
                .Where(x => x.UserId == userId && x.ProgramId == programId && x.EndedAt == null
                            && x.Role == ProgramRole.Administrator)
                .OrderBy(x => x.Id).Select(x => (Guid?)x.Id).FirstOrDefaultAsync(ct);
            if (administratorMembershipId is not null)
                return ProjectAuthorityDecision.From(ProjectAuthoritySource.AdministratorSubstitution,
                    sourceId: administratorMembershipId);
        }

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
        var memberships = await db.ProgramMemberships.AsNoTracking()
            .Where(x => x.UserId == userId && x.ProgramId == programId && x.EndedAt == null && accepted.Contains(x.Role))
            .Select(x => new { x.Id, x.Role }).ToListAsync(ct);
        var membership = accepted.SelectMany(role => memberships.Where(x => x.Role == role).OrderBy(x => x.Id))
            .FirstOrDefault();
        if (membership is not null)
            return ProjectAuthorityDecision.From(ProjectAuthoritySource.DirectBaseRole,
                sourceId: membership.Id);
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
            .OrderBy(x => x.Id).Select(x => (Guid?)x.Id).FirstOrDefaultAsync(ct);
        if (assignmentId is not null)
            return ProjectAuthorityDecision.From(ProjectAuthoritySource.LeadershipPrimary, position, assignmentId);

        var backupId = await db.ProjectLeadershipBackups.AsNoTracking()
            .Where(x => x.ProgramId == programId && x.Position == position && x.BackupUserId == userId && x.RemovedAt == null)
            .OrderBy(x => x.Id).Select(x => (Guid?)x.Id).FirstOrDefaultAsync(ct);
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
        var memberships = await db.ProgramMemberships.AsNoTracking()
            .Where(x => x.UserId == userId && x.ProgramId == programId && x.EndedAt == null && accepted.Contains(x.Role))
            .Select(x => new { x.Id, x.Role }).ToListAsync(ct);
        var membership = accepted.SelectMany(role => memberships.Where(x => x.Role == role).OrderBy(x => x.Id))
            .FirstOrDefault();
        if (membership is not null)
            return ProjectAuthorityDecision.From(ProjectAuthoritySource.DirectBaseRole,
                sourceId: membership.Id);

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
            .OrderBy(x => x.Id).Select(x => new { x.Id, x.Role }).ToListAsync(ct);
        var matchingBackup = backed.FirstOrDefault(x => !SingularProgramRoles.IsPositionGoverned(x.Role) && accepted.Contains(x.Role));
        if (matchingBackup is null)
            return ProjectAuthorityDecision.Denied;
        // A backup who has left the project is not cover. Unchanged fail-closed rule.
        return await db.ProgramMemberships.AsNoTracking()
            .AnyAsync(x => x.UserId == userId && x.ProgramId == programId && x.EndedAt == null, ct)
            ? ProjectAuthorityDecision.From(ProjectAuthoritySource.LegacyCompatibility,
                sourceId: matchingBackup.Id)
            : ProjectAuthorityDecision.Denied;
    }

    private async Task<ProjectAuthorityDecision> ResolveDelegationAsync(
        Guid userId, Guid programId, ProgramRole role, DateTimeOffset now, CancellationToken ct)
    {
        // Exact-role and time-bounded, exactly as before: a delegation of one role is not a delegation of
        // everything that role could satisfy.
        var delegated = await db.RoleDelegations.AsNoTracking()
            .Where(x => x.ProgramId == programId && x.DelegateUserId == userId && x.Role == role && x.RevokedAt == null)
            .OrderBy(x => x.Id).ToListAsync(ct);
        var activeDelegation = delegated.FirstOrDefault(x => x.StartsAt <= now && x.EndsAt > now);
        return activeDelegation is not null
            ? ProjectAuthorityDecision.From(ProjectAuthoritySource.Delegation,
                sourceId: activeDelegation.Id)
            : ProjectAuthorityDecision.Denied;
    }

    /// <summary>Everybody who currently answers a demand, retaining the exact source row for each answer.</summary>
    public Task<IReadOnlyList<ProjectAuthorityHolder>> ResolveHolderDecisionsAsync(
        Guid programId, ProgramRole demanded, DateTimeOffset now,
        bool includeProgramAdministratorSubstitution = false, CancellationToken ct = default) =>
        ResolveHolderDecisionsAsync(programId,
            ProjectAuthorityRequirement.LegacyRoleDemand(demanded, includeProgramAdministratorSubstitution),
            now, includeProgramAdministratorSubstitution, ct);

    /// <summary>
    /// Explicit-authority holder projection. The dictionary is keyed by user so a person holding several
    /// matching rows is returned once, with deterministic precedence matching ResolveAsync.
    /// </summary>
    public async Task<IReadOnlyList<ProjectAuthorityHolder>> ResolveHolderDecisionsAsync(
        Guid programId, ProjectAuthorityRequirement requirement, DateTimeOffset now,
        bool includeProgramAdministratorSubstitution = false, CancellationToken ct = default)
    {
        var results = new Dictionary<Guid, ProjectAuthorityDecision>();
        var activeMembers = await db.ProgramMemberships.AsNoTracking()
            .Where(x => x.ProgramId == programId && x.EndedAt == null)
            .Join(db.UserAccounts.AsNoTracking().Where(u => u.State == AccountState.Active),
                m => m.UserId, u => u.Id, (m, u) => new { m.Id, m.UserId, m.Role })
            .OrderBy(x => x.Id).ToListAsync(ct);

        if (requirement.Kind == ProjectAuthorityKind.LegacyRoleDemand)
        {
            var demanded = requirement.Role!.Value;
            var accepted = LegacyDemandMembershipAnswerable(demanded);
            foreach (var group in activeMembers.Where(x => accepted.Contains(x.Role)).GroupBy(x => x.UserId))
            {
                var member = accepted.SelectMany(role => group.Where(x => x.Role == role).OrderBy(x => x.Id))
                    .First();
                results.TryAdd(member.UserId, ProjectAuthorityDecision.From(
                    ProjectAuthoritySource.DirectBaseRole, sourceId: member.Id));
            }

            var matching = ProjectLeadership.All.Where(position =>
            {
                var demands = ProjectLeadership.SatisfyingDemands(position);
                return demands.Contains(demanded) || accepted.Any(demands.Contains);
            }).ToList();
            foreach (var position in matching)
            {
                var requiredBaseRole = ProjectLeadership.RequiredBaseRole(position);
                var eligibleUsers = activeMembers.Where(x => x.Role == requiredBaseRole)
                    .Select(x => x.UserId).ToHashSet();
                var primaries = await db.ProjectLeadershipAssignments.AsNoTracking()
                    .Where(x => x.ProgramId == programId && x.Position == position && x.EndedAt == null)
                    .OrderBy(x => x.Id).Select(x => new { x.Id, x.HolderUserId }).ToListAsync(ct);
                foreach (var primary in primaries.Where(x => eligibleUsers.Contains(x.HolderUserId)))
                    results.TryAdd(primary.HolderUserId, ProjectAuthorityDecision.From(
                        ProjectAuthoritySource.LeadershipPrimary, position, primary.Id));
                var backups = await db.ProjectLeadershipBackups.AsNoTracking()
                    .Where(x => x.ProgramId == programId && x.Position == position && x.RemovedAt == null)
                    .OrderBy(x => x.Id).Select(x => new { x.Id, x.BackupUserId }).ToListAsync(ct);
                foreach (var backup in backups.Where(x => eligibleUsers.Contains(x.BackupUserId)))
                    results.TryAdd(backup.BackupUserId, ProjectAuthorityDecision.From(
                        ProjectAuthoritySource.LeadershipBackup, position, backup.Id));
            }

            var activeMemberIds = activeMembers.Select(x => x.UserId).ToHashSet();
            var legacyBackups = await db.ProjectRoleBackups.AsNoTracking()
                .Where(x => x.ProgramId == programId && x.RemovedAt == null)
                .OrderBy(x => x.Id).Select(x => new { x.Id, x.BackupUserId, x.Role }).ToListAsync(ct);
            foreach (var backup in legacyBackups.Where(x =>
                         !SingularProgramRoles.IsSingular(x.Role) && !SingularProgramRoles.IsBaseEligibility(x.Role)
                         && accepted.Contains(x.Role) && activeMemberIds.Contains(x.BackupUserId)))
                results.TryAdd(backup.BackupUserId, ProjectAuthorityDecision.From(
                    ProjectAuthoritySource.LegacyCompatibility, sourceId: backup.Id));

            var delegations = await db.RoleDelegations.AsNoTracking()
                .Where(x => x.ProgramId == programId && x.Role == demanded && x.RevokedAt == null)
                .OrderBy(x => x.Id).ToListAsync(ct);
            var activeDelegatedIds = delegations.Where(x => x.StartsAt <= now && x.EndsAt > now)
                .Select(x => x.DelegateUserId).Distinct().ToList();
            var activeDelegates = activeDelegatedIds.Count == 0 ? [] :
                (await db.UserAccounts.AsNoTracking().Where(x => activeDelegatedIds.Contains(x.Id) && x.State == AccountState.Active)
                    .Select(x => x.Id).ToListAsync(ct)).ToHashSet();
            foreach (var delegation in delegations.Where(x => x.StartsAt <= now && x.EndsAt > now
                                                               && activeDelegates.Contains(x.DelegateUserId)))
                results.TryAdd(delegation.DelegateUserId, ProjectAuthorityDecision.From(
                    ProjectAuthoritySource.Delegation, sourceId: delegation.Id));
        }
        else if (requirement.Kind == ProjectAuthorityKind.BaseRole)
        {
            var role = requirement.Role!.Value;
            var accepted = BaseRoleMembershipAnswerable(role);
            foreach (var group in activeMembers.Where(x => accepted.Contains(x.Role)).GroupBy(x => x.UserId))
            {
                var member = accepted.SelectMany(candidate => group.Where(x => x.Role == candidate).OrderBy(x => x.Id))
                    .First();
                results.TryAdd(member.UserId, ProjectAuthorityDecision.From(
                    ProjectAuthoritySource.DirectBaseRole, sourceId: member.Id));
            }
            var delegations = await db.RoleDelegations.AsNoTracking()
                .Where(x => x.ProgramId == programId && x.Role == role && x.RevokedAt == null)
                .OrderBy(x => x.Id).ToListAsync(ct);
            var delegateIds = delegations.Where(x => x.StartsAt <= now && x.EndsAt > now)
                .Select(x => x.DelegateUserId).Distinct().ToList();
            var activeDelegates = delegateIds.Count == 0 ? [] :
                (await db.UserAccounts.AsNoTracking().Where(x => delegateIds.Contains(x.Id) && x.State == AccountState.Active)
                    .Select(x => x.Id).ToListAsync(ct)).ToHashSet();
            foreach (var delegation in delegations.Where(x => x.StartsAt <= now && x.EndsAt > now
                                                               && activeDelegates.Contains(x.DelegateUserId)))
                results.TryAdd(delegation.DelegateUserId, ProjectAuthorityDecision.From(
                    ProjectAuthoritySource.Delegation, sourceId: delegation.Id));
        }
        else
        {
            var position = requirement.Position!.Value;
            var requiredBaseRole = ProjectLeadership.RequiredBaseRole(position);
            var eligibleUsers = activeMembers.Where(x => x.Role == requiredBaseRole)
                .Select(x => x.UserId).ToHashSet();
            var primaries = await db.ProjectLeadershipAssignments.AsNoTracking()
                .Where(x => x.ProgramId == programId && x.Position == position && x.EndedAt == null)
                .OrderBy(x => x.Id).Select(x => new { x.Id, x.HolderUserId }).ToListAsync(ct);
            foreach (var primary in primaries.Where(x => eligibleUsers.Contains(x.HolderUserId)))
                results.TryAdd(primary.HolderUserId, ProjectAuthorityDecision.From(
                    ProjectAuthoritySource.LeadershipPrimary, position, primary.Id));
            var backups = await db.ProjectLeadershipBackups.AsNoTracking()
                .Where(x => x.ProgramId == programId && x.Position == position && x.RemovedAt == null)
                .OrderBy(x => x.Id).Select(x => new { x.Id, x.BackupUserId }).ToListAsync(ct);
            foreach (var backup in backups.Where(x => eligibleUsers.Contains(x.BackupUserId)))
                results.TryAdd(backup.BackupUserId, ProjectAuthorityDecision.From(
                    ProjectAuthoritySource.LeadershipBackup, position, backup.Id));
        }

        if (includeProgramAdministratorSubstitution)
            foreach (var administrator in activeMembers.Where(x => x.Role == ProgramRole.Administrator)
                         .GroupBy(x => x.UserId).Select(x => x.OrderBy(y => y.Id).First()))
                results[administrator.UserId] = ProjectAuthorityDecision.From(
                    ProjectAuthoritySource.AdministratorSubstitution, sourceId: administrator.Id);

        // The installation administrator is intentionally not tied to a project row; null source identity is
        // the honest answer and remains distinguishable from a Program Administrator membership.
        var systemAdministratorId = await db.UserAccounts.AsNoTracking()
            .Where(x => x.State == AccountState.Active && x.UserName == IdentityService.SystemAdministratorUserName)
            .Select(x => (Guid?)x.Id).SingleOrDefaultAsync(ct);
        if (systemAdministratorId is not null)
            results[systemAdministratorId.Value] = ProjectAuthorityDecision.From(ProjectAuthoritySource.AdministratorSubstitution);

        return results.OrderBy(x => x.Key).Select(x => new ProjectAuthorityHolder(x.Key, x.Value)).ToList();
    }

    // Compatibility projection retained for existing candidate-picker callers. New evidence-bearing callers
    // use ResolveHolderDecisionsAsync so source IDs cannot be discarded accidentally.
    public async Task<IReadOnlyList<(Guid UserId, ProjectAuthoritySource Source, ProjectLeadershipPosition? Position)>>
        ResolveHoldersAsync(Guid programId, ProgramRole demanded, DateTimeOffset now,
            bool includeProgramAdministratorSubstitution = false, CancellationToken ct = default)
    {
        var holders = await ResolveHolderDecisionsAsync(programId, demanded, now,
            includeProgramAdministratorSubstitution, ct);
        return holders.Select(x => (x.UserId, x.Decision.Source, x.Decision.Position)).ToList();
    }

    public async Task<IReadOnlyList<(Guid UserId, ProjectAuthoritySource Source, ProjectLeadershipPosition? Position)>>
        ResolveHoldersAsync(Guid programId, ProjectAuthorityRequirement requirement, DateTimeOffset now,
            bool includeProgramAdministratorSubstitution = false, CancellationToken ct = default)
    {
        var holders = await ResolveHolderDecisionsAsync(programId, requirement, now,
            includeProgramAdministratorSubstitution, ct);
        return holders.Select(x => (x.UserId, x.Decision.Source, x.Decision.Position)).ToList();
    }
}
