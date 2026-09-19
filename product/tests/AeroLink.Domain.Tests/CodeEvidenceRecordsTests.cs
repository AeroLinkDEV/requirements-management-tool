using AeroLink.Domain.Common;
using AeroLink.Domain.Integrations;

namespace AeroLink.Domain.Tests;

public sealed class CodeEvidenceRecordsTests
{
    private static readonly Guid ProjectId = Guid.NewGuid();
    private static readonly Guid ReleaseId = Guid.NewGuid();
    private static readonly Guid RequirementRevisionId = Guid.NewGuid();
    private static readonly Guid RequirementArtifactId = Guid.NewGuid();
    private static readonly Guid SnapshotId = Guid.NewGuid();
    private static readonly string Sha = new('a', 40);
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Source_selection_uses_expected_version_and_A_to_B_to_A_creates_new_binding()
    {
        var first = new GitLabSourceSnapshot(ProjectId, Guid.NewGuid(), "https://gitlab.example.com", 10,
            "aerolink/demo", Sha, "main", "alice", Now, 2);
        var second = new GitLabSourceSnapshot(ProjectId, Guid.NewGuid(), "https://gitlab.example.com", 11,
            "aerolink/demo", new('b', 40), "release", "alice", Now.AddMinutes(1), 3);
        var a = new GitLabSourceSelectionEvent(ProjectId, ReleaseId, first.Id, 0, "alice", Now);
        var current = new GitLabCurrentSourceSelection(ProjectId, ReleaseId, first.Id, a.Id, "alice", Now);
        var b = new GitLabSourceSelectionEvent(ProjectId, ReleaseId, second.Id, current.Version, "bob", Now.AddMinutes(1));

        current.Move(current.Version, second.Id, b.Id, "bob", Now.AddMinutes(1));
        var aAgain = new GitLabSourceSelectionEvent(ProjectId, ReleaseId, first.Id, current.Version, "alice", Now.AddMinutes(2));
        current.Move(current.Version, first.Id, aAgain.Id, "alice", Now.AddMinutes(2));

        Assert.Equal(first.Id, current.SourceSnapshotId);
        Assert.NotEqual(a.Id, aAgain.Id);
        Assert.Equal(3, current.Version);
        Assert.Throws<DomainException>(() => current.Move(1, second.Id, b.Id, "bob", Now));
    }

    [Fact]
    public void Relationship_active_edge_key_is_instance_release_and_target_specific()
    {
        var source = new GitLabSourceSnapshot(ProjectId, Guid.NewGuid(), "https://gitlab.example.com", 10,
            "aerolink/demo", Sha, "main", "alice", Now, 1);
        var selection = new GitLabSourceSelectionEvent(ProjectId, ReleaseId, source.Id, 0, "alice", Now);
        var target = CodeRelationshipTarget.ForRequirementRevision(RequirementRevisionId, RequirementArtifactId, 4, "LLR-001.04");
        var relationship = new GitLabMergeRequestRelationship(ProjectId, ReleaseId, "https://gitlab.example.com", 10,
            17, 501, source.Id, selection.Id, "aerolink/demo", "https://gitlab.example.com/aerolink/demo/-/merge_requests/17",
            "Implement LLR-001", target, CodeRelationshipMeaning.Implements, "alice", Now);

        Assert.Contains("https://gitlab.example.com", relationship.ActiveEdgeKey, StringComparison.Ordinal);
        Assert.Contains(ReleaseId.ToString("N"), relationship.ActiveEdgeKey, StringComparison.Ordinal);
        Assert.Contains(RequirementRevisionId.ToString("N"), relationship.ActiveEdgeKey, StringComparison.Ordinal);
    }

    [Fact]
    public void Merge_request_association_can_precede_source_selection()
    {
        var target = CodeRelationshipTarget.ForRequirementRevision(RequirementRevisionId, RequirementArtifactId, 4, "LLR-001.04");
        var relationship = new GitLabMergeRequestRelationship(ProjectId, ReleaseId, "https://gitlab.example.com", 10,
            17, 501, null, null, "aerolink/demo", "https://gitlab.example.com/aerolink/demo/-/merge_requests/17",
            "Implement LLR-001", target, CodeRelationshipMeaning.Implements, "alice", Now);

        Assert.Null(relationship.SourceSnapshotId);
        Assert.Null(relationship.SourceSelectionEventId);
    }

