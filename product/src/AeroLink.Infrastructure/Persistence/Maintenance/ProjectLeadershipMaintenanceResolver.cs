using AeroLink.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace AeroLink.Infrastructure.Persistence.Maintenance;

/// <summary>What a resolver did, or refused to do, and whether anything changed.</summary>
public sealed record AeroLinkResolutionResult(
    bool Applied,
    string Outcome,
    string Detail,
    IReadOnlyList<string> Changes)
{
    /// <summary>Analysis only; nothing was written and nothing was going to be.</summary>
    public const string DryRunOutcome = "dry-run";

    /// <summary>The decision was applied and the database changed.</summary>
    public const string AppliedOutcome = "applied";

    /// <summary>
    /// The state the operator reviewed is not the state on disk any more. Nothing was written — the point of
    /// exact preconditions is that a stale decision cannot land.
    /// </summary>
    public const string PreconditionFailedOutcome = "precondition-failed";

    /// <summary>The choice was not one this conflict offers.</summary>
    public const string ChoiceRefusedOutcome = "choice-refused";
}

/// <summary>
/// The supported way to resolve a modelled Project Leadership upgrade conflict when the API cannot start.
///
/// On 2026-08-31 this did not exist, so the #816 conflict on the work laptop was repaired with hand-written
/// SQL against a live database while the API was down. It worked, and it should never be how this is done: no
/// domain validation, no audit attribution, and no protection against the state having moved between reading
/// it and writing it.
///
/// The rules this class exists to enforce:
///
///   * Dry run by default. Applying requires <paramref name="apply"/> to be true at the call site.
///   * The operator names the choice. Granting somebody a role and retiring a historical designation are
///     opposite answers, and AeroLink is not entitled to pick — least of all because picking would let
///     startup proceed.
///   * A choice that grants authority nobody has today must be named explicitly, and is never inferred.
///   * Preconditions are exact and re-read inside the transaction immediately before the write. The row id,
///     the person, the position, the base roles they hold, and who the current primary is must all still be
///     what the analysis reported.
///   * History is ended, never deleted. A retired backup keeps its NamedBy/NamedAt and gains RemovedBy/
///     RemovedAt, so "who was standing cover in March" stays answerable.
///   * Every applied decision writes a maintenance audit event with the formal attribution, and so does
///     every refusal.
/// </summary>
public sealed class ProjectLeadershipMaintenanceResolver(AeroLinkDbContext db)
{
    /// <summary>
    /// Resolves one legacy standing-backup conflict for one position in one program.
    /// </summary>
    /// <param name="programId">The program the analysis named.</param>
    /// <param name="legacyBackupId">The exact legacy ProjectRoleBackup row the analysis named.</param>
    /// <param name="position">The leadership position the legacy row maps to.</param>
    /// <param name="personId">The person the legacy row names.</param>
    /// <param name="choice">One of the conflict's offered choice keys.</param>
    /// <param name="expectedCurrentPrimaryId">
    /// Who the analysis reported as the position's active primary, or null when it reported none. A primary
    /// that changed between review and write is a moved precondition: the operator reviewed a different
    /// situation.
    /// </param>
    /// <param name="operatorReference">
    /// Who asked for this, in the operator's own terms (a name, a ticket, an issue number). Recorded on the
    /// audit event so the decision is attributable to a person and not only to a process.
    /// </param>
    /// <param name="apply">False analyzes and reports; true writes.</param>
    public async Task<AeroLinkResolutionResult> ResolveLegacyBackupAsync(
        Guid programId,
        Guid legacyBackupId,
        ProjectLeadershipPosition position,
        Guid personId,
        string choice,
        Guid? expectedCurrentPrimaryId,
        string operatorReference,
        bool apply,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(operatorReference))
            throw new ArgumentException("A maintenance decision requires an operator reference so the audit record names who asked for it.", nameof(operatorReference));
        if (choice is not (AeroLinkUpgradeConflict.ChoiceGrantAndKeep or AeroLinkUpgradeConflict.ChoiceRetireBackup))
            return new AeroLinkResolutionResult(false, AeroLinkResolutionResult.ChoiceRefusedOutcome,
                $"'{choice}' is not a supported decision for a legacy Project Leadership backup conflict.", []);

        var requiredRole = ProjectLeadership.RequiredBaseRole(position);
        var strategy = db.Database.CreateExecutionStrategy();
        AeroLinkResolutionResult? result = null;

        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            // Re-read inside the transaction. Everything below is the state at the moment of the write, not
            // the state the analysis saw, which may be minutes or days old.
            var legacy = await db.ProjectRoleBackups
                .SingleOrDefaultAsync(x => x.Id == legacyBackupId, ct);
            if (legacy is null || legacy.RemovedAt is not null || legacy.ProgramId != programId || legacy.BackupUserId != personId)
            {
                result = await RefuseAsync(programId, position, choice, operatorReference,
                    "The legacy standing-backup row named by the decision is no longer the active row it was analyzed as. Re-run the analysis.", apply, ct);
                await transaction.CommitAsync(ct);
                return;
            }
            if (ProjectLeadership.PositionForGovernedRole(legacy.Role) != position)
            {
                result = await RefuseAsync(programId, position, choice, operatorReference,
                    $"The legacy row's role {legacy.Role} does not map to {position}. Re-run the analysis.", apply, ct);
                await transaction.CommitAsync(ct);
                return;
            }

