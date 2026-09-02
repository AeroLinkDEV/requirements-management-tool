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
/// One requirement revision named by a verification proposal.
///
/// Deliberately carries no "is this proposed" flag. The list a target sits in already states its meaning, and
/// a single boolean set once for every list said <c>isProposedCoverage: true</c> on rows that were being
/// *removed* — a removal describing itself as proposed coverage. Membership is the statement.
/// </summary>
public sealed record RequirementCoverageTarget(
    Guid RevisionId,
    Guid ArtifactId,
    string DisplayNumber,
    string Level,
    string Statement);

/// <summary>
/// An exact parent of a verification proposal, resolved and carrying what kind of thing it actually is.
///
/// The kind comes from <see cref="VerificationProcedureParentPolicy.ParentArtifactKind"/> rather than from the
/// package's own artifact kind. A System Procedure and a software Case both take requirement revisions; only a
/// software Procedure takes Case revisions. Reading "Procedure implies Case parent" reports a System Procedure
/// as hanging off a Case it has no relationship with.
///
/// <paramref name="Resolved"/> false means the record names this identity but nothing in this Project answers
/// to it. It is still returned, because a traceability surface that silently drops a named reference shows a
/// smaller relationship set than the record holds — and the Digital Thread reads Draft proposals, where an
/// incomplete reference is a legitimate state the reader needs to see rather than a validation failure.
///
/// <paramref name="Kind"/> is null when unresolved, and that is a deliberate distinction from the gap's
/// <c>ExpectedKind</c>. What the package expects to find and what the referenced object actually is are two
/// different claims; stating the expectation in a field that reads as identity would assert knowledge of an
/// object nobody could locate. Every detail stays null for the same reason — and so nothing from another
/// Project can leak through this seam.
/// </summary>
public sealed record VerificationParentTarget(
    Guid RevisionId,
    string? Kind,
    bool Resolved,
    string? DisplayNumber = null,
    string? Level = null,
    Guid? ArtifactId = null);

/// <summary>Why a recorded relationship could not be turned into a resolved target.</summary>
public enum ProposalReferenceGapReason
{
    /// <summary>The list parsed, but this identity resolves to nothing inside the authorized Project.</summary>
    UnresolvedReference,
    /// <summary>The stored list could not be interpreted at all, so no identity can be named.</summary>
    MalformedReferenceList,
}

/// <summary>
/// A recorded relationship the projection could not resolve.
///
/// <paramref name="RevisionId"/> is null for a malformed list, because there is no identity to name — the
/// bytes could not be read as one. <paramref name="ExpectedKind"/> is what this relationship expected to find,
/// which is not the same claim as what the object is; the resolved target carries that, and only when it
/// resolved.
/// </summary>
public sealed record ProposalReferenceGap(
    Guid? RevisionId,
    string Role,
    string ExpectedKind,
    ProposalReferenceGapReason Reason);

/// <summary>
/// One proposed verification artifact change, with its exact predecessor and its coverage.
///
/// Coverage is given as a final state and two deltas, because they answer different questions and the delta
/// alone is not the lane-2 story. A Modify that retains A and B, drops C and adds D leaves the successor
/// covering A, B and D; a lane fed only the added set would show D and quietly lose A and B, telling the
/// reader that retained coverage had disappeared.
/// </summary>
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
    /// <summary>What the proposed successor covers in full: retained + added − removed, as the proposal carries it.</summary>
    IReadOnlyList<RequirementCoverageTarget> FinalCoverage,
    /// <summary>Requirement revisions this proposal newly drives.</summary>
    IReadOnlyList<RequirementCoverageTarget> AddedCoverage,
    /// <summary>Requirement revisions this proposal deliberately stops covering.</summary>
    IReadOnlyList<RequirementCoverageTarget> RemovedCoverage,
    string ParentKind,
    IReadOnlyList<VerificationParentTarget> ExactParents,
    /// <summary>Recorded identities nothing in this Project answers to. Never silently dropped.</summary>
    IReadOnlyList<ProposalReferenceGap> ReferenceGaps);

