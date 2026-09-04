using AeroLink.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence.Maintenance;

/// <summary>
/// Answers "can this code run against this database, and what would it change?" without starting a web
/// server, and without writing anything.
///
/// Two real incidents motivate this. In #747 and again in #816 the operator learned that an old but perfectly
/// valid database could not be started by current code only after the launcher had installed dependencies,
/// built a client, started an API and waited out a readiness timeout — and the answer, when it came, was a
/// .NET stack trace. Everything needed to say so in two seconds was already knowable the moment PostgreSQL
/// accepted a connection.
///
/// The rule this class obeys, and the reason it is here rather than in PowerShell: it reuses the SAME
/// authorities startup runs. Semantic completion is read from the markers those authorities write, and
/// conflicts come from the authorities' own conflict analysis. A second set of SQL heuristics that "checks
/// what the upgrade would do" is a second opinion, and a second opinion about controlled data is a defect
/// waiting for a Tuesday.
/// </summary>
public sealed class AeroLinkUpgradeAnalyzer(
    AeroLinkDbContext db,
    ProjectLeadershipReconciliationAuthority leadershipReconciliation)
{
    /// <summary>
    /// The semantic upgrade authorities Program.cs runs at startup, with the audit target each records.
    ///
    /// Kept beside the startup list on purpose: if an authority is added there and not here, the analyzer
    /// under-reports, and the Infrastructure maintenance contract test asserts the two lists agree.
    /// </summary>
    public static readonly IReadOnlyList<(string Marker, string Target)> SemanticAuthorities =
    [
        (SoftwareVerificationCaseMigrationAuthority.MigrationMarker, "software-verification-identities"),
        (ProjectLeadershipMigrationAuthority.MigrationMarker, "project-leadership"),
        (ProjectLeadershipReconciliationAuthority.MigrationMarker, "project-leadership"),
        (TestChangeRequestPrefixMigrationAuthority.MigrationMarker, "test-change-request-identities"),
        (SoftwareProcedureExecutionCutoverAuthority.MigrationMarker, "software-procedure-execution-cutover"),
    ];

    /// <summary>
    /// Reads the database and reports. Every query is <c>AsNoTracking</c> and no <c>SaveChanges</c> happens
    /// on any path, so the returned <c>DatabaseModified = false</c> is a fact rather than an assurance.
    /// </summary>
    public async Task<AeroLinkUpgradeAnalysis> AnalyzeAsync(CancellationToken ct = default)
    {
        var databaseName = db.Database.GetDbConnection().Database;

        if (!await db.Database.CanConnectAsync(ct))
            return new AeroLinkUpgradeAnalysis(false,
                "The database could not be reached. Start PostgreSQL, then analyze again.",
                databaseName, [], [], [], DatabaseModified: false);

        List<string> pending;
        try { pending = [.. await db.Database.GetPendingMigrationsAsync(ct)]; }
        catch (Exception exception)
        {
            // A schema too old (or too new) to interrogate is itself the answer, and naming it beats a
            // launcher waiting on an API that can never become ready.
            return new AeroLinkUpgradeAnalysis(false,
                $"The database schema could not be interrogated by this build: {exception.Message}",
                databaseName, [], [], [], DatabaseModified: false);
        }

        // A database with schema migrations pending cannot be interrogated for semantic state: the tables
        // those markers live in may not exist yet, and asking would fail with a PostgreSQL error rather
        // than an answer. Report the schema work, and say plainly that the semantic posture is not yet
        // knowable — the clone-validation path migrates the isolated copy and asks again there, before the
        // real database is touched.
        if (pending.Count > 0)
        {
            // Completed: null, not false. These authorities may well have run years ago; claiming they are
            // outstanding would put a fabricated number in front of the operator.
            var unknown = SemanticAuthorities
                .Select(x => new AeroLinkSemanticUpgradeState(x.Marker, x.Target, Completed: null)).ToList();
            return new AeroLinkUpgradeAnalysis(true, null, databaseName, pending, unknown, [],
                DatabaseModified: false);
        }

        var completedEvents = await db.SecurityAuditEvents.AsNoTracking()
            .Where(x => x.EventType.EndsWith(".Completed"))
            .Select(x => new { x.EventType, x.Target }).ToListAsync(ct);

        var semantic = new List<AeroLinkSemanticUpgradeState>();
        foreach (var (marker, target) in SemanticAuthorities)
        {
            var completed = completedEvents.Any(x => x.EventType == marker + ".Completed" && x.Target == target);
            semantic.Add(new AeroLinkSemanticUpgradeState(marker, target, completed));
        }

        // Conflicts are analyzed in the order the authorities run, and only for the one that would run next.
        //
        // Two reasons. A completed marker makes its authority a permanent no-op for this database, so its
        // historical ambiguity cannot block anything and reporting it would send an operator to resolve
        // nothing. And v2's questions presuppose v1: before v1 backfills the assignments, every legacy lead
        // membership looks to v2 like a membership with no assignment to take over from it — a conflict the
        // upgrade itself is about to resolve. Reporting that would be a false alarm on the ordinary path.
        var conflicts = new List<AeroLinkUpgradeConflict>();
        // Reached only when no schema migration is pending, so every state below was actually read and none
        // is null; == true keeps that explicit rather than relying on it.
        var backfillCompleted = semantic.Single(x => x.Marker == ProjectLeadershipMigrationAuthority.MigrationMarker).Completed == true;
        if (!backfillCompleted)
        {
            conflicts.AddRange(await ProjectEngineerContestedAsync(ct));
        }
        else if (semantic.Single(x => x.Marker == ProjectLeadershipReconciliationAuthority.MigrationMarker).Completed != true)
        {
            conflicts.AddRange(await leadershipReconciliation.AnalyzeConflictsAsync(ct));
        }

        return new AeroLinkUpgradeAnalysis(true, null, databaseName, pending, semantic, conflicts,
            DatabaseModified: false, Showcase: await ShowcaseStateAsync(ct));
    }

    /// <summary>
    /// Which showcase upgrade steps this build knows that this database has not recorded.
    ///
    /// Called only when no schema migration is pending, because the markers live in a table an unmigrated
    /// database may not have. Reads the recorded keys and subtracts them in the seeder's own order, so the
    /// operator sees the same names the upgrade command reports back.
    /// </summary>
    private async Task<AeroLinkShowcaseUpgradeState> ShowcaseStateAsync(CancellationToken ct)
    {
        var programId = await db.Programs.AsNoTracking()
            .Where(x => x.Code == FmsShowcaseSeeder.ProgramCode)
            .Select(x => (Guid?)x.Id).FirstOrDefaultAsync(ct);
        if (programId is null)
            return new AeroLinkShowcaseUpgradeState(false, FmsShowcaseSeeder.ProgramCode, []);

        var recorded = await db.ShowcaseUpgradeSteps.AsNoTracking()
            .Where(x => x.ProgramId == programId.Value).Select(x => x.StepKey).ToListAsync(ct);
        return new AeroLinkShowcaseUpgradeState(true, FmsShowcaseSeeder.ProgramCode,
            [.. FmsShowcaseSeeder.UpgradeStepKeys.Where(x => !recorded.Contains(x))]);
    }

    /// <summary>
    /// The v1 backfill's one refusal, reported as a conflict rather than discovered as an exception: active
    /// Project Engineer and Project Engineering Lead memberships held by different people, where the #816
    /// model has exactly one Project Engineer position.
    /// </summary>
    private async Task<IReadOnlyList<AeroLinkUpgradeConflict>> ProjectEngineerContestedAsync(CancellationToken ct)
    {
        var conflicts = new List<AeroLinkUpgradeConflict>();
        var programs = await db.Programs.AsNoTracking().Select(x => new { x.Id, x.Name }).ToListAsync(ct);
        foreach (var program in programs)
        {
            if (await db.ProjectLeadershipAssignments.AsNoTracking().AnyAsync(x =>
                    x.ProgramId == program.Id
                    && x.Position == ProjectLeadershipPosition.ProjectEngineer && x.EndedAt == null, ct))
                continue;

            var memberships = await db.ProgramMemberships.AsNoTracking()
                .Where(x => x.ProgramId == program.Id && x.EndedAt == null
                    && (x.Role == ProgramRole.ProjectEngineer || x.Role == ProgramRole.ProjectEngineeringLead))
                .OrderBy(x => x.GrantedAt).ThenBy(x => x.UserId)
                .Select(x => new { x.Role, x.UserId }).ToListAsync(ct);

            var engineer = memberships.FirstOrDefault(x => x.Role == ProgramRole.ProjectEngineer);
            var lead = memberships.FirstOrDefault(x => x.Role == ProgramRole.ProjectEngineeringLead);
            if (engineer is null || lead is null || engineer.UserId == lead.UserId) continue;

            var names = await db.UserAccounts.AsNoTracking()
                .Where(x => x.Id == engineer.UserId || x.Id == lead.UserId)
                .Select(x => new { x.Id, x.DisplayName }).ToListAsync(ct);
            string Display(Guid id) => names.FirstOrDefault(x => x.Id == id)?.DisplayName ?? id.ToString();

            conflicts.Add(new AeroLinkUpgradeConflict(
                AeroLinkUpgradeConflict.ProjectEngineerContestedCode,
                ProjectLeadershipMigrationAuthority.MigrationMarker,
                $"{program.Name}: active Project Engineer ({Display(engineer.UserId)}) and Project Engineering "
                + $"Lead ({Display(lead.UserId)}) memberships are held by different people, and the #816 model "
                + "has one Project Engineer leadership position. End the membership of whoever does not hold "
                + "it, then restart, rather than letting the upgrade choose.",
                new Dictionary<string, string?>
                {
                    ["programId"] = program.Id.ToString(),
                    ["program"] = program.Name,
                    ["position"] = ProjectLeadershipPosition.ProjectEngineer.ToString(),
                    ["projectEngineerId"] = engineer.UserId.ToString(),
                    ["projectEngineer"] = Display(engineer.UserId),
                    ["projectEngineeringLeadId"] = lead.UserId.ToString(),
                    ["projectEngineeringLead"] = Display(lead.UserId),
                },
                []));
        }
        return conflicts;
    }

    /// <summary>
    /// The same analysis as human-readable operator output — the block #881 asks for in place of a stack
    /// trace. Kept next to the model so the two cannot drift, and deliberately free of stack traces,
    /// connection strings and credentials.
    /// </summary>
    public static IReadOnlyList<string> Render(AeroLinkUpgradeAnalysis analysis)
    {
        var lines = new List<string>();
        if (!analysis.DatabaseReachable)
        {
            lines.Add("DATABASE NOT AVAILABLE");
            lines.Add(analysis.UnreachableReason ?? "The database could not be reached.");
            lines.Add("No persistent data was changed.");
            return lines;
        }
        if (analysis.Conflicts.Count > 0)
        {
            lines.Add("DATABASE ATTENTION REQUIRED");
            lines.Add("");
            foreach (var conflict in analysis.Conflicts)
            {
                lines.Add($"Conflict: {conflict.Code}");
                foreach (var entry in conflict.Subject.Where(x => !string.IsNullOrWhiteSpace(x.Value) && !x.Key.EndsWith("Id")))
                    lines.Add($"  {entry.Key}: {entry.Value}");
                lines.Add($"  {conflict.Summary}");
                if (conflict.Choices.Count > 0)
                {
                    lines.Add("  Supported decisions:");
                    foreach (var choice in conflict.Choices)
                        lines.Add($"    [{choice.Key}] {choice.Description}"
                            + (choice.GrantsNewAuthority ? " (grants authority somebody does not have today)" : ""));
                }
                else
                {
                    lines.Add("  This conflict has no automated decision; resolve it in AeroLink, then analyze again.");
                }
                lines.Add("");
            }
            lines.Add("AeroLink made NO authority decision automatically.");
            lines.Add("No persistent data was changed.");
            return lines;
        }
        if (!analysis.UpgradeRequired)
        {
            lines.Add("DATABASE CURRENT");
            lines.Add($"Database: {analysis.DatabaseName}");
            lines.Add("No schema or semantic upgrade is pending.");
            lines.AddRange(RenderShowcase(analysis));
            return lines;
        }
        lines.Add("DATABASE UPGRADE REQUIRED");
        lines.Add($"Database: {analysis.DatabaseName}");
        if (analysis.PendingEfMigrations.Count > 0)
        {
            lines.Add($"Pending schema migrations ({analysis.PendingEfMigrations.Count}):");
            foreach (var migration in analysis.PendingEfMigrations) lines.Add($"  {migration}");
        }
        if (analysis.PendingSemanticUpgrades.Count > 0)
        {
            lines.Add($"Pending semantic upgrades ({analysis.PendingSemanticUpgrades.Count}):");
            foreach (var upgrade in analysis.PendingSemanticUpgrades) lines.Add($"  {upgrade.Marker}");
        }
        lines.Add("The upgrade is deterministic so far: no operator decision is required to begin it.");
        if (analysis.SemanticPostureUnknown)
            lines.Add("Semantic upgrades and conflicts cannot be assessed against a schema this build has not "
                + "migrated yet, so none is claimed here; they are assessed on the isolated validated copy "
                + "before the real database is touched.");
        lines.AddRange(RenderShowcase(analysis));
        lines.Add("No persistent data has been changed by this analysis.");
        return lines;
    }

    /// <summary>
    /// The showcase category, phrased as what it is: available, operator-initiated, and not something a
    /// restart will do. Saying "DATABASE CURRENT" and nothing else to somebody whose demo content is many
    /// steps behind the build is accurate about schema and misleading about everything they can see.
    /// </summary>
    private static IReadOnlyList<string> RenderShowcase(AeroLinkUpgradeAnalysis analysis)
    {
        if (!analysis.ShowcaseUpgradeAvailable || analysis.Showcase is null) return [];
        var lines = new List<string>
        {
            "",
            $"Showcase content upgrade available ({analysis.Showcase.PendingSteps.Count} step(s) for "
                + $"{analysis.Showcase.ProgramCode}):",
        };
        lines.AddRange(analysis.Showcase.PendingSteps.Select(x => $"  {x}"));
        lines.Add("Nothing applies these automatically: existing showcase content is operator-owned state, so "
            + "a restart will not rewrite it. Applying them can add controlled history, and the showcase "
            + "upgrade endpoint does not take a backup of its own - take one with BACKUP_AEROLINK.bat first, "
            + "then POST /api/showcase/upgrade as an administrator.");
        return lines;
    }
}
