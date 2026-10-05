using AeroLink.Domain.Notifications;
using AeroLink.Infrastructure.Notifications;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Api;

internal static class NotificationContextEndpoints
{
    private static async Task<(NotificationContext? Context, string? Route)> AuthorizedAsync(HttpContext http,
        Guid id, AeroLinkDbContext db, CancellationToken ct)
    {
        var context = await db.NotificationContexts.AsNoTracking().SingleOrDefaultAsync(x => x.NotificationId == id
            && x.Recipient == http.UserAccount().UserName, ct);
        if (context is null || context.SourceFamily == NotificationSourceFamily.DiagnosticOperation
            || !await http.HasProjectAccessAsync(db, context.ProjectId, ct)) return (null, null);
        var route = await db.UserNotifications.AsNoTracking().Where(x => x.Id == id).Select(x => x.Route).SingleOrDefaultAsync(ct);
        return (context, route);
    }
    internal static void MapNotificationContextEndpoints(this WebApplication app)
    {
        app.MapGet("/api/notifications/{id:guid}/context", async (HttpContext http, Guid id, AeroLinkDbContext db,
            NotificationEligibility eligibility, CancellationToken ct) =>
        {
            var (context, _) = await AuthorizedAsync(http, id, db, ct);
            if (context is null) return Results.NotFound();
            var current = await eligibility.EvaluateAsync(context, ct, deliveryPolicy: false);
            return Results.Ok(new { id, context.Identifier, context.EventType, context.Stage, context.Cycle,
                context.Revision, context.SourceId, context.SourceFamily, context.SnapshotHash,
                originalObligationActive = current.Eligible,
                explanation = current.Eligible ? "This original obligation is currently active. AeroLink rechecks your authority when you act."
                    : "This original obligation has ended or your action authority changed. This notice does not authorize a later cycle, step or assignment.",
                currentWorkPath = $"/api/notifications/{id:D}/current" });
        });
        // Deliberate navigation is resolved again at the server; an obsolete notice is never converted
        // into a claim that the new cycle is the original request.
        app.MapGet("/api/notifications/{id:guid}/current", async (HttpContext http, Guid id, AeroLinkDbContext db, CancellationToken ct) =>
        {
            var (context, route) = await AuthorizedAsync(http, id, db, ct);
            if (context is null || route is null) return Results.NotFound();
            return Results.Ok(new { path = NotificationLinkBuilder.PathFor(route) });
        });
    }
}