            var currentPrimaryId = await db.ProjectLeadershipAssignments.AsNoTracking()
                .Where(x => x.ProgramId == programId && x.Position == position && x.EndedAt == null)
                .Select(x => (Guid?)x.HolderUserId).FirstOrDefaultAsync(ct);
            if (currentPrimaryId != expectedCurrentPrimaryId)
            {
                result = await RefuseAsync(programId, position, choice, operatorReference,
                    "The position's active primary changed after the conflict was analyzed. Nothing was written; re-run the analysis.", apply, ct);
                await transaction.CommitAsync(ct);
                return;
            }

            var holdsRequiredRole = await db.ProgramMemberships.AsNoTracking().AnyAsync(x =>
                x.UserId == personId && x.ProgramId == programId && x.Role == requiredRole && x.EndedAt == null, ct);

            var now = DateTimeOffset.UtcNow;
            var changes = new List<string>();

            if (choice == AeroLinkUpgradeConflict.ChoiceGrantAndKeep)
            {
                if (holdsRequiredRole)
                {
                    result = await RefuseAsync(programId, position, choice, operatorReference,
                        $"The person already holds the required {requiredRole} base role, so this conflict no longer exists. Re-run the analysis.", apply, ct);
                    await transaction.CommitAsync(ct);
                    return;
                }
                if (currentPrimaryId == personId)
                {
                    result = await RefuseAsync(programId, position, choice, operatorReference,
                        "The person is the position's active primary and cannot also be its standing backup. Re-run the analysis.", apply, ct);
                    await transaction.CommitAsync(ct);
                    return;
                }
                changes.Add($"Grant {requiredRole} on program {programId} to {personId}.");
                changes.Add($"Migrate legacy {legacy.Role} standing backup {legacyBackupId} to the {position} leadership backup for {personId}.");
                changes.Add($"Retire legacy standing backup {legacyBackupId}, preserving it as ended history.");
                if (apply)
                {
                    db.ProgramMemberships.Add(new ProgramMembership(personId, programId, requiredRole,
                        AeroLinkMaintenanceAttribution.Actor, now));
                    if (!await db.ProjectLeadershipBackups.AsNoTracking().AnyAsync(x =>
                            x.ProgramId == programId && x.Position == position
                            && x.BackupUserId == personId && x.RemovedAt == null, ct))
                        db.ProjectLeadershipBackups.Add(new ProjectLeadershipBackup(programId, position, personId,
                            AeroLinkMaintenanceAttribution.Actor, now));
                    legacy.Remove(AeroLinkMaintenanceAttribution.Actor, now);
                }
            }
            else
            {
                changes.Add($"Retire legacy {legacy.Role} standing backup {legacyBackupId} for {personId}, preserving it as ended history.");
                if (apply) legacy.Remove(AeroLinkMaintenanceAttribution.Actor, now);
            }

            if (!apply)
            {
                result = new AeroLinkResolutionResult(false, AeroLinkResolutionResult.DryRunOutcome,
                    "This is what the decision would do. Nothing was written.", changes);
                await transaction.CommitAsync(ct);
                return;
            }

            db.SecurityAuditEvents.Add(new SecurityAuditEvent(
                AeroLinkMaintenanceAttribution.DecisionEvent,
                AeroLinkMaintenanceAttribution.Actor,
                "project-leadership",
                "Success",
                JsonSerializer.Serialize(new
                {
                    conflict = AeroLinkUpgradeConflict.LegacyBackupIneligibleCode,
                    choice,
                    programId,
                    position = position.ToString(),
                    personId,
                    legacyBackupId,
                    requiredBaseRole = requiredRole.ToString(),
                    expectedCurrentPrimaryId,
                    operatorReference,
                    changes,
                }),
                AeroLinkMaintenanceAttribution.Source,
                now));
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            result = new AeroLinkResolutionResult(true, AeroLinkResolutionResult.AppliedOutcome,
                "The decision was applied and recorded. Re-run the analysis to confirm the upgrade posture.", changes);
        });

        return result!;
    }

    /// <summary>
    /// Records a refusal, so a decision that could not be applied leaves evidence rather than only a console
    /// message. The refusal itself writes nothing to the rows under discussion.
    /// </summary>
    private async Task<AeroLinkResolutionResult> RefuseAsync(
        Guid programId, ProjectLeadershipPosition position, string choice, string operatorReference,
        string reason, bool apply, CancellationToken ct)
    {
        if (apply)
        {
            db.SecurityAuditEvents.Add(new SecurityAuditEvent(
                AeroLinkMaintenanceAttribution.RefusedEvent,
                AeroLinkMaintenanceAttribution.Actor,
                "project-leadership",
                "Refused",
                JsonSerializer.Serialize(new { choice, programId, position = position.ToString(), operatorReference, reason }),
                AeroLinkMaintenanceAttribution.Source,
                DateTimeOffset.UtcNow));
            await db.SaveChangesAsync(ct);
        }
        return new AeroLinkResolutionResult(false, AeroLinkResolutionResult.PreconditionFailedOutcome, reason, []);
    }
}
