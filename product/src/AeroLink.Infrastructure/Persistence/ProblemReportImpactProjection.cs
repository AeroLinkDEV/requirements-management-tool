using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Documents;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>One artifact standing as evidence under an impact answer.</summary>
public sealed record ProblemReportImpactArtifact(
    string ArtifactType,
    Guid ArtifactId,
    string Identifier,
    string Title,
    string State,
    string TargetBuild,
    string Relationship,
    string Detail);

/// <summary>One row of the impact assessment, with whatever evidence has arrived under it.</summary>
public sealed record ProblemReportImpactArea(
    string Key,
    string Label,
    string Assessment,
    bool HasArtifactSlot,
    IReadOnlyList<string> ArtifactTypes,
    string? Mismatch,
    IReadOnlyList<ProblemReportImpactArtifact> Artifacts);

/// <summary>
/// The engineering evidence that has arrived under each impact answer.
///
/// This is a projection and never a stored list. Nothing here is typed by hand and nothing is copied onto
/// the Problem Report: every row is derived at read time from the ProblemReportLink rows the change
/// request, test change request and verification workflows already write, joined to each artifact's live
/// state. That is precisely why a linked SRCR moving from Draft to In review to Approved changes what the
/// report says without anybody touching the report — and why a stored copy would be wrong the moment it
/// was written.
///
/// Everything is loaded in a fixed set of batched queries rather than one per artifact. The existing link
/// projection resolves identifiers in a loop, and a report with twenty links pays for twenty round trips;
/// this panel would have made that far worse.
/// </summary>
public sealed class ProblemReportImpactProjection(AeroLinkDbContext db)
{
    /// <summary>
    /// The eight assessed areas, their labels, and what may stand as evidence under each.
    ///
    /// System/aircraft and Airworthiness carry no artifact types on purpose. There is no controlled record
    /// that means "the aircraft is affected" — the narrative above the matrix is that record — and giving
    /// them an empty evidence slot would imply something is missing rather than that nothing belongs there.
    /// </summary>
    private static readonly (string Key, string Label, string[] Types)[] Areas =
    [
        ("SystemRequirements", "System requirements", ["SRCR", "SYSR"]),
        ("Hlr", "High-level requirements", ["HLRCR", "HLR"]),
        ("Llr", "Low-level requirements", ["LLRCR", "LLR"]),
        ("Code", "Code", ["GitLab"]),
        ("Tests", "Tests", ["TCR", "Execution"]),
        ("Documents", "Documents", ["Document"]),
        ("SystemAircraft", "System / aircraft", []),
        ("Airworthiness", "Airworthiness", []),
    ];

