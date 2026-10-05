using System.Net;
using System.Net.Sockets;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Notifications;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Infrastructure.Tests;

// Primary admission owners: withheld-era event selection, original age after configuration recovery,
// and bounded progress past old epochs/ended obligations. TLS/process owners qualify sockets separately.
public sealed class NotificationAdmissionSafetyTests
{
    [Fact]
    public async Task Widening_event_selection_does_not_automatically_enroll_an_event_raised_while_disabled()
    {
        using var fixture = await NotificationInfrastructureFixture.CreateAsync();
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        await fixture.ActivateCaptureAsync(((IPEndPoint)listener.LocalEndpoint).Port);
        await SettingsAsync(fixture, []);
        await using (var db = fixture.NewDb()) { var source = fixture.Assignment(); db.AddRange(source.Requirement, source.Assignment, source.Notice); await db.SaveChangesAsync(); }
        await SettingsAsync(fixture, NotificationOperationsService.InitialEventTypes);
        await fixture.DispatchAsync();
        await using var asserted = fixture.NewDb(); var root = await asserted.NotificationDeliveries.SingleAsync();
        Assert.Equal(NotificationDeliveryState.HeldAdmission, root.State); Assert.Null(root.AdmissionEpochId);
        Assert.Empty(await asserted.NotificationDeliveryGenerations.ToListAsync()); Assert.False(listener.Pending());
    }

    [Fact]
    public async Task Compatible_configuration_recovery_retains_the_original_admission_deadline()
    {
        using var fixture = await NotificationInfrastructureFixture.CreateAsync();
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        await fixture.ActivateCaptureAsync(((IPEndPoint)listener.LocalEndpoint).Port);
        var configuration = fixture.Services.GetRequiredService<IConfiguration>();
        configuration["Notifications:Smtp:Host"] = "not-a-capture-host.example";
        Guid rootId;
        await using (var db = fixture.NewDb())
        {
            var source = fixture.Assignment(); db.AddRange(source.Requirement, source.Assignment, source.Notice); await db.SaveChangesAsync();
            rootId = (await db.NotificationDeliveries.SingleAsync()).Id;
            // Age only this owned disposable snapshot to represent an installation recovering after a day.
            var admittedAt = DateTimeOffset.UtcNow.AddHours(-25);
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE notification_deliveries SET \"CreatedAt\"={admittedAt} WHERE \"Id\"={rootId}");
        }
        await fixture.DispatchAsync();
        await using (var blocked = fixture.NewDb()) Assert.Empty(await blocked.NotificationDeliveryGenerations.ToListAsync());
        configuration["Notifications:Smtp:Host"] = "";
        await fixture.DispatchAsync();
        await using var asserted = fixture.NewDb(); var root = await asserted.NotificationDeliveries.SingleAsync(); var generation = await asserted.NotificationDeliveryGenerations.SingleAsync();
        Assert.Equal(root.CreatedAt.AddHours(24).UtcTicks, generation.OriginalDeadlineTicks);
        Assert.Equal(NotificationGenerationState.RetryExhausted, generation.State); Assert.Empty(await asserted.NotificationPhysicalAttempts.ToListAsync()); Assert.False(listener.Pending());
    }

    [Fact]
    public async Task Prior_epoch_roots_cannot_starve_current_eligible_work_in_a_bounded_batch()
    {
        using var fixture = await NotificationInfrastructureFixture.CreateAsync(); await using var relay = new OwnedNotificationCaptureRelay();
        await fixture.ActivateCaptureAsync(relay.Port);
        await AddTasksAsync(fixture, 25, ended: false);
        using (var scope = fixture.Services.CreateScope())
        {
            var state = await scope.ServiceProvider.GetRequiredService<AeroLink.Infrastructure.Persistence.AeroLinkDbContext>().NotificationInstallationStates.SingleAsync();
            await scope.ServiceProvider.GetRequiredService<NotificationOperationsService>().ControlAsync("admin", new(Guid.NewGuid(), state.Version, "Activate", NotificationMode.Capture), default);
        }
        var current = await AddTasksAsync(fixture, 1, ended: false, identifier: "SYSR-00000002");
        Assert.Equal(1, (await fixture.DispatchAsync()).Sent); await relay.Message;
        await using var asserted = fixture.NewDb();
        Assert.Single(await asserted.NotificationDeliveryGenerations.ToListAsync());
        var root = await asserted.NotificationDeliveries.SingleAsync(x => x.NotificationId == current);
        Assert.Equal(root.Id, (await asserted.NotificationDeliveryGenerations.SingleAsync()).DeliveryId);
    }

    [Fact]
    public async Task Ended_obligations_are_dispositioned_so_the_next_batch_progresses()
    {
        using var fixture = await NotificationInfrastructureFixture.CreateAsync(); await using var relay = new OwnedNotificationCaptureRelay();
        await fixture.ActivateCaptureAsync(relay.Port); await AddTasksAsync(fixture, 25, ended: true);
        await AddTasksAsync(fixture, 1, ended: false, identifier: "SYSR-00000002");
        Assert.Equal(0, (await fixture.DispatchAsync()).Sent);
        await using (var asserted = fixture.NewDb()) Assert.Equal(25, await asserted.NotificationDeliveries.CountAsync(x => x.State == NotificationDeliveryState.Suppressed));
        Assert.Equal(1, (await fixture.DispatchAsync()).Sent); await relay.Message;
    }

    private static async Task SettingsAsync(NotificationInfrastructureFixture fixture, string[] events)
    {
        using var scope = fixture.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<AeroLink.Infrastructure.Persistence.AeroLinkDbContext>();
        var state = await db.NotificationInstallationStates.SingleAsync();
        await scope.ServiceProvider.GetRequiredService<NotificationOperationsService>().SaveSettingsAsync("admin",
            new(Guid.NewGuid(), state.Version, NotificationMode.Capture, null, 2525, null, null, null, null, EventTypes: events), default);
    }
    private static async Task<Guid> AddTasksAsync(NotificationInfrastructureFixture fixture, int count, bool ended, string identifier = "SYSR-00000001")
    {
        await using var db = fixture.NewDb(); var now = DateTimeOffset.UtcNow;
        var artifact = new RequirementArtifact(fixture.ProjectId, identifier, RequirementLevel.System, now); db.Add(artifact); Guid last = default;
        for (var index = 0; index < count; index++)
        {
            var task = new ArtifactAssignment(fixture.ProjectId, "Requirement", artifact.Id, null, "approver.user", "Distinct task " + index, "", null, "author.user", now);
            var notice = new UserNotification(fixture.ProjectId, "approver.user", "RequirementAssignment", "Task", "", "", artifact.Id, DateTimeOffset.UtcNow);
            notice.BindContext(NotificationContext.RequirementAssignment(notice, artifact, task));
            if (ended) task.Complete("approver.user", task.Version, now);
            db.AddRange(task, notice); last = notice.Id;
        }
        await db.SaveChangesAsync(); return last;
    }
}
