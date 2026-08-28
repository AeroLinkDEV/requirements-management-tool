using AeroLink.Domain.Common;
using AeroLink.Domain.Identity;

namespace AeroLink.Domain.Assurance;

/// <summary>
/// Who may approve a deviation of one class, expressed as data rather than as a role check written at the
/// place the deviation is recorded.
///
/// The product-owner decision of 2026-08-22 asked for exactly this shape: allowed authorities, a minimum
/// approval count, whether delegation is accepted and whether self-approval is. A later class, a role
/// combination, or a second required approver is then a change to the table below and to nothing else.
/// </summary>
public sealed record AssuranceAuthorityRule(
    AssuranceDeviationClass Class,
    IReadOnlyList<ProgramRole> ApprovingRoles,
    int MinimumApprovals,
    bool DelegationAllowed,
    bool SelfApprovalAllowed);

/// <summary>
/// Versioned authority policy data.
///
/// The version is stored on every deviation, so a record always says which authority rules it was approved
/// under. Changing the rules later cannot re-interpret an approval that has already been given.
/// </summary>
public static class AssuranceAuthorityPolicy
{
    /// <summary>The current rules, with accountable positions separated from base eligibility.</summary>
    public const int CurrentVersion = 2;

    // One qualified approver is sufficient throughout version 1. The count is data rather than an assumption
    // baked into the resolver, because the decision says a future class may require more.
    private static readonly AssuranceAuthorityRule[] Version1 =
    [
        new(AssuranceDeviationClass.ProjectPolicy,
            [ProgramRole.ProgramManager, ProgramRole.SoftwareQualityAnalyst], 1, true, false),
        // Verification, independence, evidence and release-gate relaxations are the four the decision
        // reserves to SQA specifically. Program Manager is deliberately absent from all four.
        new(AssuranceDeviationClass.Verification, [ProgramRole.SoftwareQualityAnalyst], 1, true, false),
        new(AssuranceDeviationClass.Independence, [ProgramRole.SoftwareQualityAnalyst], 1, true, false),
        new(AssuranceDeviationClass.Evidence, [ProgramRole.SoftwareQualityAnalyst], 1, true, false),
        new(AssuranceDeviationClass.ReleaseGate, [ProgramRole.SoftwareQualityAnalyst], 1, true, false),
        new(AssuranceDeviationClass.Airworthiness, [ProgramRole.Airworthiness], 1, true, false),
    ];

    // Version 2 preserves the approved role catalogue and changes how position-governed entries are
    // established: ProgramManager means the Project Leadership position, not the base role that qualifies a
    // person to hold it. Keeping a separate version is what lets a historical v1 approval remain truthful.
    private static readonly AssuranceAuthorityRule[] Version2 = [.. Version1];

    public static IReadOnlyList<AssuranceAuthorityRule> Version(int version) => version switch
    {
        1 => Version1,
        2 => Version2,
        _ => throw new DomainException($"Assurance authority policy version {version} is not supported."),
    };

    public static AssuranceAuthorityRule Rule(AssuranceDeviationClass deviationClass, int version = CurrentVersion) =>
        Version(version).SingleOrDefault(x => x.Class == deviationClass)
        ?? throw new DomainException($"Assurance authority policy version {version} has no rule for {deviationClass}.");
}

/// <summary>One live delegation of a role, with the scope and interval the decision requires it to carry.</summary>
public sealed record AssuranceDelegationFact(
    ProgramRole Role, Guid ProgramId, DateTimeOffset StartsAt, DateTimeOffset EndsAt, bool Revoked)
{
    public bool IsInForce(Guid programId, ProgramRole role, DateTimeOffset now) =>
        !Revoked && ProgramId == programId && Role == role && StartsAt <= now && EndsAt > now;
}

/// <summary>
/// What is known about a candidate approver, resolved outside the domain because membership lives in a
/// different aggregate.
///
/// <paramref name="HeldRoles"/> must be the roles the person actually holds on the Program. It must not
/// include an Administrator implication: technical Administrator access grants no assurance authority, and
/// AeroLink's ordinary project role check deliberately answers true for an administrator, which is exactly
/// the wrong answer here. <paramref name="IsAdministrator"/> is carried only so the refusal can
/// say why an administrator was refused.
/// </summary>
/// <param name="LeadershipAuthorities">
/// The role demands this person answers by holding a Project Leadership position — primary or standing
/// backup — each already validated against that position's own eligibility.
///
/// Kept apart from <paramref name="HeldRoles"/> because #816 made them different facts. Program Manager
/// authority over a project-policy deviation belongs to whoever holds the position; the membership that
/// makes somebody eligible for the position is not the same statement and must not approve on its own.
/// </param>
public sealed record AssuranceApproverFacts(
    Guid AccountId,
    string UserName,
    IReadOnlyCollection<ProgramRole> HeldRoles,
    IReadOnlyCollection<AssuranceDelegationFact> Delegations,
    bool IsAdministrator,
    IReadOnlyCollection<ProgramRole> LeadershipAuthorities);

/// <summary>How the approver's authority was established, recorded on the deviation.</summary>
public enum AssuranceAuthoritySource { None, Membership, Delegation, ProjectLeadership }

