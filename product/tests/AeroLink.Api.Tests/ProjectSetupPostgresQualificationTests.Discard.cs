using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Infrastructure.Persistence;
using AeroLink.Infrastructure.Tests;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

public sealed partial class ProjectSetupPostgresQualificationTests
{
    /// <summary>
    /// Discard against a finalization that is already claimed but not yet committed. The claim holds the row
    /// lock and the version token, so the discard must lose: a setup being finalized is never discarded, and a
    /// Project that the finalization creates is never hidden behind a discarded setup. This is provider-level
    /// evidence — SQLite serializes the same requests and cannot show the interleaving.
    /// </summary>
    [DisposablePostgresFact]
    public async Task Discard_cannot_win_against_a_finalization_that_already_claimed_the_setup()
    {
        await WithDatabaseAsync(async connection =>
        {
            var barrier = new FinalizationClaimBarrier();
            using var factory = new AeroLinkApiFactory(postgresConnection: connection, commandInterceptor: barrier);
            using var client = factory.CreateClient();
            await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(client);

            using var created = await client.PostAsJsonAsync("/api/project-setups",
                new { projectName = "Race discard target" });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            using var createdBody = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            var draftId = createdBody.RootElement.GetProperty("draftId").GetGuid();
            using (var saved = await client.PutAsJsonAsync($"/api/project-setups/{draftId}", new
            {
                expectedVersion = 1,
                currentStep = "Review",
                project = new { name = "Race discard target", softwareProduct = "Race discard product" },
                start = new { kind = "Fresh" },
                build = new { version = "1.3" },
                selectedCategories = Array.Empty<string>(),
                ladder = new { }, reviewRules = new { }, reviewRulesAccepted = true,
                repository = new { mode = "ConfigureLater" }, mapping = new { },
            })) Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

            // Hold the finalization after its claim is written inside the still-open transaction.
            barrier.Enabled = true;
            var finalize = client.PostAsJsonAsync($"/api/project-setups/{draftId}/finalize",
                new { expectedVersion = 2, idempotencyKey = "discard-race-finalize" });
            await barrier.ClaimWritten.WaitAsync(TimeSpan.FromSeconds(30));

            // The discard request is issued while the claim is uncommitted. It reached the same row's write
            // before the finalization transaction ended, which is the interleaving under test.
            var discard = client.PostAsJsonAsync($"/api/project-setups/{draftId}/discard",
                new { expectedVersion = 2 });
            await barrier.DiscardAttempted.WaitAsync(TimeSpan.FromSeconds(30));
            barrier.Release();

            using var finalized = await finalize;
            using var discarded = await discard;
            Assert.True(finalized.IsSuccessStatusCode,
                $"finalization should have committed: {finalized.StatusCode} {await finalized.Content.ReadAsStringAsync()}");
            Assert.Equal(HttpStatusCode.Conflict, discarded.StatusCode);
            Assert.Contains("draft_conflict", await discarded.Content.ReadAsStringAsync(), StringComparison.Ordinal);

            using var finalizedBody = JsonDocument.Parse(await finalized.Content.ReadAsStringAsync());
            var projectId = finalizedBody.RootElement.GetProperty("projectId").GetGuid();
            var releaseId = finalizedBody.RootElement.GetProperty("releaseId").GetGuid();
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            Assert.Equal("Completed", await db.ProjectSetupDrafts.Where(x => x.Id == draftId)
                .Select(x => x.State.ToString()).SingleAsync());
            Assert.True(await db.Projects.AnyAsync(x => x.Id == projectId));
            Assert.True(await db.Releases.AnyAsync(x => x.Id == releaseId));
            Assert.False(await db.SecurityAuditEvents.AnyAsync(x =>
                x.EventType == "ProjectSetupDiscarded" && x.Target == draftId.ToString("D")));
        });
    }

    /// <summary>
    /// Pauses the finalization transaction after its draft claim is written, and reports when the competing
    /// discard has reached its own write against the same row. No fixed delays are used.
    /// </summary>
    private sealed class FinalizationClaimBarrier : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _claimWritten = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _discardAttempted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private DbCommand? _claimCommand;
        private int _arrivals;

        /// <summary>Off while the fixture saves its own answers; only the interleaving under test is held.</summary>
        public bool Enabled { get; set; }

        public Task ClaimWritten => _claimWritten.Task;
        public Task DiscardAttempted => _discardAttempted.Task;

        public void Release() => _release.TrySetResult();

        private static bool IsDraftUpdate(DbCommand command) =>
            command.CommandText.StartsWith("UPDATE project_setup_drafts", StringComparison.Ordinal);

        private void Begin(DbCommand command)
        {
            if (!Enabled) return;
            if (!IsDraftUpdate(command)) return;
            if (Interlocked.Increment(ref _arrivals) == 1) _claimCommand = command;
            else _discardAttempted.TrySetResult();
        }

        private async Task EndAsync(DbCommand command, CancellationToken cancellationToken)
        {
            if (!ReferenceEquals(command, _claimCommand)) return;
            _claimWritten.TrySetResult();
            await _release.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Begin(command);
            return result;
        }

        public override async ValueTask<int> NonQueryExecutedAsync(DbCommand command,
            CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            await EndAsync(command, cancellationToken);
            return result;
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Begin(command);
            return result;
        }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command,
            CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            await EndAsync(command, cancellationToken);
            return result;
        }
    }
}
