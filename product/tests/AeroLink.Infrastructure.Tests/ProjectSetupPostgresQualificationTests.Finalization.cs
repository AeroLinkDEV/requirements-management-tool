using System.Data.Common;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Infrastructure.Tests;

public sealed partial class ProjectSetupPostgresQualificationTests
{
    [DisposablePostgresFact]
    public async Task Competing_finalizers_and_lost_committed_result_recover_one_project_on_postgresql()
    {
        await WithSetupDatabaseAsync(async connection =>
        {
            using var seedProvider = SetupProvider(connection);
            var (draft, actor) = await ReadyDraftAsync(seedProvider);
            var barrier = new DraftReadBarrier();
            using var racingProvider = SetupProvider(connection, barrier);
            async Task<ProjectSetupFinalizationResult?> CompleteAsync()
            {
                await using var scope = racingProvider.CreateAsyncScope();
                try
                {
                    return await scope.ServiceProvider.GetRequiredService<ProjectSetupService>()
                        .FinalizeAsync(draft.Id, actor, draft.Version, "same-client-operation", CancellationToken.None);
                }
                catch (ProjectSetupConflictException) { return null; }
            }
            var outcomes = await Task.WhenAll(CompleteAsync(), CompleteAsync());
            Assert.Equal(2, barrier.Reads);
            var completed = Assert.Single(outcomes, x => x is not null)!;

            // Discard the original request contexts and recover solely from committed server state. This is
            // service/provider recovery evidence, not a claim that an HTTP connection was fault-injected.
            await using var resumedScope = seedProvider.CreateAsyncScope();
            var recovered = await resumedScope.ServiceProvider.GetRequiredService<ProjectSetupService>()
                .FinalizeAsync(draft.Id, actor, draft.Version, "same-client-operation", CancellationToken.None);
            Assert.True(recovered.AlreadyCompleted);
            Assert.Equal(completed.ProjectId, recovered.ProjectId);
            Assert.Equal(completed.ReleaseId, recovered.ReleaseId);
            Assert.Equal(draft.ProjectId, recovered.ProjectId);
            Assert.Equal(draft.InitialReleaseId, recovered.ReleaseId);
            var db = resumedScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            Assert.Single(await db.Programs.ToListAsync());
            Assert.Single(await db.Projects.ToListAsync());
            Assert.Single(await db.Releases.ToListAsync());
            Assert.Empty(await db.CandidateBaselines.ToListAsync());
            Assert.Empty(await db.Set<RequirementArtifact>().ToListAsync());
            Assert.Equal(ProjectSetupState.Completed, (await db.ProjectSetupDrafts.SingleAsync()).State);
        });
    }

    [DisposablePostgresFact]
    public async Task Interrupted_finalization_rolls_back_claim_and_structure_before_retry_on_postgresql()
    {
        await WithSetupDatabaseAsync(async connection =>
        {
            using var provider = SetupProvider(connection);
            var (draft, actor) = await ReadyDraftAsync(provider);
            using (var faulting = SetupProvider(connection, new FailProjectSave()))
            {
                await using var scope = faulting.CreateAsyncScope();
                await Assert.ThrowsAsync<IOException>(() => scope.ServiceProvider.GetRequiredService<ProjectSetupService>()
                    .FinalizeAsync(draft.Id, actor, draft.Version, "interrupted-operation", CancellationToken.None));
            }
            await using (var checkedScope = provider.CreateAsyncScope())
            {
                var db = checkedScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
                var retained = await db.ProjectSetupDrafts.SingleAsync();
                Assert.Equal(ProjectSetupState.Draft, retained.State);
                Assert.Equal(draft.Version, retained.Version);
                Assert.Empty(await db.Projects.ToListAsync());
                Assert.Empty(await db.Programs.ToListAsync());
                Assert.Empty(await db.Releases.ToListAsync());
                Assert.Empty(await db.ProjectLadderConfigurations.ToListAsync());
            }
            await using var retry = provider.CreateAsyncScope();
            var completed = await retry.ServiceProvider.GetRequiredService<ProjectSetupService>()
                .FinalizeAsync(draft.Id, actor, draft.Version, "interrupted-operation", CancellationToken.None);
            Assert.False(completed.AlreadyCompleted);
            Assert.Equal(draft.ProjectId, completed.ProjectId);
            Assert.Equal(draft.InitialReleaseId, completed.ReleaseId);
        });
    }

    private static ServiceProvider SetupProvider(string connection, IInterceptor? interceptor = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Provider"] = "PostgreSql", ["ConnectionStrings:AeroLink"] = connection,
            ["DemoData:Enabled"] = "false", ["Identity:SeedDemoAccounts"] = "false",
        }).Build();
        var services = new ServiceCollection().AddLogging().AddAeroLinkInfrastructure(configuration);
        services.AddSingleton<ILadderPolicy, LegacyLadderPolicy>();
        if (interceptor is not null) services.AddDbContext<AeroLinkDbContext>(options => options.AddInterceptors(interceptor));
        return services.BuildServiceProvider();
    }

    private static async Task<(ProjectSetupDraft Draft, AuthenticatedUser Actor)> ReadyDraftAsync(ServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var account = new UserAccount("pg-finalizer", "PG Finalizer", "pg-finalizer@example.test", "fixture-hash", DateTimeOffset.UtcNow);
        db.Add(account);
        await db.SaveChangesAsync();
        var actor = new AuthenticatedUser(account.Id, account.UserName, account.DisplayName, account.Email, true, []);
        var service = scope.ServiceProvider.GetRequiredService<ProjectSetupService>();
        var draft = await service.CreateAsync(actor, "Durable Finalization", CancellationToken.None);
        var rules = ProjectSetupReviewRules.SuggestedJson("{}", draft.ProjectId);
        draft = await service.UpdateAsync(draft.Id, actor, new ProjectSetupUpdateCommand(draft.Version,
            ProjectSetupStep.Review, "Durable Finalization", "Durable Software", ProjectSetupStartKind.Fresh,
            null, null, "1.3", "[]", "{}", rules, true, "{\"mode\":\"ConfigureLater\"}", "{}"), CancellationToken.None);
        return (draft, actor);
    }

    private static async Task WithSetupDatabaseAsync(Func<string, Task> test)
    {
        await using var qualification = await DisposablePostgresDatabase.CreateAsync("aerolink_1037_finalize");
        var connection = qualification.ConnectionString;
        await using (var migrate = new AeroLinkDbContext(new DbContextOptionsBuilder<AeroLinkDbContext>().UseNpgsql(connection).Options))
            await migrate.Database.MigrateAsync();
        await test(connection);
    }

    private sealed class DraftReadBarrier : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _bothRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _reads;
        public int Reads => _reads;
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("SELECT", StringComparison.Ordinal)
                && command.CommandText.Contains("FROM project_setup_drafts", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref _reads) == 2) _bothRead.TrySetResult();
                await _bothRead.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            }
            return result;
        }
    }

    private sealed class FailProjectSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<ProjectRecord>().Any(x => x.State == EntityState.Added))
                throw new IOException("Injected interruption after the draft claim, before project structure commit.");
            return ValueTask.FromResult(result);
        }
    }
}