    [Fact]
    public void File_edge_does_not_change_when_optional_merge_request_context_is_added()
    {
        var source = new GitLabSourceSnapshot(ProjectId, Guid.NewGuid(), "https://gitlab.example.com", 10,
            "aerolink/demo", Sha, "main", "alice", Now, 1);
        var selection = new GitLabSourceSelectionEvent(ProjectId, ReleaseId, source.Id, 0, "alice", Now);
        var target = CodeRelationshipTarget.ForRequirementRevision(RequirementRevisionId, RequirementArtifactId, 4, "LLR-001.04");
        var withoutContext = new GitLabFileRelationship(ProjectId, ReleaseId, "https://gitlab.example.com", 10,
            source.Id, selection.Id, Sha, "src/flight.cs", 10, 12, null, target, CodeRelationshipMeaning.Addresses, "alice", Now);
        var withContext = new GitLabFileRelationship(ProjectId, ReleaseId, "https://gitlab.example.com", 10,
            source.Id, selection.Id, Sha, "src/flight.cs", 10, 12, 17, target, CodeRelationshipMeaning.Addresses, "alice", Now);

        Assert.Equal(withoutContext.ActiveEdgeKey, withContext.ActiveEdgeKey);
    }

    [Fact]
    public void Withdrawal_requires_rationale_and_readd_restores_same_identity_key()
    {
        var source = new GitLabSourceSnapshot(ProjectId, Guid.NewGuid(), "https://gitlab.example.com", 10,
            "aerolink/demo", Sha, "main", "alice", Now, 1);
        var selection = new GitLabSourceSelectionEvent(ProjectId, ReleaseId, source.Id, 0, "alice", Now);
        var target = CodeRelationshipTarget.ForRequirementRevision(RequirementRevisionId, RequirementArtifactId, 4, "LLR-001.04");
        var relationship = new GitLabFileRelationship(ProjectId, ReleaseId, "https://gitlab.example.com", 10,
            source.Id, selection.Id, Sha, "src/flight.cs", null, null, null, target, CodeRelationshipMeaning.Implements, "alice", Now);
        var key = relationship.ActiveEdgeKey;

        Assert.Throws<DomainException>(() => relationship.Withdraw(relationship.Version, "alice", " ", Now.AddMinutes(1)));
        relationship.Withdraw(relationship.Version, "alice", "The source line was withdrawn.", Now.AddMinutes(1));
        Assert.False(relationship.IsActive);
        Assert.Null(relationship.ActiveEdgeKey);
        relationship.ReAdd(relationship.Version, "bob", Now.AddMinutes(2));

        Assert.True(relationship.IsActive);
        Assert.Equal(key, relationship.ActiveEdgeKey);
        Assert.Equal("bob", relationship.ReAddedBy);
        Assert.Equal("The source line was withdrawn.", relationship.WithdrawalRationale);
    }

    [Fact]
    public void Evidence_disposition_requires_exact_source_binding_and_never_falls_back()
    {
        var source = new GitLabSourceSnapshot(ProjectId, Guid.NewGuid(), "https://gitlab.example.com", 10,
            "aerolink/demo", Sha, "main", "alice", Now, 1);
        var selection = new GitLabSourceSelectionEvent(ProjectId, ReleaseId, source.Id, 0, "alice", Now);
        var otherSource = new GitLabSourceSnapshot(ProjectId, Guid.NewGuid(), "https://gitlab.example.com", 10,
            "aerolink/demo", new('b', 40), "release", "alice", Now.AddMinutes(1), 1);
        Assert.Throws<DomainException>(() => CodeEvidenceDispositionSet.CreateGitLab(ProjectId, ReleaseId,
            RequirementArtifactId, RequirementRevisionId, selection, otherSource, null, "alice", Now));

        var sourceEvent = selection.Id;
        var sourceSnapshot = source.Id;
        var set = new CodeEvidenceDispositionSet(ProjectId, ReleaseId, RequirementArtifactId, RequirementRevisionId,
            CodeEvidenceDisposition.GitLabContributions, null, sourceEvent, sourceSnapshot, Guid.NewGuid(), "alice", Now);
        var selector = new CodeEvidenceCurrentSelector(ProjectId, ReleaseId, RequirementArtifactId, RequirementRevisionId, set.Id, "alice", Now);
        var invalidation = new CodeEvidenceInvalidation(set.Id, ProjectId, ReleaseId, RequirementArtifactId, RequirementRevisionId, "bob", "The selected source was revoked.", Now.AddMinutes(1));

        Assert.Equal(set.Id, selector.EvidenceSetId);
        Assert.Equal(set.Id, invalidation.EvidenceSetId);
        Assert.Throws<DomainException>(() => new CodeEvidenceDispositionSet(ProjectId, ReleaseId, RequirementArtifactId,
            RequirementRevisionId, CodeEvidenceDisposition.GitLabContributions, null, null, sourceSnapshot, null, "alice", Now));
        selector.Move(selector.Version, Guid.NewGuid(), "bob", Now.AddMinutes(2));
        Assert.Throws<DomainException>(() => selector.Move(1, Guid.NewGuid(), "alice", Now));
    }