public sealed record AssuranceAuthorityDecision(
    bool Permitted, string Reason, ProgramRole? SatisfiedBy, AssuranceAuthoritySource Source, int PolicyVersion);

/// <summary>
/// The one place that decides whether a person may approve an assurance-policy deviation.
///
/// Every enforcement point asks this. Scattering the role checks is how a lever ends up quietly accepting an
/// approver another lever refuses, and how the Administrator carve-out gets forgotten at one of them.
/// </summary>
public static class AssuranceDeviationAuthority
{
    public static AssuranceAuthorityDecision Decide(
        AssuranceDeviationClass deviationClass,
        Guid programId,
        Guid proposerAccountId,
        AssuranceApproverFacts approver,
        DateTimeOffset now,
        int policyVersion = AssuranceAuthorityPolicy.CurrentVersion)
    {
        if (programId == Guid.Empty) throw new DomainException("A deviation approval requires the Program it is scoped to.");
        if (approver.AccountId == Guid.Empty) throw new DomainException("A deviation approval requires an identified approver.");
        var rule = AssuranceAuthorityPolicy.Rule(deviationClass, policyVersion);
        var required = Readable(rule);

        if (!rule.SelfApprovalAllowed && approver.AccountId == proposerAccountId)
            return Refused($"{approver.UserName} proposed this deviation. Self-approval is prohibited: the proposer and the approver must be different people.", policyVersion);

        // Membership first, and through Satisfying so a more precise job title never removes the authority
        // its general form carried. None of the assurance roles has an implication today; taking it through
        // the shared rule means that stays true if one gains it.
        //
        // Position-governed roles are excluded here. Program Manager is an accountable position since #816,
        // so a project-policy deviation takes the holder of that position, not everybody granted the role
        // that makes them eligible for it. SQA and Airworthiness are unaffected: they remain base assurance
        // roles and membership is exactly the right question for them.
        // Version 1 predates the position/base-role split and must keep its original membership meaning when
        // an immutable historical deviation is verified. Version 2 is the first policy whose ProgramManager
        // entry means the accountable position.
        foreach (var role in rule.ApprovingRoles.Where(x => policyVersion == 1 || !IsPositionGoverned(x)))
            if (approver.HeldRoles.Any(held => ProgramRoleAuthority.Satisfying(role).Contains(held)))
                return new(true, $"{approver.UserName} holds {Readable(role)} authority on this Program.",
                    role, AssuranceAuthoritySource.Membership, policyVersion);

        foreach (var role in policyVersion >= 2 ? rule.ApprovingRoles.Where(IsPositionGoverned) : [])
            if (approver.LeadershipAuthorities.Contains(role))
                return new(true,
                    $"{approver.UserName} holds the {Readable(role)} Project Leadership position on this Program.",
                    role, AssuranceAuthoritySource.ProjectLeadership, policyVersion);

        if (rule.DelegationAllowed)
            foreach (var role in rule.ApprovingRoles)
                if (approver.Delegations.Any(x => x.IsInForce(programId, role, now)))
                    return new(true,
                        $"{approver.UserName} is acting under a recorded {Readable(role)} delegation that is in force for this Program.",
                        role, AssuranceAuthoritySource.Delegation, policyVersion);

        // Named separately so the refusal explains the rule rather than merely denying. An administrator who
        // is also the SQA representative is approved by the membership branch above; one who is not holds no
        // assurance authority at all, and being told "you lack the role" while the rest of the product treats
        // administrator access as universal is exactly the confusion worth spending a sentence on.
        if (approver.IsAdministrator)
            return Refused(
                $"{approver.UserName} holds Administrator access, which carries no assurance authority. "
                + $"A {required} deviation requires {required} authority held or delegated on this Program.",
                policyVersion);

        return Refused($"{approver.UserName} does not hold {required} authority on this Program, so they cannot approve a {Readable(deviationClass)} deviation.", policyVersion);
    }

    /// <summary>
    /// Whether an approving role names a Project Leadership position rather than a job somebody performs.
    /// Only <c>ProgramManager</c> qualifies among today's assurance rules; the predicate is shared with the
    /// rest of the model so a future rule naming a position gets the same treatment automatically.
    /// </summary>
    private static bool IsPositionGoverned(ProgramRole role) =>
        SingularProgramRoles.IsSingular(role) || SingularProgramRoles.IsBaseEligibility(role);

    private static AssuranceAuthorityDecision Refused(string reason, int policyVersion) =>
        new(false, reason, null, AssuranceAuthoritySource.None, policyVersion);

    private static string Readable(AssuranceAuthorityRule rule) =>
        string.Join(" or ", rule.ApprovingRoles.Select(Readable));

    public static string Readable(ProgramRole role) => role switch
    {
        ProgramRole.ProgramManager => "Program Manager",
        ProgramRole.SoftwareQualityAnalyst => "Software Quality Analyst",
        ProgramRole.ConfigurationManager => "Configuration Manager",
        _ => role.ToString(),
    };

    public static string Readable(AssuranceDeviationClass deviationClass) => deviationClass switch
    {
        AssuranceDeviationClass.ProjectPolicy => "project-policy",
        AssuranceDeviationClass.ReleaseGate => "release-gate",
        _ => deviationClass.ToString().ToLowerInvariant(),
    };
}
