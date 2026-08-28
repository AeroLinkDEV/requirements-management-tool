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

    /// <summary>What work somebody performs. Membership answers it; elevation is beside the point.</summary>
    private async Task<ProjectAuthorityDecision> ResolveBaseRoleAsync(
        Guid userId, Guid programId, ProgramRole role, DateTimeOffset now, CancellationToken ct)
    {
        var accepted = ProgramRoleAuthority.Satisfying(role);
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

        if (await db.ProjectLeadershipAssignments.AsNoTracking().AnyAsync(
                x => x.ProgramId == programId && x.Position == position && x.HolderUserId == userId && x.EndedAt == null, ct))
            return ProjectAuthorityDecision.From(ProjectAuthoritySource.LeadershipPrimary, position);

        if (await db.ProjectLeadershipBackups.AsNoTracking().AnyAsync(
                x => x.ProgramId == programId && x.Position == position && x.BackupUserId == userId && x.RemovedAt == null, ct))
            return ProjectAuthorityDecision.From(ProjectAuthoritySource.LeadershipBackup, position);

        return ProjectAuthorityDecision.Denied;
    }

    /// <summary>
    /// A stored demand naming a role, from before the split. It cannot say whether it meant the job or the
    /// position, so both answer it — but the leadership half is resolved position by position, each
    /// validated on its own eligibility.
    /// </summary>
    private async Task<ProjectAuthorityDecision> ResolveLegacyDemandAsync(
        Guid userId, Guid programId, ProgramRole role, DateTimeOffset now, CancellationToken ct)
    {
        var accepted = ProgramRoleAuthority.Satisfying(role);
        if (await db.ProgramMemberships.AsNoTracking().AnyAsync(
                x => x.UserId == userId && x.ProgramId == programId && x.EndedAt == null && accepted.Contains(x.Role), ct))
            return ProjectAuthorityDecision.From(ProjectAuthoritySource.DirectBaseRole);

        var leadership = await ResolveAnyLeadershipSatisfyingAsync(userId, programId, role, ct);
        if (leadership.Granted) return leadership;

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
        ResolveHoldersAsync(Guid programId, ProgramRole demanded, DateTimeOffset now, CancellationToken ct = default)
    {
        var accepted = ProgramRoleAuthority.Satisfying(demanded);
        var results = new Dictionary<Guid, (ProjectAuthoritySource, ProjectLeadershipPosition?)>();

        var activeMembers = await db.ProgramMemberships.AsNoTracking()
            .Where(x => x.ProgramId == programId && x.EndedAt == null)
            .Join(db.UserAccounts.AsNoTracking().Where(u => u.State == AccountState.Active),
                m => m.UserId, u => u.Id, (m, u) => new { m.UserId, m.Role })
            .ToListAsync(ct);

        foreach (var member in activeMembers.Where(x => accepted.Contains(x.Role)))
            results.TryAdd(member.UserId, (ProjectAuthoritySource.DirectBaseRole, null));

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
            foreach (var backup in backups.Where(eligibleUsers.Contains))
                if (!results.TryGetValue(backup, out var existing) || existing.Item1 != ProjectAuthoritySource.LeadershipPrimary)
                    results[backup] = (ProjectAuthoritySource.LeadershipBackup, position);
        }

        var activeUserIds = activeMembers.Select(x => x.UserId).ToHashSet();

        // Legacy role-keyed backups still stand for the roles that are still jobs — Reviewer, SQA and the
        // rest. They are deliberately NOT honoured for position roles: that designation belongs on
        // ProjectLeadershipBackup, and reading both is what let a removed backup keep signing.
        var legacyBackups = await db.ProjectRoleBackups.AsNoTracking()
            .Where(x => x.ProgramId == programId && x.RemovedAt == null)
            .Select(x => new { x.BackupUserId, x.Role }).ToListAsync(ct);
        foreach (var backup in legacyBackups.Where(x =>
                     !SingularProgramRoles.IsSingular(x.Role) && !SingularProgramRoles.IsBaseEligibility(x.Role)
                     && accepted.Contains(x.Role) && activeUserIds.Contains(x.BackupUserId)))
            results.TryAdd(backup.BackupUserId, (ProjectAuthoritySource.LegacyCompatibility, null));

        var delegations = await db.RoleDelegations.AsNoTracking()
            .Where(x => x.ProgramId == programId && x.Role == demanded && x.RevokedAt == null)
            .ToListAsync(ct);
        foreach (var delegation in delegations.Where(x => x.StartsAt <= now && x.EndsAt > now))
            if (activeUserIds.Contains(delegation.DelegateUserId))
                results.TryAdd(delegation.DelegateUserId, (ProjectAuthoritySource.Delegation, null));

        return results.Select(x => (x.Key, x.Value.Item1, x.Value.Item2)).ToList();
    }
}
