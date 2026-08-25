using System.Security.Cryptography;
using System.Text;
using AeroLink.Domain.Common;
using AeroLink.Domain.Identity;

namespace AeroLink.Domain.Assurance;

/// <summary>
/// The record of a project deliberately selecting a lever setting looser than AeroLink recommends.
///
/// It carries what was recommended and why, what the project chose instead, who proposed it, who approved it
/// and on what authority, and what the release consequence is. It is written once. When the lever's setting
/// changes again the record is superseded rather than edited, because a deviation is evidence of a decision
/// taken at a moment and rewriting it would replace that evidence with a claim about the present.
///
/// A deviation carries forward across a policy version whose selection for its lever is unchanged. Re-recording
/// an unchanged relaxation on every unrelated policy edit would produce a wall of identical approvals and
/// would make the interesting question — when did this actually change — harder to answer, not easier.
/// </summary>
public sealed class AssurancePolicyDeviation
{
    private AssurancePolicyDeviation() { }

    private AssurancePolicyDeviation(Guid projectId, Guid policyVersionId, int policyVersion,
        AssurancePolicyLever lever, string scope, AssuranceLeverValue recommendedValue, string recommendationBasis,
        AssuranceBasisKind basisKind, AssuranceLeverValue selectedValue, string rationale,
        AssuranceDeviationClass deviationClass, bool airworthinessDesignated, string releaseEffect,
        Guid proposedByAccountId, string proposedBy, Guid approvedByAccountId, string approvedBy,
        ProgramRole approvalAuthority, AssuranceAuthoritySource approvalAuthoritySource, int authorityPolicyVersion,
        DateTimeOffset now)
    {
        if (projectId == Guid.Empty || policyVersionId == Guid.Empty)
            throw new DomainException("A deviation requires its project and the policy version that records it.");
        if (proposedByAccountId == Guid.Empty || approvedByAccountId == Guid.Empty)
            throw new DomainException("A deviation requires an identified proposer and approver.");
        if (proposedByAccountId == approvedByAccountId)
            throw new DomainException("Self-approval is prohibited: the proposer and the approver of a deviation must be different people.");
        Id = Guid.NewGuid();
        ProjectId = projectId;
        PolicyVersionId = policyVersionId;
        PolicyVersion = policyVersion;
        Lever = lever;
        Scope = Required(scope, "A deviation must record the scope it applies to.");
        RecommendedValue = recommendedValue;
        RecommendationBasis = Required(recommendationBasis, "A deviation must record the recommendation basis it departs from.");
        BasisKind = basisKind;
        SelectedValue = selectedValue;
        Rationale = Required(rationale, "A deviation requires a rationale saying why the project is departing from the recommendation.");
        DeviationClass = deviationClass;
        AirworthinessDesignated = airworthinessDesignated;
        ReleaseEffect = Required(releaseEffect, "A deviation must record its release effect.");
        ProposedByAccountId = proposedByAccountId;
        ProposedBy = Required(proposedBy, "A deviation requires an attributable proposer.");
        ApprovedByAccountId = approvedByAccountId;
        ApprovedBy = Required(approvedBy, "A deviation requires an attributable approver.");
        ApprovalAuthority = approvalAuthority;
        ApprovalAuthoritySource = approvalAuthoritySource;
        AuthorityPolicyVersion = authorityPolicyVersion;
        ProposedAt = now;
        EffectiveFrom = now;
        RecordHash = ComputeHash();
    }

    /// <summary>
    /// Records an approved deviation.
    ///
    /// The decision is passed in rather than taken here, because whether somebody may approve depends on
    /// membership and delegations that live in another aggregate. What this constructor enforces is that a
    /// record cannot exist unless that decision permitted it — there is no path to an unapproved deviation.
    /// </summary>
    public static AssurancePolicyDeviation Approve(Guid projectId, Guid policyVersionId, int policyVersion,
        AssuranceLeverDefinition definition, string scope, AssuranceLeverValue selectedValue, string rationale,
        AssuranceDeviationClass deviationClass, bool airworthinessDesignated,
        Guid proposedByAccountId, string proposedBy, Guid approvedByAccountId, string approvedBy,
        AssuranceAuthorityDecision decision, DateTimeOffset now)
    {
        if (!definition.Accepts(selectedValue))
            throw new DomainException($"{selectedValue} is not a supported setting for the {definition.Name} policy lever.");
        if (!definition.IsRelaxation(selectedValue))
            throw new DomainException($"The {definition.Name} selection is at least as strict as the AeroLink recommendation, so it is not a deviation.");
        if (!decision.Permitted || decision.SatisfiedBy is null)
            throw new DomainException(decision.Reason);
        return new(projectId, policyVersionId, policyVersion, definition.Lever, scope, definition.RecommendedValue,
            definition.RecommendationBasis, definition.BasisKind, selectedValue, rationale, deviationClass,
            airworthinessDesignated, definition.Option(selectedValue).Effect + " " + definition.ReleaseEffect,
            proposedByAccountId, proposedBy, approvedByAccountId, approvedBy, decision.SatisfiedBy.Value,
            decision.Source, decision.PolicyVersion, now);
    }