    [Fact]
    public void No_code_disposition_and_manifest_validate_explicit_content()
    {
        var noCode = new CodeEvidenceDispositionSet(ProjectId, ReleaseId, RequirementArtifactId, RequirementRevisionId,
            CodeEvidenceDisposition.NoCodeChangeRequired, "The approved change affects documentation only.", null, null, null, "alice", Now);
        Assert.Equal(CodeEvidenceDisposition.NoCodeChangeRequired, noCode.Disposition);
        Assert.Throws<DomainException>(() => new CodeEvidenceDispositionSet(ProjectId, ReleaseId, RequirementArtifactId,
            RequirementRevisionId, CodeEvidenceDisposition.NoCodeChangeRequired, " ", null, null, null, "alice", Now));

        var manifest = new CodeReviewCycleManifestIdentity(ProjectId, ReleaseId, Guid.NewGuid(), 1, "aerolink-code-v1", 1,
            new('c', 64), null, null, Array.Empty<Guid>(), "alice", Now);
        Assert.Equal(64, manifest.ManifestHash.Length);
        Assert.Throws<DomainException>(() => new CodeReviewCycleManifestIdentity(ProjectId, ReleaseId, Guid.NewGuid(), 1, "aerolink-code-v1", 1,
            "bad", null, null, Array.Empty<Guid>(), "alice", Now));
        Assert.Throws<DomainException>(() => new CodeReviewCycleManifestIdentity(ProjectId, ReleaseId, Guid.NewGuid(), 1, "aerolink-code-v1", 1,
            new('c', 64), null, null, new[] { Guid.NewGuid() }, "alice", Now));
        var v2NoCode = new CodeReviewCycleManifestIdentity(ProjectId, ReleaseId, Guid.NewGuid(), 1, "aerolink-code-v2", 2,
            new('c', 64), null, null, Array.Empty<Guid>(), "alice", Now);
        Assert.Equal(2, v2NoCode.FormatVersion);
    }

    [Fact]
    public void Exact_identity_and_ranges_are_validated()
    {
        var target = CodeRelationshipTarget.ForProblemReportRevision(Guid.NewGuid(), Guid.NewGuid(), 2, "PR-002.02");
        Assert.Equal(CodeRelationshipTargetKind.ProblemReportRevision, target.Kind);
        Assert.Throws<DomainException>(() => CodeRelationshipTarget.ForRequirementRevision(Guid.Empty, RequirementArtifactId, 1, "LLR"));
        Assert.Throws<DomainException>(() => CodeRelationshipTarget.ForRequirementRevision(RequirementRevisionId, RequirementArtifactId, -1, "LLR"));
        Assert.Throws<DomainException>(() => CodeRelationshipTarget.ForRequirementProposal(Guid.NewGuid(), null, " "));

        var source = new GitLabSourceSnapshot(ProjectId, Guid.NewGuid(), "https://gitlab.example.com", 10,
            "aerolink/demo", Sha, "main", "alice", Now, 1);
        var selection = new GitLabSourceSelectionEvent(ProjectId, ReleaseId, source.Id, 0, "alice", Now);
        Assert.Throws<DomainException>(() => new GitLabFileRelationship(ProjectId, ReleaseId, "https://gitlab.example.com", 10,
            source.Id, selection.Id, Sha, "src/flight.cs", 12, 10, null, target, CodeRelationshipMeaning.Addresses, "alice", Now));
        Assert.Throws<DomainException>(() => new GitLabSourceSnapshot(ProjectId, Guid.NewGuid(), "https://gitlab.example.com", 10,
            "aerolink/demo", new('a', 41), "main", "alice", Now, 1));
        Assert.Throws<DomainException>(() => new GitLabSourceSnapshot(ProjectId, Guid.NewGuid(), "https://gitlab.example.com", 10,
            "aerolink/demo", new('a', 63), "main", "alice", Now, 1));
    }
}
