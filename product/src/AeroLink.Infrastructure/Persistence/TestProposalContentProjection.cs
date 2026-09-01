using System.Text.Json;
using AeroLink.Domain.Verification;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>
/// The body of a verification artifact, as its own vocabulary rather than as a requirement statement.
///
/// A procedure is not a sentence. Flattening objective, preconditions, steps and expected result into one
/// "statement" field to reuse the requirement DTO would destroy the structure a reviewer reads it by, and the
/// Digital Thread would then be showing a verification artifact as though it were a requirement.
/// </summary>
public sealed record VerificationArtifactContent(
    string Title,
    string Objective,
    string Preconditions,
    string Steps,
    string ExpectedResult,
    string EnvironmentSetup = "",
    string TestData = "",
    string OrderedSteps = "",
    string ExpectedObservations = "",
    string Cleanup = "",
    string ToolingAutomation = "");

/// <summary>
/// One requirement revision a verification proposal covers, or stops covering.
///
/// <paramref name="IsProposedCoverage"/> separates coverage this package proposes from coverage already in the
/// build. A test change request is a proposal until it is approved and materialised, so drawing the two alike
/// would say the requirement is verified when nobody has agreed to verify it yet.
/// </summary>
public sealed record RequirementCoverageTarget(
    Guid RevisionId,
    Guid ArtifactId,
    string DisplayNumber,
    string Level,
    string Statement,
    bool IsProposedCoverage);

/// <summary>
/// An exact parent of a verification proposal, carrying what kind of thing the parent actually is.
///
/// Not folded into requirement coverage. For a software Procedure the exact parent is a <b>Case</b> revision,
/// not a requirement, and relabelling it to fit the "requirements covered" lane would tell the reader a
/// procedure verifies a requirement it has no recorded relationship with. Case/Procedure parentage and
/// requirement coverage are different relationships and stay apart.
/// </summary>
public sealed record VerificationParentTarget(Guid RevisionId, string Kind, string? DisplayNumber = null);

/// <summary>One proposed verification artifact change, with its exact predecessor where it has one.</summary>
public sealed record VerificationProposalItem(
    Guid Id,
    string DisplayNumber,
    string Level,
    string ArtifactKind,
    string Kind,
    VerificationArtifactContent? ProposedContent,
    int? SupersededRevision,
    Guid? BaseRevisionId,
    VerificationArtifactContent? SupersededContent,
    IReadOnlyList<RequirementCoverageTarget> ProposedCoverage,
    IReadOnlyList<RequirementCoverageTarget> RemovedCoverage,
    string ParentKind,
    IReadOnlyList<VerificationParentTarget> ExactParents);

/// <summary>
/// The proposed content of one controlled Test Change Request.
///
/// A sibling of <see cref="ChangeProposalContentResult"/> rather than a variant of it. Both answer "what does
/// this change propose", and both resolve their predecessor at the exact revision the proposal names — but a
/// verification package proposes cases and procedures, and what sits below it is the requirements it covers,
/// not a further ladder step. <paramref name="OwnerKind"/> lets the client hold the two as a discriminated
/// union instead of a single shape with fields that mean different things depending on the owner.
/// </summary>
public sealed record VerificationProposalContent(
    string OwnerKind,
    Guid OwnerId,
    Guid ProjectId,
    Guid ReleaseId,
    string DisplayNumber,
    string Discipline,
    string ArtifactKind,
    IReadOnlyList<VerificationProposalItem> Items);

/// <summary>
/// Reads what a Test Change Request proposes, at the revision each proposal was written against.
///
/// The requirement-side projection cannot serve this: a controlled Test Change Request is a
/// <see cref="TestChangeReview"/> carrying <see cref="TestProcedureChange"/> rows, not a
/// <c>SystemChangeRequest</c> carrying requirement changes, and <c>ChangeRequestType</c> has no Test member.
/// The two are read through their own resources so the path says which aggregate is being read, rather than
/// one endpoint inspecting an identifier to find out what it points at.
/// </summary>
public static class TestProposalContentProjection
{
    private sealed record PredecessorRevision(
        string BaseNumber, int Revision, Guid Id, string Title, VerificationArtifactContent Content);

