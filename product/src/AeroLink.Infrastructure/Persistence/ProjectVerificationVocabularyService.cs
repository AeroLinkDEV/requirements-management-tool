using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Requirements;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>One stored value the project's vocabulary does not permit, with enough provenance to act on.</summary>
public sealed record NonConformingVerificationMethod(string Value, int ChangeCount, int RevisionCount,
    IReadOnlyList<string> Examples)
{
    public int TotalCount => ChangeCount + RevisionCount;
}

/// <summary>The configured vocabulary, its concurrency token, and what stored data declares outside it.</summary>
public sealed record VerificationVocabularyReadModel(bool Persisted, long Version, IReadOnlyList<string> Methods,
    IReadOnlyList<NonConformingVerificationMethod> NonConforming, bool CanManage);

public enum VerificationVocabularyEditResultKind { NotFound, Success, Conflict, Invalid }

public sealed record VerificationVocabularyEditResult(VerificationVocabularyEditResultKind Kind,
    VerificationVocabularyReadModel? Vocabulary = null, string? Error = null,
    IReadOnlyList<string>? StrandedMethods = null);

/// <summary>
/// The one application authority for a project's permitted verification methods (#701).
///
/// Two rules shape everything here.
///
/// A project must <b>carry</b> its vocabulary. Reading never invents one: a project whose row is genuinely
/// missing reports itself as unpersisted rather than answering from a conventional set nobody configured,
/// because an in-memory default would be a second source of truth that the configuration screen and the
/// reconciliation report could not see. Where a submission needs the authority and no row exists — a store
/// that predates the backfill, or a database seeded directly by a fixture —
/// <see cref="ResolveForSubmissionAsync"/> materializes the founding vocabulary into the caller's unit of
/// work and leaves the caller to commit it, exactly as <c>AeroLinkDbContext</c> materializes a missing
/// legacy ladder before sealing a project's first controlled content.
///
/// Nothing here ever rewrites a stored verification method. Values outside the vocabulary are reported, with
/// counts and example identities, so a programme can reconcile them deliberately through controlled change.
/// </summary>
public sealed class ProjectVerificationVocabularyService(AeroLinkDbContext db, IProjectLadderPolicyResolver policies)
{
    /// <summary>How many example identities the reconciliation report carries for each stored value.</summary>
    private const int ExampleLimit = 5;

    /// <summary>The persisted vocabulary for a project, or null when the project does not carry one yet.</summary>
    public Task<ProjectVerificationVocabulary?> FindAsync(Guid projectId, CancellationToken ct = default) =>
        db.ProjectVerificationVocabularies.Include(x => x.Methods)
            .SingleOrDefaultAsync(x => x.ProjectId == projectId, ct);

    /// <summary>
    /// The submission authority for a project, materializing the founding vocabulary when none is persisted.
    ///
    /// The new aggregate is added to the caller's change tracker and deliberately not saved here: it commits
    /// with whatever the caller was doing, so a submission that is refused for any reason — including the
    /// vocabulary check itself — leaves no trace. A project that reaches review therefore always has a
    /// persisted, auditable vocabulary rather than one this method remembered.
    /// </summary>
    public async Task<VerificationMethodPolicy> ResolveForSubmissionAsync(Guid projectId, string actor,
        string actorAddress, DateTimeOffset now, CancellationToken ct = default)
    {
        var vocabulary = await FindAsync(projectId, ct);
        if (vocabulary is not null) return vocabulary.ToPolicy();
        vocabulary = ProjectVerificationVocabulary.Founding(projectId, now);
        db.ProjectVerificationVocabularies.Add(vocabulary);
        db.ProjectVerificationMethods.AddRange(vocabulary.Methods);
        db.SecurityAuditEvents.Add(new SecurityAuditEvent("VerificationVocabularyMaterialized", actor,
            $"project:{projectId:D}", "Success",
            $"Materialized the founding verification-method vocabulary for a project that carried none: {string.Join(", ", vocabulary.OrderedValues)}.",
            actorAddress, now));
        return vocabulary.ToPolicy();
    }

