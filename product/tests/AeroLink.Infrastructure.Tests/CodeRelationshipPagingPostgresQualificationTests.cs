using System.Net;
using AeroLink.Domain.Integrations;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AeroLink.Infrastructure.Tests;

public sealed class CodeRelationshipPagingPostgresQualificationTests
{
    [DisposablePostgresFact]
    public async Task Mixed_relationship_pages_use_one_provider_order_and_do_not_drop_later_rows()
    {
        var raw = Environment.GetEnvironmentVariable("AEROLINK_MIGRATIONS_CONNECTION")
            ?? throw new InvalidOperationException("Code qualification requires an explicit disposable PostgreSQL connection.");
        var server = new NpgsqlConnectionStringBuilder(raw);
        var host = (server.Host ?? "").Trim('[', ']');
        if (!(host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
              || IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address)) || server.Port == 54329)
            throw new InvalidOperationException("Code qualification requires loopback PostgreSQL away from persistent port 54329.");

        var database = $"aerolink_1023_code_page_{Guid.NewGuid():N}";
        server.Database = "postgres";
        await using var administrator = new NpgsqlConnection(server.ConnectionString);
        await administrator.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", administrator))
            await create.ExecuteNonQueryAsync();
        try
        {
            server.Database = database;
            var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseNpgsql(server.ConnectionString).Options;
            await using (var migrate = new AeroLinkDbContext(options))
                await migrate.Database.MigrateAsync();

            var now = DateTimeOffset.UtcNow;
            var program = new ProgramRecord("Code relationship paging qualification", "CRP");
            var project = new ProjectRecord(program.Id, "Code relationship paging qualification", "Synthetic code");
            var release = new SoftwareRelease(project.Id, "1.0", false);
            var repository = new ProjectRepositoryConfiguration(project.Id, ProjectRepositorySetupMode.ConnectNow,
                "GitLab", "https://gitlab.example/demo/code", "tester", now);
            repository.RecordVerification("tester", now, 17, "demo/code");
            var snapshot = new GitLabSourceSnapshot(project.Id, repository.Id, "https://gitlab.example", 17,
                "demo/code", new string('a', 40), "main", "tester", now, repository.Version);
            var target = CodeRelationshipTarget.ForChangeRequestRevision(Guid.NewGuid(), 1, "SCR-1.01");
            var file = new GitLabFileRelationship(project.Id, release.Id, snapshot.InstanceBaseUrl,
                snapshot.RemoteProjectId, snapshot.Id, null, snapshot.CommitSha, "src/demo.c", null, null, null,
                target, CodeRelationshipMeaning.Implements, "tester", now);
            var merge = new GitLabMergeRequestRelationship(project.Id, release.Id, snapshot.InstanceBaseUrl,
                snapshot.RemoteProjectId, 7, 700, null, null, snapshot.PathWithNamespace,
                "https://gitlab.example/demo/code/-/merge_requests/7", "Later merge request", target,
                CodeRelationshipMeaning.RelatedContext, "tester", now.AddMinutes(1));
            await using (var seed = new AeroLinkDbContext(options))
            {
                seed.AddRange(program, project, release, repository, snapshot, file, merge);
                await seed.SaveChangesAsync();
            }

            await using var read = new AeroLinkDbContext(options);
            var first = await new CodeRelationshipService(read).ReadPageAsync(project.Id, release.Id,
                null, null, null, 1, 1, false, default);
            var second = await new CodeRelationshipService(read).ReadPageAsync(project.Id, release.Id,
                null, null, null, 2, 1, false, default);
            Assert.Equal(2, first.Total);
            Assert.Equal(CodeRelationshipKind.MergeRequest, Assert.Single(first.Items).RelationshipKind);
            Assert.Equal(CodeRelationshipKind.File, Assert.Single(second.Items).RelationshipKind);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{database}\" WITH (FORCE)", administrator);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private sealed class DisposablePostgresFactAttribute : FactAttribute
    {
        public DisposablePostgresFactAttribute()
        {
            var required = Environment.GetEnvironmentVariable("AEROLINK_REQUIRE_POSTGRES_QUALIFICATION");
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AEROLINK_MIGRATIONS_CONNECTION"))
                && (string.IsNullOrWhiteSpace(required) || required.Equals("false", StringComparison.OrdinalIgnoreCase)))
                Skip = "Set AEROLINK_MIGRATIONS_CONNECTION to an owned loopback PostgreSQL server away from port 54329.";
        }
    }
}