/// <summary>
/// One recorded run of a procedure revision this package proposes to change, for lane 3.
///
/// A verification package's lane 3 is EXECUTIONS, not covering artifacts — the prototype is explicit, and it
/// follows from what the lanes mean: a requirement change asks "what verifies this?", while a test change
/// asks "what happened when this was run?". Serving covering artifacts here would answer the wrong question
/// in the right-shaped box.
///
/// Executions are read against the exact predecessor revision each proposal names, so a run of a different
/// revision of the same procedure is never presented as evidence for this one.
/// </summary>
public sealed record VerificationExecution(
    Guid Id,
    Guid ProcedureRevisionId,
    string Outcome,
    string ExecutedBy,
    DateTimeOffset ExecutedAt,
    string Determination);

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
    IReadOnlyList<VerificationProposalItem> Items,
    /// <summary>Lane 3: recorded runs of the exact predecessor revisions this package changes.</summary>
    IReadOnlyList<VerificationExecution> Executions,
    /// <summary>Lane 4: the candidate baseline this package's build carries, and the one it supersedes.</summary>
    IReadOnlyList<ProposalBaselineEffect> BuildEffect);

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

        // Every requirement revision this package names, in any role: the full exact-parent selection, the
        // added delta and the removed delta. Resolved in one pass, and scoped to this Project so a recorded
        // identity belonging elsewhere resolves to nothing here rather than leaking another Project's artifact.
        var requirementIds = changes
            .SelectMany(x => Ids(x.ParentRevisionIdsJson).Ids
                .Concat(Ids(x.DrivingRequirementRevisionIdsJson).Ids)
                .Concat(Ids(x.RemovedRequirementRevisionIdsJson).Ids))
            .Distinct().ToList();

        var requirements = new Dictionary<Guid, RequirementCoverageTarget>();
        if (requirementIds.Count > 0)
        {
            var rows = await (from revision in db.RequirementRevisions.AsNoTracking()
                              where requirementIds.Contains(revision.Id)
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
                requirements[row.Id] = new RequirementCoverageTarget(row.Id, row.ArtifactId,
                    Display(row.BaseNumber, row.Revision), row.Level.ToString(), row.Statement);
        }

        // Case revisions a software Procedure names as its exact parents, resolved to real controlled identity
        // rather than echoed back as a bare identifier with an assumed kind.
        var caseIds = changes.SelectMany(x => Ids(x.ParentRevisionIdsJson).Ids).Distinct().ToList();
        var cases = new Dictionary<Guid, VerificationParentTarget>();
        if (caseIds.Count > 0)
        {
            var rows = await (from revision in db.TestProcedureRevisions.AsNoTracking()
                              where caseIds.Contains(revision.Id)
                              join artifact in db.TestProcedures.AsNoTracking()
                                  on revision.ProcedureId equals artifact.Id
                              where artifact.ProjectId == projectId
                                  && artifact.ArtifactKind == VerificationArtifactKind.Case
                              select new
                              {
                                  revision.Id,
                                  ArtifactId = artifact.Id,
                                  artifact.BaseNumber,
                                  revision.Revision,
                                  Level = artifact.Level,
                              }).ToListAsync(ct);
            foreach (var row in rows)
                cases[row.Id] = new VerificationParentTarget(row.Id, "Case", Resolved: true,
                    Display(row.BaseNumber, row.Revision), row.Level.ToString(), row.ArtifactId);
        }

        // What kind of thing this package's exact parents are, from the domain policy rather than from the
        // package's own artifact kind. A System Procedure and a software Case both take requirement revisions;
        // only a software Procedure takes Case revisions.
        var discipline = VerificationArtifactProfile.ToNeutral(review.Discipline);
        var parentKind = VerificationProcedureParentPolicy.ParentArtifactKind(discipline, review.ArtifactKind);
        var parentKindWord = parentKind.ToString();

        var items = new List<VerificationProposalItem>(changes.Count);
        foreach (var change in changes)
        {
            PredecessorRevision? predecessor = null;
            if (change.Kind != TestProcedureChangeKind.Introduce && !string.IsNullOrWhiteSpace(change.BaseNumber))
                predecessors.TryGetValue((change.BaseNumber, change.Revision), out predecessor);

            // A Retire proposes no successor body, so it carries no proposed content — null means absent, and an
            // empty body would read as a procedure emptied of its steps rather than one being withdrawn. Its
            // predecessor is still resolved: what hangs below the thing being retired is the cascade the view
            // draws dashed. The predecessor body travels as factual context, not as half of a diff — a Retire
            // has no successor text to compare against.
            var proposed = change.Kind == TestProcedureChangeKind.Retire
                ? null
                : new VerificationArtifactContent(change.Title, change.Objective, change.Preconditions,
                    change.Steps, change.ExpectedResult, change.EnvironmentSetup, change.TestData,
                    change.OrderedSteps, change.ExpectedObservations, change.Cleanup, change.ToolingAutomation);

            var gaps = new List<ProposalReferenceGap>();
            var parents = new List<VerificationParentTarget>();
            var finalCoverage = new List<RequirementCoverageTarget>();

            var parentIds = Ids(change.ParentRevisionIdsJson);
            var expectedParentWord = parentKind.ToString();
            if (parentIds.Malformed)
                gaps.Add(new ProposalReferenceGap(null, "ExactParent", expectedParentWord,
                    ProposalReferenceGapReason.MalformedReferenceList));

            foreach (var id in parentIds.Ids)
            {
                if (parentKind == VerificationParentArtifactKind.Case)
                {
                    // A software Procedure's exact parents are Cases. They are not requirement coverage, and
                    // putting them in the coverage lists would tell the reader the procedure covers a
                    // requirement it has no recorded relationship with.
                    if (cases.TryGetValue(id, out var resolvedCase)) parents.Add(resolvedCase);
                    else
                    {
                        // No kind: nothing located it, so nothing establishes what it is. The expectation
                        // lives on the gap, where it reads as an expectation.
                        parents.Add(new VerificationParentTarget(id, Kind: null, Resolved: false));
                        gaps.Add(new ProposalReferenceGap(id, "ExactParent", "Case",
                            ProposalReferenceGapReason.UnresolvedReference));
                    }
                    continue;
                }

                // For a System Procedure or a software Case the exact parent selection *is* the coverage: the
                // full successor set the signed proposal carries, not the delta that produced it.
                if (requirements.TryGetValue(id, out var requirement))
                {
                    parents.Add(new VerificationParentTarget(id, "Requirement", Resolved: true,
                        requirement.DisplayNumber, requirement.Level, requirement.ArtifactId));
                    finalCoverage.Add(requirement);
                }
                else
                {
                    parents.Add(new VerificationParentTarget(id, Kind: null, Resolved: false));
                    gaps.Add(new ProposalReferenceGap(id, "ExactParent", "Requirement",
                        ProposalReferenceGapReason.UnresolvedReference));
                }
            }

            var added = Resolve(change.DrivingRequirementRevisionIdsJson, requirements, "AddedCoverage", gaps);
            var removed = Resolve(change.RemovedRequirementRevisionIdsJson, requirements, "RemovedCoverage", gaps);

            items.Add(new VerificationProposalItem(
                change.Id,
                string.IsNullOrWhiteSpace(change.BaseNumber) ? "" : Display(change.BaseNumber, change.Revision),
                change.Level.ToString(),
                review.ArtifactKind.ToString(),
                change.Kind.ToString(),
                proposed,
                predecessor?.Revision,
                predecessor?.Id,
                predecessor?.Content,
                Ordered(finalCoverage),
                added,
                removed,
                change.ParentKind.ToString(),
                parents.OrderBy(x => x.DisplayNumber ?? x.RevisionId.ToString(), StringComparer.Ordinal).ToList(),
                gaps.OrderBy(x => x.Role, StringComparer.Ordinal)
                    .ThenBy(x => x.RevisionId ?? Guid.Empty).ToList()));
        }

        // Lane 3: runs of the exact predecessor revisions, keyed by revision so a run of another revision of
        // the same procedure is never shown as evidence for this one.
        var predecessorIds = items.Select(x => x.BaseRevisionId).OfType<Guid>().Distinct().ToList();
        var executions = predecessorIds.Count == 0
            ? []
            // Materialised before the enum is stringified: the projection is translated to SQL, and calling
            // ToString() on a converted enum inside the query is not.
            : (await db.TestExecutions.AsNoTracking()
                    .Where(x => x.ProjectId == projectId && predecessorIds.Contains(x.ProcedureRevisionId))
                    .Select(x => new { x.Id, x.ProcedureRevisionId, x.Outcome, x.ExecutedBy, x.ExecutedAt, x.Determination })
                    .ToListAsync(ct))
                // Ordered here rather than in the query: SQLite cannot ORDER BY a DateTimeOffset, and the
                // disposable test fixtures run on SQLite while the product runs on PostgreSQL. Sorting a
                // materialised list keeps both hosts on the same code path instead of the read working only
                // where the provider happens to allow it.
                .OrderByDescending(x => x.ExecutedAt)
                .Select(x => new VerificationExecution(x.Id, x.ProcedureRevisionId, x.Outcome.ToString(),
                    x.ExecutedBy, x.ExecutedAt, x.Determination))
                .ToList();

        var effect = await ChangeProposalContentProjection.BuildEffectAsync(db, projectId, review.ReleaseId, ct);

        return new VerificationProposalContent(
            "TestChangeRequest",
            review.Id,
            review.ProjectId,
            review.ReleaseId,
            Display(review.BaseNumber, review.Revision),
            discipline.ToString(),
            review.ArtifactKind.ToString(),
            items,
            executions,
            effect);
    }

    private static string Display(string baseNumber, int revision) =>
        string.IsNullOrWhiteSpace(baseNumber) ? "" : $"{baseNumber}.{revision:D2}";

    private static IReadOnlyList<RequirementCoverageTarget> Ordered(
        IEnumerable<RequirementCoverageTarget> targets) =>
        targets.OrderBy(x => x.DisplayNumber, StringComparer.Ordinal).ToList();

    /// <summary>
    /// Resolves a recorded delta, recording a gap for anything this Project cannot answer to.
    ///
    /// An unresolvable identity is kept as a gap rather than dropped. Dropping it would show a smaller
    /// relationship set than the record names, which on a traceability surface reads as "there is nothing
    /// there" instead of "something is recorded that I cannot resolve".
    /// </summary>
    private static IReadOnlyList<RequirementCoverageTarget> Resolve(
        string json,
        IReadOnlyDictionary<Guid, RequirementCoverageTarget> known,
        string role,
        List<ProposalReferenceGap> gaps)
    {
        var stored = Ids(json);
        if (stored.Malformed)
            gaps.Add(new ProposalReferenceGap(null, role, "Requirement",
                ProposalReferenceGapReason.MalformedReferenceList));

        var resolved = new List<RequirementCoverageTarget>();
        foreach (var id in stored.Ids)
        {
            if (known.TryGetValue(id, out var target)) resolved.Add(target);
            else if (gaps.All(gap => gap.RevisionId != id || gap.Role != role))
                gaps.Add(new ProposalReferenceGap(id, role, "Requirement",
                    ProposalReferenceGapReason.UnresolvedReference));
        }
        return Ordered(resolved);
    }

    /// <summary>
    /// A stored identity list, and whether it could be read at all.
    ///
    /// The two are kept apart because "no relationships were recorded" and "relationship data exists that
    /// AeroLink cannot interpret" are different facts, and collapsing the second into the first is the more
    /// dangerous direction on a traceability surface: it reports an absence that was never established. The
    /// Digital Thread reads Draft proposals, where controlled editing deliberately allows incomplete work to
    /// be checked in before submission validation runs, so malformed content is reachable by design.
    /// </summary>
    private readonly record struct StoredIds(IReadOnlyList<Guid> Ids, bool Malformed)
    {
        public static readonly StoredIds Empty = new([], false);
    }

    private static StoredIds Ids(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return StoredIds.Empty;
        try { return new StoredIds(JsonSerializer.Deserialize<List<Guid>>(json) ?? [], false); }
        catch (JsonException) { return new StoredIds([], true); }
    }
}
