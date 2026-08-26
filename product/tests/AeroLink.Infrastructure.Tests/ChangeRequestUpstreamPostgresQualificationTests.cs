using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AeroLink.Infrastructure.Tests;

[CollectionDefinition("Issue786Postgres", DisableParallelization = true)]
public sealed class Issue786PostgresCollection;

/// <summary>PostgreSQL-only proof for the immutable upstream-answer rows added by #786 Phase 1.</summary>
[Collection("Issue786Postgres")]
public sealed class ChangeRequestUpstreamPostgresQualificationTests
{
    private const string DatabaseName = "aerolink_786_qualify";

    [Issue786PostgresFact]
    public async Task Clean_install_guards_active_links_and_history_without_blocking_draft_cascade()
    {
        var connection = QualificationConnectionOrThrow();
        await using var db = new AeroLinkDbContext(new DbContextOptionsBuilder<AeroLinkDbContext>()
            .UseNpgsql(connection).Options);
        await db.Database.EnsureDeletedAsync();
        await db.Database.MigrateAsync();

        var now = DateTimeOffset.UtcNow;
        var tag = Guid.NewGuid().ToString("N")[..8];
        var program = new ProgramRecord($"Upstream trace qualification {tag}", $"UQ{tag}");
        var project = new ProjectRecord(program.Id, "PostgreSQL trace qualification", "Disposable software");
        var release = new SoftwareRelease(project.Id, "1.7", false);
        var source = new SystemChangeRequest("SRCR-78610", 0, project.Id, release.Id,
            "Exact upstream", "Problem", "Analysis", "Solution", "author", now);
        var protectedChild = new SystemChangeRequest("HLRCR-78611", 0, project.Id, release.Id,
            "Protected child", "Problem", "Analysis", "Solution", "author", now,
            ChangeRequestType.Software, softwareLevel: RequirementLevel.HighLevel);
        protectedChild.AddUpstreamLink("author", source.Id, source.DisplayNumber, release.Id, release.Version,
            "The exact System change controls this work.", now);
        var protectedLinkId = protectedChild.UpstreamLinks.Single().Id;
        var protectedHistoryId = protectedChild.UpstreamHistory.Single().Id;
        db.AddRange(program, project, release, source, protectedChild);
        await db.SaveChangesAsync();

        var draftDelete = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM change_request_upstream_links WHERE \"Id\" = {protectedLinkId}"));
        Assert.Contains("controlled history", draftDelete.MessageText, StringComparison.OrdinalIgnoreCase);
        Assert.True(await db.ChangeRequestUpstreamLinks.AsNoTracking().AnyAsync(x => x.Id == protectedLinkId));

        protectedChild.ChangeUpstreamLinkRationale("author", protectedLinkId,
            "The exact System change remains the controlling source after review of the trace rationale.", now.AddMinutes(1));
        await db.SaveChangesAsync();
        var protectedReplacementId = protectedChild.UpstreamLinks.Single().Id;
        Assert.NotEqual(protectedLinkId, protectedReplacementId);
        Assert.Contains(protectedChild.UpstreamHistory,
            x => x.Action == "Changed" && x.UpstreamLinkId == protectedLinkId);

        var replacementRationale = "x";
        var linkUpdate = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE change_request_upstream_links SET \"Rationale\" = {replacementRationale} WHERE \"Id\" = {protectedReplacementId}"));
        Assert.Contains("immutable", linkUpdate.MessageText, StringComparison.OrdinalIgnoreCase);

        var inReviewState = "InReview";
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE system_change_requests SET \"State\" = {inReviewState} WHERE \"Id\" = {protectedChild.Id}");

        async Task AssertScalarMutationRejected(Func<Task<int>> mutation)
        {
            var exception = await Assert.ThrowsAsync<PostgresException>(mutation);
            Assert.Contains("leaves Draft", exception.MessageText, StringComparison.OrdinalIgnoreCase);
        }

        await AssertScalarMutationRejected(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE system_change_requests SET \"NoUpstreamRationale\" = {"raw rationale"} WHERE \"Id\" = {protectedChild.Id}"));
        await AssertScalarMutationRejected(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE system_change_requests SET \"NoUpstreamStatedBy\" = {"raw.actor"} WHERE \"Id\" = {protectedChild.Id}"));
        await AssertScalarMutationRejected(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE system_change_requests SET \"NoUpstreamStatedAt\" = {now.AddHours(1)} WHERE \"Id\" = {protectedChild.Id}"));
        await AssertScalarMutationRejected(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE system_change_requests SET \"InheritedUpstreamContextJson\" = {"{}"} WHERE \"Id\" = {protectedChild.Id}"));
        await AssertScalarMutationRejected(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE system_change_requests SET \"InheritedFromChangeRequestId\" = {source.Id} WHERE \"Id\" = {protectedChild.Id}"));
        await AssertScalarMutationRejected(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE system_change_requests SET \"InheritedAt\" = {now.AddHours(1)} WHERE \"Id\" = {protectedChild.Id}"));
        await AssertScalarMutationRejected(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE system_change_requests SET \"UpstreamAnswerAffirmed\" = {false} WHERE \"Id\" = {protectedChild.Id}"));
        await AssertScalarMutationRejected(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE system_change_requests SET \"UpstreamAnswerAffirmedBy\" = {"raw.actor"} WHERE \"Id\" = {protectedChild.Id}"));
        await AssertScalarMutationRejected(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE system_change_requests SET \"UpstreamAnswerAffirmedAt\" = {now.AddHours(1)} WHERE \"Id\" = {protectedChild.Id}"));

        var approvedState = "Approved";
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE system_change_requests SET \"State\" = {approvedState} WHERE \"Id\" = {protectedChild.Id}");
        await AssertScalarMutationRejected(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE system_change_requests SET \"NoUpstreamRationale\" = {"signed rewrite"} WHERE \"Id\" = {protectedChild.Id}"));

        var signedDelete = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM change_request_upstream_links WHERE \"Id\" = {protectedReplacementId}"));
        Assert.Contains("Draft", signedDelete.MessageText, StringComparison.OrdinalIgnoreCase);
        var historyDelete = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM change_request_upstream_history WHERE \"Id\" = {protectedHistoryId}"));
        Assert.Contains("immutable", historyDelete.MessageText, StringComparison.OrdinalIgnoreCase);

        db.ChangeTracker.Clear();
        var cascadeChild = new SystemChangeRequest("HLRCR-78612", 0, project.Id, release.Id,
            "Abandoned Draft", "Problem", "Analysis", "Solution", "author", now,
            ChangeRequestType.Software, softwareLevel: RequirementLevel.HighLevel);
        cascadeChild.AddUpstreamLink("author", source.Id, source.DisplayNumber, release.Id, release.Version,
            "The Draft will be deleted during qualification.", now);
        var cascadeId = cascadeChild.Id;
        db.Add(cascadeChild);
        await db.SaveChangesAsync();
        await db.SystemChangeRequests.Where(x => x.Id == cascadeId).ExecuteDeleteAsync();

        Assert.False(await db.SystemChangeRequests.AsNoTracking().AnyAsync(x => x.Id == cascadeId));
        Assert.False(await db.ChangeRequestUpstreamLinks.AsNoTracking().AnyAsync(x => x.ChangeRequestId == cascadeId));
        Assert.False(await db.ChangeRequestUpstreamHistory.AsNoTracking().AnyAsync(x => x.ChangeRequestId == cascadeId));
    }

    private static string? ResolveQualificationConnection()
    {
        var dedicated = Environment.GetEnvironmentVariable("AEROLINK_786_CONNECTION");
        if (!string.IsNullOrWhiteSpace(dedicated)) return dedicated;
        var shared = Environment.GetEnvironmentVariable("AEROLINK_MIGRATIONS_CONNECTION");
        return string.IsNullOrWhiteSpace(shared) ? null : shared;
    }

    private static string QualificationConnectionOrThrow()
    {
        var connection = ResolveQualificationConnection();
        if (string.IsNullOrWhiteSpace(connection))
            throw new InvalidOperationException(
                "Issue #786 PostgreSQL qualification requires AEROLINK_786_CONNECTION or AEROLINK_MIGRATIONS_CONNECTION.");
        var builder = new NpgsqlConnectionStringBuilder(connection);
        var host = (builder.Host ?? string.Empty).Trim().Trim('[', ']');
        if (!string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Issue #786 PostgreSQL qualification requires a loopback host.");
        if (builder.Port == 54329)
            throw new InvalidOperationException("Issue #786 qualification refuses the protected PostgreSQL port 54329.");
        if (!string.Equals(builder.Database, DatabaseName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Issue #786 PostgreSQL qualification requires the dedicated database {DatabaseName}.");
        return connection;
    }

    private sealed class Issue786PostgresFactAttribute : FactAttribute
    {
        public Issue786PostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(ResolveQualificationConnection()))
                Skip = "Issue #786 PostgreSQL qualification skipped: set its dedicated disposable connection.";
        }
    }
}
