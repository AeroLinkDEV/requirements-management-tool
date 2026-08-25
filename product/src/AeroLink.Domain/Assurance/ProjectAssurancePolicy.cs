using System.Security.Cryptography;
using System.Text;
using AeroLink.Domain.Common;

namespace AeroLink.Domain.Assurance;

/// <summary>
/// One version of a project's declared assurance policy.
///
/// A version is written once and never edited. Recording a change writes a new version and stamps the
/// previous one as superseded, so the policy a piece of controlled work began under remains readable exactly
/// as it was. That is the whole of the prospective-versioning mechanism: work records the version it started
/// under, and resolving a record's policy means resolving that version rather than whatever is current.
///
/// The declared assurance level lives here rather than on the project row for the same reason. It is
/// metadata, but it is attributed, reasoned metadata, and putting it on a version gives it a history without
/// a second mechanism.
/// </summary>
public sealed class ProjectAssurancePolicy
{
    private ProjectAssurancePolicy() { }

    private ProjectAssurancePolicy(Guid projectId, int version, AssuranceLevel declaredLevel,
        IReadOnlyDictionary<AssurancePolicyLever, AssuranceLeverValue> selections, string reason, string actor,
        DateTimeOffset now)
    {
        if (projectId == Guid.Empty) throw new DomainException("An assurance policy requires a project.");
        if (version < 1) throw new DomainException("An assurance policy version must be positive.");
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("A meaningful reason is required for every assurance policy change.");
        if (string.IsNullOrWhiteSpace(actor)) throw new DomainException("An assurance policy version requires an attributable actor.");
        Id = Guid.NewGuid();
        ProjectId = projectId;
        Version = version;
        DeclaredLevel = declaredLevel;
        AuthorityPolicyVersion = AssuranceAuthorityPolicy.CurrentVersion;
        SelectionsSnapshot = AssurancePolicySnapshot.Canonicalize(declaredLevel, selections);
        SnapshotHash = AssurancePolicySnapshot.Hash(SelectionsSnapshot);
        Reason = reason.Trim();
        CreatedBy = actor.Trim();
        EffectiveFrom = now;
    }

    /// <summary>
    /// Records a new version. The caller has already validated the selections and the deviations they need;
    /// keeping construction here means no seeder or endpoint can write a version without its reason, actor
    /// and canonical snapshot.
    /// </summary>
    public static ProjectAssurancePolicy Record(Guid projectId, int version, AssuranceLevel declaredLevel,
        IReadOnlyDictionary<AssurancePolicyLever, AssuranceLeverValue> selections, string reason, string actor,
        DateTimeOffset now) => new(projectId, version, declaredLevel, selections, reason, actor, now);

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public int Version { get; private set; }
    public AssuranceLevel DeclaredLevel { get; private set; }
    /// <summary>The authority-rule version this policy version was recorded under.</summary>
    public int AuthorityPolicyVersion { get; private set; }
    public string SelectionsSnapshot { get; private set; } = string.Empty;
    public string SnapshotHash { get; private set; } = string.Empty;
    public string Reason { get; private set; } = string.Empty;
    public string CreatedBy { get; private set; } = string.Empty;
    public DateTimeOffset EffectiveFrom { get; private set; }
    public DateTimeOffset? SupersededAt { get; private set; }
    public string SupersededBy { get; private set; } = string.Empty;

    public bool IsEffective => SupersededAt is null;

    /// <summary>
    /// Closes this version's effective interval. The only mutation a recorded version accepts, and it adds
    /// history rather than rewriting it: nothing about what this version said can change.
    /// </summary>
    public void Supersede(string actor, DateTimeOffset now)
    {
        if (SupersededAt is not null) throw new DomainException("This assurance policy version is already superseded.");
        if (string.IsNullOrWhiteSpace(actor)) throw new DomainException("Superseding an assurance policy version requires an attributable actor.");
        if (now < EffectiveFrom) throw new DomainException("An assurance policy version cannot be superseded before it took effect.");
        SupersededAt = now;
        SupersededBy = actor.Trim();
    }

    /// <summary>Reads the stored selections back, filling any lever this version predates with its recommendation.</summary>
    public IReadOnlyDictionary<AssurancePolicyLever, AssuranceLeverValue> Selections() =>
        AssurancePolicySnapshot.ReadSelections(SelectionsSnapshot);
}

/// <summary>
/// The canonical text a policy version hashes. Deterministic ordering and no database identities, so two
/// equivalent policies produce the same hash whatever order the request listed them in.
/// </summary>
public static class AssurancePolicySnapshot
{
    public static string Canonicalize(AssuranceLevel declaredLevel,
        IReadOnlyDictionary<AssurancePolicyLever, AssuranceLeverValue> selections)
    {
        var canonical = AssurancePolicyCatalogue.All
            .OrderBy(x => x.Lever.ToString(), StringComparer.Ordinal)
            .Select(definition =>
            {
                var value = selections.TryGetValue(definition.Lever, out var selected)
                    ? selected
                    : definition.RecommendedValue;
                if (!definition.Accepts(value))
                    throw new DomainException($"{value} is not a supported setting for the {definition.Name} policy lever.");
                return $"{definition.Lever}={value}";
            });
        return $"level[{declaredLevel}]|levers[{string.Join(";", canonical)}]";
    }

    public static string Hash(string canonicalSnapshot) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalSnapshot))).ToLowerInvariant();

    /// <summary>
    /// Parses a stored snapshot. A lever the snapshot predates resolves to its recommendation, which is what
    /// the project was effectively running under before the lever existed.
    /// </summary>
    public static IReadOnlyDictionary<AssurancePolicyLever, AssuranceLeverValue> ReadSelections(string snapshot)
    {
        var selections = new Dictionary<AssurancePolicyLever, AssuranceLeverValue>(AssurancePolicyCatalogue.Recommended);
        var start = snapshot.IndexOf("levers[", StringComparison.Ordinal);
        if (start < 0) return selections;
        var body = snapshot[(start + "levers[".Length)..];
        var end = body.IndexOf(']');
        if (end >= 0) body = body[..end];
        foreach (var pair in body.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length != 2) continue;
            if (Enum.TryParse<AssurancePolicyLever>(parts[0], false, out var lever)
                && Enum.TryParse<AssuranceLeverValue>(parts[1], false, out var value)
                && AssurancePolicyCatalogue.Definition(lever).Accepts(value))
                selections[lever] = value;
        }
        return selections;
    }
}
