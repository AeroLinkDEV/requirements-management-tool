using AeroLink.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>
/// Completes the part of the #816 authority migration that cannot run in schema SQL: reading the legacy
/// singular memberships and naming the first primary holder of each of the eight Project Leadership
/// positions, with the base-role eligibility the position requires.
///
/// Deliberate decisions, all recorded in product/docs/AUTHORITY_CHARACTERIZATION.md:
///
/// - A discipline-lead membership whose holder lacks the position's base role has that base role granted
///   by this migration, attributed to `aerolink-migration`. The lead membership itself is the
///   unambiguous evidence of the discipline; hiding the derivation would look like the person always held
///   both, and an audit would be unable to tell the difference.
/// - `ProjectEngineeringLead` retires. Its holder becomes the primary of the Project Engineer position,
///   receiving the Project Engineer base role through the same named derivation — unless a different
///   person already holds an active Project Engineer membership, in which case the migration REFUSES and
///   startup fails: two different people cannot silently lose an accountability the old model gave both.
/// - Where several active memberships of one singular role exist (legacy data predating the singular
///   grant check), the earliest-granted membership names the primary; the others remain base-role members
///   without leadership authority. Deterministic and attributable, never arbitrary.
/// - Role-keyed standing backups of the four discipline-lead roles migrate to the position when their
///   holder satisfies the eligibility; a `ProjectEngineeringLead` backup deliberately does not migrate —
///   it keeps answering legacy demands through the old path until replaced.
///
/// Idempotent: a completed run is recorded by a marker audit event, and the migration refuses with no
/// partial state if the conflict rule fires. SQLite development databases seed fresh state and never run
/// the migration; the persistent developer database and every real deployment upgrade through here.
/// </summary>
public sealed class ProjectLeadershipMigrationAuthority(AeroLinkDbContext db)
{
    public const string MigrationMarker = "AuthorityMigration.ProjectLeadership.v1";
    private const string CompletedEvent = MigrationMarker + ".Completed";
    private const string Actor = "aerolink-migration";

    private static readonly ProgramRole[] LegacyLeadRoles =
    [
        ProgramRole.ProjectEngineer, ProgramRole.ProgramManager, ProgramRole.EngineeringManager,
        ProgramRole.ConfigurationManager, ProgramRole.ProjectEngineeringLead,
        ProgramRole.SystemEngineeringLead, ProgramRole.SoftwareEngineeringLead,
        ProgramRole.SystemTestLead, ProgramRole.SoftwareTestLead,
    ];

