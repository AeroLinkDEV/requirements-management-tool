using AeroLink.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>
/// The one place Project Leadership mutations run: assigning or atomically replacing a primary holder,
/// naming, changing and removing standing backups, and the idempotent backfill that migrates the legacy
/// singular memberships onto the new positions.
///
/// Every path here is attributed and transactional. Replacing a primary ends the old assignment, begins
/// the new one and — when the new holder is the position's current backup — ends that backup designation
/// in the same save, so no committed state ever shows the same person as both primary and backup, and no
/// committed vacancy appears between two replacements. Two concurrent replacements are settled by the
/// unique active-assignment index: the loser fails rather than silently double-writing.
/// </summary>
public sealed class ProjectLeadershipService(AeroLinkDbContext db)
{
    /// <summary>
    /// Assigns the primary holder of a leadership position, replacing any current holder atomically.
    ///
    /// The holder must be an active account, a current member of the program, and must already hold the
    /// base role the position requires — elevation is never implicit, and the base role is never granted
    /// as a side effect. When the position was held, the response reports the replacement and whether the
    /// previous backup (if any) remains attached, so the caller can make the continuation explicit.
    /// </summary>
    public async Task<PrimaryAssignmentResult> AssignPrimaryAsync(
        Guid programId, ProjectLeadershipPosition position, Guid holderUserId, string actor, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await ValidateHolderAsync(programId, position, holderUserId, isPrimary: true, ct);

        var active = await db.ProjectLeadershipAssignments
            .SingleOrDefaultAsync(x => x.ProgramId == programId && x.Position == position && x.EndedAt == null, ct);
        var replacedHolderId = default(Guid?);
        var backupContinues = false;

        if (active is not null)
        {
            replacedHolderId = active.HolderUserId;
            active.End(actor, now);
        }

        // Naming the current backup as primary is the promoted-backup flow: the backup designation ends in
        // the same transaction, so nobody is ever simultaneously primary and backup. A different new
        // primary leaves an existing backup attached — the endpoint reports that continuation explicitly.
        var currentBackup = await db.ProjectLeadershipBackups
            .SingleOrDefaultAsync(x => x.ProgramId == programId && x.Position == position && x.RemovedAt == null, ct);
        if (currentBackup is not null && currentBackup.BackupUserId == holderUserId)
            currentBackup.Remove(actor, now);
        else
            backupContinues = currentBackup is not null;

        db.ProjectLeadershipAssignments.Add(new ProjectLeadershipAssignment(programId, position, holderUserId, actor, now));

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Two concurrent replacements both ending the same active assignment: the unique
            // active-assignment index decides, and the loser reports the conflict instead of writing.
            throw new ProjectLeadershipConflictException(
                $"{position} was just reassigned by somebody else. Refresh and review the current holder.");
        }

        return new PrimaryAssignmentResult(position, holderUserId, replacedHolderId, backupContinues);
    }

    /// <summary>Naming a standing backup. Refused while one is already active for the position.</summary>
    public async Task AssignBackupAsync(
        Guid programId, ProjectLeadershipPosition position, Guid backupUserId, string actor, CancellationToken ct)
    {
        if (await db.ProjectLeadershipBackups.AnyAsync(x => x.ProgramId == programId && x.Position == position && x.RemovedAt == null, ct))
            throw new ProjectLeadershipConflictException(
                $"{position} already has a standing backup. Remove or change it first.");
        await ValidateHolderAsync(programId, position, backupUserId, isPrimary: false, ct);
        db.ProjectLeadershipBackups.Add(new ProjectLeadershipBackup(programId, position, backupUserId, actor, DateTimeOffset.UtcNow));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Changing a standing backup in one transaction: the current designation is removed and the new one
    /// named in the same save, so there is no committed window with two backups or with none and a half.
    /// </summary>
    public async Task ChangeBackupAsync(
        Guid programId, ProjectLeadershipPosition position, Guid backupUserId, string actor, CancellationToken ct)
    {
        var current = await db.ProjectLeadershipBackups
            .SingleOrDefaultAsync(x => x.ProgramId == programId && x.Position == position && x.RemovedAt == null, ct);
        if (current is null)
        {
            await AssignBackupAsync(programId, position, backupUserId, actor, ct);
            return;
        }
        await ValidateHolderAsync(programId, position, backupUserId, isPrimary: false, ct);
        current.Remove(actor, DateTimeOffset.UtcNow);
        db.ProjectLeadershipBackups.Add(new ProjectLeadershipBackup(programId, position, backupUserId, actor, DateTimeOffset.UtcNow));
        await db.SaveChangesAsync(ct);
    }

    public async Task RemoveBackupAsync(Guid programId, ProjectLeadershipPosition position, string actor, CancellationToken ct)
    {
        var current = await db.ProjectLeadershipBackups
            .SingleOrDefaultAsync(x => x.ProgramId == programId && x.Position == position && x.RemovedAt == null, ct);
        if (current is null) return;
        current.Remove(actor, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(ct);
    }

    private async Task ValidateHolderAsync(
        Guid programId, ProjectLeadershipPosition position, Guid holderUserId, bool isPrimary, CancellationToken ct)
    {
        if (await db.UserAccounts.AnyAsync(x => x.Id == holderUserId && x.State != AccountState.Active, ct))
            throw new ProjectLeadershipEligibilityException("The account is not active.");
        if (!await db.ProgramMemberships.AnyAsync(x => x.UserId == holderUserId && x.ProgramId == programId && x.EndedAt == null, ct))
            throw new ProjectLeadershipEligibilityException("The person is not a current member of this project.");
        var requiredRole = ProjectLeadership.RequiredBaseRole(position);
        if (!await db.ProgramMemberships.AnyAsync(x => x.UserId == holderUserId && x.ProgramId == programId
                && x.Role == requiredRole && x.EndedAt == null, ct))
        {
            var primary = isPrimary ? "primary" : "backup";
            throw new ProjectLeadershipEligibilityException(
                $"This position requires the {requiredRole} role, which the proposed {primary} does not hold.");
        }
        if (isPrimary) return;
        var activePrimary = await db.ProjectLeadershipAssignments
            .SingleOrDefaultAsync(x => x.ProgramId == programId && x.Position == position && x.EndedAt == null, ct);
        if (activePrimary is not null && activePrimary.HolderUserId == holderUserId)
            throw new ProjectLeadershipEligibilityException(
                "They already hold this position as primary, so they cannot also be its backup.");
    }
}

/// <summary>The outcome of an assign-or-replace operation, reported so the caller can confirm continuation.</summary>
public sealed record PrimaryAssignmentResult(
    ProjectLeadershipPosition Position, Guid HolderUserId, Guid? ReplacedHolderId, bool PreviousBackupContinues);

/// <summary>The proposed holder or backup does not satisfy the position's eligibility.</summary>
public sealed class ProjectLeadershipEligibilityException(string message) : Exception(message);

/// <summary>Two writers raced for the same singular position; the loser reports instead of writing.</summary>
public sealed class ProjectLeadershipConflictException(string message) : Exception(message);