    /// <summary>The configured vocabulary alongside the stored values it does not permit.</summary>
    public async Task<VerificationVocabularyReadModel?> ReadAsync(Guid projectId, bool canManage,
        CancellationToken ct = default)
    {
        if (!await db.Projects.AsNoTracking().AnyAsync(x => x.Id == projectId, ct)) return null;
        var vocabulary = await db.ProjectVerificationVocabularies.AsNoTracking().Include(x => x.Methods)
            .SingleOrDefaultAsync(x => x.ProjectId == projectId, ct);
        // A project without a persisted row is reported honestly, showing the founding set the backfill and
        // project creation both use, so the screen can say "not configured yet" instead of implying a
        // decision. Submission is what materializes it, under an attributable audit event.
        var methods = vocabulary?.OrderedValues ?? FoundingVerificationMethods.Ordered;
        var nonConforming = await FindNonConformingAsync(projectId, new VerificationMethodPolicy(methods), ct);
        return new(vocabulary is not null, vocabulary?.Version ?? 0, methods, nonConforming, canManage);
    }

    /// <summary>
    /// Replaces the permitted set for a project under optimistic concurrency.
    ///
    /// The caller establishes the actor's authority; this seam owns the reference-safety question and the
    /// version check. Both the aggregate's rules and the database's unique index have to agree, so a
    /// concurrent edit that slipped past the version predicate still loses at the constraint rather than
    /// producing two configured spellings of the same method.
    /// </summary>
    public async Task<VerificationVocabularyEditResult> ReplaceAsync(Guid projectId, IReadOnlyList<string> methods,
        long expectedVersion, string reason, string actor, string actorAddress, DateTimeOffset now,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actor))
            return Invalid("A verification-vocabulary edit requires an authenticated actor.");
        if (string.IsNullOrWhiteSpace(reason))
            return Invalid("A meaningful reason is required for a verification-vocabulary edit.");
        if (!await db.Projects.AsNoTracking().AnyAsync(x => x.Id == projectId, ct))
            return new(VerificationVocabularyEditResultKind.NotFound, Error: "That project does not exist.");

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var vocabulary = await FindAsync(projectId, ct);
        // Expected version 0 is the honest way to say "this project carries no vocabulary yet"; anything else
        // is an edit of a configuration the caller has not actually read.
        var currentVersion = vocabulary?.Version ?? 0;
        if (expectedVersion != currentVersion)
            return new(VerificationVocabularyEditResultKind.Conflict,
                Error: "The verification vocabulary changed after it was read. Refresh before editing again.");

        var declared = await DeclaredValuesAsync(projectId, ct);
        if (vocabulary is not null && methods.Count > 0)
        {
            // Asked before the aggregate refuses, purely so the API can name the offending spellings in a
            // structured field. ReplaceMembers enforces the same rule independently.
            var stranded = vocabulary.StrandedBy(methods, declared);
            if (stranded.Count > 0)
                return new(VerificationVocabularyEditResultKind.Conflict,
                    Error: ProjectVerificationVocabulary.StrandingRefusal(stranded), StrandedMethods: stranded);
        }
        try
        {
            if (vocabulary is null)
            {
                vocabulary = ProjectVerificationVocabulary.Declaring(projectId, methods, now);
                db.ProjectVerificationVocabularies.Add(vocabulary);
                db.ProjectVerificationMethods.AddRange(vocabulary.Methods);
            }
            else
            {
                var known = vocabulary.Methods.Select(x => x.Id).ToHashSet();
                vocabulary.ReplaceMembers(methods, declared, now);
                // Members introduced by this edit carry client-assigned keys and were discovered through the
                // aggregate's collection, so EF would attach them by key as updates. Adding them explicitly is
                // what makes the INSERT happen; without it a replacement that introduces a new method fails as
                // a phantom concurrency conflict.
                foreach (var member in vocabulary.Methods.Where(x => !known.Contains(x.Id)))
                    db.ProjectVerificationMethods.Add(member);
            }
        }
        catch (DomainException ex)
        {
            // The aggregate refuses before mutating, but the change tracker may still hold a partially
            // constructed graph from the attempt. Clearing it guarantees the refused edit cannot reach the
            // database through a later save in the same request.
            db.ChangeTracker.Clear();
            return Invalid(ex.Message);
        }

        db.SecurityAuditEvents.Add(new SecurityAuditEvent("VerificationVocabularyConfigured", actor,
            $"project:{projectId:D}", "Success",
            $"Set the permitted verification methods to: {string.Join(", ", vocabulary.OrderedValues)}. Reason: {reason.Trim()}",
            actorAddress, now));
        try
        {
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new(VerificationVocabularyEditResultKind.Conflict,
                Error: "The verification vocabulary changed after it was read. Refresh before editing again.");
        }
        catch (DbUpdateException ex) when (IsUniquenessViolation(ex))
        {
            return new(VerificationVocabularyEditResultKind.Conflict,
                Error: "Another verification-vocabulary edit configured one of these methods first. Refresh before editing again.");
        }
        return new(VerificationVocabularyEditResultKind.Success, await ReadAsync(projectId, canManage: true, ct));
    }

    /// <summary>
    /// Values that stored controlled data declares but the vocabulary does not permit.
    ///
    /// Reads only. Both authorities are covered — in-flight <c>RequirementChange</c> proposals and
    /// materialized <c>RequirementRevision</c> history — because a proposal about to be submitted and a
    /// released revision are equally stranded by a vocabulary that does not permit what they say, and an
    /// owner needs to see both to decide what to correct.
    ///
    /// Records at a level with no verification capability are excluded rather than reported: an Interface
    /// change carries the product's "Not applicable" sentinel because an ICD has no verification artifact at
    /// all, and listing every one of them as non-conforming would bury the real fragmentation this report
    /// exists to surface.
    /// </summary>
    public async Task<IReadOnlyList<NonConformingVerificationMethod>> FindNonConformingAsync(Guid projectId,
        VerificationMethodPolicy policy, CancellationToken ct = default)
    {
        var ladder = await policies.ResolveAsync(projectId, ct);
        // The levels this project actually configures, not every value the enum can hold: a resolved ladder
        // has no definition for a level it does not carry, and asking it would throw rather than answer "no".
        var verificationLevels = ladder.OrderedLevels.Where(ladder.HasVerification).ToArray();

        var changes = await (from change in db.RequirementChanges.AsNoTracking()
                join request in db.SystemChangeRequests.AsNoTracking() on change.ChangeRequestId equals request.Id
                where request.ProjectId == projectId
                    && change.Kind != RequirementChangeKind.Retire
                    && verificationLevels.Contains(change.Level)
                select new { change.VerificationMethod, change.BaseNumber, change.Revision })
            .ToListAsync(ct);
        var revisions = await (from revision in db.RequirementRevisions.AsNoTracking()
                join artifact in db.Requirements.AsNoTracking() on revision.ArtifactId equals artifact.Id
                where artifact.ProjectId == projectId && verificationLevels.Contains(artifact.Level)
                select new { revision.VerificationMethod, artifact.BaseNumber, revision.Revision })
            .ToListAsync(ct);

        // A blank method on a draft nobody has submitted is not a competing spelling — it is an unfinished
        // field, and submission already refuses it. Reporting it here would mix "somebody typed the wrong
        // word" with "somebody has not typed anything yet".
        var report = changes
            .Select(x => (x.VerificationMethod, Identity: Identity(x.BaseNumber, x.Revision), FromChange: true))
            .Concat(revisions.Select(x => (x.VerificationMethod, Identity: Identity(x.BaseNumber, x.Revision), FromChange: false)))
            .Where(x => !string.IsNullOrWhiteSpace(x.VerificationMethod) && !policy.IsPermitted(x.VerificationMethod))
            .GroupBy(x => x.VerificationMethod, StringComparer.Ordinal)
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(group => new NonConformingVerificationMethod(
                group.Key,
                group.Count(x => x.FromChange),
                group.Count(x => !x.FromChange),
                // Counts stay accurate across both authorities; the examples are deduplicated because one
                // requirement appearing as both a proposal and a revision is one thing for an owner to look at.
                group.Select(x => x.Identity).Where(x => x.Length > 0).Distinct(StringComparer.Ordinal)
                    .OrderBy(x => x, StringComparer.Ordinal).Take(ExampleLimit).ToArray()))
            .ToArray();
        return report;
    }

    /// <summary>
    /// The exact verification-method spellings that controlled requirement records in this project declare.
    ///
    /// Exact, not normalized. Removal safety has to be asked in the same terms review answers in, and review
    /// matches the configured spelling byte-for-byte: a project whose requirements say "Test" is stranded by
    /// re-spelling the configured member to "test" just as completely as by deleting it, because every one of
    /// those records becomes non-conforming and every future submission of one is refused. Comparing
    /// normalized keys here declared that edit safe precisely because it could not see the difference that
    /// review can.
    ///
    /// Both authorities are covered — in-flight proposals and materialized revisions — because either one
    /// declaring a spelling is enough to strand it. Blank values are excluded: nothing is stranded by
    /// removing a method no record names.
    ///
    /// Retirement proposals are excluded, because a retirement declares no verification method. Submission
    /// skips them and the reconciliation report skips them, so counting one here made the same record a
    /// declaration for the purpose of blocking configuration and a non-declaration everywhere else — and a
    /// historical value a retirement happened to carry could pin a spelling nobody had asserted.
    ///
    /// The exclusion is for retirement <i>proposals</i> only. A materialized <c>RequirementRevision</c> still
    /// counts however its requirement was later disposed of: immutable history says what it says, and a
    /// revision declaring "Test" is stranded by removing "Test" whether or not the requirement was retired
    /// afterwards.
    /// </summary>
    private async Task<IReadOnlyCollection<string>> DeclaredValuesAsync(Guid projectId, CancellationToken ct)
    {
        var declared = new HashSet<string>(StringComparer.Ordinal);
        var changeValues = await (from change in db.RequirementChanges.AsNoTracking()
                join request in db.SystemChangeRequests.AsNoTracking() on change.ChangeRequestId equals request.Id
                where request.ProjectId == projectId && change.VerificationMethod != ""
                    && change.Kind != RequirementChangeKind.Retire
                select change.VerificationMethod).Distinct().ToListAsync(ct);
        declared.UnionWith(changeValues);
        var revisionValues = await (from revision in db.RequirementRevisions.AsNoTracking()
                join artifact in db.Requirements.AsNoTracking() on revision.ArtifactId equals artifact.Id
                where artifact.ProjectId == projectId && revision.VerificationMethod != ""
                select revision.VerificationMethod).Distinct().ToListAsync(ct);
        declared.UnionWith(revisionValues);
        declared.Remove(string.Empty);
        return declared;
    }

    private static string Identity(string baseNumber, int revision) =>
        string.IsNullOrWhiteSpace(baseNumber) ? string.Empty : ArtifactNumber.Display(baseNumber, revision);

    private static VerificationVocabularyEditResult Invalid(string error) =>
        new(VerificationVocabularyEditResultKind.Invalid, Error: error);

    private static bool IsUniquenessViolation(DbUpdateException ex) =>
        ex.InnerException?.Message.Contains("IX_project_verification_methods", StringComparison.OrdinalIgnoreCase) == true
        || ex.InnerException?.Message.Contains("IX_project_verification_vocabularies", StringComparison.OrdinalIgnoreCase) == true
        || ex.InnerException?.Message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase) == true;
}
