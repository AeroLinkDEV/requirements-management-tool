using System.Data;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using AeroLink.Infrastructure.Tests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace AeroLink.Api.Tests;

/// <summary>
/// The API suite normally uses SQLite for speed. This deliberately bounded qualification runs the exact
/// attachment/check-in arbitration through PostgreSQL as well: an independent connection holds the checkout
/// row lock while both HTTP requests are in flight, then releases it so both contenders must pass through the
/// same serialized boundary. It is opt-in and refuses the persistent demo port/database.
/// </summary>
[CollectionDefinition("Issue870Postgres", DisableParallelization = true)]
public sealed class Issue870PostgresCollection : ICollectionFixture<object>;

[Trait("Category", "PostgresQualification")]
[Collection("Issue870Postgres")]
public sealed class ProblemReportPostgresQualificationTests
{
    private const string DatabaseName = "aerolink_870_qualify";

    [DisposablePostgresFact]
    public async Task Postgres_serializes_check_in_and_attachment_without_500_or_stale_manifest()
    {
        await using var database = await DisposablePostgresDatabase.CreateAsync(DatabaseName);
        var connection = database.ConnectionString;
        using var factory = new AeroLinkApiFactory(postgresConnection: connection);
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var (projectId, releaseId, _) = await ProblemReportCheckoutApiTests.SeedAsync(factory, "PRPG870");
        var reportId = await ProblemReportCheckoutApiTests.RaiseAsync(client, projectId, releaseId);

        using var checkout = await client.PostAsJsonAsync("/api/controlled-editing/checkout",
            new { artifactType = "ProblemReport", artifactId = reportId, leaseMinutes = 15 });
        Assert.Equal(HttpStatusCode.Created, checkout.StatusCode);
        var session = await checkout.Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = session.GetProperty("id").GetGuid();
        var sessionVersion = session.GetProperty("version").GetInt64();

        // Hold the exact row used by both endpoints. Requests are launched while this transaction owns it,
        // so this is a real provider-level overlap rather than two quick calls that happened to be scheduled
        // one after the other by the test runner.
        await using var holder = new NpgsqlConnection(connection);
        await holder.OpenAsync();
        await using var holdTransaction = await holder.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        await using (var lockCommand = new NpgsqlCommand(
            "SELECT \"Id\" FROM artifact_edit_sessions WHERE \"Id\" = @sessionId FOR UPDATE",
            holder, holdTransaction))
        {
            lockCommand.Parameters.AddWithValue("sessionId", sessionId);
            Assert.Equal(sessionId, (Guid)(await lockCommand.ExecuteScalarAsync())!);
        }

        var uploadTask = client.PostAsync(
            $"/api/enterprise-hardening/attachments?projectId={projectId}&artifactType=ProblemReport&artifactId={reportId}&editSessionId={sessionId}",
            ProblemReportCheckoutApiTests.SupportingFile(projectId, reportId, sessionId, "pg-race.txt",
                "text/plain", "postgres overlap\n"u8.ToArray()));
        var checkInTask = client.PostAsJsonAsync($"/api/controlled-editing/sessions/{sessionId}/check-in",
            new { expectedVersion = sessionVersion });

        // Give both request handlers time to reach the provider lock, but retain a bounded test even if a
        // runner is heavily loaded. Releasing early is safe: both tasks remain part of the assertions below.
        await Task.Delay(250);
        await holdTransaction.CommitAsync();
        await Task.WhenAll(uploadTask, checkInTask);
        using var upload = await uploadTask;
        using var checkIn = await checkInTask;

        Assert.True((int)upload.StatusCode is >= 200 and <= 499,
            $"Supporting attachment returned a server error: {await upload.Content.ReadAsStringAsync()}");
        Assert.True((int)checkIn.StatusCode is >= 200 and <= 499,
            $"Check-in returned a server error: {await checkIn.Content.ReadAsStringAsync()}");
        Assert.True(upload.StatusCode == HttpStatusCode.Created || checkIn.StatusCode == HttpStatusCode.OK,
            $"Neither operation committed: upload={upload.StatusCode}, check-in={checkIn.StatusCode}");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var active = await db.ControlledAttachments.AsNoTracking()
            .Where(x => x.ProjectId == projectId && x.ArtifactType == "ProblemReport" && x.ArtifactId == reportId
                && x.State == ControlledAttachmentState.Active)
            .ToListAsync();
        var checkedIn = await db.ProblemReportRevisions.AsNoTracking()
            .Where(x => x.ProblemReportId == reportId && x.EventType == "DetailsCheckedIn")
            .OrderByDescending(x => x.OccurredAt)
            .FirstOrDefaultAsync();
        if (checkIn.StatusCode == HttpStatusCode.OK)
        {
            Assert.NotNull(checkedIn);
            var manifest = JsonDocument.Parse(checkedIn!.SnapshotJson).RootElement.GetProperty("supportingAttachments");
            Assert.Equal(active.Count, manifest.GetArrayLength());
            Assert.All(active, item => Assert.Contains(manifest.EnumerateArray(), entry =>
                entry.GetProperty("sha256").GetString() == item.Sha256 && entry.GetProperty("attachmentId").GetGuid() == item.Id));
        }
        else
            Assert.Null(checkedIn);
    }
}
