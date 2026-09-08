using AeroLink.Domain.Common;
using AeroLink.Domain.Integrations;

namespace AeroLink.Infrastructure.Tests;

public sealed class WebhookDeliveryClaimTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Completion_requires_the_current_claim_token_after_recovery()
    {
        var delivery = NewDelivery();
        var first = Guid.NewGuid();
        delivery.BeginAttempt("worker-a", first, Now, TimeSpan.FromMinutes(2));
        Assert.True(delivery.ClaimExpired(Now.AddMinutes(3)));
        delivery.RecoverExpired(Now.AddMinutes(3));

        var second = Guid.NewGuid();
        delivery.BeginAttempt("worker-b", second, Now.AddMinutes(4), TimeSpan.FromMinutes(2));
        Assert.False(delivery.Complete(first, 200, Now.AddMinutes(5)));
        Assert.Equal(WebhookDeliveryState.Delivering, delivery.State);
        Assert.True(delivery.Complete(second, 200, Now.AddMinutes(5)));
        Assert.Equal(WebhookDeliveryState.Delivered, delivery.State);
        Assert.Equal(2, delivery.AttemptCount);
        Assert.Equal(new[] { "RetryScheduled", "Delivered" }, delivery.AttemptHistory().Select(x => x.Outcome));
    }

    [Fact]
    public void Replay_of_an_expired_claim_closes_the_old_attempt_before_resetting()
    {
        var delivery = NewDelivery();
        var token = Guid.NewGuid();
        delivery.BeginAttempt("worker-a", token, Now, TimeSpan.FromMinutes(2));
        delivery.Replay(Now.AddMinutes(3));

        Assert.Equal(WebhookDeliveryState.Pending, delivery.State);
        Assert.Equal(0, delivery.AttemptCount);
        var attempt = Assert.Single(delivery.AttemptHistory());
        Assert.Equal("Replayed", attempt.Outcome);
        Assert.Equal("worker-a", attempt.Worker);
        Assert.Null(delivery.ClaimToken);
    }

    [Fact]
    public void Live_claim_cannot_be_replayed_or_stolen()
    {
        var delivery = NewDelivery();
        var token = Guid.NewGuid();
        delivery.BeginAttempt("worker-a", token, Now, TimeSpan.FromMinutes(2));

        Assert.Throws<DomainException>(() => delivery.Replay(Now.AddMinutes(1)));
        Assert.Throws<DomainException>(() => delivery.BeginAttempt("worker-b", Guid.NewGuid(), Now.AddMinutes(1), TimeSpan.FromMinutes(2)));
        Assert.False(delivery.Fail(Guid.NewGuid(), null, "stale", Now.AddMinutes(1)));
        Assert.Equal(WebhookDeliveryState.Delivering, delivery.State);
    }

    [Fact]
    public void Repeated_shutdown_releases_retain_the_failure_budget_and_physical_history()
    {
        var delivery = NewDelivery();
        for (var cycle = 0; cycle < 6; cycle++)
        {
            var token = Guid.NewGuid();
            delivery.BeginAttempt("worker-a", token, Now.AddMinutes(cycle), TimeSpan.FromMinutes(2));
            Assert.True(delivery.ReleaseForShutdown(token, Now.AddMinutes(cycle).AddSeconds(1)));
            Assert.Equal(0, delivery.AttemptCount);
            Assert.Equal(WebhookDeliveryState.RetryScheduled, delivery.State);
        }

        Assert.Equal(6, delivery.AttemptHistory().Count(x => x.Outcome == "Cancelled"));
        var finalToken = Guid.NewGuid();
        delivery.BeginAttempt("worker-a", finalToken, Now.AddHours(1), TimeSpan.FromMinutes(2));
        Assert.True(delivery.Fail(finalToken, 500, "receiver failed", Now.AddHours(1).AddSeconds(1)));
        Assert.Equal(1, delivery.AttemptCount);
        Assert.Equal(WebhookDeliveryState.RetryScheduled, delivery.State);
    }

    [Fact]
    public void Attempt_history_is_bounded_without_changing_delivery_identity()
    {
        var delivery = NewDelivery();
        var deliveryId = delivery.Id;
        for (var attempt = 0; attempt < 25; attempt++)
        {
            var token = Guid.NewGuid();
            delivery.BeginAttempt($"worker-{attempt}", token, Now.AddMinutes(attempt), TimeSpan.FromMinutes(2));
            Assert.True(delivery.Fail(token, 500, $"failure-{attempt}", Now.AddMinutes(attempt).AddSeconds(1)));
        }

        Assert.Equal(deliveryId, delivery.Id);
        Assert.Equal(20, delivery.AttemptHistory().Count);
        Assert.Equal(25, delivery.AttemptHistory().Last().Attempt);
        Assert.Equal("failure-24", delivery.AttemptHistory().Last().Error);
    }

    private static WebhookDelivery NewDelivery() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Now);
}
