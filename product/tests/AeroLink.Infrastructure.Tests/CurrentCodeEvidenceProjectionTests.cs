using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Integrations;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Traceability;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

public sealed class CurrentCodeEvidenceProjectionTests
{
    [Fact]
    public async Task Invalidated_new_decision_never_falls_back_to_retained_legacy_evidence()
    {
        await using var f = await Fixture.CreateAsync();
        Assert.Equal(CurrentCodeEvidenceState.LegacyAccepted, (await f.CurrentAsync()).State);
        var set = new CodeEvidenceDispositionSet(f.Project.Id, f.Release.Id, f.Artifact.Id, f.Revision.Id,
            CodeEvidenceDisposition.NoCodeChangeRequired, "Explicit replacement decision.", null, null, f.Legacy.Id, "tester", f.Now);
        var selector = new CodeEvidenceCurrentSelector(f.Project.Id, f.Release.Id, f.Artifact.Id, f.Revision.Id, set.Id, "tester", f.Now);
        f.Db.AddRange(set, selector);
        await f.Db.SaveChangesAsync();
        var current = await f.CurrentAsync();
        Assert.True(current.CountsAsImplementation);
        Assert.Equal(set.Id, current.EvidenceSet!.Id);
        Assert.Null(current.LegacyRecord);
        f.Db.Add(new CodeEvidenceInvalidation(set.Id, f.Project.Id, f.Release.Id, f.Artifact.Id, f.Revision.Id,
            "tester", "Candidate taken back.", f.Now));
        await f.Db.SaveChangesAsync();
        current = await f.CurrentAsync();
        Assert.Equal(CurrentCodeEvidenceState.Invalidated, current.State);
        Assert.False(current.CountsAsImplementation);
        Assert.Null(current.LegacyRecord);
        Assert.Equal(1, await f.Db.CodeTraceabilityRecords.CountAsync());
        Assert.Equal(set.Id, (await f.Db.CodeEvidenceCurrentSelectors.SingleAsync()).EvidenceSetId);
    }

    [Fact]
    public async Task A_to_B_to_A_requires_explicit_reconfirmation_instead_of_reviving_acceptance()
    {
        await using var f = await Fixture.CreateAsync();
        var snapshotA = f.Snapshot('a');
        var first = new GitLabSourceSelectionEvent(f.Project.Id, f.Release.Id, snapshotA.Id, 0, "tester", f.Now);
        var currentSource = new GitLabCurrentSourceSelection(f.Project.Id, f.Release.Id, snapshotA.Id, first.Id, "tester", f.Now);
        f.Db.AddRange(snapshotA, first, currentSource);
        var set = f.AcceptFile(snapshotA, first);
        var selector = new CodeEvidenceCurrentSelector(f.Project.Id, f.Release.Id, f.Artifact.Id, f.Revision.Id, set.Id, "tester", f.Now);
        f.Db.Add(selector);
        await f.Db.SaveChangesAsync();
        Assert.True((await f.CurrentAsync()).CountsAsImplementation);

        var snapshotB = f.Snapshot('b');
        var second = new GitLabSourceSelectionEvent(f.Project.Id, f.Release.Id, snapshotB.Id, 1, "tester", f.Now);
        currentSource.Move(1, snapshotB.Id, second.Id, "tester", f.Now);
        f.Db.AddRange(snapshotB, second);
        await f.Db.SaveChangesAsync();
        Assert.Equal(CurrentCodeEvidenceState.SourceChanged, (await f.CurrentAsync()).State);

        var third = new GitLabSourceSelectionEvent(f.Project.Id, f.Release.Id, snapshotA.Id, 2, "tester", f.Now);
        currentSource.Move(2, snapshotA.Id, third.Id, "tester", f.Now);
        f.Db.Add(third);
        await f.Db.SaveChangesAsync();
        Assert.Equal(CurrentCodeEvidenceState.SourceChanged, (await f.CurrentAsync()).State);
        var replacement = f.AcceptFile(snapshotA, third);
        selector.Move(1, replacement.Id, "tester", f.Now);
        await f.Db.SaveChangesAsync();
        var confirmed = await f.CurrentAsync();
        Assert.Equal(CurrentCodeEvidenceState.Accepted, confirmed.State);
        Assert.Equal(replacement.Id, confirmed.EvidenceSet!.Id);
        Assert.Equal(2, await f.Db.CodeEvidenceDispositionSets.CountAsync());
    }

