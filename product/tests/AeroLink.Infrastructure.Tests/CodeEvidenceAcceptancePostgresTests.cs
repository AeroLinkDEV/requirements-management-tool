using System.Net;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Integrations;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Traceability;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AeroLink.Infrastructure.Tests;

[Trait("Category", "PostgresQualification")]
public sealed class CodeEvidenceAcceptancePostgresTests
{
    [DisposablePostgresFact]
    public async Task Concurrent_acceptance_has_one_selector_winner_and_no_orphan_evidence()
    {
        var raw = Environment.GetEnvironmentVariable("AEROLINK_MIGRATIONS_CONNECTION")
            ?? throw new InvalidOperationException("Code qualification requires an explicit disposable PostgreSQL connection.");
        var server = new NpgsqlConnectionStringBuilder(raw);
        var host = (server.Host ?? string.Empty).Trim('[', ']');
        if (!(host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
              || IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address)) || server.Port == 54329)
            throw new InvalidOperationException("Code qualification requires loopback PostgreSQL away from persistent port 54329.");

        var database = $"aerolink_1023_accept_{Guid.NewGuid():N}";
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
            {
                await migrate.Database.MigrateAsync();
                await migrate.Database.MigrateAsync();
            }

            var seed = await SeedAsync(options);
            for (var expectedVersion = 0L; expectedVersion <= 1; expectedVersion++)
            {
                var command = seed.Command with { ExpectedSelectorVersion = expectedVersion };
                var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var readyCount = 0;

                async Task<bool> AttemptAsync()
                {
                    await using var db = new AeroLinkDbContext(options);
                    if (Interlocked.Increment(ref readyCount) == 2)
                        ready.TrySetResult(true);
                    await gate.Task;
                    try
                    {
                        await using var scope = await ProjectControlledWriteScope.AcquireAsync(db, seed.ProjectId);
                        var result = await new CodeEvidenceAcceptanceService(db).AcceptAsync(scope,
                            command, new Dictionary<Guid, CodeEvidenceMergeObservation>(),
                            LegacyLadderPolicy.Instance, "race-winner", DateTimeOffset.UtcNow, default);
                        await db.SaveChangesAsync();
                        await scope.CommitAsync();
                        Assert.Equal(CodeEvidenceDisposition.NoCodeChangeRequired, result.Disposition);
                        return true;
                    }
                    catch (DomainException)
                    {
                        return false;
                    }
                }

                var first = AttemptAsync();
                var second = AttemptAsync();
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
                gate.TrySetResult(true);
                var results = await Task.WhenAll(first, second);
                Assert.Equal(1, results.Count(x => x));
                Assert.Equal(1, results.Count(x => !x));

                await using var verify = new AeroLinkDbContext(options);
                Assert.Equal(expectedVersion + 1, await verify.CodeEvidenceDispositionSets.CountAsync(x => x.ProjectId == seed.ProjectId));
                Assert.Empty(await verify.CodeEvidenceContributions.Where(x => x.ProjectId == seed.ProjectId).ToListAsync());
                var selector = await verify.CodeEvidenceCurrentSelectors.SingleAsync(x => x.ProjectId == seed.ProjectId);
                Assert.Equal(seed.ArtifactId, selector.RequirementArtifactId);
                Assert.Equal(seed.RevisionId, selector.RequirementRevisionId);
                Assert.Equal(expectedVersion + 1, selector.Version);
                Assert.Empty(await verify.CodeEvidenceInvalidations.Where(x => x.ProjectId == seed.ProjectId).ToListAsync());
            }
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{database}\" WITH (FORCE)", administrator);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static async Task<Seed> SeedAsync(DbContextOptions<AeroLinkDbContext> options)
    {
        var now = DateTimeOffset.UtcNow;
        var tag = Guid.NewGuid().ToString("N")[..8];
        var program = new ProgramRecord("Acceptance provider race", "APR" + tag);
        var project = new ProjectRecord(program.Id, "Acceptance provider race", "Synthetic code");
        var predecessor = new SoftwareRelease(project.Id, "0.9", true);
        var release = new SoftwareRelease(project.Id, "1.0", false, predecessor.Id);
        var sourceBaseline = new CandidateBaseline("BL-900004", 0, project.Id, predecessor.Id, null,
            "Prior", "tester", now);
        var baseline = new CandidateBaseline("BL-900003", 0, project.Id, release.Id, sourceBaseline.Id,
            "Current", "tester", now);
        var system = new RequirementArtifact(project.Id, "SYS-900003", RequirementLevel.System, now);
        var high = new RequirementArtifact(project.Id, "HLR-900003", RequirementLevel.HighLevel, now);
        var artifact = new RequirementArtifact(project.Id, "LLR-900003", RequirementLevel.LowLevel, now);
        var change = new SystemChangeRequest("LLRCR-90003", 0, project.Id, release.Id,
            "Concurrent acceptance", "Problem", "Analysis", "Solution", "tester", now,
            ChangeRequestType.Software, softwareLevel: RequirementLevel.LowLevel);
        var systemRevision = new RequirementRevision(system.Id, 1, "System behavior.", "Test", "Test",
            RequirementRevisionState.Active, change.Id, baseline.Id, now);
        var highRevision = new RequirementRevision(high.Id, 1, "High-level behavior.", "Test", "Test",
            RequirementRevisionState.Active, change.Id, baseline.Id, now, RequirementParentKind.Allocated,
            parentRevisionIds: [systemRevision.Id]);
        var revision = new RequirementRevision(artifact.Id, 1, "Implementation behavior.", "Test", "Test",
            RequirementRevisionState.Active, change.Id, baseline.Id, now, RequirementParentKind.Allocated,
            parentRevisionIds: [highRevision.Id]);
        var campaign = new ReleaseCampaign(project.Id, release.Id, baseline.Id, "Acceptance race campaign", "tester", now);

        await using (var db = new AeroLinkDbContext(options))
        {
            db.AddRange(program, project, predecessor, release, sourceBaseline, baseline, system, systemRevision,
                high, highRevision, artifact, change, revision, campaign,
                new BaselineRequirementSelection(baseline.Id, system.Id, systemRevision.Id),
                new BaselineRequirementSelection(baseline.Id, high.Id, highRevision.Id),
                new BaselineRequirementSelection(baseline.Id, artifact.Id, revision.Id),
                new RequirementTraceLink(project.Id, highRevision.Id, systemRevision.Id,
                    RequirementTraceType.AllocatedFrom, "Exact parent", now),
                new RequirementTraceLink(project.Id, revision.Id, highRevision.Id,
                    RequirementTraceType.AllocatedFrom, "Exact parent", now));
            await db.SaveChangesAsync();
            await db.CandidateBaselines.Where(x => x.Id == baseline.Id).ExecuteUpdateAsync(update => update
                .SetProperty(x => x.State, CandidateBaselineState.Frozen)
                .SetProperty(x => x.RequirementsMaterializedAt, now));
        }

        return new(project.Id, artifact.Id, revision.Id,
            new CodeEvidenceAcceptanceCommand(project.Id, release.Id, baseline.Id, artifact.Id, revision.Id,
                CodeEvidenceDisposition.NoCodeChangeRequired, 0, null, null, null, null, null, [],
                "No implementation code change is required."));
    }

    private sealed record Seed(Guid ProjectId, Guid ArtifactId, Guid RevisionId, CodeEvidenceAcceptanceCommand Command);
}
