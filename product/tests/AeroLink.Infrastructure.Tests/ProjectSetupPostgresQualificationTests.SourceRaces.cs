using System.Text;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Infrastructure.Tests;

public sealed partial class ProjectSetupPostgresQualificationTests
{
    [DisposablePostgresFact]
    public async Task Concurrent_native_capture_and_external_upload_leave_one_staged_package_on_postgresql()
    {
        await WithSetupDatabaseAsync(async connection =>
        {
            using var provider = SetupProvider(connection);
            var (accountId, sourceBaselineId) = await SeedNativeSourceAsync(provider);
            var actor = new AuthenticatedUser(accountId, "pg-source-race-owner", "PG Source Race Owner",
                "pg-source-race-owner@example.test", true, []);
            Guid draftId;
            long expectedVersion;
            await using (var setupScope = provider.CreateAsyncScope())
            {
                var service = setupScope.ServiceProvider.GetRequiredService<ProjectSetupService>();
                var draft = await service.CreateAsync(actor, "Source race destination", CancellationToken.None);
                draft = await service.UpdateAsync(draft.Id, actor, new ProjectSetupUpdateCommand(draft.Version,
                    ProjectSetupStep.StartingPoint, "Source race destination", "Source race product",
                    ProjectSetupStartKind.Fresh, null, null, "1.3", "[]", "{}",
                    ProjectSetupReviewRules.SuggestedJson("{}", draft.ProjectId), true,
                    "{\"mode\":\"ConfigureLater\"}", "{}"), CancellationToken.None);
                draftId = draft.Id;
                expectedVersion = draft.Version;
            }

            var barrier = new DraftReadBarrier();
            using var racingProvider = SetupProvider(connection, barrier);
            var bytes = Encoding.UTF8.GetBytes("Identifier,Level,Statement\r\nRACE-1,System,external race\r\n");
            async Task<bool> CaptureAsync()
            {
                await using var scope = racingProvider.CreateAsyncScope();
                try
                {
                    await scope.ServiceProvider.GetRequiredService<ProjectSetupInceptionService>()
                        .CaptureNativeAsync(draftId, actor, expectedVersion, sourceBaselineId, CancellationToken.None);
                    return true;
                }
                catch (ProjectSetupConcurrencyException) { return false; }
            }
            async Task<bool> UploadAsync()
            {
                await using var scope = racingProvider.CreateAsyncScope();
                try
                {
                    await scope.ServiceProvider.GetRequiredService<ProjectSetupInceptionService>()
                        .UploadAsync(draftId, actor, expectedVersion, "race.csv",
                            new MemoryStream(bytes, writable: false), CancellationToken.None);
                    return true;
                }
                catch (ProjectSetupConcurrencyException) { return false; }
            }

            var outcomes = await Task.WhenAll(CaptureAsync(), UploadAsync());
            Assert.Equal(2, barrier.Reads);
            Assert.Single(outcomes, succeeded => succeeded);

            await using var verifyScope = provider.CreateAsyncScope();
            var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var packages = await verifyDb.ProjectSetupSourcePackages.AsNoTracking()
                .Where(x => x.DraftId == draftId).ToListAsync();
            Assert.Single(packages);
            var draftAfterRace = await verifyDb.ProjectSetupDrafts.AsNoTracking().SingleAsync(x => x.Id == draftId);
            Assert.Equal(expectedVersion + 1, draftAfterRace.Version);
            Assert.True(draftAfterRace.SourceBaselineId == sourceBaselineId || draftAfterRace.SourceImportId == packages[0].Id);
            Assert.DoesNotContain(await verifyDb.Projects.AsNoTracking().ToListAsync(), x => x.Id == draftAfterRace.ProjectId);
        });
    }

