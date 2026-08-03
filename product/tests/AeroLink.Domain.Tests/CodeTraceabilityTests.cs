using AeroLink.Domain.Common;
using AeroLink.Domain.Requirements;

namespace AeroLink.Domain.Tests;

public sealed class CodeTraceabilityTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 2, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    public void GitLab_merge_records_the_exact_revision_and_immutable_commit()
    {
        var revisionId = Guid.NewGuid();
        var record = new CodeTraceabilityRecord(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), revisionId,
            CodeTraceDisposition.GitLabMerge, "flight-controls/fms", "!1842", "Implement LLR alert logic",
            "https://gitlab.example.test/flight-controls/fms/-/merge_requests/1842",
            new string('a', 40), Now, "", false, "software.engineer", Now);

        Assert.Equal(revisionId, record.RequirementRevisionId);
        Assert.Equal("!1842", record.MergeRequestReference);
        Assert.Equal(new string('a', 40), record.MergeCommitSha);
        Assert.Equal(Now, record.MergedAt);
    }

    [Fact]
    public void No_code_change_requires_an_attributable_engineering_rationale()
    {
        Assert.Throws<DomainException>(() => new CodeTraceabilityRecord(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            CodeTraceDisposition.NoCodeChangeRequired, "", "", "", "", "", null, "", false, "software.engineer", Now));

        var record = new CodeTraceabilityRecord(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            CodeTraceDisposition.NoCodeChangeRequired, "", "", "", "", "", null,
            "The approved wording clarifies an existing implementation and changes no executable behavior.",
            false, "software.engineer", Now);
        Assert.Equal(CodeTraceDisposition.NoCodeChangeRequired, record.Disposition);
        Assert.NotEmpty(record.NoCodeChangeRationale);
    }

    [Theory]
    [InlineData("http://gitlab.example.test/group/project/-/merge_requests/1", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("https://example.test/group/project/-/merge_requests/1", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("https://gitlab.example.test/group/project/-/merge_requests/1", "not-a-commit")]
    public void Merge_evidence_rejects_non_GitLab_or_mutable_references(string url, string sha)
    {
        Assert.Throws<DomainException>(() => new CodeTraceabilityRecord(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            CodeTraceDisposition.GitLabMerge, "group/project", "!1", "Change", url, sha, Now, "", false,
            "software.engineer", Now));
    }
}
