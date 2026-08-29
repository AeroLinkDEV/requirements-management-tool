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
    public static void MapPersonnelEndpoints(this WebApplication app)
    {
        app.MapGet("/api/projects/{projectId:guid}/personnel", async (Guid projectId, HttpContext http,
            AeroLinkDbContext db, IdentityService identity, ProjectAuthorityResolver resolver, CancellationToken ct) =>
        {
            if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
            var programId = await ProgramOfAsync(db, projectId, ct);
            if (programId is null) return Results.NotFound();

            var canManage = await http.HasRosterAuthorityAsync(db, resolver, projectId, ct);
            var memberships = await db.ProgramMemberships.AsNoTracking()
                .Where(x => x.ProgramId == programId).ToListAsync(ct);
            var backups = await db.ProjectRoleBackups.AsNoTracking()
                .Where(x => x.ProgramId == programId && x.RemovedAt == null).ToListAsync(ct);
            // #816: standing backups of the eight Project Leadership positions ride alongside the legacy
            // role-keyed backups, so the roster can show who covers a leadership position.
            var leadershipBackups = await db.ProjectLeadershipBackups.AsNoTracking()
                .Where(x => x.ProgramId == programId && x.RemovedAt == null).ToListAsync(ct);

            var userIds = memberships.Select(x => x.UserId)
                .Concat(backups.Select(x => x.BackupUserId))
                .Concat(leadershipBackups.Select(x => x.BackupUserId)).Distinct().ToList();
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
                        backsUp = backups.Where(x => x.BackupUserId == group.Key).Select(x => x.Role.ToString())
                            .Concat(leadershipBackups.Where(x => x.BackupUserId == group.Key).Select(x => x.Position.ToString()))
                            .Order().ToList(),
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
        // people who can no longer sign in. `search` narrows the directory by name, username or email so the
        // #816 Add Person to Project flow works as a directory rather than a bounded select.
        app.MapGet("/api/projects/{projectId:guid}/personnel/candidates", async (Guid projectId, string? search, HttpContext http,
            AeroLinkDbContext db, IdentityService identity, ProjectAuthorityResolver resolver, CancellationToken ct) =>
        {
            if (!await http.HasRosterAuthorityAsync(db, resolver, projectId, ct)) return Results.Forbid();
            var programId = await ProgramOfAsync(db, projectId, ct);
            if (programId is null) return Results.NotFound();
            var current = await db.ProgramMemberships.AsNoTracking()
                .Where(x => x.ProgramId == programId && x.EndedAt == null).Select(x => x.UserId).Distinct().ToListAsync(ct);
            var term = search?.Trim().ToLower();
            var candidates = await db.UserAccounts.AsNoTracking()
                .Where(x => x.State == AccountState.Active && !current.Contains(x.Id))
                .Where(x => term == null || term == "" || x.UserName.ToLower().Contains(term)
                    || x.DisplayName.ToLower().Contains(term) || x.Email.ToLower().Contains(term))
                .OrderBy(x => x.DisplayName)
                .Select(x => new { userId = x.Id, userName = x.UserName, displayName = x.DisplayName, x.Email })
                .ToListAsync(ct);
            return Results.Ok(candidates);
        });

        // #816: adding a person to the project grants one or more base roles as ONE attributable logical
        // operation — every requested role is validated before any membership is written. When neither the
        // legacy `role` nor the `roles` array carries a value, the request is rejected rather than silently
        // granting the enum default.
        app.MapPost("/api/projects/{projectId:guid}/personnel", async (Guid projectId, AddProjectMemberRequest request,
            HttpContext http, AeroLinkDbContext db, IdentityService identity, ProjectAuthorityResolver resolver, CancellationToken ct) =>
        {
            var actor = http.UserAccount();
            if (!await http.HasRosterAuthorityAsync(db, resolver, projectId, ct)) return Results.Forbid();
            var roles = request.Roles is { Length: > 0 } many ? many.Distinct().ToList()
                : request.Role is not null ? new List<ProgramRole> { request.Role.Value }
                : null;
            if (roles is null || roles.Count == 0)
                return Results.BadRequest(new { error = "Choose at least one project role." });
            // Project leadership staffs its own project. It does not mint project administrators — that stays
            // with the global account, so nobody can promote themselves out of the authority they were given.
            if (roles.Contains(ProgramRole.Administrator) && !actor.IsAdministrator)
                return Results.Forbid();
            // #816: retired position roles are history, not grants. Existing rows stay readable and the
            // v2 reconciliation retires them, but a new grant resurrects a parallel accountability the
            // leadership position now owns — and on a database that has not yet run v2 it recreates exactly
            // the state the reconciliation refuses, so the next restart would fail to start.
            if (roles.Any(SingularProgramRoles.IsSingular))
            {
                var retired = roles.First(SingularProgramRoles.IsSingular);
                return Results.Conflict(new { error = $"{Readable(retired)} is retired as a project role. Assign the matching Project Leadership position instead." });
            }
            // #816 Slice 4: Reviewer and Approver are signature meanings a workflow stage records, not jobs.
            // The browser hides them; the server refuses them so a crafted request cannot grant standing
            // control authority the workflow model replaced.
            if (roles.Any(RetiredGrantRoles.IsRetiredGrant))
            {
                var retiredGrant = roles.First(RetiredGrantRoles.IsRetiredGrant);
                return Results.Conflict(new { error = $"{Readable(retiredGrant)} is a signature meaning a review workflow stage records, not a project role. Configure the workflow's required authority instead." });
            }
            var programId = await ProgramOfAsync(db, projectId, ct);
            if (programId is null) return Results.NotFound();
            if (!await db.UserAccounts.AnyAsync(x => x.Id == request.UserId && x.State == AccountState.Active, ct))
                return Results.BadRequest(new { error = "That person does not have an active AeroLink account." });
            foreach (var role in roles)
            {
                if (await db.ProgramMemberships.AnyAsync(x => x.UserId == request.UserId && x.ProgramId == programId && x.Role == role && x.EndedAt == null, ct))
                    return Results.Conflict(new { error = $"They already hold {Readable(role)} on this project." });
                if (SingularProgramRoles.IsSingular(role))
                {
                    var holder = await db.ProgramMemberships.AsNoTracking()
                        .Where(x => x.ProgramId == programId && x.Role == role && x.EndedAt == null)
                        .Join(db.UserAccounts.AsNoTracking(), m => m.UserId, u => u.Id, (m, u) => u.DisplayName)
                        .FirstOrDefaultAsync(ct);
                    if (holder is not null)
                        return Results.Conflict(new { error = $"{Readable(role)} is held by {holder}. End their position before assigning it to somebody else." });
                }
            }

            foreach (var role in roles)
                db.ProgramMemberships.Add(new ProgramMembership(request.UserId, programId.Value, role, actor.UserName, DateTimeOffset.UtcNow));
            db.SecurityAuditEvents.Add(new("RoleGranted", actor.UserName, request.UserId.ToString(), "Success",
                $"Granted {string.Join(", ", roles.Select(x => x.ToString()).Order())} on project {projectId} from the project personnel page.",
                http.Connection.RemoteIpAddress?.ToString() ?? "local", DateTimeOffset.UtcNow));
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        app.MapDelete("/api/projects/{projectId:guid}/personnel/{userId:guid}/roles/{role}", async (Guid projectId,
            Guid userId, ProgramRole role, HttpContext http, AeroLinkDbContext db, IdentityService identity, ProjectAuthorityResolver resolver, CancellationToken ct) =>
        {
            var actor = http.UserAccount();
            if (!await http.HasRosterAuthorityAsync(db, resolver, projectId, ct)) return Results.Forbid();
            if (role == ProgramRole.Administrator && !actor.IsAdministrator) return Results.Forbid();
            var programId = await ProgramOfAsync(db, projectId, ct);
            if (programId is null) return Results.NotFound();
            var membership = await db.ProgramMemberships
                .SingleOrDefaultAsync(x => x.UserId == userId && x.ProgramId == programId && x.Role == role && x.EndedAt == null, ct);
            if (membership is null) return Results.NotFound();

            var now = DateTimeOffset.UtcNow;
            membership.End(actor.UserName, now);
            await AdministrationEndpoints.EndBackupsForEndedMembershipAsync(
                db, userId, programId.Value, membership.Id, role, actor.UserName, ct);
            db.SecurityAuditEvents.Add(new("RoleRevoked", actor.UserName, userId.ToString(), "Success",
                $"Ended {role} on project {projectId} from the project personnel page.",
                http.Connection.RemoteIpAddress?.ToString() ?? "local", now));
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        app.MapPost("/api/projects/{projectId:guid}/personnel/backups", async (Guid projectId, NameBackupRequest request,
            HttpContext http, AeroLinkDbContext db, IdentityService identity, ProjectAuthorityResolver resolver, CancellationToken ct) =>
        {
            var actor = http.UserAccount();
            if (!await http.HasRosterAuthorityAsync(db, resolver, projectId, ct)) return Results.Forbid();
            var programId = await ProgramOfAsync(db, projectId, ct);
            if (programId is null) return Results.NotFound();
            // #816 Slice 4: Reviewer and Approver are signature meanings, not roles that can carry standing
            // cover. A role-keyed backup naming one would recreate exactly the standing control authority
            // that membership and delegation grants now refuse, so it is refused here too. Historical
            // backup rows remain readable compatibility data; this gates only NEW creation.
            if (RetiredGrantRoles.IsRetiredGrant(request.Role))
                return Results.Conflict(new { error = $"{Readable(request.Role)} is a signature meaning a review workflow stage records, not a role that can be backed up. Configure the workflow's required authority instead." });
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
            HttpContext http, AeroLinkDbContext db, IdentityService identity, ProjectAuthorityResolver resolver, CancellationToken ct) =>
        {
            var actor = http.UserAccount();
            if (!await http.HasRosterAuthorityAsync(db, resolver, projectId, ct)) return Results.Forbid();
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

public sealed record AddProjectMemberRequest(Guid UserId, ProgramRole? Role = null, ProgramRole[]? Roles = null);
public sealed record NameBackupRequest(Guid BackupUserId, ProgramRole Role);