    [DisposablePostgresFact]
    public async Task Concurrent_source_mapping_and_reconcile_return_one_conflict_and_preserve_reconciled_package_on_postgresql()
    {
        await WithSetupDatabaseAsync(async connection =>
        {
            using var provider = SetupProvider(connection);
            Guid accountId;
            using (var seedScope = provider.CreateScope())
            {
                var db = seedScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
                var account = new UserAccount("pg-mapping-race-owner", "PG Mapping Race Owner",
                    "pg-mapping-race-owner@example.test", "fixture-hash", DateTimeOffset.UtcNow);
                db.Add(account);
                await db.SaveChangesAsync();
                accountId = account.Id;
            }
            var actor = new AuthenticatedUser(accountId, "pg-mapping-race-owner", "PG Mapping Race Owner",
                "pg-mapping-race-owner@example.test", true, []);
            Guid draftId;
            ProjectSetupSourceView source;
            long configuredVersion;
            await using (var setupScope = provider.CreateAsyncScope())
            {
                var service = setupScope.ServiceProvider.GetRequiredService<ProjectSetupService>();
                var inception = setupScope.ServiceProvider.GetRequiredService<ProjectSetupInceptionService>();
                var draft = await service.CreateAsync(actor, "Mapping race destination", CancellationToken.None);
                draft = await PrepareExternalDraftAsync(service, draft, actor, "mapping-race.csv");
                var uploaded = await inception.UploadAsync(draft.Id, actor, draft.Version, "mapping-race.csv",
                    new MemoryStream(ExternalSourceBytes("mapping-race.csv"), writable: false), CancellationToken.None);
                source = (await inception.ReadSourceAsync(draft.Id, actor, CancellationToken.None))!;
                var configured = await inception.SaveConfigurationAsync(draft.Id, actor,
                    new InceptionConfigurationCommand(uploaded.DraftVersion, "[\"Requirements\"]",
                        MappingJson(source), "{}"), CancellationToken.None);
                Assert.Equal(ProjectSetupSourceStage.Reconciled, configured.Package.Stage);
                draftId = draft.Id;
                configuredVersion = configured.DraftVersion;
            }

            var barrier = new DraftReadBarrier();
            using var racingProvider = SetupProvider(connection, barrier);
            async Task<bool> SaveMappingAsync()
            {
                await using var scope = racingProvider.CreateAsyncScope();
                try
                {
                    await scope.ServiceProvider.GetRequiredService<ProjectSetupInceptionService>()
                        .SaveConfigurationAsync(draftId, actor,
                            new InceptionConfigurationCommand(configuredVersion, "[\"Requirements\"]",
                                MappingJson(source), "{}"), CancellationToken.None);
                    return true;
                }
                catch (ProjectSetupConcurrencyException) { return false; }
            }
            async Task<bool> ReconcileAsync()
            {
                await using var scope = racingProvider.CreateAsyncScope();
                try
                {
                    await scope.ServiceProvider.GetRequiredService<ProjectSetupInceptionService>()
                        .ReconcileAsync(draftId, actor, configuredVersion, CancellationToken.None);
                    return true;
                }
                catch (ProjectSetupConcurrencyException) { return false; }
            }

            var outcomes = await Task.WhenAll(SaveMappingAsync(), ReconcileAsync());
            Assert.Equal(2, barrier.Reads);
            Assert.Single(outcomes, succeeded => succeeded);

            await using var verifyScope = provider.CreateAsyncScope();
            var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var package = await verifyDb.ProjectSetupSourcePackages.AsNoTracking().SingleAsync(x => x.DraftId == draftId);
            Assert.Equal(ProjectSetupSourceStage.Reconciled, package.Stage);
            Assert.NotNull(package.ManifestHash);
            Assert.Equal(configuredVersion + 1,
                (await verifyDb.ProjectSetupDrafts.AsNoTracking().SingleAsync(x => x.Id == draftId)).Version);
        });
    }

    private static async Task<(Guid AccountId, Guid BaselineId)> SeedNativeSourceAsync(ServiceProvider provider)
    {
        await using var seedScope = provider.CreateAsyncScope();
        var db = seedScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;
        var account = new UserAccount("pg-source-race-owner", "PG Source Race Owner",
            "pg-source-race-owner@example.test", "fixture-hash", now);
        var program = new ProgramRecord("PG source race program", "PSRP");
        var project = new ProjectRecord(program.Id, "PG source race source", "PG source race product");
        var release = new SoftwareRelease(project.Id, "1.0", false);
        var baseline = new CandidateBaseline("SW-92.01", 0, project.Id, release.Id, null,
            "PG source race baseline", "source.manager", now);
        var change = new SystemChangeRequest("SRCR-920001", 0, project.Id, release.Id,
            "PG source race requirement", "Problem", "Analysis", "Solution", "source.author", now);
        var requirement = new RequirementArtifact(project.Id, "SYSR-920001", RequirementLevel.System, now);
        var revision = new RequirementRevision(requirement.Id, 0, "The source race requirement shall remain exact.",
            "Source race rationale", "Inspection", RequirementRevisionState.Active, change.Id, baseline.Id, now);
        baseline.FreezeForInception("source.manager", now);
        baseline.MarkRequirementsMaterialized("source.manager", new string('d', 64), 1, now);
        db.AddRange(account, program, project, release, baseline, change, requirement, revision,
            new BaselineRequirementSelection(baseline.Id, requirement.Id, revision.Id));
        await db.SaveChangesAsync();
        return (account.Id, baseline.Id);
    }
}
