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
///   * The conflict is RE-DERIVED inside the transaction, from the same authority that reported it, and the
///     caller's conflict code, subject and choice must all match what actually exists. Taking the caller's
///     conflict code as truth was a real defect: a decision could be applied under a code naming a different
///     conflict from the one present, and the audit would then faithfully record the wrong one.
///   * Preconditions are exact and re-read inside the transaction immediately before the write. The row id,
///     the person, the position, the base roles they hold, and who the current primary is must all still be
///     what the analysis reported.
///   * History is ended, never deleted. A retired backup keeps its NamedBy/NamedAt and gains RemovedBy/
///     RemovedAt, so "who was standing cover in March" stays answerable.
///   * An applied decision writes a maintenance audit event with the formal attribution. A REFUSAL writes
///     nothing at all: #881 says a stale or conflicting precondition causes no write, and an audit row is a
///     write. The refusal is reported to the operator instead, which is where they are looking.
/// </summary>
public sealed class ProjectLeadershipMaintenanceResolver(
    AeroLinkDbContext db,
    ProjectLeadershipReconciliationAuthority reconciliation)
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
    /// <param name="conflictCode">
    /// The conflict the operator reviewed. Re-derived and matched against the conflict that actually exists
    /// for this legacy row before anything is written, and recorded on the audit event. Several conflicts
    /// share this resolution path, so a decision applied under a code naming a different conflict would put
    /// an untrue record in the audit trail — which is the one thing this path exists to be better at than SQL.
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
        string conflictCode = AeroLinkUpgradeConflict.LegacyBackupIneligibleCode,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(operatorReference))
            throw new ArgumentException("A maintenance decision requires an operator reference so the audit record names who asked for it.", nameof(operatorReference));
        if (string.IsNullOrWhiteSpace(conflictCode))
            throw new ArgumentException("A maintenance decision must name the conflict it resolves.", nameof(conflictCode));
        if (choice is not (AeroLinkUpgradeConflict.ChoiceGrantAndKeep or AeroLinkUpgradeConflict.ChoiceRetireBackup))
            return new AeroLinkResolutionResult(false, AeroLinkResolutionResult.ChoiceRefusedOutcome,
                $"'{choice}' is not a supported decision for a legacy Project Leadership backup conflict.", []);

        var requiredRole = ProjectLeadership.RequiredBaseRole(position);
        var strategy = db.Database.CreateExecutionStrategy();
        AeroLinkResolutionResult? result = null;

        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            // Re-derive the conflict from the authority that reports it, inside the transaction, and require
            // the decision to be about the conflict that actually exists right now. Everything below this
            // point is acting on state the resolver has just confirmed for itself rather than on the
            // caller's description of it.
            var liveConflicts = await reconciliation.AnalyzeConflictsAsync(ct);
            var actual = liveConflicts.FirstOrDefault(x =>
                x.Subject.TryGetValue("legacyBackupId", out var id) && id == legacyBackupId.ToString());
            if (actual is null)
            {
                result = Refuse("No modelled Project Leadership conflict exists for that legacy standing backup any more. Nothing was written; re-run the analysis.");
                await transaction.RollbackAsync(ct);
                return;
            }
            if (actual.Code != conflictCode)
            {
                result = Refuse($"The conflict on that legacy standing backup is {actual.Code}, not the {conflictCode} the decision names. Nothing was written; re-run the analysis and act on the conflict that exists.");
                await transaction.RollbackAsync(ct);
                return;
            }
            if (actual.Subject.GetValueOrDefault("programId") != programId.ToString()
                || actual.Subject.GetValueOrDefault("position") != position.ToString()
                || actual.Subject.GetValueOrDefault("personId") != personId.ToString())
            {
                result = Refuse("The conflict that exists names a different program, position, or person than the decision does. Nothing was written; re-run the analysis.");
                await transaction.RollbackAsync(ct);
                return;
            }
            if (actual.Subject.GetValueOrDefault("currentPrimaryId") != expectedCurrentPrimaryId?.ToString())
            {
                result = Refuse("The position's active primary changed after the conflict was analyzed. Nothing was written; re-run the analysis.");
                await transaction.RollbackAsync(ct);
                return;
            }
            // The offered choices are part of the conflict, not a global list. Retiring a designation is
            // offered for every one of these; granting a role is offered only where granting is the question.
            if (!actual.Choices.Any(x => x.Key == choice))
            {
                result = new AeroLinkResolutionResult(false, AeroLinkResolutionResult.ChoiceRefusedOutcome,
                    $"'{choice}' is not one of the decisions {actual.Code} offers. Nothing was written.", []);
                await transaction.RollbackAsync(ct);
                return;
            }

            // The row to mutate, tracked. Its identity was already proved by the conflict subject above; this
            // read is for the entity, not for the decision.
            var legacy = await db.ProjectRoleBackups.SingleOrDefaultAsync(x => x.Id == legacyBackupId, ct);
            if (legacy is null || legacy.RemovedAt is not null)
            {
                result = Refuse("The legacy standing-backup row named by the decision is no longer active. Nothing was written; re-run the analysis.");
                await transaction.RollbackAsync(ct);
                return;
            }

            var now = DateTimeOffset.UtcNow;
            var changes = new List<string>();

            if (choice == AeroLinkUpgradeConflict.ChoiceGrantAndKeep)
            {
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
                    conflict = conflictCode,
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
    /// A refusal, which writes nothing.
    ///
    /// This used to record a maintenance audit row so a refused decision left evidence. That was the wrong
    /// trade: #881 says in as many words that a stale or conflicting precondition causes **no write**, the
    /// result type says "Nothing was written", and the maintenance host prints "No persistent data was
    /// changed" — while an audit row was being committed behind all three. A contract that is contradicted
    /// by the code is worse than a missing audit row, and the operator is told the reason directly.
    /// </summary>
    private static AeroLinkResolutionResult Refuse(string reason) =>
        new(false, AeroLinkResolutionResult.PreconditionFailedOutcome, reason, []);
}
