using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Integrations;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

public sealed class WebhookDeliveryOperationsApiTests
{
    [Fact]
    public async Task Overview_counts_and_surfaces_expired_work_beyond_recent_activity_without_cross_project_rows()
    {
        using var factory = new AeroLinkApiFactory();
        using var administrator = factory.CreateClient();
        await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(administrator);
        var scenario = await SeedAsync(factory, recentDeliveredCount: 81, includeLiveClaim: false, includeDeadLetter: false);

        using var response = await administrator.GetAsync($"/api/integrations/overview?projectId={scenario.ProjectId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var metrics = body.GetProperty("metrics");
        Assert.Equal(1, metrics.GetProperty("expiredClaims").GetInt32());

        var deliveries = body.GetProperty("deliveries").EnumerateArray().ToList();
        Assert.Contains(deliveries, item => item.GetProperty("id").GetGuid() == scenario.ExpiredDeliveryId
            && item.GetProperty("needsAttention").GetBoolean());
        Assert.DoesNotContain(deliveries, item => item.GetProperty("id").GetGuid() == scenario.ForeignExpiredDeliveryId);
    }

    [Fact]
    public async Task Expired_and_dead_letter_replay_preserve_delivery_event_subscription_and_attempt_history()
    {
        using var factory = new AeroLinkApiFactory();
        using var administrator = factory.CreateClient();
        await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(administrator);
        var scenario = await SeedAsync(factory, recentDeliveredCount: 0, includeLiveClaim: false, includeDeadLetter: true);

        Guid expiredEventId;
        Guid expiredSubscriptionId;
        string expiredHistory;
        using (var before = factory.Services.CreateScope())
        {
            var db = before.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var delivery = await db.WebhookDeliveries.SingleAsync(x => x.Id == scenario.ExpiredDeliveryId);
            expiredEventId = delivery.IntegrationEventId;
            expiredSubscriptionId = delivery.SubscriptionId;
            expiredHistory = delivery.AttemptHistoryJson;
        }

        await ReplayAsync(administrator, scenario.ExpiredDeliveryId);
        await ReplayAsync(administrator, scenario.DeadLetterDeliveryId);

        using var after = factory.Services.CreateScope();
        var afterDb = after.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var expired = await afterDb.WebhookDeliveries.SingleAsync(x => x.Id == scenario.ExpiredDeliveryId);
        var deadLetter = await afterDb.WebhookDeliveries.SingleAsync(x => x.Id == scenario.DeadLetterDeliveryId);

        Assert.Equal(WebhookDeliveryState.Pending, expired.State);
        Assert.Equal(expiredEventId, expired.IntegrationEventId);
        Assert.Equal(expiredSubscriptionId, expired.SubscriptionId);
        Assert.Null(expired.ClaimToken);
        Assert.Null(expired.ClaimedBy);
        Assert.Null(expired.ClaimExpiresAt);
        Assert.NotEqual("[]", expired.AttemptHistoryJson);
        Assert.NotEqual("[]", expiredHistory);
        Assert.Contains(expired.AttemptHistory(), attempt => attempt.Outcome == "Replayed");

        Assert.Equal(WebhookDeliveryState.Pending, deadLetter.State);
        Assert.Equal(scenario.DeadLetterEventId, deadLetter.IntegrationEventId);
        Assert.Equal(scenario.DeadLetterSubscriptionId, deadLetter.SubscriptionId);
        Assert.Null(deadLetter.ClaimToken);
        Assert.Null(deadLetter.ClaimedBy);
        Assert.Null(deadLetter.ClaimExpiresAt);
        Assert.Equal(5, deadLetter.AttemptHistory().Count);
        Assert.Equal(0, deadLetter.AttemptCount);
    }

    [Fact]
    public async Task Replay_rejects_a_live_claim_and_leaves_current_owner_and_history_unchanged()
    {
        using var factory = new AeroLinkApiFactory();
        using var administrator = factory.CreateClient();
        await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(administrator);
        var scenario = await SeedAsync(factory, recentDeliveredCount: 0, includeLiveClaim: true, includeDeadLetter: false);

        Guid claimToken;
        string history;
        using (var before = factory.Services.CreateScope())
        {
            var db = before.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var delivery = await db.WebhookDeliveries.SingleAsync(x => x.Id == scenario.LiveDeliveryId);
            claimToken = delivery.ClaimToken!.Value;
            history = delivery.AttemptHistoryJson;
        }

        using var response = await administrator.PostAsync($"/api/integrations/deliveries/{scenario.LiveDeliveryId}/replay", null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("webhook_delivery_claim_active", problem.GetProperty("code").GetString());

        using var after = factory.Services.CreateScope();
        var row = await after.ServiceProvider.GetRequiredService<AeroLinkDbContext>().WebhookDeliveries
            .SingleAsync(x => x.Id == scenario.LiveDeliveryId);
        Assert.Equal(WebhookDeliveryState.Delivering, row.State);
        Assert.Equal(claimToken, row.ClaimToken);
        Assert.Equal("live-worker", row.ClaimedBy);
        Assert.Equal(history, row.AttemptHistoryJson);
    }

    [Fact]
    public async Task Unauthenticated_and_foreign_project_operators_cannot_replay_a_delivery()
    {
        using var factory = new AeroLinkApiFactory();
        var scenario = await SeedAsync(factory, recentDeliveredCount: 0, includeLiveClaim: false, includeDeadLetter: false);

        using (var anonymous = factory.CreateClient())
        using (var response = await anonymous.PostAsync($"/api/integrations/deliveries/{scenario.ExpiredDeliveryId}/replay", null))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        using var operatorClient = factory.CreateClient();
        using var login = await operatorClient.PostAsJsonAsync("/api/auth/login", new
        {
            userName = scenario.OperatorUserName,
            password = AeroLinkApiFactory.MemberPassword,
        });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(operatorClient);

        using var foreign = await operatorClient.PostAsync($"/api/integrations/deliveries/{scenario.ForeignExpiredDeliveryId}/replay", null);
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
    }

    [Fact]
    public async Task Scoped_service_health_reports_expired_and_live_claims_without_foreign_project_data()
    {
        using var factory = new AeroLinkApiFactory();
        using var administrator = factory.CreateClient();
        await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(administrator);
        var scenario = await SeedAsync(factory, recentDeliveredCount: 0, includeLiveClaim: true, includeDeadLetter: false);

        using var created = await administrator.PostAsJsonAsync("/api/integrations/service-identities", new
        {
            projectId = scenario.ProjectId,
            name = "Webhook health observer",
            scopes = new[] { "integrations:read" },
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var credential = await created.Content.ReadFromJsonAsync<JsonElement>();

        using var service = factory.CreateClient();
        service.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", credential.GetProperty("apiKey").GetString());
        using var response = await service.GetAsync("/api/v1/integrations/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var health = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("attention", health.GetProperty("status").GetString());
        Assert.Equal(scenario.ProjectId, health.GetProperty("projectId").GetGuid());
        Assert.Equal(1, health.GetProperty("expiredClaims").GetInt32());
        Assert.Equal(1, health.GetProperty("activeDeliveries").GetInt32());
        Assert.Equal(0, health.GetProperty("deadLetters").GetInt32());
        Assert.Equal(0, health.GetProperty("pendingDeliveries").GetInt32());
    }

    private static async Task<HttpResponseMessage> ReplayAsync(HttpClient client, Guid deliveryId)
    {
        var response = await client.PostAsync($"/api/integrations/deliveries/{deliveryId}/replay", null);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return response;
    }

    private static async Task<Scenario> SeedAsync(
        AeroLinkApiFactory factory,
        int recentDeliveredCount,
        bool includeLiveClaim,
        bool includeDeadLetter)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;
        var operatorUserName = $"webhook.operator.{Guid.NewGuid():N}";
        var operatorAccount = new UserAccount(
            operatorUserName,
            "Webhook Operator",
            $"{operatorUserName}@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword),
            now);
        var projectProgram = new ProgramRecord($"Webhook Operations {Guid.NewGuid():N}", $"WOP{Guid.NewGuid():N}"[..12]);
        var project = new ProjectRecord(projectProgram.Id, "Webhook Operations Product", "Webhook Operations Product");
        var foreignProgram = new ProgramRecord($"Foreign Webhook Operations {Guid.NewGuid():N}", $"FWP{Guid.NewGuid():N}"[..12]);
        var foreignProject = new ProjectRecord(foreignProgram.Id, "Foreign Webhook Product", "Foreign Webhook Product");
        db.AddRange(projectProgram, project, foreignProgram, foreignProject, operatorAccount,
            new ProgramMembership(operatorAccount.Id, projectProgram.Id, ProgramRole.ConfigurationManager, "test.setup", now),
            new ProjectLeadershipAssignment(projectProgram.Id, ProjectLeadershipPosition.ConfigurationManager,
                operatorAccount.Id, "test.setup", now));

        var subscription = new WebhookSubscription(project.Id, "Primary destination", "https://example.test/primary",
            "[\"aerolink.integration.test\"]", "protected-secret", "test.setup", now);
        var foreignSubscription = new WebhookSubscription(foreignProject.Id, "Foreign destination", "https://example.test/foreign",
            "[\"aerolink.integration.test\"]", "protected-secret", "test.setup", now);
        db.AddRange(subscription, foreignSubscription);

        Guid expiredDeliveryId = Guid.Empty;
        Guid foreignExpiredDeliveryId = Guid.Empty;
        Guid liveDeliveryId = Guid.Empty;
        Guid deadLetterDeliveryId = Guid.Empty;
        Guid deadLetterEventId = Guid.Empty;
        var deadLetterSubscriptionId = subscription.Id;

        for (var index = 0; index < recentDeliveredCount; index++)
        {
            var occurred = now.AddMinutes(-index);
            var integrationEvent = new IntegrationEvent(project.Id, "aerolink.integration.test", "Test", Guid.NewGuid(),
                "{\"kind\":\"recent\"}", "test.setup", occurred);
            var delivery = new WebhookDelivery(project.Id, integrationEvent.Id, subscription.Id, occurred);
            var token = Guid.NewGuid();
            delivery.BeginAttempt("history-worker", token, occurred, TimeSpan.FromMinutes(2));
            delivery.Complete(token, 200, occurred.AddSeconds(1));
            db.AddRange(integrationEvent, delivery);
        }

        {
            var occurred = now.AddDays(-30);
            var integrationEvent = new IntegrationEvent(project.Id, "aerolink.integration.test", "Expired", Guid.NewGuid(),
                "{\"kind\":\"expired\"}", "test.setup", occurred);
            var delivery = new WebhookDelivery(project.Id, integrationEvent.Id, subscription.Id, occurred);
            delivery.BeginAttempt("expired-worker", Guid.NewGuid(), occurred, TimeSpan.FromMinutes(1));
            expiredDeliveryId = delivery.Id;
            db.AddRange(integrationEvent, delivery);
        }

        {
            var occurred = now.AddDays(-31);
            var integrationEvent = new IntegrationEvent(foreignProject.Id, "aerolink.integration.test", "Expired", Guid.NewGuid(),
                "{\"kind\":\"foreign-expired\"}", "test.setup", occurred);
            var delivery = new WebhookDelivery(foreignProject.Id, integrationEvent.Id, foreignSubscription.Id, occurred);
            delivery.BeginAttempt("foreign-worker", Guid.NewGuid(), occurred, TimeSpan.FromMinutes(1));
            foreignExpiredDeliveryId = delivery.Id;
            db.AddRange(integrationEvent, delivery);
        }

        if (includeLiveClaim)
        {
            var integrationEvent = new IntegrationEvent(project.Id, "aerolink.integration.test", "Live", Guid.NewGuid(),
                "{\"kind\":\"live\"}", "test.setup", now);
            var delivery = new WebhookDelivery(project.Id, integrationEvent.Id, subscription.Id, now);
            delivery.BeginAttempt("live-worker", Guid.NewGuid(), now, TimeSpan.FromHours(1));
            liveDeliveryId = delivery.Id;
            db.AddRange(integrationEvent, delivery);
        }

        if (includeDeadLetter)
        {
            var integrationEvent = new IntegrationEvent(project.Id, "aerolink.integration.test", "DeadLetter", Guid.NewGuid(),
                "{\"kind\":\"dead-letter\"}", "test.setup", now);
            var delivery = new WebhookDelivery(project.Id, integrationEvent.Id, subscription.Id, now);
            for (var attempt = 0; attempt < WebhookDelivery.MaximumAttempts; attempt++)
            {
                var attemptTime = now.AddMinutes(attempt);
                delivery.BeginAttempt("dead-letter-worker", Guid.NewGuid(), attemptTime, TimeSpan.FromMinutes(2));
                delivery.Fail(delivery.ClaimToken!.Value, 503, "receiver unavailable", attemptTime.AddSeconds(1));
            }
            deadLetterDeliveryId = delivery.Id;
            deadLetterEventId = integrationEvent.Id;
            db.AddRange(integrationEvent, delivery);
        }

        await db.SaveChangesAsync();
        return new(project.Id, foreignProject.Id, expiredDeliveryId, foreignExpiredDeliveryId, liveDeliveryId,
            deadLetterDeliveryId, deadLetterEventId, deadLetterSubscriptionId, operatorUserName);
    }

    private sealed record Scenario(
        Guid ProjectId,
        Guid ForeignProjectId,
        Guid ExpiredDeliveryId,
        Guid ForeignExpiredDeliveryId,
        Guid LiveDeliveryId,
        Guid DeadLetterDeliveryId,
        Guid DeadLetterEventId,
        Guid DeadLetterSubscriptionId,
        string OperatorUserName);
}
