using System.Text.Json;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Integrations;
using AeroLink.Domain.Notifications;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Notifications;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>
/// Appends integration events and notification deliveries for entities in the current save unit.
/// This phase runs before EF's base save so the resulting outbox rows share the caller's transaction.
/// It does not send mail, dispatch webhooks, open transactions, or save independently.
/// </summary>
internal sealed class SaveBoundaryLifecycleAppender(AeroLinkDbContext db)
{
    internal async Task AppendAsync(CancellationToken cancellationToken)
    {
        await AddLifecycleEventsAsync(cancellationToken);
        await QueueNotificationDeliveriesAsync(cancellationToken);
    }

    /// <summary>
    /// Queues an outbound delivery for every notification being written, in the same unit of work.
    /// A notification with no recipient address or an opted-out recipient still receives a suppressed row;
    /// the absence and reason are controlled evidence rather than a silently dropped obligation.
    /// </summary>
    private async Task QueueNotificationDeliveriesAsync(CancellationToken cancellationToken)
    {
        var raised = db.ChangeTracker.Entries<UserNotification>()
            .Where(x => x.State == EntityState.Added)
            .Select(x => x.Entity)
            .ToList();
        if (raised.Count == 0)
            return;

        await new NotificationOutbox(db).QueueEmailAsync(
            raised, DateTimeOffset.UtcNow, cancellationToken);
    }

    private async Task AddLifecycleEventsAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var pending = new List<(
            Guid ProjectId,
            string EventType,
            string AggregateType,
            Guid AggregateId,
            object Payload,
            string Actor)>();

        foreach (var entry in db.ChangeTracker.Entries<SystemChangeRequest>()
                     .Where(x => x.State is EntityState.Added or EntityState.Modified))
        {
            pending.Add((entry.Entity.ProjectId, "aerolink.change-request.changed", "ChangeRequest", entry.Entity.Id,
                new
                {
                    entry.Entity.DisplayNumber,
                    state = entry.Entity.State.ToString(),
                    entry.Entity.Version,
                    entry.Entity.TargetReleaseId,
                },
                entry.Entity.AuditEvents.OrderByDescending(x => x.OccurredAt).FirstOrDefault()?.ActorId
                    ?? entry.Entity.AuthorId));
        }

        foreach (var entry in db.ChangeTracker.Entries<CandidateBaseline>()
                     .Where(x => x.State is EntityState.Added or EntityState.Modified))
        {
            pending.Add((entry.Entity.ProjectId, "aerolink.baseline.changed", "CandidateBaseline", entry.Entity.Id,
                new
                {
                    entry.Entity.DisplayNumber,
                    state = entry.Entity.State.ToString(),
                    entry.Entity.ReleaseId,
                    entry.Entity.ContentHash,
                },
                entry.Entity.Events.OrderByDescending(x => x.OccurredAt).FirstOrDefault()?.ActorId
                    ?? "aerolink.lifecycle"));
        }

        foreach (var entry in db.ChangeTracker.Entries<RequirementRevision>()
                     .Where(x => x.State == EntityState.Added))
        {
            var projectId = db.ChangeTracker.Entries<RequirementArtifact>()
                .FirstOrDefault(x => x.Entity.Id == entry.Entity.ArtifactId)?.Entity.ProjectId;
            projectId ??= await db.Requirements.AsNoTracking()
                .Where(x => x.Id == entry.Entity.ArtifactId)
                .Select(x => (Guid?)x.ProjectId)
                .SingleOrDefaultAsync(cancellationToken);
            if (projectId is not Guid id)
                continue;

            pending.Add((id, "aerolink.requirement.revision-created", "RequirementRevision", entry.Entity.Id,
                new
                {
                    entry.Entity.ArtifactId,
                    entry.Entity.Revision,
                    state = entry.Entity.State.ToString(),
                    originKind = entry.Entity.OriginKind.ToString(),
                    entry.Entity.SourceChangeRequestId,
                    entry.Entity.SourceBaselineImportId,
                    entry.Entity.EffectiveBaselineId,
                },
                "aerolink.lifecycle"));
        }

        foreach (var entry in db.ChangeTracker.Entries<ReleaseCampaign>()
                     .Where(x => x.State is EntityState.Added or EntityState.Modified))
        {
            pending.Add((entry.Entity.ProjectId, "aerolink.release-campaign.changed", "ReleaseCampaign", entry.Entity.Id,
                new
                {
                    state = entry.Entity.State.ToString(),
                    entry.Entity.ReleaseId,
                    entry.Entity.BaselineId,
                    entry.Entity.SoftwareBuildId,
                    entry.Entity.ReleaseHash,
                },
                entry.Entity.Events.OrderByDescending(x => x.OccurredAt).FirstOrDefault()?.ActorId
                    ?? entry.Entity.OwnerId));
        }

        foreach (var entry in db.ChangeTracker.Entries<SoftwareBuild>()
                     .Where(x => x.State is EntityState.Added or EntityState.Modified))
        {
            pending.Add((entry.Entity.ProjectId,
                entry.State == EntityState.Added
                    ? "aerolink.software-build.recorded"
                    : "aerolink.software-build.changed",
                "SoftwareBuild", entry.Entity.Id,
                new
                {
                    entry.Entity.BuildNumber,
                    state = entry.Entity.State.ToString(),
                    entry.Entity.ReleaseId,
                    entry.Entity.BaselineId,
                },
                entry.Entity.RecordedBy));
        }

        foreach (var entry in db.ChangeTracker.Entries<TestExecution>()
                     .Where(x => x.State == EntityState.Added))
        {
            pending.Add((entry.Entity.ProjectId, "aerolink.test-execution.recorded", "TestExecution", entry.Entity.Id,
                new
                {
                    outcome = entry.Entity.Outcome.ToString(),
                    entry.Entity.ReleaseId,
                    entry.Entity.ProcedureRevisionId,
                    entry.Entity.SoftwareBuildId,
                    entry.Entity.RetestOfExecutionId,
                    entry.Entity.ExecutedAt,
                },
                entry.Entity.ExecutedBy));
        }

        if (pending.Count == 0)
            return;

        var projectIds = pending.Select(x => x.ProjectId).Distinct().ToList();
        var subscriptions = await db.WebhookSubscriptions.AsNoTracking()
            .Where(x => projectIds.Contains(x.ProjectId) && x.IsEnabled)
            .ToListAsync(cancellationToken);

        var addedEventKeys = db.ChangeTracker.Entries<IntegrationEvent>()
            .Where(x => x.State == EntityState.Added)
            .Select(x => (x.Entity.AggregateId, x.Entity.EventType))
            .ToHashSet();
        foreach (var item in pending)
        {
            if (!addedEventKeys.Add((item.AggregateId, item.EventType)))
                continue;

            var payload = JsonSerializer.Serialize(item.Payload,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var integrationEvent = new IntegrationEvent(
                item.ProjectId, item.EventType, item.AggregateType, item.AggregateId,
                payload, item.Actor, now);
            db.IntegrationEvents.Add(integrationEvent);

            foreach (var subscription in subscriptions.Where(x => x.ProjectId == item.ProjectId))
            {
                var types = JsonSerializer.Deserialize<string[]>(subscription.EventTypesJson) ?? [];
                if (types.Any(x => x == "*"
                    || x.Equals(item.EventType, StringComparison.OrdinalIgnoreCase)))
                {
                    db.WebhookDeliveries.Add(new WebhookDelivery(
                        item.ProjectId, integrationEvent.Id, subscription.Id, now));
                }
            }

            integrationEvent.MarkDispatched(now);
        }
    }
}