    public async Task<IReadOnlyList<ProblemReportImpactArea>> BuildAsync(
        ProblemReport report, IReadOnlyList<ProblemReportLink> links, CancellationToken ct)
    {
        var assessments = ReadAssessments(report.ImpactAssessmentJson);
        var buckets = Areas.ToDictionary(area => area.Key, _ => new List<ProblemReportImpactArtifact>());

        var changeRequestIds = links
            .Where(link => link.ArtifactType == "ChangeRequest")
            .Select(link => link.ArtifactId).Distinct().ToList();
        var testChangeRequestIds = links
            .Where(link => link.ArtifactType == "TestChangeRequest")
            .Select(link => link.ArtifactId).Distinct().ToList();
        var executionIds = links
            .Where(link => link.ArtifactType == "TestExecution")
            .Select(link => link.ArtifactId).Distinct().ToList();

        var changeRequests = changeRequestIds.Count == 0
            ? []
            : await db.SystemChangeRequests.AsNoTracking().Include(item => item.RequirementChanges)
                .Where(item => changeRequestIds.Contains(item.Id)).ToListAsync(ct);

        // One lookup for every build any of this evidence targets, rather than one per artifact.
        var releaseIds = changeRequests.Select(item => item.TargetReleaseId).Distinct().ToList();
        var releases = releaseIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.Releases.AsNoTracking().Where(item => releaseIds.Contains(item.Id))
                .ToDictionaryAsync(item => item.Id, item => item.Version, ct);

        // A requirement change names its requirement by base number, not by id, so the artifacts are
        // resolved once for every number this report reaches rather than one lookup per change.
        var changedNumbers = changeRequests.SelectMany(item => item.RequirementChanges)
            .Select(change => change.BaseNumber)
            .Where(number => !string.IsNullOrWhiteSpace(number)).Distinct().ToList();
        var requirementArtifacts = changedNumbers.Count == 0
            ? new Dictionary<string, Guid>()
            : await db.Requirements.AsNoTracking()
                .Where(item => item.ProjectId == report.ProjectId && changedNumbers.Contains(item.BaseNumber))
                .ToDictionaryAsync(item => item.BaseNumber, item => item.Id, ct);

        var relationships = links
            .GroupBy(link => link.ArtifactId)
            .ToDictionary(group => group.Key, group => group.Select(link => link.Relationship).Distinct().ToList());

        foreach (var request in changeRequests.OrderBy(item => item.BaseNumber))
        {
            var key = AreaFor(request);
            if (key is null || !buckets.TryGetValue(key, out var bucket)) continue;
            var changes = request.RequirementChanges.Count;
            bucket.Add(new ProblemReportImpactArtifact(
                "ChangeRequest", request.Id, request.DisplayNumber, request.Title,
                Spaced(request.State.ToString()), Build(releases, request.TargetReleaseId),
                Relationship(relationships, request.Id),
                changes == 1 ? "1 requirement change" : $"{changes} requirement changes"));

            // The requirements the change actually touches, so the reader does not have to open the change
            // request to find out which ones this report reaches.
            foreach (var change in request.RequirementChanges.OrderBy(item => item.BaseNumber))
            {
                var requirementKey = AreaFor(change.Level);
                if (requirementKey is null || !buckets.TryGetValue(requirementKey, out var target)) continue;
                if (string.IsNullOrWhiteSpace(change.DisplayNumber)) continue;
                target.Add(new ProblemReportImpactArtifact(
                    "Requirement",
                    requirementArtifacts.TryGetValue(change.BaseNumber, out var artifactId) ? artifactId : change.Id,
                    change.DisplayNumber,
                    Truncate(change.Statement), change.Kind.ToString(), "",
                    "ChangedRequirement", $"Changed by {request.DisplayNumber}"));
            }
        }

        if (testChangeRequestIds.Count > 0)
            foreach (var review in await db.TestChangeReviews.AsNoTracking()
                         .Where(item => testChangeRequestIds.Contains(item.Id)).ToListAsync(ct))
                buckets["Tests"].Add(new ProblemReportImpactArtifact(
                    "TestChangeRequest", review.Id, review.DisplayNumber, review.Title,
                    Spaced(review.State.ToString()), "", Relationship(relationships, review.Id),
                    $"{review.Discipline} verification"));

        if (executionIds.Count > 0)
            foreach (var execution in await db.TestExecutions.AsNoTracking()
                         .Where(item => executionIds.Contains(item.Id)).ToListAsync(ct))
                buckets["Tests"].Add(new ProblemReportImpactArtifact(
                    "TestExecution", execution.Id, $"Execution {execution.Id.ToString()[..8]}",
                    execution.Determination, execution.Outcome.ToString(), "",
                    Relationship(relationships, execution.Id),
                    execution.Id == report.ResolutionVerificationExecutionId
                        ? "Selected as closure-supporting evidence"
                        : "Recorded against this report"));

        // Code reaches the report through the requirements its change requests touch. GitLab remains
        // authoritative for its own records; this is the controlled thread to them, and is read-only.
        var requirementArtifactIds = requirementArtifacts.Values.Distinct().ToList();
        if (requirementArtifactIds.Count > 0)
            foreach (var record in await db.CodeTraceabilityRecords.AsNoTracking()
                         .Where(item => requirementArtifactIds.Contains(item.RequirementArtifactId))
                         .ToListAsync(ct))
                buckets["Code"].Add(new ProblemReportImpactArtifact(
                    "CodeTraceability", record.Id,
                    string.IsNullOrWhiteSpace(record.MergeRequestReference) ? record.RepositoryPath : record.MergeRequestReference,
                    string.IsNullOrWhiteSpace(record.MergeRequestTitle) ? record.RepositoryPath : record.MergeRequestTitle,
                    record.MergedAt is null ? "Open" : "Merged", "", "CodeChange",
                    record.MergeCommitSha.Length >= 12 ? record.MergeCommitSha[..12] : record.MergeCommitSha));

        // A document reaches the report either directly or through one of its change requests.
        var documentTargets = changeRequestIds.Append(report.Id).ToList();
        foreach (var link in await db.ManagedDocumentLinks.AsNoTracking()
                     .Where(item => documentTargets.Contains(item.ArtifactId)).ToListAsync(ct))
            buckets["Documents"].Add(new ProblemReportImpactArtifact(
                "Document", link.RevisionId, link.DisplayNumber, link.CanonicalTitle,
                link.TargetState, link.TargetReleaseVersion, link.Relationship, "Controlled document"));

        return Areas.Select(area =>
        {
            var artifacts = buckets[area.Key]
                .GroupBy(artifact => (artifact.ArtifactType, artifact.ArtifactId))
                .Select(group => group.First()).ToList();
            var assessment = assessments.TryGetValue(area.Key, out var value) ? value : "Unknown";
            return new ProblemReportImpactArea(area.Key, area.Label, assessment,
                area.Types.Length > 0, area.Types, Mismatch(area.Label, assessment, artifacts.Count), artifacts);
        }).ToList();
    }

