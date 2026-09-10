using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Integrations;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AeroLink.Infrastructure.Tests;

/// <summary>
/// The specialized context has one controlled write boundary. These tests deliberately call the sync API
/// through EF's base type so the runtime guard is exercised without making the test itself a new forbidden
/// concrete-context caller. The compiler-visible Obsolete(error: true) attributes provide the source guard
/// for code that does hold an AeroLinkDbContext directly.
/// </summary>
public sealed class SaveChangesContractTests
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

    [Fact]
    public async Task Lifecycle_event_deduplication_does_not_add_a_tracker_scan_per_pending_request()
    {
        static async Task<int> SaveRequestBatchAsync(int requestCount)
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
            detections = 0;

            for (var index = 0; index < requestCount; index++)
            {
                db.SystemChangeRequests.Add(new SystemChangeRequest(
                    $"SRCR-{index + 1:D5}", 0, project.Id, release.Id,
                    $"Request {index + 1}", "Problem", "Analysis", "Solution", "author", now));
            }

            await db.SaveChangesAsync();
            var saveDetections = detections;
            Assert.Equal(requestCount, await db.IntegrationEvents.CountAsync());
            return saveDetections;
        }

        var oneRequest = await SaveRequestBatchAsync(1);
        var twentyRequests = await SaveRequestBatchAsync(20);

        Assert.True(oneRequest > 0, "The EF diagnostic probe must observe real change detection.");
        Assert.True(twentyRequests <= oneRequest + 2,
            $"A 20-request lifecycle batch caused {twentyRequests} full graph scans versus {oneRequest} for one request.");
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
