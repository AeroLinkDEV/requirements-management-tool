using AeroLink.Domain.Common;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

/// <summary>
/// The claim, the lease, and the transitions a stale worker must not win.
///
/// Selecting a queued job and writing Running were two statements, so two instances read the same oldest job
/// and both started it. A crash after the write left a job Running with nobody working on it and no way to
/// tell. And `Complete` accepted any state, so a worker holding a stale entity could report success for work
/// an operator had already cancelled.
///
/// These use a real SQLite file rather than a shared in-memory database, because the claim is a conditional
/// UPDATE and what is being tested is that exactly one of two concurrent ones takes effect.
/// </summary>
public sealed class JobLeaseTests : IAsyncLifetime
{
    private string _path = string.Empty;
    private DbContextOptions<AeroLinkDbContext> _options = null!;
    private readonly Guid _projectId = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        _path = Path.Combine(Path.GetTempPath(), $"aerolink-job-lease-{Guid.NewGuid():N}.db");
        _options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={_path};Pooling=False").Options;
        await using var db = new AeroLinkDbContext(_options);
        await db.Database.EnsureCreatedAsync();
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try { if (File.Exists(_path)) File.Delete(_path); } catch (IOException) { }
        return Task.CompletedTask;
    }

    private AeroLinkDbContext Context() => new(_options);

    private async Task<Guid> QueueAsync(string type = "BackgroundRepositoryExport")
    {
        await using var db = Context();
        var job = new EnterpriseOperationJob(_projectId, type, "{}", 1, "operator", DateTimeOffset.UtcNow, Guid.NewGuid().ToString());
        db.EnterpriseOperationJobs.Add(job);
        await db.SaveChangesAsync();
        return job.Id;
    }

    /// <summary>
    /// The worker's own claim, called directly so two of them can be interleaved deliberately.
    ///
    /// This used to be a copy of the statement written out here, which is worse than useless: a copy keeps
    /// passing while the real one diverges. The recovery sweep's untranslatable date comparison reached a green
    /// suite for exactly that reason.
    /// </summary>
    private static Task<bool> TryClaimAsync(AeroLinkDbContext db, Guid id, long version, string worker, DateTimeOffset now)
        => EnterpriseJobWorker.TryClaimAsync(db, id, version, worker, now);

    [Fact]
    public async Task Two_workers_reading_the_same_queued_job_cannot_both_claim_it()
    {
        var id = await QueueAsync();
        var now = DateTimeOffset.UtcNow;

        // Both read the row first, which is exactly the interleaving that used to double-run the job.
        await using var first = Context();
        await using var second = Context();
        var seenByFirst = await first.EnterpriseOperationJobs.AsNoTracking().Where(x => x.Id == id).Select(x => x.Version).SingleAsync();
        var seenBySecond = await second.EnterpriseOperationJobs.AsNoTracking().Where(x => x.Id == id).Select(x => x.Version).SingleAsync();
        Assert.Equal(seenByFirst, seenBySecond);

        var wonByFirst = await TryClaimAsync(first, id, seenByFirst, "worker-a", now);
        var wonBySecond = await TryClaimAsync(second, id, seenBySecond, "worker-b", now);

        Assert.True(wonByFirst);
        Assert.False(wonBySecond);

        await using var check = Context();
        var job = await check.EnterpriseOperationJobs.AsNoTracking().SingleAsync(x => x.Id == id);
        Assert.Equal(EnterpriseJobState.Running, job.State);
        Assert.Equal("worker-a", job.ClaimedBy);
        // One claim, so one attempt. Two would mean the job had been started twice.
        Assert.Equal(1, job.Attempt);
    }

    [Fact]
    public async Task An_abandoned_job_returns_to_the_queue_once_its_lease_expires()
    {
        var id = await QueueAsync();
        await using var db = Context();
        var job = await db.EnterpriseOperationJobs.SingleAsync(x => x.Id == id);
        var claimedAt = DateTimeOffset.UtcNow;
        job.Claim("worker-that-dies", claimedAt, TimeSpan.FromMinutes(5));
        await db.SaveChangesAsync();

        // Before expiry the claim is still believed, so nothing may take it.
        Assert.False(job.LeaseExpired(claimedAt.AddMinutes(4)));
        var after = claimedAt.AddMinutes(6);
        Assert.True(job.LeaseExpired(after));

        job.RecoverExpiredLease(after, EnterpriseJobWorker.MaximumAttempts);
        await db.SaveChangesAsync();

        Assert.Equal(EnterpriseJobState.Preview, job.State);
        Assert.Null(job.ClaimedBy);
        Assert.Null(job.LeaseExpiresAt);
        Assert.Contains("stopped responding", job.LastError);
        // The attempt is kept, and the reason is in the history rather than only in the current field.
        Assert.Equal(1, job.Attempt);
        Assert.Single(job.ErrorHistory());
    }

    [Fact]
    public async Task An_abandoned_job_stops_being_requeued_once_it_has_used_its_attempts()
    {
        var id = await QueueAsync();
        await using var db = Context();
        var job = await db.EnterpriseOperationJobs.SingleAsync(x => x.Id == id);

        for (var attempt = 1; attempt <= EnterpriseJobWorker.MaximumAttempts; attempt++)
        {
            var claimedAt = DateTimeOffset.UtcNow.AddMinutes(attempt * 10);
            job.Claim($"worker-{attempt}", claimedAt, TimeSpan.FromMinutes(5));
            job.RecoverExpiredLease(claimedAt.AddMinutes(6), EnterpriseJobWorker.MaximumAttempts);
        }
        await db.SaveChangesAsync();

        // A job that cannot be finished must stop cycling and become something an operator can see.
        Assert.Equal(EnterpriseJobState.Failed, job.State);
        Assert.Equal(EnterpriseJobWorker.MaximumAttempts, job.Attempt);
        Assert.Equal(EnterpriseJobWorker.MaximumAttempts, job.ErrorHistory().Count);
    }

    [Fact]
    public async Task A_cancelled_job_cannot_be_completed_by_a_worker_holding_stale_state()
    {
        var id = await QueueAsync();
        await using var worker = Context();
        var held = await worker.EnterpriseOperationJobs.SingleAsync(x => x.Id == id);
        held.Claim("worker-a", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));
        await worker.SaveChangesAsync();

        // An operator cancels while the worker is still rendering.
        await using (var operatorContext = Context())
        {
            var cancelling = await operatorContext.EnterpriseOperationJobs.SingleAsync(x => x.Id == id);
            cancelling.Cancel(DateTimeOffset.UtcNow);
            await operatorContext.SaveChangesAsync();
        }

        // The worker finishes and tries to record success against the entity it has been holding. Re-reading is
        // what the worker now does, and the re-read job refuses the outcome because it is no longer Running.
        await using var reread = Context();
        var current = await reread.EnterpriseOperationJobs.SingleAsync(x => x.Id == id);
        Assert.Equal(EnterpriseJobState.Cancelled, current.State);
        Assert.Throws<DomainException>(() => current.Complete(1, 0, "{}", DateTimeOffset.UtcNow));

        await using var check = Context();
        var final = await check.EnterpriseOperationJobs.AsNoTracking().SingleAsync(x => x.Id == id);
        Assert.Equal(EnterpriseJobState.Cancelled, final.State);
    }

    [Fact]
    public async Task A_shutdown_returns_the_job_without_spending_it_as_a_failure()
    {
        var id = await QueueAsync();
        await using var db = Context();
        var job = await db.EnterpriseOperationJobs.SingleAsync(x => x.Id == id);
        job.Claim("worker-a", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));

        job.ReleaseForShutdown(DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();

        // Queued again, not failed: stopping a process is not the job going wrong. The diagnostic survives.
        Assert.Equal(EnterpriseJobState.Preview, job.State);
        Assert.Null(job.ClaimedBy);
        Assert.Contains("shut down", job.LastError);
        Assert.Single(job.ErrorHistory());
    }

    [Fact]
    public async Task A_retry_keeps_what_the_earlier_attempts_reported()
    {
        var id = await QueueAsync();
        await using var db = Context();
        var job = await db.EnterpriseOperationJobs.SingleAsync(x => x.Id == id);

        job.Claim("worker-a", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));
        job.Fail("The renderer ran out of memory.", DateTimeOffset.UtcNow);
        job.Retry(DateTimeOffset.UtcNow);
        job.Claim("worker-b", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));
        job.Fail("The storage volume was unavailable.", DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();

        // Two attempts, two reasons. LastError alone erased the first, which is the history that says whether
        // this is one problem recurring or two different ones.
        var history = job.ErrorHistory();
        Assert.Equal(2, history.Count);
        Assert.Contains("out of memory", history[0].Error);
        Assert.Contains("storage volume", history[1].Error);
        Assert.Equal(1, history[0].Attempt);
        Assert.Equal(2, history[1].Attempt);
        Assert.Equal(2, job.Attempt);
    }

    [Fact]
    public async Task A_claim_survives_being_read_back_from_the_database()
    {
        var id = await QueueAsync();
        var now = DateTimeOffset.UtcNow;
        await using (var db = Context())
        {
            var job = await db.EnterpriseOperationJobs.SingleAsync(x => x.Id == id);
            job.Claim("worker-a", now, TimeSpan.FromMinutes(5));
            await db.SaveChangesAsync();
        }

        // The lease is only useful if it is durable — recovery after a restart reads it from the database.
        await using var reopened = Context();
        var stored = await reopened.EnterpriseOperationJobs.AsNoTracking().SingleAsync(x => x.Id == id);
        Assert.Equal("worker-a", stored.ClaimedBy);
        Assert.NotNull(stored.ClaimedAt);
        Assert.NotNull(stored.LeaseExpiresAt);
        Assert.True(stored.LeaseExpired(now.AddMinutes(6)));
    }
}
