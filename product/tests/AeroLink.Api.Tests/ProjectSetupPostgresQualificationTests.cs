using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using AeroLink.Domain.Imports;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace AeroLink.Api.Tests;

public sealed partial class ProjectSetupPostgresQualificationTests
{
    [RequiredSetupPostgresFact]
    public async Task Fresh_project_services_and_deliberate_staffing_survive_host_restart_and_migration_reapplication()
    {
        await WithDatabaseAsync(async connection =>
        {
            (Guid ProjectId, Guid ProgramId, Guid ReleaseId) created;
            string before;
            using (var firstHost = new AeroLinkApiFactory(postgresConnection: connection))
            {
                created = await ProjectSetupServiceQualificationTests.QualifyAsync(firstHost);
                before = await ProjectSetupServiceQualificationTests.RosterAsync(firstHost, created.ProgramId);
            }
            // Re-enter the supported idempotent migration path on the same owned database before a NEW
            // server host starts. The original factory and all request scopes are disposed.
            await using (var db = new AeroLinkDbContext(new DbContextOptionsBuilder<AeroLinkDbContext>().UseNpgsql(connection).Options))
                await db.Database.MigrateAsync();
            // Exercise the supported restart path with demo-account seeding enabled. The seeder must be
            // idempotent and preserve the deliberately configured project roster/leadership across host startup.
            using var restarted = new AeroLinkApiFactory(seedDemoAccounts: true, allowDemoAccounts: true,
                postgresConnection: connection);
            using var client = restarted.CreateClient();
            using var login = await client.PostAsJsonAsync("/api/auth/login", new
            {
                userName = "admin", password = AeroLinkApiFactory.AdministratorPassword,
            });
            Assert.True(login.IsSuccessStatusCode, await login.Content.ReadAsStringAsync());
            Assert.Equal(before, await ProjectSetupServiceQualificationTests.RosterAsync(restarted, created.ProgramId));
            using var workspaces = await client.GetAsync("/api/workspaces");
            Assert.True(workspaces.IsSuccessStatusCode);
            Assert.Contains(created.ProjectId.ToString(), await workspaces.Content.ReadAsStringAsync());
        });
    }

    [RequiredSetupPostgresFact]
    public async Task Release_creation_and_legacy_import_acceptance_cannot_commit_equivalent_build_identities()
    {
        await WithDatabaseAsync(async connection =>
        {
            var barrier = new ReleaseInsertBarrier();
            using var factory = new AeroLinkApiFactory(postgresConnection: connection, commandInterceptor: barrier);
            using var client = factory.CreateClient();
            await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(client);
            Guid projectId, importId;
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
                var program = new ProgramRecord("Canonical race fixture", "PG1037RACE");
                var project = new ProjectRecord(program.Id, "Canonical race fixture", "Software");
                var now = DateTimeOffset.UtcNow;
                var import = new BaselineImport(project.Id, "Fixture source", "1", "Fixture baseline", now,
                    "fixture.reqif", new string('a', 64), 1, ImportedArtifactKinds.Requirements,
                    "fixture.extractor", now, "admin", now);
                import.RecordAnalysis(now);
                import.RecordMapping("{}", now);
                import.NoteSourceRecordsAccountedFor(1, now);
                import.RecordReconciliation("{\"objectsIn\":1}", now);
                var source = new SourceIdentity(project.Id, import.Id, "Fixture source", "Requirements", "1", "SOURCE-1", now);
                db.AddRange(program, project, import, source,
                    new BaselineImportSourceIdentityMembership(import.Id, source.Id, true, now));
                await db.SaveChangesAsync();
                projectId = project.Id; importId = import.Id;
            }
            barrier.Enabled = true;
            var normal = client.PostAsJsonAsync("/api/releases", new { projectId, version = "1.3" });
            var inherited = client.PostAsJsonAsync($"/api/baseline-imports/{importId}/accept", new { version = "1.30" });
            var responses = await Task.WhenAll(normal, inherited);
            try
            {
                Assert.Equal(2, barrier.Arrivals);
                Assert.Single(responses, response => response.IsSuccessStatusCode);
                Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
                using var scope = factory.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
                var release = Assert.Single(await db.Releases.AsNoTracking().Where(x => x.ProjectId == projectId).ToListAsync());
                Assert.Equal("SW-01.30", release.CanonicalIdentity);
                var import = await db.BaselineImports.AsNoTracking().SingleAsync(x => x.Id == importId);
                if (responses[1].IsSuccessStatusCode)
                {
                    Assert.Equal(BaselineImportState.Accepted, import.State);
                    Assert.Equal(release.Id, import.ReleaseId);
                    Assert.True(release.IsReleased); // Legacy existing-project acceptance retains DEC-093.
                }
                else
                {
                    Assert.Equal(BaselineImportState.Reconciled, import.State);
                    Assert.Null(import.ReleaseId);
                    Assert.False(release.IsReleased);
                }
            }
            finally { foreach (var response in responses) response.Dispose(); }
        });
    }

    private static async Task WithDatabaseAsync(Func<string, Task> test)
    {
        var raw = Environment.GetEnvironmentVariable("AEROLINK_MIGRATIONS_CONNECTION");
        if (string.IsNullOrWhiteSpace(raw)) throw new InvalidOperationException("Required setup API PostgreSQL qualification needs an explicit disposable connection.");
        var server = new NpgsqlConnectionStringBuilder(raw);
        var host = (server.Host ?? "").Trim().Trim('[', ']');
        if (!(host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address)) || server.Port == 54329)
            throw new InvalidOperationException("Setup API qualification requires loopback PostgreSQL away from persistent port 54329.");
        var database = "aerolink_1037_api_" + Guid.NewGuid().ToString("N");
        server.Database = "postgres";
        await using var admin = new NpgsqlConnection(server.ConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", admin)) await create.ExecuteNonQueryAsync();
        try
        {
            var connection = new NpgsqlConnectionStringBuilder(server.ConnectionString) { Database = database }.ConnectionString;
            await using (var db = new AeroLinkDbContext(new DbContextOptionsBuilder<AeroLinkDbContext>().UseNpgsql(connection).Options))
                await db.Database.MigrateAsync();
            await test(connection);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private sealed class ReleaseInsertBarrier : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _both = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;
        public bool Enabled { get; set; }
        public int Arrivals => _arrivals;
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Enabled && command.CommandText.Contains("INSERT INTO software_releases", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref _arrivals) == 2) _both.TrySetResult();
                await _both.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            }
            return result;
        }
    }

    private sealed class RequiredSetupPostgresFactAttribute : FactAttribute
    {
        public RequiredSetupPostgresFactAttribute()
        {
            var required = Environment.GetEnvironmentVariable("AEROLINK_REQUIRE_POSTGRES_QUALIFICATION");
            if ((string.IsNullOrWhiteSpace(required) || required.Equals("false", StringComparison.OrdinalIgnoreCase))
                && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AEROLINK_MIGRATIONS_CONNECTION")))
                Skip = "Set AEROLINK_MIGRATIONS_CONNECTION for owned disposable PostgreSQL qualification.";
        }
    }
}
