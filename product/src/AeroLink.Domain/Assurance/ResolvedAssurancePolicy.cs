namespace AeroLink.Domain.Assurance;

/// <summary>
/// The assurance policy in force for one piece of work, and the version it came from.
///
/// A project that has never recorded a policy resolves to <see cref="Recommended"/>: every lever at its
/// AeroLink recommendation, no declared level, no version. That is exactly what the product enforced before
/// this feature existed, so introducing the feature changes nothing until a project chooses to change
/// something.
/// </summary>
public sealed record ResolvedAssurancePolicy(
    Guid? PolicyVersionId,
    int Version,
    AssuranceLevel DeclaredLevel,
    IReadOnlyDictionary<AssurancePolicyLever, AssuranceLeverValue> Selections)
{
    /// <summary>The shipped recommendation for every lever, which is also the effective policy of a project with no recorded one.</summary>
    public static ResolvedAssurancePolicy Recommended { get; } =
        new(null, 0, AssuranceLevel.NotDeclared, AssurancePolicyCatalogue.Recommended);

    public AssuranceLeverValue Value(AssurancePolicyLever lever) =>
        Selections.TryGetValue(lever, out var value)
            ? value
            : AssurancePolicyCatalogue.Definition(lever).RecommendedValue;

    public bool Requires(AssurancePolicyLever lever) => Value(lever) == AssuranceLeverValue.Required;

    /// <summary>True when the project has deliberately chosen a value looser than the AeroLink recommendation.</summary>
    public bool IsRelaxed(AssurancePolicyLever lever) =>
        AssurancePolicyCatalogue.Definition(lever).IsRelaxation(Value(lever));
}

/// <summary>
/// Resolves the assurance policy at one application seam.
///
/// Two questions, deliberately separate. <see cref="ResolveAsync"/> answers "what is the project's policy
/// now", which is what a configuration screen and newly started work need. <see cref="ResolveVersionAsync"/>
/// answers "what did version N say", which is what work that already began under a snapshot needs — and is
/// the whole of why a later policy change cannot reinterpret it.
/// </summary>
public interface IProjectAssurancePolicyResolver
{
    Task<ResolvedAssurancePolicy> ResolveAsync(Guid projectId, CancellationToken ct = default);

    Task<ResolvedAssurancePolicy> ResolveVersionAsync(Guid policyVersionId, CancellationToken ct = default);
}