    public async Task EnsureCompletedAsync(CancellationToken ct = default)
    {
        if (!db.Database.IsNpgsql()) return;
        if (await db.SecurityAuditEvents.AsNoTracking().AnyAsync(x => x.EventType == CompletedEvent, ct)) return;
        await BackfillAsync(ct);
        db.SecurityAuditEvents.Add(new SecurityAuditEvent(CompletedEvent, Actor, "project-leadership",
            "Success", "Project Leadership assignments and backups migrated from legacy singular memberships.",
            "local", DateTimeOffset.UtcNow));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Provider-agnostic backfill core, so disposable qualification can exercise it directly.</summary>
    public async Task BackfillAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var programIds = await db.Programs.AsNoTracking().Select(x => x.Id).ToListAsync(ct);
        foreach (var programId in programIds)
        {
            var memberships = await db.ProgramMemberships
                .Where(x => x.ProgramId == programId && x.EndedAt == null && LegacyLeadRoles.Contains(x.Role))
                .OrderBy(x => x.GrantedAt).ThenBy(x => x.UserId)
                .ToListAsync(ct);

            foreach (var position in ProjectLeadership.All)
            {
                if (await db.ProjectLeadershipAssignments.AnyAsync(
                        x => x.ProgramId == programId && x.Position == position && x.EndedAt == null, ct))
                    continue;

                var requiredRole = ProjectLeadership.RequiredBaseRole(position);
                if (position == ProjectLeadershipPosition.ProjectEngineer)
                {
                    var projectEngineers = memberships.Where(x => x.Role == ProgramRole.ProjectEngineer).ToList();
                    var engineeringLeads = memberships.Where(x => x.Role == ProgramRole.ProjectEngineeringLead).ToList();
                    if (projectEngineers.Count > 0 && engineeringLeads.Count > 0
                        && projectEngineers[0].UserId != engineeringLeads[0].UserId)
                        throw new InvalidOperationException(
                            "Conflicting legacy authority: this program has active Project Engineer and " +
                            "Project Engineering Lead memberships held by different people. The #816 model has " +
                            "one Project Engineer leadership position; resolve which person holds it explicitly " +
                            "(end the other membership) and restart, rather than letting the upgrade choose.");
                    var holder = engineeringLeads.Count > 0 ? engineeringLeads[0] : projectEngineers.FirstOrDefault();
                    if (holder is null) continue;
                    // The named derivation: the retiring ProjectEngineeringLead membership is the evidence the
                    // holder performs the Project Engineer's job, so the base role is granted here and now.
                    if (!await db.ProgramMemberships.AnyAsync(x => x.UserId == holder.UserId
                            && x.ProgramId == programId && x.Role == ProgramRole.ProjectEngineer && x.EndedAt == null, ct))
                        db.ProgramMemberships.Add(new ProgramMembership(holder.UserId, programId, ProgramRole.ProjectEngineer, Actor, now));
                    db.ProjectLeadershipAssignments.Add(new ProjectLeadershipAssignment(programId, position, holder.UserId, Actor, now));
                    continue;
                }

                var legacyRole = position switch
                {
                    ProjectLeadershipPosition.ProgramManager => ProgramRole.ProgramManager,
                    ProjectLeadershipPosition.EngineeringManager => ProgramRole.EngineeringManager,
                    ProjectLeadershipPosition.ConfigurationManager => ProgramRole.ConfigurationManager,
                    ProjectLeadershipPosition.SystemEngineeringLead => ProgramRole.SystemEngineeringLead,
                    ProjectLeadershipPosition.SoftwareEngineeringLead => ProgramRole.SoftwareEngineeringLead,
                    ProjectLeadershipPosition.SystemTestLead => ProgramRole.SystemTestLead,
                    ProjectLeadershipPosition.SoftwareTestLead => ProgramRole.SoftwareTestLead,
                    _ => throw new InvalidOperationException("Unhandled position."),
                };
                var leadMembership = memberships.FirstOrDefault(x => x.Role == legacyRole);
                if (leadMembership is null) continue;

                // The named derivation for the discipline leads: the lead membership is the evidence of the
                // discipline, so the base role it requires is granted when the legacy data lacks it.
                var baseRole = ProjectLeadership.RequiredBaseRole(position);
                if (!await db.ProgramMemberships.AnyAsync(x => x.UserId == leadMembership.UserId
                        && x.ProgramId == programId && x.Role == baseRole && x.EndedAt == null, ct))
                    db.ProgramMemberships.Add(new ProgramMembership(leadMembership.UserId, programId, baseRole, Actor, now));

                db.ProjectLeadershipAssignments.Add(new ProjectLeadershipAssignment(programId, position, leadMembership.UserId, Actor, now));
            }

            // Role-keyed backups of the four discipline-lead roles migrate to the position when the named
            // person satisfies the eligibility. A ProjectEngineeringLead backup deliberately stays behind:
            // it keeps answering legacy PEL demands through the old path until the position is replaced.
            var legacyBackups = await db.ProjectRoleBackups
                .Where(x => x.ProgramId == programId && x.RemovedAt == null
                    && (x.Role == ProgramRole.SystemEngineeringLead || x.Role == ProgramRole.SoftwareEngineeringLead
                        || x.Role == ProgramRole.SystemTestLead || x.Role == ProgramRole.SoftwareTestLead))
                .ToListAsync(ct);
            foreach (var legacyBackup in legacyBackups)
            {
                var position = legacyBackup.Role switch
                {
                    ProgramRole.SystemEngineeringLead => ProjectLeadershipPosition.SystemEngineeringLead,
                    ProgramRole.SoftwareEngineeringLead => ProjectLeadershipPosition.SoftwareEngineeringLead,
                    ProgramRole.SystemTestLead => ProjectLeadershipPosition.SystemTestLead,
                    _ => ProjectLeadershipPosition.SoftwareTestLead,
                };
                if (await db.ProjectLeadershipBackups.AnyAsync(
                        x => x.ProgramId == programId && x.Position == position && x.RemovedAt == null, ct))
                    continue;
                var baseRole = ProjectLeadership.RequiredBaseRole(position);
                if (!await db.ProgramMemberships.AnyAsync(x => x.UserId == legacyBackup.BackupUserId
                        && x.ProgramId == programId && x.Role == baseRole && x.EndedAt == null, ct))
                    continue;
                db.ProjectLeadershipBackups.Add(new ProjectLeadershipBackup(programId, position, legacyBackup.BackupUserId, Actor, now));
            }

            await db.SaveChangesAsync(ct);
        }
    }
}
