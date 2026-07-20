using AeroLink.Domain.Common;

namespace AeroLink.Domain.Requirements;

public enum ControlledArtifactFamily
{
    ChangeRequest,
    RequirementProposal,
    SpecificationStructure,
    TestProcedure,
    TraceLinkProposal,
    ReleasePlanning,
    DocumentTemplate,
    ProblemReport,
    ConfigurationChangeSet
}

public sealed record ControlledArtifactEditPolicy(
    ControlledArtifactFamily Family,
    string CanonicalType,
    bool Exclusive,
    int DefaultLeaseMinutes,
    int MinimumLeaseMinutes,
    int MaximumLeaseMinutes,
    IReadOnlySet<string> EditableStates,
    IReadOnlySet<string> Aliases)
{
    public int NormalizeLease(int? requestedMinutes)
    {
        var lease = requestedMinutes ?? DefaultLeaseMinutes;
        if (lease < MinimumLeaseMinutes || lease > MaximumLeaseMinutes)
            throw new DomainException($"{CanonicalType} edit-session leases must be between {MinimumLeaseMinutes} and {MaximumLeaseMinutes} minutes.");
        return lease;
    }

    public bool IsEditableState(string? state) =>
        !string.IsNullOrWhiteSpace(state) && EditableStates.Contains(state.Trim());
}

public static class ControlledArtifactEditPolicies
{
    private static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

    private static IReadOnlySet<string> Set(params string[] values) =>
        new HashSet<string>(values, Comparer);

    private static readonly ControlledArtifactEditPolicy[] Policies =
    [
        new(ControlledArtifactFamily.ChangeRequest, "ChangeRequest", true, 15, 2, 120,
            Set("Draft"), Set("ChangeRequest", "SCR", "SWCR")),
        new(ControlledArtifactFamily.RequirementProposal, "RequirementProposal", true, 15, 2, 120,
            Set("Draft", "Proposed"), Set("RequirementProposal", "RequirementChange", "RequirementDraft")),
        new(ControlledArtifactFamily.SpecificationStructure, "SpecificationStructure", true, 15, 2, 120,
            Set("Draft", "InWork"), Set("SpecificationStructure", "Specification", "SpecificationNode")),
        new(ControlledArtifactFamily.TestProcedure, "TestProcedure", true, 15, 2, 120,
            Set("Draft", "ChangesRequested"), Set("TestProcedure", "Procedure", "TestProcedureRevision")),
        new(ControlledArtifactFamily.TraceLinkProposal, "TraceLinkProposal", true, 15, 2, 120,
            Set("Draft", "Proposed", "Suspect"), Set("TraceLinkProposal", "TraceLink", "RequirementTrace")),
        new(ControlledArtifactFamily.ReleasePlanning, "ReleasePlanning", true, 15, 2, 120,
            Set("Draft", "InWork", "Candidate"), Set("ReleasePlanning", "CandidateBaseline", "ReleasePlan")),
        new(ControlledArtifactFamily.DocumentTemplate, "DocumentTemplate", true, 15, 2, 120,
            Set("Draft", "InWork"), Set("DocumentTemplate", "ControlledTemplate")),
        new(ControlledArtifactFamily.ProblemReport, "ProblemReport", true, 15, 2, 120,
            Set("Draft", "Open", "Investigating", "ResolutionProposed"), Set("ProblemReport", "PR")),
        new(ControlledArtifactFamily.ConfigurationChangeSet, "ConfigurationChangeSet", true, 15, 2, 120,
            Set("Draft", "InWork", "Conflict"), Set("ConfigurationChangeSet", "ChangeSet"))
    ];

    private static readonly IReadOnlyDictionary<string, ControlledArtifactEditPolicy> ByAlias =
        Policies
            .SelectMany(policy => policy.Aliases.Append(policy.CanonicalType)
                .Select(alias => new KeyValuePair<string, ControlledArtifactEditPolicy>(alias, policy)))
            .GroupBy(pair => pair.Key, Comparer)
            .ToDictionary(group => group.Key, group => group.First().Value, Comparer);

    public static IReadOnlyCollection<ControlledArtifactEditPolicy> All => Policies;

    public static bool TryResolve(string? artifactType, out ControlledArtifactEditPolicy policy)
    {
        if (!string.IsNullOrWhiteSpace(artifactType) && ByAlias.TryGetValue(artifactType.Trim(), out var resolved))
        {
            policy = resolved;
            return true;
        }

        policy = null!;
        return false;
    }

    public static ControlledArtifactEditPolicy Resolve(string artifactType) =>
        TryResolve(artifactType, out var policy)
            ? policy
            : throw new DomainException($"'{artifactType}' is not a supported controlled draft artifact type.");

    public static string Canonicalize(string artifactType) => Resolve(artifactType).CanonicalType;
}
