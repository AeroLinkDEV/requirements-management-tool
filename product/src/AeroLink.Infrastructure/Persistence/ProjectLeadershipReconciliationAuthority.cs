using AeroLink.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>
/// Retires the legacy authority rows that the v1 backfill copied but left alive.
///
/// v1 created a Project Leadership assignment for each legacy lead membership and a leadership backup for
/// each legacy role backup, and then left both originals active. Two live authority systems is one too many:
/// replacing a System Engineering Lead in the new model left the old membership answering Reviewer and
/// Approver through <c>ProgramRoleAuthority</c>, so the previous holder kept signing after the API reported
/// they had been replaced, and removing a migrated backup left the legacy <c>ProjectRoleBackup</c> behind to
/// do the same. Neither is visible anywhere in the product, which is what makes it worth a second migration
/// rather than a note.
///
/// This runs as its own marker because v1's marker already exists in databases that ran it: editing v1 would
/// change nothing for exactly the installations that need repairing.
///
/// What it retires, and what it deliberately does not:
///
/// - Legacy LEADERSHIP memberships — the four discipline leads and the retired ProjectEngineeringLead — are
///   ended once the equivalent assignment exists. They named a position, and the position is now a row.
/// - BASE ELIGIBILITY memberships (ProjectEngineer, ProgramManager, EngineeringManager,
///   ConfigurationManager) are PRESERVED. The enum conflated "the job" with "the post"; #816 split them, and
///   these four are the job. Ending them would strip the eligibility that keeps the assignment valid and
///   revoke the very authority this migration exists to protect.
/// - Every legacy role-keyed backup that names one of the eight positions is migrated to that exact
///   position for the same person, then retired. A different new-model holder is a conflict, never a cue to
///   delete the legacy row. ProjectEngineeringLead and ProjectEngineer both map to Project Engineer.
///
/// Conflicts are refused, never resolved by guessing, and the whole repair is one transaction: a conflict in
/// the last program must leave the first program untouched.
/// </summary>
public sealed class ProjectLeadershipReconciliationAuthority(AeroLinkDbContext db)
{
    public const string MigrationMarker = "AuthorityMigration.ProjectLeadership.v2";
    private const string CompletedEvent = MigrationMarker + ".Completed";
    private const string Actor = "aerolink-migration";

    /// <summary>The legacy memberships that named a position rather than a job, and so must stop granting.</summary>
    private static readonly (ProgramRole Role, ProjectLeadershipPosition Position)[] LegacyPositionMemberships =
    [
        (ProgramRole.SystemEngineeringLead, ProjectLeadershipPosition.SystemEngineeringLead),
        (ProgramRole.SoftwareEngineeringLead, ProjectLeadershipPosition.SoftwareEngineeringLead),
        (ProgramRole.SystemTestLead, ProjectLeadershipPosition.SystemTestLead),
        (ProgramRole.SoftwareTestLead, ProjectLeadershipPosition.SoftwareTestLead),
        (ProgramRole.ProjectEngineeringLead, ProjectLeadershipPosition.ProjectEngineer),
    ];

    /// <summary>
    /// Every legacy role key that designated standing cover for a position. There are nine keys for eight
    /// positions because both the retired ProjectEngineeringLead key and its base ProjectEngineer key map
    /// to the Project Engineer position.
    /// </summary>
    private static readonly (ProgramRole Role, ProjectLeadershipPosition Position)[] LegacyPositionBackups =
    [
        (ProgramRole.SystemEngineeringLead, ProjectLeadershipPosition.SystemEngineeringLead),
        (ProgramRole.SoftwareEngineeringLead, ProjectLeadershipPosition.SoftwareEngineeringLead),
        (ProgramRole.SystemTestLead, ProjectLeadershipPosition.SystemTestLead),
        (ProgramRole.SoftwareTestLead, ProjectLeadershipPosition.SoftwareTestLead),
        (ProgramRole.ProjectEngineeringLead, ProjectLeadershipPosition.ProjectEngineer),
        (ProgramRole.ProjectEngineer, ProjectLeadershipPosition.ProjectEngineer),
        (ProgramRole.ProgramManager, ProjectLeadershipPosition.ProgramManager),
        (ProgramRole.EngineeringManager, ProjectLeadershipPosition.EngineeringManager),
        (ProgramRole.ConfigurationManager, ProjectLeadershipPosition.ConfigurationManager),
    ];

