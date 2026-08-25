using AeroLink.Domain.Common;

namespace AeroLink.Domain.Assurance;

/// <summary>
/// The assurance level a project declares for itself.
///
/// This is recorded metadata and nothing more. It does not select recommendations, because no
/// certification-derived mapping from a declared level to an enforceable lever has been approved for this
/// installation — and a level that silently changed what AeroLink recommends would assert a derivation that
/// does not exist. When such a mapping is approved it arrives as its own slice, with its own reviewer.
/// </summary>
public enum AssuranceLevel { NotDeclared, LevelA, LevelB, LevelC, LevelD, LevelE }

/// <summary>
/// Where a recommendation comes from, stated so a reader never has to guess.
///
/// <see cref="AeroLinkRule"/> is a rule this product enforces because of how it is built. Same-level
/// verification coverage is the clearest example: it is an AeroLink rule and is deliberately not attributed
/// to published guidance. <see cref="PublishedGuidance"/> exists so that a later, separately approved
/// certification mapping has somewhere truthful to land; nothing ships under it in this slice.
/// </summary>
public enum AssuranceBasisKind { AeroLinkRule, PublishedGuidance }

/// <summary>
/// What kind of relaxation a deviation is, which is what decides who may approve it.
///
/// Kept as data keyed by class rather than as role checks at each lever, so a later authority class, role
/// combination or approval count is a change to <see cref="AssuranceAuthorityPolicy"/> and not a change to
/// every enforcement point. <see cref="Independence"/> and <see cref="ReleaseGate"/> are named by the
/// product-owner decision of 2026-08-22 and carry their authority rule here; no lever shipped in this slice
/// resolves a relaxation to them.
/// </summary>
public enum AssuranceDeviationClass { ProjectPolicy, Verification, Independence, Evidence, ReleaseGate, Airworthiness }

/// <summary>The enumerated policy levers this installation supports. A lever with no enforcement point does not exist here.</summary>
public enum AssurancePolicyLever
{
    RequirementCoverageBeforeRelease,
    TestEvidenceBeforeRelease,
    ChangeImpactDispositionBeforeRelease,
    ProblemReportWaiverAcceptance,
}

/// <summary>
/// The values a lever can be set to. One enum across all levers, with each lever declaring which of them it
/// accepts, so a stored selection is always a controlled value rather than free text.
/// </summary>
public enum AssuranceLeverValue { Required, NotRequired, WaiversAccepted, WaiversRefused }

/// <summary>
/// One selectable value for one lever.
///
/// Strictness is what decides whether a selection needs a governed deviation. Choosing a value at least as
/// strict as the recommendation is the project exercising its own judgement upward and needs no approval;
/// choosing a looser one relaxes what AeroLink recommends, and does.
/// </summary>
public sealed record AssuranceLeverOption(AssuranceLeverValue Value, string Name, int Strictness, string Effect);

/// <summary>
/// One lever, its recommendation, the honest basis for that recommendation, and where it is enforced.
///
/// EnforcementPoint names the exact runtime seam that reads the lever, so the claim that a setting does
/// something is checkable rather than asserted.
/// </summary>
public sealed record AssuranceLeverDefinition(
    AssurancePolicyLever Lever,
    string Name,
    string Description,
    string EnforcementPoint,
    AssuranceLeverValue RecommendedValue,
    string RecommendationBasis,
    AssuranceBasisKind BasisKind,
    AssuranceDeviationClass DeviationClass,
    string ReleaseEffect,
    IReadOnlyList<AssuranceLeverOption> Options)
{
    public AssuranceLeverOption Option(AssuranceLeverValue value) =>
        Options.SingleOrDefault(x => x.Value == value)
        ?? throw new DomainException($"{value} is not a supported setting for the {Name} policy lever.");

    public bool Accepts(AssuranceLeverValue value) => Options.Any(x => x.Value == value);

    /// <summary>True when the selection is looser than what AeroLink recommends, which is what a deviation records.</summary>
    public bool IsRelaxation(AssuranceLeverValue selected) =>
        Option(selected).Strictness < Option(RecommendedValue).Strictness;
}

/// <summary>
/// The shipped levers.
///
/// Deliberately short. A lever exists here only when a real AeroLink enforcement point reads it, because a
/// switch nothing consumes is a screen asserting something untrue about a controlled project — the one screen
/// this product must never have. Every recommendation below is an AeroLink project-policy default; none is
/// attributed to published guidance, and no certification-derived mapping is approved for this installation.
/// </summary>
public static class AssurancePolicyCatalogue
{
    private const string RelaxedGateEffect =
        "The release-readiness gate reports the relaxation rather than the obligation, naming the approved deviation. " +
        "Release becomes possible without the evidence the recommendation would have required.";

