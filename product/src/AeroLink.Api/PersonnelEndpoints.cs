using AeroLink.Domain.Identity;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Api;

/// <summary>
/// Who is on a project, what position they hold, and who acts for them.
///
/// Membership administration already existed, but only as a global console organised by user: an operator
/// answered "which projects is this person on", never "who is on this project". A Program Manager could not
/// see their own team, because every route required the single <c>admin</c> account. These routes are the
/// project's own view of the same records, with authority a project can actually hold.
///
/// Nothing here deletes. Ending a role keeps the row and stamps who ended it, so the roster can still answer
/// what it was during a period that has already closed.
/// </summary>
public static class PersonnelEndpoints
{
    /// <summary>
    /// Who may change a project's roster. Administrator is the Program-scoped role, not the global account —
    /// that one satisfies every check already, inside <c>IdentityService</c>.
    /// </summary>
    private static readonly ProgramRole[] RosterAuthority =
    [
        ProgramRole.ProgramManager, ProgramRole.ProjectEngineeringLead,
        ProgramRole.ProjectEngineer, ProgramRole.Administrator
    ];

    public static void MapPersonnelEndpoints(this WebApplication app)
    {
        app.MapGet("/api/projects/{projectId:guid}/personnel", async (Guid projectId, HttpContext http,
            AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
        {
            if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
            var programId = await ProgramOfAsync(db, projectId, ct);
            if (programId is null) return Results.NotFound();

            var canManage = await http.HasProjectRoleAsync(db, identity, projectId, ct, RosterAuthority);
            var memberships = await db.ProgramMemberships.AsNoTracking()
                .Where(x => x.ProgramId == programId).ToListAsync(ct);
            var backups = await db.ProjectRoleBackups.AsNoTracking()
                .Where(x => x.ProgramId == programId && x.RemovedAt == null).ToListAsync(ct);

            var userIds = memberships.Select(x => x.UserId).Concat(backups.Select(x => x.BackupUserId)).Distinct().ToList();
            var accounts = await db.UserAccounts.AsNoTracking().Where(x => userIds.Contains(x.Id))
                .Select(x => new { x.Id, x.UserName, x.DisplayName, x.Email, x.State }).ToListAsync(ct);
            var byId = accounts.ToDictionary(x => x.Id);

            object? Person(Guid id) => byId.TryGetValue(id, out var a)
                ? new { userId = a.Id, userName = a.UserName, displayName = a.DisplayName }
                : null;

            var backupByRole = backups.ToDictionary(x => x.Role, x => x);

            // Every singular position is reported, held or not. A position nobody holds is the answer somebody
            // came to this page for, so it cannot be represented by absence from a list.
            var positions = SingularProgramRoles.All.Select(role =>
            {
                var holder = memberships.FirstOrDefault(x => x.Role == role && x.EndedAt == null);
                return new
                {
                    role = role.ToString(),
                    holder = holder is null ? null : Person(holder.UserId),
                    heldSince = holder?.GrantedAt,
                    backup = backupByRole.TryGetValue(role, out var b) ? Person(b.BackupUserId) : null,
                };
            }).ToList();

            var members = memberships
                .GroupBy(x => x.UserId)
                .Select(group =>
                {
                    var account = byId.GetValueOrDefault(group.Key);
                    var active = group.Where(x => x.EndedAt == null).ToList();
                    var ended = group.Where(x => x.EndedAt != null).ToList();
                    return new
                    {
                        userId = group.Key,
                        userName = account?.UserName ?? "",
                        displayName = account?.DisplayName ?? "Unknown user",
                        email = account?.Email ?? "",
                        accountDisabled = account is not null && account.State != AccountState.Active,
                        roles = active.Select(x => x.Role.ToString()).Order().ToList(),
                        endedRoles = ended.Select(x => x.Role.ToString()).Order().ToList(),
                        backsUp = backups.Where(x => x.BackupUserId == group.Key).Select(x => x.Role.ToString()).Order().ToList(),
                        joinedAt = group.Min(x => x.GrantedAt),
                        leftAt = active.Count == 0 ? ended.Max(x => x.EndedAt) : null,
                        isCurrent = active.Count > 0,
                    };
                })
                .OrderByDescending(x => x.isCurrent).ThenBy(x => x.displayName)
                .ToList();

            return Results.Ok(new { projectId, canManage, positions, members });
        });

        // Accounts that could be added. Deliberately excludes current members so the picker cannot offer
        // somebody who is already here, and excludes disabled accounts so a lead cannot staff a project with
        // people who can no longer sign in.
        app.MapGet("/api/projects/{projectId:guid}/personnel/candidates", async (Guid projectId, HttpContext http,
            AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
        {
            if (!await http.HasProjectRoleAsync(db, identity, projectId, ct, RosterAuthority)) return Results.Forbid();
            var programId = await ProgramOfAsync(db, projectId, ct);
            if (programId is null) return Results.NotFound();
            var current = await db.ProgramMemberships.AsNoTracking()
                .Where(x => x.ProgramId == programId && x.EndedAt == null).Select(x => x.UserId).Distinct().ToListAsync(ct);
            var candidates = await db.UserAccounts.AsNoTracking()
                .Where(x => x.State == AccountState.Active && !current.Contains(x.Id))
                .OrderBy(x => x.DisplayName)
                .Select(x => new { userId = x.Id, userName = x.UserName, displayName = x.DisplayName, x.Email })
                .ToListAsync(ct);
            return Results.Ok(candidates);
        });

        app.MapPost("/api/projects/{projectId:guid}/personnel", async (Guid projectId, AddProjectMemberRequest request,
            HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
        {
            var actor = http.UserAccount();
            if (!await http.HasProjectRoleAsync(db, identity, projectId, ct, RosterAuthority)) return Results.Forbid();
            // Project leadership staffs its own project. It does not mint project administrators — that stays
            // with the global account, so nobody can promote themselves out of the authority they were given.
            if (request.Role == ProgramRole.Administrator && !actor.IsAdministrator)
                return Results.Forbid();
            // #816: ProjectEngineeringLead is retired. Historical rows stay readable, but no new grant may
            // resurrect a parallel accountability the Project Engineer leadership position now owns.
            if (request.Role == ProgramRole.ProjectEngineeringLead)
                return Results.Conflict(new { error = "Project Engineering Lead is retired. Assign the Project Engineer leadership position instead." });
            var programId = await ProgramOfAsync(db, projectId, ct);
            if (programId is null) return Results.NotFound();
            if (!await db.UserAccounts.AnyAsync(x => x.Id == request.UserId && x.State == AccountState.Active, ct))
                return Results.BadRequest(new { error = "That person does not have an active AeroLink account." });
            if (await db.ProgramMemberships.AnyAsync(x => x.UserId == request.UserId && x.ProgramId == programId && x.Role == request.Role && x.EndedAt == null, ct))
                return Results.Conflict(new { error = "They already hold that position on this project." });
            if (SingularProgramRoles.IsSingular(request.Role))
            {
                var holder = await db.ProgramMemberships.AsNoTracking()
                    .Where(x => x.ProgramId == programId && x.Role == request.Role && x.EndedAt == null)
                    .Join(db.UserAccounts.AsNoTracking(), m => m.UserId, u => u.Id, (m, u) => u.DisplayName)
                    .FirstOrDefaultAsync(ct);
                if (holder is not null)
                    return Results.Conflict(new { error = $"{Readable(request.Role)} is held by {holder}. End their position before assigning it to somebody else." });
            }

            db.ProgramMemberships.Add(new ProgramMembership(request.UserId, programId.Value, request.Role, actor.UserName, DateTimeOffset.UtcNow));
            db.SecurityAuditEvents.Add(new("RoleGranted", actor.UserName, request.UserId.ToString(), "Success",
                $"Granted {request.Role} on project {projectId} from the project personnel page.",
                http.Connection.RemoteIpAddress?.ToString() ?? "local", DateTimeOffset.UtcNow));
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        app.MapDelete("/api/projects/{projectId:guid}/personnel/{userId:guid}/roles/{role}", async (Guid projectId,
            Guid userId, ProgramRole role, HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
        {
            var actor = http.UserAccount();
            if (!await http.HasProjectRoleAsync(db, identity, projectId, ct, RosterAuthority)) return Results.Forbid();
            if (role == ProgramRole.Administrator && !actor.IsAdministrator) return Results.Forbid();
            var programId = await ProgramOfAsync(db, projectId, ct);
            if (programId is null) return Results.NotFound();
            var membership = await db.ProgramMemberships
                .SingleOrDefaultAsync(x => x.UserId == userId && x.ProgramId == programId && x.Role == role && x.EndedAt == null, ct);
            if (membership is null) return Results.NotFound();

            var now = DateTimeOffset.UtcNow;
            membership.End(actor.UserName, now);
            await AdministrationEndpoints.EndBackupsForEndedMembershipAsync(db, userId, programId.Value, membership.Id, actor.UserName, ct);
            db.SecurityAuditEvents.Add(new("RoleRevoked", actor.UserName, userId.ToString(), "Success",
                $"Ended {role} on project {projectId} from the project personnel page.",
                http.Connection.RemoteIpAddress?.ToString() ?? "local", now));
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        app.MapPost("/api/projects/{projectId:guid}/personnel/backups", async (Guid projectId, NameBackupRequest request,
            HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
        {
            var actor = http.UserAccount();
            if (!await http.HasProjectRoleAsync(db, identity, projectId, ct, RosterAuthority)) return Results.Forbid();
            var programId = await ProgramOfAsync(db, projectId, ct);
            if (programId is null) return Results.NotFound();
            if (!await db.ProgramMemberships.AnyAsync(x => x.UserId == request.BackupUserId && x.ProgramId == programId && x.EndedAt == null, ct))
                return Results.BadRequest(new { error = "A backup has to be on this project. Add them first." });
            // Somebody who already holds the position cannot also be its cover: the point of a backup is that
            // there is a second person, and naming the holder would report cover that does not exist.
            if (await db.ProgramMemberships.AnyAsync(x => x.UserId == request.BackupUserId && x.ProgramId == programId && x.Role == request.Role && x.EndedAt == null, ct))
                return Results.BadRequest(new { error = "They already hold this position, so they cannot also be its backup." });
            if (await db.ProjectRoleBackups.AnyAsync(x => x.ProgramId == programId && x.Role == request.Role && x.RemovedAt == null, ct))
                return Results.Conflict(new { error = "That position already has a backup. Remove the current one first." });

            db.ProjectRoleBackups.Add(new ProjectRoleBackup(programId.Value, request.Role, request.BackupUserId, actor.UserName, DateTimeOffset.UtcNow));
            db.SecurityAuditEvents.Add(new("BackupNamed", actor.UserName, request.BackupUserId.ToString(), "Success",
                $"Named a standing backup for {request.Role} on project {projectId}. The backup may act in that role until removed.",
                http.Connection.RemoteIpAddress?.ToString() ?? "local", DateTimeOffset.UtcNow));
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        app.MapDelete("/api/projects/{projectId:guid}/personnel/backups/{role}", async (Guid projectId, ProgramRole role,
            HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
        {
            var actor = http.UserAccount();
            if (!await http.HasProjectRoleAsync(db, identity, projectId, ct, RosterAuthority)) return Results.Forbid();
            var programId = await ProgramOfAsync(db, projectId, ct);
            if (programId is null) return Results.NotFound();
            var backup = await db.ProjectRoleBackups.SingleOrDefaultAsync(x => x.ProgramId == programId && x.Role == role && x.RemovedAt == null, ct);
            if (backup is null) return Results.NotFound();
            backup.Remove(actor.UserName, DateTimeOffset.UtcNow);
            db.SecurityAuditEvents.Add(new("BackupRemoved", actor.UserName, backup.BackupUserId.ToString(), "Success",
                $"Removed the standing backup for {role} on project {projectId}.",
                http.Connection.RemoteIpAddress?.ToString() ?? "local", DateTimeOffset.UtcNow));
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }

    private static async Task<Guid?> ProgramOfAsync(AeroLinkDbContext db, Guid projectId, CancellationToken ct) =>
        await db.Projects.AsNoTracking().Where(x => x.Id == projectId).Select(x => (Guid?)x.ProgramId).SingleOrDefaultAsync(ct);

    private static string Readable(ProgramRole role) => role switch
    {
        ProgramRole.ConfigurationManager => "Configuration Manager",
        ProgramRole.ProgramManager => "Program Manager",
        ProgramRole.EngineeringManager => "Engineering Manager",
        ProgramRole.ProjectEngineer => "Project Engineer",
        ProgramRole.ProjectEngineeringLead => "Project Engineering Lead",
        ProgramRole.SystemEngineeringLead => "System Engineering Lead",
        ProgramRole.SoftwareEngineeringLead => "Software Engineering Lead",
        ProgramRole.SystemTestLead => "System Test Lead",
        ProgramRole.SoftwareTestLead => "Software Test Lead",
        _ => role.ToString(),
    };
}

public sealed record AddProjectMemberRequest(Guid UserId, ProgramRole Role);
public sealed record NameBackupRequest(Guid BackupUserId, ProgramRole Role);
