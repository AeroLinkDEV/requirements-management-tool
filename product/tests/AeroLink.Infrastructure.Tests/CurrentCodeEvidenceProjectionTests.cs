using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Integrations;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Traceability;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

public sealed class CurrentCodeEvidenceProjectionTests
{
    [Fact]
    public async Task Release_gate_does_not_revive_legacy_evidence_after_replacement_is_invalidated()
    {
        await using var f = await Fixture.CreateAsync(changedInBuild: true);
        var campaign = await f.CampaignAsync();
        await f.Db.CandidateBaselines.Where(x => x.Id == campaign.BaselineId).ExecuteUpdateAsync(update => update
            .SetProperty(x => x.State, CandidateBaselineState.Frozen)
            .SetProperty(x => x.RequirementsMaterializedAt, f.Now));
        async Task<ReadinessGate> Gate() => (await new ReleaseReadinessService(f.Db).CalculateAsync(campaign.Id, default))
            .Gates.Single(x => x.Code == "code_traceability");
        Assert.Equal(1, (await Gate()).Completed);
        var set = new CodeEvidenceDispositionSet(f.Project.Id, f.Release.Id, f.Artifact.Id, f.Revision.Id,
            CodeEvidenceDisposition.NoCodeChangeRequired, "Replacement decision.", null, null, f.Legacy.Id, "tester", f.Now);
        f.Db.AddRange(set, new CodeEvidenceCurrentSelector(f.Project.Id, f.Release.Id, f.Artifact.Id, f.Revision.Id,
            set.Id, "tester", f.Now));
        await f.Db.SaveChangesAsync();
        Assert.Equal(1, (await Gate()).Completed);
        f.Db.Add(new CodeEvidenceInvalidation(set.Id, f.Project.Id, f.Release.Id, f.Artifact.Id, f.Revision.Id,
            "tester", "Evidence no longer applies.", f.Now));
        await f.Db.SaveChangesAsync();
        var gate = await Gate();
        Assert.Equal(1, gate.Total);
        Assert.Equal(0, gate.Completed);
        Assert.False(gate.Complete);
        Assert.Equal(1, await f.Db.CodeTraceabilityRecords.CountAsync());
    }

    [Fact]
    public async Task Removing_exact_parent_link_from_a_retained_requirement_still_fails_closed()
    {
        await using var f = await Fixture.CreateAsync();
        f.Db.RequirementTraces.Remove(await f.Db.RequirementTraces.SingleAsync(x => x.SourceRevisionId == f.Revision.Id));
        await Assert.ThrowsAsync<AeroLink.Domain.Common.DomainException>(() => f.Db.SaveChangesAsync());
    }

    [Fact]
    public async Task Reopen_preview_matches_retained_evidence_invalidation_without_legacy_fallback()
    {
        await using var f = await Fixture.CreateAsync();
        var set = new CodeEvidenceDispositionSet(f.Project.Id, f.Release.Id, f.Artifact.Id, f.Revision.Id,
            CodeEvidenceDisposition.NoCodeChangeRequired, "Controlled decision.", null, null, f.Legacy.Id, "tester", f.Now);
        f.Db.AddRange(set, new CodeEvidenceCurrentSelector(f.Project.Id, f.Release.Id, f.Artifact.Id, f.Revision.Id,
            set.Id, "tester", f.Now));
        await f.Db.SaveChangesAsync();
        var baseline = await f.Db.CandidateBaselines.SingleAsync(x => x.ReleaseId == f.Release.Id);
        var service = new RequirementBaselineDematerializer(f.Db, new VerificationImpactService(f.Db));
        var preview = await service.PreviewAsync(baseline.Id, baseline.DisplayNumber, default);
        Assert.Equal(1, preview.CodeEvidenceSetsInvalidated);
        Assert.Empty(await f.Db.CodeEvidenceInvalidations.ToListAsync());
        await using var scope = await ProjectControlledWriteScope.AcquireAsync(f.Db, f.Project.Id);
        var actual = await service.DematerializeAsync(baseline.Id, "tester", baseline.DisplayNumber, f.Now, default, scope);
        await f.Db.SaveChangesAsync();
        await scope.CommitAsync();
        Assert.Equal(preview.CodeEvidenceSetsInvalidated, actual.CodeEvidenceSetsInvalidated);
        Assert.Equal(set.Id, (await f.Db.CodeEvidenceDispositionSets.SingleAsync()).Id);
        Assert.Equal(set.Id, (await f.Db.CodeEvidenceCurrentSelectors.SingleAsync()).EvidenceSetId);
        var current = await f.CurrentAsync();
        Assert.Equal(CurrentCodeEvidenceState.Invalidated, current.State);
        Assert.False(current.CountsAsImplementation);
        Assert.Null(current.LegacyRecord);
        Assert.Contains(baseline.DisplayNumber, current.InvalidationRationale!);
        Assert.Equal(0, (await service.PreviewAsync(baseline.Id, baseline.DisplayNumber, default)).CodeEvidenceSetsInvalidated);
        Assert.Single(await f.Db.CodeEvidenceInvalidations.ToListAsync());
    }