    /// <summary>
    /// Says so when the answer and the evidence disagree, rather than hiding either.
    ///
    /// Suppressing a link because the answer says "No" would make the record assert something untrue, and
    /// changing the answer because a link exists would put words in an engineer's mouth. Both are shown and
    /// the disagreement is named. It is advisory: it does not block a transition, because the reader is the
    /// one who knows which half is wrong.
    /// </summary>
    private static string? Mismatch(string label, string assessment, int artifacts) =>
        artifacts > 0 && assessment is "No" or "Unknown"
            ? $"{label} is recorded as {(assessment == "No" ? "not impacted" : "not yet assessed")}, but "
              + $"{(artifacts == 1 ? "one controlled artifact is" : $"{artifacts} controlled artifacts are")} "
              + "linked here. Re-assess the answer, or explain the link."
            : null;

    private static string? AreaFor(SystemChangeRequest request) => request.Type switch
    {
        ChangeRequestType.System => "SystemRequirements",
        ChangeRequestType.Software => AreaFor(request.SoftwareLevel),
        _ => null,
    };

    private static string? AreaFor(RequirementLevel? level) => level switch
    {
        RequirementLevel.System => "SystemRequirements",
        RequirementLevel.HighLevel => "Hlr",
        RequirementLevel.LowLevel => "Llr",
        _ => null,
    };

    private static string Relationship(IReadOnlyDictionary<Guid, List<string>> relationships, Guid id) =>
        relationships.TryGetValue(id, out var values) && values.Count > 0
            // Approved outranks proposed: it is the stronger claim, and showing both would read as two
            // separate links to the same record.
            ? values.OrderByDescending(value => value == ProblemReportRelationshipPolicy.ApprovedCorrectiveAction).First()
            : "";

    private static string Build(IReadOnlyDictionary<Guid, string> releases, Guid releaseId) =>
        releases.TryGetValue(releaseId, out var version) ? version : "";

    private static string Truncate(string value) =>
        value.Length <= 160 ? value : value[..160] + "…";

    private static string Spaced(string value) =>
        System.Text.RegularExpressions.Regex.Replace(value, "([a-z])([A-Z])", "$1 $2");

    private static Dictionary<string, string> ReadAssessments(string? json)
    {
        var result = new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(json)) return result;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return result;
            foreach (var property in document.RootElement.EnumerateObject())
                // "Safety" is what Airworthiness was called before it was named for what is actually being
                // judged; records written under the old key keep their answer.
                result[property.Name == "Safety" ? "Airworthiness" : property.Name] = property.Value.GetString() ?? "Unknown";
        }
        catch (System.Text.Json.JsonException) { /* An unreadable assessment reads as unanswered. */ }
        return result;
    }
}