    /// <summary>
    /// Which authority class a proposed deviation falls into.
    ///
    /// An explicit airworthiness designation outranks the lever's own class, because the decision of
    /// 2026-08-22 makes airworthiness relevance a property of the deviation rather than of the lever.
    /// </summary>
    public static AssuranceDeviationClass ClassOf(AssuranceLeverDefinition definition, bool airworthinessDesignated) =>
        airworthinessDesignated ? AssuranceDeviationClass.Airworthiness : definition.DeviationClass;

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public Guid PolicyVersionId { get; private set; }
    public int PolicyVersion { get; private set; }
    public AssurancePolicyLever Lever { get; private set; }
    public string Scope { get; private set; } = string.Empty;
    public AssuranceLeverValue RecommendedValue { get; private set; }
    public string RecommendationBasis { get; private set; } = string.Empty;
    public AssuranceBasisKind BasisKind { get; private set; }
    public AssuranceLeverValue SelectedValue { get; private set; }
    public string Rationale { get; private set; } = string.Empty;
    public AssuranceDeviationClass DeviationClass { get; private set; }
    public bool AirworthinessDesignated { get; private set; }
    public string ReleaseEffect { get; private set; } = string.Empty;
    public Guid ProposedByAccountId { get; private set; }
    public string ProposedBy { get; private set; } = string.Empty;
    public DateTimeOffset ProposedAt { get; private set; }
    public Guid ApprovedByAccountId { get; private set; }
    public string ApprovedBy { get; private set; } = string.Empty;
    public ProgramRole ApprovalAuthority { get; private set; }
    public AssuranceAuthoritySource ApprovalAuthoritySource { get; private set; }
    public int AuthorityPolicyVersion { get; private set; }
    public DateTimeOffset EffectiveFrom { get; private set; }
    public DateTimeOffset? SupersededAt { get; private set; }
    public string SupersededBy { get; private set; } = string.Empty;
    public string SupersededReason { get; private set; } = string.Empty;
    /// <summary>A hash over the recorded decision, so a later read can show the record is the one that was approved.</summary>
    public string RecordHash { get; private set; } = string.Empty;

    public bool IsEffective => SupersededAt is null;

    public void Supersede(string actor, string reason, DateTimeOffset now)
    {
        if (SupersededAt is not null) throw new DomainException("This deviation is already superseded.");
        if (string.IsNullOrWhiteSpace(actor)) throw new DomainException("Superseding a deviation requires an attributable actor.");
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("Superseding a deviation requires a reason.");
        if (now < EffectiveFrom) throw new DomainException("A deviation cannot be superseded before it took effect.");
        SupersededAt = now;
        SupersededBy = actor.Trim();
        SupersededReason = reason.Trim();
    }

    /// <summary>Recomputes the hash over the approved decision, so a reader can confirm the record was not altered.</summary>
    public bool VerifyRecord() => string.Equals(RecordHash, ComputeHash(), StringComparison.Ordinal);

    private string ComputeHash()
    {
        // Milliseconds, not ticks. PostgreSQL stores a timestamptz to microsecond precision, so a hash taken
        // over the full .NET tick count verifies in memory and then fails the moment the row is read back —
        // which would make VerifyRecord report tampering on a record nothing had touched.
        var canonical = string.Join("|", ProjectId, PolicyVersionId, PolicyVersion, Lever, Scope, RecommendedValue,
            BasisKind, SelectedValue, DeviationClass, AirworthinessDesignated, ProposedByAccountId,
            ApprovedByAccountId, ApprovalAuthority, ApprovalAuthoritySource, AuthorityPolicyVersion,
            EffectiveFrom.ToUnixTimeMilliseconds(), Rationale, RecommendationBasis, ReleaseEffect);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static string Required(string? value, string message) =>
        string.IsNullOrWhiteSpace(value) ? throw new DomainException(message) : value.Trim();
}
