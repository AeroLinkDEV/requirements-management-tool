using AeroLink.Domain.Identity;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Api;

/// <summary>
/// The Project Leadership surface: who holds each of the eight positions, who stands behind them, and the
/// authorized mutations that keep the two truthful.
///
/// Authorization follows the roster: the same people who may staff a project may elevate and back up its
/// leadership. The mutation rules live in ProjectLeadershipService — this surface only translates them —
/// so Personnel, the workflow candidate resolution and the signing gate answer from one model.
/// </summary>
public static class ProjectLeadershipEndpoints
{
    public static void MapProjectLeadershipEndpoints(this WebApplication app)
    {
        app.MapGet("/api/projects/{projectId:guid}/leadership", async (Guid projectId, HttpContext http,
            AeroLinkDbContext db, IdentityService identity, ProjectAuthorityResolver resolver, CancellationToken ct) =>
        {
            if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
            var programId = await ProgramOfAsync(db, projectId, ct);
            if (programId is null) return Results.NotFound();

            var primaries = await db.ProjectLeadershipAssignments.AsNoTracking()
                .Where(x => x.ProgramId == programId && x.EndedAt == null).ToListAsync(ct);
            var backups = await db.ProjectLeadershipBackups.AsNoTracking()
                .Where(x => x.ProgramId == programId && x.RemovedAt == null).ToListAsync(ct);
            var memberUserIds = primaries.Select(x => x.HolderUserId).Concat(backups.Select(x => x.BackupUserId)).Distinct().ToList();
            var memberships = await db.ProgramMemberships.AsNoTracking()
                .Where(x => x.ProgramId == programId && x.EndedAt == null && memberUserIds.Contains(x.UserId)).ToListAsync(ct);
            var accounts = await db.UserAccounts.AsNoTracking()
                .Where(x => memberUserIds.Contains(x.Id))
                .Select(x => new { x.Id, x.UserName, x.DisplayName, x.State }).ToListAsync(ct);
            var accountById = accounts.ToDictionary(x => x.Id);

            object Person(Guid userId) => accountById.TryGetValue(userId, out var account)
                ? new { userId, userName = account.UserName, displayName = account.DisplayName }
                : new { userId, userName = "unknown", displayName = "Unknown account" };
            bool AccountActive(Guid userId) => accountById.TryGetValue(userId, out var a) && a.State == AccountState.Active;

            // Every position is reported, held or not: a vacancy is the answer somebody came for, and a
            // backup must never be mistaken for a primary. Eligibility requires both the base-role
            // membership and an active account — a disabled holder's authority is suspended (#816).
            var positions = ProjectLeadership.All.Select(position =>
            {
                var requiredRole = ProjectLeadership.RequiredBaseRole(position);
                var primary = primaries.Where(x => x.Position == position).Select(x => new
                {
                    person = Person(x.HolderUserId),
                    assignedAt = x.AssignedAt,
                    eligibilityValid = memberships.Any(m => m.UserId == x.HolderUserId && m.Role == requiredRole)
                        && AccountActive(x.HolderUserId),
                }).SingleOrDefault();
                var backup = backups.Where(x => x.Position == position).Select(x => new
                {
                    person = Person(x.BackupUserId),
                    namedAt = x.NamedAt,
                    eligibilityValid = memberships.Any(m => m.UserId == x.BackupUserId && m.Role == requiredRole)
                        && AccountActive(x.BackupUserId),
                }).SingleOrDefault();
                return new
                {
                    position = position.ToString(),
                    requiredBaseRole = requiredRole.ToString(),
                    primary,
                    backup,
                };
            }).ToList();
            return Results.Ok(new { positions });
        });

        app.MapPost("/api/projects/{projectId:guid}/leadership/{position}/primary", async (
            Guid projectId, string position, AssignPrimaryRequest request, HttpContext http,
            AeroLinkDbContext db, IdentityService identity, ProjectAuthorityResolver resolver, ProjectLeadershipService leadership, CancellationToken ct) =>
        {
            if (!await http.HasRosterAuthorityAsync(db, resolver, projectId, ct)) return Results.Forbid();
            if (!TryResolvePosition(position, out var resolved)) return Results.NotFound(new { error = "Unknown Project Leadership position." });
            var programId = await ProgramOfAsync(db, projectId, ct);
            if (programId is null) return Results.NotFound();
            try
            {
                var result = await leadership.AssignPrimaryAsync(programId.Value, resolved, request.HolderUserId,
                    http.UserAccount().UserName, ct);
                await AuditAsync(db, http, "LeadershipAssigned", result.Position,
                    result.ReplacedHolderId is null
                        ? $"Assigned the {resolved} Project Leadership position."
                        : $"Replaced the {resolved} Project Leadership primary.", request.HolderUserId.ToString());
                // A different new primary can leave the previous backup in place; the response says so
                // explicitly rather than surprising the operator with a continuation they did not see.
                return Results.Ok(new { replaced = result.ReplacedHolderId, previousBackupContinues = result.PreviousBackupContinues });
            }
            catch (ProjectLeadershipEligibilityException exception)
            {
                return Results.Conflict(new { error = exception.Message });
            }
            catch (ProjectLeadershipConflictException exception)
            {
                return Results.Conflict(new { error = exception.Message });
            }
        });

        app.MapPost("/api/projects/{projectId:guid}/leadership/{position}/backup", async (
            Guid projectId, string position, AssignBackupRequest request, HttpContext http,
            AeroLinkDbContext db, IdentityService identity, ProjectAuthorityResolver resolver, ProjectLeadershipService leadership, CancellationToken ct) =>
        {
            if (!await http.HasRosterAuthorityAsync(db, resolver, projectId, ct)) return Results.Forbid();
            if (!TryResolvePosition(position, out var resolved)) return Results.NotFound(new { error = "Unknown Project Leadership position." });
            var programId = await ProgramOfAsync(db, projectId, ct);
            if (programId is null) return Results.NotFound();
            try
            {
                await leadership.AssignBackupAsync(programId.Value, resolved, request.BackupUserId, http.UserAccount().UserName, ct);
                await AuditAsync(db, http, "LeadershipBackupNamed", resolved,
                    $"Named a standing backup for the {resolved} Project Leadership position.", request.BackupUserId.ToString());
                return Results.NoContent();
            }
            catch (ProjectLeadershipEligibilityException exception)
            {
                return Results.Conflict(new { error = exception.Message });
            }
            catch (ProjectLeadershipConflictException exception)
            {
                return Results.Conflict(new { error = exception.Message });
            }
        });

        app.MapPut("/api/projects/{projectId:guid}/leadership/{position}/backup", async (
            Guid projectId, string position, AssignBackupRequest request, HttpContext http,
            AeroLinkDbContext db, IdentityService identity, ProjectAuthorityResolver resolver, ProjectLeadershipService leadership, CancellationToken ct) =>
        {
            if (!await http.HasRosterAuthorityAsync(db, resolver, projectId, ct)) return Results.Forbid();
            if (!TryResolvePosition(position, out var resolved)) return Results.NotFound(new { error = "Unknown Project Leadership position." });
            var programId = await ProgramOfAsync(db, projectId, ct);
            if (programId is null) return Results.NotFound();
            try
            {
                await leadership.ChangeBackupAsync(programId.Value, resolved, request.BackupUserId, http.UserAccount().UserName, ct);
                await AuditAsync(db, http, "LeadershipBackupChanged", resolved,
                    $"Changed the standing backup for the {resolved} Project Leadership position.", request.BackupUserId.ToString());
                return Results.NoContent();
            }
            catch (ProjectLeadershipEligibilityException exception)
            {
                return Results.Conflict(new { error = exception.Message });
            }
            catch (ProjectLeadershipConflictException exception)
            {
                return Results.Conflict(new { error = exception.Message });
            }
        });

        app.MapDelete("/api/projects/{projectId:guid}/leadership/{position}/backup", async (
            Guid projectId, string position, HttpContext http,
            AeroLinkDbContext db, IdentityService identity, ProjectAuthorityResolver resolver, ProjectLeadershipService leadership, CancellationToken ct) =>
        {
            if (!await http.HasRosterAuthorityAsync(db, resolver, projectId, ct)) return Results.Forbid();
            if (!TryResolvePosition(position, out var resolved)) return Results.NotFound(new { error = "Unknown Project Leadership position." });
            var programId = await ProgramOfAsync(db, projectId, ct);
            if (programId is null) return Results.NotFound();
            await leadership.RemoveBackupAsync(programId.Value, resolved, http.UserAccount().UserName, ct);
            await AuditAsync(db, http, "LeadershipBackupRemoved", resolved,
                $"Removed the standing backup for the {resolved} Project Leadership position.", projectId.ToString());
            return Results.NoContent();
        });
    }

    private static bool TryResolvePosition(string name, out ProjectLeadershipPosition position)
        => Enum.TryParse(name, ignoreCase: true, out position) && Enum.IsDefined(position);

    private static async Task<Guid?> ProgramOfAsync(AeroLinkDbContext db, Guid projectId, CancellationToken ct) =>
        await db.Projects.AsNoTracking().Where(x => x.Id == projectId).Select(x => (Guid?)x.ProgramId).SingleOrDefaultAsync(ct);

    private static async Task AuditAsync(AeroLinkDbContext db, HttpContext http, string eventType,
        ProjectLeadershipPosition position, string detail, string target)
    {
        var actor = http.UserAccount();
        db.SecurityAuditEvents.Add(new SecurityAuditEvent(eventType, actor.UserName, target,
            "Success", detail, http.Connection.RemoteIpAddress?.ToString() ?? "local", DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();
    }

    public sealed record AssignPrimaryRequest(Guid HolderUserId);
    public sealed record AssignBackupRequest(Guid BackupUserId);
}
