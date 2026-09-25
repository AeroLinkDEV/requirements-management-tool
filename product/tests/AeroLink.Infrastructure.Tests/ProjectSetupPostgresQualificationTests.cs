using System.Net;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AeroLink.Infrastructure.Tests;

/// <summary>
/// Provider qualification for the durable setup aggregate. SQLite API fixtures cover route behavior, but only
/// PostgreSQL proves the forward migration SQL, database-backed optimistic token, and a restart/reload against
/// the provider used by the product. Each run owns a fresh database on the caller-supplied loopback server.
/// </summary>
public sealed partial class ProjectSetupPostgresQualificationTests
{
    [DisposablePostgresFact]
    public async Task Migration_draft_reload_and_concurrent_edit_are_durable_on_postgresql()
    {
        var rawConnection = Environment.GetEnvironmentVariable("AEROLINK_MIGRATIONS_CONNECTION");
        if (string.IsNullOrWhiteSpace(rawConnection))
            throw new InvalidOperationException("Required project-setup PostgreSQL qualification needs an explicit disposable connection.");
        var server = ValidateServer(rawConnection);
        var databaseName = $"aerolink_1037_setup_{Guid.NewGuid():N}";
        await CreateDatabaseAsync(server, databaseName);
        try
        {
            var connection = new NpgsqlConnectionStringBuilder(server) { Database = databaseName }.ConnectionString;
            var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseNpgsql(connection).Options;

            await using (var migrate = new AeroLinkDbContext(options))
            {
                await migrate.Database.MigrateAsync();
                // A second migration pass is part of the provider contract: restart/upgrade must be safe.
                await migrate.Database.MigrateAsync();
            }

            var now = DateTimeOffset.UtcNow;
            var account = new UserAccount("pg-setup-owner", "PG Setup Owner", "pg-setup-owner@example.test",
                "test-password-hash", now);
            var draft = new ProjectSetupDraft(account.Id, account.UserName, "PG durable setup");
            draft.UpdateAnswers(1, ProjectSetupStep.Review, "PG durable setup", "PG product",
                ProjectSetupStartKind.Fresh, null, null, "1.3", "[]", "{}", "{}", true,
                "{\"mode\":\"ConfigureLater\"}", "{}", now);
            await using (var seed = new AeroLinkDbContext(options))
            {
                seed.AddRange(account, draft);
                await seed.SaveChangesAsync();
            }

            // A fresh context represents an application restart. The reserved identities and accepted answers
            // must be available without re-running the wizard or relying on in-memory state.
            await using (var restarted = new AeroLinkDbContext(options))
            {
                var reloaded = await restarted.ProjectSetupDrafts.AsNoTracking().SingleAsync(x => x.Id == draft.Id);
                Assert.Equal(draft.ProjectId, reloaded.ProjectId);
                Assert.Equal(draft.InternalProgramId, reloaded.InternalProgramId);
                Assert.Equal(draft.InitialReleaseId, reloaded.InitialReleaseId);
                Assert.Equal(ProjectSetupState.Draft, reloaded.State);
                Assert.Equal(2, reloaded.Version);
                Assert.Equal("1.3", reloaded.InitialReleaseVersion);
                Assert.Equal("SW-01.30", reloaded.InitialReleaseCanonicalIdentity);
                Assert.True(reloaded.ReviewRulesAccepted);
                Assert.NotNull(reloaded.ReviewRulesAcceptanceHash);
            }

            await using var first = new AeroLinkDbContext(options);
            await using var second = new AeroLinkDbContext(options);
            var firstDraft = await first.ProjectSetupDrafts.SingleAsync(x => x.Id == draft.Id);
            var secondDraft = await second.ProjectSetupDrafts.SingleAsync(x => x.Id == draft.Id);
            firstDraft.UpdateAnswers(firstDraft.Version, ProjectSetupStep.Services, null, null, null, null, null,
                null, null, null, null, null, "{\"mode\":\"ConfigureLater\"}", null,
                DateTimeOffset.UtcNow);
            secondDraft.UpdateAnswers(secondDraft.Version, ProjectSetupStep.Ladder, null, null, null, null, null,
                null, null, "{}", null, null, null, null, DateTimeOffset.UtcNow);
            await first.SaveChangesAsync();

            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
            await using var checkedAfterRace = new AeroLinkDbContext(options);
            var winner = await checkedAfterRace.ProjectSetupDrafts.AsNoTracking().SingleAsync(x => x.Id == draft.Id);
            Assert.Equal(ProjectSetupStep.Services, winner.CurrentStep);
            Assert.Equal(3, winner.Version);
            Assert.Equal(draft.ProjectId, winner.ProjectId);
            Assert.Empty(await checkedAfterRace.Projects.AsNoTracking().ToListAsync());
            Assert.Empty(await checkedAfterRace.Programs.AsNoTracking().ToListAsync());
        }
        finally
        {
            await DropDatabaseAsync(server, databaseName);
        }
    }

    private static string ValidateServer(string raw)
    {
        var builder = new NpgsqlConnectionStringBuilder(raw);
        var host = (builder.Host ?? string.Empty).Trim().Trim('[', ']');
        var loopback = string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
        if (!loopback) throw new InvalidOperationException("Issue #1037 PostgreSQL qualification requires a loopback host.");
        if (builder.Port == 54329) throw new InvalidOperationException("Issue #1037 qualification refuses persistent port 54329.");
        return raw;
    }

    private static async Task CreateDatabaseAsync(string server, string database)
    {
        await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(server)
        { Database = "postgres" }.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE \"{database}\"";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DropDatabaseAsync(string server, string database)
    {
        await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(server)
        { Database = "postgres" }.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)";
        await command.ExecuteNonQueryAsync();
    }
}