    public async Task EnsureCompletedAsync(CancellationToken ct = default)
    {
        if (!db.Database.IsNpgsql()) return;
        if (await db.SecurityAuditEvents.AsNoTracking().AnyAsync(x => x.EventType == CompletedEvent, ct)) return;

        // One transaction over every program, the conflict scan and the marker. v1 saved inside its
        // per-program loop, so a conflict found in the second program committed the first program's writes
        // and then failed startup without a marker — the partial state it claimed to prevent.
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await ReconcileAsync(ct);
            db.SecurityAuditEvents.Add(new SecurityAuditEvent(CompletedEvent, Actor, "project-leadership",
                "Success", "Legacy Project Leadership authority rows reconciled with the #816 model.",
                "local", DateTimeOffset.UtcNow));
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        });
    }

    /// <summary>
    /// Provider-agnostic core, so disposable qualification can exercise it directly and so the caller owns
    /// the transaction. Validates every program before writing anything.
    /// </summary>
    public async Task ReconcileAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var programIds = await db.Programs.AsNoTracking().Select(x => x.Id).ToListAsync(ct);

        // Validate first. A refusal must name what a human has to decide, and must not have written anything
        // by the time it is raised.
        var conflicts = new List<string>();
        foreach (var programId in programIds)
            conflicts.AddRange(await ConflictsAsync(programId, ct));
        if (conflicts.Count > 0)
            throw new InvalidOperationException(
                "Conflicting legacy Project Leadership authority. The upgrade will not choose between these; "
                + "resolve each explicitly and restart:" + Environment.NewLine
                + string.Join(Environment.NewLine, conflicts.Select(x => "  - " + x)));

        foreach (var programId in programIds)
        {
            await RetireLegacyPositionMembershipsAsync(programId, now, ct);
            await MigrateLegacyPositionBackupsAsync(programId, now, ct);
        }
    }

    /// <summary>
    /// What this program cannot repair without somebody deciding. Reported all at once so an operator fixes
    /// the whole set in one pass rather than discovering them one restart at a time.
    /// </summary>
    private async Task<IReadOnlyList<string>> ConflictsAsync(Guid programId, CancellationToken ct)
    {
        var problems = new List<string>();
        var name = await db.Programs.AsNoTracking().Where(x => x.Id == programId)
            .Select(x => x.Name).SingleOrDefaultAsync(ct) ?? programId.ToString();

        // A legacy position membership with no assignment to take over from it. Ending it would silently
        // remove authority somebody currently has; leaving it keeps two live systems. Neither is ours to pick.
        foreach (var (role, position) in LegacyPositionMemberships)
        {
            var holders = await db.ProgramMemberships.AsNoTracking()
                .Where(x => x.ProgramId == programId && x.EndedAt == null && x.Role == role)
                .Select(x => x.UserId).ToListAsync(ct);
            if (holders.Count == 0) continue;
            var assigned = await db.ProjectLeadershipAssignments.AsNoTracking()
                .Where(x => x.ProgramId == programId && x.Position == position && x.EndedAt == null)
                .Select(x => x.HolderUserId).ToListAsync(ct);
            if (assigned.Count == 0)
            {
                problems.Add($"{name}: an active {role} membership has no {position} leadership assignment to "
                    + "take over from it. Assign the position, or end the membership, then restart.");
                continue;
            }
            // The assignment exists, but for somebody else. Ending this membership would revoke authority
            // from a person the new model never gave anything to — a silent choice, not a reconciliation.
            var stranded = holders.Where(x => !assigned.Contains(x)).ToList();
            if (stranded.Count > 0)
                problems.Add($"{name}: an active {role} membership is held by somebody who does not hold the "
                    + $"{position} position. Retiring it would revoke their authority without replacing it. "
                    + "End the membership deliberately, or assign them the position, then restart.");
        }

        // A legacy role-keyed backup is repairable only when one unambiguous person is eligible for the
        // mapped position and any already-created leadership backup names that same person. Existence of an
        // arbitrary new-model row is not equivalence: retiring Alice's legacy row because Bob is the new
        // backup would silently choose Bob and destroy the evidence of the unresolved conflict.
        var positionBackups = await db.ProjectRoleBackups.AsNoTracking()
            .Where(x => x.ProgramId == programId && x.RemovedAt == null)
            .Select(x => new { x.Role, x.BackupUserId }).ToListAsync(ct);
        var mappedBackups = positionBackups
            .Select(x => new { x.Role, x.BackupUserId, Position = PositionForBackup(x.Role) })
            .Where(x => x.Position is not null)
            .GroupBy(x => x.Position!.Value);
        foreach (var group in mappedBackups)
        {
            var position = group.Key;
            var legacyHolders = group.Select(x => x.BackupUserId).Distinct().ToList();
            if (legacyHolders.Count != 1)
            {
                problems.Add($"{name}: legacy standing backups that map to {position} name different people. "
                    + "Decide who backs the position and remove the other legacy designation, then restart.");
                continue;
            }
            var legacyHolder = legacyHolders[0];
            var requiredRole = ProjectLeadership.RequiredBaseRole(position);
            var eligible = await db.ProgramMemberships.AsNoTracking().AnyAsync(
                x => x.UserId == legacyHolder && x.ProgramId == programId && x.EndedAt == null
                     && x.Role == requiredRole, ct);
            if (!eligible)
            {
                problems.Add($"{name}: the legacy {position} standing backup does not hold the required "
                    + $"{requiredRole} base role. Grant the role if they should keep backing the position, "
                    + "or remove the legacy backup, then restart.");
                continue;
            }
            var currentHolders = await db.ProjectLeadershipBackups.AsNoTracking()
                .Where(x => x.ProgramId == programId && x.Position == position && x.RemovedAt == null)
                .Select(x => x.BackupUserId).Distinct().ToListAsync(ct);
            if (currentHolders.Any(x => x != legacyHolder))
                problems.Add($"{name}: the legacy {position} standing backup and the current leadership "
                    + "backup name different people. Decide who backs the position and remove the other "
                    + "designation, then restart.");
        }

        return problems;
    }

    /// <summary>
    /// The position a legacy role-keyed backup belongs to, including the four base eligibility roles whose
    /// backups were never in scope for v1 and so were left active with nothing to supersede them.
    /// </summary>
    private static ProjectLeadershipPosition? PositionForBackup(ProgramRole role) =>
        LegacyPositionBackups.Where(x => x.Role == role).Select(x => (ProjectLeadershipPosition?)x.Position)
            .SingleOrDefault();

    /// <summary>Ends legacy position memberships once the assignment that replaced them exists.</summary>
    private async Task RetireLegacyPositionMembershipsAsync(Guid programId, DateTimeOffset now, CancellationToken ct)
    {
        foreach (var (role, position) in LegacyPositionMemberships)
        {
            var assignmentExists = await db.ProjectLeadershipAssignments.AsNoTracking()
                .AnyAsync(x => x.ProgramId == programId && x.Position == position && x.EndedAt == null, ct);
            if (!assignmentExists) continue;

            var memberships = await db.ProgramMemberships
                .Where(x => x.ProgramId == programId && x.EndedAt == null && x.Role == role)
                .ToListAsync(ct);
            foreach (var membership in memberships) membership.End(Actor, now);
        }
    }

    /// <summary>
    /// Moves every unambiguous legacy position backup to the corresponding leadership position for the
    /// same person. Conflict validation has already proved eligibility and same-person equivalence before
    /// this method changes a row.
    /// </summary>
    private async Task MigrateLegacyPositionBackupsAsync(Guid programId, DateTimeOffset now, CancellationToken ct)
    {
        var legacyRoles = LegacyPositionBackups.Select(x => x.Role).Distinct().ToList();
        var backups = await db.ProjectRoleBackups
            .Where(x => x.ProgramId == programId && x.RemovedAt == null && legacyRoles.Contains(x.Role))
            .ToListAsync(ct);
        foreach (var group in backups.GroupBy(x => PositionForBackup(x.Role)!.Value))
        {
            var holder = group.Select(x => x.BackupUserId).Distinct().Single();
            var existing = await db.ProjectLeadershipBackups.AsNoTracking().AnyAsync(x =>
                x.ProgramId == programId && x.Position == group.Key && x.BackupUserId == holder
                && x.RemovedAt == null, ct);
            if (!existing)
                db.ProjectLeadershipBackups.Add(new ProjectLeadershipBackup(
                    programId, group.Key, holder, Actor, now));
            foreach (var backup in group) backup.Remove(Actor, now);
        }
    }
}
