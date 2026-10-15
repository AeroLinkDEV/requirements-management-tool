using AeroLink.Domain.Identity;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Data;

namespace AeroLink.Api;

/// <summary>
/// Local identity administration, and time-bounded role delegation.
///
/// Administration grants authority. It never replaces an assigned approval identity, and it never deletes an
/// account whose name appears in history.
/// </summary>
public static class AdministrationEndpoints
{
    public static void MapAdministrationEndpoints(this WebApplication app)
    {
        app.MapGet("/api/admin/users", async (HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
        {
            if (!http.UserAccount().IsSystemAdministrator) return Results.Forbid();
            var users = await db.UserAccounts.AsNoTracking().OrderBy(x => x.DisplayName).ToListAsync(ct); var memberships = await db.ProgramMemberships.AsNoTracking().Where(x => x.EndedAt == null).ToListAsync(ct);
            return Results.Ok(users.Select(x => new { x.Id, x.UserName, x.DisplayName, x.Email, state = x.State.ToString(), x.LastLoginAt, x.CreatedAt, isGlobalAdministrator = x.IsGlobalAdministrator, isBuiltInAdministrator = x.IsBuiltInAdministrator, origin = x.Origin.ToString(), windowsPrincipal = x.WindowsSid != null ? x.WindowsPrincipal : null, windowsLinked = x.WindowsSid != null, memberships = memberships.Where(m => m.UserId == x.Id).Select(m => new { m.ProgramId, role = m.Role.ToString() }) }));
        });

        app.MapPost("/api/admin/users", async (CreateUserRequest request, HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
        {
            var actor = http.UserAccount(); if (!actor.IsSystemAdministrator) return Results.Forbid(); var userName = request.UserName.Trim().ToLowerInvariant(); if (await db.UserAccounts.AnyAsync(x => x.UserName == userName, ct)) return Results.Conflict(new { error = "Username already exists." });
            try { var user = new UserAccount(userName, request.DisplayName, request.Email, IdentityService.HashPassword(request.TemporaryPassword), DateTimeOffset.UtcNow);user.RequirePasswordChange(user.PasswordHash); db.UserAccounts.Add(user); db.SecurityAuditEvents.Add(new("AccountCreated", actor.UserName, userName, "Success", $"Created account for {request.DisplayName}; password rotation is required.", http.Connection.RemoteIpAddress?.ToString() ?? "local", DateTimeOffset.UtcNow)); await db.SaveChangesAsync(ct); return Results.Created($"/api/admin/users/{user.Id}", new { user.Id, user.UserName, user.DisplayName, user.MustChangePassword }); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // #816: the global administrator may correct a person's CURRENT identity data. This is deliberately
        // not a history rewrite: past signatures, audit events and role grants keep the names they were
        // recorded with, and this change is itself attributable here.
        app.MapPatch("/api/admin/users/{id:guid}/identity", async (Guid id, UpdateIdentityRequest request, HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
        {
            var actor = http.UserAccount(); if (!actor.IsSystemAdministrator) return Results.Forbid();
            var account = await db.UserAccounts.SingleOrDefaultAsync(x => x.Id == id, ct); if (account is null) return Results.NotFound();
            var displayName = (request.DisplayName ?? account.DisplayName).Trim();
            var email = (request.Email ?? account.Email).Trim();
            if (displayName.Length == 0) return Results.BadRequest(new { error = "A display name is required." });
            if (email.Length == 0 || !email.Contains('@') || email.Contains(' ')) return Results.BadRequest(new { error = "A valid email address is required." });
            var before = $"{account.DisplayName} <{account.Email}>";
            account.RefreshDirectoryProfile(displayName, email);
            db.SecurityAuditEvents.Add(new("IdentityUpdated", actor.UserName, account.UserName, "Success",
                $"Current profile changed from {before} to {account.DisplayName} <{account.Email}>. Historical records were not changed.",
                http.Connection.RemoteIpAddress?.ToString() ?? "local", DateTimeOffset.UtcNow));
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
        app.MapPost("/api/admin/users/{id:guid}/memberships", async (Guid id, GrantRoleRequest request, HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
        {
            var actor = http.UserAccount(); if (!actor.IsSystemAdministrator) return Results.Forbid(); if (!await db.UserAccounts.AnyAsync(x => x.Id == id, ct) || !await db.Programs.AnyAsync(x => x.Id == request.ProgramId, ct)) return Results.NotFound();
            if (await db.ProgramMemberships.AnyAsync(x => x.UserId == id && x.ProgramId == request.ProgramId && x.Role == request.Role && x.EndedAt == null, ct)) return Results.Conflict(new { error = "That Program role is already assigned." });
            // #816: the retired position roles are history, not grants. A new grant resurrects a parallel
            // accountability the leadership position now owns, and on a database that has not yet run the v2
            // reconciliation it recreates the state that migration refuses.
            if (SingularProgramRoles.IsSingular(request.Role))
                return Results.Conflict(new { error = $"{request.Role} is retired as a project role. Assign the matching Project Leadership position instead." });
            // #816 Slice 4: Reviewer and Approver are signature meanings a workflow stage records, not jobs a
            // person is granted. A crafted API request must not resurrect them as standing control authority.
            if (RetiredGrantRoles.IsRetiredGrant(request.Role))
                return Results.Conflict(new { error = $"{request.Role} is a signature meaning a review workflow stage records, not a project role. Configure the workflow's required authority instead." });
            db.ProgramMemberships.Add(new(id, request.ProgramId, request.Role, actor.UserName, DateTimeOffset.UtcNow));
            db.SecurityAuditEvents.Add(new("RoleGranted", actor.UserName, id.ToString(), "Success", $"Granted {request.Role} for program {request.ProgramId}.", http.Connection.RemoteIpAddress?.ToString() ?? "local", DateTimeOffset.UtcNow)); await db.SaveChangesAsync(ct); return Results.NoContent();
        });

        app.MapDelete("/api/admin/users/{id:guid}/memberships/{programId:guid}/{role}", async (Guid id, Guid programId, ProgramRole role, HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
        {
            var actor = http.UserAccount(); if (!actor.IsSystemAdministrator) return Results.Forbid();
            // Ended, not deleted: the roster has to be able to say who held this role during a period that has
            // already passed, which a removed row cannot answer.
            var membership = await db.ProgramMemberships.SingleOrDefaultAsync(x => x.UserId == id && x.ProgramId == programId && x.Role == role && x.EndedAt == null, ct); if (membership is null) return Results.NotFound();
            membership.End(actor.UserName, DateTimeOffset.UtcNow);
            await EndBackupsForEndedMembershipAsync(db, id, programId, membership.Id, role, actor.UserName, ct);
            db.SecurityAuditEvents.Add(new("RoleRevoked", actor.UserName, id.ToString(), "Success", $"Revoked {role} for program {programId}.", http.Connection.RemoteIpAddress?.ToString() ?? "local", DateTimeOffset.UtcNow));
            await db.SaveChangesAsync(ct); return Results.NoContent();
        });

        app.MapPost("/api/admin/users/{id:guid}/state", async (Guid id, SetAccountStateRequest request, HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
        {
            var actor = http.UserAccount(); if (!actor.IsSystemAdministrator) return Results.Forbid();
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            // #1639: the session said administrator before this transaction began; a revoke committed since
            // then must win, or a disable racing a revoke can leave no active administrator.
            if (!await StillAdministersAsync(db, actor.Id, ct)) return Results.Forbid();
            var user = await db.UserAccounts.SingleOrDefaultAsync(x => x.Id == id, ct); if (user is null) return Results.NotFound(); if (user.Id == actor.Id && !request.Enabled) return Results.BadRequest(new { error = "You cannot disable your own active account." });
            if (!request.Enabled && user.IsBuiltInAdministrator)
                return BuiltInAdministratorProtected();
            if (!request.Enabled && user.IsGlobalAdministrator && user.State == AccountState.Active && !await OtherActiveAdministratorExistsAsync(db, user.Id, ct))
                return LastAdministrator();
            var now = DateTimeOffset.UtcNow; var revokedSessions = 0;
            if (request.Enabled) user.Enable();
            else
            {
                user.Disable(now);
                var sessions = await db.UserSessions.Where(x => x.UserId == id && x.RevokedAt == null).ToListAsync(ct);
                foreach (var session in sessions) session.Revoke(now);
                revokedSessions = sessions.Count;
            }
            db.SecurityAuditEvents.Add(new(request.Enabled ? "AccountEnabled" : "AccountDisabled", actor.UserName, user.UserName, "Success", $"Account state set to {(request.Enabled ? "Active" : "Disabled")}; revoked {revokedSessions} outstanding session(s).", http.Connection.RemoteIpAddress?.ToString() ?? "local", now)); await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); return Results.NoContent();
        });

        // #1639: global administration is a persisted, attributable grant rather than the account named
        // `admin`. Each route re-reads the actor's own grant inside a serializable transaction, so a grant,
        // revoke or disable racing another administrator's change cannot act on authority that was already
        // taken away, and the last-administrator checks below read the committed state.
        app.MapPost("/api/admin/users/{id:guid}/global-administrator", async (Guid id, HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
        {
            var actor = http.UserAccount(); if (!actor.IsSystemAdministrator) return Results.Forbid();
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            if (!await StillAdministersAsync(db, actor.Id, ct)) return Results.Forbid();
            var user = await db.UserAccounts.SingleOrDefaultAsync(x => x.Id == id, ct); if (user is null) return Results.NotFound();
            if (user.IsGlobalAdministrator) return Results.Conflict(new { error = "This person is already a global administrator." });
            var now = DateTimeOffset.UtcNow;
            user.GrantGlobalAdministrator(actor.UserName, now);
            db.SecurityAuditEvents.Add(new("GlobalAdministratorGranted", actor.UserName, user.UserName, "Success", $"Granted global administrator to {user.DisplayName}.", http.Connection.RemoteIpAddress?.ToString() ?? "local", now));
            await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); return Results.NoContent();
        });

        app.MapDelete("/api/admin/users/{id:guid}/global-administrator", async (Guid id, HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
        {
            var actor = http.UserAccount(); if (!actor.IsSystemAdministrator) return Results.Forbid();
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            if (!await StillAdministersAsync(db, actor.Id, ct)) return Results.Forbid();
            var user = await db.UserAccounts.SingleOrDefaultAsync(x => x.Id == id, ct); if (user is null) return Results.NotFound();
            if (!user.IsGlobalAdministrator) return Results.Conflict(new { error = "This person is not a global administrator." });
            // Owner decision on #1639: the built-in administrator is Sean's break-glass and linked account, and
            // only that account may give up its own grant.
            if (user.IsBuiltInAdministrator && user.Id != actor.Id) return BuiltInAdministratorProtected();
            // The actor is an active administrator, so revoking somebody else always leaves one. The refusal is
            // reached by revoking the last active administrator — in practice, yourself when nobody else is left.
            if (user.State == AccountState.Active && !await OtherActiveAdministratorExistsAsync(db, id, ct))
                return LastAdministrator();
            var now = DateTimeOffset.UtcNow;
            user.RevokeGlobalAdministrator(actor.UserName, now);
            db.SecurityAuditEvents.Add(new("GlobalAdministratorRevoked", actor.UserName, user.UserName, "Success", $"Revoked global administrator from {user.DisplayName}.", http.Connection.RemoteIpAddress?.ToString() ?? "local", now));
            await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); return Results.NoContent();
        });

        app.MapPost("/api/admin/users/{id:guid}/reset-password",async(Guid id,ResetPasswordRequest request,HttpContext http,AeroLinkDbContext db,CancellationToken ct)=>
        {
            var actor=http.UserAccount();if(!actor.IsSystemAdministrator)return Results.Forbid();try{var user=await db.UserAccounts.SingleOrDefaultAsync(x=>x.Id==id,ct);if(user is null)return Results.NotFound();
            // #1615: a Windows account proves itself to the domain only. An AeroLink password would outlive its
            // directory account, make it lockable by guessing, and let it sign where its Windows password is required.
            if(user.Origin==AccountOrigin.Windows)return Results.Conflict(new{error="This account signs in with Windows and has no AeroLink password.",code="windows_account"});
            user.RequirePasswordChange(IdentityService.HashPassword(request.TemporaryPassword));var now=DateTimeOffset.UtcNow;var sessions=await db.UserSessions.Where(x=>x.UserId==id&&x.RevokedAt==null).ToListAsync(ct);foreach(var session in sessions)session.Revoke(now);db.SecurityAuditEvents.Add(new("AdministratorPasswordReset",actor.UserName,user.UserName,"Success",$"Issued temporary password requiring rotation and revoked {sessions.Count} session(s). Reason: {request.Reason.Trim()}",http.Connection.RemoteIpAddress?.ToString()??"local",now));await db.SaveChangesAsync(ct);return Results.NoContent();}catch(ArgumentException ex){return Results.BadRequest(new{error=ex.Message});}
        });

        // #1615: removes a Windows logon linked to a local account (the administrator's link above all), so a link
        // made by mistake or by somebody who once held the password can be seen and undone. It revokes the account's
        // sessions, because any of them may have been opened through the link. A Windows-created account has no
        // other identity than its SID; disabling it is the tool there.
        app.MapPost("/api/admin/users/{id:guid}/windows/unlink", async (Guid id, HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
        {
            var actor = http.UserAccount(); if (!actor.IsSystemAdministrator) return Results.Forbid();
            var user = await db.UserAccounts.SingleOrDefaultAsync(x => x.Id == id, ct); if (user is null) return Results.NotFound();
            if (user.Origin == AccountOrigin.Windows)
                return Results.Conflict(new { error = "A Windows account is its Windows logon. Disable the account instead.", code = "windows_account" });
            if (user.WindowsSid is null) return Results.Conflict(new { error = "This account is not linked to a Windows logon.", code = "not_linked" });
            var now = DateTimeOffset.UtcNow; var unlinked = $"{user.WindowsPrincipal} ({user.WindowsSid})";
            user.UnlinkWindows();
            var sessions = await db.UserSessions.Where(x => x.UserId == id && x.RevokedAt == null).ToListAsync(ct);
            foreach (var session in sessions) session.Revoke(now);
            db.SecurityAuditEvents.Add(new("WindowsUnlinked", actor.UserName, user.UserName, "Success",
                $"Removed the link to Windows logon {unlinked} and revoked {sessions.Count} session(s).", http.Connection.RemoteIpAddress?.ToString() ?? "local", now));
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        app.MapPost("/api/delegations", async (CreateDelegationRequest request, HttpContext http, AeroLinkDbContext db, IdentityService identity, ProjectAuthorityResolver authority, CancellationToken ct) =>
        {
            var actor = http.UserAccount(); if (request.DelegatorUserId != actor.Id && !actor.IsSystemAdministrator) return Results.Forbid(); if (request.DelegatorUserId == request.DelegateUserId) return Results.BadRequest(new { error = "A person cannot delegate a role to themselves." });
            var activeUsers=await db.UserAccounts.AsNoTracking().Where(x=>(x.Id==request.DelegatorUserId||x.Id==request.DelegateUserId)&&x.State==AccountState.Active).Select(x=>x.Id).ToListAsync(ct);
            if(activeUsers.Count!=2)return Results.BadRequest(new{error="Both delegation participants must be active AeroLink users."});
            var members=await db.ProgramMemberships.AsNoTracking().Where(x=>x.ProgramId==request.ProgramId&&x.EndedAt==null&&(x.UserId==request.DelegatorUserId||x.UserId==request.DelegateUserId)).Select(x=>x.UserId).Distinct().ToListAsync(ct);
            if(members.Count!=2)return Results.BadRequest(new{error="Both delegation participants must belong to the selected Program."});
            var now = DateTimeOffset.UtcNow;
            // #816 Slice 4: a delegation of Reviewer/Approver would extend the same retired standing authority
            // a new grant would, so it is refused here as well.
            if (RetiredGrantRoles.IsRetiredGrant(request.Role))
                return Results.Conflict(new { error = $"{request.Role} is a signature meaning a review workflow stage records, not a delegable project role." });
            var governedPosition = ProjectLeadership.PositionForGovernedRole(request.Role);
            var canDelegate = SingularProgramRoles.IsPositionGoverned(request.Role)
                ? governedPosition is not null && await authority.IsSatisfiedAsync(
                    request.DelegatorUserId, request.ProgramId,
                    ProjectAuthorityRequirement.Leadership(governedPosition.Value), now, ct)
                : await identity.HasRoleAsync(request.DelegatorUserId, request.ProgramId, request.Role, now, ct);
            // A base eligibility membership is deliberately not the accountable position. Letting it mint a
            // ProgramManager (or other position-governed) delegation would turn that base role into leadership
            // through the delegate, bypassing the split #816 introduced. Existing non-position role delegation
            // remains unchanged; a position's primary or standing backup may still arrange temporary cover.
            if (!canDelegate) return Results.Forbid();
            try { var delegation = new RoleDelegation(request.ProgramId, request.DelegatorUserId, request.DelegateUserId, request.Role, request.StartsAt, request.EndsAt, request.Reason, actor.UserName, DateTimeOffset.UtcNow); db.RoleDelegations.Add(delegation); db.SecurityAuditEvents.Add(new("DelegationCreated", actor.UserName, request.DelegateUserId.ToString(), "Success", $"Delegated {request.Role} through {request.EndsAt:u}.", http.Connection.RemoteIpAddress?.ToString() ?? "local", DateTimeOffset.UtcNow)); await db.SaveChangesAsync(ct); return Results.Created($"/api/delegations/{delegation.Id}", new { delegation.Id }); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapGet("/api/delegations", async (HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
        {
            var actor = http.UserAccount();
            var now = DateTimeOffset.UtcNow;
            var delegations = await db.RoleDelegations.AsNoTracking()
                .Where(x => actor.IsSystemAdministrator || x.DelegatorUserId == actor.Id || x.DelegateUserId == actor.Id)
                .ToListAsync(ct);
            var programIds = delegations.Select(x => x.ProgramId).Distinct().ToList();
            var userIds = delegations.SelectMany(x => new[] { x.DelegatorUserId, x.DelegateUserId }).Distinct().ToList();
            var programs = await db.Programs.AsNoTracking().Where(x => programIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Name, ct);
            var users = await db.UserAccounts.AsNoTracking().Where(x => userIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.DisplayName, ct);
            return Results.Ok(delegations.OrderByDescending(x => x.CreatedAt).Select(x => new
            {
                x.Id,
                x.ProgramId,
                program = programs.GetValueOrDefault(x.ProgramId, "Unknown Program"),
                x.DelegatorUserId,
                delegator = users.GetValueOrDefault(x.DelegatorUserId, "Unknown user"),
                x.DelegateUserId,
                delegateName = users.GetValueOrDefault(x.DelegateUserId, "Unknown user"),
                role = x.Role.ToString(),
                x.StartsAt,
                x.EndsAt,
                x.Reason,
                actor = x.CreatedBy,
                x.CreatedAt,
                x.RevokedAt,
                status = x.RevokedAt is not null ? "Revoked" : x.EndsAt <= now ? "Expired" : x.StartsAt > now ? "Future" : "Active",
                canRevoke = x.RevokedAt is null && x.EndsAt > now && (actor.IsSystemAdministrator || actor.Id == x.DelegatorUserId)
            }));
        });

        app.MapDelete("/api/delegations/{id:guid}", async (Guid id, HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
        {
            var delegation = await db.RoleDelegations.SingleOrDefaultAsync(x => x.Id == id, ct); if (delegation is null) return Results.NotFound();
            var actor = http.UserAccount(); if (!actor.IsSystemAdministrator && actor.Id != delegation.DelegatorUserId) return Results.Forbid();
            if (delegation.RevokedAt is not null) return Results.NoContent();
            delegation.Revoke(DateTimeOffset.UtcNow);
            db.SecurityAuditEvents.Add(new("DelegationRevoked", actor.UserName, delegation.DelegateUserId.ToString(), "Success", $"Revoked delegated {delegation.Role} authority for program {delegation.ProgramId}.", http.Connection.RemoteIpAddress?.ToString() ?? "local", DateTimeOffset.UtcNow));
            await db.SaveChangesAsync(ct); return Results.NoContent();
        });

        // SQLite cannot order by a DateTimeOffset in SQL (#1189), so on SQLite the rows are ordered after reading,
        // the same split the managed-document audit uses. PostgreSQL keeps ordering and limiting in the database.
        app.MapGet("/api/admin/security-audit", async (HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
        {
            if (!http.UserAccount().IsSystemAdministrator) return Results.Forbid();
            var events = db.SecurityAuditEvents.AsNoTracking();
            return Results.Ok(db.Database.IsNpgsql()
                ? await events.OrderByDescending(x => x.OccurredAt).Take(1000).ToListAsync(ct)
                : (await events.ToListAsync(ct)).OrderByDescending(x => x.OccurredAt).Take(1000).ToList());
        });

        // Enterprise Requirements Workspace: configurable schemas, structured specifications,
        // collaboration, saved views, governed bulk operations, redlines, and onboarding.
    }

    /// <summary>
    /// Stands down anybody's standing backups on a project once they no longer belong to it.
    ///
    /// Authority is already refused — <c>IdentityService</c> requires a current membership before honouring a
    /// backup — but leaving the record standing would show a departed person as the named cover on the
    /// Personnel page, which is exactly the reassurance nobody should be given. Only their last remaining role
    /// ending removes them; losing one of several roles does not.
    /// </summary>
    private static Task<bool> StillAdministersAsync(AeroLinkDbContext db, Guid actorId, CancellationToken ct) =>
        db.UserAccounts.AnyAsync(x => x.Id == actorId && x.IsGlobalAdministrator && x.State == AccountState.Active, ct);

    private static Task<bool> OtherActiveAdministratorExistsAsync(AeroLinkDbContext db, Guid userId, CancellationToken ct) =>
        db.UserAccounts.AnyAsync(x => x.Id != userId && x.IsGlobalAdministrator && x.State == AccountState.Active, ct);

    private static IResult LastAdministrator() => Results.Conflict(new
    {
        error = "The last active global administrator cannot be revoked or disabled. Grant another person global administrator first.",
        code = "last_global_administrator",
    });

    private static IResult BuiltInAdministratorProtected() => Results.Conflict(new
    {
        error = "The built-in administrator account can be changed only by itself.",
        code = "built_in_administrator_protected",
    });

    internal static async Task EndBackupsForEndedMembershipAsync(AeroLinkDbContext db, Guid userId, Guid programId,
        Guid justEndedMembershipId, ProgramRole endedRole, string actor, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        // Leadership eligibility is exact. Ending the required base role must stand down any primary or
        // backup designation immediately, even if the person still holds another role on the Program;
        // otherwise the stale row silently resurrects authority when that base role is granted again.
        var affectedPositions = ProjectLeadership.All
            .Where(position => ProjectLeadership.RequiredBaseRole(position) == endedRole).ToList();
        if (affectedPositions.Count > 0)
        {
            var assignments = await db.ProjectLeadershipAssignments
                .Where(x => x.HolderUserId == userId && x.ProgramId == programId && x.EndedAt == null)
                .ToListAsync(ct);
            foreach (var assignment in assignments.Where(x => affectedPositions.Contains(x.Position)))
                assignment.End(actor, now);

            var leadershipBackups = await db.ProjectLeadershipBackups
                .Where(x => x.BackupUserId == userId && x.ProgramId == programId && x.RemovedAt == null)
                .ToListAsync(ct);
            foreach (var backup in leadershipBackups.Where(x => affectedPositions.Contains(x.Position)))
                backup.Remove(actor, now);
        }

        // The membership ended moments ago is tracked but unsaved, so a database query still sees it as
        // current. It is excluded by identity rather than relying on the change tracker being flushed.
        var stillAMember = await db.ProgramMemberships
            .AnyAsync(x => x.UserId == userId && x.ProgramId == programId && x.EndedAt == null && x.Id != justEndedMembershipId, ct);
        if (stillAMember) return;
        var backups = await db.ProjectRoleBackups
            .Where(x => x.ProgramId == programId && x.BackupUserId == userId && x.RemovedAt == null)
            .ToListAsync(ct);
        foreach (var backup in backups) backup.Remove(actor, now);
    }
}
