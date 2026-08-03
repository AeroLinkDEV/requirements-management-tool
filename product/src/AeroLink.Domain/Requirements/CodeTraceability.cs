using System.Text.RegularExpressions;
using AeroLink.Domain.Common;

namespace AeroLink.Domain.Requirements;

public enum CodeTraceDisposition { GitLabMerge, NoCodeChangeRequired }

/// <summary>
/// AeroLink's immutable pointer from one exact approved LLR revision to the code authority in GitLab.
/// Source, review discussion, and commit content remain in GitLab; AeroLink stores only the release evidence link.
/// </summary>
public sealed class CodeTraceabilityRecord
{
    private CodeTraceabilityRecord() { }

    public CodeTraceabilityRecord(Guid projectId, Guid releaseId, Guid requirementArtifactId, Guid requirementRevisionId,
        CodeTraceDisposition disposition, string repositoryPath, string mergeRequestReference, string mergeRequestTitle,
        string mergeRequestUrl, string mergeCommitSha, DateTimeOffset? mergedAt, string noCodeChangeRationale,
        bool isDemonstration, string recordedBy, DateTimeOffset recordedAt)
    {
        if (projectId == Guid.Empty || releaseId == Guid.Empty || requirementArtifactId == Guid.Empty || requirementRevisionId == Guid.Empty)
            throw new DomainException("Project, build, and exact LLR revision are required for code traceability.");
        Id = Guid.NewGuid(); ProjectId = projectId; ReleaseId = releaseId; RequirementArtifactId = requirementArtifactId;
        RequirementRevisionId = requirementRevisionId; Disposition = disposition; IsDemonstration = isDemonstration;
        RecordedBy = Required(recordedBy, "The person recording code traceability is required."); RecordedAt = recordedAt;

        if (disposition == CodeTraceDisposition.NoCodeChangeRequired)
        {
            NoCodeChangeRationale = Required(noCodeChangeRationale, "A rationale is required when no code change is needed.");
            return;
        }

        RepositoryPath = Required(repositoryPath, "The GitLab repository path is required.");
        MergeRequestReference = Required(mergeRequestReference, "The GitLab merge request reference is required.");
        MergeRequestTitle = Required(mergeRequestTitle, "The GitLab merge request title is required.");
        MergeRequestUrl = Required(mergeRequestUrl, "The GitLab merge request URL is required.");
        if (!Uri.TryCreate(MergeRequestUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !uri.Host.Contains("gitlab", StringComparison.OrdinalIgnoreCase))
            throw new DomainException("Code traceability must point to an HTTPS GitLab merge request.");
        MergeCommitSha = Required(mergeCommitSha, "The immutable GitLab merge commit SHA is required.").ToLowerInvariant();
        if (!Regex.IsMatch(MergeCommitSha, "^[0-9a-f]{40,64}$")) throw new DomainException("The merge commit SHA must be 40 to 64 hexadecimal characters.");
        MergedAt = mergedAt ?? throw new DomainException("The GitLab merge timestamp is required.");
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public Guid ReleaseId { get; private set; }
    public Guid RequirementArtifactId { get; private set; }
    public Guid RequirementRevisionId { get; private set; }
    public CodeTraceDisposition Disposition { get; private set; }
    public string RepositoryPath { get; private set; } = "";
    public string MergeRequestReference { get; private set; } = "";
    public string MergeRequestTitle { get; private set; } = "";
    public string MergeRequestUrl { get; private set; } = "";
    public string MergeCommitSha { get; private set; } = "";
    public DateTimeOffset? MergedAt { get; private set; }
    public string NoCodeChangeRationale { get; private set; } = "";
    public bool IsDemonstration { get; private set; }
    public string RecordedBy { get; private set; } = "";
    public DateTimeOffset RecordedAt { get; private set; }

    private static string Required(string? value, string error) => string.IsNullOrWhiteSpace(value) ? throw new DomainException(error) : value.Trim();
}
