using System.Text.Json;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Integrations;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit.Abstractions;

namespace AeroLink.Infrastructure.Tests;

/// <summary>
/// The specialized context has one controlled write boundary. These tests deliberately call the sync API
/// through EF's base type so the runtime guard is exercised without making the test itself a new forbidden
/// concrete-context caller. The compiler-visible Obsolete(error: true) attributes provide the source guard
/// for code that does hold an AeroLinkDbContext directly.
/// </summary>
public sealed class SaveChangesContractTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Synchronous_save_is_rejected_before_tracker_or_provider_mutation(int overload)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite(connection).Options;
        await using var db = new AeroLinkDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var program = new ProgramRecord("Sync contract", $"SYNC{Guid.NewGuid():N}"[..12]);
        db.Add(program);
        DbContext baseView = db;

        var error = overload switch
        {
            0 => Assert.Throws<InvalidOperationException>(() => baseView.SaveChanges()),
            1 => Assert.Throws<InvalidOperationException>(() => baseView.SaveChanges(true)),
            _ => Assert.Throws<InvalidOperationException>(() => baseView.SaveChanges(false)),
        };

        Assert.Contains("SaveChangesAsync", error.Message, StringComparison.Ordinal);
        Assert.Equal(EntityState.Added, db.Entry(program).State);
        Assert.Equal(0, await db.Programs.CountAsync());
    }

    [Fact]
    public async Task Async_save_false_persists_the_full_pipeline_until_the_caller_accepts_changes()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite(connection).Options;
        await using var db = new AeroLinkDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var now = DateTimeOffset.UtcNow;
        var program = new ProgramRecord("Async save contract", $"ASYNC{Guid.NewGuid():N}"[..12]);
        var project = new ProjectRecord(program.Id, "Software", "Async save contract project");
        var release = new SoftwareRelease(project.Id, "1.0", false);
        var request = new SystemChangeRequest("SRCR-00001", 0, project.Id, release.Id,
            "Async save contract", "Problem", "Analysis", "Solution", "author", now);
        db.AddRange(program, project, release, request);

        var written = await db.SaveChangesAsync(acceptAllChangesOnSuccess: false);

        Assert.True(written >= 4);
        Assert.Equal(1, request.Version);
        Assert.Equal(EntityState.Added, db.Entry(request).State);
        Assert.Equal(1, await db.IntegrationEvents.CountAsync(x => x.AggregateId == request.Id));

        // AcceptAllChanges is the caller's explicit boundary when false was requested. The next save is a
        // no-op and must not manufacture a second lifecycle event from the still-tracked aggregate.
        db.ChangeTracker.AcceptAllChanges();
        Assert.Equal(EntityState.Unchanged, db.Entry(request).State);
        Assert.Equal(0, await db.SaveChangesAsync());
        Assert.Equal(1, await db.IntegrationEvents.CountAsync(x => x.AggregateId == request.Id));
    }

    [Fact]
    public async Task Added_lifecycle_event_key_is_deduplicated_but_an_unchanged_history_event_does_not_suppress_a_new_change()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite(connection).Options;
        await using var db = new AeroLinkDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var now = DateTimeOffset.UtcNow;
        var program = new ProgramRecord("Lifecycle dedupe", "LIFE");
        var project = new ProjectRecord(program.Id, "Lifecycle project", "Lifecycle product");
        var release = new SoftwareRelease(project.Id, "1.0", false);
        var request = new SystemChangeRequest("SRCR-00001", 0, project.Id, release.Id,
            "Original title", "Problem", "Analysis", "Solution", "author", now);
        var existing = new IntegrationEvent(project.Id, "aerolink.change-request.changed", "ChangeRequest",
            request.Id, "{\"sentinel\":true}", "seed", now);
        db.AddRange(program, project, release, request, existing);

        await db.SaveChangesAsync();

        var afterInitialSave = await db.IntegrationEvents.AsNoTracking()
            .Where(x => x.AggregateId == request.Id).ToListAsync();
        var retained = Assert.Single(afterInitialSave);
        Assert.Equal(existing.Id, retained.Id);
        Assert.Equal("{\"sentinel\":true}", retained.PayloadJson);

        db.ChangeTracker.Clear();
        var trackedHistory = await db.IntegrationEvents.SingleAsync(x => x.Id == existing.Id);
        Assert.Equal(EntityState.Unchanged, db.Entry(trackedHistory).State);
        var persisted = await db.SystemChangeRequests.SingleAsync(x => x.Id == request.Id);
        persisted.UpdateDraft("author", "Revised title", "Problem", "Analysis", "Solution", [], now.AddMinutes(1));
        await db.SaveChangesAsync();

        var afterRevision = await db.IntegrationEvents.AsNoTracking()
            .Where(x => x.AggregateId == request.Id).ToListAsync();
        Assert.Equal(2, afterRevision.Count);
        Assert.Contains(afterRevision, x => x.Id == existing.Id && x.PayloadJson == "{\"sentinel\":true}");
        Assert.Contains(afterRevision, x => x.Id != existing.Id && x.EventType == "aerolink.change-request.changed");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lifecycle_events_do_not_add_a_tracker_scan_per_pending_entity(bool revisions)
    {
        static async Task<int> SaveBatchAsync(int requestCount, bool revisions)
        {
            var detections = 0;
            await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AeroLinkDbContext>()
                .UseSqlite(connection)
                .LogTo(_ => detections++, [CoreEventId.DetectChangesCompleted])
                .Options;
            await using var db = new AeroLinkDbContext(options);
            await db.Database.EnsureCreatedAsync();

            var now = DateTimeOffset.UtcNow;
            var program = new ProgramRecord("Lifecycle detection", "LDET");
            var project = new ProjectRecord(program.Id, "Lifecycle detection project", "Lifecycle detection");
            var release = new SoftwareRelease(project.Id, "1.0", false);
            db.AddRange(program, project, release);
            await db.SaveChangesAsync();
            SystemChangeRequest? source = null;
            CandidateBaseline? baseline = null;
            if (revisions)
            {
                (source, baseline) = ApprovedRevisionOrigin(project, release, now);
                db.AddRange(source, baseline);
                await db.SaveChangesAsync();
            }
            detections = 0;

            var revisionIds = new List<Guid>();
            for (var index = 0; index < requestCount; index++)
            {
                if (revisions)
                {
                    var artifact = new RequirementArtifact(project.Id, $"SYSR-{index + 1:D8}", RequirementLevel.System, now);
                    var revision = new RequirementRevision(artifact.Id, 0, "The controller shall retain state.",
                        "Safety continuity", "Test", RequirementRevisionState.Active, source!.Id, baseline!.Id, now);
                    db.AddRange(artifact, revision);
                    revisionIds.Add(revision.Id);
                }
                else
                {
                    db.SystemChangeRequests.Add(new SystemChangeRequest(
                        $"SRCR-{index + 1:D5}", 0, project.Id, release.Id,
                        $"Request {index + 1}", "Problem", "Analysis", "Solution", "author", now));
                }
            }

            await db.SaveChangesAsync();
            var saveDetections = detections;
            if (revisions)
            {
                var events = await db.IntegrationEvents.AsNoTracking().Where(x => revisionIds.Contains(x.AggregateId)).ToListAsync();
                Assert.Equal(requestCount, events.Count);
                Assert.All(events, row =>
                {
                    Assert.Equal(project.Id, row.ProjectId);
                    Assert.Equal("aerolink.requirement.revision-created", row.EventType);
                    Assert.Equal("RequirementRevision", row.AggregateType);
                    Assert.Equal("aerolink.lifecycle", row.Actor);
                });
            }
            else Assert.Equal(requestCount, await db.IntegrationEvents.CountAsync());
            return saveDetections;
        }

        var oneRequest = await SaveBatchAsync(1, revisions);
        var twentyRequests = await SaveBatchAsync(20, revisions);
        output.WriteLine($"Lifecycle scans: revisions={revisions}, batch1={oneRequest}, batch20={twentyRequests}");

        Assert.True(oneRequest > 0, "The EF diagnostic probe must observe real change detection.");
        Assert.True(twentyRequests <= oneRequest + 2,
            $"A 20-{(revisions ? "revision" : "request")} lifecycle batch caused {twentyRequests} full graph scans versus {oneRequest} for one entity.");
    }

    [Fact]
    public async Task Revision_lifecycle_events_preserve_projects_payloads_fallback_and_later_save_freshness()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite(connection).Options;
        await using var db = new AeroLinkDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var now = new DateTimeOffset(2024, 1, 8, 14, 0, 0, TimeSpan.Zero);

        async Task<(ProjectRecord Project, SystemChangeRequest Request, CandidateBaseline Baseline)> AddProject(string code)
        {
            var program = new ProgramRecord("Revision lifecycle", code);
            var project = new ProjectRecord(program.Id, "Lifecycle project", "Lifecycle product");
            var release = new SoftwareRelease(project.Id, "1.0", false);
            var (request, baseline) = ApprovedRevisionOrigin(project, release, now);
            db.AddRange(program, project, release, request, baseline);
            await db.SaveChangesAsync();
            return (project, request, baseline);
        }

        var first = await AddProject("LIFEA");
        var second = await AddProject("LIFEB");
        var unchanged = new RequirementArtifact(first.Project.Id, "SYSR-00000002", RequirementLevel.System, now);
        var modified = new RequirementArtifact(second.Project.Id, "SYSR-00000001", RequirementLevel.System, now);
        var fallback = new RequirementArtifact(second.Project.Id, "SYSR-00000002", RequirementLevel.System, now);
        db.AddRange(unchanged, modified, fallback);
        await db.SaveChangesAsync();
        db.Entry(fallback).State = EntityState.Detached;
        var subscriptionA = new WebhookSubscription(first.Project.Id, "Project A revisions", "https://example.test/a",
            "[\"aerolink.requirement.revision-created\"]", "owned-secret", "author", now);
        var subscriptionB = new WebhookSubscription(second.Project.Id, "Project B revisions", "https://example.test/b",
            "[\"aerolink.requirement.revision-created\"]", "owned-secret", "author", now);
        db.AddRange(subscriptionA, subscriptionB);
        await db.SaveChangesAsync(); // Admission uses persisted subscriptions, not Added rows in this save.
        db.Entry(modified).State = EntityState.Modified; // Existing artifact values remain unchanged.
        Assert.Equal(EntityState.Unchanged, db.Entry(unchanged).State);
        Assert.Equal(EntityState.Modified, db.Entry(modified).State);
        Assert.DoesNotContain(db.ChangeTracker.Entries<RequirementArtifact>(), x => x.Entity.Id == fallback.Id);
        var added = new RequirementArtifact(first.Project.Id, "SYSR-00000001", RequirementLevel.System, now);
        db.Add(added);
        var rows = new[]
        {
            (Artifact: added, Revision: 0, State: RequirementRevisionState.Superseded, Origin: first, Subscription: subscriptionA.Id),
            (Artifact: added, Revision: 1, State: RequirementRevisionState.Active, Origin: first, Subscription: subscriptionA.Id),
            (Artifact: unchanged, Revision: 0, State: RequirementRevisionState.Active, Origin: first, Subscription: subscriptionA.Id),
            (Artifact: modified, Revision: 0, State: RequirementRevisionState.Active, Origin: second, Subscription: subscriptionB.Id),
            (Artifact: fallback, Revision: 0, State: RequirementRevisionState.Active, Origin: second, Subscription: subscriptionB.Id),
        };
        var expected = rows.Select(row => (row, revision: new RequirementRevision(row.Artifact.Id, row.Revision,
            "The controller shall retain state.", "Safety continuity", "Test", row.State,
            row.Origin.Request.Id, row.Origin.Baseline.Id, now))).ToArray();
        db.AddRange(expected.Select(x => x.revision));
        await db.SaveChangesAsync();
        Assert.True(db.ChangeTracker.AutoDetectChangesEnabled);
        Assert.DoesNotContain(db.ChangeTracker.Entries<RequirementArtifact>(), x => x.Entity.Id == fallback.Id);

        async Task AssertRevisionEvent(RequirementRevision revision, Guid artifactId, int number, string state,
            Guid projectId, Guid requestId, Guid baselineId, Guid? subscriptionId)
        {
            var record = Assert.Single(await db.IntegrationEvents.AsNoTracking()
                .Where(x => x.AggregateId == revision.Id).ToListAsync());
            Assert.Equal(projectId, record.ProjectId);
            Assert.Equal("aerolink.requirement.revision-created", record.EventType);
            Assert.Equal("RequirementRevision", record.AggregateType);
            Assert.Equal("aerolink.lifecycle", record.Actor);
            using var payload = JsonDocument.Parse(record.PayloadJson);
            Assert.Equal(7, payload.RootElement.EnumerateObject().Count());
            Assert.Equal(artifactId, payload.RootElement.GetProperty("artifactId").GetGuid());
            Assert.Equal(number, payload.RootElement.GetProperty("revision").GetInt32());
            Assert.Equal(state, payload.RootElement.GetProperty("state").GetString());
            Assert.Equal("ChangeRequest", payload.RootElement.GetProperty("originKind").GetString());
            Assert.Equal(requestId, payload.RootElement.GetProperty("sourceChangeRequestId").GetGuid());
            Assert.Equal(JsonValueKind.Null, payload.RootElement.GetProperty("sourceBaselineImportId").ValueKind);
            Assert.Equal(baselineId, payload.RootElement.GetProperty("effectiveBaselineId").GetGuid());
            var deliveries = await db.WebhookDeliveries.AsNoTracking().Where(x => x.IntegrationEventId == record.Id).ToListAsync();
            if (subscriptionId is Guid subscription)
            {
                var delivery = Assert.Single(deliveries);
                Assert.Equal(projectId, delivery.ProjectId);
                Assert.Equal(subscription, delivery.SubscriptionId);
            }
            else Assert.Empty(deliveries);
        }

        foreach (var (row, revision) in expected)
            await AssertRevisionEvent(revision, row.Artifact.Id, row.Revision,
                row.Revision == 0 && row.Artifact == added ? "Superseded" : "Active", row.Origin.Project.Id,
                row.Origin.Request.Id, row.Origin.Baseline.Id, row.Subscription);
        Assert.Equal(5, await db.IntegrationEvents.CountAsync(x => x.EventType == "aerolink.requirement.revision-created"));

        var later = await AddProject("LIFEC");
        var laterArtifact = new RequirementArtifact(later.Project.Id, "SYSR-00000001", RequirementLevel.System, now);
        var laterRevision = new RequirementRevision(laterArtifact.Id, 0, "The controller shall retain state.",
            "Safety continuity", "Test", RequirementRevisionState.Active, later.Request.Id, later.Baseline.Id, now);
        db.AddRange(laterArtifact, laterRevision);
        await db.SaveChangesAsync();
        await AssertRevisionEvent(laterRevision, laterArtifact.Id, 0, "Active", later.Project.Id,
            later.Request.Id, later.Baseline.Id, null);
        Assert.Equal(6, await db.IntegrationEvents.CountAsync(x => x.EventType == "aerolink.requirement.revision-created"));
    }

    private static (SystemChangeRequest Request, CandidateBaseline Baseline) ApprovedRevisionOrigin(
        ProjectRecord project, SoftwareRelease release, DateTimeOffset now)
    {
        var request = new SystemChangeRequest("SRCR-00001", 0, project.Id, release.Id,
            "Seed requirements", "Need", "Analysis", "Solution", "author", now);
        request.AddRequirementChange("author", "SYSR-00000001", 0, RequirementLevel.System,
            RequirementChangeKind.Introduce, "The controller shall retain state.", "Safety continuity", "Test", now);
        request.SubmitForReview("author", [new("reviewer", "Reviewer")], now);
        request.ApproveActiveStage("reviewer", now);
        var baseline = new CandidateBaseline("BL-00000001", 0, project.Id, release.Id, null, "Lifecycle baseline", "cm", now);
        baseline.Select(request, "cm", now);
        baseline.Freeze("cm", now);
        return (request, baseline);
    }

    [Fact]
    public async Task A_transaction_failure_can_be_retried_without_duplicate_outbox_rows()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite(connection).Options;
        await using var db = new AeroLinkDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var first = new ProgramRecord("Retry one", "RETRY");
        var duplicate = new ProgramRecord("Retry duplicate", "RETRY");
        db.AddRange(first, duplicate);
        db.UserNotifications.Add(new UserNotification(
            first.Id, "missing-recipient", "SaveRetry", "Save retry", "Save retry", "scr:retry", null,
            DateTimeOffset.UtcNow));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.Remove(duplicate);
        await db.SaveChangesAsync();

        Assert.Equal(1, await db.Programs.CountAsync());
        Assert.Single(await db.UserNotifications.AsNoTracking().ToListAsync());
        Assert.Single(await db.NotificationDeliveries.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Cancellation_before_the_provider_write_is_atomic_and_retryable()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite(connection).Options;
        await using var db = new AeroLinkDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var program = new ProgramRecord("Cancelled save", $"CANCEL{Guid.NewGuid():N}"[..12]);
        db.Add(program);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => db.SaveChangesAsync(cancellation.Token));
        Assert.Equal(0, await db.Programs.CountAsync());
        Assert.Equal(EntityState.Added, db.Entry(program).State);

        await db.SaveChangesAsync();
        Assert.Equal(1, await db.Programs.CountAsync());
    }
}