    public static async Task<VerificationProposalContent?> ForTestChangeReviewAsync(
        AeroLinkDbContext db, Guid projectId, Guid testChangeReviewId, CancellationToken ct)
    {
        var review = await db.TestChangeReviews.AsNoTracking()
            .Include(x => x.ProcedureChanges)
            .SingleOrDefaultAsync(x => x.Id == testChangeReviewId && x.ProjectId == projectId, ct);
        if (review is null) return null;

        var changes = review.ProcedureChanges.OrderBy(x => x.BaseNumber, StringComparer.Ordinal)
            .ThenBy(x => x.Revision).ToList();

        // Exact predecessors for every Modify and Retire, resolved by the (base number, revision) the proposal
        // names. Deliberately not the latest revision and not whatever the current build happens to select: a
        // proposal written against revision 01 must be shown against revision 01, or the view reports a change
        // to text the author never touched.
        var baseNumbers = changes
            .Where(x => x.Kind != TestProcedureChangeKind.Introduce && !string.IsNullOrWhiteSpace(x.BaseNumber))
            .Select(x => x.BaseNumber).Distinct().ToList();

        var predecessors = new Dictionary<(string, int), PredecessorRevision>();
        if (baseNumbers.Count > 0)
        {
            var rows = await (from procedure in db.TestProcedures.AsNoTracking()
                              where procedure.ProjectId == projectId
                                  && baseNumbers.Contains(procedure.BaseNumber)
                              join revision in db.TestProcedureRevisions.AsNoTracking()
                                  on procedure.Id equals revision.ProcedureId
                              select new
                              {
                                  procedure.BaseNumber,
                                  procedure.Title,
                                  revision.Revision,
                                  revision.Id,
                                  revision.Objective,
                                  revision.Preconditions,
                                  revision.Steps,
                                  revision.ExpectedResult,
                                  revision.EnvironmentSetup,
                                  revision.TestData,
                                  revision.OrderedSteps,
                                  revision.ExpectedObservations,
                                  revision.Cleanup,
                                  revision.ToolingAutomation,
                              }).ToListAsync(ct);
            foreach (var row in rows)
            {
                predecessors[(row.BaseNumber, row.Revision)] = new PredecessorRevision(
                    row.BaseNumber, row.Revision, row.Id, row.Title,
                    new VerificationArtifactContent(row.Title, row.Objective, row.Preconditions, row.Steps,
                        row.ExpectedResult, row.EnvironmentSetup, row.TestData, row.OrderedSteps,
                        row.ExpectedObservations, row.Cleanup, row.ToolingAutomation));
            }
        }

        // Every requirement revision named as driving or removed coverage, resolved in one pass. These are exact
        // revision identities carried by the proposal; nothing is inferred from an identifier prefix.
        var coverageIds = changes
            .SelectMany(x => Ids(x.DrivingRequirementRevisionIdsJson).Concat(Ids(x.RemovedRequirementRevisionIdsJson)))
            .Distinct().ToList();

        var coverage = new Dictionary<Guid, RequirementCoverageTarget>();
        if (coverageIds.Count > 0)
        {
            var rows = await (from revision in db.RequirementRevisions.AsNoTracking()
                              where coverageIds.Contains(revision.Id)
                              join artifact in db.Requirements.AsNoTracking()
                                  on revision.ArtifactId equals artifact.Id
                              where artifact.ProjectId == projectId
                              select new
                              {
                                  revision.Id,
                                  revision.ArtifactId,
                                  artifact.BaseNumber,
                                  revision.Revision,
                                  Level = artifact.Level,
                                  revision.Statement,
                              }).ToListAsync(ct);
            foreach (var row in rows)
            {
                coverage[row.Id] = new RequirementCoverageTarget(row.Id, row.ArtifactId,
                    $"{row.BaseNumber}.{row.Revision:D2}", row.Level.ToString(), row.Statement,
                    // A test change request is a proposal until it is approved and materialised. Until then the
                    // coverage it names is proposed, whatever else is true of the requirement.
                    IsProposedCoverage: true);
            }
        }

        var items = new List<VerificationProposalItem>(changes.Count);
        foreach (var change in changes)
        {
            PredecessorRevision? predecessor = null;
            if (change.Kind != TestProcedureChangeKind.Introduce && !string.IsNullOrWhiteSpace(change.BaseNumber))
                predecessors.TryGetValue((change.BaseNumber, change.Revision), out predecessor);

            // A Retire proposes no successor body, so it carries no proposed content — null means absent, and an
            // empty body would read as a procedure emptied of its steps rather than one being withdrawn.
            var proposed = change.Kind == TestProcedureChangeKind.Retire
                ? null
                : new VerificationArtifactContent(change.Title, change.Objective, change.Preconditions,
                    change.Steps, change.ExpectedResult, change.EnvironmentSetup, change.TestData,
                    change.OrderedSteps, change.ExpectedObservations, change.Cleanup, change.ToolingAutomation);

            items.Add(new VerificationProposalItem(
                change.Id,
                string.IsNullOrWhiteSpace(change.BaseNumber) ? "" : $"{change.BaseNumber}.{change.Revision:D2}",
                change.Level.ToString(),
                review.ArtifactKind.ToString(),
                change.Kind.ToString(),
                proposed,
                predecessor?.Revision,
                predecessor?.Id,
                predecessor?.Content,
                Resolve(change.DrivingRequirementRevisionIdsJson, coverage),
                Resolve(change.RemovedRequirementRevisionIdsJson, coverage),
                change.ParentKind.ToString(),
                // Parent identities keep their own kind. For a software Procedure the exact parent is a Case
                // revision, and calling it requirement coverage would assert a relationship nobody recorded.
                Ids(change.ParentRevisionIdsJson)
                    .Select(id => new VerificationParentTarget(id,
                        review.ArtifactKind == VerificationArtifactKind.Procedure ? "Case" : "Requirement"))
                    .ToList()));
        }

        return new VerificationProposalContent(
            "TestChangeRequest",
            review.Id,
            review.ProjectId,
            review.ReleaseId,
            $"{review.BaseNumber}.{review.Revision:D2}",
            review.Discipline.ToString(),
            review.ArtifactKind.ToString(),
            items);
    }

    private static IReadOnlyList<RequirementCoverageTarget> Resolve(
        string json, IReadOnlyDictionary<Guid, RequirementCoverageTarget> known) =>
        Ids(json).Select(id => known.GetValueOrDefault(id)).OfType<RequirementCoverageTarget>()
            .OrderBy(x => x.DisplayNumber, StringComparer.Ordinal).ToList();

    private static IReadOnlyList<Guid> Ids(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<Guid>>(json) ?? []; }
        catch (JsonException) { return []; }
    }
}