    [Fact]
    public async Task Contextual_proposal_contribution_does_not_count_as_exact_requirement_acceptance()
    {
        await using var f = await Fixture.CreateAsync();
        var source = f.Snapshot('a');
        var selection = new GitLabSourceSelectionEvent(f.Project.Id, f.Release.Id, source.Id, 0, "tester", f.Now);
        f.Db.AddRange(source, selection,
            new GitLabCurrentSourceSelection(f.Project.Id, f.Release.Id, source.Id, selection.Id, "tester", f.Now));
        var set = f.AcceptFile(source, selection, CodeRelationshipTarget.ForRequirementProposal(Guid.NewGuid(), null, "Proposed requirement"));
        f.Db.Add(new CodeEvidenceCurrentSelector(f.Project.Id, f.Release.Id, f.Artifact.Id, f.Revision.Id, set.Id, "tester", f.Now));
        await f.Db.SaveChangesAsync();
        Assert.Equal(CurrentCodeEvidenceState.InvalidIdentity, (await f.CurrentAsync()).State);
        Assert.Empty(await CurrentCodeEvidenceProjection.ForReleaseAsync(f.Db, Guid.NewGuid(), f.Release.Id, default));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        public AeroLinkDbContext Db { get; private set; } = null!;
        public DateTimeOffset Now { get; } = DateTimeOffset.UtcNow;
        public ProjectRecord Project { get; private set; } = null!;
        public SoftwareRelease Release { get; private set; } = null!;
        public RequirementArtifact Artifact { get; private set; } = null!;
        public RequirementRevision Revision { get; private set; } = null!;
        public CodeTraceabilityRecord Legacy { get; private set; } = null!;
        private ProjectRepositoryConfiguration repository = null!;
        public static async Task<Fixture> CreateAsync()
        {
            var f = new Fixture();
            await f.connection.OpenAsync();
            f.Db = new(new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite(f.connection).Options);
            await f.Db.Database.EnsureCreatedAsync();
            var program = new ProgramRecord("Evidence projection", "EVP");
            f.Project = new(program.Id, "Evidence projection", "Synthetic source");
            var predecessor = new SoftwareRelease(f.Project.Id, "0.9", true);
            f.Release = new(f.Project.Id, "1.0", false, predecessor.Id);
            var sourceBaseline = new CandidateBaseline("BL-000001", 0, f.Project.Id, predecessor.Id, null, "Prior", "tester", f.Now);
            var baseline = new CandidateBaseline("BL-000002", 0, f.Project.Id, f.Release.Id, sourceBaseline.Id, "Current", "tester", f.Now);
            var system = new RequirementArtifact(f.Project.Id, "SYS-000001", RequirementLevel.System, f.Now);
            var systemRevision = RequirementRevision.FromAeroLinkBaseline(system.Id, 1, "System behavior.", "Test", RequirementRevisionState.Active,
                sourceBaseline.Id, baseline.Id, f.Now, "SYS-000001.00");
            var high = new RequirementArtifact(f.Project.Id, "HLR-000001", RequirementLevel.HighLevel, f.Now);
            var highRevision = RequirementRevision.FromAeroLinkBaseline(high.Id, 1, "Software behavior.", "Test", RequirementRevisionState.Active,
                sourceBaseline.Id, baseline.Id, f.Now, "HLR-000001.00", parentKind: RequirementParentKind.Allocated, parentRevisionIds: [systemRevision.Id]);
            f.Artifact = new(f.Project.Id, "LLR-000001", RequirementLevel.LowLevel, f.Now);
            f.Revision = RequirementRevision.FromAeroLinkBaseline(f.Artifact.Id, 1, "Synthetic requirement.", "Test", RequirementRevisionState.Active,
                sourceBaseline.Id, baseline.Id, f.Now, "LLR-000001.00", parentKind: RequirementParentKind.Allocated, parentRevisionIds: [highRevision.Id]);
            f.Legacy = new(f.Project.Id, f.Release.Id, f.Artifact.Id, f.Revision.Id, CodeTraceDisposition.NoCodeChangeRequired,
                "", "", "", "", "", null, "Existing retained disposition.", false, "tester", f.Now);
            f.repository = new(f.Project.Id, ProjectRepositorySetupMode.ConnectNow, "GitLab", "https://gitlab.example/group/project", "tester", f.Now);
            f.Db.AddRange(program, f.Project, predecessor, f.Release, sourceBaseline, baseline, f.Artifact, f.Revision, f.Legacy, f.repository);
            f.Db.AddRange(system, systemRevision, high, highRevision,
                new BaselineRequirementSelection(baseline.Id, system.Id, systemRevision.Id),
                new BaselineRequirementSelection(baseline.Id, high.Id, highRevision.Id),
                new BaselineRequirementSelection(baseline.Id, f.Artifact.Id, f.Revision.Id),
                new RequirementTraceLink(f.Project.Id, highRevision.Id, systemRevision.Id, RequirementTraceType.AllocatedFrom, "Exact parent", f.Now),
                new RequirementTraceLink(f.Project.Id, f.Revision.Id, highRevision.Id, RequirementTraceType.AllocatedFrom, "Exact parent", f.Now));
            await f.Db.SaveChangesAsync();
            return f;
        }
        public GitLabSourceSnapshot Snapshot(char sha) => new(Project.Id, repository.Id, "https://gitlab.example", 17,
            "group/project", new string(sha, 40), "main", "tester", Now, repository.Version);
        public CodeEvidenceDispositionSet AcceptFile(GitLabSourceSnapshot snapshot, GitLabSourceSelectionEvent selection,
            CodeRelationshipTarget? target = null)
        {
            var set = CodeEvidenceDispositionSet.CreateGitLab(Project.Id, Release.Id, Artifact.Id, Revision.Id, selection, snapshot, Legacy.Id, "tester", Now);
            Db.Add(set);
            Db.Add(new CodeEvidenceContribution(set.Id, Project.Id, Release.Id, Artifact.Id, Revision.Id, snapshot.Id, CodeEvidenceContributionKind.File,
                null, snapshot.InstanceBaseUrl, snapshot.RemoteProjectId, snapshot.PathWithNamespace, null, null, null, null,
                snapshot.CommitSha, "src/demo.c", null, null,
                target ?? CodeRelationshipTarget.ForRequirementRevision(Revision.Id, Artifact.Id, Revision.Revision, "LLR-000001.01"), "tester", Now));
            return set;
        }
        public async Task<CurrentCodeEvidence> CurrentAsync() => Assert.Single(await CurrentCodeEvidenceProjection.ForReleaseAsync(Db, Project.Id, Release.Id, default));
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await connection.DisposeAsync(); }
    }
}
