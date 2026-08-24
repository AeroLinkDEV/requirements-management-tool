using AeroLink.Domain.Common;

namespace AeroLink.Domain.Requirements;

/// <summary>
/// The verification methods a project is founded on when nothing else has been configured.
///
/// This is a founding default, not a product enum. It is the set the product already treats as its
/// verification-method contract in two independent places — the enterprise workspace schema declares
/// exactly these as an enumeration field, and requirement authoring has always offered exactly these
/// four options — so a project that predates #701 lands on the vocabulary it has been using in practice
/// rather than on one somebody invented at migration time. A programme that verifies by Similarity or
/// Service Experience configures that; nothing here prevents it.
/// </summary>
public static class FoundingVerificationMethods
{
    public static readonly IReadOnlyList<string> Ordered = ["Test", "Analysis", "Inspection", "Demonstration"];
}

/// <summary>Case- and whitespace-insensitive configuration key for verification-method names.</summary>
public static class VerificationMethodName
{
    /// <summary>The longest a configured method name may be. Wide enough for "Service Experience"; narrow
    /// enough that a pasted paragraph is refused rather than becoming a permitted method.</summary>
    public const int MaxLength = 100;

    /// <summary>
    /// Invariant-case folding of a trimmed method name.
    ///
    /// This is a <b>configuration</b> key and nothing else. It exists so that "Test" and "test" cannot both
    /// be configured as separate permitted methods — that fragmentation is the defect #701 corrects. It is
    /// deliberately NOT the runtime membership test: a requirement declaring "test" when the project permits
    /// "Test" is refused at submission and named for a deliberate correction, because silently accepting the
    /// variant and re-spelling it would make the record assert a decision nobody took. See
    /// <see cref="VerificationMethodPolicy.IsPermitted"/>.
    ///
    /// Ordinal and culture-blind: method names are engineering terms compared byte-for-byte once folded,
    /// not natural-language text.
    /// </summary>
    public static string Normalize(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant();
}

/// <summary>
/// One permitted verification method as a project declares it.
///
/// <see cref="DisplayValue"/> is the configured spelling and the only thing controlled documents ever
/// render. <see cref="NormalizedValue"/> is the uniqueness key that stops the same method being configured
/// twice under different casing. <see cref="Position"/> is the configured order, which is the order
/// authoring offers and the order a refusal message lists — deterministic so two readers of the same
/// project see the same vocabulary in the same sequence.
/// </summary>
public sealed class ProjectVerificationMethod
{
    private ProjectVerificationMethod() { }

    internal ProjectVerificationMethod(Guid vocabularyId, Guid projectId, int position, string displayValue,
        DateTimeOffset now)
    {
        if (vocabularyId == Guid.Empty) throw new DomainException("A verification method requires a vocabulary.");
        if (projectId == Guid.Empty) throw new DomainException("A verification method requires a project.");
        if (position < 1) throw new DomainException("A verification-method position must be positive.");
        Id = Guid.NewGuid();
        VocabularyId = vocabularyId;
        ProjectId = projectId;
        Position = position;
        DisplayValue = displayValue;
        NormalizedValue = VerificationMethodName.Normalize(displayValue);
        CreatedAt = UpdatedAt = now;
        Version = 1;
    }

    public Guid Id { get; private set; }
    public Guid VocabularyId { get; private set; }
    public Guid ProjectId { get; private set; }
    public int Position { get; private set; }

    /// <summary>The spelling the programme configured. Controlled documents render this verbatim.</summary>
    public string DisplayValue { get; private set; } = string.Empty;

