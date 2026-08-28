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
/// - Legacy ProjectRoleBackups of the four discipline-lead roles are removed once the equivalent
///   ProjectLeadershipBackup exists.
/// - A legacy ProjectEngineeringLead backup is migrated to the Project Engineer position when that is
///   unambiguous, and refused when it is not. v1 left it alone on purpose, which made it permanent.
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
            await RetireMigratedBackupsAsync(programId, now, ct);
            await MigrateProjectEngineeringLeadBackupAsync(programId, now, ct);
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
                problems.Add($"{name}: an active {role} membership has no {position} leadership assignment to "
                    + "take over from it. Assign the position, or end the membership, then restart.");
        }

        // A ProjectEngineeringLead backup that cannot be moved to Project Engineer without overwriting a
        // different decision or inventing eligibility.
        var pelBackups = await db.ProjectRoleBackups.AsNoTracking()
            .Where(x => x.ProgramId == programId && x.RemovedAt == null && x.Role == ProgramRole.ProjectEngineeringLead)
            .ToListAsync(ct);
        foreach (var pelBackup in pelBackups)
        {
            var existing = await db.ProjectLeadershipBackups.AsNoTracking()
                .Where(x => x.ProgramId == programId && x.Position == ProjectLeadershipPosition.ProjectEngineer && x.RemovedAt == null)
                .Select(x => x.BackupUserId).SingleOrDefaultAsync(ct);
            if (existing != Guid.Empty && existing != pelBackup.BackupUserId)
            {
                problems.Add($"{name}: a legacy Project Engineering Lead backup and a different Project "
                    + "Engineer leadership backup both exist. Decide which person is the backup and remove "
                    + "the other, then restart.");
                continue;
            }
            var eligible = await db.ProgramMemberships.AsNoTracking().AnyAsync(
                x => x.UserId == pelBackup.BackupUserId && x.ProgramId == programId && x.EndedAt == null
                     && x.Role == ProgramRole.ProjectEngineer, ct);
            if (!eligible)
                problems.Add($"{name}: the legacy Project Engineering Lead backup does not hold the Project "
                    + "Engineer role, so the position's eligibility cannot be satisfied. Grant the role if "
                    + "they should keep backing the position, or remove the legacy backup, then restart.");
        }

        return problems;
    }

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

    /// <summary>Removes legacy role-keyed backups whose designation now lives on the position.</summary>
    private async Task RetireMigratedBackupsAsync(Guid programId, DateTimeOffset now, CancellationToken ct)
    {
        foreach (var (role, position) in LegacyPositionMemberships)
        {
            if (role == ProgramRole.ProjectEngineeringLead) continue; // handled with its own mapping rules
            var backups = await db.ProjectRoleBackups
                .Where(x => x.ProgramId == programId && x.RemovedAt == null && x.Role == role)
                .ToListAsync(ct);
            if (backups.Count == 0) continue;
            var migrated = await db.ProjectLeadershipBackups.AsNoTracking()
                .AnyAsync(x => x.ProgramId == programId && x.Position == position && x.RemovedAt == null, ct);
            if (!migrated) continue;
            foreach (var backup in backups) backup.Remove(Actor, now);
        }
    }

    /// <summary>
    /// Moves a legacy Project Engineering Lead backup onto the Project Engineer position, or leaves it for
    /// the conflict report. v1 deliberately left this row alone so it would keep answering legacy demands —
    /// which made it a permanent second authority channel that removing the new backup could not switch off.
    /// </summary>
    private async Task MigrateProjectEngineeringLeadBackupAsync(Guid programId, DateTimeOffset now, CancellationToken ct)
    {
        var pelBackups = await db.ProjectRoleBackups
            .Where(x => x.ProgramId == programId && x.RemovedAt == null && x.Role == ProgramRole.ProjectEngineeringLead)
            .ToListAsync(ct);
        foreach (var pelBackup in pelBackups)
        {
            var eligible = await db.ProgramMemberships.AsNoTracking().AnyAsync(
                x => x.UserId == pelBackup.BackupUserId && x.ProgramId == programId && x.EndedAt == null
                     && x.Role == ProgramRole.ProjectEngineer, ct);
            if (!eligible) continue; // already reported as a conflict; never invent the eligibility

            var existing = await db.ProjectLeadershipBackups.AsNoTracking().AnyAsync(
                x => x.ProgramId == programId && x.Position == ProjectLeadershipPosition.ProjectEngineer
                     && x.RemovedAt == null, ct);
            if (!existing)
                db.ProjectLeadershipBackups.Add(new ProjectLeadershipBackup(
                    programId, ProjectLeadershipPosition.ProjectEngineer, pelBackup.BackupUserId, Actor, now));
            pelBackup.Remove(Actor, now);
        }
    }
}
