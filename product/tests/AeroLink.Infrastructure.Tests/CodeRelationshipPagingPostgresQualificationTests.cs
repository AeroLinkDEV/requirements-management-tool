using AeroLink.Domain.Integrations;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AeroLink.Infrastructure.Tests;

[Trait("Category", "PostgresQualification")]
public sealed class CodeRelationshipPagingPostgresQualificationTests
{
    [DisposablePostgresFact]
    public async Task Mixed_relationship_pages_use_one_provider_order_and_do_not_drop_later_rows()
    {
        await using var qualification = await DisposablePostgresDatabase.CreateAsync("aerolink_1023_code_page");
        var server = new NpgsqlConnectionStringBuilder(qualification.ConnectionString);
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
        var projectedFile = Assert.Single(second.Items);
        Assert.Equal(CodeRelationshipKind.File, projectedFile.RelationshipKind);
        Assert.Equal(snapshot.Id, projectedFile.SourceSnapshotId);
        Assert.Equal(snapshot.PathWithNamespace, projectedFile.RepositoryPathSnapshot);

        var exactFile = await new CodeRelationshipService(read).ReadPageAsync(project.Id, release.Id,
            CodeRelationshipKind.File, null, null, 1, 1, false, default, snapshot.Id, "src/demo.c");
        Assert.Equal(1, exactFile.Total);
        Assert.Equal("src/demo.c", Assert.Single(exactFile.Items).Path);
        var caseMismatch = await new CodeRelationshipService(read).ReadPageAsync(project.Id, release.Id,
            CodeRelationshipKind.File, null, null, 1, 1, false, default, snapshot.Id, "src/Demo.c");
        Assert.Equal(0, caseMismatch.Total);
    }
}