    /// <summary>The configuration uniqueness key; unique within a project. Never a membership test.</summary>
    public string NormalizedValue { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public long Version { get; private set; }

    /// <summary>
    /// Moves a surviving member to its new configured position, and re-spells it when the configuration
    /// changed only its casing. Both keep the row identity, so re-ordering a vocabulary is an edit of the
    /// configuration rather than a delete-and-recreate that would discard when each method was introduced.
    /// </summary>
    internal void Reconfigure(int position, string displayValue, DateTimeOffset now)
    {
        if (position < 1) throw new DomainException("A verification-method position must be positive.");
        if (Position == position && string.Equals(DisplayValue, displayValue, StringComparison.Ordinal)) return;
        Position = position;
        DisplayValue = displayValue;
        NormalizedValue = VerificationMethodName.Normalize(displayValue);
        UpdatedAt = now;
        Version++;
    }
}

/// <summary>
/// A project's declared set of permitted verification methods (#701).
///
/// Verification method used to be free text on <c>RequirementChange</c>, so one project could hold "Test",
/// "test" and "Testing" on requirements that meant the same thing — and an auditor filtering on one of them
/// would miss the others. The vocabulary is the project's declaration of what may legitimately be declared,
/// enforced where a change request crosses into review.
///
/// It is per-project rather than a product enum because the permitted set is a programme decision:
/// Test / Analysis / Inspection / Demonstration is common, but programmes add Similarity or Service
/// Experience, and a fixed enum would be wrong in the other direction.
///
/// The aggregate refuses three things nothing else can see at once: an empty vocabulary (authoring would
/// have nothing legitimate to offer and every submission would fail), two members differing only in case or
/// surrounding whitespace (the same fragmentation the issue exists to correct, moved into configuration),
/// and removing a member that controlled requirement records still declare (which would strand those
/// records outside their own project's vocabulary). Every rule is checked before anything mutates, so a
/// refused edit leaves the configured set exactly as it was.
/// </summary>
public sealed class ProjectVerificationVocabulary
{
    private readonly List<ProjectVerificationMethod> _methods = [];

    private ProjectVerificationVocabulary() { }

    private ProjectVerificationVocabulary(Guid projectId, IEnumerable<string> methods, DateTimeOffset now)
    {
        if (projectId == Guid.Empty) throw new DomainException("A verification vocabulary requires a project.");
        Id = Guid.NewGuid();
        ProjectId = projectId;
        CreatedAt = UpdatedAt = now;
        Version = 1;
        var prepared = Prepare(methods, initial: true);
        foreach (var (display, _) in prepared)
            _methods.Add(new ProjectVerificationMethod(Id, projectId, _methods.Count + 1, display, now));
    }

    /// <summary>
    /// The vocabulary a project is born with: the product's founding methods, in the order authoring has
    /// always offered them. Used at project creation and by the #701 backfill, so that every project carries
    /// a persisted vocabulary rather than an implied one.
    /// </summary>
    public static ProjectVerificationVocabulary Founding(Guid projectId, DateTimeOffset now) =>
        new(projectId, FoundingVerificationMethods.Ordered, now);

    /// <summary>A vocabulary declaring exactly the methods given, for a programme that configures its own.</summary>
    public static ProjectVerificationVocabulary Declaring(Guid projectId, IEnumerable<string> methods,
        DateTimeOffset now) => new(projectId, methods, now);

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>The optimistic-concurrency token an authorized edit must present.</summary>
    public long Version { get; private set; }

    public IReadOnlyList<ProjectVerificationMethod> Methods => _methods;

    /// <summary>The configured spellings in configured order.</summary>
    public IReadOnlyList<string> OrderedValues =>
        _methods.OrderBy(x => x.Position).Select(x => x.DisplayValue).ToArray();

    /// <summary>The runtime authority a submission boundary consults.</summary>
    public VerificationMethodPolicy ToPolicy() => new(OrderedValues);

