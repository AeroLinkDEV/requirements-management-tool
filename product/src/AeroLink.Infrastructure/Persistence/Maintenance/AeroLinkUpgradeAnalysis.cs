namespace AeroLink.Infrastructure.Persistence.Maintenance;

/// <summary>
/// The formal attribution every maintenance mutation carries.
///
/// Before this existed, the 2026-08-31 #816 repair had to invent an actor string in ad-hoc SQL because the
/// API could not start and no supported path existed. An audit trail whose actor is whatever the person at
/// the keyboard typed that day is not an audit trail. These constants are the one supported answer, so a
/// maintenance decision is distinguishable from a migration, from a user action, and from a seeder — years
/// later, by somebody who was not there.
/// </summary>
public static class AeroLinkMaintenanceAttribution
{
    /// <summary>The actor recorded on rows a supported operator maintenance decision changed.</summary>
    public const string Actor = "aerolink-maintenance";

    /// <summary>The security-audit event type for a maintenance decision that changed persistent state.</summary>
    public const string DecisionEvent = "AeroLinkMaintenance.Decision";

    /// <summary>
    /// The security-audit event type a refused maintenance decision would have carried. Retained so an
    /// installation that recorded one before #881''s zero-write correction can still be queried for it.
    ///
    /// Nothing writes this any more, and that is deliberate: #881 requires a stale or conflicting
    /// precondition to cause no write, and an audit row is a write.
    /// </summary>
    public const string RefusedEvent = "AeroLinkMaintenance.Refused";

    /// <summary>The audit source recorded for a local operator-run maintenance host.</summary>
    public const string Source = "local-maintenance";
}

/// <summary>One supported operator decision for a conflict, and whether taking it grants new authority.</summary>
/// <param name="Key">The exact value an operator passes back to act on this choice.</param>
/// <param name="Description">What taking this choice does, in the operator's terms.</param>
/// <param name="GrantsNewAuthority">
/// True when taking this choice would give somebody authority they do not have today. AeroLink never selects
/// such a choice on its own, and the resolver requires it to be named explicitly.
/// </param>
public sealed record AeroLinkUpgradeChoice(string Key, string Description, bool GrantsNewAuthority);

/// <summary>
/// One thing this database cannot be upgraded past without a human deciding, in a shape a program can read.
///
/// The #816 incident surfaced as a .NET exception whose message was a bulleted string, after a seventy-five
/// second wait. The information was all there; nothing could act on it. A conflict is therefore a record with
/// an identity (<paramref name="Code"/>), the exact rows it is about (<paramref name="Subject"/>), and the
/// decisions that would clear it (<paramref name="Choices"/>) — the same object rendered for a person and
/// consumed by the resolver.
/// </summary>
public sealed record AeroLinkUpgradeConflict(
    string Code,
    string Authority,
    string Summary,
    IReadOnlyDictionary<string, string?> Subject,
    IReadOnlyList<AeroLinkUpgradeChoice> Choices)
{
    /// <summary>The legacy standing backup for a position whose holder lacks the position's required base role (#816 / Avery Chen).</summary>
    public const string LegacyBackupIneligibleCode = "project-leadership.legacy-backup-base-role-missing";

    /// <summary>Legacy standing backups mapping to one position name different people.</summary>
    public const string LegacyBackupAmbiguousCode = "project-leadership.legacy-backup-ambiguous";

    /// <summary>The legacy standing backup names the position's active primary.</summary>
    public const string LegacyBackupIsPrimaryCode = "project-leadership.legacy-backup-is-primary";

    /// <summary>The legacy standing backup and the current leadership backup name different people.</summary>
    public const string LegacyBackupSupersededCode = "project-leadership.legacy-backup-superseded";

    /// <summary>An active legacy position membership with no leadership assignment to take over from it.</summary>
    public const string LegacyMembershipUnassignedCode = "project-leadership.legacy-membership-unassigned";

    /// <summary>An active legacy position membership held by somebody who does not hold the position.</summary>
    public const string LegacyMembershipStrandedCode = "project-leadership.legacy-membership-stranded";

    /// <summary>Active Project Engineer and Project Engineering Lead memberships held by different people.</summary>
    public const string ProjectEngineerContestedCode = "project-leadership.project-engineer-contested";

    /// <summary>Choice key: grant the required base role and keep the standing backup.</summary>
    public const string ChoiceGrantAndKeep = "grant-required-role-and-keep-backup";

    /// <summary>Choice key: retire the legacy standing backup, preserving its history.</summary>
    public const string ChoiceRetireBackup = "retire-legacy-backup";
}

/// <summary>
/// The state of one semantic upgrade authority against this database.
///
/// <paramref name="Completed"/> is deliberately nullable. On a database with schema migrations still pending
/// the markers these authorities write live in tables that do not exist yet, so the honest answer is "not
/// knowable from here" — null — rather than false. Reporting unknown as not-completed made the launcher print
/// a pending count that was fabricated, on a database that may have completed every one of them years ago.
/// </summary>
public sealed record AeroLinkSemanticUpgradeState(string Marker, string Target, bool? Completed);

/// <summary>
/// Everything an operator or a launcher needs to know about this database before starting a web server.
///
/// <paramref name="DatabaseModified"/> is part of the contract, not a courtesy: the first question after a
/// refusal is always "did it change anything?", and analysis must be able to answer "no" as a fact rather
/// than as a claim. The analyzer only reads.
/// </summary>
public sealed record AeroLinkUpgradeAnalysis(
    bool DatabaseReachable,
    string? UnreachableReason,
    string? DatabaseName,
    IReadOnlyList<string> PendingEfMigrations,
    IReadOnlyList<AeroLinkSemanticUpgradeState> SemanticUpgrades,
    IReadOnlyList<AeroLinkUpgradeConflict> Conflicts,
    bool DatabaseModified)
{
    /// <summary>
    /// Semantic upgrades known to be outstanding. Authorities whose state could not be read are not counted:
    /// an operator told "5 semantic upgrades pending" must be able to trust the number.
    /// </summary>
    public IReadOnlyList<AeroLinkSemanticUpgradeState> PendingSemanticUpgrades =>
        [.. SemanticUpgrades.Where(x => x.Completed == false)];

    /// <summary>True when schema work has to happen before the semantic posture can be read at all.</summary>
    public bool SemanticPostureUnknown => SemanticUpgrades.Any(x => x.Completed is null);

    /// <summary>True when starting current code against this database would write to it.</summary>
    public bool UpgradeRequired => PendingEfMigrations.Count > 0 || PendingSemanticUpgrades.Count > 0;

    /// <summary>
    /// True when the pending work is deterministic — it needs no human decision and may proceed through the
    /// backup and clone-validation path.
    /// </summary>
    public bool DeterministicUpgrade => UpgradeRequired && Conflicts.Count == 0;

    /// <summary>The single word a launcher branches on.</summary>
    public string Status =>
        !DatabaseReachable ? "unreachable"
        : Conflicts.Count > 0 ? "conflict"
        : UpgradeRequired ? "upgrade-required"
        : "current";
}