    [Fact]
    public async Task Associated_register_pages_grouped_local_identities_before_remote_decoration()
    {
        await using var f = await Fixture.CreateAsync();
        var target = CodeRelationshipTarget.ForRequirementRevision(f.Revision.Id, f.Artifact.Id, 1, "LLR-000001.01");
        GitLabMergeRequestRelationship Mr(int iid, CodeRelationshipMeaning meaning, Guid? release = null) =>
            new(f.Project.Id, release ?? f.Release.Id, "https://gitlab.example", 17, iid, null, null, null,
                "group/project", $"https://gitlab.example/group/project/-/merge_requests/{iid}", $"Recorded MR {iid}",
                target, meaning, "tester", f.Now);
        var first = Mr(1, CodeRelationshipMeaning.Implements);
        var firstContext = Mr(1, CodeRelationshipMeaning.RelatedContext);
        var withdrawn = Mr(3, CodeRelationshipMeaning.Addresses);
        withdrawn.Withdraw(1, "tester", "Retain abandoned work.", f.Now);
        var predecessor = await f.Db.Releases.SingleAsync(x => x.ProjectId == f.Project.Id && x.Id != f.Release.Id);
        var snapshot = f.Snapshot('a');
        var file = new GitLabFileRelationship(f.Project.Id, f.Release.Id, snapshot.InstanceBaseUrl, 17,
            snapshot.Id, null, snapshot.CommitSha, "src/demo.c", null, null, 2, target,
            CodeRelationshipMeaning.Implements, "tester", f.Now);
        f.Db.AddRange(first, firstContext, withdrawn, Mr(99, CodeRelationshipMeaning.Implements, predecessor.Id), snapshot, file);
        await f.Db.SaveChangesAsync();
        var page1 = await CodeMergeRequestRegisterProjection.ReadPageAsync(f.Db, f.Project.Id, f.Release.Id, 1, 1, false, default);
        var page2 = await CodeMergeRequestRegisterProjection.ReadPageAsync(f.Db, f.Project.Id, f.Release.Id, 2, 1, false, default);
        Assert.Equal(2, page1.Total);
        Assert.Equal(1, Assert.Single(page1.Items).MergeRequestIid);
        Assert.Equal(2, page1.Items[0].RelationshipCount);
        Assert.Equal(2, Assert.Single(page2.Items).MergeRequestIid);
        Assert.Empty((await CodeMergeRequestRegisterProjection.ReadPageAsync(f.Db, f.Project.Id, f.Release.Id, 3, 1, false, default)).Items);
        Assert.Equal(3, (await CodeMergeRequestRegisterProjection.ReadPageAsync(f.Db, f.Project.Id, f.Release.Id, 1, 10, true, default)).Total);
        Assert.Empty((await CodeMergeRequestRegisterProjection.ReadPageAsync(f.Db, Guid.NewGuid(), f.Release.Id, 1, 10, true, default)).Items);
    }