    /// <summary>
    /// Replaces the permitted set wholesale.
    ///
    /// <paramref name="declaredValues"/> is supplied by the caller because only the caller can see the
    /// requirement data; the invariant it feeds lives here so no endpoint, importer or seeder can route
    /// around it. A spelling those records still declare cannot leave the vocabulary — the vocabulary would
    /// then contradict controlled history, and #701 exists precisely to stop a project's records and its
    /// declared vocabulary drifting apart.
    ///
    /// Nothing in this method rewrites a requirement value. Reconciling a stored value that the new
    /// vocabulary does not permit is a separate, deliberate act by whoever owns those records.
    /// </summary>
    public void ReplaceMembers(IEnumerable<string> methods, IReadOnlyCollection<string> declaredValues,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(declaredValues);
        // Everything is validated against a prepared copy before a single member moves. A refused edit must
        // leave the configuration byte-identical, because a half-applied vocabulary would silently change
        // what the next submission accepts.
        var prepared = Prepare(methods, initial: false);
        var surviving = prepared.Select(x => x.Normalized).ToHashSet(StringComparer.Ordinal);
        var stranded = StrandedBy(prepared.Select(x => x.Display), declaredValues);
        if (stranded.Count > 0)
            throw new DomainException(StrandingRefusal(stranded));

        var existing = _methods.ToDictionary(x => x.NormalizedValue, StringComparer.Ordinal);
        var position = 1;
        foreach (var (display, normalized) in prepared)
        {
            if (existing.TryGetValue(normalized, out var member)) member.Reconfigure(position, display, now);
            else _methods.Add(new ProjectVerificationMethod(Id, ProjectId, position, display, now));
            position++;
        }
        _methods.RemoveAll(x => !surviving.Contains(x.NormalizedValue));
        _methods.Sort((left, right) => left.Position.CompareTo(right.Position));
        UpdatedAt = now;
        Version++;
    }

    /// <summary>
    /// Which configured spellings a proposed replacement would take away while controlled records still
    /// declare them.
    ///
    /// The comparison is <b>exact</b>, on both sides, and that is the whole point. Runtime membership is
    /// exact ordinal (see <see cref="VerificationMethodPolicy.IsPermitted"/>), so re-spelling a configured
    /// member strands the records declaring the old spelling every bit as completely as deleting it: a
    /// project whose requirements say "Test" and whose vocabulary is edited to say "test" would find every
    /// one of those requirements suddenly non-conforming and every future submission of one refused, with
    /// nothing having been removed. Comparing normalized keys here — which is what this method used to do —
    /// declared that edit safe precisely because it could not see the difference that review can.
    ///
    /// So a casing change is treated as a removal plus an introduction whenever records declare the old
    /// exact value, and is refused. A casing change nothing declares is still permitted: no record can be
    /// stranded by it, and forbidding it would stop a programme correcting its own configuration.
    ///
    /// Only currently configured members can be stranded. A stored value the vocabulary never permitted —
    /// "Testing" against a Test/Analysis/Inspection/Demonstration project — is already reported by the
    /// reconciliation report and must not also block unrelated configuration work.
    ///
    /// A pure query, so an API can name the offending spellings in a structured refusal without parsing the
    /// message text. <see cref="ReplaceMembers"/> asks the same question independently and refuses on the
    /// answer, so calling this first is a courtesy to the caller rather than the thing that enforces the rule.
    /// </summary>
    public IReadOnlyList<string> StrandedBy(IEnumerable<string> methods, IReadOnlyCollection<string> declaredValues)
    {
        ArgumentNullException.ThrowIfNull(methods);
        ArgumentNullException.ThrowIfNull(declaredValues);
        // Rebuilt with an ordinal comparer rather than trusted: a set handed in with a case-insensitive one
        // would silently restore exactly the blindness this method exists to remove.
        var declared = new HashSet<string>(declaredValues, StringComparer.Ordinal);
        var surviving = new HashSet<string>(methods.Select(x => (x ?? string.Empty).Trim()), StringComparer.Ordinal);
        return _methods
            .Where(x => !surviving.Contains(x.DisplayValue))
            .Where(x => declared.Contains(x.DisplayValue))
            .OrderBy(x => x.Position)
            .Select(x => x.DisplayValue)
            .ToArray();
    }

