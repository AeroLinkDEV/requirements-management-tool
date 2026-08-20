using AeroLink.Domain.Common;

namespace AeroLink.Domain.Requirements;

public enum ControlledArtifactFamily
{
    ChangeRequest,
    RequirementProposal,
    SpecificationStructure,
    // Retained as historical evidence only. DEC-103 governs procedure change through a Test Change Request;
    // test procedures are deliberately absent from ControlledArtifactEditPolicies, so this value resolves to
    // no operational editing path. Records written while the family was editable keep their type strings.
    TestProcedure,
    TraceLinkProposal,
    ReleasePlanning,
    DocumentTemplate,
    ProblemReport,
    ConfigurationChangeSet,
    /// <summary>
    /// The package that governs procedure change. <see cref="TestProcedure"/> above is deliberately not
    /// editable — DEC-103 governs a procedure through a test change request — and this is the record that
    /// does the governing, so it is the one that can be checked out.
    /// </summary>
    TestChangeRequest
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
        new(ControlledArtifactFamily.TraceLinkProposal, "TraceLinkProposal", true, 15, 2, 120,
            Set("Draft", "Proposed", "Suspect"), Set("TraceLinkProposal", "TraceLink", "RequirementTrace")),
        new(ControlledArtifactFamily.ReleasePlanning, "ReleasePlanning", true, 15, 2, 120,
            Set("Draft", "InWork", "Candidate"), Set("ReleasePlanning", "CandidateBaseline", "ReleasePlan")),
        new(ControlledArtifactFamily.DocumentTemplate, "DocumentTemplate", true, 15, 2, 120,
            Set("Draft", "InWork"), Set("DocumentTemplate", "ControlledTemplate")),
        // A Problem Report is editable in every state except the ones that end it. It is the record most
        // likely to need correcting while the work it describes is in flight. Closed and Rejected are
        // read-only; the lifecycle transition endpoints are the route back.
        new(ControlledArtifactFamily.ProblemReport, "ProblemReport", true, 15, 2, 120,
            Set("Draft", "ReadyForSccb", "Open", "Implementing", "Verifying", "WaitingForSqaToClose"),
            Set("ProblemReport", "PR")),
        new(ControlledArtifactFamily.ConfigurationChangeSet, "ConfigurationChangeSet", true, 15, 2, 120,
            Set("Draft", "InWork", "Conflict"), Set("ConfigurationChangeSet", "ChangeSet")),
        // Editable only as a Draft, exactly like the change request it mirrors: once a package is in review
        // the approvers are looking at a frozen case, and once approved it is controlled evidence. The
        // aliases cover the three controlled prefixes a package can carry.
        new(ControlledArtifactFamily.TestChangeRequest, "TestChangeRequest", true, 15, 2, 120,
            Set("Draft"), Set("TestChangeRequest", "TCR", "SYSTCR", "HLRTCR", "LLRTCR"))
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