    [Fact]
    public async Task V2_manifest_is_stable_and_commits_explicit_selection_and_invalidation()
    {
        await using var f = await Fixture.CreateAsync();
        var campaign = await f.CampaignAsync();
        var builder = new CodeReviewManifestBuilder(f.Db);
        Task<CodeReviewManifestMaterial> Build() => builder.BuildV2Async(campaign.Id, new string('a', 64), LegacyLadderPolicy.Instance, default);
        var empty = await Build();
        Assert.Equal(empty.Hash, (await Build()).Hash);
        var snapshot = f.Snapshot('a');
        var selection = new GitLabSourceSelectionEvent(f.Project.Id, f.Release.Id, snapshot.Id, 0, "tester", f.Now);
        f.Db.AddRange(snapshot, selection, new GitLabCurrentSourceSelection(f.Project.Id, f.Release.Id,
            snapshot.Id, selection.Id, "tester", f.Now));
        var set = f.AcceptFile(snapshot, selection);
        f.Db.Add(new CodeEvidenceCurrentSelector(f.Project.Id, f.Release.Id, f.Artifact.Id, f.Revision.Id, set.Id, "tester", f.Now));
        await f.Db.SaveChangesAsync();
        var accepted = await Build();
        Assert.NotEqual(empty.Hash, accepted.Hash);
        Assert.Equal(selection.Id, accepted.SourceSelectionEventId);
        Assert.Equal(snapshot.Id, accepted.SourceSnapshotId);
        Assert.Equal(set.Id, Assert.Single(accepted.EvidenceReferenceIds));
        Assert.Equal(accepted.Hash, (await Build()).Hash);
        f.Db.Add(new CodeEvidenceInvalidation(set.Id, f.Project.Id, f.Release.Id, f.Artifact.Id, f.Revision.Id,
            "tester", "Reopened", f.Now));
        await f.Db.SaveChangesAsync();
        Assert.NotEqual(accepted.Hash, (await Build()).Hash);
        var invalidatedHash = (await Build()).Hash;
        var offBaseline = new CodeEvidenceDispositionSet(f.Project.Id, f.Release.Id, Guid.NewGuid(), Guid.NewGuid(),
            CodeEvidenceDisposition.NoCodeChangeRequired, "Historical off-baseline record.", null, null, null, "tester", f.Now);
        f.Db.AddRange(offBaseline, new CodeEvidenceCurrentSelector(f.Project.Id, f.Release.Id,
            offBaseline.RequirementArtifactId, offBaseline.RequirementRevisionId, offBaseline.Id, "tester", f.Now));
        await f.Db.SaveChangesAsync();
        Assert.Equal(invalidatedHash, (await Build()).Hash);
    }

