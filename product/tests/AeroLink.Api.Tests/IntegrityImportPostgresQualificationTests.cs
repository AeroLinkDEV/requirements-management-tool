using System.Data;
using System.Net;
using System.Net.Http.Json;
using AeroLink.Infrastructure.Persistence;
using AeroLink.Infrastructure.Tests;
using AeroLink.Tests;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace AeroLink.Api.Tests;

// Authoring gate: SQLite cannot prove the migration, timestamptz precision or PostgreSQL row-lock arbitration.
[Trait("Category", "PostgresQualification")]
public sealed class IntegrityImportPostgresQualificationTests
{
    [DisposablePostgresFact]
    public async Task Migrated_postgres_publishes_one_reviewed_batch_under_overlapping_imports()
    {
        await using var database = await DisposablePostgresDatabase.CreateAsync("aerolink_integrity_1186");
        using var factory = new AeroLinkApiFactory(postgresConnection: database.ConnectionString);
        using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["IntegrityImport:AllowFixtures"] = "true" })));
        using var client = configured.CreateClient(); await IntegrityImportApiTests.Bootstrap(client);
        var project = await IntegrityImportApiTests.SeedProject(configured.Services);
        var (bytes, hash) = await IntegrityFixturePackage.ReadAsync();
        var mapping = IntegrityImportApiTests.Mapping("admin"); var first = Guid.NewGuid(); var second = Guid.NewGuid();
        const string root = "/api/problem-reports/integrity-import";
        using var previewResponse = await client.PostAsync(root + "/preview", IntegrityImportApiTests.BuildForm(project, bytes, hash, mapping, first));
        var preview = (await previewResponse.Content.ReadFromJsonAsync<IntegrityImportPreview>(IntegritySourcePackage.Json))!;
        await using var holder = new NpgsqlConnection(database.ConnectionString); await holder.OpenAsync();
        await using var transaction = await holder.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        await using (var command = new NpgsqlCommand("SELECT \"Id\" FROM projects WHERE \"Id\" = @id FOR UPDATE", holder, transaction))
        { command.Parameters.AddWithValue("id", project); Assert.Equal(project, await command.ExecuteScalarAsync()); }
        var a = client.PostAsync(root + "/commit", IntegrityImportApiTests.BuildForm(project, bytes, hash, mapping, first, preview.PreviewHash, AeroLinkApiFactory.AdministratorPassword));
        var b = client.PostAsync(root + "/commit", IntegrityImportApiTests.BuildForm(project, bytes, hash, mapping, second, preview.PreviewHash, AeroLinkApiFactory.AdministratorPassword));
        await using var observer = new NpgsqlConnection(database.ConnectionString); await observer.OpenAsync();
        var waiters = 0L;
        for (var attempt = 0; attempt < 100 && waiters < 2; attempt++)
        {
            await using var command = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE datname = @db AND wait_event_type = 'Lock'", observer);
            command.Parameters.AddWithValue("db", database.Name); waiters = (long)(await command.ExecuteScalarAsync())!;
            if (waiters < 2) await Task.Delay(100);
        }
        await transaction.CommitAsync();
        using var firstResult = await a; using var secondResult = await b;
        Assert.True(waiters >= 2, "Both HTTP operations must actually overlap at the provider lock.");
        Assert.Equal(new[] { HttpStatusCode.OK, HttpStatusCode.Conflict },
            new[] { firstResult.StatusCode, secondResult.StatusCode }.OrderBy(x => (int)x));
        using var scope = configured.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal(2, await db.ProblemReports.CountAsync()); Assert.Single(await db.IntegrityImportBatches.ToListAsync());
        Assert.Single(await db.ElectronicSignatures.Where(x => x.Action == "ImportIntegrityProblemReports").ToListAsync());
        var source = await db.ProblemReports.SingleAsync(x => x.SourceKey == "1001");
        Assert.Equal(DateTimeOffset.Parse("2024-03-01T10:34:56.123456Z"), source.SourceCreatedAt);
        Assert.Equal(0, source.SourceCreatedAt!.Value.Ticks % 10);
    }
}
