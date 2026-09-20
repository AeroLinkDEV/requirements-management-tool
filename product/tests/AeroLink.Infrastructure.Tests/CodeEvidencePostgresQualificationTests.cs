using System.Net;
using AeroLink.Domain.Integrations;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AeroLink.Infrastructure.Tests;

public sealed class CodeEvidencePostgresQualificationTests
{
    [DisposablePostgresFact]
    public async Task Optional_associations_exact_sources_and_retained_evidence_selectors_survive_provider_restart_and_conflicts()
    {
        var raw = Environment.GetEnvironmentVariable("AEROLINK_MIGRATIONS_CONNECTION")
            ?? throw new InvalidOperationException("Code qualification requires an explicit disposable PostgreSQL connection.");
        var server = new NpgsqlConnectionStringBuilder(raw);
        var host = (server.Host ?? "").Trim('[', ']');
        if (!(host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
              || IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address)) || server.Port == 54329)
            throw new InvalidOperationException("Code qualification requires loopback PostgreSQL away from persistent port 54329.");
        var database = $"aerolink_1023_code_{Guid.NewGuid():N}";
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

            var now = DateTimeOffset.UtcNow;
            var program = new ProgramRecord("Code provider qualification", "CPQ");
            var project = new ProjectRecord(program.Id, "Code provider qualification", "Synthetic code");
            var release = new SoftwareRelease(project.Id, "1.0", false);
            var repository = new ProjectRepositoryConfiguration(project.Id, ProjectRepositorySetupMode.ConnectNow,
                "GitLab", "https://gitlab.example/demo/code", "tester", now);
            var artifact = Guid.NewGuid();
            var revision = Guid.NewGuid();
            CodeEvidenceDispositionSet NoCode(string reason) => new(project.Id, release.Id, artifact, revision,
                CodeEvidenceDisposition.NoCodeChangeRequired, reason, null, null, null, "tester", now);
            var firstEvidence = NoCode("Initial documentation-only disposition.");
            var secondEvidence = NoCode("Explicit replacement disposition.");
            var losingEvidence = NoCode("Competing replacement must not become current.");
            var selector = new CodeEvidenceCurrentSelector(project.Id, release.Id, artifact, revision, firstEvidence.Id, "tester", now);
            var target = CodeRelationshipTarget.ForRequirementRevision(revision, artifact, 1, "LLR-000001.01");
            var association = new GitLabMergeRequestRelationship(project.Id, release.Id, "https://gitlab.example", 17,
                3, 53, null, null, "demo/code", "https://gitlab.example/demo/code/-/merge_requests/3",
                "Context before source selection", target, CodeRelationshipMeaning.RelatedContext, "tester", now);
            var snapshot = new GitLabSourceSnapshot(project.Id, repository.Id, "https://gitlab.example", 17,
                "demo/code", new string('a', 40), "main", "tester", now, repository.Version);
            var file = new GitLabFileRelationship(project.Id, release.Id, snapshot.InstanceBaseUrl, snapshot.RemoteProjectId,
                snapshot.Id, null, snapshot.CommitSha, "src/demo.c", null, null, null,
                target, CodeRelationshipMeaning.RelatedContext, "tester", now);
            await using (var seed = new AeroLinkDbContext(options))
            {
                seed.AddRange(program, project, release, repository, firstEvidence, secondEvidence, losingEvidence,
                    selector, association, snapshot, file);
                await seed.SaveChangesAsync();
                var register = await CodeMergeRequestRegisterProjection.ReadPageAsync(seed, project.Id,
                    release.Id, 1, 1, false, default);
                Assert.Equal(1, register.Total);
                Assert.Equal(3, Assert.Single(register.Items).MergeRequestIid);
                Assert.Empty((await CodeMergeRequestRegisterProjection.ReadPageAsync(seed, project.Id,
                    release.Id, 2, 1, false, default)).Items);
            }