    [Fact]
    public async Task V2_manifest_commits_reconfirmation_even_when_returning_to_the_same_snapshot()
    {
        await using var f = await Fixture.CreateAsync();
        var campaign = await f.CampaignAsync();
        var snapshot = f.Snapshot('a');
        var selection = new GitLabSourceSelectionEvent(f.Project.Id, f.Release.Id, snapshot.Id, 0, "tester", f.Now);
        var pointer = new GitLabCurrentSourceSelection(f.Project.Id, f.Release.Id, snapshot.Id, selection.Id, "tester", f.Now);
        f.Db.AddRange(snapshot, selection, pointer);
        await f.Db.SaveChangesAsync();
        var builder = new CodeReviewManifestBuilder(f.Db);
        Task<CodeReviewManifestMaterial> Build() => builder.BuildV2Async(campaign.Id, new string('a', 64), LegacyLadderPolicy.Instance, default);
        var first = await Build();
        var secondSnapshot = f.Snapshot('b');
        var second = new GitLabSourceSelectionEvent(f.Project.Id, f.Release.Id, secondSnapshot.Id, 1, "tester", f.Now);
        f.Db.AddRange(secondSnapshot, second);
        pointer.Move(1, secondSnapshot.Id, second.Id, "tester", f.Now);
        await f.Db.SaveChangesAsync();
        Assert.NotEqual(first.Hash, (await Build()).Hash);
        var third = new GitLabSourceSelectionEvent(f.Project.Id, f.Release.Id, snapshot.Id, 2, "tester", f.Now);
        f.Db.Add(third);
        pointer.Move(2, snapshot.Id, third.Id, "tester", f.Now);
        await f.Db.SaveChangesAsync();
        var final = await Build();
        Assert.Equal(first.SourceSnapshotId, final.SourceSnapshotId);
        Assert.NotEqual(first.SourceSelectionEventId, final.SourceSelectionEventId);
        Assert.NotEqual(first.Hash, final.Hash);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Retained_history_for_a_missing_or_mismatched_requirement_does_not_count(bool wrongArtifact)
    {
        await using var f = await Fixture.CreateAsync();
        var artifactId = wrongArtifact ? Guid.NewGuid() : f.Artifact.Id;
        var revisionId = wrongArtifact ? f.Revision.Id : Guid.NewGuid();
        var set = new CodeEvidenceDispositionSet(f.Project.Id, f.Release.Id, artifactId, revisionId,
            CodeEvidenceDisposition.NoCodeChangeRequired, "Retained historical decision.", null, null, null, "tester", f.Now);
        f.Db.AddRange(set, new CodeEvidenceCurrentSelector(f.Project.Id, f.Release.Id, artifactId, revisionId, set.Id, "tester", f.Now));
        await f.Db.SaveChangesAsync();
        var current = Assert.Single((await CurrentCodeEvidenceProjection.ForReleaseAsync(f.Db, f.Project.Id, f.Release.Id, default))
            .Where(x => x.RequirementRevisionId == revisionId));
        Assert.Equal(CurrentCodeEvidenceState.InvalidIdentity, current.State);
        Assert.False(current.CountsAsImplementation);
        Assert.Equal(set.Id, current.EvidenceSet!.Id);
        Assert.Null(current.LegacyRecord);
        Assert.Equal(1, await f.Db.CodeEvidenceDispositionSets.CountAsync());
    }

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
    public async Task Acceptance_service_records_no_code_without_gitlab_and_supersedes_only_expected_legacy_identity()
    {
        await using var f = await Fixture.CreateAsync(changedInBuild: true);
        var campaign = await f.CampaignAsync();
        await f.Db.CandidateBaselines.Where(x => x.Id == campaign.BaselineId).ExecuteUpdateAsync(update => update
            .SetProperty(x => x.State, CandidateBaselineState.Frozen)
            .SetProperty(x => x.RequirementsMaterializedAt, f.Now));

        await using var scope = await ProjectControlledWriteScope.AcquireAsync(f.Db, f.Project.Id);
        var command = new CodeEvidenceAcceptanceCommand(f.Project.Id, f.Release.Id, f.Artifact.Id, f.Revision.Id,
            CodeEvidenceDisposition.NoCodeChangeRequired, 0, f.Legacy.Id, null, null, null, null, [],
            "The changed requirement has no implementation code impact.");
        var result = await new CodeEvidenceAcceptanceService(f.Db).AcceptAsync(scope, command,
            new Dictionary<Guid, CodeEvidenceMergeObservation>(),
            LegacyLadderPolicy.Instance, "tester", f.Now, default);
        await f.Db.SaveChangesAsync();
        await scope.CommitAsync();

        var set = await f.Db.CodeEvidenceDispositionSets.SingleAsync(x => x.Id == result.EvidenceSetId);
        var selector = await f.Db.CodeEvidenceCurrentSelectors.SingleAsync();
        Assert.Equal(CodeEvidenceDisposition.NoCodeChangeRequired, set.Disposition);
        Assert.Equal(f.Legacy.Id, set.SupersededLegacyRecordId);
        Assert.Null(set.SourceSnapshotId);
        Assert.Null(set.SourceSelectionEventId);
        Assert.Equal(result.EvidenceSetId, selector.EvidenceSetId);
        Assert.Equal(1, selector.Version);
        Assert.Empty(await f.Db.CodeEvidenceContributions.ToListAsync());
    }

    [Fact]
    public async Task Acceptance_service_refuses_a_gitlab_contribution_without_provider_observation()
    {
        await using var f = await Fixture.CreateAsync(changedInBuild: true);
        var campaign = await f.CampaignAsync();
        await f.Db.CandidateBaselines.Where(x => x.Id == campaign.BaselineId).ExecuteUpdateAsync(update => update
            .SetProperty(x => x.State, CandidateBaselineState.Frozen)
            .SetProperty(x => x.RequirementsMaterializedAt, f.Now));
        f.Repository.RecordVerification("tester", f.Now, 17, "group/project");
        var snapshot = f.Snapshot('a');
        var selection = new GitLabSourceSelectionEvent(f.Project.Id, f.Release.Id, snapshot.Id, 0, "tester", f.Now);
        var current = new GitLabCurrentSourceSelection(f.Project.Id, f.Release.Id, snapshot.Id, selection.Id, "tester", f.Now);
        var target = CodeRelationshipTarget.ForRequirementRevision(f.Revision.Id, f.Artifact.Id, f.Revision.Revision, "LLR-000001.01");
        var relationship = new GitLabMergeRequestRelationship(f.Project.Id, f.Release.Id, snapshot.InstanceBaseUrl,
            snapshot.RemoteProjectId, 12, 1200, snapshot.Id, selection.Id, snapshot.PathWithNamespace,
            "https://gitlab.example/group/project/-/merge_requests/12", "Observed later", target,
            CodeRelationshipMeaning.Implements, "tester", f.Now);
        f.Db.AddRange(snapshot, selection, current, relationship);
        await f.Db.SaveChangesAsync();

        var command = new CodeEvidenceAcceptanceCommand(f.Project.Id, f.Release.Id, f.Artifact.Id, f.Revision.Id,
            CodeEvidenceDisposition.GitLabContributions, 0, f.Legacy.Id, f.Repository.Version,
            selection.Id, snapshot.Id, selection.ResultingVersion,
            [new(CodeEvidenceContributionKind.MergeRequest, relationship.Id, relationship.Version)], null);
        await using (var scope = await ProjectControlledWriteScope.AcquireAsync(f.Db, f.Project.Id))
        {
            await Assert.ThrowsAsync<DomainException>(() => new CodeEvidenceAcceptanceService(f.Db).AcceptAsync(scope,
                command, new Dictionary<Guid, CodeEvidenceMergeObservation>(), LegacyLadderPolicy.Instance,
                "tester", f.Now, default));
        }
        Assert.Empty(await f.Db.CodeEvidenceDispositionSets.ToListAsync());
        Assert.Empty(await f.Db.CodeEvidenceContributions.ToListAsync());
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
        var set = f.AcceptFile(source, selection, CodeRelationshipTarget.ForRequirementProposal(Guid.NewGuid(), Guid.NewGuid(), "Proposed requirement"));
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
        public ProjectRepositoryConfiguration Repository { get; private set; } = null!;
        public static async Task<Fixture> CreateAsync(bool changedInBuild = false)
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
            if (changedInBuild)
            {
                var change = new SystemChangeRequest("LLRCR-00001", 0, f.Project.Id, f.Release.Id,
                    "Changed behavior", "Problem", "Analysis", "Solution", "tester", f.Now,
                    ChangeRequestType.Software, softwareLevel: RequirementLevel.LowLevel);
                f.Db.Add(change);
                f.Revision = new RequirementRevision(f.Artifact.Id, 1, "Changed synthetic requirement.", "Controlled change",
                    "Test", RequirementRevisionState.Active, change.Id, baseline.Id, f.Now,
                    RequirementParentKind.Allocated, parentRevisionIds: [highRevision.Id]);
            }
            f.Legacy = new(f.Project.Id, f.Release.Id, f.Artifact.Id, f.Revision.Id, CodeTraceDisposition.NoCodeChangeRequired,
                "", "", "", "", "", null, "Existing retained disposition.", false, "tester", f.Now);
            f.Repository = new(f.Project.Id, ProjectRepositorySetupMode.ConnectNow, "GitLab", "https://gitlab.example/group/project", "tester", f.Now);
            f.Db.AddRange(program, f.Project, predecessor, f.Release, sourceBaseline, baseline, f.Artifact, f.Revision, f.Legacy, f.Repository);
            f.Db.AddRange(system, systemRevision, high, highRevision,
                new BaselineRequirementSelection(baseline.Id, system.Id, systemRevision.Id),
                new BaselineRequirementSelection(baseline.Id, high.Id, highRevision.Id),
                new BaselineRequirementSelection(baseline.Id, f.Artifact.Id, f.Revision.Id),
                new RequirementTraceLink(f.Project.Id, highRevision.Id, systemRevision.Id, RequirementTraceType.AllocatedFrom, "Exact parent", f.Now),
                new RequirementTraceLink(f.Project.Id, f.Revision.Id, highRevision.Id, RequirementTraceType.AllocatedFrom, "Exact parent", f.Now));
            await f.Db.SaveChangesAsync();
            return f;
        }
        public GitLabSourceSnapshot Snapshot(char sha) => new(Project.Id, Repository.Id, "https://gitlab.example", 17,
            "group/project", new string(sha, 40), "main", "tester", Now, Repository.Version);
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
        public async Task<ReleaseCampaign> CampaignAsync()
        {
            var baseline = await Db.CandidateBaselines.SingleAsync(x => x.ReleaseId == Release.Id);
            var build = new SoftwareBuild(Project.Id, Release.Id, baseline.Id, "SW-01.00", "Exact build", "tester", Now);
            var campaign = new ReleaseCampaign(Project.Id, Release.Id, baseline.Id, "Code review", "tester", Now);
            campaign.SelectVerificationBuild(build.Id, "tester", Now);
            Db.AddRange(build, campaign);
            await Db.SaveChangesAsync();
            return campaign;
        }
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await connection.DisposeAsync(); }
    }
}
