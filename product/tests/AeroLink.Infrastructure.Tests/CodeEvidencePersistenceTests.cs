using AeroLink.Domain.Integrations;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

public sealed class CodeEvidencePersistenceTests
{
    [Fact]
    public async Task No_code_mr_association_and_file_pointer_persist_with_their_intended_source_requirements()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite(connection).Options;
        await using var db = new AeroLinkDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var now = new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);
        var program = new ProgramRecord("Code evidence", "CODE");
        var project = new ProjectRecord(program.Id, "Code evidence", "Code evidence product");
        var release = new SoftwareRelease(project.Id, "1.0", false);
        var configuration = new ProjectRepositoryConfiguration(project.Id, ProjectRepositorySetupMode.ConnectNow,
            "GitLab", "https://gitlab.example.com/aerolink/demo", "tester", now);
        db.AddRange(program, project, release, configuration);
        await db.SaveChangesAsync();

        var artifactId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var noCode = new CodeEvidenceDispositionSet(project.Id, release.Id, artifactId, revisionId,
            CodeEvidenceDisposition.NoCodeChangeRequired, "This release changes documentation only.", null, null, null, "tester", now);
        var target = CodeRelationshipTarget.ForRequirementRevision(revisionId, artifactId, 1, "LLR-000001.01");
        var mergeRequest = new GitLabMergeRequestRelationship(project.Id, release.Id, "https://gitlab.example.com", 10,
            17, 501, null, null, "aerolink/demo", "https://gitlab.example.com/aerolink/demo/-/merge_requests/17",
            "Implement LLR-000001", target, CodeRelationshipMeaning.Implements, "tester", now);
        db.AddRange(noCode, mergeRequest);
        await db.SaveChangesAsync();

        var snapshot = new GitLabSourceSnapshot(project.Id, configuration.Id, "https://gitlab.example.com", 10,
            "aerolink/demo", new('a', 40), "main", "tester", now, configuration.Version);
        var file = new GitLabFileRelationship(project.Id, release.Id, "https://gitlab.example.com", 10,
            snapshot.Id, null, snapshot.CommitSha, "src/flight.cs", 10, 12, 17, target,
            CodeRelationshipMeaning.Addresses, "tester", now);
        db.AddRange(snapshot, file);
        await db.SaveChangesAsync();

        Assert.NotNull(await db.CodeEvidenceDispositionSets.SingleAsync(x => x.Id == noCode.Id));
        var persistedMr = await db.GitLabMergeRequestRelationships.SingleAsync(x => x.Id == mergeRequest.Id);
        Assert.Null(persistedMr.SourceSnapshotId);
        Assert.Null(persistedMr.SourceSelectionEventId);
        var persistedFile = await db.GitLabFileRelationships.SingleAsync(x => x.Id == file.Id);
        Assert.Equal(snapshot.Id, persistedFile.SourceSnapshotId);
        Assert.Null(persistedFile.SourceSelectionEventId);
    }

    [Fact]
    public async Task File_pointer_rejects_a_source_identity_that_does_not_match_the_snapshot()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite(connection).Options;
        await using var db = new AeroLinkDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var now = DateTimeOffset.UtcNow;
        var program = new ProgramRecord("Code identity", "CID");
        var project = new ProjectRecord(program.Id, "Code identity", "Code identity product");
        var release = new SoftwareRelease(project.Id, "1.0", false);
        var configuration = new ProjectRepositoryConfiguration(project.Id, ProjectRepositorySetupMode.ConnectNow,
            "GitLab", "https://gitlab.example.com/aerolink/demo", "tester", now);
        db.AddRange(program, project, release, configuration);
        await db.SaveChangesAsync();

        var snapshot = new GitLabSourceSnapshot(project.Id, configuration.Id, "https://gitlab.example.com", 10,
            "aerolink/demo", new('b', 40), "main", "tester", now, configuration.Version);
        db.Add(snapshot);
        await db.SaveChangesAsync();
        var target = CodeRelationshipTarget.ForRequirementRevision(Guid.NewGuid(), Guid.NewGuid(), 1, "LLR-000002.01");
        var file = new GitLabFileRelationship(project.Id, release.Id, "https://gitlab.example.com", 11,
            snapshot.Id, null, snapshot.CommitSha, "src/flight.cs", null, null, null, target,
            CodeRelationshipMeaning.RelatedContext, "tester", now);
        db.Add(file);

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