            await using (var restarted = new AeroLinkDbContext(options))
            {
                Assert.Null((await restarted.CodeEvidenceDispositionSets.SingleAsync(x => x.Id == firstEvidence.Id)).SourceSnapshotId);
                Assert.Null((await restarted.GitLabMergeRequestRelationships.SingleAsync()).SourceSelectionEventId);
                Assert.Null((await restarted.GitLabFileRelationships.SingleAsync()).SourceSelectionEventId);
                Assert.Empty(await restarted.GitLabCurrentSourceSelections.ToListAsync());
            }
            // Both callers read version 1. The second write must lose rather than overwrite the explicit selection.
            await using (var first = new AeroLinkDbContext(options))
            await using (var second = new AeroLinkDbContext(options))
            {
                var a = await first.CodeEvidenceCurrentSelectors.SingleAsync();
                var b = await second.CodeEvidenceCurrentSelectors.SingleAsync();
                a.Move(1, secondEvidence.Id, "tester", now);
                b.Move(1, losingEvidence.Id, "tester", now);
                await first.SaveChangesAsync();
                await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
            }
            await using (var invalidate = new AeroLinkDbContext(options))
            {
                invalidate.Add(new CodeEvidenceInvalidation(secondEvidence.Id, project.Id, release.Id, artifact, revision,
                    "tester", "Exact requirement was taken back; retain the recorded decision.", now));
                await invalidate.SaveChangesAsync();
            }
            await using (var verify = new AeroLinkDbContext(options))
            {
                Assert.Equal(secondEvidence.Id, (await verify.CodeEvidenceCurrentSelectors.SingleAsync()).EvidenceSetId);
                Assert.Equal(3, await verify.CodeEvidenceDispositionSets.CountAsync());
                Assert.Equal(secondEvidence.Id, (await verify.CodeEvidenceInvalidations.SingleAsync()).EvidenceSetId);
            }
            await using (var wrongIdentity = new AeroLinkDbContext(options))
            {
                wrongIdentity.Add(new GitLabFileRelationship(project.Id, release.Id, snapshot.InstanceBaseUrl, 999,
                    snapshot.Id, null, snapshot.CommitSha, "src/other.c", null, null, null,
                    target, CodeRelationshipMeaning.RelatedContext, "tester", now));
                var failure = await Assert.ThrowsAsync<DbUpdateException>(() => wrongIdentity.SaveChangesAsync());
                Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, Assert.IsType<PostgresException>(failure.InnerException).SqlState);
            }
            await using (var duplicate = new AeroLinkDbContext(options))
            {
                // An optional MR annotation must not manufacture a second active file edge.
                duplicate.Add(new GitLabFileRelationship(project.Id, release.Id, snapshot.InstanceBaseUrl, snapshot.RemoteProjectId,
                    snapshot.Id, null, snapshot.CommitSha, file.Path, null, null, 99,
                    target, CodeRelationshipMeaning.RelatedContext, "tester", now));
                var failure = await Assert.ThrowsAsync<DbUpdateException>(() => duplicate.SaveChangesAsync());
                Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(failure.InnerException).SqlState);
            }
            var sourceEvent = new GitLabSourceSelectionEvent(project.Id, release.Id, snapshot.Id, 0, "tester", now);
            var mergedEvidence = CodeEvidenceDispositionSet.CreateGitLab(project.Id, release.Id, artifact, revision,
                sourceEvent, snapshot, null, "tester", now);
            var mergedContribution = new CodeEvidenceContribution(mergedEvidence.Id, project.Id, release.Id,
                artifact, revision, snapshot.Id, CodeEvidenceContributionKind.MergeRequest, association.Id,
                snapshot.InstanceBaseUrl, snapshot.RemoteProjectId, snapshot.PathWithNamespace, 3, 53,
                "https://gitlab.example/demo/code/-/merge_requests/3", "Context before source selection",
                snapshot.CommitSha, null, null, null, target, "tester", now,
                new string('b', 40), GitLabMergeResultKind.SquashCommit, now.AddHours(-1), now);
            await using (var captureMerge = new AeroLinkDbContext(options))
            {
                captureMerge.AddRange(sourceEvent, mergedEvidence, mergedContribution);
                await captureMerge.SaveChangesAsync();
            }
            await using (var readMerge = new AeroLinkDbContext(options))
            {
                var persisted = await readMerge.CodeEvidenceContributions.SingleAsync(x => x.Id == mergedContribution.Id);
                Assert.Equal(snapshot.CommitSha, persisted.CommitSha);
                Assert.Equal(new string('b', 40), persisted.MergeResultSha);
                Assert.Equal(GitLabMergeResultKind.SquashCommit, persisted.MergeResultKind);
                Assert.Equal(now.AddHours(-1).ToUnixTimeMilliseconds(), persisted.MergedAt!.Value.ToUnixTimeMilliseconds());
                Assert.Equal(now.ToUnixTimeMilliseconds(), persisted.ProviderObservedAt!.Value.ToUnixTimeMilliseconds());
            }
        }
        finally
        {
            // The name is generated above and is never supplied by the caller.
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