    /// <summary>The one wording for a stranding refusal, so the aggregate and the API say the same thing.</summary>
    public static string StrandingRefusal(IReadOnlyList<string> stranded) =>
        $"These verification methods are still declared by controlled requirement records, so they cannot be removed or re-spelled: {string.Join(", ", stranded)}. Review matches the configured spelling exactly, so either change would leave those records outside their own project's vocabulary. Correct or retire them first.";

    private static List<(string Display, string Normalized)> Prepare(IEnumerable<string> methods, bool initial)
    {
        ArgumentNullException.ThrowIfNull(methods);
        var prepared = new List<(string Display, string Normalized)>();
        foreach (var raw in methods)
        {
            var display = (raw ?? string.Empty).Trim();
            if (display.Length == 0)
                throw new DomainException("A verification method cannot be blank.");
            if (display.Length > VerificationMethodName.MaxLength)
                throw new DomainException(
                    $"The verification method '{display}' exceeds {VerificationMethodName.MaxLength} characters.");
            prepared.Add((display, VerificationMethodName.Normalize(display)));
        }
        if (prepared.Count == 0)
            throw new DomainException(initial
                ? "A verification vocabulary requires at least one permitted method."
                : "A verification vocabulary cannot be emptied. Configure the methods this programme permits.");
        var duplicate = prepared.GroupBy(x => x.Normalized, StringComparer.Ordinal).FirstOrDefault(x => x.Count() > 1);
        if (duplicate is not null)
            throw new DomainException(
                $"These verification methods differ only in case or surrounding whitespace and cannot both be configured: {string.Join(" / ", duplicate.Select(x => $"'{x.Display}'"))}.");
        return prepared;
    }
}

/// <summary>
/// The runtime submission authority: exactly which spellings a project permits, in configured order.
///
/// Membership is <b>exact and ordinal</b>. "test" is not "Test", and "Testing" is not "Test". That is the
/// whole point of #701: the alternative — accepting a near-miss and re-spelling it on the way into review —
/// would have the product decide that two engineering terms mean the same thing and rewrite an author's
/// declaration to match, on a record an approver is about to sign. A mismatch is refused, named, and left
/// for a person to correct.
///
/// Case-insensitivity lives one level up, in configuration: a project cannot declare both "Test" and "test"
/// as separate permitted methods. See <see cref="VerificationMethodName.Normalize"/>.
///
/// Passed across domain seams like <c>ILadderPolicy</c>, so the rule is enforced where a change request
/// crosses into review rather than wherever a particular client remembered to check.
/// </summary>
public sealed class VerificationMethodPolicy
{
    private readonly HashSet<string> permitted;

    public VerificationMethodPolicy(IReadOnlyList<string> permittedMethods)
    {
        ArgumentNullException.ThrowIfNull(permittedMethods);
        if (permittedMethods.Count == 0)
            throw new DomainException("A verification-method policy requires at least one permitted method.");
        var ordered = new List<string>();
        var normalized = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in permittedMethods)
        {
            var display = (raw ?? string.Empty).Trim();
            if (display.Length == 0)
                throw new DomainException("A permitted verification method cannot be blank.");
            if (!normalized.Add(VerificationMethodName.Normalize(display)))
                throw new DomainException(
                    $"The verification method '{display}' differs from another permitted method only in case or surrounding whitespace.");
            ordered.Add(display);
        }
        PermittedMethods = ordered;
        permitted = new HashSet<string>(ordered, StringComparer.Ordinal);
    }

    /// <summary>The configured spellings, in configured order; authoring offers exactly these.</summary>
    public IReadOnlyList<string> PermittedMethods { get; }

    /// <summary>Exact ordinal membership. A blank, a near-miss, and an unknown term are all refused.</summary>
    public bool IsPermitted(string? candidate) =>
        !string.IsNullOrWhiteSpace(candidate) && permitted.Contains(candidate);

    /// <summary>Renders the permitted set for a refusal message, in configured order.</summary>
    public string DescribePermitted() => string.Join(", ", PermittedMethods);
}
