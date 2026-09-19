using AeroLink.Domain.Common;
using AeroLink.Domain.Integrations;

namespace AeroLink.Domain.Tests;

public sealed class CodeMergeResultEvidenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(GitLabMergeResultKind.MergeCommit)]
    [InlineData(GitLabMergeResultKind.SquashCommit)]
    public void Merged_result_is_retained_separately_from_the_selected_source(GitLabMergeResultKind kind)
    {
        var result = Contribution(CodeEvidenceContributionKind.MergeRequest, new string('B', 40), kind, Now.AddHours(-1), Now);
        Assert.Equal(new string('a', 40), result.CommitSha);
        Assert.Equal(new string('b', 40), result.MergeResultSha);
        Assert.Equal(kind, result.MergeResultKind);
        Assert.Equal(Now.AddHours(-1), result.MergedAt);
        Assert.Equal(Now, result.ProviderObservedAt);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Partial_merge_observation_cannot_be_accepted(int missing)
    {
        Assert.Throws<DomainException>(() => Contribution(CodeEvidenceContributionKind.MergeRequest,
            missing == 0 ? null : new string('b', 40), missing == 1 ? null : GitLabMergeResultKind.MergeCommit,
            missing == 2 ? null : Now, missing == 3 ? null : Now));
    }

    [Theory]
    [InlineData(41)]
    [InlineData(63)]
    public void Partial_merge_SHA_is_not_an_exact_accepted_identity(int length) =>
        Assert.Throws<DomainException>(() => Contribution(CodeEvidenceContributionKind.MergeRequest,
            new string('b', length), GitLabMergeResultKind.MergeCommit, Now, Now));

    [Fact]
    public void File_contributions_do_not_assert_that_an_MR_was_merged()
    {
        var file = Contribution(CodeEvidenceContributionKind.File, null, null, null, null);
        Assert.Null(file.MergeResultSha);
        Assert.Null(file.MergedAt);
        Assert.Throws<DomainException>(() => Contribution(CodeEvidenceContributionKind.File,
            new string('b', 40), GitLabMergeResultKind.MergeCommit, Now, Now));
    }

    private static CodeEvidenceContribution Contribution(CodeEvidenceContributionKind contributionKind,
        string? resultSha, GitLabMergeResultKind? kind, DateTimeOffset? mergedAt, DateTimeOffset? observedAt)
    {
        var artifact = Guid.NewGuid(); var revision = Guid.NewGuid();
        return new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), artifact, revision, Guid.NewGuid(),
            contributionKind, null, "https://gitlab.example", 17, "group/project",
            contributionKind == CodeEvidenceContributionKind.MergeRequest ? 3 : null, null,
            contributionKind == CodeEvidenceContributionKind.MergeRequest ? "https://gitlab.example/group/project/-/merge_requests/3" : null,
            null, new string('a', 40), contributionKind == CodeEvidenceContributionKind.File ? "src/demo.c" : null,
            null, null, CodeRelationshipTarget.ForRequirementRevision(revision, artifact, 1, "LLR-000001.01"),
            "tester", Now, resultSha, kind, mergedAt, observedAt);
    }
}