    private static readonly AssuranceLeverDefinition[] Levers =
    [
        new(AssurancePolicyLever.RequirementCoverageBeforeRelease,
            "Requirement coverage before release",
            "Whether every effective requirement revision the baseline carries must hold settled verification coverage before the release is ready.",
            "ReleaseReadinessService — release-readiness gate 'coverage'",
            AssuranceLeverValue.Required,
            "AeroLink counts a requirement as covered only when the link is not suspect, names an approved verification artifact revision, "
            + "and that artifact has no revision still in draft or review. Coverage is also resolved at the same ladder level as the requirement. "
            + "That same-level rule follows from how AeroLink binds verification artifacts to a ladder step: it is an AeroLink rule, and is "
            + "deliberately not attributed to published certification guidance.",
            AssuranceBasisKind.AeroLinkRule,
            AssuranceDeviationClass.Verification,
            RelaxedGateEffect,
            [
                new(AssuranceLeverValue.Required, "Required", 1, "Uncovered requirement revisions block release readiness."),
                new(AssuranceLeverValue.NotRequired, "Not required", 0, "Uncovered requirement revisions do not block release readiness."),
            ]),
        new(AssurancePolicyLever.TestEvidenceBeforeRelease,
            "Checksummed evidence for selected test results",
            "Whether every result in the build's selected test set must carry a checksummed evidence package before the release is ready.",
            "ReleaseReadinessService — release-readiness gate 'evidence'",
            AssuranceLeverValue.Required,
            "A determination with no attached, checksummed evidence package is a claim the record cannot support later. AeroLink recommends "
            + "that the evidence exist before release, because the release record is what a reviewer reads afterwards. This is an AeroLink "
            + "project-policy default.",
            AssuranceBasisKind.AeroLinkRule,
            AssuranceDeviationClass.Evidence,
            RelaxedGateEffect,
            [
                new(AssuranceLeverValue.Required, "Required", 1, "Results without checksummed evidence block release readiness."),
                new(AssuranceLeverValue.NotRequired, "Not required", 0, "Results without checksummed evidence do not block release readiness."),
            ]),
        new(AssurancePolicyLever.ChangeImpactDispositionBeforeRelease,
            "Change impact dispositioned before release",
            "Whether every impact finding raised against the release's change requests must be dispositioned before the release is ready.",
            "ReleaseReadinessService — release-readiness gate 'impact_disposition'",
            AssuranceLeverValue.Required,
            "An impact finding left pending is analysis nobody concluded. AeroLink recommends that each one carry an Addressed or "
            + "Not Applicable decision with its rationale before release. This is an AeroLink project-policy default.",
            AssuranceBasisKind.AeroLinkRule,
            AssuranceDeviationClass.ProjectPolicy,
            RelaxedGateEffect,
            [
                new(AssuranceLeverValue.Required, "Required", 1, "Pending impact findings block release readiness."),
                new(AssuranceLeverValue.NotRequired, "Not required", 0, "Pending impact findings do not block release readiness."),
            ]),
        new(AssurancePolicyLever.ProblemReportWaiverAcceptance,
            "Problem-report release-blocker waivers",
            "Whether an attributable, time-boxed readiness waiver may suppress a release-blocking problem report.",
            "ReleaseReadinessService — release-readiness gate 'problem_reports'",
            AssuranceLeverValue.WaiversAccepted,
            "AeroLink recommends the governed waiver. A waiver names its approver and signature meaning, expires on its own, and is void if "
            + "the report changes underneath it, so the release record says openly that a blocker was accepted rather than hiding that it was. "
            + "A project may still refuse waivers outright: that is stricter than the recommendation and needs no deviation. This is an "
            + "AeroLink project-policy default.",
            AssuranceBasisKind.AeroLinkRule,
            AssuranceDeviationClass.ReleaseGate,
            "Refusing waivers means an active waiver no longer suppresses its blocker, so every release-blocking problem report must be "
            + "resolved or dispositioned before release.",
            [
                new(AssuranceLeverValue.WaiversRefused, "Waivers refused", 1, "An active readiness waiver does not suppress a release-blocking problem report."),
                new(AssuranceLeverValue.WaiversAccepted, "Waivers accepted", 0, "An active readiness waiver suppresses its release-blocking problem report."),
            ]),
    ];

    public static IReadOnlyList<AssuranceLeverDefinition> All => Levers;

    public static AssuranceLeverDefinition Definition(AssurancePolicyLever lever) =>
        Levers.SingleOrDefault(x => x.Lever == lever)
        ?? throw new DomainException($"{lever} is not a supported assurance policy lever.");

    /// <summary>
    /// The recommended selection for every lever, which is also the effective policy of a project that has
    /// never recorded one. Every recommendation matches what AeroLink already enforced before this feature
    /// existed, so declaring a policy changes nothing until the project actually chooses to change something.
    /// </summary>
    public static IReadOnlyDictionary<AssurancePolicyLever, AssuranceLeverValue> Recommended =>
        Levers.ToDictionary(x => x.Lever, x => x.RecommendedValue);
}
