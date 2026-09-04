using AeroLink.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using System.Data;
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

        try
        {
        await strategy.ExecuteAsync(async () =>
        {
            // Serializable, not the Read Committed default, and the reason is the whole point of this method.
            //
            // Re-deriving the conflict inside the transaction proves the state at the moment it is read. Under
            // Read Committed that proof expires immediately: AnalyzeConflictsAsync takes no locks, so another
            // maintenance process - or an ordinary user changing a leadership assignment in the application -
            // can move the primary, the base role, or the legacy backup between this read and the write below,
            // and the decision then commits against state nobody validated. "Exact preconditions" has to mean
            // exact at commit, not exact at query time.
            //
            // PostgreSQL implements this with predicate locks and aborts the loser with a serialization
            // failure, which is what should happen: an aborted transaction writes nothing, and nothing written
            // is the same answer the resolver gives to any other stale precondition. SQLite, used by the
            // in-memory tests, is serializable already.
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);

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
        }
        catch (Exception exception) when (IsSerializationFailure(exception))
        {
            return Refuse("Another writer changed the leadership state this decision depends on while it was being applied, so the transaction was aborted and nothing was written. Re-run the analysis and act on the conflict that exists now.");
        }

        return result!;
    }

    /// <summary>
    /// A serialization failure is a stale precondition that PostgreSQL noticed for us, so it gets the same
    /// answer as every other stale precondition: nothing written, and go and look again.
    ///
    /// It is deliberately not retried. Retrying would re-derive the conflict and could apply the decision
    /// against state the operator has not seen — which is the exact thing the exact-precondition contract
    /// exists to prevent. 40001 is serialization_failure and 40P01 is deadlock_detected; both mean the
    /// transaction was aborted and no row survives it.
    /// </summary>
    private static bool IsSerializationFailure(Exception exception) =>
        exception is PostgresException { SqlState: "40001" or "40P01" }
        || (exception.InnerException is not null && IsSerializationFailure(exception.InnerException));

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
