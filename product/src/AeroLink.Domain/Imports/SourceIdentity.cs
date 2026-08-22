using AeroLink.Domain.Common;

namespace AeroLink.Domain.Imports;

/// <summary>
/// What a record was called in the system it came from, kept as a record in its own right.
///
/// Deliberately not named ExternalIdentity: that already means a federated sign-in account elsewhere in this
/// product, and the two have nothing to do with each other.
///
/// Every drawing, CDRL, email and test procedure outside this tool still says SYS-01234. Discarding that
/// identifier would sever every one of those references, and nothing here could answer "where did it go?".
/// So the source identifier survives the import as a first-class, searchable record.
///
/// The keys are what make a re-import a delta rather than a duplicate set: source system, module and the
/// source's own stable object number identify the same object across two extracts, even when its identifier
/// text has been edited in between.
/// </summary>
public sealed class SourceIdentity
{
    private SourceIdentity() { }

    public SourceIdentity(Guid projectId, Guid baselineImportId, string sourceSystem, string sourceModule,
        string sourceObjectKey, string sourceIdentifier, DateTimeOffset now)
    {
        if (projectId == Guid.Empty) throw new DomainException("A source identity requires its Project.");
        if (baselineImportId == Guid.Empty) throw new DomainException("A source identity requires the import that recorded it.");
        Id = Guid.NewGuid();
        ProjectId = projectId;
        BaselineImportId = baselineImportId;
        SourceSystem = Required(sourceSystem, "source system");
        SourceModule = Required(sourceModule, "source module");
        SourceObjectKey = Required(sourceObjectKey, "source object key");
        SourceIdentifier = Required(sourceIdentifier, "source identifier");
        FirstSeenAt = now;
        LastSeenAt = now;
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    /// <summary>The import that first recorded this identity. Never reassigned by a later import.</summary>
    public Guid BaselineImportId { get; private set; }

    public string SourceSystem { get; private set; } = "";
    public string SourceModule { get; private set; } = "";
    /// <summary>The source's own stable key — a DOORS Absolute Number, for instance. Survives renaming.</summary>
    public string SourceObjectKey { get; private set; } = "";
    /// <summary>What a person outside this tool would quote: <c>SYS-01234</c>.</summary>
    public string SourceIdentifier { get; private set; } = "";

    /// <summary>
    /// False when the object existed in the source's history but not in the baseline that was imported.
    ///
    /// Such an object is recorded so a reference to it can still be answered, and it joins nothing: no
    /// requirement, no provenance link. History is narrative, not nodes, so a retired ancestor never becomes
    /// a dangling reference in the traceability network.
    /// </summary>
    public bool InImportedBaseline { get; private set; } = true;

    public DateTimeOffset FirstSeenAt { get; private set; }
    public DateTimeOffset LastSeenAt { get; private set; }

    public static SourceIdentity FromHistoryOnly(Guid projectId, Guid baselineImportId, string sourceSystem,
        string sourceModule, string sourceObjectKey, string sourceIdentifier, DateTimeOffset now)
    {
        var identity = new SourceIdentity(projectId, baselineImportId, sourceSystem, sourceModule,
            sourceObjectKey, sourceIdentifier, now);
        identity.InImportedBaseline = false;
        return identity;
    }

    /// <summary>Marks the identity as seen again by a later extract, without disturbing who first recorded it.</summary>
    public void SeenAgain(DateTimeOffset now) => LastSeenAt = now;

    /// <summary>
    /// Creates the provenance link from a controlled requirement revision to this source object.
    ///
    /// It lives on the identity rather than standing free because the rule that keeps source history
    /// narrative rather than nodes — an object retired before the imported baseline joins nothing — can only
    /// be enforced where the identity itself is in hand. Constructing the link directly would let a caller
    /// hang a real trace off an object that was never in the baseline anybody signed for.
    /// </summary>
    public SourceIdentityLink LinkTo(Guid requirementRevisionId, DateTimeOffset now, Guid? committingBaselineImportId = null)
    {
        if (!InImportedBaseline)
            throw new DomainException(
                $"{SourceIdentifier} was not in the imported baseline. It is recorded so a reference to it can be answered, and nothing here originates from it.");
        var packageId = committingBaselineImportId ?? BaselineImportId;
        if (packageId == Guid.Empty)
            throw new DomainException("A provenance link requires the package that committed the revision.");
        return new SourceIdentityLink(ProjectId, requirementRevisionId, Id, packageId, now);
    }

    /// <summary>
    /// Creates a provenance link when the current import membership, rather than the first-seen identity row,
    /// proves that this source object was present in the package being materialized.
    /// </summary>
    public SourceIdentityLink LinkToFromImport(Guid requirementRevisionId, Guid committingBaselineImportId,
        DateTimeOffset now)
    {
        if (committingBaselineImportId == Guid.Empty)
            throw new DomainException("A provenance link requires the package that committed the revision.");
        return new SourceIdentityLink(ProjectId, requirementRevisionId, Id, committingBaselineImportId, now);
    }

    private static string Required(string value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new DomainException($"A {name} is required.") : value.Trim();
}

/// <summary>
/// Joins a controlled requirement revision to the source object it came from.
///
/// Reads in one direction only: <c>SYSR-000148.00 originates from SYS-01234</c>. The controlled requirement
/// is the subject; the source object is what it came from. A reversed link would produce a complete,
/// plausible and entirely wrong lineage, so the direction is structural rather than a matter of convention.
///
/// Deliberately not a RequirementTraceLink. That binds two requirement revisions and nothing else, and a
/// source object is not a revision — making its target nullable to fit this would weaken the traceability
/// invariant for every real trace in the product. This is a provenance link, presented alongside traces
/// because that is where a reader looks for it, and stored apart because it is not one.
/// </summary>
public sealed class SourceIdentityLink
{
    private SourceIdentityLink() { }

    public SourceIdentityLink(Guid projectId, Guid requirementRevisionId, Guid sourceIdentityId,
        Guid baselineImportId, DateTimeOffset now)
    {
        if (projectId == Guid.Empty) throw new DomainException("A provenance link requires its Project.");
        if (requirementRevisionId == Guid.Empty) throw new DomainException("A provenance link requires the requirement revision it describes.");
        if (sourceIdentityId == Guid.Empty) throw new DomainException("A provenance link requires the source identity it points to.");
        if (baselineImportId == Guid.Empty) throw new DomainException("A provenance link requires the import that created it.");
        Id = Guid.NewGuid();
        ProjectId = projectId;
        RequirementRevisionId = requirementRevisionId;
        SourceIdentityId = sourceIdentityId;
        BaselineImportId = baselineImportId;
        CreatedAt = now;
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    /// <summary>The subject: what this product controls.</summary>
    public Guid RequirementRevisionId { get; private set; }
    /// <summary>The object: what it originated from.</summary>
    public Guid SourceIdentityId { get; private set; }
    /// <summary>
    /// The import that created this link — never a change request. Imports are a second recognised origin
    /// for links precisely so that nothing here suggests a build carried work it did not.
    /// </summary>
    public Guid BaselineImportId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
}

/// <summary>
/// What the source system reported about an object before the baseline that was imported.
///
/// Recorded verbatim as reported facts. This product makes no claim about any of it: it is never restated as
/// a requirement revision, never signed for, and never participates in a gate, a coverage figure or a
/// readiness computation. That is exactly what makes importing history safe — a source's history is often
/// incomplete or inconsistent, and it can be recorded honestly because nothing downstream reasons over it.
/// </summary>
public sealed class SourceHistoryEntry
{
    private SourceHistoryEntry() { }

    public SourceHistoryEntry(Guid projectId, Guid sourceIdentityId, Guid baselineImportId,
        string sourceBaselineName, string statement, string changedBy, DateTimeOffset? changedAt,
        string sourceChangeReference)
    {
        if (projectId == Guid.Empty) throw new DomainException("A source history entry requires its Project.");
        if (sourceIdentityId == Guid.Empty) throw new DomainException("A source history entry requires its source identity.");
        if (baselineImportId == Guid.Empty) throw new DomainException("A source history entry requires the import that recorded it.");
        Id = Guid.NewGuid();
        ProjectId = projectId;
        SourceIdentityId = sourceIdentityId;
        BaselineImportId = baselineImportId;
        SourceBaselineName = string.IsNullOrWhiteSpace(sourceBaselineName)
            ? throw new DomainException("A source history entry requires the source baseline it describes.")
            : sourceBaselineName.Trim();
        // Everything below is reported, not required. A source that recorded no author, no date or no
        // statement is described as it was found rather than filled in with something plausible.
        Statement = statement?.Trim() ?? "";
        ChangedBy = changedBy?.Trim() ?? "";
        ChangedAt = changedAt;
        SourceChangeReference = sourceChangeReference?.Trim() ?? "";
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public Guid SourceIdentityId { get; private set; }
    public Guid BaselineImportId { get; private set; }
    public string SourceBaselineName { get; private set; } = "";
    public string Statement { get; private set; } = "";
    public string ChangedBy { get; private set; } = "";
    public DateTimeOffset? ChangedAt { get; private set; }
    public string SourceChangeReference { get; private set; } = "";
}
