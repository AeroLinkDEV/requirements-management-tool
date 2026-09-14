using System.Data.Common;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace AeroLink.Api.Tests;

public sealed partial class ProjectSetupPostgresQualificationTests
{
    [RequiredSetupPostgresFact]
    public async Task Repository_edit_waits_for_verified_evidence_commit_and_cannot_rewrite_its_identity()
    {
        await WithDatabaseAsync(async connection =>
        {
            var barrier = new RepositoryEvidenceBarrier();
            using var factory = new AeroLinkApiFactory(postgresConnection: connection, commandInterceptor: barrier);
            await ProjectSetupCodeEvidenceApiTests.QualifyAsync(factory, async (client, projectId, version, postMerge) =>
            {
                barrier.Enabled = true;
                var mapping = postMerge();
                await barrier.MappingReady.Task.WaitAsync(TimeSpan.FromSeconds(20));
                var edit = client.PutAsJsonAsync($"/api/projects/{projectId}/repository", new
                { expectedVersion = version, mode = "ConfigureLater", provider = (string?)null, endpoint = (string?)null });
                try
                {
                    await barrier.EditStarted.Task.WaitAsync(TimeSpan.FromSeconds(20));
                    await using var observer = new NpgsqlConnection(connection);
                    await observer.OpenAsync();
                    var elapsed = Stopwatch.StartNew();
                    var blocked = false;
                    while (elapsed.Elapsed < TimeSpan.FromSeconds(10))
                    {
                        Assert.False(edit.IsCompleted, "Repository edit committed while the earlier verified mapping was paused before INSERT.");
                        await using var query = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE datname=current_database() AND wait_event_type='Lock' AND query LIKE 'UPDATE project_repository_configurations%')", observer);
                        if (await query.ExecuteScalarAsync() is true) { blocked = true; break; }
                        await Task.Delay(25);
                    }
                    Assert.True(blocked, "PostgreSQL did not expose the competing configuration UPDATE waiting for the evidence transaction's row lock.");
                }
                finally { barrier.AllowMapping.TrySetResult(); }
                using var recorded = await mapping;
                Assert.True(recorded.IsSuccessStatusCode, await recorded.Content.ReadAsStringAsync());
                using var edited = await edit;
                Assert.True(edited.IsSuccessStatusCode, await edited.Content.ReadAsStringAsync());
                using var subsequent = await postMerge();
                Assert.Equal(HttpStatusCode.Conflict, subsequent.StatusCode);
                Assert.Contains("repository_pending", await subsequent.Content.ReadAsStringAsync());
                // The shared qualification then reloads the mapping and asserts its original observed
                // remote ID, endpoint/path, actor, version and time despite the now-Pending configuration.
            });
        });
    }

    private sealed class RepositoryEvidenceBarrier : DbCommandInterceptor
    {
        public bool Enabled { get; set; }
        public TaskCompletionSource MappingReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource EditStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowMapping { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (!Enabled) return result;
            if (command.CommandText.Contains("INSERT INTO code_traceability_records", StringComparison.Ordinal))
            {
                MappingReady.TrySetResult();
                await AllowMapping.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            }
            if (command.CommandText.StartsWith("UPDATE project_repository_configurations", StringComparison.Ordinal))
                EditStarted.TrySetResult();
            return result;
        }
    }
}
